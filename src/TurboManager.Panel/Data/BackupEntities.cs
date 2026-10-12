namespace TurboManager.Panel.Data;

public enum BackupTargetKind
{
    Local,
    S3,
    Sftp,
}

/// <summary>A restic repository. Secrets (the repository password, an S3 secret) are stored encrypted.</summary>
public sealed class BackupTarget
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public BackupTargetKind Kind { get; set; }

    /// <summary>Local: absolute folder. S3: bucket[/prefix]. SFTP: folder on the server.</summary>
    public required string Path { get; set; }

    /// <summary>S3: https:// endpoint. SFTP: host.</summary>
    public string Endpoint { get; set; } = "";

    public int Port { get; set; }

    public string User { get; set; } = "";

    public string AccessKeyId { get; set; } = "";

    public string? SecretProtected { get; set; }

    public string Region { get; set; } = "";

    /// <summary>SFTP: the server's host key, pinned once the owner confirmed its fingerprint.</summary>
    public string? HostKey { get; set; }

    /// <summary>SFTP: this target's public key (goes into the remote authorized_keys).</summary>
    public string? PublicKey { get; set; }

    public required string PasswordProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>Null when the repository last opened fine.</summary>
    public string? LastError { get; set; }

    /// <summary>The agent's name for it (key files, ids in calls).</summary>
    public string AgentId => $"t{Id}";
}

/// <summary>What to back up, where to and when, and how many snapshots to keep.</summary>
public sealed class BackupPlan
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public int TargetId { get; set; }

    /// <summary>"engine/name", e.g. "mariadb/turbo".</summary>
    public List<string> Databases { get; set; } = [];

    /// <summary>"user:path", relative to the app user's home.</summary>
    public List<string> Folders { get; set; } = [];

    public bool PanelState { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Daily at this local time (HH:mm).</summary>
    public string TimeOfDay { get; set; } = "03:00";

    public int KeepDaily { get; set; } = 7;

    public int KeepWeekly { get; set; } = 4;

    public int KeepMonthly { get; set; } = 6;

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastResult { get; set; }

    public string? LastSnapshot { get; set; }

    public string AgentId => $"p{Id}";
}
