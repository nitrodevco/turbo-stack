using Grpc.Core;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Endpoints;

public static class PackagesEndpoints
{
    public sealed record UpgradeRequestBody(string? Mode, List<string>? Names);

    public sealed record RebootRequestBody(int DelayMinutes, bool Cancel);

    public static void MapPackagesEndpoints(this IEndpointRouteBuilder app)
    {
        var packages = app.MapGroup("/api/packages").WithAgentErrors();

        packages.MapGet("/", async (bool? refresh, AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.Packages.ListUpgradableAsync(new ListUpgradableRequest { Refresh = refresh ?? false },
                deadline: DateTime.UtcNow.AddMinutes(6), cancellationToken: cancellationToken)))
            .RequirePermission(Permissions.PackagesView);

        packages.MapPost("/upgrade", async (UpgradeRequestBody body, HttpContext http, PanelDbContext db, JobRunner jobs, AuditLog audit) =>
        {
            var request = new UpgradeRequest
            {
                Mode = body.Mode switch
                {
                    "all" => UpgradeMode.All,
                    "security" => UpgradeMode.Security,
                    "selected" => UpgradeMode.Selected,
                    _ => UpgradeMode.Unspecified,
                },
            };
            request.Names.AddRange(body.Names ?? []);

            if (request.Mode == UpgradeMode.Unspecified || (request.Mode == UpgradeMode.Selected && request.Names.Count == 0))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mode"] = ["Choose all, security, or selected packages."] });
            }

            var user = await CurrentUser.GetAsync(http, db);
            var title = request.Mode switch
            {
                UpgradeMode.All => "Upgrade all packages",
                UpgradeMode.Security => "Install security updates",
                _ => $"Upgrade {string.Join(", ", request.Names)}",
            };

            try
            {
                var job = await jobs.StartAsync("packages", title, user, context => RunUpgradeAsync(context, request));

                audit.Record("packages.upgrade_started", user, details: title);
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }
            catch (JobConflictException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).RequirePermission(Permissions.PackagesManage);

        var reboot = app.MapGroup("/api/server/reboot").WithAgentErrors();

        reboot.MapGet("/", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok(await agent.Packages.GetRebootStatusAsync(new RebootRequest(), cancellationToken: cancellationToken)))
            .RequirePermission(Permissions.SystemView);

        reboot.MapPost("/", async (RebootRequestBody body, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            // Audit first: an immediate reboot may stop the panel before it can write anything else.
            audit.Record(body.Cancel ? "server.reboot_cancelled" : "server.reboot_scheduled", await CurrentUser.GetAsync(http, db),
                details: body.Cancel ? null : $"in {body.DelayMinutes} minutes");
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(await agent.Packages.ScheduleRebootAsync(new RebootRequest { DelayMinutes = body.DelayMinutes, Cancel = body.Cancel },
                cancellationToken: cancellationToken));
        }).RequirePermission(Permissions.ServerReboot);
    }

    private static async Task<bool> RunUpgradeAsync(JobContext context, UpgradeRequest request)
    {
        var agent = context.Services.GetRequiredService<AgentChannelFactory>();
        using var call = agent.Packages.UpgradePackages(request, cancellationToken: context.CancellationToken);
        var exitCode = -1;

        await foreach (var line in call.ResponseStream.ReadAllAsync(context.CancellationToken))
        {
            if (line.Finished)
            {
                exitCode = line.ExitCode;
            }
            else
            {
                context.Log(line.Line);
            }
        }

        context.Log(exitCode == 0 ? "Finished successfully." : $"Finished with exit code {exitCode}.");

        return exitCode == 0;
    }
}
