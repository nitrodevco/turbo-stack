namespace TurboManager.Panel.Data;

public enum JobStatus
{
    Running,
    Succeeded,
    Failed,
}

public sealed class Job
{
    public long Id { get; set; }

    public required string Kind { get; set; }

    public required string Title { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Running;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public int? StartedByUserId { get; set; }

    public string? StartedBy { get; set; }

    /// <summary>Full output, saved when the job finishes (live output streams from memory).</summary>
    public string? Output { get; set; }
}

/// <summary>One host resource sample (every 10 seconds, kept for <see cref="RetentionDays"/> days).</summary>
public sealed class MetricSample
{
    public const int RetentionDays = 7;

    public long Id { get; set; }

    public long AtUnixMs { get; set; }

    public double CpuPercent { get; set; }

    public double Load1 { get; set; }

    public long MemoryUsedBytes { get; set; }

    public long MemoryTotalBytes { get; set; }

    public long SwapUsedBytes { get; set; }

    public long DiskUsedBytes { get; set; }

    public long DiskTotalBytes { get; set; }

    public double NetRxBytesPerSec { get; set; }

    public double NetTxBytesPerSec { get; set; }
}
