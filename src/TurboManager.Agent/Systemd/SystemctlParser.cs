using System.Globalization;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Systemd;

public static class SystemctlParser
{
    public static readonly string[] ShowProperties =
    [
        "Id", "Description", "LoadState", "ActiveState", "SubState", "UnitFileState",
        "MainPID", "MemoryCurrent", "CPUUsageNSec", "ActiveEnterTimestamp", "NRestarts",
    ];

    /// <summary>First column of `systemctl list-units/list-unit-files --plain --no-legend`.</summary>
    public static IEnumerable<string> ParseUnitNames(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2)[0])
            .Where(name => name.EndsWith(".service", StringComparison.Ordinal) && !name.Contains("@.", StringComparison.Ordinal));

    /// <summary>Parses `systemctl show -p ... a b c` output: one key=value block per unit, separated by blank lines.</summary>
    public static List<Dictionary<string, string>> ParseShowBlocks(string output)
    {
        var blocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                current = null;

                continue;
            }

            var index = line.IndexOf('=');

            if (index <= 0)
            {
                continue;
            }

            if (current is null)
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                blocks.Add(current);
            }

            current[line[..index]] = line[(index + 1)..];
        }

        return blocks;
    }

    public static ServiceUnit ToServiceUnit(IReadOnlyDictionary<string, string> block) => new()
    {
        Name = block.GetValueOrDefault("Id", ""),
        Description = block.GetValueOrDefault("Description", ""),
        LoadState = block.GetValueOrDefault("LoadState", ""),
        ActiveState = block.GetValueOrDefault("ActiveState", ""),
        SubState = block.GetValueOrDefault("SubState", ""),
        UnitFileState = block.GetValueOrDefault("UnitFileState", ""),
        MainPid = (int)ParseCounter(block.GetValueOrDefault("MainPID")),
        MemoryBytes = ParseCounter(block.GetValueOrDefault("MemoryCurrent")),
        CpuUsageNsec = ParseCounter(block.GetValueOrDefault("CPUUsageNSec")),
        ActiveSinceUnixMs = ParseUnixTimestamp(block.GetValueOrDefault("ActiveEnterTimestamp")),
        Restarts = (int)Math.Max(0, ParseCounter(block.GetValueOrDefault("NRestarts"))),
    };

    /// <summary>systemd prints "[not set]" or UINT64_MAX for unknown counters; both become -1.</summary>
    public static long ParseCounter(string? value) =>
        ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number < long.MaxValue
            ? (long)number
            : -1;

    /// <summary>Parses "@1760155200" (from --timestamp=unix) into unix milliseconds; 0 when unset.</summary>
    public static long ParseUnixTimestamp(string? value) =>
        value is ['@', ..] && long.TryParse(value[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds * 1000
            : 0;
}
