using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Update;

public sealed class UpdateAgentService(UpdateManager updates) : UpdateAgent.UpdateAgentBase
{
    private static readonly Empty Done = new();

    public override Task<UpdateInfo> CheckUpdate(Empty request, ServerCallContext context) => updates.CheckAsync(context.CancellationToken);

    public override async Task<Empty> StartUpdate(UpdateRequest request, ServerCallContext context)
    {
        await updates.StartAsync(request, context.CancellationToken);

        return Done;
    }

    public override Task<UpdateStatus> GetUpdateStatus(Empty request, ServerCallContext context) => updates.StatusAsync(context.CancellationToken);

    public override async Task<Empty> SetUpdateChannel(UpdateChannel request, ServerCallContext context)
    {
        await updates.SetChannelAsync(request.Channel, context.CancellationToken);

        return Done;
    }
}
