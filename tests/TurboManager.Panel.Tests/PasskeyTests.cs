using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurboManager.Panel.Tests;

public sealed class PasskeyTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private readonly SoftwareAuthenticator _authenticator = new();
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
        _authenticator.Dispose();
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private async Task SudoAsync()
    {
        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        (await _client.PostAsJsonAsync("/api/account/sudo", new { code = _panel.CurrentTotp(_secret) })).EnsureSuccessStatusCode();
    }

    private static async Task<(string Ceremony, JsonNode Options)> OptionsAsync(HttpClient client, string path)
    {
        var response = await client.PostAsync(path, null);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        return (body["ceremony"]!.GetValue<string>(), body["options"]!);
    }

    private async Task RegisterAsync()
    {
        await SudoAsync();
        var (ceremony, options) = await OptionsAsync(_client, "/api/account/passkeys/options");
        Assert.Equal("localhost", options["rp"]!["id"]!.GetValue<string>());
        Assert.Equal("required", options["authenticatorSelection"]!["userVerification"]!.GetValue<string>());

        var response = await _client.PostAsJsonAsync("/api/account/passkeys", new { ceremony, name = "Laptop", response = _authenticator.Create(options) });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Adding_a_passkey_needs_the_second_factor_again()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("/api/account/passkeys/options", null)).StatusCode);

        await RegisterAsync();

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/account/passkeys");
        Assert.Equal("localhost", list.GetProperty("rpId").GetString());
        Assert.Equal("Laptop", list.GetProperty("passkeys")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_passkey_signs_in_without_password_or_code()
    {
        await RegisterAsync();
        var browser = _panel.CreateApiClient();

        var (ceremony, options) = await OptionsAsync(browser, "/api/auth/passkey/options");
        Assert.Empty(options["allowCredentials"]?.AsArray() ?? []);
        var response = await browser.PostAsJsonAsync("/api/auth/passkey", new { ceremony, response = _authenticator.Get(options) });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("owner", (await browser.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("username").GetString());
        Assert.Contains("auth.login_passkey", await _client.GetStringAsync("/api/audit?action=auth.login_passkey"));
    }

    [Fact]
    public async Task A_response_for_another_origin_or_a_reused_ceremony_is_refused()
    {
        await RegisterAsync();
        var browser = _panel.CreateApiClient();

        var (ceremony, options) = await OptionsAsync(browser, "/api/auth/passkey/options");
        var phished = await browser.PostAsJsonAsync("/api/auth/passkey", new { ceremony, response = _authenticator.Get(options, "https://evil.example.com") });
        Assert.Equal(HttpStatusCode.BadRequest, phished.StatusCode);

        // The ceremony was used up by the failed attempt.
        var replay = await browser.PostAsJsonAsync("/api/auth/passkey", new { ceremony, response = _authenticator.Get(options) });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_passkey_confirms_it_is_you()
    {
        await RegisterAsync();
        _panel.Time.Advance(TimeSpan.FromMinutes(11)); // the sudo window from adding it has closed
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("/api/account/passkeys/options", null)).StatusCode);

        var (ceremony, options) = await OptionsAsync(_client, "/api/account/sudo/passkey/options");
        Assert.Single(options["allowCredentials"]!.AsArray());
        (await _client.PostAsJsonAsync("/api/account/sudo/passkey", new { ceremony, response = _authenticator.Get(options) })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/account/passkeys/options", null)).StatusCode);
    }
}
