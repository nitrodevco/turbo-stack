using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Tests;

/// <summary>Panel API against a fake agent: firewall commits/confirmations/reverts, services, jobs, metrics.</summary>
public sealed class ServerManagementApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Installer_rules_are_seeded_and_in_sync()
    {
        var firewall = await GetJsonAsync("/api/firewall");

        Assert.True(firewall.GetProperty("inSync").GetBoolean());
        Assert.Equal(4, firewall.GetProperty("rules").GetArrayLength());
        Assert.All(firewall.GetProperty("rules").EnumerateArray(), r => Assert.Equal("applied", r.GetProperty("status").GetString()));
        Assert.Equal("51820", firewall.GetProperty("external")[0].GetProperty("ports").GetString());
    }

    [Fact]
    public async Task Adding_an_allow_rule_applies_immediately()
    {
        var response = await _client.PostAsJsonAsync("/api/firewall/rules",
            new { name = "Game TCP", action = "allow", protocol = "tcp", ports = "30000", sourceKind = "Any" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pending").ValueKind);
        Assert.Contains(_agent.Firewall.Rules, r => r.Ports == "30000" && r.Comment.StartsWith("tm:r"));
    }

    [Fact]
    public async Task Invalid_rule_is_rejected_before_reaching_the_agent()
    {
        var response = await _client.PostAsJsonAsync("/api/firewall/rules",
            new { name = "Bad", action = "allow", protocol = "tcp", ports = "70000", sourceKind = "Any" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_agent.Firewall.Changes);
    }

    [Fact]
    public async Task Risky_change_waits_for_confirmation_and_is_kept_when_confirmed()
    {
        var response = await _client.PostAsJsonAsync("/api/firewall/rules",
            new { name = "Block scanner", action = "deny", protocol = "tcp", ports = "22", sourceKind = "Address", sourceAddress = "198.51.100.66" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var pendingId = body.GetProperty("pending").GetProperty("id").GetString();

        Assert.Equal(60, _agent.Firewall.Changes.Last().ConfirmWithinSeconds);
        Assert.Equal(pendingId, (await GetJsonAsync("/api/firewall")).GetProperty("pending").GetProperty("id").GetString());

        var confirm = await _client.PostAsync($"/api/firewall/changes/{pendingId}/confirm", null);

        Assert.Equal("confirmed", (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync("/api/firewall")).GetProperty("pending").ValueKind);
    }

    [Fact]
    public async Task Change_reverted_by_the_agent_is_rolled_back_in_the_panel()
    {
        var response = await _client.DeleteAsync($"/api/firewall/rules/{await RuleIdAsync("http")}");
        var pendingId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pending").GetProperty("id").GetString()!;

        // The admin never confirms: the agent reverts and puts the rule back.
        _agent.Firewall.Transactions[pendingId] = "reverted";
        _agent.Firewall.Rules.Add(new FirewallRuleSpec { Action = "allow", Protocol = "tcp", Ports = "80", Source = "any", Comment = "tm:http" });
        // The fake agent stamps revert times from the real clock; move well past the window.
        _panel.Time.Advance(TimeSpan.FromMinutes(2));

        var firewall = await GetJsonAsync("/api/firewall");

        Assert.Equal(JsonValueKind.Null, firewall.GetProperty("pending").ValueKind);
        Assert.Contains(firewall.GetProperty("rules").EnumerateArray(), r => r.GetProperty("key").GetString() == "http");
        Assert.True(firewall.GetProperty("inSync").GetBoolean());
    }

    [Fact]
    public async Task Second_change_is_refused_while_one_awaits_confirmation()
    {
        await _client.DeleteAsync($"/api/firewall/rules/{await RuleIdAsync("http")}");

        var second = await _client.PostAsJsonAsync("/api/firewall/rules",
            new { name = "Another", action = "allow", protocol = "tcp", ports = "9999", sourceKind = "Any" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Editing_an_ip_list_updates_rules_that_use_it()
    {
        var list = await _client.PostAsJsonAsync("/api/firewall/ip-lists",
            new { name = "Staff", entries = new[] { new { source = "203.0.113.7" } } });
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listId = (await GetJsonAsync("/api/firewall/ip-lists"))[0].GetProperty("id").GetInt32();

        await _client.PostAsJsonAsync("/api/firewall/rules",
            new { name = "MySQL for staff", action = "allow", protocol = "tcp", ports = "3306", sourceKind = "IpList", ipListId = listId });
        await _client.PutAsJsonAsync($"/api/firewall/ip-lists/{listId}",
            new { name = "Staff", entries = new[] { new { source = "203.0.113.7" }, new { source = "198.51.100.0/24" } } });

        Assert.Equal(2, _agent.Firewall.Rules.Count(r => r.Ports == "3306"));
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/firewall/ip-lists/{listId}")).StatusCode);
    }

    [Fact]
    public async Task Exposure_audit_reports_sockets()
    {
        var items = await GetJsonAsync("/api/firewall/exposure");

        Assert.Contains(items.EnumerateArray(), i => i.GetProperty("port").GetInt32() == 3306 && i.GetProperty("exposure").GetString() == "blocked");
        Assert.Contains(items.EnumerateArray(), i => i.GetProperty("port").GetInt32() == 7800 && i.GetProperty("exposure").GetString() == "local");
    }

    [Fact]
    public async Task Services_can_be_listed_and_controlled_with_agent_errors_mapped()
    {
        Assert.Equal("nginx.service", (await GetJsonAsync("/api/services"))[0].GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/services/nginx.service/restart", null)).StatusCode);
        Assert.Equal(ServiceAction.Restart, _agent.Services.Controls.Single().Action);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/services/missing.service/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/services/nginx.service/explode", null)).StatusCode);
    }

    [Fact]
    public async Task Package_upgrade_runs_as_a_job_with_output()
    {
        var start = await _client.PostAsJsonAsync("/api/packages/upgrade", new { mode = "security" });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt64();

        JsonElement job = default;

        for (var i = 0; i < 100; i++)
        {
            job = await GetJsonAsync($"/api/jobs/{jobId}");

            if (job.GetProperty("status").GetString() != "running")
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        Assert.Contains("Upgrading Security", job.GetProperty("output").GetString());

        var stream = await _client.GetStringAsync($"/api/jobs/{jobId}/stream");
        Assert.Contains("event: end", stream);
        Assert.Contains("data: succeeded", stream);
    }

    [Fact]
    public async Task Metrics_history_is_downsampled()
    {
        using (var scope = _panel.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
            var now = _panel.Time.GetUtcNow().ToUnixTimeMilliseconds();

            for (var i = 0; i < 60; i++)
            {
                db.MetricSamples.Add(new MetricSample { AtUnixMs = now - i * 10_000, CpuPercent = i % 2 == 0 ? 10 : 30, MemoryTotalBytes = 100 });
            }

            await db.SaveChangesAsync();
        }

        var history = await GetJsonAsync("/api/metrics/history?range=6h");
        var points = history.GetProperty("points");

        Assert.InRange(points.GetArrayLength(), 10, 11);
        Assert.Equal(20, points[1].GetProperty("cpuPercent").GetDouble(), 3);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/metrics/history?range=1y")).StatusCode);
    }

    [Fact]
    public async Task Audit_log_records_firewall_changes()
    {
        await _client.PostAsJsonAsync("/api/firewall/rules", new { name = "Web alt", action = "allow", protocol = "tcp", ports = "8080", sourceKind = "Any" });

        var audit = await GetJsonAsync("/api/audit?action=firewall");

        Assert.Contains(audit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("details").GetString()!.Contains("Web alt"));
    }

    [Fact]
    public async Task Me_includes_permissions()
    {
        var me = await GetJsonAsync("/api/auth/me");

        Assert.Contains(me.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "firewall.manage");
    }

    private async Task<int> RuleIdAsync(string key) =>
        (await GetJsonAsync("/api/firewall")).GetProperty("rules").EnumerateArray()
        .First(r => r.GetProperty("key").GetString() == key).GetProperty("id").GetInt32();
}
