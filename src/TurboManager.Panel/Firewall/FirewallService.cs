using System.Net;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Firewall;

public sealed record PendingFirewallChange(string Id, DateTimeOffset RevertAt, string Summary);

public sealed record FirewallApplyResult(bool Changed, PendingFirewallChange? Pending, IReadOnlyList<string> Reasons);

/// <summary>The change would block the requester's own IP and needs explicit acknowledgement.</summary>
public sealed class FirewallLockoutException(IReadOnlyList<string> reasons) : Exception("This change would block your own IP address.")
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>
/// The panel's firewall: desired state in the database, applied to ufw through the agent.
/// Callers modify entities, then call <see cref="CommitAsync"/>; failures restore the previous state.
/// </summary>
public sealed class FirewallService(PanelDbContext db, AgentChannelFactory agent, AuditLog audit, IOptions<PanelOptions> options,
    TimeProvider time, ILogger<FirewallService> logger)
{
    public static readonly (string Key, string Name, string Action, string Ports)[] InstallerRules =
    [
        ("ssh", "SSH", "limit", "22"),
        ("http", "HTTP", "allow", "80"),
        ("https", "HTTPS", "allow", "443"),
        ("setup", "Panel setup address", "allow", "8443"),
    ];

    /// <summary>Creates the settings row and the installer's rules (same keys as install.sh's ufw comments).</summary>
    public static async Task SeedAsync(PanelDbContext db)
    {
        if (await db.FirewallSettings.AnyAsync())
        {
            return;
        }

        db.FirewallSettings.Add(new FirewallSettings());
        db.FirewallRules.AddRange(InstallerRules.Select(r => new ManagedFirewallRule
        {
            Key = r.Key,
            Name = r.Name,
            Action = r.Action,
            Protocol = "tcp",
            Ports = r.Ports,
            System = true,
        }));

        await db.SaveChangesAsync();
    }

    public async Task<FirewallSettings> GetSettingsAsync() =>
        await db.FirewallSettings.FirstOrDefaultAsync() ?? db.FirewallSettings.Add(new FirewallSettings()).Entity;

    public Task<List<ManagedFirewallRule>> GetRulesAsync() =>
        db.FirewallRules.Include(r => r.IpList).ThenInclude(l => l!.Entries).OrderBy(r => r.Id).ToListAsync();

    public Task<FirewallState> GetActualStateAsync(CancellationToken cancellationToken) =>
        agent.Firewall.GetFirewallStateAsync(new Empty(), cancellationToken: cancellationToken).ResponseAsync;

    public Task<FirewallChangeRecord?> GetPendingAsync() =>
        db.FirewallChanges.Where(c => c.Status == FirewallChangeStatus.Pending).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync();

    /// <summary>Serialised desired state, taken before a modification so it can be restored.</summary>
    public async Task<string> SnapshotAsync()
    {
        var state = new DesiredState(
            await db.FirewallSettings.AsNoTracking().FirstOrDefaultAsync() ?? new FirewallSettings(),
            await db.FirewallRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            await db.IpLists.AsNoTracking().Include(l => l.Entries).OrderBy(l => l.Id).ToListAsync());

        return JsonSerializer.Serialize(state);
    }

    /// <summary>
    /// Saves pending entity changes, then applies the difference to ufw. Risky changes get a
    /// confirmation window; if the agent reports a failure the database is restored from <paramref name="snapshot"/>.
    /// </summary>
    /// <param name="systemChange">Routine changes made by the panel itself (e.g. Cloudflare range updates): no confirmation window.</param>
    public async Task<FirewallApplyResult> CommitAsync(string snapshot, string summary, PanelUser? user, IPAddress? requester,
        bool acknowledgeLockout, CancellationToken cancellationToken, bool systemChange = false)
    {
        if (await GetPendingAsync() is not null)
        {
            throw new InvalidOperationException("Another firewall change is waiting for confirmation. Confirm it or wait for it to revert.");
        }

        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var plan = FirewallPlanner.Plan(await GetRulesAsync(), await GetSettingsAsync(), await GetActualStateAsync(cancellationToken));

            if (plan.IsEmpty)
            {
                audit.Record("firewall.updated", user, details: summary);
                await db.SaveChangesAsync(cancellationToken);

                return new FirewallApplyResult(false, null, []);
            }

            var risk = FirewallRiskAssessor.Assess(plan, requester);

            if (risk.BlocksRequester && !acknowledgeLockout)
            {
                throw new FirewallLockoutException(risk.Reasons);
            }

            var change = new FirewallChange
            {
                ConfirmWithinSeconds = risk.RequiresConfirmation && !systemChange ? options.Value.FirewallConfirmSeconds : 0,
                SetDefaultIncoming = plan.SetDefaultIncoming ?? "",
            };
            change.Add.AddRange(plan.Add.Select(t => t.ToSpec()));
            change.Delete.AddRange(plan.Delete.Select(t => t.ToSpec()));

            if (plan.SetEnabled is { } enabled)
            {
                change.SetEnabled = enabled;
            }

            var result = await agent.Firewall.ApplyFirewallChangeAsync(change, cancellationToken: cancellationToken);
            PendingFirewallChange? pending = null;

            if (!string.IsNullOrEmpty(result.TransactionId))
            {
                var revertAt = DateTimeOffset.FromUnixTimeMilliseconds(result.RevertAtUnixMs);
                db.FirewallChanges.Add(new FirewallChangeRecord
                {
                    Id = result.TransactionId,
                    RevertAt = revertAt,
                    PreviousStateJson = snapshot,
                    Summary = summary,
                    UserId = user?.Id,
                });
                pending = new PendingFirewallChange(result.TransactionId, revertAt, summary);
            }

            audit.Record(pending is null ? "firewall.applied" : "firewall.applied_pending_confirmation", user,
                details: $"{summary} ({plan.Add.Count} added, {plan.Delete.Count} removed)");
            await db.SaveChangesAsync(cancellationToken);

            return new FirewallApplyResult(true, pending, risk.Reasons);
        }
        catch
        {
            await RestoreAsync(snapshot);

            throw;
        }
    }

    public async Task<FirewallChangeRecord?> ConfirmAsync(string id, PanelUser user, CancellationToken cancellationToken)
    {
        var record = await db.FirewallChanges.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (record is null || record.Status != FirewallChangeStatus.Pending)
        {
            return record;
        }

        var status = await agent.Firewall.ConfirmFirewallChangeAsync(new FirewallTransactionRef { TransactionId = id }, cancellationToken: cancellationToken);

        if (status.Status == "confirmed")
        {
            record.Status = FirewallChangeStatus.Confirmed;
            audit.Record("firewall.change_confirmed", user, details: record.Summary);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await ReconcileAsync(cancellationToken);
        }

        return record;
    }

    /// <summary>
    /// Brings pending records in line with the agent: if the agent reverted a change (not confirmed
    /// in time, or it restarted), the panel restores its own desired state to match.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = await db.FirewallChanges
            .Where(c => c.Status == FirewallChangeStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var record in due.Where(r => r.RevertAt <= now))
        {
            var status = await agent.Firewall.GetFirewallTransactionAsync(new FirewallTransactionRef { TransactionId = record.Id },
                cancellationToken: cancellationToken);

            if (status.Status == "pending")
            {
                continue;
            }

            if (status.Status == "confirmed")
            {
                record.Status = FirewallChangeStatus.Confirmed;
                await db.SaveChangesAsync(cancellationToken);

                continue;
            }

            logger.LogWarning("Firewall change {Id} was reverted by the agent ({Status}); restoring previous rules", record.Id, status.Status);
            await RestoreAsync(record.PreviousStateJson);

            // RestoreAsync cleared the change tracker, so update the record directly.
            await db.FirewallChanges.Where(c => c.Id == record.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, FirewallChangeStatus.Reverted), cancellationToken);
            audit.Record("firewall.change_reverted", details: record.Summary);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RestoreAsync(string snapshot)
    {
        var state = JsonSerializer.Deserialize<DesiredState>(snapshot)!;
        db.ChangeTracker.Clear();

        await db.FirewallRules.ExecuteDeleteAsync();
        await db.IpLists.ExecuteDeleteAsync();
        await db.FirewallSettings.ExecuteDeleteAsync();

        db.FirewallSettings.Add(state.Settings);
        db.IpLists.AddRange(state.IpLists);
        db.FirewallRules.AddRange(state.Rules.Select(r => { r.IpList = null; return r; }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed record DesiredState(FirewallSettings Settings, List<ManagedFirewallRule> Rules, List<IpList> IpLists);
}

/// <summary>Checks pending confirmations every few seconds so reverted changes are reflected promptly.</summary>
public sealed class FirewallReconciler(IServiceScopeFactory scopes, ILogger<FirewallReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();

                if (await db.FirewallChanges.AnyAsync(c => c.Status == FirewallChangeStatus.Pending, stoppingToken))
                {
                    await scope.ServiceProvider.GetRequiredService<FirewallService>().ReconcileAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "Firewall reconcile failed (agent unavailable?)");
            }
        }
    }
}
