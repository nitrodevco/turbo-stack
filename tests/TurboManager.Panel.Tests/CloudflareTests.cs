using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TurboManager.FakeCloudflare;
using TurboManager.Panel.Cloudflare;

namespace TurboManager.Panel.Tests;

public sealed class CloudflareDnsPlannerTests
{
    private static readonly Dictionary<string, string> Here = new() { ["A"] = "203.0.113.10" };

    private static CloudflareDnsRecord Record(string type, string content, bool proxied = true, string name = "play.hotel.test") =>
        new($"id-{type}-{content}", type, name, content, proxied);

    [Fact]
    public void Missing_record_is_created()
    {
        var plan = CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, [], replaceConflicts: false);

        Assert.Equal(DnsActionKind.Create, Assert.Single(plan).Kind);
    }

    [Fact]
    public void Correct_record_is_left_alone_and_proxy_mismatch_is_updated()
    {
        Assert.Equal(DnsActionKind.Ok, Assert.Single(CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, [Record("A", "203.0.113.10")], false)).Kind);
        Assert.Equal(DnsActionKind.Update, Assert.Single(CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, [Record("A", "203.0.113.10", proxied: false)], false)).Kind);
    }

    [Fact]
    public void Record_pointing_elsewhere_is_a_conflict_unless_replacing()
    {
        var existing = new[] { Record("A", "198.51.100.1") };

        Assert.Equal(DnsActionKind.Conflict, Assert.Single(CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, existing, false)).Kind);
        Assert.Equal(DnsActionKind.Update, Assert.Single(CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, existing, true)).Kind);
    }

    [Fact]
    public void Cname_is_deleted_only_when_replacing()
    {
        var existing = new[] { Record("CNAME", "hotel.netlify.app") };

        Assert.Equal(DnsActionKind.Conflict, Assert.Single(CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, existing, false)).Kind);
        Assert.Equal([DnsActionKind.Delete, DnsActionKind.Create], CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, existing, true).Select(a => a.Kind));
    }

    [Fact]
    public void Extra_round_robin_records_are_flagged()
    {
        var existing = new[] { Record("A", "203.0.113.10"), Record("A", "198.51.100.1") };

        var plan = CloudflareDnsPlanner.Plan("play.hotel.test", Here, true, existing, false);

        Assert.Equal([DnsActionKind.Ok, DnsActionKind.Conflict], plan.Select(a => a.Kind));
    }

    [Theory]
    [InlineData("play.hotel.test", "hotel.test")]
    [InlineData("hotel.test", "hotel.test")]
    [InlineData("a.b.sub.hotel.test", "sub.hotel.test")]
    [InlineData("nothotel.test", null)]
    public void Zone_is_the_longest_matching_suffix(string domain, string? zone) =>
        Assert.Equal(zone, CloudflareDnsPlanner.ZoneFor(new[] { "hotel.test", "sub.hotel.test" }, z => z, domain));

    [Fact]
    public void Real_ip_config_trusts_only_the_given_ranges()
    {
        var config = CloudflareService.RenderRealIpConfig(["173.245.48.0/20", "2400:cb00::/32"]);

        Assert.Contains("set_real_ip_from 173.245.48.0/20;", config);
        Assert.Contains("set_real_ip_from 2400:cb00::/32;", config);
        Assert.Contains("real_ip_header CF-Connecting-IP;", config);
    }
}

