using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.HostInfo;

/// <summary>Reads host facts from /proc and /etc. All paths are relative to <see cref="AgentOptions.HostRoot"/>.</summary>
public sealed class SystemInfoReader(IOptions<AgentOptions> options)
{
    private string Root => options.Value.HostRoot;

    public SystemInfo Read()
    {
        var osRelease = ParseKeyValueFile(HostPath("etc/os-release"));
        var meminfo = ParseMeminfo(HostPath("proc/meminfo"));
        var (diskTotal, diskFree) = ReadDisk();

        return new SystemInfo
        {
            Hostname = ReadFirstLine("etc/hostname") ?? Environment.MachineName,
            OsName = osRelease.GetValueOrDefault("PRETTY_NAME", RuntimeInformation.OSDescription),
            OsVersionId = osRelease.GetValueOrDefault("VERSION_ID", ""),
            Kernel = ReadFirstLine("proc/sys/kernel/osrelease") ?? "",
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            UptimeSeconds = (long)ReadLeadingDouble("proc/uptime"),
            CpuCount = Environment.ProcessorCount,
            MemoryTotalBytes = meminfo.GetValueOrDefault("MemTotal") * 1024,
            MemoryAvailableBytes = meminfo.GetValueOrDefault("MemAvailable") * 1024,
            DiskTotalBytes = diskTotal,
            DiskFreeBytes = diskFree,
            LoadAverage1M = ReadLeadingDouble("proc/loadavg"),
            Container = DetectContainer(),
        };
    }

    public string DetectContainer()
    {
        // systemd writes the detected container manager here when it runs as PID 1 in a container.
        var systemdContainer = ReadFirstLine("run/systemd/container");

        if (!string.IsNullOrWhiteSpace(systemdContainer))
        {
            return systemdContainer.Trim();
        }

        return File.Exists(HostPath(".dockerenv")) ? "docker" : "";
    }

    public static Dictionary<string, string> ParseKeyValueFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOf('=');

            if (index <= 0 || line.StartsWith('#'))
            {
                continue;
            }

            result[line[..index].Trim()] = line[(index + 1)..].Trim().Trim('"');
        }

        return result;
    }

    public static Dictionary<string, long> ParseMeminfo(string path)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadLines(path))
        {
            // "MemTotal:       16314200 kB"
            var parts = line.Split(':', 2);

            if (parts.Length != 2)
            {
                continue;
            }

            var number = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                result[parts[0].Trim()] = value;
            }
        }

        return result;
    }

    private (long Total, long Free) ReadDisk()
    {
        try
        {
            var drive = new DriveInfo(Root);

            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private double ReadLeadingDouble(string relativePath)
    {
        var first = ReadFirstLine(relativePath)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private string? ReadFirstLine(string relativePath)
    {
        var path = HostPath(relativePath);

        return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
    }

    private string HostPath(string relativePath) => Path.Combine(Root, relativePath);
}
