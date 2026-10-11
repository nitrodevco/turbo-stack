using Grpc.Core;

namespace TurboManager.Panel.AgentClient;

/// <summary>Turns agent (gRPC) failures into HTTP problem responses with the agent's message.</summary>
public sealed class AgentErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (RpcException e) when (e.StatusCode != StatusCode.Cancelled)
        {
            return Results.Problem(e.Status.Detail, statusCode: e.StatusCode switch
            {
                StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
                StatusCode.NotFound => StatusCodes.Status404NotFound,
                StatusCode.AlreadyExists or StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
                StatusCode.Aborted => StatusCodes.Status422UnprocessableEntity,
                StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
                StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status502BadGateway,
            }, title: e.StatusCode == StatusCode.Unavailable ? "The system agent is not reachable." : null);
        }
        catch (IOException e)
        {
            return Results.Problem($"The system agent is not reachable: {e.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

public static class AgentErrorFilterExtensions
{
    public static RouteGroupBuilder WithAgentErrors(this RouteGroupBuilder group) => group.AddEndpointFilter<AgentErrorFilter>();
}
