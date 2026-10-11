using System.Globalization;
using System.Text.RegularExpressions;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Packages;

public static partial class AptParser
{
    /// <summary>
    /// Parses `apt list --upgradable`, e.g.
    /// "libssl3t64/noble-updates,noble-security 3.0.13-0ubuntu3.5 amd64 [upgradable from: 3.0.13-0ubuntu3.4]".
    /// </summary>
    public static List<UpgradablePackage> ParseUpgradable(string output)
    {
        var packages = new List<UpgradablePackage>();

        foreach (var line in output.Split('\n'))
        {
            var match = UpgradablePattern().Match(line.Trim());

            if (!match.Success)
            {
                continue;
            }

            var source = match.Groups["source"].Value;
            packages.Add(new UpgradablePackage
            {
                Name = match.Groups["name"].Value,
                Source = source,
                CandidateVersion = match.Groups["candidate"].Value,
                CurrentVersion = match.Groups["current"].Value,
                Security = source.Split(',').Any(s => s.EndsWith("-security", StringComparison.Ordinal)),
            });
        }

        return packages;
    }

    /// <summary>Reads USEC= from /run/systemd/shutdown/scheduled; null when no shutdown is scheduled.</summary>
    public static long? ParseScheduledShutdownMs(string content)
    {
        var line = content.Split('\n').FirstOrDefault(l => l.StartsWith("USEC=", StringComparison.Ordinal));

        return line is not null && long.TryParse(line[5..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usec)
            ? usec / 1000
            : null;
    }

    public static bool IsValidPackageName(string name) => PackageNamePattern().IsMatch(name);

    [GeneratedRegex(@"^(?<name>[^/\s]+)/(?<source>\S+)\s+(?<candidate>\S+)\s+\S+\s+\[upgradable from: (?<current>[^\]]+)\]")]
    private static partial Regex UpgradablePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]{0,127}(:[a-z0-9]+)?$")]
    private static partial Regex PackageNamePattern();
}
