using System.Text.Json;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Backups;

/// <summary>tm-panel-restore leaves the outcome of a panel state restore next to the database: recorded once, at start-up.</summary>
public static class PanelRestoreOutcome
{
    public const string FileName = "panel-restore.json";

    public static async Task RecordAsync(string dataDirectory, PanelDbContext db, AuditLog audit, ILogger logger)
    {
        var path = Path.Combine(dataDirectory, FileName);

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var state = json.RootElement.GetProperty("state").GetString();
            var message = json.RootElement.TryGetProperty("message", out var text) ? text.GetString() : null;

            audit.Record(state == "restored" ? "panel.state_restored" : "panel.state_restore_failed", details: message);
            await db.SaveChangesAsync();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(e, "Could not read {File}", path);
        }

        File.Delete(path);
    }
}
