using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Updates;

namespace TurboManager.Panel.Endpoints;

public sealed record PanelUpdateRequest(string? Version);

public sealed record PanelChannelRequest(string? Channel);

public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/panel-update").WithAgentErrors().RequirePermission(Permissions.SystemView);
        var manage = app.MapGroup("/api/panel-update").WithAgentErrors().RequirePermission(Permissions.PanelUpdate);

        view.MapGet("", async (PanelUpdateService updates, CancellationToken cancellationToken) =>
        {
            var state = await updates.GetAsync(cancellationToken);
            var status = await updates.StatusAsync(cancellationToken);

            return Results.Ok(new
            {
                state.CurrentVersion,
                state.Channel,
                state.LatestVersion,
                state.Notes,
                state.Published,
                state.Available,
                state.Error,
                state.CheckedAt,
                running = status.Running,
                last = state.LastState is null ? null : new { state = state.LastState, from = state.LastFrom, to = state.LastTo, message = state.LastMessage, at = state.LastAt },
            });
        });

        manage.MapPost("/check", async (PanelUpdateService updates, CancellationToken cancellationToken) =>
        {
            var state = await updates.CheckAsync(cancellationToken);

            return Results.Ok(new { state.LatestVersion, state.Available, state.Error });
        });

        manage.MapPost("/apply", async (PanelUpdateRequest request, HttpContext http, PanelDbContext db, PanelUpdateService updates,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Version))
            {
                return Results.Problem("Which version?", statusCode: StatusCodes.Status400BadRequest);
            }

            await updates.StartAsync(request.Version.Trim(), await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.Accepted();
        }).RequireSudo();

        manage.MapPut("/channel", async (PanelChannelRequest request, HttpContext http, PanelDbContext db, PanelUpdateService updates,
            CancellationToken cancellationToken) =>
        {
            var state = await updates.SetChannelAsync((request.Channel ?? "").Trim(), await CurrentUser.GetAsync(http, db), cancellationToken);

            return Results.Ok(new { state.Channel, state.LatestVersion, state.Available, state.Error });
        });
    }
}
