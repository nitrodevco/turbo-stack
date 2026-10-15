using System.Text.Json;

namespace TurboManager.Panel.Tests;

public sealed class PanelRestoreOutcomeTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;

    public async Task InitializeAsync() => _agent = await FakeAgent.StartAsync(_panel.AgentSocket);

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task The_outcome_ts_panel_restore_left_is_recorded_once_at_start_up()
    {
        // As ts-panel-restore leaves it before starting the panel with the restored database.
        var outcome = Path.Combine(_panel.DataDirectory, "panel-restore.json");
        await File.WriteAllTextAsync(outcome, "{\"state\":\"restored\",\"message\":\"The panel's state was restored from a backup snapshot.\",\"at\":\"2026-10-13T10:00:00Z\"}");

        var client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(client);

        var audit = JsonDocument.Parse(await client.GetStringAsync("/api/audit?action=panel.state_restored")).RootElement;
        Assert.Single(audit.GetProperty("entries").EnumerateArray());
        Assert.False(File.Exists(outcome));
    }
}
