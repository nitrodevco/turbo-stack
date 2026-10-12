using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Backups;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;
using BackupTarget = TurboManager.Panel.Data.BackupTarget;

namespace TurboManager.Panel.Endpoints;

public sealed record HostKeyConfirmation(string? HostKey);

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/backups").WithAgentErrors().RequirePermission(Permissions.BackupsView);
        var manage = app.MapGroup("/api/backups").WithAgentErrors().RequirePermission(Permissions.BackupsManage);

        view.MapGet("", async (PanelDbContext db) => Results.Ok(new
        {
            targets = (await db.BackupTargets.OrderBy(t => t.Name).ToListAsync()).Select(Describe),
            plans = await db.BackupPlans.OrderBy(p => p.Name).ToListAsync(),
        }));

        manage.MapPost("/targets", async (BackupTargetRequest request, HttpContext http, PanelDbContext db, BackupService backups, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (BackupService.ValidateTarget(request) is { } error)
            {
                return Invalid(error);
            }

            if (await db.BackupTargets.AnyAsync(t => t.Name == request.Name!.Trim(), cancellationToken))
            {
                return Invalid($"There is already a target called {request.Name}.");
            }

            var (target, password) = await backups.CreateTargetAsync(request, cancellationToken);
            audit.Record("backups.target_added", await CurrentUser.GetAsync(http, db), target.Name, $"{target.Kind} {target.Path}");
            await db.SaveChangesAsync(cancellationToken);

            // The password is shown once: without it, the backups can't be read anywhere else.
            return Results.Ok(new { target = Describe(target), password });
        });

        manage.MapGet("/targets/{id:int}/host-key", async (int id, PanelDbContext db, AgentChannelFactory agent, CancellationToken cancellationToken) =>
            await db.BackupTargets.FindAsync([id], cancellationToken) is { Kind: BackupTargetKind.Sftp } target
                ? Results.Ok(await agent.Backups.ScanHostKeyAsync(new HostKeyRequest { Host = target.Endpoint, Port = target.Port }, cancellationToken: cancellationToken))
                : Results.NotFound());

        manage.MapPost("/targets/{id:int}/host-key", async (int id, HostKeyConfirmation request, HttpContext http, PanelDbContext db, BackupService backups,
            AuditLog audit, CancellationToken cancellationToken) =>
        {
            if (await db.BackupTargets.FindAsync([id], cancellationToken) is not { Kind: BackupTargetKind.Sftp } target)
            {
                return Results.NotFound();
            }

            target.HostKey = (request.HostKey ?? "").Trim();
            audit.Record("backups.host_key_confirmed", await CurrentUser.GetAsync(http, db), target.Name, target.HostKey);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(Describe(await backups.CheckAsync(target, cancellationToken)));
        });

        manage.MapPost("/targets/{id:int}/check", async (int id, PanelDbContext db, BackupService backups, CancellationToken cancellationToken) =>
            await db.BackupTargets.FindAsync([id], cancellationToken) is { } target ? Results.Ok(Describe(await backups.CheckAsync(target, cancellationToken))) : Results.NotFound());

        manage.MapGet("/targets/{id:int}/password", async (int id, HttpContext http, PanelDbContext db, BackupService backups, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (await db.BackupTargets.FindAsync([id], cancellationToken) is not { } target)
            {
                return Results.NotFound();
            }

            audit.Record("backups.password_shown", await CurrentUser.GetAsync(http, db), target.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new { password = backups.Unprotect(target.PasswordProtected) });
        }).RequireSudo();

        manage.MapDelete("/targets/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit, CancellationToken cancellationToken) =>
        {
            if (await db.BackupTargets.FindAsync([id], cancellationToken) is not { } target)
            {
                return Results.NotFound();
            }

            if (await db.BackupPlans.AnyAsync(p => p.TargetId == id, cancellationToken))
            {
                return Invalid("Plans still back up to this target; change or delete them first.");
            }

            // Only the panel forgets it: the repository and its snapshots stay where they are.
            db.BackupTargets.Remove(target);
            audit.Record("backups.target_removed", await CurrentUser.GetAsync(http, db), target.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        }).RequireSudo();

        manage.MapPost("/plans", async (BackupPlanRequest request, HttpContext http, PanelDbContext db, AuditLog audit, CancellationToken cancellationToken) =>
        {
            if (await ValidateAsync(request, null, db, cancellationToken) is { } error)
            {
                return Invalid(error);
            }

            var plan = new BackupPlan { Name = "" };
            BackupService.Apply(plan, request);
            db.BackupPlans.Add(plan);
            audit.Record("backups.plan_added", await CurrentUser.GetAsync(http, db), plan.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(plan);
        });

        manage.MapPut("/plans/{id:int}", async (int id, BackupPlanRequest request, HttpContext http, PanelDbContext db, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            if (await db.BackupPlans.FindAsync([id], cancellationToken) is not { } plan)
            {
                return Results.NotFound();
            }

            if (await ValidateAsync(request, id, db, cancellationToken) is { } error)
            {
                return Invalid(error);
            }

            BackupService.Apply(plan, request);
            audit.Record("backups.plan_changed", await CurrentUser.GetAsync(http, db), plan.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(plan);
        });

        manage.MapDelete("/plans/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit, CancellationToken cancellationToken) =>
        {
            if (await db.BackupPlans.FindAsync([id], cancellationToken) is not { } plan)
            {
                return Results.NotFound();
            }

            db.BackupPlans.Remove(plan);
            audit.Record("backups.plan_removed", await CurrentUser.GetAsync(http, db), plan.Name);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        manage.MapPost("/plans/{id:int}/run", async (int id, HttpContext http, PanelDbContext db, BackupService backups, CancellationToken cancellationToken) =>
        {
            if (await db.BackupPlans.FindAsync([id], cancellationToken) is not { } plan)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Accepted(value: new { jobId = (await backups.StartRunAsync(plan, await CurrentUser.GetAsync(http, db))).Id });
            }
            catch (JobConflictException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        view.MapGet("/targets/{id:int}/snapshots", async (int id, PanelDbContext db, BackupService backups, AgentChannelFactory agent,
            CancellationToken cancellationToken) =>
            await db.BackupTargets.FindAsync([id], cancellationToken) is { } target
                ? Results.Ok((await agent.Backups.ListSnapshotsAsync(new SnapshotsRequest { Target = backups.ToAgent(target) }, cancellationToken: cancellationToken))
                    .Snapshots.Select(s => new { s.Id, s.ShortId, s.Time, s.Hostname, s.PlanId, s.Tags }))
                : Results.NotFound());

        view.MapGet("/targets/{id:int}/snapshots/{snapshot}", async (int id, string snapshot, PanelDbContext db, BackupService backups,
            AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            if (await db.BackupTargets.FindAsync([id], cancellationToken) is not { } target)
            {
                return Results.NotFound();
            }

            var manifest = await agent.Backups.ReadManifestAsync(new SnapshotRef { Target = backups.ToAgent(target), SnapshotId = snapshot },
                cancellationToken: cancellationToken);

            return Results.Ok(new
            {
                manifest.PlanName,
                manifest.Created,
                databases = manifest.Databases.Select(d => $"{d.Engine}/{d.Name}"),
                folders = manifest.Folders.Select(f => $"{f.User}:{f.Path}"),
                manifest.PanelState,
            });
        });

        manage.MapPost("/targets/{id:int}/snapshots/{snapshot}/restore", async (int id, string snapshot, BackupRestoreRequest request, HttpContext http,
            PanelDbContext db, BackupService backups, AuditLog audit, CancellationToken cancellationToken) =>
        {
            if (await db.BackupTargets.FindAsync([id], cancellationToken) is not { } target)
            {
                return Results.NotFound();
            }

            if ((request.Databases?.Count ?? 0) + (request.Folders?.Count ?? 0) == 0 && !request.PanelState)
            {
                return Invalid("Choose what to restore.");
            }

            var user = await CurrentUser.GetAsync(http, db);

            try
            {
                var job = await backups.StartRestoreAsync(target, snapshot, request, user);
                audit.Record("backups.restore", user, target.Name, $"{snapshot}: {string.Join(", ", [.. request.Databases ?? [], .. request.Folders ?? []])}"
                    + (request.PanelState ? " + panel state" : ""));
                await db.SaveChangesAsync(cancellationToken);

                return Results.Accepted(value: new { jobId = job.Id });
            }
            catch (JobConflictException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireSudo();
    }

    private static object Describe(BackupTarget target) => new
    {
        target.Id,
        target.Name,
        target.Kind,
        target.Path,
        target.Endpoint,
        target.Port,
        target.User,
        target.AccessKeyId,
        target.Region,
        target.HostKey,
        target.PublicKey,
        target.CheckedAt,
        target.LastError,
        // SFTP targets are usable once the host key is pinned and the repository opened.
        ready = target.CheckedAt is not null && target.LastError is null && (target.Kind != BackupTargetKind.Sftp || target.HostKey is not null),
    };

    private static async Task<string?> ValidateAsync(BackupPlanRequest request, int? id, PanelDbContext db, CancellationToken cancellationToken)
    {
        if (BackupService.ValidatePlan(request) is { } error)
        {
            return error;
        }

        if (!await db.BackupTargets.AnyAsync(t => t.Id == request.TargetId, cancellationToken))
        {
            return "Choose a backup target.";
        }

        return await db.BackupPlans.AnyAsync(p => p.Name == request.Name!.Trim() && p.Id != id, cancellationToken)
            ? $"There is already a plan called {request.Name}."
            : null;
    }

    private static IResult Invalid(string message) => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);
}
