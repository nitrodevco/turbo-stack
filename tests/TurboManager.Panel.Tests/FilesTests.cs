using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Tests;

public sealed class FilesApiTests : IAsyncLifetime
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
        (await _client.PostAsJsonAsync("/api/repositories", new { name = "web", systemUser = "hotel", repository = "billsonnn/web", branch = "main" }))
            .EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task Files_can_be_edited_uploaded_downloaded_and_deleted()
    {
        Assert.Equal(["hotel"], (await _client.GetFromJsonAsync<string[]>("/api/files/users"))!);

        (await _client.PutAsJsonAsync("/api/files/content", new { user = "hotel", path = "apps/web/shared/.env", content = "KEY=value\n" })).EnsureSuccessStatusCode();
        var file = await _client.GetFromJsonAsync<JsonElement>("/api/files/content?user=hotel&path=apps/web/shared/.env");
        Assert.Equal("KEY=value\n", file.GetProperty("content").GetString());
        Assert.False(file.GetProperty("binary").GetBoolean());

        var upload = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3, 4]);
        (await _client.PostAsync("/api/files/upload?user=hotel&path=badge.png", upload)).EnsureSuccessStatusCode();
        Assert.True((await _client.GetFromJsonAsync<JsonElement>("/api/files/content?user=hotel&path=badge.png")).GetProperty("binary").GetBoolean());

        var download = await _client.GetAsync("/api/files/download?user=hotel&path=badge.png");
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3, 4], await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("badge.png", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var listing = await _client.GetFromJsonAsync<JsonElement>("/api/files?user=hotel");
        Assert.Contains(listing.GetProperty("entries").EnumerateArray(), e => e.GetProperty("name").GetString() == "badge.png");

        (await _client.DeleteAsync("/api/files?user=hotel&path=badge.png")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("hotel:badge.png", _agent.Files.Files.Keys);
        Assert.Contains("files.written", await _client.GetStringAsync("/api/audit?action=files.written"));
    }

    [Fact]
    public async Task Only_app_users_this_person_may_act_as()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/api/files?user=root")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PutAsJsonAsync("/api/files/content", new { user = "postgres", path = "x", content = "y" })).StatusCode);

        // A viewer has no files.manage at all.
        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        (await _client.PostAsJsonAsync("/api/account/sudo", new { code = _panel.CurrentTotp(_secret) })).EnsureSuccessStatusCode();
        var invite = await (await _client.PostAsJsonAsync("/api/team", new { username = "viewer", role = nameof(PanelRole.Viewer) })).Content.ReadFromJsonAsync<JsonElement>();
        var token = invite.GetProperty("inviteUrl").GetString()!.Split("token=")[1];
        var viewer = _panel.CreateApiClient();
        var secret = (await (await viewer.PostAsJsonAsync("/api/invite/password", new { token, password = "a long enough password!" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        (await viewer.PostAsJsonAsync("/api/invite/confirm", new { token, code = _panel.CurrentTotp(secret) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/files?user=hotel")).StatusCode);
    }
}
