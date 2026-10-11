using System.Globalization;

namespace TurboManager.Agent.Metrics;

public readonly record struct CpuTimes(ulong Total, ulong Idle);

public readonly record struct NetCounters(ulong RxBytes, ulong TxBytes);

public sealed record MountPoint(string Mount, string FsType);

/// <summary>Pure parsers for /proc files used by the metrics sampler.</summary>
public static class ProcReader
{
    private static readonly HashSet<string> DiskFsTypes = ["ext4", "ext3", "xfs", "btrfs", "zfs", "vfat", "f2fs"];

    /// <summary>Aggregate "cpu" line of /proc/stat. Idle includes iowait.</summary>
    public static CpuTimes ParseCpuTimes(string procStat)
    {
        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));

        if (line is null)
        {
            return default;
        }

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(v => ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToArray();

        // user nice system idle iowait irq softirq steal (guest values are already counted in user/nice).
        var total = values.Take(8).Aggregate(0UL, (sum, v) => sum + v);
        var idle = values.ElementAtOrDefault(3) + values.ElementAtOrDefault(4);

        return new CpuTimes(total, idle);
    }

    public static double CpuPercent(CpuTimes previous, CpuTimes current)
    {
        var total = current.Total - previous.Total;

        return total == 0 || current.Total < previous.Total
            ? 0
            : Math.Clamp(100.0 * (total - (current.Idle - previous.Idle)) / total, 0, 100);
    }

    /// <summary>Sums /proc/net/dev counters over all interfaces except loopback.</summary>
    public static NetCounters ParseNetCounters(string procNetDev)
    {
        ulong rx = 0, tx = 0;

        foreach (var line in procNetDev.Split('\n').Skip(2))
        {
            var colon = line.IndexOf(':');

            if (colon < 0 || line[..colon].Trim() == "lo")
            {
                continue;
            }

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 9)
            {
                rx += ulong.Parse(fields[0], CultureInfo.InvariantCulture);
                tx += ulong.Parse(fields[8], CultureInfo.InvariantCulture);
            }
        }

        return new NetCounters(rx, tx);
    }

    public static (double One, double Five, double Fifteen) ParseLoadAverage(string loadavg)
    {
        var parts = loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        double Read(int index) =>
            parts.Length > index && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        return (Read(0), Read(1), Read(2));
    }

    /// <summary>Real disk mounts from /proc/mounts, plus "/" whatever its type (overlay in containers).</summary>
    public static List<MountPoint> ParseDiskMounts(string procMounts)
    {
        var mounts = new List<MountPoint>();

        foreach (var line in procMounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');

            if (parts.Length < 3)
            {
                continue;
            }

            var mount = parts[1].Replace("\\040", " ", StringComparison.Ordinal);

            if ((DiskFsTypes.Contains(parts[2]) || mount == "/") && mounts.All(m => m.Mount != mount) && !mount.StartsWith("/snap/", StringComparison.Ordinal))
            {
                mounts.Add(new MountPoint(mount, parts[2]));
            }
        }

        return mounts;
    }
}
