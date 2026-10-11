using TurboManager.Core;

namespace TurboManager.Panel;

public sealed class PanelOptions
{
    public const string Section = "Panel";

    /// <summary>SQLite database, data-protection keys and other panel state.</summary>
    public string DataDirectory { get; set; } = ManagerPaths.StateRoot;

    public string SetupTokenHashFile { get; set; } = ManagerPaths.SetupTokenHashFile;

    public string AgentSocket { get; set; } = ManagerPaths.AgentSocket;

    /// <summary>Development only: create a setup token at startup when the panel is unclaimed and log the setup URL.</summary>
    public bool DevGenerateSetupToken { get; set; }

    /// <summary>Cookies are marked Secure. Disable only for plain-http local development.</summary>
    public bool SecureCookies { get; set; } = true;
}
