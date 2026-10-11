using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Grpc.Core;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Endpoints;

public static class ServicesEndpoints
{
    public sealed record LogQuery(string? Unit, int? Priority, long? Since, long? Until, string? Grep, int? Limit);

    public static void MapServicesEndpoints(this IEndpointRouteBuilder app)
    {
        var services = app.MapGroup("/api/services").WithAgentErrors();

        services.MapGet("/", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok((await agent.Services.ListServicesAsync(new ListServicesRequest(), cancellationToken: cancellationToken)).Services))
            .RequirePermission(Permissions.ServicesView);

        services.MapPost("/{name}/{action}", async (string name, string action, HttpContext http, PanelDbContext db,
            AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
        {
            var serviceAction = action switch
            {
                "start" => ServiceAction.Start,
                "stop" => ServiceAction.Stop,
                "restart" => ServiceAction.Restart,
                "reload" => ServiceAction.Reload,
                "enable" => ServiceAction.Enable,
                "disable" => ServiceAction.Disable,
                _ => ServiceAction.Unspecified,
            };

            if (serviceAction == ServiceAction.Unspecified)
            {
                return Results.NotFound();
            }

            var unit = await agent.Services.ControlServiceAsync(new ControlServiceRequest { Name = name, Action = serviceAction },
                cancellationToken: cancellationToken);

            audit.Record($"service.{action}", await CurrentUser.GetAsync(http, db), target: name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(unit);
        }).RequirePermission(Permissions.ServicesControl);

        var logs = app.MapGroup("/api/logs").WithAgentErrors().RequirePermission(Permissions.LogsView);

        logs.MapGet("/", async ([AsParameters] LogQuery query, AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            var reply = await agent.Logs.QueryJournalAsync(ToJournalQuery(query, defaultLimit: 300), cancellationToken: cancellationToken);

            return Results.Ok(reply.Entries);
        });

        // Live tail as Server-Sent Events; the agent's journalctl stops when the browser disconnects.
        logs.MapGet("/stream", ([AsParameters] LogQuery query, AgentChannelFactory agent, HttpContext http) =>
        {
            http.Response.Headers["X-Accel-Buffering"] = "no";

            return TypedResults.ServerSentEvents(Follow(agent, ToJournalQuery(query, defaultLimit: 100), http.RequestAborted));
        });
    }

    private static async IAsyncEnumerable<SseItem<JournalEntry>> Follow(AgentChannelFactory agent, JournalQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = agent.Logs.FollowJournal(query, cancellationToken: cancellationToken);
        var entries = call.ResponseStream.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            try
            {
                if (!await entries.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled || cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            yield return new SseItem<JournalEntry>(entries.Current, "entry");
        }
    }

    private static JournalQuery ToJournalQuery(LogQuery query, int defaultLimit)
    {
        var journal = new JournalQuery
        {
            Unit = query.Unit ?? "",
            SinceUnixMs = query.Since ?? 0,
            UntilUnixMs = query.Until ?? 0,
            Grep = query.Grep ?? "",
            Limit = Math.Clamp(query.Limit ?? defaultLimit, 1, 5000),
        };

        if (query.Priority is { } priority)
        {
            journal.MaxPriority = Math.Clamp(priority, 0, 7);
        }

        return journal;
    }
}
