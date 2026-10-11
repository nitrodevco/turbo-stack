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

    /// <summary>Seconds a risky firewall change stays applied before it reverts unless confirmed.</summary>
    public int FirewallConfirmSeconds { get; set; } = 60;

    /// <summary>Failed sign-ins are appended here for the fail2ban panel jail.</summary>
    public string AuthLogFile { get; set; } = $"{ManagerPaths.LogRoot}/auth.log";

    /// <summary>ACME directory for Let's Encrypt (staging, or Pebble in tests, can be configured).</summary>
    public string AcmeDirectory { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>Test environments only: accept the ACME server's self-signed TLS certificate (Pebble).</summary>
    public bool AcmeSkipTlsVerify { get; set; }

    /// <summary>Cookies are marked Secure. Disable only for plain-http local development.</summary>
    public bool SecureCookies { get; set; } = true;
}
