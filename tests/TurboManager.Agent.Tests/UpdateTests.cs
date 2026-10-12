using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TurboManager.Agent;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Update;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Tests;

public sealed class UpdateTests : IDisposable
{
    private const string BaseUrl = "https://get.example.test";

    private readonly FakeHostRoot _host = new();
    private readonly FakeCommandRunner _commands = new();
    private readonly FakeHttp _http = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly UpdateManager _updates;

    public UpdateTests()
    {
        _host.WithFile("etc/turbo-manager/install.env", $"# Written by install.sh\nTM_VERSION=0.1.0\nTM_CHANNEL=stable\nTM_BASE_URL={BaseUrl}\n")
            .WithFile("etc/turbo-manager/release-key.pem", _key.ExportSubjectPublicKeyInfoPem())
            .WithFile("opt/turbo-manager/VERSION", "0.1.0\n");
        // Nothing is updating right now.
        _commands.Handler = (file, args) => new CommandResult(file == "systemctl" && args.Contains("is-active") ? 3 : 0, "", "");
        _updates = new UpdateManager(_commands, _http, Options.Create(new AgentOptions { HostRoot = _host.Path, UpdateRoot = "/var/lib/tm-updates" }),
            NullLogger<UpdateManager>.Instance);
    }

    public void Dispose()
    {
        _key.Dispose();
        _host.Dispose();
    }

