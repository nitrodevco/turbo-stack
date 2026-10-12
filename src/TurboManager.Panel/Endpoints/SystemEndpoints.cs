using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Endpoints;

public static class SystemEndpoints
{
    /// <summary>History ranges and the bucket size used to downsample them (≈300-400 points each).</summary>
    private static readonly Dictionary<string, (TimeSpan Range, long BucketMs)> HistoryRanges = new()
    {
        ["1h"] = (TimeSpan.FromHours(1), 10_000),
        ["6h"] = (TimeSpan.FromHours(6), 60_000),
        ["24h"] = (TimeSpan.FromHours(24), 300_000),
        ["7d"] = (TimeSpan.FromDays(7), 1_800_000),
    };

    private static readonly string InstalledVersion = ReleaseVersion.Installed();

    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            // The release this process belongs to (tm-update waits for the new one to answer here).
            version = InstalledVersion,
        }));

        var group = app.MapGroup("/api").WithAgentErrors().RequirePermission(Permissions.SystemView);

        group.MapGet("/system/info", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.System.GetSystemInfoAsync(new SystemInfoRequest(), cancellationToken: cancellationToken)));

        group.MapGet("/metrics/current", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.Metrics.GetMetricsAsync(new MetricsRequest(), cancellationToken: cancellationToken)));

        group.MapGet("/metrics/history", async (string? range, PanelDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (!HistoryRanges.TryGetValue(range ?? "1h", out var window))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use 1h, 6h, 24h or 7d."] });
            }

            var from = time.GetUtcNow().Add(-window.Range).ToUnixTimeMilliseconds();
            var bucket = window.BucketMs;
            var points = await db.MetricSamples
                .Where(m => m.AtUnixMs >= from)
                .GroupBy(m => m.AtUnixMs / bucket)
                .Select(g => new
                {
                    at = g.Key * bucket,
                    cpuPercent = g.Average(m => m.CpuPercent),
                    load1 = g.Average(m => m.Load1),
                    memoryUsedBytes = (long)g.Average(m => m.MemoryUsedBytes),
                    memoryTotalBytes = g.Max(m => m.MemoryTotalBytes),
                    swapUsedBytes = (long)g.Average(m => m.SwapUsedBytes),
                    diskUsedBytes = g.Max(m => m.DiskUsedBytes),
                    diskTotalBytes = g.Max(m => m.DiskTotalBytes),
                    netRxBytesPerSec = g.Average(m => m.NetRxBytesPerSec),
                    netTxBytesPerSec = g.Average(m => m.NetTxBytesPerSec),
                })
                .OrderBy(p => p.at)
                .ToListAsync(cancellationToken);

            return Results.Ok(new { range = range ?? "1h", bucketMs = bucket, points });
        });
    }
}
