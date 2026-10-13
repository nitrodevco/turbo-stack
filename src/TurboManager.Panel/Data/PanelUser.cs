namespace TurboManager.Panel.Data;

public enum PanelRole
{
    Owner,
    Admin,
    Developer,
    Operator,
    Viewer,

    /// <summary>Permissions from a <see cref="CustomRole"/> (<see cref="PanelUser.CustomRoleId"/>).</summary>
    Custom,
}

/// <summary>A role made from the permission matrix (never the root terminal or team management: those stay with owners and admins).</summary>
public sealed class CustomRole
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = "";

    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PanelUser
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public PanelRole Role { get; set; }

    /// <summary>Set when <see cref="Role"/> is <see cref="PanelRole.Custom"/>.</summary>
    public int? CustomRoleId { get; set; }

    /// <summary>TOTP secret protected with ASP.NET Data Protection.</summary>
    public string? TotpSecretProtected { get; set; }

    public bool TotpEnabled { get; set; }

    /// <summary>Last accepted TOTP time step; codes at or before it are rejected (replay protection).</summary>
    public long LastTotpStep { get; set; }

    /// <summary>Rotated whenever credentials change; sessions carrying an old stamp are rejected.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }

    public List<RecoveryCode> RecoveryCodes { get; set; } = [];

    /// <summary>SHA-256 of a pending invite (or reset) link; cleared once the user has set up 2FA.</summary>
    public string? InviteHash { get; set; }

    public DateTimeOffset? InviteExpiresAt { get; set; }

    public string? InvitedBy { get; set; }

    /// <summary>
    /// Limits a user to these hotels and apps (both empty = everything their role allows). Owners and admins
    /// are never limited.
    /// </summary>
    public List<int> ScopeStacks { get; set; } = [];

    public List<int> ScopeApps { get; set; } = [];

    /// <summary>The WebAuthn user handle (random, base64url): passkeys identify the user by it, never by username.</summary>
    public string? PasskeyHandle { get; set; }
}

public sealed class RecoveryCode
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>SHA-256 hex of the normalized code (codes are high-entropy, so a fast hash is fine).</summary>
    public required string CodeHash { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public int? UserId { get; set; }

    public string? Username { get; set; }

    public required string Action { get; set; }

    public string? Target { get; set; }

    public string? IpAddress { get; set; }

    public string? Details { get; set; }
}
