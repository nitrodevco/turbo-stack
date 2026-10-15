using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Updates;

/// <summary>
/// The panel's own updates. The agent does the checking and verifying (signature, checksum) and starts ts-update; this
/// keeps what it found for the UI and alerts, and records each update's outcome in the activity log once.
/// </summary>
public sealed class PanelUpdateService(PanelDbContext db, AgentChannelFactory agent, AuditLog audit, TimeProvider time)
{
    public static string CurrentVersion { get; } = ReleaseVersion.Installed();

    public async Task<PanelUpdateState> GetAsync(CancellationToken cancellationToken)
    {
        var state = await db.PanelUpdates.FindAsync([1], cancellationToken);

        if (state is null)
        {
            state = new PanelUpdateState { CurrentVersion = CurrentVersion };
            db.PanelUpdates.Add(state);
            await db.SaveChangesAsync(cancellationToken);
        }

        // After an update (or a rollback) the stored check may describe another version.
        state.CurrentVersion = CurrentVersion;
        state.Available = state.LatestVersion is { Length: > 0 } latest && ReleaseVersion.Compare(latest, CurrentVersion) > 0;

        return state;
    }

    public async Task<PanelUpdateState> CheckAsync(CancellationToken cancellationToken)
    {
        var info = await agent.Updates.CheckUpdateAsync(new Empty(), cancellationToken: cancellationToken);
        var state = await GetAsync(cancellationToken);

        state.Channel = info.Channel;
        state.Error = info.Error.Length > 0 ? info.Error : null;
        state.CheckedAt = time.GetUtcNow();

        if (state.Error is null)
        {
            state.LatestVersion = info.LatestVersion;
            state.Notes = info.Notes;
            state.Published = info.Published;
            state.Available = info.Available;
        }

        await RecordOutcomeAsync(state, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return state;
    }

    /// <summary>The live state of the update process, recording a finished update's outcome on the way.</summary>
    public async Task<UpdateStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var state = await GetAsync(cancellationToken);
        var status = await RecordOutcomeAsync(state, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return status;
    }

    public async Task StartAsync(string version, PanelUser user, CancellationToken cancellationToken)
    {
        await agent.Updates.StartUpdateAsync(new UpdateRequest { Version = version }, cancellationToken: cancellationToken);
        audit.Record("panel.update.start", user, $"{CurrentVersion} → {version}");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PanelUpdateState> SetChannelAsync(string channel, PanelUser user, CancellationToken cancellationToken)
    {
        await agent.Updates.SetUpdateChannelAsync(new UpdateChannel { Channel = channel }, cancellationToken: cancellationToken);
        audit.Record("panel.update.channel", user, channel);

        return await CheckAsync(cancellationToken);
    }

    private async Task<UpdateStatus> RecordOutcomeAsync(PanelUpdateState state, CancellationToken cancellationToken)
    {
        var status = await agent.Updates.GetUpdateStatusAsync(new Empty(), cancellationToken: cancellationToken);

        if (status.State is "done" or "rolled-back" or "failed" && status.UpdatedAt.Length > 0 && status.UpdatedAt != state.LastAt)
        {
            state.LastState = status.State;
            state.LastFrom = status.FromVersion;
            state.LastTo = status.ToVersion;
            state.LastMessage = status.Message;
            state.LastAt = status.UpdatedAt;
            audit.Record($"panel.update.{status.State}", null, $"{status.FromVersion} → {status.ToVersion}", status.Message);
        }

        return status;
    }
}

/// <summary>Checks the release channel a minute after start-up (recording the update that may have just happened) and every six hours.</summary>
public sealed class PanelUpdateChecker(IServiceScopeFactory scopes, ILogger<PanelUpdateChecker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var state = await scope.ServiceProvider.GetRequiredService<PanelUpdateService>().CheckAsync(stoppingToken);

                    if (state.Error is not null)
                    {
                        logger.LogWarning("Checking for panel updates failed: {Error}", state.Error);
                    }
                }
                catch (RpcException e)
                {
                    logger.LogWarning("Checking for panel updates failed: {Error}", e.Status.Detail);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
