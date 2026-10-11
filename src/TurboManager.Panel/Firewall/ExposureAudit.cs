using System.Net;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Firewall;

namespace TurboManager.Panel.Firewall;

public enum Exposure
{
    Local,
    Blocked,
    Restricted,
    Public,
}

public enum ExposureRisk
{
    Ok,
    Warning,
    Danger,
}

public sealed record ExposureItem(string Protocol, string Address, int Port, string Process, Exposure Exposure, ExposureRisk Risk, string Note);

/// <summary>
/// Compares listening sockets with the firewall to show what the internet can actually reach.
/// Rule order is not evaluated (ufw is first-match); a deny listed before an allow is reported as
/// reachable, which errs on the side of warning.
/// </summary>
public static class ExposureAudit
{
    /// <summary>Ports a managed server is expected to expose.</summary>
    public static readonly HashSet<int> ExpectedPublicPorts = [22, 80, 443, 8443];

    /// <summary>Ports that should never be reachable from the internet directly.</summary>
    public static readonly Dictionary<int, string> SensitivePorts = new()
    {
        [3306] = "MySQL/MariaDB should only listen locally or be restricted to trusted IPs.",
        [5432] = "PostgreSQL should only listen locally or be restricted to trusted IPs.",
        [6379] = "Redis must never be public.",
        [7800] = "The panel's internal port; it is meant to be reached through nginx only.",
        [11111] = "Orleans silo port used internally by Turbo.",
        [3000] = "Orleans gateway port used internally by Turbo.",
        [9001] = "Turbo's WebSocket port; it should be reached through nginx (wss://) only.",
        [8090] = "Turbo admin API; it should be reached through nginx only.",
        [8092] = "Turbo web API; it should be reached through nginx only.",
        [8094] = "Turbo gamedata host; it should be reached through nginx only.",
        [3030] = "Nitro imager; it should be reached through nginx only.",
    };

    public static List<ExposureItem> Evaluate(IEnumerable<ListeningSocket> sockets, FirewallState firewall)
    {
        var items = new List<ExposureItem>();

        foreach (var socket in sockets)
        {
            var (exposure, detail) = Classify(socket, firewall);
            var (risk, note) = Judge(socket.Port, exposure, detail);
            items.Add(new ExposureItem(socket.Protocol, socket.Address, socket.Port, socket.Process, exposure, risk, note));
        }

        return items.OrderByDescending(i => i.Risk).ThenBy(i => i.Exposure == Exposure.Local).ThenBy(i => i.Port).ToList();
    }

    private static (Exposure Exposure, string Detail) Classify(ListeningSocket socket, FirewallState firewall)
    {
        if (IPAddress.TryParse(socket.Address, out var address) && IPAddress.IsLoopback(address))
        {
            return (Exposure.Local, "Only reachable from this server.");
        }

        if (!firewall.Enabled)
        {
            return (Exposure.Public, "The firewall is off.");
        }

        var allowing = firewall.Rules
            .Where(r => r.Direction is "in" or "" && r.Action is "allow" or "limit")
            .Where(r => r.Protocol == "any" || r.Protocol == socket.Protocol)
            .Where(r => FirewallRuleValidator.CoversPort(r.Ports, socket.Port))
            .ToList();

        if (allowing.Any(r => FirewallSource.Normalize(r.Source) == FirewallSource.Any))
        {
            return (Exposure.Public, "Allowed from anywhere.");
        }

        if (allowing.Count > 0)
        {
            var sources = string.Join(", ", allowing.Select(r => r.Source).Distinct());

            return (Exposure.Restricted, $"Allowed from {sources}.");
        }

        return firewall.DefaultIncoming == "allow"
            ? (Exposure.Public, "No rule matches and the default allows incoming traffic.")
            : (Exposure.Blocked, "Blocked by the firewall.");
    }

    private static (ExposureRisk Risk, string Note) Judge(int port, Exposure exposure, string detail)
    {
        if (exposure != Exposure.Public)
        {
            return (ExposureRisk.Ok, detail);
        }

        if (SensitivePorts.TryGetValue(port, out var why))
        {
            return (ExposureRisk.Danger, $"{detail} {why}");
        }

        return ExpectedPublicPorts.Contains(port)
            ? (ExposureRisk.Ok, detail)
            : (ExposureRisk.Warning, $"{detail} Make sure this service is meant to be public.");
    }
}