    [Theory]
    [InlineData("0.1.1", "0.1.0", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0-alpha.9", 1)]
    [InlineData("0.1.0", "0.1.0", 0)]
    [InlineData("0.1.0", "0.2.0", -1)]
    public void Versions_compare_like_semver(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(ReleaseVersion.Compare(a, b)));

    [Fact]
    public void Only_the_exact_bytes_signed_with_the_release_key_verify()
    {
        var data = Encoding.UTF8.GetBytes("{\"version\":\"0.1.1\"}\n");
        var signature = Sign(data);
        var pem = _key.ExportSubjectPublicKeyInfoPem();

        Assert.True(UpdateManager.VerifySignature(data, signature, pem));
        Assert.False(UpdateManager.VerifySignature(Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\"}\n"), signature, pem));
        Assert.False(UpdateManager.VerifySignature(data, signature, ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfoPem()));
        Assert.False(UpdateManager.VerifySignature(data, "not base64!", pem));
    }

    [Fact]
    public void Setting_a_value_keeps_the_rest_of_install_env()
    {
        var content = "# comment\nTM_VERSION=0.1.0\nTM_CHANNEL=stable\n";

        Assert.Equal("# comment\nTM_VERSION=0.1.0\nTM_CHANNEL=beta\n", UpdateManager.SetEnvValue(content, "TM_CHANNEL", "beta"));
        Assert.Equal("# comment\nTM_VERSION=0.1.0\nTM_CHANNEL=stable\nTM_NEW=1\n", UpdateManager.SetEnvValue(content, "TM_NEW", "1"));
    }

    [Fact]
    public async Task A_signed_newer_release_is_offered()
    {
        Publish("0.1.1", Bundle("0.1.1"), notes: "Faster deploys.");

        var info = await _updates.CheckAsync(CancellationToken.None);

        Assert.Equal("", info.Error);
        Assert.Equal("0.1.0", info.CurrentVersion);
        Assert.Equal("0.1.1", info.LatestVersion);
        Assert.Equal("Faster deploys.", info.Notes);
        Assert.True(info.Available);
    }

    [Fact]
    public async Task A_tampered_channel_file_is_refused()
    {
        Publish("0.1.1", Bundle("0.1.1"));
        _http.Files[$"{BaseUrl}/channels/stable.json"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(_http.Files[$"{BaseUrl}/channels/stable.json"]).Replace("0.1.1", "0.1.2"));

        var info = await _updates.CheckAsync(CancellationToken.None);

        Assert.Contains("signature doesn't match the release key", info.Error);
        Assert.False(info.Available);
        var error = await Assert.ThrowsAsync<RpcException>(() => _updates.StartAsync(new UpdateRequest { Version = "0.1.2" }, CancellationToken.None));
        Assert.Equal(StatusCode.Aborted, error.StatusCode);
        Assert.DoesNotContain(_commands.Calls, c => c.StartsWith("systemd-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bundle_that_does_not_match_the_signed_checksum_is_refused()
    {
        Publish("0.1.1", Bundle("0.1.1"));
        _http.Files[$"{BaseUrl}/files/tm-0.1.1-linux-x64.tar.gz"] = Bundle("0.1.1", extra: "backdoor");

        var error = await Assert.ThrowsAsync<RpcException>(() => _updates.StartAsync(new UpdateRequest { Version = "0.1.1" }, CancellationToken.None));

        Assert.Contains("doesn't match the signed checksum", error.Status.Detail);
        Assert.DoesNotContain(_commands.Calls, c => c.StartsWith("systemd-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_channels_latest_and_only_upwards()
    {
        Publish("0.1.1", Bundle("0.1.1"));

        Assert.Contains("now offers 0.1.1", (await Assert.ThrowsAsync<RpcException>(() =>
            _updates.StartAsync(new UpdateRequest { Version = "0.1.5" }, CancellationToken.None))).Status.Detail);

        _host.WithFile("opt/turbo-manager/VERSION", "0.1.1\n");
        Assert.Contains("is not newer", (await Assert.ThrowsAsync<RpcException>(() =>
            _updates.StartAsync(new UpdateRequest { Version = "0.1.1" }, CancellationToken.None))).Status.Detail);
    }

    [Fact]
    public async Task A_verified_bundle_is_unpacked_and_handed_to_its_own_tm_update_in_a_transient_unit()
    {
        Publish("0.1.1", Bundle("0.1.1"));

        await _updates.StartAsync(new UpdateRequest { Version = "0.1.1" }, CancellationToken.None);

        var bundle = Path.Combine(_host.Path, "var/lib/tm-updates/0.1.1/bundle");
        Assert.Equal("0.1.1", File.ReadAllText(Path.Combine(bundle, "VERSION")).Trim());
        Assert.Contains("systemd-run --unit tm-update --collect --no-block --quiet --description Turbo Manager update to 0.1.1 "
            + "--property TimeoutStopSec=15min /bin/bash /var/lib/tm-updates/0.1.1/bundle/bin/tm-update apply /var/lib/tm-updates/0.1.1/bundle",
            _commands.Calls);
    }

    [Fact]
    public async Task One_update_at_a_time()
    {
        Publish("0.1.1", Bundle("0.1.1"));
        _commands.Handler = (_, _) => new CommandResult(0, "", "");

        var error = await Assert.ThrowsAsync<RpcException>(() => _updates.StartAsync(new UpdateRequest { Version = "0.1.1" }, CancellationToken.None));

        Assert.Contains("already running", error.Status.Detail);
    }

    [Fact]
    public async Task The_last_outcome_is_read_from_tm_updates_status_file()
    {
        _host.WithFile("var/lib/tm-updates/status.json",
            "{\"state\":\"rolled-back\",\"from\":\"0.1.0\",\"to\":\"0.1.1\",\"message\":\"0.1.1 didn't start\",\"at\":\"2026-10-12T15:00:00Z\"}");

        var status = await _updates.StatusAsync(CancellationToken.None);

        Assert.Equal("rolled-back", status.State);
        Assert.Equal("0.1.1", status.ToVersion);
        Assert.False(status.Running);
    }

    [Fact]
    public async Task Channels_are_stable_or_beta()
    {
        await Assert.ThrowsAsync<RpcException>(() => _updates.SetChannelAsync("nightly", CancellationToken.None));
        await _updates.SetChannelAsync("beta", CancellationToken.None);

        Assert.Contains("TM_CHANNEL=beta\n", File.ReadAllText(Path.Combine(_host.Path, "etc/turbo-manager/install.env")));
    }

    [Fact]
    public void A_failed_command_reaches_the_panel_as_a_readable_error_without_its_arguments()
    {
        var message = CommandErrorInterceptor.Describe(new CommandFailedException("useradd --comment secret-ish assets",
            new CommandResult(3, "", "useradd: invalid comment\n")));

        Assert.Equal("useradd failed (exit code 3): useradd: invalid comment", message);
    }

    private void Publish(string version, byte[] bundle, string notes = "")
    {
        var file = $"files/tm-{version}-linux-x64.tar.gz";
        var arm = $"files/tm-{version}-linux-arm64.tar.gz";
        var sha = Convert.ToHexStringLower(SHA256.HashData(bundle));
        var json = Encoding.UTF8.GetBytes(
            $"{{\"version\":\"{version}\",\"channel\":\"stable\",\"published\":\"2026-10-12T12:00:00Z\",\"notes\":\"{notes}\","
            + $"\"artifacts\":{{\"x64\":{{\"url\":\"{file}\",\"sha256\":\"{sha}\"}},\"arm64\":{{\"url\":\"{arm}\",\"sha256\":\"{sha}\"}}}}}}\n");

        _http.Files[$"{BaseUrl}/channels/stable.json"] = json;
        _http.Files[$"{BaseUrl}/channels/stable.json.sig"] = Encoding.UTF8.GetBytes(Sign(json) + "\n");
        _http.Files[$"{BaseUrl}/{file}"] = bundle;
        _http.Files[$"{BaseUrl}/{arm}"] = bundle;
    }

    private string Sign(byte[] data) =>
        Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    private static byte[] Bundle(string version, string extra = "")
    {
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            void Add(string name, string content)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }

            Add("VERSION", version + "\n");
            Add("bin/tm-update", "#!/usr/bin/env bash\n" + extra);
            Add("panel/tm-panel", "panel");
            Add("agent/tm-agent", "agent");
        }

        return output.ToArray();
    }

    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
