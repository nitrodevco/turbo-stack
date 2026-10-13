namespace TurboManager.Panel.Data;

/// <summary>A signed-in browser. The session cookie names it; deleting the row signs that browser out.</summary>
public sealed class PanelSession
{
    /// <summary>Random, unguessable (the cookie also carries the user's security stamp).</summary>
    public required string Id { get; set; }

    public int UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Re-authenticated (second factor again) until then: dangerous actions are allowed.</summary>
    public DateTimeOffset? SudoUntil { get; set; }
}

/// <summary>A token for scripts and CI: a subset of its user's permissions, until it expires or is deleted.</summary>
public sealed class ApiToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>The token's first characters, to recognise it in lists ("tm_ab12…").</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the whole token; the token itself is shown once and never stored.</summary>
    public required string Hash { get; set; }

    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>A WebAuthn credential (passkey): signs in without a password, and counts as two factors.</summary>
public sealed class Passkey
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>base64url of the credential id.</summary>
    public required string CredentialId { get; set; }

    public required byte[] PublicKey { get; set; }

    public uint SignCount { get; set; }

    /// <summary>The domain it was made for (a passkey only works there).</summary>
    public required string RpId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }
}
