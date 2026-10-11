using System.Globalization;
using System.Text;
using TurboManager.Core;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Sites;

public sealed record CertificatePaths(string Fullchain, string PrivateKey);

/// <summary>Renders a <see cref="Site"/> into an nginx config. Pure, so it is unit tested with exact output.</summary>
public static class SiteRenderer
{
    public const string AcmeWebRoot = "/var/lib/turbo-manager-acme";

    /// <param name="certificate">Paths of a certificate covering the site; null renders HTTP only.</param>
    public static string Render(Site site, CertificatePaths? certificate)
    {
        if (site.CustomConfig is { Length: > 0 } custom)
        {
            return custom.EndsWith('\n') ? custom : custom + "\n";
        }

        var tls = site.TlsMode != TlsMode.None && certificate is not null;
        var builder = new StringBuilder();
        var names = string.Join(' ', site.Domains);

        builder.AppendLine($"# Managed by {Branding.ProductName}: site \"{site.Name}\". Edit it in the panel; manual changes are overwritten.");

        if (site.TlsMode != TlsMode.None && certificate is null)
        {
            builder.AppendLine("# HTTPS is pending: no certificate covering these domains has been issued yet.");
        }

        // Port 80: ACME challenges always; then either a redirect to HTTPS or the site itself.
        builder.AppendLine("server {");
        builder.AppendLine("    listen 80;");
        builder.AppendLine("    listen [::]:80;");
        builder.AppendLine($"    server_name {names};");
        builder.AppendLine();
        builder.AppendLine("    location ^~ /.well-known/acme-challenge/ {");
        builder.AppendLine($"        root {AcmeWebRoot};");
        builder.AppendLine("        default_type text/plain;");
        builder.AppendLine("    }");

        if (tls && site.ForceHttps)
        {
            builder.AppendLine();
            builder.AppendLine("    location / {");
            builder.AppendLine("        return 301 https://$host$request_uri;");
            builder.AppendLine("    }");
        }
        else
        {
            AppendSiteBody(builder, site, tls: false);
        }

        builder.AppendLine("}");

        if (tls)
        {
            builder.AppendLine();
            builder.AppendLine("server {");
            builder.AppendLine("    listen 443 ssl http2;");
            builder.AppendLine("    listen [::]:443 ssl http2;");
            builder.AppendLine($"    server_name {names};");
            builder.AppendLine();
            builder.AppendLine($"    ssl_certificate {certificate!.Fullchain};");
            builder.AppendLine($"    ssl_certificate_key {certificate.PrivateKey};");
            builder.AppendLine("    ssl_protocols TLSv1.2 TLSv1.3;");
            builder.AppendLine("    ssl_session_cache shared:tm_ssl:10m;");
            builder.AppendLine("    ssl_session_timeout 1d;");
            AppendSiteBody(builder, site, tls: true);
            builder.AppendLine("}");
        }

        return builder.ToString();
    }

    private static void AppendSiteBody(StringBuilder builder, Site site, bool tls)
    {
        var headers = ServerHeaders(site, tls);

        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"    client_max_body_size {site.ClientMaxBodyMb}m;");
        builder.AppendLine($"    access_log /var/log/nginx/tm-{site.Name}.access.log;");
        builder.AppendLine($"    error_log /var/log/nginx/tm-{site.Name}.error.log;");

        foreach (var header in headers)
        {
            builder.AppendLine($"    {header}");
        }

        if (!string.IsNullOrWhiteSpace(site.ServerSnippet))
        {
            builder.AppendLine();
            builder.AppendLine("    # Custom server directives");
            AppendIndented(builder, site.ServerSnippet, "    ");
        }

        foreach (var route in site.Routes.OrderBy(r => r.SortOrder).ThenByDescending(r => r.Path.Length))
        {
            builder.AppendLine();
            AppendRoute(builder, route, headers);
        }
    }

    /// <summary>
    /// Security headers set at server level. nginx drops inherited add_header directives in any
    /// location that adds its own, so locations with CORS or cache headers repeat these.
    /// </summary>
    private static List<string> ServerHeaders(Site site, bool tls)
    {
        var headers = new List<string>();

        if (site.SecurityHeaders)
        {
            headers.Add("add_header X-Content-Type-Options \"nosniff\" always;");
            headers.Add("add_header Referrer-Policy \"strict-origin-when-cross-origin\" always;");
        }

        if (tls && site.Hsts)
        {
            headers.Add("add_header Strict-Transport-Security \"max-age=31536000\" always;");
        }

        return headers;
    }

    private static void AppendRoute(StringBuilder builder, SiteRoute route, List<string> serverHeaders)
    {
        var isRoot = route.Path == "/";
        var routeHeaders = new List<string>();

        if (route.Cors)
        {
            routeHeaders.Add("add_header Access-Control-Allow-Origin \"*\" always;");
        }

        if (route.CacheSeconds is { } seconds && route.Kind is RouteKind.Static or RouteKind.Spa)
        {
            routeHeaders.Add(seconds > 0
                ? string.Create(CultureInfo.InvariantCulture, $"add_header Cache-Control \"public, max-age={seconds}\" always;")
                : "add_header Cache-Control \"no-store\" always;");
        }

        builder.AppendLine(isRoot ? "    location / {" : $"    location ^~ {route.Path} {{");

        switch (route.Kind)
        {
            case RouteKind.Static:
            case RouteKind.Spa:
                var directory = route.Target.TrimEnd('/');
                builder.AppendLine(isRoot ? $"        root {directory};" : $"        alias {directory}/;");
                builder.AppendLine("        index index.html;");
                builder.AppendLine(route.Kind == RouteKind.Spa
                    ? $"        try_files $uri $uri/ {(isRoot ? "/" : route.Path)}index.html;"
                    : "        try_files $uri $uri/ =404;");
                break;

            case RouteKind.Proxy:
            case RouteKind.WebSocket:
                builder.AppendLine($"        proxy_pass {route.Target};");
                builder.AppendLine("        proxy_http_version 1.1;");
                builder.AppendLine("        proxy_set_header Host $host;");
                builder.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
                builder.AppendLine("        proxy_set_header X-Forwarded-For $remote_addr;");
                builder.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");

                if (route.Kind == RouteKind.WebSocket)
                {
                    builder.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                    builder.AppendLine("        proxy_set_header Connection $tm_connection_upgrade;");
                    builder.AppendLine("        proxy_read_timeout 1h;");
                    builder.AppendLine("        proxy_send_timeout 1h;");
                    builder.AppendLine("        proxy_buffering off;");
                }
                else
                {
                    builder.AppendLine("        proxy_read_timeout 120s;");
                }

                break;

            case RouteKind.Redirect:
                builder.AppendLine($"        return 301 {route.Target.TrimEnd('/')}$request_uri;");
                break;
        }

        if (routeHeaders.Count > 0)
        {
            foreach (var header in serverHeaders.Concat(routeHeaders))
            {
                builder.AppendLine($"        {header}");
            }
        }

        if (!string.IsNullOrWhiteSpace(route.Snippet))
        {
            AppendIndented(builder, route.Snippet, "        ");
        }

        builder.AppendLine("    }");
    }

    private static void AppendIndented(StringBuilder builder, string text, string indent)
    {
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            builder.AppendLine(line.Length == 0 ? "" : indent + line.TrimEnd());
        }
    }
}
