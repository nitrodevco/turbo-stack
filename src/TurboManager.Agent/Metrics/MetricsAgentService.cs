using Grpc.Core;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Metrics;

public sealed class MetricsAgentService(MetricsSampler sampler) : MetricsAgent.MetricsAgentBase
{
    public override Task<MetricsSnapshot> GetMetrics(MetricsRequest request, ServerCallContext context) =>
        Task.FromResult(sampler.Latest);
}
