using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Deploy;
using TurboManager.Panel.Http;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Endpoints;

public static class DeployEndpoints
{
    public sealed record RuntimeInstallRequest(string? Channel);

    public static void MapDeployEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.AppsView);
        var deploy = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.AppsDeploy);
        var manage = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.AppsManage);

        // ----- Repositories -----

        view.MapGet("/repositories", async (HttpContext http, PanelDbContext db) =>
            Results.Ok((await db.Repositories.OrderBy(r => r.Name).ToListAsync()).Select(r => RepositoryView(r, http.Request))));

        view.MapGet("/repositories/{id:int}", async (int id, HttpContext http, PanelDbContext db) =>
        {
            var repository = await db.Repositories.FindAsync(id);

            if (repository is null)
            {
                return Results.NotFound();
            }

            var deliveries = await db.WebhookDeliveries.Where(d => d.RepositoryId == id).OrderByDescending(d => d.ReceivedAt).Take(20).ToListAsync();
            var apps = await db.Apps.Where(a => a.RepositoryId == id).Select(a => new { a.Id, a.Name }).ToListAsync();

            return Results.Ok(new { repository = RepositoryView(repository, http.Request), deliveries, apps });
        });

        manage.MapPost("/repositories", async (RepositoryRequest request, HttpContext http, PanelDbContext db, DeployService deployments,
            AuditLog audit, CancellationToken cancellationToken) =>
        {
            if (DeployModels.ValidateRepository(request) is { } error)
            {
                return Invalid(error);
            }

            if (await db.Repositories.AnyAsync(r => r.SystemUser == request.SystemUser && r.Name == request.Name, cancellationToken))
            {
                return Invalid($"{request.SystemUser} already has a repository called {request.Name}.");
            }

            var repository = new GitRepository
            {
                Name = request.Name!,
                SystemUser = request.SystemUser!,
                Owner = "",
                RepositoryName = "",
                WebhookSecretProtected = deployments.Protect(DeployService.NewWebhookSecret()),
            };
            DeployModels.Apply(repository, request);
            await deployments.PrepareRepositoryAsync(repository, cancellationToken);
            db.Repositories.Add(repository);
            audit.Record("repository.added", await CurrentUser.GetAsync(http, db), target: repository.FullName, details: $"user {repository.SystemUser}");
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(RepositoryView(repository, http.Request));
        });

        manage.MapPut("/repositories/{id:int}", async (int id, RepositoryRequest request, HttpContext http, PanelDbContext db, DeployService deployments,
            AuditLog audit, CancellationToken cancellationToken) =>
        {
            var repository = await db.Repositories.FindAsync([id], cancellationToken);

            if (repository is null)
            {
                return Results.NotFound();
            }

            // Name and Linux user are fixed once created (they're the checkout directory and key owner).
            if (DeployModels.ValidateRepository(request with { Name = repository.Name, SystemUser = repository.SystemUser }) is { } error)
            {
                return Invalid(error);
            }

            DeployModels.Apply(repository, request);
            await deployments.PrepareRepositoryAsync(repository, cancellationToken);
            audit.Record("repository.updated", await CurrentUser.GetAsync(http, db), target: repository.FullName);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(RepositoryView(repository, http.Request));
        });

        manage.MapPost("/repositories/{id:int}/test", async (int id, PanelDbContext db, DeployService deployments, CancellationToken cancellationToken) =>
        {
            var repository = await db.Repositories.FindAsync([id], cancellationToken);

            if (repository is null)
            {
                return Results.NotFound();
            }

            var result = await deployments.TestAccessAsync(repository, cancellationToken);

            return Results.Ok(new { result.Ok, result.Error, result.Head });
        });

        manage.MapGet("/repositories/{id:int}/webhook", async (int id, HttpContext http, PanelDbContext db, DeployService deployments) =>
        {
            var repository = await db.Repositories.FindAsync(id);

            return repository is null
                ? Results.NotFound()
                : Results.Ok(new { url = WebhookUrl(repository, http.Request), secret = deployments.Unprotect(repository.WebhookSecretProtected) });
        });

        manage.MapPost("/repositories/{id:int}/webhook/rotate", async (int id, HttpContext http, PanelDbContext db, DeployService deployments, AuditLog audit) =>
        {
            var repository = await db.Repositories.FindAsync(id);

            if (repository is null)
            {
                return Results.NotFound();
            }

            var secret = DeployService.NewWebhookSecret();
            repository.WebhookSecretProtected = deployments.Protect(secret);
            audit.Record("repository.webhook_secret_rotated", await CurrentUser.GetAsync(http, db), target: repository.FullName);
            await db.SaveChangesAsync();

            return Results.Ok(new { url = WebhookUrl(repository, http.Request), secret });
        });

        manage.MapDelete("/repositories/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var repository = await db.Repositories.FindAsync(id);

            if (repository is null)
            {
                return Results.NotFound();
            }

            if (await db.Apps.AnyAsync(a => a.RepositoryId == id))
            {
                return Results.Problem("Delete the apps that deploy from this repository first.", statusCode: StatusCodes.Status409Conflict);
            }

            db.Repositories.Remove(repository);
            audit.Record("repository.removed", await CurrentUser.GetAsync(http, db), target: repository.FullName);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        // ----- Apps -----

        view.MapGet("/apps", async (PanelDbContext db) =>
        {
            var apps = await db.Apps.Include(a => a.Repository).OrderBy(a => a.Name).ToListAsync();
            var latest = await db.Releases.GroupBy(r => r.AppId).Select(g => g.OrderByDescending(r => r.Id).First()).ToListAsync();

            return Results.Ok(apps.Select(a => new
            {
                a.Id,
                a.Name,
                a.SystemUser,
                repository = a.Repository!.FullName,
                a.Repository.Branch,
                a.CurrentRelease,
                a.AutoDeploy,
                hasService = a.ServiceCommand is not null,
                staticPath = StaticPath(a),
                lastRelease = latest.FirstOrDefault(r => r.AppId == a.Id) is { } r
                    ? new { r.Name, status = r.Status.ToString().ToLowerInvariant(), r.StartedAt, r.CommitMessage }
                    : null,
            }));
        });

        view.MapGet("/apps/{id:int}", async (int id, PanelDbContext db) =>
        {
            var found = await db.Apps.Include(a => a.Repository).FirstOrDefaultAsync(a => a.Id == id);

            if (found is null)
            {
                return Results.NotFound();
            }

            var releases = await db.Releases.Where(r => r.AppId == id).OrderByDescending(r => r.Id).Take(30)
                .Select(r => new
                {
                    r.Name, r.Commit, r.CommitMessage, r.CommitAuthor, status = r.Status.ToString().ToLowerInvariant(),
                    r.Trigger, r.TriggeredBy, r.JobId, r.StartedAt, r.FinishedAt, r.Error,
                })
                .ToListAsync();

            return Results.Ok(new
            {
                app = new
                {
                    found.Id, found.Name, found.SystemUser, found.RepositoryId, repository = found.Repository!.FullName, found.WorkingDirectory,
                    found.BuildScript, found.Runtimes, sharedPaths = found.SharedPaths.Select(p => new { p.Path, p.Directory }), found.StaticOutput,
                    found.ServiceCommand, found.ServicePort, found.HealthPath, found.MemoryMaxMb, found.StopTimeoutSeconds, found.KeepReleases,
                    found.AutoDeploy, found.DeployPaths, found.PurgeCloudflare, found.CurrentRelease, found.ServiceDirectory, found.RecipeId,
                    found.RecipeInputs, found.Ports,
                    staticPath = StaticPath(found), unit = found.ServiceCommand is null ? null : $"tm-app-{found.Name}.service",
                },
                releases,
            });
        });

        manage.MapPost("/apps", async (AppRequest request, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            if (DeployModels.ValidateApp(request) is { } error)
            {
                return Invalid(error);
            }

            var repository = await db.Repositories.FindAsync(request.RepositoryId);

            if (repository is null)
            {
                return Invalid("Choose a repository.");
            }

            if (await db.Apps.AnyAsync(a => a.Name == request.Name))
            {
                return Invalid($"An app called {request.Name} already exists.");
            }

            var created = new App { Name = "", SystemUser = repository.SystemUser };
            DeployModels.Apply(created, request);
            db.Apps.Add(created);
            audit.Record("app.created", await CurrentUser.GetAsync(http, db), target: created.Name, details: repository.FullName);
            await db.SaveChangesAsync();

            return Results.Ok(new { created.Id });
        });

        manage.MapPut("/apps/{id:int}", async (int id, AppRequest request, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var existing = await db.Apps.FindAsync(id);

            if (existing is null)
            {
                return Results.NotFound();
            }

            // Name and repository are fixed (they define the directory and its owner).
            request = request with { Name = existing.Name, RepositoryId = existing.RepositoryId };

            if (DeployModels.ValidateApp(request) is { } error)
            {
                return Invalid(error);
            }

            DeployModels.Apply(existing, request);
            audit.Record("app.updated", await CurrentUser.GetAsync(http, db), target: existing.Name);
            await db.SaveChangesAsync();

            return Results.Ok(new { existing.Id });
        });

        manage.MapDelete("/apps/{id:int}", async (int id, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var existing = await db.Apps.FindAsync([id], cancellationToken);

            if (existing is null)
            {
                return Results.NotFound();
            }

            await agent.Deploy.RemoveAppAsync(new AppRef { User = existing.SystemUser, App = existing.Name }, cancellationToken: cancellationToken);
            db.Apps.Remove(existing);
            audit.Record("app.deleted", await CurrentUser.GetAsync(http, db), target: existing.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        manage.MapGet("/apps/{id:int}/environment", async (int id, PanelDbContext db, DeployService deployments) =>
            await db.Apps.FindAsync(id) is { } found ? Results.Ok(deployments.GetEnvironment(found)) : Results.NotFound());

        manage.MapPut("/apps/{id:int}/environment", async (int id, Dictionary<string, string> environment, HttpContext http, PanelDbContext db,
            DeployService deployments, AuditLog audit) =>
        {
            var found = await db.Apps.FindAsync(id);

            if (found is null)
            {
                return Results.NotFound();
            }

            if (environment.FirstOrDefault(e => !System.Text.RegularExpressions.Regex.IsMatch(e.Key, "^[A-Za-z_][A-Za-z0-9_]{0,127}$")
                    || e.Value.Contains('\n') || e.Value.Contains('\r')) is { Key: not null } bad)
            {
                return Invalid($"\"{bad.Key}\": names are letters, digits and _, and values can't contain line breaks.");
            }

            deployments.SetEnvironment(found, environment);
            audit.Record("app.environment_updated", await CurrentUser.GetAsync(http, db), target: found.Name,
                details: $"{environment.Count} variables: {string.Join(", ", environment.Keys.Order())}");
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        deploy.MapPost("/apps/{id:int}/deploy", async (int id, HttpContext http, PanelDbContext db, DeployService deployments, AuditLog audit) =>
        {
            var found = await db.Apps.FindAsync(id);

            if (found is null)
            {
                return Results.NotFound();
            }

            var user = await CurrentUser.GetAsync(http, db);

            try
            {
                var job = await deployments.StartDeployAsync(found, "manual", user);
                audit.Record("app.deploy_started", user, target: found.Name);
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }
            catch (JobConflictException)
            {
                return Results.Problem($"{found.Name} is already deploying.", statusCode: StatusCodes.Status409Conflict);
            }
        });

        deploy.MapPost("/apps/{id:int}/releases/{release}/activate", async (int id, string release, HttpContext http, PanelDbContext db,
            DeployService deployments, AuditLog audit) =>
        {
            var found = await db.Apps.FindAsync(id);

            if (found is null)
            {
                return Results.NotFound();
            }

            var user = await CurrentUser.GetAsync(http, db);

            try
            {
                var job = await deployments.StartRollbackAsync(found, release, user);
                audit.Record("app.rollback_started", user, target: found.Name, details: release);
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }
            catch (JobConflictException)
            {
                return Results.Problem($"{found.Name} is busy deploying.", statusCode: StatusCodes.Status409Conflict);
            }
        });

        // Applies changed service settings (command, environment, limits) without a new deploy.
        deploy.MapPost("/apps/{id:int}/restart", async (int id, HttpContext http, PanelDbContext db, DeployService deployments, AgentChannelFactory agent,
            AuditLog audit, CancellationToken cancellationToken) =>
        {
            var found = await db.Apps.FindAsync([id], cancellationToken);

            if (found is null)
            {
                return Results.NotFound();
            }

            if (found.ServiceCommand is null || found.CurrentRelease is null)
            {
                return Results.Problem("This app has no running service yet. Deploy it first.", statusCode: StatusCodes.Status409Conflict);
            }

            await deployments.WriteServiceAsync(found, cancellationToken);
            await agent.Services.ControlServiceAsync(new ControlServiceRequest { Name = $"tm-app-{found.Name}.service", Action = ServiceAction.Restart },
                cancellationToken: cancellationToken);
            audit.Record("app.restarted", await CurrentUser.GetAsync(http, db), target: found.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        // ----- Runtimes -----

        view.MapGet("/runtimes", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok((await agent.Deploy.ListRuntimesAsync(new Empty(), cancellationToken: cancellationToken)).Items));

        manage.MapPost("/runtimes/{name}/install", async (string name, RuntimeInstallRequest request, HttpContext http, PanelDbContext db, JobRunner jobs,
            AuditLog audit) =>
        {
            if (name is not ("dotnet" or "node"))
            {
                return Results.NotFound();
            }

            var channel = request.Channel ?? (name == "dotnet" ? "10.0" : "24");
            var user = await CurrentUser.GetAsync(http, db);

            try
            {
                var job = await jobs.StartAsync("runtimes", $"Install {name} {channel}", user, async context =>
                {
                    var agentClient = context.Services.GetRequiredService<AgentChannelFactory>();
                    using var call = agentClient.Deploy.InstallRuntime(new RuntimeRequest { Name = name, Channel = channel },
                        cancellationToken: context.CancellationToken);
                    var ok = false;

                    await foreach (var line in call.ResponseStream.ReadAllAsync(context.CancellationToken))
                    {
                        if (line.Finished)
                        {
                            ok = line.ExitCode == 0;
                        }
                        else
                        {
                            context.Log(line.Line);
                        }
                    }

                    return ok;
                });

                audit.Record("runtime.install_started", user, target: $"{name} {channel}");
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }
            catch (JobConflictException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // ----- GitHub webhooks (no session; authenticated by the HMAC signature) -----

        app.MapPost("/hooks/github/{id:int}", async (int id, HttpContext http, DeployService deployments) =>
        {
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, http.RequestAborted);

            if (body.Length > 5 * 1024 * 1024)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var headers = http.Request.Headers;
            var (status, result) = await deployments.HandleWebhookAsync(id, headers["X-GitHub-Delivery"], headers["X-GitHub-Event"],
                headers["X-Hub-Signature-256"], body.ToArray());

            return Results.Json(new { result }, statusCode: status);
        }).RequireRateLimiting(RateLimits.Webhooks);
    }

    private static object RepositoryView(GitRepository repository, HttpRequest request) => new
    {
        repository.Id,
        repository.Name,
        repository.SystemUser,
        repository.Owner,
        repository.RepositoryName,
        fullName = repository.FullName,
        repository.Branch,
        isPrivate = repository.Auth == RepositoryAuth.DeployKey,
        repository.PublicKey,
        repository.KeyFingerprint,
        repository.Submodules,
        submoduleKeys = repository.SubmoduleKeys.Select(k => new { repository = $"{k.Owner}/{k.Name}", k.PublicKey, k.Fingerprint }),
        repository.LastSyncedAt,
        repository.LastCommit,
        repository.LastCommitMessage,
        repository.LastError,
        webhookUrl = WebhookUrl(repository, request),
        deployKeysUrl = $"https://github.com/{repository.FullName}/settings/keys",
        webhooksUrl = $"https://github.com/{repository.FullName}/settings/hooks/new",
    };

    private static string WebhookUrl(GitRepository repository, HttpRequest request) =>
        $"{request.Scheme}://{request.Host}/hooks/github/{repository.Id}";

    private static string? StaticPath(App app) => app.StaticOutput is null ? null : $"{app.AppDirectory}/current/{app.StaticOutput}";

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]> { ["app"] = [message] });
}
