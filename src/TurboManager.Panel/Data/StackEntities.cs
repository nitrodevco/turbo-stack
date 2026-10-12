namespace TurboManager.Panel.Data;

/// <summary>
/// A hotel: apps and sites created together from a preset, each playing a role (emulator, client, ...), wired to
/// each other through their recipes' exports. The stack keeps the answers, so it can be re-rendered when they change.
/// </summary>
public sealed class Stack
{
    public int Id { get; set; }

    /// <summary>Slug; prefixes the apps' names (hotel-emulator, hotel-client, ...).</summary>
    public required string Name { get; set; }

    public required string Preset { get; set; }

    public required string SystemUser { get; set; }

    /// <summary>The hotel's domain (myhotel.com); roles get subdomains of it by default.</summary>
    public required string Domain { get; set; }

    public TlsMode TlsMode { get; set; } = TlsMode.LetsEncrypt;

    public bool CloudflareDns { get; set; }

    /// <summary>Role → domain. A role that isn't here isn't part of this hotel (only optional ones can be left out).</summary>
    public Dictionary<string, string> Hosts { get; set; } = [];

    /// <summary>The preset's settings (hotelName, ...), plus "&lt;role&gt;.&lt;export&gt;" values for roles left out.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>"&lt;role&gt;.&lt;input&gt;" → value: pinned by hand, wins over the wiring.</summary>
    public Dictionary<string, string> Overrides { get; set; } = [];

    /// <summary>Where each recipe's default repository comes from in this hotel.</summary>
    public List<StackSource> Sources { get; set; } = [];

    public string? DatabaseEngine { get; set; }

    /// <summary>Each part runs as its own Linux user (&lt;hotel&gt;-&lt;role&gt;) instead of sharing SystemUser.</summary>
    public bool Isolated { get; set; }

    /// <summary>The owner's one-time admin panel setup link, once the emulator has logged it (encrypted).</summary>
    public string? AdminSetupLinkProtected { get; set; }

    public long? InstallJobId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StackSource
{
    /// <summary>The recipes' default (nitrodevco/turbo-cloud) this replaces.</summary>
    public required string Default { get; set; }

    public required string Repository { get; set; }

    public string Branch { get; set; } = "main";

    public bool Private { get; set; }

    public List<string> PrivateSubmodules { get; set; } = [];
}
