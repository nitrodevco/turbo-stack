using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Databases;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Endpoints;

public static class DatabaseEndpoints
{
    public sealed record CreateDatabaseRequest(string? Name, string? User, string? Password);

    public sealed record UserRequest(string? Name, string? Password);

    public sealed record PasswordRequest(string? Password);

    public sealed record GrantBody(string? Database, string? Level);

    public sealed record RestoreBody(string? BackupId, string? Database);

    public sealed record ScheduleBody(bool Enabled, string? TimeOfDay, int RetentionCount);

    public static void MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/databases").WithAgentErrors().RequirePermission(Permissions.DatabasesView);
        var manage = app.MapGroup("/api/databases").WithAgentErrors().RequirePermission(Permissions.DatabasesManage);

        view.MapGet("/engines", async (AgentChannelFactory agent, CancellationToken cancellationToken) =>
            Results.Ok((await agent.Databases.GetEnginesAsync(new Empty(), cancellationToken: cancellationToken)).Engines.Select(e => new
            {
                engine = DatabaseService.Slug(e.Engine),
                label = DatabaseService.Label(e.Engine),
                e.Installed,
                e.Running,
                e.Version,
                e.Service,
                e.Port,
                e.BlockedReason,
                e.CandidateVersion,
                e.Recommended,
                e.Note,
            })));

        manage.MapPost("/{engine}/install", (string engine, HttpContext http, PanelDbContext db, DatabaseService databases, AuditLog audit) =>
            WithEngine(engine, async parsed =>
            {
                var user = await CurrentUser.GetAsync(http, db);
                var job = await databases.StartInstallAsync(parsed, user);
                audit.Record("database.engine_install", user, target: engine);
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }));

        view.MapGet("/{engine}/databases", (string engine, AgentChannelFactory agent, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
                Results.Ok((await agent.Databases.ListDatabasesAsync(new EngineRef { Engine = parsed }, cancellationToken: cancellationToken)).Databases)));

