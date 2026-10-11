using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Nginx;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Tests;

public sealed class NginxTests : IDisposable
{
    private readonly FakeHostRoot _host = new();
    private readonly FakeCommandRunner _commands = new();
    private readonly NginxManager _nginx;

    public NginxTests() =>
        _nginx = new NginxManager(_commands, Options.Create(new AgentOptions { HostRoot = _host.Path }), NullLogger<NginxManager>.Instance);

    private string HostFile(string path) => Path.Combine(_host.Path, path.TrimStart('/'));

    [Fact]
    public void Policy_accepts_a_normal_site()
    {
        const string config = """
            server {
                listen 443 ssl http2;
                server_name play.example.com;
                ssl_certificate /etc/turbo-manager/certs/example.com/fullchain.pem;
                ssl_certificate_key /etc/turbo-manager/certs/example.com/privkey.pem;
                root /var/www/play;
                access_log /var/log/nginx/tm-play.access.log;
                location ^~ /.well-known/acme-challenge/ { root /var/lib/turbo-manager-acme; }
                location /api/ { proxy_pass http://127.0.0.1:8092; proxy_set_header Host "$host"; }
                add_header Content-Security-Policy "default-src 'self'; img-src * data:" always;
            }
            """;

        Assert.Empty(NginxConfigPolicy.Check(config));
    }

