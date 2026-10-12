namespace TurboManager.Panel.Data;

public enum AlertChannelKind
{
    Discord,
    Webhook,
    Email,
}

/// <summary>Where alerts go. The target (a Discord or webhook URL, or an email address) is kept encrypted.</summary>
public sealed class AlertChannel
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public AlertChannelKind Kind { get; set; }

    public required string TargetProtected { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Only critical alerts (failures), not warnings (thresholds, pending updates).</summary>
    public bool CriticalOnly { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LastError { get; set; }
}

/// <summary>Thresholds and the mail server (one row).</summary>
public sealed class AlertSettings
{
    public int Id { get; set; } = 1;

    public int DiskPercent { get; set; } = 90;

    public int MemoryPercent { get; set; } = 95;

    public int CertificateDays { get; set; } = 14;

    public bool SecurityUpdates { get; set; } = true;

    /// <summary>Failed jobs up to here have been reported.</summary>
    public DateTimeOffset? LastJobScanAt { get; set; }

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;

    public string? SmtpUser { get; set; }

    public string? SmtpPasswordProtected { get; set; }

    public string? SmtpFrom { get; set; }
}

public enum AlertSeverity
{
    Warning,
    Critical,
}

/// <summary>
/// Something wrong, from when it was noticed until it cleared. Conditions (a full disk, a failed service) resolve
/// on their own; one-off events (a failed deploy) are resolved when they're recorded.
/// </summary>
public sealed class AlertEvent
{
    public long Id { get; set; }

    /// <summary>Identifies the condition (e.g. "disk:/", "unit:tm-app-hotel-emulator.service").</summary>
    public required string Key { get; set; }

    public AlertSeverity Severity { get; set; }

    public required string Title { get; set; }

    public string? Detail { get; set; }

    public DateTimeOffset FiredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ResolvedAt { get; set; }

    public bool OneOff { get; set; }
}
