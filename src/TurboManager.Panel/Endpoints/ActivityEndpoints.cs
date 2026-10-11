using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Endpoints;

/// <summary>Background jobs and the audit log.</summary>
public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs").RequirePermission(Permissions.SystemView);

        jobs.MapGet("/", async (string? kind, PanelDbContext db) =>
            Results.Ok(await db.Jobs
                .Where(j => kind == null || j.Kind == kind)
                .OrderByDescending(j => j.Id)
                .Take(50)
                .Select(j => new { j.Id, j.Kind, j.Title, status = j.Status.ToString().ToLowerInvariant(), j.StartedAt, j.FinishedAt, j.StartedBy })
                .ToListAsync()));

        jobs.MapGet("/{id:long}", async (long id, PanelDbContext db, JobRunner runner) =>
        {
            var job = await db.Jobs.FindAsync(id);

            return job is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    job.Id,
                    job.Kind,
                    job.Title,
                    status = job.Status.ToString().ToLowerInvariant(),
                    job.StartedAt,
                    job.FinishedAt,
                    job.StartedBy,
                    output = runner.GetRunning(id)?.Output ?? job.Output ?? "",
                });
        });

        // Streams a job's output: everything so far, then new lines, then an "end" event with the result.
        jobs.MapGet("/{id:long}/stream", (long id, HttpContext http, IServiceScopeFactory scopes, JobRunner runner) =>
        {
            http.Response.Headers["X-Accel-Buffering"] = "no";

            return TypedResults.ServerSentEvents(StreamJob(id, scopes, runner, http.RequestAborted));
        });

        app.MapGet("/api/audit", async (int? page, string? action, PanelDbContext db) =>
        {
            const int pageSize = 50;
            var query = db.AuditLog.AsQueryable();

            if (!string.IsNullOrWhiteSpace(action))
            {
                query = query.Where(a => a.Action.StartsWith(action));
            }

            var entries = await query
                .OrderByDescending(a => a.Id)
                .Skip(Math.Max(0, (page ?? 1) - 1) * pageSize)
                .Take(pageSize + 1)
                .Select(a => new { a.Id, a.At, a.Username, a.Action, a.Target, a.Details, a.IpAddress })
                .ToListAsync();

            return Results.Ok(new { entries = entries.Take(pageSize), hasMore = entries.Count > pageSize });
        }).RequirePermission(Permissions.AuditView);
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamJob(long id, IServiceScopeFactory scopes, JobRunner runner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var running = runner.GetRunning(id);

        if (running is not null)
        {
            var (backlog, live) = running.Subscribe();

            foreach (var line in SplitLines(backlog))
            {
                yield return new SseItem<string>(line, "line");
            }

            await foreach (var line in live.ReadAllAsync(cancellationToken))
            {
                yield return new SseItem<string>(line, "line");
            }
        }

        using var scope = scopes.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<PanelDbContext>().Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job is null)
        {
            yield break;
        }

        if (running is null)
        {
            foreach (var line in SplitLines(job.Output ?? ""))
            {
                yield return new SseItem<string>(line, "line");
            }
        }

        yield return new SseItem<string>(job.Status.ToString().ToLowerInvariant(), "end");
    }

    /// <summary>Output is newline-terminated; drop the empty piece after the final newline.</summary>
    private static IEnumerable<string> SplitLines(string text) =>
        text.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd('\r')).Where(_ => text.Length > 0);
}
