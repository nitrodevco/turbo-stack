using TurboManager.Contracts.Agent;
using TurboManager.Core.Firewall;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Firewall;

/// <summary>One ufw rule as the panel reasons about it (address families merged).</summary>
public sealed record RuleTuple(string Action, string Protocol, string Ports, string Source, string Comment)
{
    public FirewallRuleSpec ToSpec() => new() { Action = Action, Protocol = Protocol, Ports = Ports, Source = Source, Comment = Comment };

    public string Describe() => $"{Action} {Protocol}/{Ports} from {Source}";
}

public sealed record FirewallPlan(IReadOnlyList<RuleTuple> Add, IReadOnlyList<RuleTuple> Delete, bool? SetEnabled, string? SetDefaultIncoming)
{
    public bool IsEmpty => Add.Count == 0 && Delete.Count == 0 && SetEnabled is null && SetDefaultIncoming is null;
}

/// <summary>
/// Compares the panel's desired rules with ufw's actual rules. Only rules tagged "ts:*" are
/// considered ours; anything else in ufw is external and never touched.
/// </summary>
public static class FirewallPlanner
{
    public const string CommentPrefix = "ts:";

    public static string CommentFor(ManagedFirewallRule rule) => CommentPrefix + rule.Key;

    /// <summary>Expands enabled rules into tuples (one per source; IP lists fan out). IpList entries must be loaded.</summary>
    public static List<RuleTuple> Desired(IEnumerable<ManagedFirewallRule> rules)
    {
        var tuples = new List<RuleTuple>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            IEnumerable<string> sources = rule.SourceKind switch
            {
                FirewallSourceKind.Any => [FirewallSource.Any],
                FirewallSourceKind.Address => [rule.SourceAddress ?? FirewallSource.Any],
                FirewallSourceKind.IpList => rule.IpList?.Entries.Select(e => e.Source) ?? [],
                _ => [],
            };

            foreach (var source in sources.Select(FirewallSource.Normalize).Distinct())
            {
                tuples.Add(new RuleTuple(rule.Action, rule.Protocol, NormalizePorts(rule.Ports), source, CommentFor(rule)));
            }
        }

        return tuples.Distinct().ToList();
    }

    /// <summary>Panel-owned rules currently in ufw, with v4/v6 duplicates of "any" rules merged.</summary>
    public static HashSet<RuleTuple> ActualManaged(FirewallState state) =>
        state.Rules
            .Where(r => r.Comment.StartsWith(CommentPrefix, StringComparison.Ordinal) && r.Direction is "in" or "")
            .Select(r => new RuleTuple(r.Action, r.Protocol, NormalizePorts(r.Ports), FirewallSource.Normalize(r.Source), r.Comment))
            .ToHashSet();

    public static FirewallPlan Plan(IEnumerable<ManagedFirewallRule> rules, FirewallSettings settings, FirewallState state)
    {
        var desired = Desired(rules).ToHashSet();
        var actual = ActualManaged(state);

        return new FirewallPlan(
            desired.Where(t => !actual.Contains(t)).ToList(),
            actual.Where(t => !desired.Contains(t)).ToList(),
            settings.Enabled != state.Enabled ? settings.Enabled : null,
            settings.DefaultIncoming != state.DefaultIncoming ? settings.DefaultIncoming : null);
    }

    /// <summary>"80, 443" → "80,443"; ufw stores port lists without spaces.</summary>
    public static string NormalizePorts(string ports) =>
        string.Join(',', ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
