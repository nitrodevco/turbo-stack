namespace TurboManager.Panel.Data;

public enum FirewallSourceKind
{
    Any,
    Address,
    IpList,
}

/// <summary>A firewall rule the panel owns. It is applied to ufw with the comment "tm:&lt;Key&gt;".</summary>
public sealed class ManagedFirewallRule
{
    public int Id { get; set; }

    /// <summary>Stable identifier used in the ufw comment, e.g. "ssh" for installer rules or "r12".</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public string Action { get; set; } = "allow";

    public string Protocol { get; set; } = "tcp";

    public required string Ports { get; set; }

    public FirewallSourceKind SourceKind { get; set; }

    public string? SourceAddress { get; set; }

    public int? IpListId { get; set; }

    public IpList? IpList { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Created by the installer; deleting it is allowed but warned about.</summary>
    public bool System { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A named, reusable set of addresses (e.g. "Staff IPs").</summary>
public sealed class IpList
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Maintained by the panel (e.g. Cloudflare's ranges); read-only in the UI.</summary>
    public bool System { get; set; }

    public List<IpListEntry> Entries { get; set; } = [];
}

public sealed class IpListEntry
{
    public int Id { get; set; }

    public int IpListId { get; set; }

    public required string Source { get; set; }

    public string? Note { get; set; }
}

/// <summary>Singleton row (Id = 1) with the firewall's global settings.</summary>
public sealed class FirewallSettings
{
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public string DefaultIncoming { get; set; } = "deny";
}

public enum FirewallChangeStatus
{
    Pending,
    Confirmed,
    Reverted,
}

/// <summary>
/// A firewall change applied with a confirmation window. Holds the previous desired state so the
/// panel can restore its own records if the agent reverts the change.
/// </summary>
public sealed class FirewallChangeRecord
{
    public required string Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset RevertAt { get; set; }

    public FirewallChangeStatus Status { get; set; } = FirewallChangeStatus.Pending;

    public required string PreviousStateJson { get; set; }

    public required string Summary { get; set; }

    public int? UserId { get; set; }
}