/// <summary>The panel against a fake agent and a fake Cloudflare API.</summary>
public sealed class CloudflareApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private WebApplication _cloudflare = null!;
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;
    private HttpClient _fake = null!;

    public async Task InitializeAsync()
    {
        _cloudflare = FakeCloudflareServer.Create([], "http://127.0.0.1:0");
        await _cloudflare.StartAsync();
        var address = _cloudflare.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _fake = new HttpClient { BaseAddress = new Uri(address) };

        _panel.Settings["Panel:CloudflareApiBase"] = $"{address}/client/v4";
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
        await _client.PutAsJsonAsync("/api/settings/server", new { publicIPv4 = "203.0.113.10" });
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
        await _cloudflare.DisposeAsync();
    }

    private async Task<JsonElement> ConnectAsync(string token = "test-token")
    {
        var response = await _client.PutAsJsonAsync("/api/cloudflare/token", new { token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> FakeStateAsync() => _fake.GetFromJsonAsync<JsonElement>("/__fake/state");

    private async Task<JsonElement> CreateSiteAsync(object model)
    {
        var response = await _client.PostAsJsonAsync("/api/sites", model);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task WaitForJobAsync(long jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");

            if (job.GetProperty("status").GetString() != "running")
            {
                Assert.Equal("succeeded", job.GetProperty("status").GetString());

                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Job did not finish");
    }

    [Fact]
    public async Task Connecting_probes_permissions_lists_zones_and_installs_real_ip_config()
    {
        var overview = await ConnectAsync();

        Assert.True(overview.GetProperty("connected").GetBoolean());
        Assert.Equal(2, overview.GetProperty("zones").GetArrayLength());
        Assert.All(overview.GetProperty("checks").EnumerateArray(), c => Assert.Equal("ok", c.GetProperty("status").GetString()));
        Assert.Contains("real_ip_header CF-Connecting-IP;", _agent.Nginx.Sites["cloudflare-realip"]);

        // The DNS permission probe cleaned up after itself.
        Assert.Equal(0, (await FakeStateAsync()).GetProperty("records").GetArrayLength());

        var lists = await _client.GetFromJsonAsync<JsonElement>("/api/firewall/ip-lists");
        Assert.Contains(lists.EnumerateArray(), l => l.GetProperty("name").GetString() == "Cloudflare" && l.GetProperty("entries").GetArrayLength() == 4);
    }

    [Fact]
    public async Task Limited_token_reports_missing_permissions()
    {
        var overview = await ConnectAsync("dns-only-token");
        var checks = overview.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("status").GetString());

        Assert.Equal("ok", checks["dns.edit"]);
        Assert.Equal("missing", checks["settings.edit"]);
        Assert.Equal("missing", checks["cache.purge"]);
        Assert.Equal("missing", checks["ssl.edit"]);
    }

    [Fact]
    public async Task Invalid_token_is_rejected()
    {
        var response = await _client.PutAsJsonAsync("/api/cloudflare/token", new { token = "nope" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await _client.GetFromJsonAsync<JsonElement>("/api/cloudflare")).GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Site_with_cloudflare_dns_gets_proxied_records()
    {
        await ConnectAsync();

        var site = await CreateSiteAsync(new
        {
            name = "play",
            domains = new[] { "play.hotel.test" },
            tlsMode = "None",
            cloudflareDns = true,
            routes = new[] { new { path = "/", kind = "Spa", target = "/var/www/play" } },
        });

        Assert.Equal(JsonValueKind.Null, site.GetProperty("dnsError").ValueKind);
        var record = Assert.Single((await FakeStateAsync()).GetProperty("records").EnumerateArray());
        Assert.Equal("A", record.GetProperty("type").GetString());
        Assert.Equal("203.0.113.10", record.GetProperty("content").GetString());
        Assert.True(record.GetProperty("proxied").GetBoolean());
        Assert.Equal("Managed by Turbo Manager", record.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Conflicting_cname_is_reported_and_replaced_on_request()
    {
        await ConnectAsync();
        var http = new HttpRequestMessage(HttpMethod.Post, "/client/v4/zones/zone-hotel/dns_records")
        {
            Content = JsonContent.Create(new { type = "CNAME", name = "play.hotel.test", content = "old.example.net", proxied = false, ttl = 1 }),
        };
        http.Headers.Authorization = new("Bearer", "test-token");
        await _fake.SendAsync(http);

        var site = await CreateSiteAsync(new
        {
            name = "play",
            domains = new[] { "play.hotel.test" },
            tlsMode = "None",
            cloudflareDns = true,
            routes = new[] { new { path = "/", kind = "Spa", target = "/var/www/play" } },
        });
        Assert.Equal(1, site.GetProperty("dnsConflicts").GetInt32());

        var sync = await _client.PostAsJsonAsync($"/api/sites/{site.GetProperty("id").GetInt32()}/cloudflare-dns", new { replaceConflicts = true });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        var record = Assert.Single((await FakeStateAsync()).GetProperty("records").EnumerateArray());
        Assert.Equal("A", record.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Cloudflare_origin_site_gets_a_zone_wide_certificate()
    {
        await ConnectAsync();

        var site = await CreateSiteAsync(new
        {
            name = "play",
            domains = new[] { "play.hotel.test" },
            tlsMode = "CloudflareOrigin",
            routes = new[] { new { path = "/", kind = "Spa", target = "/var/www/play" } },
        });
        await WaitForJobAsync(site.GetProperty("certificateJobId").GetInt64());

        Assert.Contains("listen 443 ssl http2;", _agent.Nginx.Sites["play"]);
        Assert.Contains("/etc/turbo-manager/certs/hotel.test/fullchain.pem", _agent.Nginx.Sites["play"]);
        var certificate = Assert.Single((await _client.GetFromJsonAsync<JsonElement>("/api/certificates")).EnumerateArray());
        Assert.Equal(["hotel.test", "*.hotel.test"], certificate.GetProperty("domains").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task Preset_and_purge_reach_cloudflare()
    {
        await ConnectAsync();

        var preset = await _client.PostAsync("/api/cloudflare/zones/zone-hotel/preset", null);
        var results = await preset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(results.EnumerateArray(), r => Assert.True(r.GetProperty("ok").GetBoolean()));

        var state = await FakeStateAsync();
        Assert.Equal("strict", state.GetProperty("settings").GetProperty("ssl").GetString());
        Assert.Equal("off", state.GetProperty("settings").GetProperty("rocket_loader").GetString());

        var purge = await _client.PostAsJsonAsync("/api/cloudflare/zones/zone-hotel/purge", new { hosts = new[] { "play.hotel.test" } });
        Assert.Equal(HttpStatusCode.NoContent, purge.StatusCode);
        // The first purge is the permission probe (a single harmless URL).
        var purges = (await FakeStateAsync()).GetProperty("purges").EnumerateArray().ToList();
        Assert.Contains(purges, p => p.TryGetProperty("files", out _));
        Assert.Contains(purges, p => p.TryGetProperty("hosts", out var hosts) && hosts[0].GetString() == "play.hotel.test");
    }

    [Fact]
    public async Task Cloudflare_only_mode_restricts_web_ports_and_needs_confirmation()
    {
        await ConnectAsync();

        var response = await _client.PutAsJsonAsync("/api/cloudflare/cloudflare-only", new { enabled = true });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("pending").ValueKind);
        Assert.Contains(_agent.Firewall.Rules, r => r.Ports == "443" && r.Source == "173.245.48.0/20");
        Assert.DoesNotContain(_agent.Firewall.Rules, r => r.Ports == "443" && r.Source == "any");
        Assert.True((await _client.GetFromJsonAsync<JsonElement>("/api/cloudflare")).GetProperty("cloudflareOnlyWeb").GetBoolean());
    }

    [Fact]
    public async Task Panel_moves_to_its_own_domain_with_verification_handover_and_setup_close()
    {
        await ConnectAsync();

        var set = await _client.PutAsJsonAsync("/api/panel-address", new { domain = "panel.hotel.test" });
        var setBody = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(setBody.GetProperty("applied").GetBoolean());
        await WaitForJobAsync(setBody.GetProperty("certificateJobId").GetInt64());
        Assert.Contains("proxy_pass http://127.0.0.1:7800;", _agent.Nginx.Sites["panel"]);

        var address = await _client.GetFromJsonAsync<JsonElement>("/api/panel-address");
        var code = new Uri(address.GetProperty("verifyUrl").GetString()!).Query.Split('=')[1];

        // The check only counts when it arrives on the new address over HTTPS.
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/panel-address/verify", new { code })).StatusCode);
        var newAddress = _panel.CreateApiClient("https://panel.hotel.test");
        Assert.Equal(HttpStatusCode.NoContent, (await newAddress.PostAsJsonAsync("/api/panel-address/verify", new { code })).StatusCode);

        // Closing the setup address from the old address is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/panel-address/close-setup", null)).StatusCode);

        var handover = await (await _client.PostAsync("/api/panel-address/handover", null)).Content.ReadFromJsonAsync<JsonElement>();
        var token = new Uri(handover.GetProperty("url").GetString()!).Query.Split('=')[1];
        Assert.Equal(HttpStatusCode.NoContent, (await newAddress.PostAsJsonAsync("/api/auth/handover", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await newAddress.PostAsJsonAsync("/api/auth/handover", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newAddress.GetAsync("/api/auth/me")).StatusCode);

        var close = await newAddress.PostAsync("/api/panel-address/close-setup", null);
        var closeBody = await close.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.True(_agent.Nginx.SetupClosed);
        Assert.NotEqual(JsonValueKind.Null, closeBody.GetProperty("pending").ValueKind);
        Assert.DoesNotContain(_agent.Firewall.Rules, r => r.Comment == "tm:setup");
    }
}
