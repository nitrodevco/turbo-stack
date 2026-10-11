namespace TurboManager.Panel.Data;

/// <summary>
/// Password of a database user the panel created, kept (encrypted) so apps can be configured with
/// it later and an admin can look it up. Users created outside the panel have no entry.
/// </summary>
public sealed class DatabaseCredential
{
    public int Id { get; set; }

    /// <summary>mysql | mariadb | postgres</summary>
    public required string Engine { get; set; }

    public required string Username { get; set; }

    public required string PasswordProtected { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Daily automatic backup of one database, keeping the newest <see cref="RetentionCount"/> dumps.</summary>
public sealed class BackupSchedule
{
    public int Id { get; set; }

    public required string Engine { get; set; }

    public required string Database { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>"HH:mm" in the server's local time.</summary>
    public string TimeOfDay { get; set; } = "03:00";

    public int RetentionCount { get; set; } = 7;

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastResult { get; set; }
}
