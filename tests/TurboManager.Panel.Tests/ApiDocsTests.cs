using System.Net;
using System.Text.Json;

namespace TurboManager.Panel.Tests;

public sealed class ApiDocsTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task The_api_is_described_with_the_permission_each_endpoint_needs()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/openapi.json")).StatusCode);
        await _panel.CompleteSetupAsync(_client);

        var document = JsonDocument.Parse(await _client.GetStringAsync("/api/openapi.json")).RootElement;
        var paths = document.GetProperty("paths");

        Assert.Equal("Turbo Manager API", document.GetProperty("info").GetProperty("title").GetString());
        Assert.Contains("`apps.view`", paths.GetProperty("/api/apps").GetProperty("get").GetProperty("description").GetString());
        Assert.Contains("second factor", paths.GetProperty("/api/panel-update/apply").GetProperty("post").GetProperty("description").GetString());
        Assert.True(paths.EnumerateObject().Count() > 100, "every endpoint is listed");
    }
}
