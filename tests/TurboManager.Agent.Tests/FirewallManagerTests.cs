using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Firewall;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Tests;

public sealed class FirewallManagerTests : IDisposable
{
    private const string OriginalRules = "### tuple ### limit tcp 22 0.0.0.0/0 any 0.0.0.0/0 in comment=74733a737368\n";

    private readonly FakeHostRoot _host = new FakeHostRoot()
        .WithFile("etc/ufw/user.rules", OriginalRules)
        .WithFile("etc/ufw/user6.rules", "")
        .WithFile("etc/default/ufw", "DEFAULT_INPUT_POLICY=\"DROP\"\nDEFAULT_OUTPUT_POLICY=\"ACCEPT\"\n")
        .WithFile("etc/ufw/ufw.conf", "ENABLED=yes\n");

    private readonly FakeCommandRunner _commands = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FirewallManager _firewall;

    public FirewallManagerTests()
    {
        var options = Options.Create(new AgentOptions { HostRoot = _host.Path, StateDirectory = Path.Combine(_host.Path, "state") });
        var files = new UfwFiles(options);

        _commands.Handler = (file, arguments) =>
        {
            if (file == "ufw" && arguments is ["status"])
            {
                return new CommandResult(0, "Status: active\n", "");
            }

            // Simulate ufw rewriting its rules file when a rule is added.
            if (file == "ufw" && arguments[0] == "allow")
            {
                File.AppendAllText(files.UserRules, "### tuple ### allow tcp 8080 0.0.0.0/0 any 0.0.0.0/0 in comment=74733a7231\n");
            }

            return new CommandResult(0, "", "");
        };

        _firewall = CreateManager(options, files);
    }

    private FirewallManager CreateManager(IOptions<AgentOptions> options, UfwFiles files) =>
        new(_commands, files, _time, options, NullLogger<FirewallManager>.Instance);

