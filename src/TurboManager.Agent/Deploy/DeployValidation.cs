using System.Text.RegularExpressions;
using TurboManager.Agent.Validation;

namespace TurboManager.Agent.Deploy;

/// <summary>Input patterns for deploy operations. Everything becomes part of a path or command line.</summary>
public static partial class DeployValidation
{
    public static string User(string user) =>
        UserPattern().IsMatch(user) ? user : throw Validate.Invalid($"Invalid app user \"{user}\" (2-32 lowercase letters, digits, '-').");

    public static string App(string app) =>
        SlugPattern().IsMatch(app) ? app : throw Validate.Invalid($"Invalid app name \"{app}\".");

    public static string RepoDir(string dir) =>
        SlugPattern().IsMatch(dir) ? dir : throw Validate.Invalid($"Invalid repository directory \"{dir}\".");

    public static string KeyName(string key) =>
        KeyPattern().IsMatch(key) ? key : throw Validate.Invalid($"Invalid key name \"{key}\".");

    public static string Release(string release) =>
        ReleasePattern().IsMatch(release) ? release : throw Validate.Invalid($"Invalid release id \"{release}\".");

    public static string Owner(string owner) =>
        OwnerPattern().IsMatch(owner) ? owner : throw Validate.Invalid($"Invalid GitHub owner \"{owner}\".");

    public static string RepositoryName(string name) =>
        RepoNamePattern().IsMatch(name) && name is not ("." or "..") ? name : throw Validate.Invalid($"Invalid repository name \"{name}\".");

    public static string Branch(string branch) =>
        BranchPattern().IsMatch(branch) && !branch.Contains("..", StringComparison.Ordinal) && !branch.EndsWith('/') && !branch.EndsWith(".lock", StringComparison.Ordinal)
            ? branch
            : throw Validate.Invalid($"Invalid branch \"{branch}\".");

    public static string Host(string host) =>
        HostPattern().IsMatch(host) ? host : throw Validate.Invalid($"Invalid host \"{host}\".");

    /// <summary>A path inside a release ("" allowed when <paramref name="allowEmpty"/>): no absolute paths, no "..".</summary>
    public static string RelativePath(string path, bool allowEmpty = false)
    {
        path = path.Trim().Trim('/');

        if (path.Length == 0)
        {
            return allowEmpty ? "" : throw Validate.Invalid("Path is required.");
        }

        if (!RelativePathPattern().IsMatch(path) || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw Validate.Invalid($"Invalid path \"{path}\" (relative, letters, digits, '.', '_', '-' and '/').");
        }

        return path;
    }

    public static IReadOnlyDictionary<string, string> Environment(IDictionary<string, string> environment)
    {
        foreach (var (key, value) in environment)
        {
            if (!EnvKeyPattern().IsMatch(key))
            {
                throw Validate.Invalid($"Invalid environment variable name \"{key}\".");
            }

            if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
            {
                throw Validate.Invalid($"Environment variable {key} can't contain line breaks.");
            }
        }

        return environment.AsReadOnly();
    }

    /// <summary>One-line commands and scripts: no NUL bytes; scripts may span lines, commands may not.</summary>
    public static string Command(string command) =>
        command.Length is > 0 and <= 4000 && !command.Contains('\n') && !command.Contains('\r') && !command.Contains('\0')
            ? command
            : throw Validate.Invalid("The service command must be a single line.");

    public static string Script(string script) =>
        script.Length <= 64 * 1024 && !script.Contains('\0') ? script.Replace("\r\n", "\n", StringComparison.Ordinal) : throw Validate.Invalid("Invalid build script.");

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[0-9]{14}-[0-9a-f]{7}$")]
    private static partial Regex ReleasePattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoNamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,253}$")]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[A-Za-z0-9._/-]{1,300}$")]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvKeyPattern();
}
