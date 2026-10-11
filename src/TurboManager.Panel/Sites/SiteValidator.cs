using System.Text.RegularExpressions;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Sites;

public static partial class SiteValidator
{
    /// <summary>Config names the panel or installer use for their own files.</summary>
    public static readonly HashSet<string> ReservedNames = ["setup", "maps", "cloudflare-realip", "default", "panel"];

    private static readonly string[] WebRoots = ["/var/www/", "/srv/", "/home/", "/opt/turbo-manager/"];

    public static string? Validate(Site site, IEnumerable<string> domainsUsedElsewhere, bool allowReservedName = false)
    {
        if (!NamePattern().IsMatch(site.Name))
        {
            return "Name must be lowercase letters, digits and dashes (up to 40 characters).";
        }

        if (!allowReservedName && ReservedNames.Contains(site.Name))
        {
            return $"\"{site.Name}\" is reserved; pick another name.";
        }

        if (site.Domains.Count is 0 or > 20)
        {
            return "Add between 1 and 20 domains.";
        }

        if (site.Domains.FirstOrDefault(d => !DomainPattern().IsMatch(d)) is { } badDomain)
        {
            return $"\"{badDomain}\" is not a valid domain name.";
        }

        if (site.Domains.Distinct().Count() != site.Domains.Count)
        {
            return "Each domain can only be listed once.";
        }

        var taken = domainsUsedElsewhere.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (site.Domains.FirstOrDefault(taken.Contains) is { } clash)
        {
            return $"{clash} is already used by another site.";
        }

        if (site.ClientMaxBodyMb is < 1 or > 2048)
        {
            return "Upload limit must be between 1 and 2048 MB.";
        }

        if (site.CustomConfig is { Length: > 0 })
        {
            return null;
        }

        if (site.Routes.Count == 0)
        {
            return "Add at least one route.";
        }

        if (site.Routes.Select(r => r.Path).Distinct().Count() != site.Routes.Count)
        {
            return "Each path can only have one route.";
        }

        foreach (var route in site.Routes)
        {
            if (ValidateRoute(route) is { } error)
            {
                return $"Route {route.Path}: {error}";
            }
        }

        return null;
    }

    public static string? ValidateRoute(SiteRoute route)
    {
        if (route.Path != "/" && !PathPattern().IsMatch(route.Path))
        {
            return "Paths must start and end with \"/\" (e.g. /api/) and use only letters, digits, '-', '_' and '.'.";
        }

        switch (route.Kind)
        {
            case RouteKind.Static or RouteKind.Spa:
                var directory = route.Target.TrimEnd('/');

                if (!DirectoryPattern().IsMatch(directory) || directory.Contains("..", StringComparison.Ordinal)
                    || directory.Contains("/.", StringComparison.Ordinal) || !WebRoots.Any(r => directory.StartsWith(r, StringComparison.Ordinal) && directory.Length > r.Length))
                {
                    return $"Directory must be an absolute path under {string.Join(", ", WebRoots)}.";
                }

                break;

            case RouteKind.Proxy or RouteKind.WebSocket:
                if (!IsHttpUrl(route.Target))
                {
                    return "Upstream must be an http:// or https:// URL, e.g. http://127.0.0.1:8092.";
                }

                break;

            case RouteKind.Redirect:
                if (!IsHttpUrl(route.Target))
                {
                    return "Redirect target must be an http:// or https:// URL.";
                }

                break;
        }

        if (route.CacheSeconds is < 0 or > 31_536_000)
        {
            return "Cache time must be between 0 and 1 year.";
        }

        return null;
    }

    /// <summary>"play.myhotel.com" → "play-myhotel-com" (max 40 characters).</summary>
    public static string Slugify(string domain)
    {
        var slug = SlugCleanup().Replace(domain.ToLowerInvariant().Replace('.', '-'), "");

        return slug[..Math.Min(40, slug.Length)].Trim('-');
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && UrlCharacters().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]*/$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^/[A-Za-z0-9/_.-]{1,200}$")]
    private static partial Regex DirectoryPattern();

    /// <summary>No whitespace, quotes or nginx syntax characters inside URLs.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9:/._~%?&=+\-\[\]@!,]+$")]
    private static partial Regex UrlCharacters();

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex SlugCleanup();
}
