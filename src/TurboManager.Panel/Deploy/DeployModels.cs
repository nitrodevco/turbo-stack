using System.Text.RegularExpressions;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Deploy;

public sealed record RepositoryRequest(
    string? Name,
    string? SystemUser,
    string? Repository,
    string? Branch,
    bool Private,
    bool Submodules,
    List<string>? PrivateSubmodules);

public sealed record SharedPathModel(string Path, bool Directory);

public sealed record AppRequest(
    string? Name,
    int RepositoryId,
    string? WorkingDirectory,
    string? BuildScript,
    List<string>? Runtimes,
    List<SharedPathModel>? SharedPaths,
    string? StaticOutput,
    string? ServiceCommand,
    int? ServicePort,
    string? HealthPath,
    int MemoryMaxMb = 0,
    int StopTimeoutSeconds = 30,
    int KeepReleases = 5,
    bool AutoDeploy = false,
    List<string>? DeployPaths = null,
    bool PurgeCloudflare = true,
    string? ServiceDirectory = null,
    bool Console = false);

/// <summary>Validation and normalisation for repository and app input (the agent validates again).</summary>
public static partial class DeployModels
{
    public static readonly HashSet<string> ReservedUsers =
        ["root", "turbo-manager", "www-data", "nobody", "postgres", "mysql", "daemon", "bin", "sys", "ubuntu", "admin"];

    public static readonly string[] KnownRuntimes = ["dotnet", "node"];

    /// <summary>Accepts owner/repo, https://github.com/owner/repo(.git) or git@github.com:owner/repo.git.</summary>
    public static (string Owner, string Name)? ParseRepository(string? input)
    {
        var match = RepositoryPattern().Match((input ?? "").Trim());

        return match.Success ? (match.Groups["owner"].Value, match.Groups["name"].Value) : null;
    }

    public static string? ValidateRepository(RepositoryRequest request)
    {
        if (!SlugPattern().IsMatch(request.Name ?? ""))
        {
            return "Name: lowercase letters, digits and dashes (up to 40).";
        }

        if (!UserPattern().IsMatch(request.SystemUser ?? "") || ReservedUsers.Contains(request.SystemUser!))
        {
            return "Linux user: 2-32 lowercase letters, digits or dashes, and not a system account (e.g. \"hotel\").";
        }

        if (ParseRepository(request.Repository) is null)
        {
            return "Repository: use owner/name or the GitHub URL.";
        }

        if (!BranchPattern().IsMatch(request.Branch ?? "") || request.Branch!.Contains("..", StringComparison.Ordinal))
        {
            return "Enter a valid branch name.";
        }

        return (request.PrivateSubmodules ?? []).FirstOrDefault(s => ParseRepository(s) is null) is { } bad
            ? $"\"{bad}\" isn't a GitHub repository (owner/name)."
            : null;
    }

    public static void Apply(GitRepository repository, RepositoryRequest request)
    {
        var (owner, name) = ParseRepository(request.Repository)!.Value;
        repository.Owner = owner;
        repository.RepositoryName = name;
        repository.Branch = request.Branch!.Trim();
        repository.Auth = request.Private ? RepositoryAuth.DeployKey : RepositoryAuth.Public;
        repository.Submodules = request.Submodules;

        var wanted = (request.PrivateSubmodules ?? []).Select(s => ParseRepository(s)!.Value).Distinct().ToList();
        repository.SubmoduleKeys.RemoveAll(k => !wanted.Contains((k.Owner, k.Name)));

        foreach (var (subOwner, subName) in wanted.Where(w => !repository.SubmoduleKeys.Any(k => k.Owner == w.Owner && k.Name == w.Name)))
        {
            var key = $"{repository.Name}-{SlugCleanup().Replace(subName.ToLowerInvariant(), "-")}";
            repository.SubmoduleKeys.Add(new SubmoduleKey { Owner = subOwner, Name = subName, KeyName = key[..Math.Min(48, key.Length)].Trim('-') });
        }
    }

