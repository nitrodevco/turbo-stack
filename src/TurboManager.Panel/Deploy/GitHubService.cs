using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TurboManager.Core;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Deploy;

public sealed class GitHubNotConnectedException() : InvalidOperationException("GitHub is not connected. Connect an account under Apps → Repositories first.");

public sealed record GitHubRepositoryChoice(string FullName, string Owner, string Name, bool Private, string? Description, string DefaultBranch,
    DateTimeOffset? PushedAt, bool Admin);

/// <summary>One thing done on GitHub for a repository ("ok", "skipped" or "failed").</summary>
public sealed record GitHubSetupStep(string Label, string Status, string? Detail = null);

/// <summary>
/// The connected GitHub account: lists its repositories and branches, and installs a repository's
/// deploy keys and push webhook so nothing has to be pasted into GitHub by hand.
/// </summary>
public sealed class GitHubService(
    PanelDbContext db,
    GitHubClient github,
    DeployService deployments,
    AuditLog audit,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<GitHubService> logger)
{
    /// <summary>Opens GitHub's "new classic token" page with the scope the panel needs filled in.</summary>
    public const string CreateTokenUrl = $"https://github.com/settings/tokens/new?scopes=repo,admin:repo_hook&description={Branding.Slug}";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.GitHub.v1");

    public Task<GitHubAccount?> GetAccountAsync() => db.GitHubAccounts.FirstOrDefaultAsync();

    public async Task<string> RequireTokenAsync()
    {
        var account = await GetAccountAsync() ?? throw new GitHubNotConnectedException();

        return _protector.Unprotect(account.TokenProtected);
    }

    public async Task<GitHubAccount> ConnectAsync(string token, PanelUser user, CancellationToken cancellationToken)
    {
        token = token.Trim();
        var (profile, scopes) = await github.GetUserAsync(token, cancellationToken);

        if (scopes is not null && !scopes.Split(',', StringSplitOptions.TrimEntries).Contains("repo"))
        {
            throw new InvalidOperationException("This token doesn't have the \"repo\" scope, so the panel can't see private repositories or add deploy keys.");
        }

        var account = await GetAccountAsync();

        if (account is null)
        {
            account = new GitHubAccount { TokenProtected = "", Login = "" };
            db.GitHubAccounts.Add(account);
        }

        account.TokenProtected = _protector.Protect(token);
        account.Login = profile.Login;
        account.DisplayName = profile.Name;
        account.AvatarUrl = profile.AvatarUrl;
        account.Scopes = scopes;
        account.ConnectedBy = user.Username;
        account.ConnectedAt = time.GetUtcNow();
        audit.Record("github.connected", user, target: profile.Login);
        await db.SaveChangesAsync(cancellationToken);

        return account;
    }

    /// <summary>Forgets the token. Keys and webhooks already on GitHub stay (deploys keep working).</summary>
    public async Task DisconnectAsync(PanelUser user, CancellationToken cancellationToken)
    {
        if (await GetAccountAsync() is { } account)
        {
            db.GitHubAccounts.Remove(account);
            audit.Record("github.disconnected", user, target: account.Login);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<List<GitHubRepositoryChoice>> ListRepositoriesAsync(CancellationToken cancellationToken)
    {
        var token = await RequireTokenAsync();

        return (await github.ListRepositoriesAsync(token, cancellationToken))
            .Where(r => !r.Archived)
            .Select(r => new GitHubRepositoryChoice(r.FullName, r.Owner.Login, r.Name, r.Private, r.Description, r.DefaultBranch, r.PushedAt,
                r.Permissions?.Admin ?? false))
            .ToList();
    }

    public async Task<List<string>> ListBranchesAsync(string owner, string name, CancellationToken cancellationToken) =>
        (await github.ListBranchesAsync(await RequireTokenAsync(), owner, name, cancellationToken)).Select(b => b.Name).ToList();

    /// <summary>
    /// Adds the repository's deploy key (and each private submodule's) and, with a <paramref name="webhookUrl"/>, the push
    /// webhook. Safe to repeat: keys and hooks already there are reused. Failures are reported per step, not thrown.
    /// </summary>
    public async Task<List<GitHubSetupStep>> InstallAsync(GitRepository repository, string? webhookUrl, CancellationToken cancellationToken)
    {
        var token = await RequireTokenAsync();
        var steps = new List<GitHubSetupStep>();

        if (repository.Auth == RepositoryAuth.DeployKey && repository.PublicKey is { } key)
        {
            steps.Add(await StepAsync($"Deploy key on {repository.FullName}", async () =>
                repository.GitHubDeployKeyId = await EnsureDeployKeyAsync(token, repository.Owner, repository.RepositoryName, KeyTitle(repository), key, cancellationToken)));
        }

        foreach (var submodule in repository.SubmoduleKeys.Where(s => s.PublicKey is not null))
        {
            steps.Add(await StepAsync($"Deploy key on {submodule.Owner}/{submodule.Name}", async () =>
                submodule.GitHubKeyId = await EnsureDeployKeyAsync(token, submodule.Owner, submodule.Name, KeyTitle(repository), submodule.PublicKey!, cancellationToken)));
        }

        if (webhookUrl is not null)
        {
            var secret = deployments.Unprotect(repository.WebhookSecretProtected);
            steps.Add(await StepAsync($"Push webhook on {repository.FullName}", async () =>
                repository.GitHubHookId = await EnsureHookAsync(token, repository, webhookUrl, secret, cancellationToken)));
        }

        await db.SaveChangesAsync(cancellationToken);

        return steps;
    }

    /// <summary>After the secret was rotated: updates the webhook the panel created, if any.</summary>
    public async Task<GitHubSetupStep?> UpdateHookSecretAsync(GitRepository repository, string webhookUrl, string secret, CancellationToken cancellationToken)
    {
        if (repository.GitHubHookId is not { } hookId || await GetAccountAsync() is null)
        {
            return null;
        }

        var token = await RequireTokenAsync();

        return await StepAsync("Webhook secret on GitHub", () =>
            github.UpdateHookAsync(token, repository.Owner, repository.RepositoryName, hookId, webhookUrl, secret, cancellationToken));
    }

    /// <summary>Removes the keys and webhook the panel added (best effort: the repository is going away either way).</summary>
    public async Task RemoveAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        if (await GetAccountAsync() is null)
        {
            return;
        }

        var token = await RequireTokenAsync();
        var removals = new List<(string Repo, Func<Task> Remove)>();

        if (repository.GitHubDeployKeyId is { } keyId)
        {
            removals.Add((repository.FullName, () => github.DeleteDeployKeyAsync(token, repository.Owner, repository.RepositoryName, keyId, cancellationToken)));
        }

        if (repository.GitHubHookId is { } hookId)
        {
            removals.Add((repository.FullName, () => github.DeleteHookAsync(token, repository.Owner, repository.RepositoryName, hookId, cancellationToken)));
        }

        foreach (var submodule in repository.SubmoduleKeys)
        {
            if (submodule.GitHubKeyId is { } id)
            {
                removals.Add(($"{submodule.Owner}/{submodule.Name}", () => github.DeleteDeployKeyAsync(token, submodule.Owner, submodule.Name, id, cancellationToken)));
            }
        }

        foreach (var (repo, remove) in removals)
        {
            try
            {
                await remove();
            }
            catch (Exception e) when (e is GitHubApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Could not clean up {Repository} on GitHub", repo);
            }
        }
    }

    private async Task<long> EnsureDeployKeyAsync(string token, string owner, string name, string title, string publicKey, CancellationToken cancellationToken)
    {
        var material = KeyMaterial(publicKey);
        var existing = await github.ListDeployKeysAsync(token, owner, name, cancellationToken);

        if (existing.FirstOrDefault(k => KeyMaterial(k.Key) == material) is { } found)
        {
            return found.Id;
        }

        return (await github.AddDeployKeyAsync(token, owner, name, title, material, cancellationToken)).Id;
    }

    private async Task<long> EnsureHookAsync(string token, GitRepository repository, string url, string secret, CancellationToken cancellationToken)
    {
        var (owner, name) = (repository.Owner, repository.RepositoryName);
        var hooks = await github.ListHooksAsync(token, owner, name, cancellationToken);

        // Ours (by id, if the URL changed since) or any hook already pointing here: update it rather than add a duplicate.
        if ((hooks.FirstOrDefault(h => h.Id == repository.GitHubHookId) ?? hooks.FirstOrDefault(h => h.Config.Url == url)) is { } existing)
        {
            return (await github.UpdateHookAsync(token, owner, name, existing.Id, url, secret, cancellationToken)).Id;
        }

        return (await github.CreateHookAsync(token, owner, name, url, secret, cancellationToken)).Id;
    }

    private async Task<GitHubSetupStep> StepAsync(string label, Func<Task> action)
    {
        try
        {
            await action();

            return new GitHubSetupStep(label, "ok");
        }
        catch (GitHubApiException e)
        {
            var hint = e.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                ? " The connected account needs admin rights on the repository (and the token the \"repo\" scope)."
                : "";

            return new GitHubSetupStep(label, "failed", e.Message + hint);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new GitHubSetupStep(label, "failed", $"Could not reach GitHub: {e.Message}");
        }
    }

    private static string KeyTitle(GitRepository repository) => $"{Branding.ProductName} ({repository.SystemUser}@{Environment.MachineName})";

    /// <summary>"type base64" without the comment, which GitHub drops.</summary>
    private static string KeyMaterial(string key) => string.Join(' ', key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
}