    [Theory]
    [InlineData("location /x { alias /etc/; }", "alias /etc/")]
    [InlineData("root /home/bill/.ssh;", "root /home/bill/.ssh")]
    [InlineData("root /var/www/../../etc;", "root /var/www/../../etc")]
    [InlineData("load_module modules/ngx_http_perl_module.so;", "load_module")]
    [InlineData("perl_modules /tmp;", "perl_modules")]
    [InlineData("include /tmp/evil.conf;", "include /tmp/evil.conf")]
    [InlineData("root $document;", "variables")]
    [InlineData("ssl_certificate_key /etc/shadow;", "ssl_certificate_key /etc/shadow")]
    [InlineData("access_log /etc/cron.d/x;", "access_log /etc/cron.d/x")]
    [InlineData("client_body_temp_path /etc;", "client_body_temp_path")]
    public void Policy_rejects_dangerous_directives(string config, string expected)
    {
        var errors = NginxConfigPolicy.Check($"server {{ {config} }}");

        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Tokenizer_ignores_comments_and_quoted_braces()
    {
        var directives = NginxConfigPolicy.Tokenize("# root /etc;\nadd_header X \"a { b; }\";\nroot /var/www/x;").ToList();

        Assert.Equal(["add_header", "root"], directives.Select(d => d.Directive));
        Assert.Equal("a { b; }", directives[0].Arguments[1]);
        Assert.Equal(3, directives[1].Line);
    }

    [Fact]
    public async Task Valid_config_is_written_enabled_and_reloaded()
    {
        var result = await _nginx.ApplyAsync(Site("play", "server { root /var/www/play; }"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("server { root /var/www/play; }", await File.ReadAllTextAsync(HostFile("/etc/nginx/sites-available/tm-play.conf")));
        Assert.True(File.Exists(HostFile("/etc/nginx/sites-enabled/tm-play.conf")));
        Assert.Contains("systemctl reload-or-restart nginx", _commands.Calls);
    }

    [Fact]
    public async Task Config_failing_nginx_test_is_rolled_back_to_the_previous_version()
    {
        await _nginx.ApplyAsync(Site("play", "server { root /var/www/v1; }"), CancellationToken.None);
        _commands.Handler = (file, _) => file == "nginx"
            ? new CommandResult(1, "", "nginx: [emerg] unknown directive \"rot\" in /etc/nginx/sites-enabled/tm-play.conf:1\nnginx: configuration file /etc/nginx/nginx.conf test failed")
            : new CommandResult(0, "", "");

        var result = await _nginx.ApplyAsync(Site("play", "server { rot /var/www/v2; }"), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("unknown directive", result.Error);
        Assert.DoesNotContain("test failed", result.Error);
        Assert.Equal("server { root /var/www/v1; }", await File.ReadAllTextAsync(HostFile("/etc/nginx/sites-available/tm-play.conf")));
    }

    [Fact]
    public async Task New_config_failing_the_test_leaves_nothing_behind()
    {
        _commands.Handler = (file, _) => new CommandResult(file == "nginx" ? 1 : 0, "", "nginx: [emerg] bad");

        var result = await _nginx.ApplyAsync(Site("new", "server { listen 80; }"), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.False(File.Exists(HostFile("/etc/nginx/sites-available/tm-new.conf")));
        Assert.False(File.Exists(HostFile("/etc/nginx/sites-enabled/tm-new.conf")));
    }

    [Fact]
    public async Task Policy_violation_is_reported_without_touching_disk()
    {
        var result = await _nginx.ApplyAsync(Site("bad", "server { alias /etc/; }"), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Empty(_commands.Calls);
        Assert.False(File.Exists(HostFile("/etc/nginx/sites-available/tm-bad.conf")));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Play")]
    [InlineData("a/b")]
    public async Task Invalid_names_are_rejected(string name)
    {
        await Assert.ThrowsAsync<RpcException>(() => _nginx.ApplyAsync(Site(name, "server {}"), CancellationToken.None));
    }

    [Fact]
    public async Task Certificate_is_installed_with_domains_and_private_key_permissions()
    {
        var (chain, key) = SelfSigned("play.example.com", "*.example.com");

        var info = await _nginx.InstallCertificateAsync(new InstallCertificateRequest { Name = "example.com", FullchainPem = chain, PrivateKeyPem = key },
            CancellationToken.None);

        Assert.Equal(["play.example.com", "*.example.com"], info.Domains);
        Assert.True(File.Exists(HostFile("/etc/turbo-manager/certs/example.com/privkey.pem")));
        Assert.Single(_nginx.ListCertificates().Certificates);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(HostFile("/etc/turbo-manager/certs/example.com/privkey.pem")));
        }
    }

    [Fact]
    public async Task Certificate_with_a_mismatched_key_is_rejected()
    {
        var (chain, _) = SelfSigned("a.example.com");
        var (_, otherKey) = SelfSigned("b.example.com");

        await Assert.ThrowsAsync<RpcException>(() => _nginx.InstallCertificateAsync(
            new InstallCertificateRequest { Name = "a.example.com", FullchainPem = chain, PrivateKeyPem = otherKey }, CancellationToken.None));
    }

    [Fact]
    public async Task Acme_challenges_are_written_to_the_webroot_and_validated()
    {
        var challenge = new AcmeChallenge { Token = "abcDEF123_-xyz", KeyAuthorization = "abcDEF123_-xyz.0123456789abcdefghijABCDEFG" };

        await _nginx.WriteAcmeChallengeAsync(challenge, CancellationToken.None);

        Assert.True(File.Exists(HostFile("/var/lib/turbo-manager-acme/.well-known/acme-challenge/abcDEF123_-xyz")));
        await Assert.ThrowsAsync<RpcException>(() => _nginx.WriteAcmeChallengeAsync(new AcmeChallenge { Token = "../../etc/passwd", KeyAuthorization = "x" }, CancellationToken.None));
    }

    [Theory]
    [InlineData("/var/www/play", true)]
    [InlineData("/srv/assets/public", true)]
    [InlineData("/var/www", false)]
    [InlineData("/etc/nginx", false)]
    [InlineData("/var/www/../etc", false)]
    public void Web_roots_are_restricted(string path, bool allowed)
    {
        if (allowed)
        {
            Assert.True(_nginx.EnsureWebRoot(new WebRootRequest { Path = path, CreatePlaceholder = true }).Created);
            Assert.True(File.Exists(Path.Combine(HostFile(path), "index.html")));
        }
        else
        {
            Assert.Throws<RpcException>(() => _nginx.EnsureWebRoot(new WebRootRequest { Path = path }));
        }
    }

    private static NginxConfig Site(string name, string content) => new() { Name = name, Kind = NginxConfigKind.Site, Content = content };

    public static (string Chain, string Key) SelfSigned(params string[] domains)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domains[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();

        foreach (var domain in domains)
        {
            san.AddDnsName(domain);
        }

        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose() => _host.Dispose();
}