    [Fact]
    public async Task Pending_change_left_by_a_restart_is_reverted_on_startup()
    {
        await _firewall.ApplyAsync(new FirewallChange { Add = { Spec() }, ConfirmWithinSeconds = 60 }, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_host.Path, "state", "pending-firewall-change.json")));

        // A new manager instance stands in for the agent process after a restart.
        var options = Options.Create(new AgentOptions { HostRoot = _host.Path, StateDirectory = Path.Combine(_host.Path, "state") });
        await CreateManager(options, new UfwFiles(options)).RecoverAsync(CancellationToken.None);

        Assert.Equal(OriginalRules, await File.ReadAllTextAsync(Path.Combine(_host.Path, "etc/ufw/user.rules")));
        Assert.False(File.Exists(Path.Combine(_host.Path, "state", "pending-firewall-change.json")));
    }

    private static FirewallRuleSpec Spec(string action = "allow", string ports = "8080", string source = "any", string comment = "ts:r1") =>
        new() { Action = action, Protocol = "tcp", Ports = ports, Source = source, Comment = comment };

    [Fact]
    public void Rule_specs_become_full_ufw_syntax()
    {
        Assert.Equal(["allow", "proto", "tcp", "from", "203.0.113.0/24", "to", "any", "port", "80,443", "comment", "ts:web"],
            UfwCommands.Add(Spec(ports: "80,443", source: "203.0.113.0/24", comment: "ts:web")));
        Assert.Equal(["--force", "delete", "limit", "proto", "tcp", "from", "any", "to", "any", "port", "22"],
            UfwCommands.Delete(Spec(action: "limit", ports: "22", comment: "ts:ssh")));
    }

    [Theory]
    [InlineData("allow", "tcp", "0", "any", "ts:x")]
    [InlineData("allow", "tcp", "22;reboot", "any", "ts:x")]
    [InlineData("allow", "tcp", "22", "evil.example.com", "ts:x")]
    [InlineData("allow", "tcp", "22", "any", "no-prefix")]
    [InlineData("accept", "tcp", "22", "any", "ts:x")]
    [InlineData("allow", "any", "1000:2000", "any", "ts:x")]
    [InlineData("limit", "udp", "53", "any", "ts:x")]
    public void Invalid_specs_are_rejected(string action, string protocol, string ports, string source, string comment)
    {
        var spec = new FirewallRuleSpec { Action = action, Protocol = protocol, Ports = ports, Source = source, Comment = comment };

        Assert.Throws<RpcException>(() => UfwCommands.ValidateSpec(spec));
    }

    [Fact]
    public async Task State_is_read_from_rule_files()
    {
        var state = await _firewall.GetStateAsync(CancellationToken.None);

        Assert.True(state.Enabled);
        Assert.Equal("deny", state.DefaultIncoming);
        Assert.Single(state.Rules);
        Assert.Equal("ts:ssh", state.Rules[0].Comment);
    }

    [Fact]
    public async Task Change_without_confirmation_window_is_permanent()
    {
        var result = await _firewall.ApplyAsync(new FirewallChange { Add = { Spec() } }, CancellationToken.None);

        Assert.Empty(result.TransactionId);
        Assert.Equal(2, result.State.Rules.Count);
    }

    [Fact]
    public async Task Unconfirmed_change_is_reverted_after_the_window()
    {
        var result = await _firewall.ApplyAsync(new FirewallChange { Add = { Spec() }, ConfirmWithinSeconds = 60 }, CancellationToken.None);
        Assert.NotEmpty(result.TransactionId);
        Assert.Contains("8080", await File.ReadAllTextAsync(Path.Combine(_host.Path, "etc/ufw/user.rules")));

        _time.Advance(TimeSpan.FromSeconds(61));
        await WaitForStatusAsync(result.TransactionId, "reverted");

        Assert.Equal(OriginalRules, await File.ReadAllTextAsync(Path.Combine(_host.Path, "etc/ufw/user.rules")));
        Assert.Contains("ufw reload", _commands.Calls);
    }

    [Fact]
    public async Task Confirmed_change_is_kept()
    {
        var result = await _firewall.ApplyAsync(new FirewallChange { Add = { Spec() }, ConfirmWithinSeconds = 60 }, CancellationToken.None);

        var status = await _firewall.ConfirmAsync(result.TransactionId, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("confirmed", status.Status);
        Assert.Equal("confirmed", _firewall.GetTransaction(result.TransactionId).Status);
        Assert.Contains("8080", await File.ReadAllTextAsync(Path.Combine(_host.Path, "etc/ufw/user.rules")));
    }

    [Fact]
    public async Task Only_one_change_can_await_confirmation()
    {
        await _firewall.ApplyAsync(new FirewallChange { Add = { Spec() }, ConfirmWithinSeconds = 60 }, CancellationToken.None);

        var second = await Assert.ThrowsAsync<RpcException>(() =>
            _firewall.ApplyAsync(new FirewallChange { Add = { Spec(ports: "9090", comment: "ts:r2") }, ConfirmWithinSeconds = 60 }, CancellationToken.None));

        Assert.Equal(StatusCode.FailedPrecondition, second.StatusCode);
    }

    [Fact]
    public async Task Failed_command_rolls_back_immediately()
    {
        var inner = _commands.Handler;
        _commands.Handler = (file, arguments) => file == "ufw" && arguments[0] == "deny"
            ? new CommandResult(1, "", "ERROR: Bad port")
            : inner(file, arguments);

        var error = await Assert.ThrowsAsync<RpcException>(() => _firewall.ApplyAsync(
            new FirewallChange { Add = { Spec(), Spec(action: "deny", ports: "9", comment: "ts:r2") } }, CancellationToken.None));

        Assert.Equal(StatusCode.Aborted, error.StatusCode);
        Assert.Equal(OriginalRules, await File.ReadAllTextAsync(Path.Combine(_host.Path, "etc/ufw/user.rules")));
    }

    private async Task WaitForStatusAsync(string id, string expected)
    {
        for (var i = 0; i < 100 && _firewall.GetTransaction(id).Status != expected; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(expected, _firewall.GetTransaction(id).Status);
    }

    public void Dispose() => _host.Dispose();
}
