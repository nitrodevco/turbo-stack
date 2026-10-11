using System.Net;
using System.Net.Http.Json;

namespace TurboManager.Panel.Tests;

public sealed class SetupFlowTests : IDisposable
{
    private readonly PanelFactory _panel = new();

    [Fact]
    public async Task Fresh_panel_reports_unclaimed()
    {
        var status = await _panel.CreateApiClient().GetFromJsonAsync<StatusResponse>("/api/setup/status");

        Assert.False(status!.Claimed);
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        _panel.IssueSetupToken();
        var response = await _panel.CreateApiClient().PostAsJsonAsync("/api/setup/claim", new { token = "nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var token = _panel.IssueSetupToken();
        _panel.Time.Advance(TimeSpan.FromHours(25));

        var response = await _panel.CreateApiClient().PostAsJsonAsync("/api/setup/claim", new { token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_be_created_without_claiming_first()
    {
        var response = await _panel.CreateApiClient().PostAsJsonAsync("/api/setup/owner",
            new { username = "owner", password = "correct horse battery staple" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Weak_password_is_rejected()
    {
        var client = _panel.CreateApiClient();
        await client.PostAsJsonAsync("/api/setup/claim", new { token = _panel.IssueSetupToken() });

        var response = await client.PostAsJsonAsync("/api/setup/owner", new { username = "owner", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Completing_setup_signs_in_owner_consumes_token_and_locks_setup()
    {
        var client = _panel.CreateApiClient();
        var (_, recoveryCodes) = await _panel.CompleteSetupAsync(client);

        Assert.Equal(10, recoveryCodes.Length);
        Assert.False(File.Exists(_panel.TokenFile));

        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.Equal("owner", me!.Username);
        Assert.Equal("Owner", me.Role);

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/setup/status");
        Assert.True(status!.Claimed);

        var again = await _panel.CreateApiClient().PostAsJsonAsync("/api/setup/claim", new { token = _panel.IssueSetupToken() });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_without_csrf_header_are_rejected()
    {
        var client = _panel.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup/claim", new { token = _panel.IssueSetupToken() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public void Dispose() => _panel.Dispose();

    private sealed record StatusResponse(bool Claimed, string ProductName);

    private sealed record MeResponse(string Username, string Role, int RemainingRecoveryCodes);
}
