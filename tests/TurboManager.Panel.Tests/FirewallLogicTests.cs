using System.Net;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.Data;
using TurboManager.Panel.Firewall;

namespace TurboManager.Panel.Tests;

public sealed class FirewallLogicTests
{
    private static ManagedFirewallRule Rule(string key, string action = "allow", string ports = "80", string? address = null, IpList? list = null) => new()
    {
        Key = key,
        Name = key,
        Action = action,
        Protocol = "tcp",
        Ports = ports,
        SourceKind = list is not null ? FirewallSourceKind.IpList : address is null ? FirewallSourceKind.Any : FirewallSourceKind.Address,
        SourceAddress = address,
        IpList = list,
    };

    private static FirewallState State(params (string Action, string Ports, string Source, string Comment, bool Ipv6)[] rules)
    {
        var state = new FirewallState { Enabled = true, DefaultIncoming = "deny" };
        state.Rules.AddRange(rules.Select(r => new FirewallRule
        {
            Action = r.Action, Protocol = "tcp", Ports = r.Ports, Source = r.Source, Comment = r.Comment, Ipv6 = r.Ipv6, Direction = "in",
        }));

        return state;
    }

    [Fact]
    public void Plan_adds_missing_and_removes_orphaned_managed_rules_only()
    {
        var rules = new[] { Rule("ssh", "limit", "22"), Rule("web", ports: "80, 443") };
        var state = State(
            ("limit", "22", "0.0.0.0/0", "ts:ssh", false),
            ("limit", "22", "::/0", "ts:ssh", true),
            ("allow", "9000", "0.0.0.0/0", "ts:old", false),
            ("allow", "51820", "0.0.0.0/0", "", false));

        var plan = FirewallPlanner.Plan(rules, new FirewallSettings(), state);

        Assert.Equal([new RuleTuple("allow", "tcp", "80,443", "any", "ts:web")], plan.Add);
        Assert.Equal([new RuleTuple("allow", "tcp", "9000", "any", "ts:old")], plan.Delete);
        Assert.Null(plan.SetEnabled);
        Assert.Null(plan.SetDefaultIncoming);
    }

    [Fact]
    public void Ip_lists_expand_to_one_rule_per_entry_and_disabled_rules_are_omitted()
    {
        var staff = new IpList { Name = "Staff", Entries = [new() { Source = "203.0.113.7/32" }, new() { Source = "198.51.100.0/24" }] };
        var disabled = Rule("off", ports: "25");
        disabled.Enabled = false;

        var desired = FirewallPlanner.Desired([Rule("db", ports: "3306", list: staff), disabled]);

        Assert.Equal(2, desired.Count);
        Assert.Contains(new RuleTuple("allow", "tcp", "3306", "203.0.113.7", "ts:db"), desired);
        Assert.Contains(new RuleTuple("allow", "tcp", "3306", "198.51.100.0/24", "ts:db"), desired);
    }

    [Fact]
    public void Plan_includes_setting_changes()
    {
        var plan = FirewallPlanner.Plan([], new FirewallSettings { Enabled = false, DefaultIncoming = "reject" }, State());

        Assert.False(plan.SetEnabled);
        Assert.Equal("reject", plan.SetDefaultIncoming);
    }

    [Fact]
    public void Adding_allow_rules_is_not_risky()
    {
        var plan = new FirewallPlan([new RuleTuple("allow", "tcp", "8080", "any", "ts:a")], [], null, null);

        Assert.False(FirewallRiskAssessor.Assess(plan, IPAddress.Parse("203.0.113.7")).RequiresConfirmation);
    }

    [Fact]
    public void Removing_access_or_blocking_requires_confirmation()
    {
        var plan = new FirewallPlan(
            [new RuleTuple("deny", "tcp", "22", "198.51.100.0/24", "ts:b")],
            [new RuleTuple("limit", "tcp", "22", "any", "ts:ssh")],
            true, "deny");

        var risk = FirewallRiskAssessor.Assess(plan, IPAddress.Parse("203.0.113.7"));

        Assert.True(risk.RequiresConfirmation);
        Assert.Equal(4, risk.Reasons.Count);
        Assert.False(risk.BlocksRequester);
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("203.0.113.0/24", true)]
    [InlineData("any", true)]
    [InlineData("198.51.100.1", false)]
    public void Blocking_the_requesters_own_ip_is_flagged(string source, bool blocks)
    {
        var plan = new FirewallPlan([new RuleTuple("deny", "tcp", "443", source, "ts:x")], [], null, null);

        Assert.Equal(blocks, FirewallRiskAssessor.Assess(plan, IPAddress.Parse("::ffff:203.0.113.7")).BlocksRequester);
    }

    [Theory]
    [InlineData("22", 22, true)]
    [InlineData("80,443", 443, true)]
    [InlineData("6000:6010", 6005, true)]
    [InlineData("6000:6010", 6011, false)]
    [InlineData("any", 1, true)]
    public void Port_coverage(string ports, int port, bool covered) =>
        Assert.Equal(covered, FirewallRuleValidator.CoversPort(ports, port));

    [Theory]
    [InlineData("allow", "tcp", "22", FirewallSourceKind.Any, null, true)]
    [InlineData("allow", "tcp", "0", FirewallSourceKind.Any, null, false)]
    [InlineData("allow", "tcp", "100:50", FirewallSourceKind.Any, null, false)]
    [InlineData("allow", "any", "80,443", FirewallSourceKind.Any, null, false)]
    [InlineData("limit", "udp", "53", FirewallSourceKind.Any, null, false)]
    [InlineData("allow", "tcp", "3306", FirewallSourceKind.Address, "10.0.0.0/8", true)]
    [InlineData("allow", "tcp", "3306", FirewallSourceKind.Address, "office.example.com", false)]
    public void Rule_validation(string action, string protocol, string ports, FirewallSourceKind kind, string? address, bool valid) =>
        Assert.Equal(valid, FirewallRuleValidator.Validate("Rule", action, protocol, ports, kind, address) is null);

    [Fact]
    public void Exposure_audit_classifies_sockets()
    {
        var state = State(("allow", "80", "any", "ts:http", false), ("allow", "3306", "10.0.0.0/8", "ts:db", false));
        var sockets = new[]
        {
            new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 80 },
            new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 3306 },
            new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 11111 },
            new ListeningSocket { Protocol = "tcp", Address = "127.0.0.1", Port = 7800 },
        };

        var items = ExposureAudit.Evaluate(sockets, state).ToDictionary(i => i.Port);

        Assert.Equal((Exposure.Public, ExposureRisk.Ok), (items[80].Exposure, items[80].Risk));
        Assert.Equal(Exposure.Restricted, items[3306].Exposure);
        Assert.Equal(Exposure.Blocked, items[11111].Exposure);
        Assert.Equal(Exposure.Local, items[7800].Exposure);
    }

    [Fact]
    public void Exposure_audit_flags_sensitive_ports_when_the_firewall_is_off()
    {
        var state = State();
        state.Enabled = false;

        var item = ExposureAudit.Evaluate([new ListeningSocket { Protocol = "tcp", Address = "::", Port = 11111 }], state).Single();

        Assert.Equal((Exposure.Public, ExposureRisk.Danger), (item.Exposure, item.Risk));
    }
}
