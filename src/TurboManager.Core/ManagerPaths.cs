namespace TurboManager.Core;

/// <summary>Well-known filesystem locations on a managed server.</summary>
public static class ManagerPaths
{
    public const string InstallRoot = $"/opt/{Branding.Slug}";

    public const string StateRoot = $"/var/lib/{Branding.Slug}";

    public const string LogRoot = $"/var/log/{Branding.Slug}";

    public const string RunRoot = $"/run/{Branding.Slug}";

    public const string AgentSocket = $"{RunRoot}/agent.sock";

    /// <summary>SHA-256 hex of the one-time setup token, written by install.sh, deleted once the panel is claimed.</summary>
    public const string SetupTokenHashFile = $"{StateRoot}/setup-token.sha256";
}
