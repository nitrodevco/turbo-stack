using Grpc.Core;
using TurboManager.Agent.HostInfo;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Services;

public sealed class SystemAgentService(SystemInfoReader systemInfo) : SystemAgent.SystemAgentBase
{
    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingReply { AgentVersion = typeof(SystemAgentService).Assembly.GetName().Version?.ToString() ?? "" });

    public override Task<SystemInfo> GetSystemInfo(SystemInfoRequest request, ServerCallContext context) =>
        Task.FromResult(systemInfo.Read());
}
