using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;
using AgentTarget = TurboManager.Contracts.Agent.BackupTarget;
using BackupTarget = TurboManager.Panel.Data.BackupTarget;

namespace TurboManager.Panel.Backups;

public sealed record BackupTargetRequest(string? Name, BackupTargetKind Kind, string? Path, string? Endpoint = null, int Port = 0, string? User = null,
    string? AccessKeyId = null, string? SecretAccessKey = null, string? Region = null, string? Password = null);

public sealed record BackupPlanRequest(string? Name, int TargetId, List<string>? Databases, List<string>? Folders, bool PanelState, bool Enabled = true,
    string? TimeOfDay = "03:00", int KeepDaily = 7, int KeepWeekly = 4, int KeepMonthly = 6);

public sealed record BackupRestoreRequest(List<string>? Databases, List<string>? Folders, bool PanelState);

/// <summary>
/// Backup targets (restic repositories) and plans. The agent does the work; this keeps the configuration and its
/// secrets (encrypted), and runs plans as jobs, on demand and on schedule.
/// </summary>
public sealed partial class BackupService(PanelDbContext db, AgentChannelFactory agent, JobRunner jobs, IDataProtectionProvider dataProtection,
    IOptions<PanelOptions> options, TimeProvider time)
{
    public const string JobKind = "backups";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Backups.v1");

    public string Unprotect(string value) => _protector.Unprotect(value);

    public AgentTarget ToAgent(BackupTarget target) => new()
    {
        Id = target.AgentId,
        Kind = target.Kind.ToString().ToLowerInvariant(),
        Path = target.Path,
        Endpoint = target.Endpoint,
        Port = target.Port,
        User = target.User,
        AccessKeyId = target.AccessKeyId,
        SecretAccessKey = target.SecretProtected is null ? "" : Unprotect(target.SecretProtected),
        Region = target.Region,
        HostKey = target.HostKey ?? "",
        Password = Unprotect(target.PasswordProtected),
    };

    public static string? ValidateTarget(BackupTargetRequest request)
    {
        if (!NamePattern().IsMatch(request.Name ?? ""))
        {
            return "Names: 1-60 letters, digits, spaces, dots, dashes or underscores.";
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return request.Kind switch
            {
                BackupTargetKind.Local => "Which folder? For example /var/backups/turbo-manager (better: a separate disk under /mnt).",
                BackupTargetKind.S3 => "Which bucket (optionally bucket/folder)?",
                _ => "Which folder on the server?",
            };
        }

        if (request.Password is { Length: > 0 and < 16 })
        {
            return "Repository passwords are at least 16 characters.";
        }

        return request.Kind switch
        {
            BackupTargetKind.S3 when string.IsNullOrWhiteSpace(request.AccessKeyId) || string.IsNullOrWhiteSpace(request.SecretAccessKey) =>
                "S3 needs an access key and its secret.",
            BackupTargetKind.Sftp when string.IsNullOrWhiteSpace(request.Endpoint) || string.IsNullOrWhiteSpace(request.User) =>
                "SFTP needs the server's address and a user name.",
            _ => null,
        };
    }

    /// <summary>
    /// Saves a target. Local and S3 targets are opened (or created) right away; an SFTP target first gets its own key and
    /// waits for the owner to confirm the server's host key. Returns the generated repository password, if one was generated.
    /// </summary>
    public async Task<(BackupTarget Target, string? GeneratedPassword)> CreateTargetAsync(BackupTargetRequest request, CancellationToken cancellationToken)
    {
        var generated = string.IsNullOrEmpty(request.Password) ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_') : null;
        var target = new BackupTarget
        {
            Name = request.Name!.Trim(),
            Kind = request.Kind,
            Path = request.Path!.Trim(),
            Endpoint = (request.Endpoint ?? "").Trim(),
            Port = request.Kind == BackupTargetKind.Sftp ? request.Port is 0 ? 22 : request.Port : 0,
            User = (request.User ?? "").Trim(),
            AccessKeyId = (request.AccessKeyId ?? "").Trim(),
            SecretProtected = string.IsNullOrEmpty(request.SecretAccessKey) ? null : _protector.Protect(request.SecretAccessKey.Trim()),
            Region = (request.Region ?? "").Trim(),
            PasswordProtected = _protector.Protect(generated ?? request.Password!),
        };
        db.BackupTargets.Add(target);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            if (target.Kind == BackupTargetKind.Sftp)
            {
                target.PublicKey = (await agent.Backups.CreateTargetKeyAsync(new TargetKeyRequest { TargetId = target.AgentId },
                    cancellationToken: cancellationToken)).PublicKey;
            }
            else
            {
                await agent.Backups.PrepareTargetAsync(ToAgent(target), cancellationToken: cancellationToken);
                target.CheckedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A target that can't be reached isn't kept (SFTP keeps it: its key has to be installed on the server first).
            db.BackupTargets.Remove(target);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return (target, generated);
    }

    /// <summary>Opens the repository (creating it if it doesn't exist yet); the outcome is kept on the target.</summary>
    public async Task<BackupTarget> CheckAsync(BackupTarget target, CancellationToken cancellationToken)
    {
        try
        {
            await agent.Backups.PrepareTargetAsync(ToAgent(target), cancellationToken: cancellationToken);
            target.LastError = null;
        }
        catch (RpcException e)
        {
            target.LastError = e.Status.Detail;
        }

        target.CheckedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return target;
    }

    public static string? ValidatePlan(BackupPlanRequest request)
    {
        if (!NamePattern().IsMatch(request.Name ?? ""))
        {
            return "Names: 1-60 letters, digits, spaces, dots, dashes or underscores.";
        }

        if ((request.Databases?.Count ?? 0) + (request.Folders?.Count ?? 0) == 0 && !request.PanelState)
        {
            return "Choose at least one database, folder, or the panel's own state.";
        }

        if (request.Databases?.FirstOrDefault(d => !DatabasePattern().IsMatch(d)) is { } database)
        {
            return $"Not a database: {database} (engine/name, e.g. mariadb/turbo).";
        }

        if (request.Folders?.FirstOrDefault(f => !FolderPattern().IsMatch(f) || f.Split(':', 2)[1].Split('/').Any(p => p is ".." or ".")) is { } folder)
        {
            return $"Not a folder: {folder} (user:path inside its home, e.g. hotel:sites/assets).";
        }

        if (!TimeOnly.TryParseExact(request.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return "Run time: HH:mm.";
        }

        if (request.KeepDaily is < 0 or > 365 || request.KeepWeekly is < 0 or > 520 || request.KeepMonthly is < 0 or > 240
            || request.KeepDaily + request.KeepWeekly + request.KeepMonthly == 0)
        {
            return "Keep at least one snapshot (daily 0-365, weekly 0-520, monthly 0-240).";
        }

        return null;
    }

    public static void Apply(BackupPlan plan, BackupPlanRequest request)
    {
        plan.Name = request.Name!.Trim();
        plan.TargetId = request.TargetId;
        plan.Databases = [.. (request.Databases ?? []).Distinct()];
        plan.Folders = [.. (request.Folders ?? []).Select(f => f.TrimEnd('/')).Distinct()];
        plan.PanelState = request.PanelState;
        plan.Enabled = request.Enabled;
        plan.TimeOfDay = request.TimeOfDay!;
        plan.KeepDaily = request.KeepDaily;
        plan.KeepWeekly = request.KeepWeekly;
        plan.KeepMonthly = request.KeepMonthly;
    }

    public Task<Job> StartRunAsync(BackupPlan plan, PanelUser? user) =>
        jobs.StartAsync(JobKind, $"Backup: {plan.Name}", user, context => context.Services.GetRequiredService<BackupService>().RunAsync(context, plan.Id));

    private async Task<bool> RunAsync(JobContext context, int planId)
    {
        var plan = await db.BackupPlans.FindAsync([planId], context.CancellationToken) ?? throw new InvalidOperationException("The plan is gone.");
        var target = await db.BackupTargets.FindAsync([plan.TargetId], context.CancellationToken) ?? throw new InvalidOperationException("The target is gone.");
        plan.LastRunAt = time.GetUtcNow();
        var copy = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "backup", "panel.db");

        try
        {
            if (plan.PanelState)
            {
                // A consistent copy of the live database for the agent to pick up (it deletes it afterwards).
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Delete(copy);
                await db.Database.ExecuteSqlAsync($"VACUUM INTO {copy}", context.CancellationToken);
            }

            var request = new BackupRunRequest
            {
                Target = ToAgent(target),
                PlanId = plan.AgentId,
                PlanName = plan.Name,
                PanelState = plan.PanelState,
                Retention = new Retention { Daily = plan.KeepDaily, Weekly = plan.KeepWeekly, Monthly = plan.KeepMonthly },
            };
            request.Databases.AddRange(plan.Databases.Select(d => d.Split('/', 2)).Select(p => new DatabaseItem { Engine = p[0], Name = p[1] }));
            request.Folders.AddRange(plan.Folders.Select(f => f.Split(':', 2)).Select(p => new FolderItem { User = p[0], Path = p[1] }));

            using var call = agent.Backups.RunBackup(request, cancellationToken: context.CancellationToken);
            string? snapshot = null;

            await foreach (var line in call.ResponseStream.ReadAllAsync(context.CancellationToken))
            {
                if (line.Finished && line.Line.StartsWith("snapshot:", StringComparison.Ordinal))
                {
                    snapshot = line.Line["snapshot:".Length..];
                }
                else
                {
                    context.Log(line.Line);
                }
            }

            plan.LastSnapshot = snapshot;
            plan.LastResult = snapshot is null ? "Failed: no snapshot was saved." : "OK";

            if (snapshot is not null)
            {
                plan.LastSuccessAt = time.GetUtcNow();
            }

            return snapshot is not null;
        }
        catch (RpcException e)
        {
            context.Log($"Failed: {e.Status.Detail}");
            plan.LastResult = $"Failed: {e.Status.Detail}";

            return false;
        }
        finally
        {
            File.Delete(copy);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public Task<Job> StartRestoreAsync(BackupTarget target, string snapshotId, BackupRestoreRequest request, PanelUser user) =>
        jobs.StartAsync(JobKind, $"Restore from snapshot {snapshotId[..Math.Min(8, snapshotId.Length)]} ({target.Name})", user,
            context => context.Services.GetRequiredService<BackupService>().RestoreAsync(context, target.Id, snapshotId, request));

    private async Task<bool> RestoreAsync(JobContext context, int targetId, string snapshotId, BackupRestoreRequest request)
    {
        var target = await db.BackupTargets.FindAsync([targetId], context.CancellationToken) ?? throw new InvalidOperationException("The target is gone.");
        var restore = new RestoreSnapshotRequest { Target = ToAgent(target), SnapshotId = snapshotId, PanelState = request.PanelState };
        restore.Databases.AddRange((request.Databases ?? []).Select(d => d.Split('/', 2)).Where(p => p.Length == 2)
            .Select(p => new DatabaseItem { Engine = p[0], Name = p[1] }));
        restore.Folders.AddRange((request.Folders ?? []).Select(f => f.Split(':', 2)).Where(p => p.Length == 2)
            .Select(p => new FolderItem { User = p[0], Path = p[1] }));

        try
        {
            using var call = agent.Backups.RestoreSnapshot(restore, cancellationToken: context.CancellationToken);

            await foreach (var line in call.ResponseStream.ReadAllAsync(context.CancellationToken))
            {
                if (!line.Finished)
                {
                    context.Log(line.Line);
                }
            }

            return true;
        }
        catch (RpcException e)
        {
            context.Log($"Failed: {e.Status.Detail}");

            return false;
        }
    }

    public static bool IsDue(BackupPlan plan, DateTimeOffset now)
    {
        if (!plan.Enabled || !TimeOnly.TryParseExact(plan.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return false;
        }

        var todayAt = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);

        return now >= todayAt && (plan.LastRunAt is null || plan.LastRunAt < todayAt);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,59}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^(mysql|mariadb|postgres)/[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex DatabasePattern();

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}:[A-Za-z0-9._ /-]{1,500}$")]
    private static partial Regex FolderPattern();
}

/// <summary>Starts the plans that are due (checked every minute); one backup job runs at a time.</summary>
public sealed class BackupPlanScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<BackupPlanScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Backup plan scheduler run failed");
            }
        }
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
        var now = time.GetLocalNow();

        foreach (var plan in (await db.BackupPlans.ToListAsync(cancellationToken)).Where(p => BackupService.IsDue(p, now)))
        {
            try
            {
                await backups.StartRunAsync(plan, null);
                logger.LogInformation("Started the scheduled backup {Plan}", plan.Name);
            }
            catch (JobConflictException)
            {
                // Another backup is running; this one starts on a later tick.
                return;
            }
        }
    }
}
