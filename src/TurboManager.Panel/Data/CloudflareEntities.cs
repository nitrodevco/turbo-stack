namespace TurboManager.Panel.Data;

/// <summary>Singleton (Id = 1): the connected Cloudflare API token and Cloudflare-wide settings.</summary>
public sealed class CloudflareAccount
{
    public int Id { get; set; } = 1;

    /// <summary>API token, protected with Data Protection. Never returned by the API.</summary>
    public required string TokenProtected { get; set; }

    public string? TokenId { get; set; }

    public string? TokenStatus { get; set; }

    public DateTimeOffset? TokenExpiresOn { get; set; }

    public DateTimeOffset VerifiedAt { get; set; }

    /// <summary>Zones the token can see (JSON list of {id, name, status, plan}).</summary>
    public string ZonesJson { get; set; } = "[]";

    /// <summary>Result of the permission probes (JSON list of checks).</summary>
    public string ChecksJson { get; set; } = "[]";

    /// <summary>Restore visitor IPs from CF-Connecting-IP for requests from Cloudflare's ranges.</summary>
    public bool RealIpEnabled { get; set; } = true;

    public DateTimeOffset? IpRangesUpdatedAt { get; set; }
}

/// <summary>Singleton (Id = 1): where the panel itself is served once it leaves https://&lt;ip&gt;:8443.</summary>
public sealed class PanelAddress
{
    public int Id { get; set; } = 1;

    public required string Domain { get; set; }

    public int? SiteId { get; set; }

    /// <summary>Random code the browser presents from the new address to prove it works end to end.</summary>
    public required string VerificationCode { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset? SetupClosedAt { get; set; }
}
