using System.Globalization;
using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Data;
using TurboManager.Panel.Jobs;

namespace TurboManager.Panel.Databases;

public sealed record CreatedCredentials(string Engine, string Database, string User, string Password, string Host, int Port);

/// <summary>Database administration through the agent, plus the credentials the panel keeps.</summary>
public sealed class DatabaseService(PanelDbContext db, AgentChannelFactory agent, JobRunner jobs, IDataProtectionProvider dataProtection, TimeProvider time)
{
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.DatabaseCredentials.v1");

    public static string Slug(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Mysql => "mysql",
        DatabaseEngine.Mariadb => "mariadb",
        DatabaseEngine.Postgres => "postgres",
        _ => "",
    };

    public static DatabaseEngine? Parse(string slug) => slug switch
    {
        "mysql" => DatabaseEngine.Mysql,
        "mariadb" => DatabaseEngine.Mariadb,
        "postgres" => DatabaseEngine.Postgres,
        _ => null,
    };

    public static int Port(DatabaseEngine engine) => engine == DatabaseEngine.Postgres ? 5432 : 3306;

    /// <summary>32 characters from an unambiguous alphabet (no quoting issues in any client or config file).</summary>
    public static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 32);

    /// <summary>Creates a database and, optionally, a user with full access to it (the usual app setup).</summary>
    public async Task<CreatedCredentials?> CreateDatabaseAsync(DatabaseEngine engine, string name, string? user, string? password, CancellationToken cancellationToken)
    {
        var databases = agent.Databases;
        await databases.CreateDatabaseAsync(new DatabaseRequest { Engine = engine, Name = name }, cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        password = string.IsNullOrWhiteSpace(password) ? GeneratePassword() : password;

        try
        {
            await databases.CreateUserAsync(new DatabaseUserRequest { Engine = engine, Name = user, Password = password }, cancellationToken: cancellationToken);
            await databases.SetGrantAsync(new GrantRequest { Engine = engine, User = user, Database = name, Level = "all" }, cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // Don't leave a half-created setup behind.
            await databases.DropDatabaseAsync(new DatabaseRequest { Engine = engine, Name = name }, cancellationToken: CancellationToken.None);
            throw;
        }

        await StoreCredentialAsync(engine, user, password, cancellationToken);

        return new CreatedCredentials(Slug(engine), name, user, password, "127.0.0.1", Port(engine));
    }

    public async Task<string> CreateUserAsync(DatabaseEngine engine, string user, string? password, CancellationToken cancellationToken)
    {
        password = string.IsNullOrWhiteSpace(password) ? GeneratePassword() : password;
        await agent.Databases.CreateUserAsync(new DatabaseUserRequest { Engine = engine, Name = user, Password = password }, cancellationToken: cancellationToken);
        await StoreCredentialAsync(engine, user, password, cancellationToken);

        return password;
    }

    public async Task<string> SetPasswordAsync(DatabaseEngine engine, string user, string? password, CancellationToken cancellationToken)
    {
        password = string.IsNullOrWhiteSpace(password) ? GeneratePassword() : password;
        await agent.Databases.SetUserPasswordAsync(new DatabaseUserRequest { Engine = engine, Name = user, Password = password }, cancellationToken: cancellationToken);
        await StoreCredentialAsync(engine, user, password, cancellationToken);

        return password;
    }

    public async Task DropUserAsync(DatabaseEngine engine, string user, CancellationToken cancellationToken)
    {
        await agent.Databases.DropUserAsync(new DatabaseUserRequest { Engine = engine, Name = user }, cancellationToken: cancellationToken);
        await db.DatabaseCredentials.Where(c => c.Engine == Slug(engine) && c.Username == user).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<string?> RevealPasswordAsync(DatabaseEngine engine, string user)
    {
        var credential = await db.DatabaseCredentials.FirstOrDefaultAsync(c => c.Engine == Slug(engine) && c.Username == user);

        return credential is null ? null : _protector.Unprotect(credential.PasswordProtected);
    }

    public Task<List<string>> UsersWithStoredPasswordsAsync(DatabaseEngine engine) =>
        db.DatabaseCredentials.Where(c => c.Engine == Slug(engine)).Select(c => c.Username).ToListAsync();

    private async Task StoreCredentialAsync(DatabaseEngine engine, string user, string password, CancellationToken cancellationToken)
    {
        var slug = Slug(engine);
        var credential = await db.DatabaseCredentials.FirstOrDefaultAsync(c => c.Engine == slug && c.Username == user, cancellationToken);

        if (credential is null)
        {
            credential = new DatabaseCredential { Engine = slug, Username = user, PasswordProtected = "" };
            db.DatabaseCredentials.Add(credential);
        }

        credential.PasswordProtected = _protector.Protect(password);
        credential.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Job> StartInstallAsync(DatabaseEngine engine, PanelUser user) =>
        jobs.StartAsync("databases", $"Install {Label(engine)}", user, context =>
            StreamAsync(context, context.Services.GetRequiredService<AgentChannelFactory>().Databases
                .InstallEngine(new EngineRef { Engine = engine }, cancellationToken: context.CancellationToken)));

    public Task<Job> StartBackupAsync(DatabaseEngine engine, string database, PanelUser? user) =>
        jobs.StartAsync("databases", $"Back up {database} ({Label(engine)})", user, async context =>
        {
            context.Log($"Dumping {database}…");
            var backup = await context.Services.GetRequiredService<AgentChannelFactory>().Databases
                .BackupDatabaseAsync(new DatabaseRequest { Engine = engine, Name = database }, cancellationToken: context.CancellationToken);
            context.Log($"Saved {backup.Id} ({backup.SizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes).");

            return true;
        });

    public Task<Job> StartRestoreAsync(string backupId, DatabaseEngine engine, string database, PanelUser user) =>
        jobs.StartAsync("databases", $"Restore {database} ({Label(engine)}) from {Path.GetFileName(backupId)}", user, context =>
            StreamAsync(context, context.Services.GetRequiredService<AgentChannelFactory>().Databases
                .RestoreBackup(new RestoreRequest { BackupId = backupId, Engine = engine, Database = database }, cancellationToken: context.CancellationToken)));

    public static string Label(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Mysql => "MySQL",
        DatabaseEngine.Mariadb => "MariaDB",
        DatabaseEngine.Postgres => "PostgreSQL",
        _ => "database",
    };

    private static async Task<bool> StreamAsync(JobContext context, AsyncServerStreamingCall<OutputLine> call)
    {
        using (call)
        {
            var finished = false;

            await foreach (var line in call.ResponseStream.ReadAllAsync(context.CancellationToken))
            {
                if (line.Finished)
                {
                    finished = line.ExitCode == 0;
                }
                else
                {
                    context.Log(line.Line);
                }
            }

            return finished;
        }
    }
}

/// <summary>Runs daily database backups and prunes old ones (checks every minute).</summary>
public sealed class BackupScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<BackupScheduler> logger) : BackgroundService
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
                logger.LogWarning(e, "Backup scheduler run failed");
            }
        }
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        var agent = scope.ServiceProvider.GetRequiredService<AgentChannelFactory>();
        var now = time.GetLocalNow();

        foreach (var schedule in await db.BackupSchedules.Where(s => s.Enabled).ToListAsync(cancellationToken))
        {
            if (!IsDue(schedule, now) || DatabaseService.Parse(schedule.Engine) is not { } engine)
            {
                continue;
            }

            schedule.LastRunAt = now;

            try
            {
                var backup = await agent.Databases.BackupDatabaseAsync(new DatabaseRequest { Engine = engine, Name = schedule.Database },
                    cancellationToken: cancellationToken);
                schedule.LastResult = $"OK: {backup.Id}";

                var old = (await agent.Databases.ListBackupsAsync(new EngineRef { Engine = engine }, cancellationToken: cancellationToken)).Backups
                    .Where(b => !b.Import && b.Database == schedule.Database)
                    .OrderByDescending(b => b.CreatedUnixMs)
                    .Skip(schedule.RetentionCount);

                foreach (var expired in old)
                {
                    await agent.Databases.DeleteBackupAsync(new BackupRef { Id = expired.Id }, cancellationToken: cancellationToken);
                }
            }
            catch (RpcException e)
            {
                schedule.LastResult = $"Failed: {e.Status.Detail}";
                logger.LogWarning("Scheduled backup of {Database} failed: {Error}", schedule.Database, e.Status.Detail);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Due once per day, at or after the configured time, if it hasn't run since that time today.</summary>
    public static bool IsDue(BackupSchedule schedule, DateTimeOffset now)
    {
        if (!TimeOnly.TryParseExact(schedule.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return false;
        }

        var todayAt = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);

        return now >= todayAt && (schedule.LastRunAt is null || schedule.LastRunAt < todayAt);
    }
}
