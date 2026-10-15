using TurboManager.Core;

namespace TurboManager.Agent;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    public string SocketPath { get; set; } = ManagerPaths.AgentSocket;

    /// <summary>Besides root, the only local user allowed to call the agent.</summary>
    public string AllowedUser { get; set; } = Branding.SystemUser;

    /// <summary>Group given access to the socket file (mode 0660).</summary>
    public string SocketGroup { get; set; } = Branding.SystemUser;

    /// <summary>Development only: platforms without SO_PEERCRED (Windows) cannot identify peers.</summary>
    public bool AllowUnverifiedPeers { get; set; }

    /// <summary>Root-only state (e.g. pending firewall changes that must survive restarts).</summary>
    public string StateDirectory { get; set; } = $"{ManagerPaths.StateRoot}-agent";

    /// <summary>Database dumps and uploaded imports (root-only).</summary>
    public string DatabaseBackupRoot { get; set; } = "/var/lib/turbo-stack-backups/databases";

    /// <summary>Backup work space (root-only): staging for runs, scratch space for restores, local repositories.</summary>
    public string BackupRoot { get; set; } = "/var/lib/turbo-stack-backups";

    /// <summary>Downloaded update bundles, the rollback copy and status.json of the last update (root-only).</summary>
    public string UpdateRoot { get; set; } = $"{ManagerPaths.StateRoot}-updates";

    /// <summary>Filesystem root used when reading /proc, /etc etc. Overridable for tests.</summary>
    public string HostRoot { get; set; } = "/";
}
