namespace TurboManager.Panel.Data;

public enum TlsMode
{
    /// <summary>HTTP only.</summary>
    None,

    /// <summary>Let's Encrypt certificate issued by the panel (HTTP-01).</summary>
    LetsEncrypt,

    /// <summary>Cloudflare Origin CA certificate (requires a Cloudflare token).</summary>
    CloudflareOrigin,

    /// <summary>Self-signed certificate: for testing or behind a proxy that doesn't verify the origin.</summary>
    SelfSigned,
}

public enum RouteKind
{
    /// <summary>Files from a directory.</summary>
    Static,

    /// <summary>Single-page app: unknown paths fall back to index.html.</summary>
    Spa,

    /// <summary>HTTP reverse proxy to an upstream URL.</summary>
    Proxy,

    /// <summary>Reverse proxy with WebSocket upgrade and long timeouts.</summary>
    WebSocket,

    /// <summary>Permanent redirect to another URL.</summary>
    Redirect,
}

public sealed class Site
{
    public int Id { get; set; }

    /// <summary>Slug used for the nginx file (tm-&lt;name&gt;.conf) and logs.</summary>
    public required string Name { get; set; }

    /// <summary>First entry is the primary domain.</summary>
    public List<string> Domains { get; set; } = [];

    public List<SiteRoute> Routes { get; set; } = [];

    public TlsMode TlsMode { get; set; } = TlsMode.LetsEncrypt;

    public bool ForceHttps { get; set; } = true;

    public bool Hsts { get; set; }

    public bool SecurityHeaders { get; set; } = true;

    public int ClientMaxBodyMb { get; set; } = 10;

    /// <summary>Extra directives placed inside the server block.</summary>
    public string? ServerSnippet { get; set; }

    /// <summary>When set, used verbatim instead of the generated config ("ejected" site).</summary>
    public string? CustomConfig { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Keep this site's DNS records (A/AAAA to this server) in sync through Cloudflare.</summary>
    public bool CloudflareDns { get; set; }

    /// <summary>Records are proxied through Cloudflare (orange cloud) rather than DNS-only.</summary>
    public bool CloudflareProxied { get; set; } = true;

    /// <summary>Created by the panel itself (e.g. its own address); can't be deleted from the UI.</summary>
    public bool System { get; set; }

    public int? LastAppliedRevision { get; set; }

    public string? LastError { get; set; }

    /// <summary>The recipe that made this site (null for a hand-made one).</summary>
    public string? RecipeId { get; set; }

    /// <summary>The settings the recipe last rendered (SiteModel JSON): a site still equal to it wasn't edited by hand.</summary>
    public string? RecipeRendered { get; set; }

    public int? StackId { get; set; }

    public string? StackRole { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SiteRoute
{
    public int Id { get; set; }

    public int SiteId { get; set; }

    public int SortOrder { get; set; }

    /// <summary>"/" or a prefix ending in "/", e.g. "/api/".</summary>
    public required string Path { get; set; }

    public RouteKind Kind { get; set; }

    /// <summary>Directory (static/spa), upstream URL (proxy/websocket) or redirect URL.</summary>
    public required string Target { get; set; }

    /// <summary>Adds Access-Control-Allow-Origin: * (asset hosts).</summary>
    public bool Cors { get; set; }

    /// <summary>Browser cache lifetime for static files; null leaves nginx's default.</summary>
    public int? CacheSeconds { get; set; }

    /// <summary>Extra directives placed inside this location block.</summary>
    public string? Snippet { get; set; }
}

/// <summary>Every config the panel applied (or tried to), with the model that produced it.</summary>
public sealed class SiteRevision
{
    public int Id { get; set; }

    public int SiteId { get; set; }

    public int Number { get; set; }

    public required string Content { get; set; }

    public required string ModelJson { get; set; }

    public bool Applied { get; set; }

    public string? Error { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum CertificateKind
{
    LetsEncrypt,
    CloudflareOrigin,
    SelfSigned,
}

public enum CertificateStatus
{
    Pending,
    Valid,
    Failed,
}

public sealed class ManagedCertificate
{
    public int Id { get; set; }

    /// <summary>Directory name under /etc/turbo-manager/certs.</summary>
    public required string Name { get; set; }

    public List<string> Domains { get; set; } = [];

    public CertificateKind Kind { get; set; }

    public CertificateStatus Status { get; set; } = CertificateStatus.Pending;

    public DateTimeOffset? NotAfter { get; set; }

    public DateTimeOffset? IssuedAt { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public string? LastError { get; set; }

    public bool AutoRenew { get; set; } = true;
}

/// <summary>Singleton (Id = 1): the ACME account used for Let's Encrypt.</summary>
public sealed class AcmeAccount
{
    public int Id { get; set; } = 1;

    public required string DirectoryUrl { get; set; }

    public string? Email { get; set; }

    /// <summary>Account key (PEM), protected with Data Protection.</summary>
    public required string KeyProtected { get; set; }
}

/// <summary>Singleton (Id = 1): facts about this server the panel needs (e.g. for DNS checks).</summary>
public sealed class ServerSettings
{
    public int Id { get; set; } = 1;

    public string? PublicIPv4 { get; set; }

    public string? PublicIPv6 { get; set; }
}
