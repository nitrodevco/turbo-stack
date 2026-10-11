using TurboManager.Panel.Data;

namespace TurboManager.Panel.Sites;

public sealed record SiteRouteModel(string Path, RouteKind Kind, string Target, bool Cors = false, int? CacheSeconds = null, string? Snippet = null);

/// <summary>A site's editable settings: the API request body and the snapshot stored with each revision.</summary>
public sealed record SiteModel(
    string Name,
    List<string> Domains,
    List<SiteRouteModel> Routes,
    TlsMode TlsMode = TlsMode.LetsEncrypt,
    bool ForceHttps = true,
    bool Hsts = false,
    bool SecurityHeaders = true,
    int ClientMaxBodyMb = 10,
    string? ServerSnippet = null,
    string? CustomConfig = null,
    bool Enabled = true,
    bool CloudflareDns = false,
    bool CloudflareProxied = true)
{
    public static SiteModel From(Site site) => new(
        site.Name,
        [.. site.Domains],
        site.Routes.OrderBy(r => r.SortOrder).Select(r => new SiteRouteModel(r.Path, r.Kind, r.Target, r.Cors, r.CacheSeconds, r.Snippet)).ToList(),
        site.TlsMode,
        site.ForceHttps,
        site.Hsts,
        site.SecurityHeaders,
        site.ClientMaxBodyMb,
        site.ServerSnippet,
        site.CustomConfig,
        site.Enabled,
        site.CloudflareDns,
        site.CloudflareProxied);

    /// <summary>Copies the model onto an entity (routes are replaced), normalising input.</summary>
    public void ApplyTo(Site site)
    {
        site.Name = Name.Trim().ToLowerInvariant();
        site.Domains = (Domains ?? []).Select(d => d.Trim().TrimEnd('.').ToLowerInvariant()).Where(d => d.Length > 0).ToList();
        site.TlsMode = TlsMode;
        site.ForceHttps = ForceHttps;
        site.Hsts = Hsts;
        site.SecurityHeaders = SecurityHeaders;
        site.ClientMaxBodyMb = ClientMaxBodyMb;
        site.ServerSnippet = string.IsNullOrWhiteSpace(ServerSnippet) ? null : ServerSnippet.Trim();
        site.CustomConfig = string.IsNullOrWhiteSpace(CustomConfig) ? null : CustomConfig;
        site.Enabled = Enabled;
        site.CloudflareDns = CloudflareDns;
        site.CloudflareProxied = CloudflareProxied;
        site.Routes.Clear();
        site.Routes.AddRange((Routes ?? []).Select((r, i) => new SiteRoute
        {
            SortOrder = i,
            Path = NormalizePath(r.Path),
            Kind = r.Kind,
            Target = r.Target.Trim(),
            Cors = r.Cors,
            CacheSeconds = r.CacheSeconds,
            Snippet = string.IsNullOrWhiteSpace(r.Snippet) ? null : r.Snippet.Trim(),
        }));
        site.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>"api" → "/api/", "/" stays "/".</summary>
    public static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('/');

        return trimmed.Length == 0 ? "/" : $"/{trimmed}/";
    }
}
