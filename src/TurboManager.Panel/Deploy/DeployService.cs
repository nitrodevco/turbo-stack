using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Cloudflare;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Deploy;

public sealed record DeployOutcome(bool Success, string? Release, string? Error);

/// <summary>Repositories, apps and the deploy pipeline (sync → release → build → activate → health check).</summary>
public sealed class DeployService(
    PanelDbContext db,
    AgentChannelFactory agent,
    JobRunner jobs,
    GitHubHostKeys hostKeys,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClients,
    IOptions<PanelOptions> options,
    TimeProvider time,
    ILogger<DeployService> logger)
{

    public const string RemovedFromDisk = "Removed from disk";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Deploy.v1");

    public static string JobKind(int appId) => $"deploy-app-{appId}";

    public string Unprotect(string value) => _protector.Unprotect(value);

    public string Protect(string value) => _protector.Protect(value);

    public Dictionary<string, string> GetEnvironment(App app) =>
        string.IsNullOrEmpty(app.EnvironmentProtected)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(app.EnvironmentProtected)) ?? [];

    public void SetEnvironment(App app, IDictionary<string, string> environment) =>
        app.EnvironmentProtected = _protector.Protect(JsonSerializer.Serialize(environment));

    public static string NewWebhookSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>Creates the app user and (for private repositories) the deploy keys, as the agent's user.</summary>
    public async Task PrepareRepositoryAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        await agent.Deploy.EnsureAppUserAsync(new AppUserRequest { User = repository.SystemUser }, cancellationToken: cancellationToken);

        if (repository.Auth == RepositoryAuth.DeployKey)
        {
            var key = await agent.Deploy.EnsureDeployKeyAsync(new DeployKeyRequest { User = repository.SystemUser, KeyName = repository.Name },
                cancellationToken: cancellationToken);
            repository.PublicKey = key.PublicKey;
            repository.KeyFingerprint = key.Fingerprint;
        }

        foreach (var submodule in repository.SubmoduleKeys)
        {
            var key = await agent.Deploy.EnsureDeployKeyAsync(new DeployKeyRequest { User = repository.SystemUser, KeyName = submodule.KeyName },
                cancellationToken: cancellationToken);
            submodule.PublicKey = key.PublicKey;
            submodule.Fingerprint = key.Fingerprint;
        }
    }

    public async Task<GitSyncRequest> SyncRequestAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        var request = new GitSyncRequest
        {
            User = repository.SystemUser,
            RepoDir = repository.Name,
            Branch = repository.Branch,
            Submodules = repository.Submodules,
            Repository = new RepositoryAccess
            {
                Owner = repository.Owner,
                Name = repository.RepositoryName,
                KeyName = repository.Auth == RepositoryAuth.DeployKey ? repository.Name : "",
            },
            SshHost = options.Value.GitHubSshHost,
            SshPort = options.Value.GitHubSshPort,
            HttpsBase = options.Value.GitHubHttpsBase,
        };

        request.SubmoduleAccess.AddRange(repository.SubmoduleKeys.Select(s => new RepositoryAccess { Owner = s.Owner, Name = s.Name, KeyName = s.KeyName }));
        request.KnownHosts.AddRange(await hostKeys.GetAsync(cancellationToken));

        return request;
    }

    public async Task<GitAccessResult> TestAccessAsync(GitRepository repository, CancellationToken cancellationToken) =>
        await agent.Deploy.TestRepositoryAccessAsync(await SyncRequestAsync(repository, cancellationToken), cancellationToken: cancellationToken);

    public async Task<Job> StartDeployAsync(App app, string trigger, PanelUser? user)
    {
        var appId = app.Id;

        return await jobs.StartAsync(JobKind(appId), $"Deploy {app.Name}", user, async context =>
        {
            var service = context.Services.GetRequiredService<DeployService>();
            var outcome = await service.DeployAsync(appId, trigger, user?.Username, context);

            return outcome.Success;
        });
    }

    public async Task<Job> StartRollbackAsync(App app, string release, PanelUser user)
    {
        var appId = app.Id;

        return await jobs.StartAsync(JobKind(appId), $"Roll back {app.Name} to {release}", user, async context =>
        {
            var service = context.Services.GetRequiredService<DeployService>();

            return await service.ActivateExistingAsync(appId, release, user.Username, context);
        });
    }

    /// <summary>The whole pipeline. Any failure before activation leaves the live release untouched.</summary>
    public async Task<DeployOutcome> DeployAsync(int appId, string trigger, string? triggeredBy, JobContext context)
    {
        var app = await db.Apps.Include(a => a.Repository).FirstAsync(a => a.Id == appId, context.CancellationToken);
        var repository = app.Repository!;
        var cancellationToken = context.CancellationToken;

        context.Log($"Fetching {repository.FullName} ({repository.Branch})…");
        DeployOutput? synced = null;

        try
        {
            using var sync = agent.Deploy.SyncRepository(await SyncRequestAsync(repository, cancellationToken), cancellationToken: cancellationToken);

            await foreach (var output in sync.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (output.Finished)
                {
                    synced = output;
                }
                else
                {
                    context.Log(output.Line);
                }
            }
        }
        catch (RpcException e)
        {
            repository.LastError = e.Status.Detail;
            await db.SaveChangesAsync(CancellationToken.None);
            context.Log($"Fetching failed: {e.Status.Detail}");

            return new DeployOutcome(false, null, e.Status.Detail);
        }

        repository.LastSyncedAt = time.GetUtcNow();
        repository.LastCommit = synced!.Commit;
        repository.LastCommitMessage = synced.CommitMessage;
        repository.LastError = null;

        var releaseName = $"{time.GetUtcNow():yyyyMMddHHmmss}-{synced.Commit[..7]}";
        var release = new AppRelease
        {
            AppId = app.Id,
            Name = releaseName,
            Commit = synced.Commit,
            CommitMessage = synced.CommitMessage,
            CommitAuthor = synced.CommitAuthor,
            Trigger = trigger,
            TriggeredBy = triggeredBy,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync(cancellationToken);
        context.Log($"Commit {synced.Commit[..7]} by {synced.CommitAuthor}: {synced.CommitMessage}");

        async Task<DeployOutcome> Fail(string error)
        {
            release.Status = ReleaseStatus.Failed;
            release.Error = error;
            release.FinishedAt = time.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            context.Log($"✕ {error}");

            return new DeployOutcome(false, releaseName, error);
        }

        try
        {
            var releaseRequest = new ReleaseRequest
            {
                User = app.SystemUser,
                App = app.Name,
                RepoDir = repository.Name,
                Release = releaseName,
                WebReadable = app.StaticOutput is not null,
            };
            releaseRequest.Shared.AddRange(app.SharedPaths.Select(p => new SharedPath { Path = p.Path, Directory = p.Directory }));

            if (!await StreamAsync(agent.Deploy.CreateRelease(releaseRequest, cancellationToken: cancellationToken), context))
            {
                return await Fail("Creating the release failed.");
            }

            context.Log("Building…");
            var build = new BuildRequest
            {
                User = app.SystemUser,
                App = app.Name,
                Release = releaseName,
                WorkingDirectory = app.WorkingDirectory,
                Script = app.BuildScript,
                TimeoutMinutes = 60,
            };
            build.Environment.Add(GetEnvironment(app));
            build.Runtimes.AddRange(app.Runtimes);

            if (!await StreamAsync(agent.Deploy.RunBuild(build, cancellationToken: cancellationToken), context))
            {
                return await Fail("The build failed. The live release was not changed.");
            }

            if (app.StaticOutput is not null && !releaseRequest.WebReadable)
            {
                context.Log("Static output is not readable by nginx.");
            }

            var previous = app.CurrentRelease;

            if (app.ServiceCommand is not null)
            {
                await WriteServiceAsync(app, cancellationToken);
            }

            await agent.Deploy.ActivateReleaseAsync(new ReleaseRequest { User = app.SystemUser, App = app.Name, Release = releaseName },
                cancellationToken: cancellationToken);
            context.Log($"Release {releaseName} is now live.");

            if (app.ServiceCommand is not null && !await RestartAndCheckAsync(app, context))
            {
                if (previous is not null)
                {
                    context.Log($"Rolling back to {previous}…");
                    await agent.Deploy.ActivateReleaseAsync(new ReleaseRequest { User = app.SystemUser, App = app.Name, Release = previous },
                        cancellationToken: CancellationToken.None);
                    await RestartAndCheckAsync(app, context);
                }

                return await Fail(previous is null
                    ? "The service didn't become healthy."
                    : $"The service didn't become healthy; rolled back to {previous}.");
            }

            await MarkActiveAsync(app, release);
            await PruneAsync(app, context);
            await PurgeCloudflareAsync(app, context);

            context.Log("✓ Deployed.");

            return new DeployOutcome(true, releaseName, null);
        }
        catch (RpcException e)
        {
            return await Fail(e.Status.Detail);
        }
    }

    /// <summary>Re-activates a release that is still on disk (rollback), restarting and checking the service.</summary>
    public async Task<bool> ActivateExistingAsync(int appId, string releaseName, string triggeredBy, JobContext context)
    {
        var app = await db.Apps.FirstAsync(a => a.Id == appId, context.CancellationToken);
        var release = await db.Releases.FirstOrDefaultAsync(r => r.AppId == appId && r.Name == releaseName, context.CancellationToken);

        if (release is null)
        {
            context.Log("Release not found.");

            return false;
        }

        try
        {
            if (app.ServiceCommand is not null)
            {
                await WriteServiceAsync(app, context.CancellationToken);
            }

            await agent.Deploy.ActivateReleaseAsync(new ReleaseRequest { User = app.SystemUser, App = app.Name, Release = releaseName },
                cancellationToken: context.CancellationToken);
            context.Log($"Release {releaseName} is now live.");

            if (app.ServiceCommand is not null && !await RestartAndCheckAsync(app, context))
            {
                context.Log("✕ The service didn't become healthy on this release.");

                return false;
            }

            await MarkActiveAsync(app, release);
            await PurgeCloudflareAsync(app, context);
            context.Log($"✓ Rolled back to {releaseName} ({triggeredBy}).");

            return true;
        }
        catch (RpcException e)
        {
            context.Log($"✕ {e.Status.Detail}");

            return false;
        }
    }

    public async Task WriteServiceAsync(App app, CancellationToken cancellationToken)
    {
        var request = new AppServiceRequest
        {
            User = app.SystemUser,
            App = app.Name,
            WorkingDirectory = app.WorkingDirectory,
            Command = app.ServiceCommand!,
            MemoryMaxMb = app.MemoryMaxMb,
            StopTimeoutSeconds = app.StopTimeoutSeconds,
        };
        request.Environment.Add(GetEnvironment(app));
        request.Runtimes.AddRange(app.Runtimes);

        await agent.Deploy.WriteAppServiceAsync(request, cancellationToken: cancellationToken);
    }

    private async Task<bool> RestartAndCheckAsync(App app, JobContext context)
    {
        context.Log($"Restarting tm-app-{app.Name}…");
        await agent.Services.ControlServiceAsync(new ControlServiceRequest { Name = $"tm-app-{app.Name}.service", Action = ServiceAction.Restart },
            cancellationToken: context.CancellationToken);

        if (app.ServicePort is not { } port)
        {
            return true;
        }

        context.Log(app.HealthPath is null ? $"Waiting for port {port}…" : $"Checking http://127.0.0.1:{port}{app.HealthPath}…");
        // Real elapsed time (not the injectable clock): this waits on an actual process.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(options.Value.DeployHealthTimeoutSeconds);

        while (waited.Elapsed < limit)
        {
            if (await IsHealthyAsync(port, app.HealthPath, context.CancellationToken))
            {
                context.Log("Healthy.");

                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
        }

        return false;
    }

    public async Task<bool> IsHealthyAsync(int port, string? path, CancellationToken cancellationToken)
    {
        try
        {
            if (path is null)
            {
                using var socket = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync("127.0.0.1", port, timeout.Token);

                return true;
            }

            var client = httpClients.CreateClient("health");
            using var response = await client.GetAsync($"http://127.0.0.1:{port}{path}", cancellationToken);

            return (int)response.StatusCode < 500;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task MarkActiveAsync(App app, AppRelease release)
    {
        await db.Releases.Where(r => r.AppId == app.Id && r.Status == ReleaseStatus.Active && r.Id != release.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReleaseStatus.Inactive));
        release.Status = ReleaseStatus.Active;
        release.FinishedAt ??= time.GetUtcNow();
        release.Error = null;
        app.CurrentRelease = release.Name;
        await db.SaveChangesAsync();
    }

    private async Task PruneAsync(App app, JobContext context)
    {
        var state = await agent.Deploy.PruneReleasesAsync(new PruneRequest { User = app.SystemUser, App = app.Name, Keep = app.KeepReleases },
            cancellationToken: context.CancellationToken);
        var kept = state.Releases.ToHashSet();
        // History is kept; releases no longer on disk just can't be rolled back to.
        var removed = await db.Releases
            .Where(r => r.AppId == app.Id && r.Status == ReleaseStatus.Inactive && r.Error == null && !kept.Contains(r.Name))
            .ToListAsync();

        if (removed.Count > 0)
        {
            context.Log($"Removed {removed.Count} old release(s) from disk.");

            foreach (var old in removed)
            {
                old.Error = RemovedFromDisk;
            }

            await db.SaveChangesAsync();
        }
    }

    /// <summary>Purges Cloudflare's cache for every site whose files come from this app.</summary>
    private async Task PurgeCloudflareAsync(App app, JobContext context)
    {
        if (!app.PurgeCloudflare)
        {
            return;
        }

        var cloudflare = context.Services.GetRequiredService<CloudflareService>();

        if (await cloudflare.GetAccountAsync() is null)
        {
            return;
        }

        var sites = await db.Sites.Include(s => s.Routes).ToListAsync();
        var prefix = app.AppDirectory + "/";

        foreach (var site in sites.Where(s => s.Routes.Any(r => r.Target.StartsWith(prefix, StringComparison.Ordinal))))
        {
            try
            {
                if (await cloudflare.ZoneForAsync(site.Domains[0]) is { } zone)
                {
                    await cloudflare.PurgeAsync(zone.Id, site.Domains, null, context.CancellationToken);
                    context.Log($"Purged Cloudflare's cache for {string.Join(", ", site.Domains)}.");
                }
            }
            catch (Exception e) when (e is CloudflareApiException or InvalidOperationException or HttpRequestException)
            {
                context.Log($"Cloudflare purge for {site.Name} failed: {e.Message}");
            }
        }
    }

    private static async Task<bool> StreamAsync(AsyncServerStreamingCall<DeployOutput> call, JobContext context)
    {
        using (call)
        {
            var success = false;

            await foreach (var output in call.ResponseStream.ReadAllAsync(context.CancellationToken))
            {
                if (output.Line.Length > 0)
                {
                    context.Log(output.Line);
                }

                if (output.Finished)
                {
                    success = output.Success;
                }
            }

            return success;
        }
    }

    /// <summary>
    /// Handles a GitHub push: verifies the signature, ignores replays, and deploys every auto-deploy
    /// app on that branch whose path filters match the changed files.
    /// </summary>
    public async Task<(int Status, string Result)> HandleWebhookAsync(int repositoryId, string? deliveryId, string? eventName, string? signature, byte[] body)
    {
        var repository = await db.Repositories.FindAsync(repositoryId);

        if (repository is null)
        {
            return (404, "Unknown repository.");
        }

        if (!SignatureValid(Unprotect(repository.WebhookSecretProtected), signature, body))
        {
            logger.LogWarning("Webhook for {Repository} had an invalid signature", repository.FullName);

            return (401, "Invalid signature.");
        }

        if (string.IsNullOrEmpty(deliveryId) || await db.WebhookDeliveries.AnyAsync(d => d.Id == deliveryId))
        {
            return (409, "Duplicate or missing delivery id.");
        }

        var delivery = new WebhookDelivery { Id = deliveryId, RepositoryId = repositoryId, Event = eventName ?? "", Result = "" };
        db.WebhookDeliveries.Add(delivery);

        if (eventName == "ping")
        {
            delivery.Result = "Ping received.";
            await db.SaveChangesAsync();

            return (200, delivery.Result);
        }

        if (eventName != "push")
        {
            delivery.Result = $"Ignored {eventName} event.";
            await db.SaveChangesAsync();

            return (202, delivery.Result);
        }

        using var payload = JsonDocument.Parse(body);
        var gitRef = payload.RootElement.TryGetProperty("ref", out var r) ? r.GetString() : null;
        delivery.Ref = gitRef;

        if (gitRef != $"refs/heads/{repository.Branch}")
        {
            delivery.Result = $"Push to {gitRef} ignored (deploying {repository.Branch}).";
            await db.SaveChangesAsync();

            return (202, delivery.Result);
        }

        var changed = ChangedFiles(payload.RootElement);
        var results = new List<string>();

        foreach (var app in await db.Apps.Where(a => a.RepositoryId == repositoryId && a.AutoDeploy).ToListAsync())
        {
            if (changed is not null && app.DeployPaths.Count > 0 && !changed.Any(file => app.DeployPaths.Any(p => file.StartsWith(p.TrimStart('/'), StringComparison.Ordinal))))
            {
                results.Add($"{app.Name}: no changes under {string.Join(", ", app.DeployPaths)}");

                continue;
            }

            try
            {
                var job = await StartDeployAsync(app, "webhook", null);
                results.Add($"{app.Name}: deploying (job {job.Id})");
            }
            catch (JobConflictException)
            {
                results.Add($"{app.Name}: a deploy is already running");
            }
        }

        delivery.Result = results.Count == 0 ? "No auto-deploy apps for this repository." : string.Join("; ", results);
        await db.SaveChangesAsync();

        return (202, delivery.Result);
    }

    public static bool SignatureValid(string secret, string? signature, byte[] body)
    {
        if (signature is null || !signature.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>Files changed by a push, or null when GitHub truncated the list (then every app deploys).</summary>
    public static HashSet<string>? ChangedFiles(JsonElement push)
    {
        if (!push.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // GitHub includes at most 20 commits (and 3000 files per commit) in a push payload.
        if (commits.GetArrayLength() >= 20)
        {
            return null;
        }

        var files = new HashSet<string>(StringComparer.Ordinal);

        foreach (var commit in commits.EnumerateArray())
        {
            foreach (var field in new[] { "added", "removed", "modified" })
            {
                if (commit.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    files.UnionWith(list.EnumerateArray().Select(f => f.GetString() ?? ""));
                }
            }
        }

        return files;
    }
}
