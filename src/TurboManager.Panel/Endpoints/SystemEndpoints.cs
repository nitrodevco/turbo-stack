using Grpc.Core;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;

namespace TurboManager.Panel.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            version = typeof(SystemEndpoints).Assembly.GetName().Version?.ToString(3),
        }));

        var group = app.MapGroup("/api/system").RequireAuthorization();

        group.MapGet("/info", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            try
            {
                var info = await agent.System.GetSystemInfoAsync(new SystemInfoRequest(), cancellationToken: cancellationToken);

                return Results.Ok(info);
            }
            catch (RpcException e)
            {
                return AgentUnavailable(e);
            }
            catch (IOException e)
            {
                return AgentUnavailable(e);
            }
        });
    }

    private static IResult AgentUnavailable(Exception e) =>
        Results.Problem($"The system agent is not reachable: {e.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
}
