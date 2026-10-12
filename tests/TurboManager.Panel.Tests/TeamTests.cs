using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Tests;

public sealed class TeamApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _owner = null!;
    private string _ownerSecret = "";

    public async Task InitializeAsync()
    {
        _panel.Settings["Panel:GitHubKnownHosts:0"] = "ssh-ed25519 AAAATESTKEY";
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _owner = _panel.CreateApiClient();
        (_ownerSecret, _) = await _panel.CompleteSetupAsync(_owner);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    /// <summary>Re-authenticates (sudo mode) with the next TOTP code.</summary>
    private async Task SudoAsync(HttpClient client, string secret)
    {
        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        (await client.PostAsJsonAsync("/api/account/sudo", new { code = _panel.CurrentTotp(secret) })).EnsureSuccessStatusCode();
    }

    /// <summary>Invites a user and walks through their invite link; returns their signed-in client and TOTP secret.</summary>
    private async Task<(HttpClient Client, string Secret, int Id)> AddMemberAsync(string username, PanelRole role, int[]? apps = null)
    {
        var invite = await (await _owner.PostAsJsonAsync("/api/team", new { username, role = role.ToString(), scopeApps = apps ?? [] }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var token = invite.GetProperty("inviteUrl").GetString()!.Split("token=")[1];

        var client = _panel.CreateApiClient();
        Assert.Equal(username, (await client.GetFromJsonAsync<JsonElement>($"/api/invite/{token}")).GetProperty("username").GetString());
        var enrollment = await (await client.PostAsJsonAsync("/api/invite/password", new { token, password = "a long enough password!" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var secret = enrollment.GetProperty("secret").GetString()!;
        (await client.PostAsJsonAsync("/api/invite/confirm", new { token, code = _panel.CurrentTotp(secret) })).EnsureSuccessStatusCode();

        return (client, secret, invite.GetProperty("id").GetInt32());
    }

    private async Task<int> CreateAppAsync(string name)
    {
        var repository = await (await _owner.PostAsJsonAsync("/api/repositories", new
        {
            name = $"{name}-repo", systemUser = "hotel", repository = $"billsonnn/{name}", branch = "main", @private = false,
        })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();

        return (await (await _owner.PostAsJsonAsync("/api/apps", new
        {
            name, repositoryId = repository.GetProperty("id").GetInt32(), serviceCommand = "node server.js", servicePort = 4000,
        })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Managing_the_team_needs_a_fresh_second_factor()
    {
        var response = await _owner.PostAsJsonAsync("/api/team", new { username = "alice", role = "Developer" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sudo").GetBoolean());

        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync("/api/account/sudo", new { code = "000000" })).StatusCode);
        await SudoAsync(_owner, _ownerSecret);
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsJsonAsync("/api/team", new { username = "alice", role = "Developer" })).StatusCode);

        _panel.Time.Advance(TimeSpan.FromMinutes(11)); // the window closes
        Assert.Equal(HttpStatusCode.Forbidden, (await _owner.PostAsJsonAsync("/api/team", new { username = "bob", role = "Viewer" })).StatusCode);
    }

    [Fact]
    public async Task An_invited_member_sets_a_password_and_2fa_and_gets_their_role()
    {
        await SudoAsync(_owner, _ownerSecret);
        var (bob, _, _) = await AddMemberAsync("bob", PanelRole.Viewer);

        var me = await bob.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Viewer", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/team")).StatusCode);

        var team = await _owner.GetFromJsonAsync<JsonElement[]>("/api/team");
        var member = team!.Single(u => u.GetProperty("username").GetString() == "bob");
        Assert.True(member.GetProperty("twoFactor").GetBoolean());
        Assert.False(member.GetProperty("invitePending").GetBoolean());
    }

    [Fact]
    public async Task Invite_links_expire_and_work_once()
    {
        await SudoAsync(_owner, _ownerSecret);
        var invite = await (await _owner.PostAsJsonAsync("/api/team", new { username = "carol", role = "Operator" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = invite.GetProperty("inviteUrl").GetString()!.Split("token=")[1];

        _panel.Time.Advance(TimeSpan.FromDays(4));
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"/api/invite/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync("/api/invite/not-a-token")).StatusCode);
    }

    [Fact]
    public async Task A_member_limited_to_one_app_sees_only_that_app()
    {
        var allowed = await CreateAppAsync("allowed");
        var other = await CreateAppAsync("other");
        await SudoAsync(_owner, _ownerSecret);
        var (dev, _, _) = await AddMemberAsync("dave", PanelRole.Developer, [allowed]);

        var apps = await dev.GetFromJsonAsync<JsonElement[]>("/api/apps");
        Assert.Equal(["allowed"], apps!.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await dev.GetAsync($"/api/apps/{allowed}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.GetAsync($"/api/apps/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PostAsync($"/api/apps/{other}/deploy", null)).StatusCode);
        Assert.Single((await dev.GetFromJsonAsync<JsonElement[]>("/api/repositories"))!);

        // Logs: only their app's service, and they have to say which.
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.GetAsync("/api/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.GetAsync("/api/logs?unit=tm-app-other.service")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await dev.GetAsync("/api/logs?unit=tm-app-allowed.service")).StatusCode);
        Assert.Equal("Developer", (await dev.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("role").GetString());
        Assert.True((await dev.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("limited").GetBoolean());
    }

    [Fact]
    public async Task Revoked_sessions_and_role_changes_sign_browsers_out()
    {
        await SudoAsync(_owner, _ownerSecret);
        var (erin, _, erinId) = await AddMemberAsync("erin", PanelRole.Operator);

        var sessions = await _owner.GetFromJsonAsync<JsonElement[]>("/api/team/sessions");
        var erinSession = sessions!.Single(s => s.GetProperty("username").GetString() == "erin").GetProperty("id").GetString();
        (await _owner.DeleteAsync($"/api/team/sessions/{erinSession}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await erin.GetAsync("/api/auth/me")).StatusCode);

        var (frank, _, frankId) = await AddMemberAsync("frank", PanelRole.Operator);
        Assert.Equal(HttpStatusCode.OK, (await frank.GetAsync("/api/services")).StatusCode);
        (await _owner.PutAsJsonAsync($"/api/team/{frankId}", new { role = "Viewer" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await frank.GetAsync("/api/auth/me")).StatusCode);

        // The owner can't be demoted or removed, and nobody changes their own role here.
        var owner = (await _owner.GetFromJsonAsync<JsonElement[]>("/api/team"))!.Single(u => u.GetProperty("role").GetString() == "Owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PutAsJsonAsync($"/api/team/{owner.GetProperty("id").GetInt32()}", new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.DeleteAsync($"/api/team/{owner.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/team/{erinId}")).StatusCode);
    }

    [Fact]
    public async Task Logging_out_ends_the_session_for_good()
    {
        var sessions = await _owner.GetFromJsonAsync<JsonElement[]>("/api/account/sessions");
        Assert.True(Assert.Single(sessions!).GetProperty("current").GetBoolean());

        (await _owner.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _owner.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Api_tokens_carry_only_the_permissions_they_were_given()
    {
        await CreateAppAsync("site");
        Assert.Equal(HttpStatusCode.Forbidden, (await _owner.PostAsJsonAsync("/api/account/tokens", new { name = "ci", permissions = new[] { "apps.view" } })).StatusCode);
        await SudoAsync(_owner, _ownerSecret);

        var created = await (await _owner.PostAsJsonAsync("/api/account/tokens", new { name = "ci", permissions = new[] { "apps.view", "apps.deploy" }, expiresInDays = 30 }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("token").GetString()!;
        Assert.StartsWith("tm_", secret);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _owner.PostAsJsonAsync("/api/account/tokens", new { name = "root", permissions = new[] { "terminal.root" } })).StatusCode);

        var bot = _panel.CreateClient(); // no cookies, no CSRF header: just the token
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync("/api/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/firewall")).StatusCode); // the owner may; the token may not
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/account/tokens")).StatusCode); // account pages need a session
        var app = (await bot.GetFromJsonAsync<JsonElement[]>("/api/apps"))![0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Accepted, (await bot.PostAsync($"/api/apps/{app}/deploy", null)).StatusCode);

        var tokens = await _owner.GetFromJsonAsync<JsonElement[]>("/api/account/tokens");
        Assert.NotEqual(JsonValueKind.Null, Assert.Single(tokens!).GetProperty("lastUsedAt").ValueKind);

        _panel.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/apps")).StatusCode);

        var wrong = _panel.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tm_not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/apps")).StatusCode);
    }
}
