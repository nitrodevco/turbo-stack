using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Metrics;

/// <summary>Stores a host metrics sample every 10 seconds and prunes samples older than the retention.</summary>
public sealed class MetricsCollector(IServiceScopeFactory scopes, AgentChannelFactory agent, TimeProvider time, ILogger<MetricsCollector> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var snapshot = await agent.Metrics.GetMetricsAsync(new MetricsRequest(), cancellationToken: stoppingToken);

                // The agent's first sample has no CPU/network rates yet.
                if (snapshot.TimestampUnixMs == 0)
                {
                    continue;
                }

                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                db.MetricSamples.Add(ToSample(snapshot));
                await db.SaveChangesAsync(stoppingToken);

                if (time.GetUtcNow() - _lastPrune > TimeSpan.FromHours(1))
                {
                    var cutoff = time.GetUtcNow().AddDays(-MetricSample.RetentionDays).ToUnixTimeMilliseconds();
                    await db.MetricSamples.Where(m => m.AtUnixMs < cutoff).ExecuteDeleteAsync(stoppingToken);
                    _lastPrune = time.GetUtcNow();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "Metrics collection skipped (agent unavailable?)");
            }
        }
    }

    public static MetricSample ToSample(MetricsSnapshot snapshot)
    {
        var root = snapshot.Disks.FirstOrDefault(d => d.Mount == "/");

        return new MetricSample
        {
            AtUnixMs = snapshot.TimestampUnixMs,
            CpuPercent = snapshot.CpuPercent,
            Load1 = snapshot.Load1M,
            MemoryTotalBytes = snapshot.MemoryTotalBytes,
            MemoryUsedBytes = snapshot.MemoryTotalBytes - snapshot.MemoryAvailableBytes,
            SwapUsedBytes = snapshot.SwapTotalBytes - snapshot.SwapFreeBytes,
            DiskTotalBytes = root?.TotalBytes ?? 0,
            DiskUsedBytes = root is null ? 0 : root.TotalBytes - root.FreeBytes,
            NetRxBytesPerSec = snapshot.NetRxBytesPerSec,
            NetTxBytesPerSec = snapshot.NetTxBytesPerSec,
        };
    }
}
