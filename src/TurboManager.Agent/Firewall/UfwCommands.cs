using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Firewall;

/// <summary>Turns validated rule specs into ufw argument lists (full syntax, no shell).</summary>
public static class UfwCommands
{
    public static FirewallRuleSpec ValidateSpec(FirewallRuleSpec spec)
    {
        Validate.OneOf(spec.Action, "action", "allow", "deny", "reject", "limit");
        Validate.OneOf(spec.Protocol, "protocol", "tcp", "udp", "any");
        Validate.Ports(spec.Ports);
        Validate.Source(spec.Source);
        Validate.RuleComment(spec.Comment);

        if (spec.Protocol == "any" && (spec.Ports.Contains(',') || spec.Ports.Contains(':')))
        {
            throw Validate.Invalid("Port ranges and lists need a protocol (tcp or udp).");
        }

        if (spec.Action == "limit" && spec.Protocol != "tcp")
        {
            throw Validate.Invalid("Rate-limited rules must use tcp.");
        }

        return spec;
    }

    public static List<string> Add(FirewallRuleSpec spec)
    {
        var arguments = RuleArguments(spec);
        arguments.AddRange(["comment", spec.Comment]);

        return arguments;
    }

    /// <summary>ufw matches a rule for deletion by its spec; the comment isn't part of the match.</summary>
    public static List<string> Delete(FirewallRuleSpec spec)
    {
        List<string> arguments = ["--force", "delete"];
        arguments.AddRange(RuleArguments(spec));

        return arguments;
    }

    public static List<string> SetDefaultIncoming(string policy) =>
        ["default", Validate.OneOf(policy, "default policy", "allow", "deny", "reject"), "incoming"];

    private static List<string> RuleArguments(FirewallRuleSpec spec)
    {
        List<string> arguments = [spec.Action];

        if (spec.Protocol != "any")
        {
            arguments.AddRange(["proto", spec.Protocol]);
        }

        arguments.AddRange(["from", spec.Source, "to", "any", "port", spec.Ports]);

        return arguments;
    }
}
