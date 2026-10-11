using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Agent.Commands;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Firewall;

public sealed class FirewallAgentService(FirewallManager firewall, Fail2banManager fail2ban, ICommandRunner commands)
    : FirewallAgent.FirewallAgentBase
{
    public override Task<FirewallState> GetFirewallState(Empty request, ServerCallContext context) =>
        firewall.GetStateAsync(context.CancellationToken);

    public override Task<FirewallChangeResult> ApplyFirewallChange(FirewallChange request, ServerCallContext context) =>
        firewall.ApplyAsync(request, context.CancellationToken);

    public override Task<FirewallTransactionStatus> ConfirmFirewallChange(FirewallTransactionRef request, ServerCallContext context) =>
        firewall.ConfirmAsync(request.TransactionId, context.CancellationToken);

    public override Task<FirewallTransactionStatus> GetFirewallTransaction(FirewallTransactionRef request, ServerCallContext context) =>
        Task.FromResult(firewall.GetTransaction(request.TransactionId));

    public override async Task<ListeningSockets> ListListeningSockets(Empty request, ServerCallContext context)
    {
        var result = await commands.RunCheckedAsync("ss", ["-H", "-tulpn"], cancellationToken: context.CancellationToken);
        var reply = new ListeningSockets();
        reply.Sockets.AddRange(SsParser.Parse(result.StdOut));

        return reply;
    }

    public override Task<Fail2banStatus> GetFail2banStatus(Empty request, ServerCallContext context) =>
        fail2ban.GetStatusAsync(context.CancellationToken);

    public override Task<Fail2banStatus> Fail2banBan(Fail2banBanRequest request, ServerCallContext context) =>
        fail2ban.BanAsync(request, context.CancellationToken);

    public override Task<Fail2banStatus> UpdateFail2banJail(Fail2banJailSettings request, ServerCallContext context) =>
        fail2ban.UpdateJailAsync(request, context.CancellationToken);
}