        manage.MapPost("/{engine}/databases", (string engine, CreateDatabaseRequest request, HttpContext http, PanelDbContext db,
            DatabaseService databases, AuditLog audit, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                var credentials = await databases.CreateDatabaseAsync(parsed, request.Name ?? "", request.User, request.Password, cancellationToken);
                audit.Record("database.created", await CurrentUser.GetAsync(http, db), target: $"{engine}/{request.Name}",
                    details: credentials is null ? null : $"with user {credentials.User}");
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { database = request.Name, credentials });
            }));

        // Drops a database, taking a final backup first unless told not to.
        manage.MapDelete("/{engine}/databases/{name}", (string engine, string name, bool? skipBackup, HttpContext http, PanelDbContext db,
            AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                BackupFile? backup = null;

                if (skipBackup != true)
                {
                    backup = await agent.Databases.BackupDatabaseAsync(new DatabaseRequest { Engine = parsed, Name = name }, cancellationToken: cancellationToken);
                }

                await agent.Databases.DropDatabaseAsync(new DatabaseRequest { Engine = parsed, Name = name }, cancellationToken: cancellationToken);
                await db.BackupSchedules.Where(s => s.Engine == engine && s.Database == name).ExecuteDeleteAsync(cancellationToken);
                audit.Record("database.dropped", await CurrentUser.GetAsync(http, db), target: $"{engine}/{name}",
                    details: backup is null ? "no final backup" : $"final backup {backup.Id}");
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { finalBackup = backup?.Id });
            }));

        view.MapGet("/{engine}/users", (string engine, AgentChannelFactory agent, DatabaseService databases, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                var users = await agent.Databases.ListUsersAsync(new EngineRef { Engine = parsed }, cancellationToken: cancellationToken);
                var stored = (await databases.UsersWithStoredPasswordsAsync(parsed)).ToHashSet();

                return Results.Ok(users.Users.Select(u => new
                {
                    u.Name,
                    grants = u.Grants.Select(g => new { g.Database, g.Level }),
                    passwordStored = stored.Contains(u.Name),
                }));
            }));

        manage.MapPost("/{engine}/users", (string engine, UserRequest request, HttpContext http, PanelDbContext db, DatabaseService databases,
            AuditLog audit, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                var password = await databases.CreateUserAsync(parsed, request.Name ?? "", request.Password, cancellationToken);
                audit.Record("database.user_created", await CurrentUser.GetAsync(http, db), target: $"{engine}/{request.Name}");
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { user = request.Name, password });
            }));

        manage.MapPut("/{engine}/users/{name}/password", (string engine, string name, PasswordRequest request, HttpContext http, PanelDbContext db,
            DatabaseService databases, AuditLog audit, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                var password = await databases.SetPasswordAsync(parsed, name, request.Password, cancellationToken);
                audit.Record("database.password_changed", await CurrentUser.GetAsync(http, db), target: $"{engine}/{name}");
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { user = name, password });
            }));

        manage.MapGet("/{engine}/users/{name}/password", (string engine, string name, HttpContext http, PanelDbContext db, DatabaseService databases,
            AuditLog audit) =>
            WithEngine(engine, async parsed =>
            {
                var password = await databases.RevealPasswordAsync(parsed, name);

                if (password is null)
                {
                    return Results.Problem("The panel doesn't know this user's password (it wasn't created here). Set a new one instead.",
                        statusCode: StatusCodes.Status404NotFound);
                }

                audit.Record("database.password_revealed", await CurrentUser.GetAsync(http, db), target: $"{engine}/{name}");
                await db.SaveChangesAsync();

                return Results.Ok(new { user = name, password });
            }));

        manage.MapDelete("/{engine}/users/{name}", (string engine, string name, HttpContext http, PanelDbContext db, DatabaseService databases,
            AuditLog audit, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                await databases.DropUserAsync(parsed, name, cancellationToken);
                audit.Record("database.user_dropped", await CurrentUser.GetAsync(http, db), target: $"{engine}/{name}");
                await db.SaveChangesAsync(cancellationToken);

                return Results.NoContent();
            }));

        manage.MapPut("/{engine}/users/{name}/grants", (string engine, string name, GrantBody request, HttpContext http, PanelDbContext db,
            AgentChannelFactory agent, AuditLog audit, CancellationToken cancellationToken) =>
            WithEngine(engine, async parsed =>
            {
                await agent.Databases.SetGrantAsync(new GrantRequest { Engine = parsed, User = name, Database = request.Database ?? "", Level = request.Level ?? "" },
                    cancellationToken: cancellationToken);
                audit.Record("database.grant_changed", await CurrentUser.GetAsync(http, db), target: $"{engine}/{name}",
                    details: $"{request.Database}: {request.Level}");
                await db.SaveChangesAsync(cancellationToken);

                return Results.NoContent();
            }));

        view.MapGet("/backups", async (string? engine, AgentChannelFactory agent, CancellationToken cancellationToken) =>
        {
            var parsed = engine is null ? DatabaseEngine.Unspecified : DatabaseService.Parse(engine) ?? DatabaseEngine.Unspecified;
            var backups = await agent.Databases.ListBackupsAsync(new EngineRef { Engine = parsed }, cancellationToken: cancellationToken);

            return Results.Ok(backups.Backups.Select(b => new
            {
                b.Id,
                engine = DatabaseService.Slug(b.Engine),
                b.Database,
                b.SizeBytes,
                b.CreatedUnixMs,
                b.Import,
                fileName = Path.GetFileName(b.Id),
            }));
        });

        manage.MapPost("/{engine}/databases/{name}/backup", (string engine, string name, HttpContext http, PanelDbContext db,
            DatabaseService databases) =>
            WithEngine(engine, async parsed =>
            {
                var job = await databases.StartBackupAsync(parsed, name, await CurrentUser.GetAsync(http, db));

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }));

        manage.MapPost("/{engine}/restore", (string engine, RestoreBody request, HttpContext http, PanelDbContext db, DatabaseService databases,
            AuditLog audit) =>
            WithEngine(engine, async parsed =>
            {
                var user = await CurrentUser.GetAsync(http, db);
                var job = await databases.StartRestoreAsync(request.BackupId ?? "", parsed, request.Database ?? "", user);
                audit.Record("database.restore_started", user, target: $"{engine}/{request.Database}", details: request.BackupId);
                await db.SaveChangesAsync();

                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
            }));

        manage.MapDelete("/backups", async (string id, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit,
            CancellationToken cancellationToken) =>
        {
            await agent.Databases.DeleteBackupAsync(new BackupRef { Id = id }, cancellationToken: cancellationToken);
            audit.Record("database.backup_deleted", await CurrentUser.GetAsync(http, db), target: id);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        });

        manage.MapGet("/backups/download", async (string id, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit) =>
        {
            using var call = agent.Databases.DownloadBackup(new BackupRef { Id = id }, cancellationToken: http.RequestAborted);
            var chunks = call.ResponseStream.ReadAllAsync(http.RequestAborted).GetAsyncEnumerator(http.RequestAborted);

            // Read the first chunk before sending headers so "not found" can still become a 404.
            var hasData = await chunks.MoveNextAsync();
            audit.Record("database.backup_downloaded", await CurrentUser.GetAsync(http, db), target: id);
            await db.SaveChangesAsync();

            http.Response.ContentType = "application/octet-stream";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(id)}\"";

            while (hasData)
            {
                await http.Response.Body.WriteAsync(chunks.Current.Data.Memory, http.RequestAborted);
                hasData = await chunks.MoveNextAsync();
            }

            return Results.Empty;
        });

        // Raw upload (body = the .sql/.sql.gz file, name in the query) streamed straight to the agent.
        manage.MapPost("/backups/upload", async (string name, HttpContext http, PanelDbContext db, AgentChannelFactory agent, AuditLog audit) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;
            }

            using var call = agent.Databases.UploadImport(cancellationToken: http.RequestAborted);
            var buffer = new byte[128 * 1024];
            var first = true;
            int read;

            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                await call.RequestStream.WriteAsync(new FileChunk { Name = first ? name : "", Data = ByteString.CopyFrom(buffer, 0, read) });
                first = false;
            }

            if (first)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is empty."] });
            }

            await call.RequestStream.CompleteAsync();
            var file = await call.ResponseAsync;
            audit.Record("database.import_uploaded", await CurrentUser.GetAsync(http, db), target: file.Id,
                details: $"{file.SizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes");
            await db.SaveChangesAsync();

            return Results.Ok(new { file.Id, file.SizeBytes });
        });

        view.MapGet("/schedules", async (PanelDbContext db) =>
            Results.Ok(await db.BackupSchedules.OrderBy(s => s.Engine).ThenBy(s => s.Database).ToListAsync()));

        manage.MapPut("/{engine}/databases/{name}/schedule", (string engine, string name, ScheduleBody request, HttpContext http, PanelDbContext db,
            AuditLog audit, TimeProvider time) =>
            WithEngine(engine, async _ =>
            {
                if (!TimeOnly.TryParseExact(request.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var _)
                    || request.RetentionCount is < 1 or > 365)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["schedule"] = ["Use a time like 03:00 and keep 1-365 backups."] });
                }

                var schedule = await db.BackupSchedules.FirstOrDefaultAsync(s => s.Engine == engine && s.Database == name);

                if (schedule is null)
                {
                    // Start counting from now: the first run is at the next scheduled time, not immediately.
                    schedule = new BackupSchedule { Engine = engine, Database = name, LastRunAt = time.GetLocalNow() };
                    db.BackupSchedules.Add(schedule);
                }

                schedule.Enabled = request.Enabled;
                schedule.TimeOfDay = request.TimeOfDay!;
                schedule.RetentionCount = request.RetentionCount;
                audit.Record("database.schedule_updated", await CurrentUser.GetAsync(http, db), target: $"{engine}/{name}",
                    details: request.Enabled ? $"daily at {request.TimeOfDay}, keep {request.RetentionCount}" : "disabled");
                await db.SaveChangesAsync();

                return Results.Ok(schedule);
            }));
    }

    private static async Task<IResult> WithEngine(string engine, Func<DatabaseEngine, Task<IResult>> action)
    {
        if (DatabaseService.Parse(engine) is not { } parsed)
        {
            return Results.NotFound();
        }

        try
        {
            return await action(parsed);
        }
        catch (JobConflictException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
