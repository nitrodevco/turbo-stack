using System.Net;
using System.Net.Http.Json;

namespace TurboManager.Panel.Tests;

public sealed class LoginFlowTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly PanelFactory _panel = new();

    [Fact]
    public async Task Password_then_totp_signs_in()
    {
        var (secret, _) = await _panel.CompleteSetupAsync(_panel.CreateApiClient());
        _panel.Time.Advance(TimeSpan.FromSeconds(30));

        var client = _panel.CreateApiClient();
        await LoginPasswordAsync(client);
        var mfa = await client.PostAsJsonAsync("/api/auth/mfa", new { code = _panel.CurrentTotp(secret) });

        Assert.Equal(HttpStatusCode.OK, mfa.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Password_alone_does_not_grant_a_session()
    {
        await _panel.CompleteSetupAsync(_panel.CreateApiClient());

        var client = _panel.CreateApiClient();
        await LoginPasswordAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system/info")).StatusCode);
    }

    [Fact]
    public async Task Totp_code_cannot_be_replayed()
    {
        var (secret, _) = await _panel.CompleteSetupAsync(_panel.CreateApiClient());

        // Same time step as the code used to confirm setup.
        var client = _panel.CreateApiClient();
        await LoginPasswordAsync(client);
        var mfa = await client.PostAsJsonAsync("/api/auth/mfa", new { code = _panel.CurrentTotp(secret) });

        Assert.Equal(HttpStatusCode.Unauthorized, mfa.StatusCode);
    }

    [Fact]
    public async Task Recovery_code_works_once()
    {
        var (_, recoveryCodes) = await _panel.CompleteSetupAsync(_panel.CreateApiClient());

        var first = _panel.CreateApiClient();
        await LoginPasswordAsync(first);
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/auth/mfa", new { recoveryCode = recoveryCodes[0].ToUpperInvariant() })).StatusCode);

        var second = _panel.CreateApiClient();
        await LoginPasswordAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsJsonAsync("/api/auth/mfa", new { recoveryCode = recoveryCodes[0] })).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_with_a_generic_message()
    {
        await _panel.CompleteSetupAsync(_panel.CreateApiClient());

        var wrongPassword = await _panel.CreateApiClient().PostAsJsonAsync("/api/auth/login", new { username = "owner", password = "wrong password!!" });
        var unknownUser = await _panel.CreateApiClient().PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(client);

        await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task System_info_reports_unavailable_agent()
    {
        var client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/system/info")).StatusCode);
    }

    private static async Task LoginPasswordAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "owner", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose() => _panel.Dispose();
}
