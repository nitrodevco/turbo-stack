using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TurboManager.Contracts.Agent;

namespace TurboManager.Panel.Tests;

public sealed class PanelUpdateTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;
    private string _secret = "";

    public async Task InitializeAsync()
    {
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        (_secret, _) = await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private Task<JsonElement> StateAsync() => _client.GetFromJsonAsync<JsonElement>("/api/panel-update");

    private async Task<string[]> OpenAlertsAsync()
    {
        (await _client.PostAsync("/api/alerts/check", null)).EnsureSuccessStatusCode();

        return (await _client.GetFromJsonAsync<JsonElement>("/api/alerts")).GetProperty("open").EnumerateArray()
            .Select(a => a.GetProperty("title").GetString()!).ToArray();
    }

    private void Offer(string version) =>
        _agent.Updates.Info = new UpdateInfo
        {
            CurrentVersion = "0.1.0", Channel = "stable", LatestVersion = version, Notes = "Faster deploys.", Available = true, Published = "2026-10-12T12:00:00Z",
        };

    [Fact]
    public async Task A_newer_release_is_shown_after_a_check_and_alerted()
    {
        Assert.False((await StateAsync()).GetProperty("available").GetBoolean());

        Offer("0.2.0");
        (await _client.PostAsync("/api/panel-update/check", null)).EnsureSuccessStatusCode();

        var state = await StateAsync();
        Assert.Equal("0.1.0", state.GetProperty("currentVersion").GetString());
        Assert.Equal("0.2.0", state.GetProperty("latestVersion").GetString());
        Assert.Equal("Faster deploys.", state.GetProperty("notes").GetString());
        Assert.True(state.GetProperty("available").GetBoolean());
        Assert.Contains("Turbo Stack 0.2.0 is available", await OpenAlertsAsync());
    }

    [Fact]
    public async Task A_failed_check_keeps_the_last_good_one_and_says_why()
    {
        Offer("0.2.0");
        (await _client.PostAsync("/api/panel-update/check", null)).EnsureSuccessStatusCode();
        _agent.Updates.Info = new UpdateInfo { CurrentVersion = "0.1.0", Channel = "stable", Error = "The stable channel's signature doesn't match the release key" };
        (await _client.PostAsync("/api/panel-update/check", null)).EnsureSuccessStatusCode();

        var state = await StateAsync();
        Assert.Equal("0.2.0", state.GetProperty("latestVersion").GetString());
        Assert.Contains("signature", state.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Updating_needs_a_fresh_sign_in_and_is_recorded()
    {
        Offer("0.2.0");
        (await _client.PostAsync("/api/panel-update/check", null)).EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync("/api/panel-update/apply", new { version = "0.2.0" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_agent.Updates.Started);

        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        (await _client.PostAsJsonAsync("/api/account/sudo", new { code = _panel.CurrentTotp(_secret) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/panel-update/apply", new { version = "0.3.0" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/panel-update/apply", new { version = "0.2.0" })).StatusCode);

        Assert.Equal(["0.2.0"], _agent.Updates.Started);
        Assert.Contains("0.1.0 → 0.2.0", await _client.GetStringAsync("/api/audit?action=panel.update.start"));
    }

    [Fact]
    public async Task A_rolled_back_update_is_recorded_once_and_raises_a_critical_alert()
    {
        Offer("0.2.0");
        _agent.Updates.Status = new UpdateStatus
        {
            State = "rolled-back", FromVersion = "0.1.0", ToVersion = "0.2.0", Message = "0.2.0 didn't start, so 0.1.0 was put back.",
            UpdatedAt = "2026-10-12T15:00:00Z",
        };

        var state = await StateAsync();
        await StateAsync();

        Assert.Equal("rolled-back", state.GetProperty("last").GetProperty("state").GetString());
        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit?action=panel.update.rolled-back");
        Assert.Single(audit.GetProperty("entries").EnumerateArray());
        Assert.Contains("The update to Turbo Stack 0.2.0 didn't work", await OpenAlertsAsync());
    }

    [Fact]
    public async Task Switching_to_beta_checks_that_channel()
    {
        _agent.Updates.Info = new UpdateInfo { CurrentVersion = "0.1.0", Channel = "beta", LatestVersion = "0.2.0-beta.1", Available = true };

        var response = await _client.PutAsJsonAsync("/api/panel-update/channel", new { channel = "beta" });
        response.EnsureSuccessStatusCode();

        Assert.Equal("beta", _agent.Updates.Channel);
        Assert.Equal("0.2.0-beta.1", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("latestVersion").GetString());
    }
}
