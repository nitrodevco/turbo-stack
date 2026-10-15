namespace TurboManager.Core;

/// <summary>
/// Every user-visible product name and system identifier lives here so a rename is a one-file change.
/// Keep install/install.sh (PRODUCT_* variables) in sync.
/// </summary>
public static class Branding
{
    public const string ProductName = "Turbo Stack";

    /// <summary>Short identifier used for system users, paths, units and CLI names.</summary>
    public const string Slug = "turbo-stack";

    public const string ShortName = "ts";

    public const string SystemUser = Slug;

    public const string AgentUnit = $"{ShortName}-agent.service";

    public const string PanelUnit = $"{ShortName}-panel.service";
}