    public static string? ValidateApp(AppRequest request)
    {
        if (!SlugPattern().IsMatch(request.Name ?? ""))
        {
            return "Name: lowercase letters, digits and dashes (up to 40).";
        }

        foreach (var path in new[] { request.WorkingDirectory, request.ServiceDirectory, request.StaticOutput }.Concat((request.SharedPaths ?? []).Select(p => p.Path))
                     .Concat(request.DeployPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!IsRelativePath(path!))
            {
                return $"\"{path}\" must be a path inside the repository (no leading \"/\" or \"..\").";
            }
        }

        if ((request.Runtimes ?? []).Any(r => !KnownRuntimes.Contains(r)))
        {
            return "Runtimes can be dotnet and/or node.";
        }

        if (request.ServiceCommand is { } command && (command.Contains('\n') || command.Length > 4000))
        {
            return "The service command must be a single line.";
        }

        if (request.ServicePort is < 1 or > 65535)
        {
            return "Port must be between 1 and 65535.";
        }

        if (!string.IsNullOrEmpty(request.HealthPath) && (!request.HealthPath.StartsWith('/') || request.HealthPath.Contains(' ')))
        {
            return "Health check path must start with \"/\".";
        }

        if (!string.IsNullOrEmpty(request.HealthPath) && request.ServicePort is null)
        {
            return "A health check path needs the port the service listens on.";
        }

        if (request.MemoryMaxMb is < 0 or > 262_144 || request.KeepReleases is < 1 or > 50 || request.StopTimeoutSeconds is < 1 or > 600)
        {
            return "Memory limit, releases to keep (1-50) or stop timeout (1-600 s) out of range.";
        }

        return null;
    }

    public static void Apply(App app, AppRequest request)
    {
        app.Name = request.Name!;
        app.RepositoryId = request.RepositoryId;
        app.WorkingDirectory = Clean(request.WorkingDirectory) ?? "";
        app.BuildScript = (request.BuildScript ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        app.Runtimes = (request.Runtimes ?? []).Distinct().ToList();
        app.SharedPaths = (request.SharedPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Path))
            .Select(p => new AppSharedPath { Path = Clean(p.Path)!, Directory = p.Directory }).ToList();
        app.StaticOutput = Clean(request.StaticOutput);
        app.ServiceCommand = string.IsNullOrWhiteSpace(request.ServiceCommand) ? null : request.ServiceCommand.Trim();
        app.ServicePort = request.ServicePort;
        app.HealthPath = string.IsNullOrWhiteSpace(request.HealthPath) ? null : request.HealthPath.Trim();
        app.MemoryMaxMb = request.MemoryMaxMb;
        app.StopTimeoutSeconds = request.StopTimeoutSeconds;
        app.KeepReleases = request.KeepReleases;
        app.AutoDeploy = request.AutoDeploy;
        app.DeployPaths = (request.DeployPaths ?? []).Select(Clean).OfType<string>().ToList();
        app.PurgeCloudflare = request.PurgeCloudflare;
        app.ServiceDirectory = Clean(request.ServiceDirectory);
        app.Console = request.Console && app.ServiceCommand is not null;
    }

    public static bool IsRelativePath(string path)
    {
        var trimmed = path.Trim().Trim('/');

        return !path.TrimStart().StartsWith('/') && RelativePathPattern().IsMatch(trimmed) && trimmed.Split('/').All(p => p is not ("" or "." or ".."));
    }

    private static string? Clean(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('/');

    [GeneratedRegex(@"^(?:https?://github\.com/|git@github\.com:|ssh://git@github\.com/)?(?<owner>[A-Za-z0-9][A-Za-z0-9-]{0,38})/(?<name>[A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex("^[A-Za-z0-9._/-]{1,300}$")]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SlugCleanup();
}
