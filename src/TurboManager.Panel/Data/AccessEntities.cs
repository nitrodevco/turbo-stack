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
