using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Tests;

/// <summary>Records what the panel posts to webhooks (and answers 204).</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public List<(Uri Url, JsonElement Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();

        lock (Requests)
        {
            Requests.Add((request.RequestUri!, body));
        }

        return new HttpResponseMessage(request.RequestUri!.AbsolutePath.Contains("broken") ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent);
    }
}

public sealed class AlertApiTests : IAsyncLifetime
{
    private const string Webhook = "https://hooks.example.com/turbo";
    private const string Discord = "https://discord.com/api/webhooks/123/secret";

    private readonly PanelFactory _panel = new();
    private readonly RecordingHandler _sent = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _panel.ConfigureTestServices = services => services.AddHttpClient("alerts").ConfigurePrimaryHttpMessageHandler(() => _sent);
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
        (await _client.PostAsJsonAsync("/api/alerts/channels", new { name = "Ops webhook", kind = "Webhook", target = Webhook })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/alerts/channels", new { name = "Discord", kind = "Discord", target = Discord, criticalOnly = true })).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private async Task CheckAsync() => (await _client.PostAsync("/api/alerts/check", null)).EnsureSuccessStatusCode();

    private List<JsonElement> SentTo(string url) => _sent.Requests.Where(r => r.Url.ToString() == url).Select(r => r.Body).ToList();

    private async Task<JsonElement[]> OpenAsync() =>
        (await _client.GetFromJsonAsync<JsonElement>("/api/alerts")).GetProperty("open").EnumerateArray().ToArray();

    [Fact]
    public async Task A_full_disk_is_announced_once_then_resolved()
    {
        await CheckAsync();
        Assert.Empty(_sent.Requests);

        _agent.Metrics.Snapshot.Disks[0].FreeBytes = 5L << 30; // 95% used
        await CheckAsync();
        await CheckAsync(); // still full: no repeat

        var alert = Assert.Single(await OpenAsync());
        Assert.Equal("disk:/", alert.GetProperty("key").GetString());
        Assert.Equal("Disk / is 95% full", alert.GetProperty("title").GetString());
        var firing = Assert.Single(SentTo(Webhook));
        Assert.Equal("firing", firing.GetProperty("status").GetString());
        Assert.Equal("warning", firing.GetProperty("severity").GetString());
        Assert.Empty(SentTo(Discord)); // critical only

        _agent.Metrics.Snapshot.Disks[0].FreeBytes = 50L << 30;
        await CheckAsync();
        Assert.Empty(await OpenAsync());
        Assert.Equal("resolved", SentTo(Webhook)[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Failed_and_crash_looping_services_are_critical()
    {
        _agent.Services.Units.Add(new ServiceUnit { Name = "tm-app-hotel-emulator.service", ActiveState = "failed", SubState = "failed" });
        _agent.Services.Units.Add(new ServiceUnit { Name = "tm-app-hotel-imager.service", ActiveState = "activating", SubState = "auto-restart", Restarts = 7 });
        _agent.Services.Units.Add(new ServiceUnit { Name = "unrelated.service", ActiveState = "failed" });
        await CheckAsync();

        var open = await OpenAsync();
        Assert.Equal(["tm-app-hotel-emulator.service has failed", "tm-app-hotel-imager.service keeps crashing"],
            open.Select(a => a.GetProperty("title").GetString()).Order());
        var discord = SentTo(Discord);
        Assert.Equal(2, discord.Count);
        Assert.StartsWith("🔴", discord[0].GetProperty("embeds")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Failed_jobs_are_reported_once_and_old_ones_not_at_all()
    {
        using (var scope = _panel.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
            db.Jobs.Add(new Job { Kind = "deploy-app-1", Title = "Deploy old", Status = JobStatus.Failed, FinishedAt = _panel.Time.GetUtcNow().AddHours(-1) });
            await db.SaveChangesAsync();
        }

        await CheckAsync(); // the first check sets the starting point
        Assert.Empty(SentTo(Discord));

        _panel.Time.Advance(TimeSpan.FromMinutes(1));

        using (var scope = _panel.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
            db.Jobs.Add(new Job { Kind = "deploy-app-1", Title = "Deploy web", Status = JobStatus.Failed, Output = "Building…\nerror: build broke", FinishedAt = _panel.Time.GetUtcNow() });
            db.Jobs.Add(new Job { Kind = "packages", Title = "Upgrade packages", Status = JobStatus.Failed, FinishedAt = _panel.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        _panel.Time.Advance(TimeSpan.FromSeconds(1));
        await CheckAsync();
        await CheckAsync();

        var embed = Assert.Single(SentTo(Discord)).GetProperty("embeds")[0];
        Assert.Contains("Failed: Deploy web", embed.GetProperty("title").GetString());
        Assert.Contains("build broke", embed.GetProperty("description").GetString());
        Assert.Empty(await OpenAsync()); // one-off: already resolved
    }

    [Fact]
    public async Task Security_updates_and_expiring_certificates_are_warnings()
    {
        _agent.Packages.SecurityUpdates = 3;

        using (var scope = _panel.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
            db.Certificates.Add(new ManagedCertificate
            {
                Name = "play", Domains = ["play.hotel.test"], Kind = CertificateKind.LetsEncrypt, Status = CertificateStatus.Valid,
                NotAfter = _panel.Time.GetUtcNow().AddDays(5),
            });
            await db.SaveChangesAsync();
        }

        await CheckAsync();

        Assert.Equal(["3 security updates waiting", "Certificate for play.hotel.test expires in 5 days"],
            (await OpenAsync()).Select(a => a.GetProperty("title").GetString()).Order());
    }

    [Fact]
    public async Task Channels_can_be_tested_and_keep_their_secret()
    {
        var settings = await _client.GetFromJsonAsync<JsonElement>("/api/alerts/settings");
        var discord = settings.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Discord");
        Assert.Equal("https://discord.com/…", discord.GetProperty("target").GetString()); // the token in the URL isn't shown

        (await _client.PostAsync($"/api/alerts/channels/{discord.GetProperty("id").GetInt32()}/test", null)).EnsureSuccessStatusCode();
        Assert.Equal("🟠 Test alert", Assert.Single(SentTo(Discord)).GetProperty("embeds")[0].GetProperty("title").GetString());

        var broken = await (await _client.PostAsJsonAsync("/api/alerts/channels", new { name = "Broken", kind = "Webhook", target = "https://hooks.example.com/broken" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadGateway, (await _client.PostAsync($"/api/alerts/channels/{broken.GetProperty("id").GetInt32()}/test", null)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/alerts/channels", new { name = "Fake", kind = "Discord", target = "https://evil.example.com/webhook" })).StatusCode);
    }

    [Fact]
    public async Task The_mail_password_is_write_only()
    {
        (await _client.PutAsJsonAsync("/api/alerts/settings", new
        {
            diskPercent = 85, memoryPercent = 95, certificateDays = 14, securityUpdates = true,
            smtpHost = "smtp.example.com", smtpPort = 587, smtpUser = "alerts", smtpPassword = "s3cret", smtpFrom = "panel@example.com",
        })).EnsureSuccessStatusCode();

        var json = await _client.GetStringAsync("/api/alerts/settings");
        Assert.DoesNotContain("s3cret", json);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("smtpPasswordSet").GetBoolean());
        Assert.Equal(85, JsonDocument.Parse(json).RootElement.GetProperty("diskPercent").GetInt32());
    }
}
