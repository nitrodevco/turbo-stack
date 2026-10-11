using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Firewall;

/// <summary>
/// Applies firewall changes atomically. Every change snapshots ufw's files first and is rolled back
/// if any command fails. A change applied with a confirmation window is rolled back automatically
/// unless confirmed in time, so a rule that locks the admin out undoes itself.
/// </summary>
public sealed class FirewallManager(
    ICommandRunner commands, UfwFiles files, TimeProvider time, IOptions<AgentOptions> options, ILogger<FirewallManager> logger)
{
    public static readonly TimeSpan MaxConfirmWindow = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TransactionRecord> _transactions = new(StringComparer.Ordinal);
    private PendingTransaction? _pending;

    public async Task<FirewallState> GetStateAsync(CancellationToken cancellationToken)
    {
        var state = new FirewallState();
        CommandResult status;

        try
        {
            status = await commands.RunAsync("ufw", ["status"], cancellationToken: cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            state.Available = false;
            state.Error = "ufw is not installed.";

            return state;
        }

        var (incoming, outgoing) = UfwParser.ParseDefaults(files.ReadOrEmpty(files.Defaults));
        state.Available = true;
        state.Enabled = status.Success && UfwParser.ParseStatusActive(status.StdOut);
        state.DefaultIncoming = incoming;
        state.DefaultOutgoing = outgoing;
        state.Rules.AddRange(UfwParser.ParseRulesFile(files.ReadOrEmpty(files.UserRules), ipv6: false));
        state.Rules.AddRange(UfwParser.ParseRulesFile(files.ReadOrEmpty(files.User6Rules), ipv6: true));

        if (!status.Success)
        {
            state.Error = status.ErrorText;
        }

        return state;
    }

    public async Task<FirewallChangeResult> ApplyAsync(FirewallChange change, CancellationToken cancellationToken)
    {
        var adds = change.Add.Select(UfwCommands.ValidateSpec).ToList();
        var deletes = change.Delete.Select(UfwCommands.ValidateSpec).ToList();
        var confirmWindow = TimeSpan.FromSeconds(change.ConfirmWithinSeconds);

        if (confirmWindow < TimeSpan.Zero || confirmWindow > MaxConfirmWindow)
        {
            throw Validate.Invalid("Confirmation window must be between 0 and 600 seconds.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_pending is not null)
            {
                throw Validate.FailedPrecondition("Another firewall change is waiting for confirmation. Confirm it or let it revert first.");
            }

            var before = await GetStateAsync(cancellationToken);
            var snapshot = files.Snapshot();

            try
            {
                foreach (var spec in deletes)
                {
                    await RunUfwAsync(UfwCommands.Delete(spec), cancellationToken);
                }

                foreach (var spec in adds)
                {
                    await RunUfwAsync(UfwCommands.Add(spec), cancellationToken);
                }

                if (change.SetDefaultIncoming.Length > 0)
                {
                    await RunUfwAsync(UfwCommands.SetDefaultIncoming(change.SetDefaultIncoming), cancellationToken);
                }

                if (change.HasSetEnabled)
                {
                    await RunUfwAsync(change.SetEnabled ? ["--force", "enable"] : ["disable"], cancellationToken);
                }
            }
            catch (Exception e) when (e is CommandFailedException or OperationCanceledException)
            {
                logger.LogWarning(e, "Firewall change failed; restoring previous configuration");
                await RestoreAsync(snapshot, before.Enabled, CancellationToken.None);

                throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Aborted,
                    $"The firewall change failed and was rolled back: {e.Message}"));
            }

            var result = new FirewallChangeResult { State = await GetStateAsync(cancellationToken) };

            if (confirmWindow > TimeSpan.Zero)
            {
                var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
                var revertAt = time.GetUtcNow() + confirmWindow;
                var cancel = new CancellationTokenSource();

                _pending = new PendingTransaction(id, snapshot, before.Enabled, cancel);
                Persist(_pending);
                SetRecord(id, new TransactionRecord("pending", revertAt));
                _ = RevertWhenDueAsync(_pending, confirmWindow);

                result.TransactionId = id;
                result.RevertAtUnixMs = revertAt.ToUnixTimeMilliseconds();
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FirewallTransactionStatus> ConfirmAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_pending?.Id == id)
            {
                await _pending.Cancel.CancelAsync();
                SetRecord(id, new TransactionRecord("confirmed", GetRecord(id).RevertAt));
                _pending = null;
                DeletePersisted();
                logger.LogInformation("Firewall change {Id} confirmed", id);
            }

            return GetTransaction(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public FirewallTransactionStatus GetTransaction(string id)
    {
        lock (_transactions)
        {
            return _transactions.TryGetValue(id, out var record)
                ? new FirewallTransactionStatus { TransactionId = id, Status = record.Status, RevertAtUnixMs = record.RevertAt.ToUnixTimeMilliseconds() }
                : new FirewallTransactionStatus { TransactionId = id, Status = "unknown" };
        }
    }

    private async Task RevertWhenDueAsync(PendingTransaction pending, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, time, pending.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _gate.WaitAsync();

        try
        {
            if (_pending != pending)
            {
                return;
            }

            logger.LogWarning("Firewall change {Id} was not confirmed in time; reverting", pending.Id);
            await RestoreAsync(pending.Snapshot, pending.WasEnabled, CancellationToken.None);
            SetRecord(pending.Id, new TransactionRecord("reverted", GetRecord(pending.Id).RevertAt));
            _pending = null;
            DeletePersisted();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Reverting firewall change {Id} failed", pending.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Called at startup: a change still awaiting confirmation when the agent stopped (crash, restart,
    /// reboot) was never confirmed, so it is reverted rather than left in place.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PendingFile))
        {
            return;
        }

        var persisted = JsonSerializer.Deserialize<PersistedTransaction>(await File.ReadAllTextAsync(PendingFile, cancellationToken));

        if (persisted is not null)
        {
            logger.LogWarning("Reverting unconfirmed firewall change {Id} left over from before the agent restarted", persisted.Id);
            await RestoreAsync(persisted.Snapshot, persisted.WasEnabled, cancellationToken);
            SetRecord(persisted.Id, new TransactionRecord("reverted", time.GetUtcNow()));
        }

        DeletePersisted();
    }

    private string PendingFile => Path.Combine(options.Value.StateDirectory, "pending-firewall-change.json");

    private void Persist(PendingTransaction pending)
    {
        Directory.CreateDirectory(options.Value.StateDirectory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(options.Value.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(PendingFile, JsonSerializer.Serialize(new PersistedTransaction(pending.Id, pending.Snapshot, pending.WasEnabled)));
    }

    private void DeletePersisted()
    {
        if (File.Exists(PendingFile))
        {
            File.Delete(PendingFile);
        }
    }

    private async Task RestoreAsync(Dictionary<string, string?> snapshot, bool wasEnabled, CancellationToken cancellationToken)
    {
        files.Restore(snapshot);

        if (wasEnabled)
        {
            await RunUfwAsync(["--force", "enable"], cancellationToken);
            await RunUfwAsync(["reload"], cancellationToken);
        }
        else
        {
            await RunUfwAsync(["disable"], cancellationToken);
        }
    }

    private void SetRecord(string id, TransactionRecord record)
    {
        lock (_transactions)
        {
            _transactions[id] = record;
        }
    }

    private TransactionRecord GetRecord(string id)
    {
        lock (_transactions)
        {
            return _transactions[id];
        }
    }

    private Task<CommandResult> RunUfwAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        commands.RunCheckedAsync("ufw", arguments, timeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);

    private sealed record PendingTransaction(string Id, Dictionary<string, string?> Snapshot, bool WasEnabled, CancellationTokenSource Cancel);

    private sealed record TransactionRecord(string Status, DateTimeOffset RevertAt);

    private sealed record PersistedTransaction(string Id, Dictionary<string, string?> Snapshot, bool WasEnabled);
}
