using TurboManager.Panel.Data;
using TurboManager.Panel.Sites;

namespace TurboManager.Panel.Tests;

public sealed class SiteRenderingTests
{
    private static Site WebSite(TlsMode tls = TlsMode.LetsEncrypt) => new()
    {
        Name = "web",
        Domains = ["myhotel.com", "www.myhotel.com"],
        TlsMode = tls,
        Hsts = true,
        Routes =
        [
            new() { SortOrder = 0, Path = "/", Kind = RouteKind.Spa, Target = "/var/www/web/current/dist" },
            new() { SortOrder = 1, Path = "/api/", Kind = RouteKind.Proxy, Target = "http://127.0.0.1:8092" },
        ],
    };

    private static readonly CertificatePaths Certificate = new("/etc/turbo-manager/certs/myhotel.com/fullchain.pem", "/etc/turbo-manager/certs/myhotel.com/privkey.pem");

    [Fact]
    public void Https_site_redirects_port_80_and_serves_routes_on_443()
    {
        var config = SiteRenderer.Render(WebSite(), Certificate);

        Assert.Contains("server_name myhotel.com www.myhotel.com;", config);
        Assert.Contains("location ^~ /.well-known/acme-challenge/ {\n        root /var/lib/turbo-manager-acme;", config.Replace("\r", ""));
        Assert.Contains("return 301 https://$host$request_uri;", config);
        Assert.Contains("listen 443 ssl;", config);
        Assert.Contains("ssl_certificate /etc/turbo-manager/certs/myhotel.com/fullchain.pem;", config);
        Assert.Contains("try_files $uri $uri/ /index.html;", config);
        Assert.Contains("location ^~ /api/ {", config);
        Assert.Contains("proxy_pass http://127.0.0.1:8092;", config);
        Assert.Contains("Strict-Transport-Security", config);
    }

    [Fact]
    public void Missing_certificate_renders_http_only_with_a_note()
    {
        var config = SiteRenderer.Render(WebSite(), certificate: null);

        Assert.Contains("HTTPS is pending", config);
        Assert.DoesNotContain("listen 443", config);
        Assert.DoesNotContain("return 301 https", config);
        Assert.Contains("proxy_pass http://127.0.0.1:8092;", config);
        Assert.DoesNotContain("Strict-Transport-Security", config);
    }

    [Fact]
    public void Websocket_routes_upgrade_and_keep_connections_open()
    {
        var site = new Site
        {
            Name = "ws",
            Domains = ["ws.myhotel.com"],
            TlsMode = TlsMode.None,
            Routes = [new() { Path = "/", Kind = RouteKind.WebSocket, Target = "http://127.0.0.1:9001" }],
        };

        var config = SiteRenderer.Render(site, null);

        Assert.Contains("proxy_set_header Upgrade $http_upgrade;", config);
        Assert.Contains("proxy_set_header Connection $tm_connection_upgrade;", config);
        Assert.Contains("proxy_read_timeout 1h;", config);
        Assert.Contains("proxy_buffering off;", config);
    }

    [Fact]
    public void Locations_with_their_own_headers_repeat_the_security_headers()
    {
        var site = new Site
        {
            Name = "assets",
            Domains = ["assets.myhotel.com"],
            TlsMode = TlsMode.None,
            Routes = [new() { Path = "/", Kind = RouteKind.Static, Target = "/var/www/assets", Cors = true, CacheSeconds = 86400 }],
        };

        var config = SiteRenderer.Render(site, null).Replace("\r", "");
        var location = config[config.IndexOf("location / {", StringComparison.Ordinal)..];

        Assert.Contains("add_header Access-Control-Allow-Origin \"*\" always;", location);
        Assert.Contains("max-age=86400", location);
        Assert.Contains("X-Content-Type-Options", location);
    }

    [Fact]
    public void Prefix_static_routes_use_alias_and_spa_fallback_stays_inside_the_prefix()
    {
        var site = new Site
        {
            Name = "x",
            Domains = ["x.example.com"],
            TlsMode = TlsMode.None,
            Routes =
            [
                new() { Path = "/", Kind = RouteKind.Redirect, Target = "https://example.com/" },
                new() { Path = "/play/", Kind = RouteKind.Spa, Target = "/var/www/play/" },
            ],
        };

        var config = SiteRenderer.Render(site, null);

        Assert.Contains("alias /var/www/play/;", config);
        Assert.Contains("try_files $uri $uri/ /play/index.html;", config);
        Assert.Contains("return 301 https://example.com$request_uri;", config);
    }

    [Fact]
    public void Custom_config_is_used_verbatim()
    {
        var site = WebSite();
        site.CustomConfig = "server { listen 80; }";

        Assert.Equal("server { listen 80; }\n", SiteRenderer.Render(site, Certificate));
    }

    [Theory]
    [InlineData("myhotel.com", true)]
    [InlineData("Play.MyHotel.com", false)]
    [InlineData("*.myhotel.com", false)]
    [InlineData("localhost", false)]
    [InlineData("my_hotel.com", false)]
    public void Domain_validation(string domain, bool valid)
    {
        var site = WebSite();
        site.Domains = [domain];

        Assert.Equal(valid, SiteValidator.Validate(site, []) is null);
    }

    [Theory]
    [InlineData(RouteKind.Spa, "/var/www/web", true)]
    [InlineData(RouteKind.Spa, "/etc/nginx", false)]
    [InlineData(RouteKind.Static, "/home/hotel/.ssh", false)]
    [InlineData(RouteKind.Static, "/var/www/../etc", false)]
    [InlineData(RouteKind.Proxy, "http://127.0.0.1:8092", true)]
    [InlineData(RouteKind.Proxy, "http://127.0.0.1:8092; include /etc/passwd", false)]
    [InlineData(RouteKind.Proxy, "unix:/run/x.sock", false)]
    [InlineData(RouteKind.Redirect, "https://example.com", true)]
    public void Route_target_validation(RouteKind kind, string target, bool valid) =>
        Assert.Equal(valid, SiteValidator.ValidateRoute(new SiteRoute { Path = "/", Kind = kind, Target = target }) is null);

    [Fact]
    public void Domains_used_by_other_sites_and_reserved_names_are_rejected()
    {
        Assert.Contains("already used", SiteValidator.Validate(WebSite(), ["www.myhotel.com"]));

        var site = WebSite();
        site.Name = "setup";
        Assert.Contains("reserved", SiteValidator.Validate(site, []));
    }

    [Theory]
    [InlineData("api", "/api/")]
    [InlineData("/api", "/api/")]
    [InlineData("/", "/")]
    [InlineData(" ", "/")]
    public void Paths_are_normalised(string input, string expected) => Assert.Equal(expected, SiteModel.NormalizePath(input));

    [Theory]
    [InlineData("play.myhotel.com", true)]
    [InlineData("myhotel.com", false)]
    [InlineData("a.play.myhotel.com", false)]
    public void Wildcard_certificates_cover_one_level(string domain, bool covered) =>
        Assert.Equal(covered, CertificateService.Covers(["*.myhotel.com"], domain));

    [Fact]
    public void Slug_is_derived_from_the_domain() => Assert.Equal("play-myhotel-com", SiteValidator.Slugify("play.myhotel.com"));
}
