using Microsoft.Extensions.Options;
using TurboManager.Agent.HostInfo;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Metrics;

/// <summary>Samples /proc every few seconds so CPU and network figures are rates, not lifetime totals.</summary>
public sealed class MetricsSampler(IOptions<AgentOptions> options, ILogger<MetricsSampler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private CpuTimes _lastCpu;
    private NetCounters _lastNet;
    private long _lastSampleMs;
    private volatile MetricsSnapshot _latest = new();

    public MetricsSnapshot Latest => _latest;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux() && options.Value.HostRoot == "/")
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                _latest = Sample();
            }
            catch (IOException e)
            {
                logger.LogWarning(e, "Metrics sample failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public MetricsSnapshot Sample()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cpu = ProcReader.ParseCpuTimes(Read("proc/stat"));
        var net = ProcReader.ParseNetCounters(Read("proc/net/dev"));
        var load = ProcReader.ParseLoadAverage(Read("proc/loadavg"));
        var meminfo = SystemInfoReader.ParseMeminfo(HostPath("proc/meminfo"));
        var seconds = _lastSampleMs == 0 ? 0 : (now - _lastSampleMs) / 1000.0;

        var snapshot = new MetricsSnapshot
        {
            TimestampUnixMs = now,
            CpuPercent = _lastSampleMs == 0 ? 0 : ProcReader.CpuPercent(_lastCpu, cpu),
            CpuCount = Environment.ProcessorCount,
            Load1M = load.One,
            Load5M = load.Five,
            Load15M = load.Fifteen,
            MemoryTotalBytes = meminfo.GetValueOrDefault("MemTotal") * 1024,
            MemoryAvailableBytes = meminfo.GetValueOrDefault("MemAvailable") * 1024,
            SwapTotalBytes = meminfo.GetValueOrDefault("SwapTotal") * 1024,
            SwapFreeBytes = meminfo.GetValueOrDefault("SwapFree") * 1024,
            NetRxBytesPerSec = seconds > 0 && net.RxBytes >= _lastNet.RxBytes ? (net.RxBytes - _lastNet.RxBytes) / seconds : 0,
            NetTxBytesPerSec = seconds > 0 && net.TxBytes >= _lastNet.TxBytes ? (net.TxBytes - _lastNet.TxBytes) / seconds : 0,
            UptimeSeconds = (long)ProcReader.ParseLoadAverage(Read("proc/uptime")).One,
        };

        foreach (var mount in ProcReader.ParseDiskMounts(Read("proc/mounts")))
        {
            try
            {
                var drive = new DriveInfo(HostPath(mount.Mount.TrimStart('/')));
                snapshot.Disks.Add(new DiskUsage
                {
                    Mount = mount.Mount,
                    FsType = mount.FsType,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Mount vanished or isn't readable; skip it.
            }
        }

        _lastCpu = cpu;
        _lastNet = net;
        _lastSampleMs = now;

        return snapshot;
    }

    private string Read(string relativePath)
    {
        var path = HostPath(relativePath);

        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private string HostPath(string relativePath) => Path.Combine(options.Value.HostRoot, relativePath);
}
