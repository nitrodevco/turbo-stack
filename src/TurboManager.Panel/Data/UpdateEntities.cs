namespace TurboManager.Panel.Data;

/// <summary>What the panel last learned about its own updates (one row): the channel check and the last update's outcome.</summary>
public sealed class PanelUpdateState
{
    public int Id { get; set; } = 1;

    public string CurrentVersion { get; set; } = "";

    public string Channel { get; set; } = "stable";

    public string? LatestVersion { get; set; }

    public string? Notes { get; set; }

    public string? Published { get; set; }

    public bool Available { get; set; }

    /// <summary>Why the last check failed (unreachable, or a signature that doesn't match the release key).</summary>
    public string? Error { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>The last update's outcome from ts-update (done, rolled-back, failed), once recorded in the activity log.</summary>
    public string? LastState { get; set; }

    public string? LastFrom { get; set; }

    public string? LastTo { get; set; }

    public string? LastMessage { get; set; }

    /// <summary>ts-update's timestamp of that outcome: recorded once.</summary>
    public string? LastAt { get; set; }
}
