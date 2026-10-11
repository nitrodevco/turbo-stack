using System.Globalization;
using System.Net;
using TurboManager.Core.Firewall;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Firewall;

/// <summary>Validation of user input for rules (the agent validates again before touching ufw).</summary>
public static class FirewallRuleValidator
{
    public static readonly string[] Actions = ["allow", "deny", "reject", "limit"];

    public static readonly string[] Protocols = ["tcp", "udp", "any"];

    public static string? Validate(string? name, string? action, string? protocol, string? ports, FirewallSourceKind sourceKind, string? sourceAddress)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return "Give the rule a name (up to 64 characters).";
        }

        if (!Actions.Contains(action))
        {
            return "Action must be allow, deny, reject or limit.";
        }

        if (!Protocols.Contains(protocol))
        {
            return "Protocol must be tcp, udp or any.";
        }

        var portError = ValidatePorts(ports);

        if (portError is not null)
        {
            return portError;
        }

        if (protocol == "any" && (ports!.Contains(',') || ports.Contains(':')))
        {
            return "Port ranges and lists need a protocol (tcp or udp).";
        }

        if (action == "limit" && protocol != "tcp")
        {
            return "Rate limiting only works with tcp.";
        }

        if (sourceKind == FirewallSourceKind.Address && (string.IsNullOrWhiteSpace(sourceAddress) || !IsAddress(sourceAddress)))
        {
            return "Source must be an IP address or network (e.g. 203.0.113.7 or 203.0.113.0/24).";
        }

        return null;
    }

    public static string? ValidatePorts(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports))
        {
            return "Enter a port, a range (6000:6010) or a list (80,443).";
        }

        foreach (var part in ports.Split(',', StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split(':');

            if (bounds.Length > 2 || bounds.Any(b => !int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535))
            {
                return $"\"{part}\" is not a valid port or range (1-65535).";
            }

            if (bounds.Length == 2 && int.Parse(bounds[0], CultureInfo.InvariantCulture) >= int.Parse(bounds[1], CultureInfo.InvariantCulture))
            {
                return $"\"{part}\": the range must go from low to high.";
            }
        }

        return null;
    }

    public static bool IsAddress(string value) =>
        IPAddress.TryParse(value.Trim(), out _) || IPNetwork.TryParse(value.Trim(), out _);

    /// <summary>True when the rule's ports include <paramref name="port"/>.</summary>
    public static bool CoversPort(string ports, int port)
    {
        if (ports == "any")
        {
            return true;
        }

        foreach (var part in ports.Split(',', StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split(':');

            if (bounds.Length == 1 && int.TryParse(bounds[0], CultureInfo.InvariantCulture, out var single) && single == port)
            {
                return true;
            }

            if (bounds.Length == 2 && int.TryParse(bounds[0], CultureInfo.InvariantCulture, out var low)
                && int.TryParse(bounds[1], CultureInfo.InvariantCulture, out var high) && port >= low && port <= high)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record FirewallRisk(bool RequiresConfirmation, IReadOnlyList<string> Reasons, bool BlocksRequester);

/// <summary>
/// Decides whether a change could cut off access. Risky changes are applied with an automatic
/// revert unless the admin confirms from a fresh request (which proves they can still connect).
/// </summary>
public static class FirewallRiskAssessor
{
    public static FirewallRisk Assess(FirewallPlan plan, IPAddress? requester)
    {
        var reasons = new List<string>();

        if (plan.SetEnabled == true)
        {
            reasons.Add("Turns the firewall on.");
        }

        if (plan.SetDefaultIncoming is "deny" or "reject")
        {
            reasons.Add($"Sets the default for incoming traffic to {plan.SetDefaultIncoming}.");
        }

        foreach (var removed in plan.Delete.Where(t => t.Action is "allow" or "limit"))
        {
            reasons.Add($"Removes access: {removed.Describe()}.");
        }

        var blocking = plan.Add.Where(t => t.Action is "deny" or "reject").ToList();

        foreach (var added in blocking)
        {
            reasons.Add($"Blocks traffic: {added.Describe()}.");
        }

        var blocksRequester = requester is not null && blocking.Any(t => FirewallSource.Contains(t.Source, requester));

        return new FirewallRisk(reasons.Count > 0, reasons, blocksRequester);
    }
}
