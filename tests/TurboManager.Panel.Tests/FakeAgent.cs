using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Firewall;

namespace TurboManager.Panel.Tests;

/// <summary>In-memory firewall that behaves like the agent's (rules keyed by spec, pending transactions).</summary>
public sealed class FakeFirewallState
{
    public bool Enabled { get; set; } = true;

    public string DefaultIncoming { get; set; } = "deny";

    public List<FirewallRuleSpec> Rules { get; } =
    [
        new() { Action = "limit", Protocol = "tcp", Ports = "22", Source = "any", Comment = "tm:ssh" },
        new() { Action = "allow", Protocol = "tcp", Ports = "80", Source = "any", Comment = "tm:http" },
        new() { Action = "allow", Protocol = "tcp", Ports = "443", Source = "any", Comment = "tm:https" },
        new() { Action = "allow", Protocol = "tcp", Ports = "8443", Source = "any", Comment = "tm:setup" },
        new() { Action = "allow", Protocol = "udp", Ports = "51820", Source = "any", Comment = "" },
    ];

    public List<FirewallChange> Changes { get; } = [];

    public Dictionary<string, string> Transactions { get; } = [];

    public FirewallState ToState()
    {
        var state = new FirewallState { Available = true, Enabled = Enabled, DefaultIncoming = DefaultIncoming, DefaultOutgoing = "allow" };
        state.Rules.AddRange(Rules.Select(r => new FirewallRule
        {
            Action = r.Action,
            Protocol = r.Protocol,
            Ports = r.Ports,
            Source = r.Source,
            Comment = r.Comment,
            Direction = "in",
        }));

        return state;
    }

    public static bool Same(FirewallRuleSpec a, FirewallRuleSpec b) =>
        a.Action == b.Action && a.Protocol == b.Protocol && a.Ports == b.Ports
        && FirewallSource.Normalize(a.Source) == FirewallSource.Normalize(b.Source);
}

public sealed class FakeFirewallAgent(FakeFirewallState state) : FirewallAgent.FirewallAgentBase
{
    public override Task<FirewallState> GetFirewallState(Empty request, ServerCallContext context) => Task.FromResult(state.ToState());

    public override Task<FirewallChangeResult> ApplyFirewallChange(FirewallChange request, ServerCallContext context)
    {
        lock (state)
        {
            state.Changes.Add(request);
            state.Rules.RemoveAll(r => request.Delete.Any(d => FakeFirewallState.Same(r, d)));
            state.Rules.AddRange(request.Add);

            if (request.HasSetEnabled)
            {
                state.Enabled = request.SetEnabled;
            }

            if (request.SetDefaultIncoming.Length > 0)
            {
                state.DefaultIncoming = request.SetDefaultIncoming;
            }

            var result = new FirewallChangeResult { State = state.ToState() };

            if (request.ConfirmWithinSeconds > 0)
            {
                result.TransactionId = $"tx{state.Changes.Count}";
                result.RevertAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(request.ConfirmWithinSeconds).ToUnixTimeMilliseconds();
                state.Transactions[result.TransactionId] = "pending";
            }

            return Task.FromResult(result);
        }
    }

    public override Task<FirewallTransactionStatus> ConfirmFirewallChange(FirewallTransactionRef request, ServerCallContext context)
    {
        lock (state)
        {
            if (state.Transactions.GetValueOrDefault(request.TransactionId) == "pending")
            {
                state.Transactions[request.TransactionId] = "confirmed";
            }

            return GetFirewallTransaction(request, context);
        }
    }

    public override Task<FirewallTransactionStatus> GetFirewallTransaction(FirewallTransactionRef request, ServerCallContext context) =>
        Task.FromResult(new FirewallTransactionStatus
        {
            TransactionId = request.TransactionId,
            Status = state.Transactions.GetValueOrDefault(request.TransactionId, "unknown"),
        });

    public override Task<ListeningSockets> ListListeningSockets(Empty request, ServerCallContext context)
    {
        var reply = new ListeningSockets();
        reply.Sockets.Add(new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 22, Process = "sshd" });
        reply.Sockets.Add(new ListeningSocket { Protocol = "tcp", Address = "127.0.0.1", Port = 7800, Process = "tm-panel" });
        reply.Sockets.Add(new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 3306, Process = "mysqld" });

        return Task.FromResult(reply);
    }
}

public sealed class FakeServicesAgent : ServicesAgent.ServicesAgentBase
{
    public List<ControlServiceRequest> Controls { get; } = [];

    public override Task<ListServicesReply> ListServices(ListServicesRequest request, ServerCallContext context)
    {
        var reply = new ListServicesReply();
        reply.Services.Add(new ServiceUnit { Name = "nginx.service", ActiveState = "active", SubState = "running", UnitFileState = "enabled" });

        return Task.FromResult(reply);
    }

    public override Task<ServiceUnit> ControlService(ControlServiceRequest request, ServerCallContext context)
    {
        Controls.Add(request);

        return request.Name == "missing.service"
            ? throw new RpcException(new Status(StatusCode.NotFound, "missing.service not found"))
            : Task.FromResult(new ServiceUnit { Name = request.Name, ActiveState = "active" });
    }
}

public sealed class FakePackagesAgent : PackagesAgent.PackagesAgentBase
{
    public override async Task UpgradePackages(UpgradeRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        await responseStream.WriteAsync(new OutputLine { Line = "Reading package lists..." });
        await responseStream.WriteAsync(new OutputLine { Line = $"Upgrading {request.Mode}" });
        await responseStream.WriteAsync(new OutputLine { Finished = true, ExitCode = 0 });
    }
}

/// <summary>A gRPC server on a unix socket implementing the agent services the panel tests need.</summary>
public sealed class FakeAgent : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FakeFirewallState Firewall { get; } = new();

    public FakeServicesAgent Services { get; } = new();

    private FakeAgent(string socketPath)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(Firewall);
        builder.Services.AddSingleton(Services);
        _app = builder.Build();
        _app.MapGrpcService<FakeFirewallAgent>();
        _app.MapGrpcService<FakeServicesAgent>();
        _app.MapGrpcService<FakePackagesAgent>();
    }

    public static async Task<FakeAgent> StartAsync(string socketPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        var agent = new FakeAgent(socketPath);
        await agent._app.StartAsync();

        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
