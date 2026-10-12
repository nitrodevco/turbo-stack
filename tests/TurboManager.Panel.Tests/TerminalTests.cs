using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace TurboManager.Panel.Tests;

public sealed class TerminalApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private readonly CookieContainer _cookies = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;
    private string _secret = "";

    public async Task InitializeAsync()
    {
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(_cookies));
        _client.DefaultRequestHeaders.Add("X-TM-Request", "1");
        (_secret, _) = await _panel.CompleteSetupAsync(_client);
        (await _client.PostAsJsonAsync("/api/repositories", new { name = "web", systemUser = "hotel", repository = "billsonnn/web", branch = "main" }))
            .EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private Task<WebSocket> OpenAsync(string user, string origin = "http://localhost")
    {
        var client = _panel.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = _cookies.GetCookieHeader(new Uri("http://localhost"));
            request.Headers["Origin"] = origin;
        };

        return client.ConnectAsync(new Uri($"ws://localhost/api/terminal?user={user}&cols=120&rows=40"), CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task A_shell_as_an_app_user_relays_keystrokes_and_resizes()
    {
        var users = await _client.GetFromJsonAsync<JsonElement>("/api/terminal/users");
        Assert.Equal(["hotel"], users.GetProperty("users").EnumerateArray().Select(u => u.GetString()));

        using var socket = await OpenAsync("hotel");
        await socket.SendAsync(Encoding.UTF8.GetBytes("ls -la\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("ls -la\n", await ReceiveAsync(socket));

        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"resize":{"cols":200,"rows":50}}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("exit\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("""{"exit":0}""", await ReceiveAsync(socket));

        Assert.Equal(["hotel"], _agent.Terminal.OpenedAs);
        Assert.Equal(200, Assert.Single(_agent.Terminal.Resizes).Cols);
        Assert.Contains("terminal.opened", await _client.GetStringAsync("/api/audit?action=terminal.opened"));
    }

    [Fact]
    public async Task Other_sites_unknown_users_and_root_without_reauthentication_are_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync("hotel", origin: "https://evil.example.com"));
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync("postgres"));
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync("root"));
        Assert.Empty(_agent.Terminal.OpenedAs);

        // Root: the owner, after confirming with a fresh code.
        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        (await _client.PostAsJsonAsync("/api/account/sudo", new { code = _panel.CurrentTotp(_secret) })).EnsureSuccessStatusCode();
        using var socket = await OpenAsync("root");

        for (var i = 0; i < 50 && _agent.Terminal.OpenedAs.Count == 0; i++)
        {
            await Task.Delay(50); // the bridge opens the agent's terminal right after the handshake
        }

        Assert.Equal(["root"], _agent.Terminal.OpenedAs);
    }
}
