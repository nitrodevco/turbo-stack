using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Jobs;

public sealed class JobContext(RunningJob job, IServiceProvider services, CancellationToken cancellationToken)
{
    public IServiceProvider Services { get; } = services;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public void Log(string line) => job.Append(line);
}

/// <summary>A job in progress: buffered output plus live subscribers (SSE streams).</summary>
public sealed class RunningJob(long id)
{
    private readonly StringBuilder _output = new();
    private readonly List<Channel<string>> _subscribers = [];

    public long Id { get; } = id;

    public void Append(string line)
    {
        lock (_output)
        {
            _output.AppendLine(line);

            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(line);
            }
        }
    }

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>Returns the output so far and a channel that receives every later line.</summary>
    public (string Backlog, ChannelReader<string> Live) Subscribe()
    {
        lock (_output)
        {
            var channel = Channel.CreateUnbounded<string>();
            _subscribers.Add(channel);

            return (_output.ToString(), channel.Reader);
        }
    }

    public void Complete()
    {
        lock (_output)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}

/// <summary>
/// Runs long operations (package upgrades now; deploys later) in the background with persisted,
/// streamable output. Only one job per <see cref="Job.Kind"/> runs at a time.
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<JobRunner> logger)
{
    private readonly ConcurrentDictionary<long, RunningJob> _running = new();
    private readonly ConcurrentDictionary<string, long> _runningKinds = new();

    public RunningJob? GetRunning(long id) => _running.GetValueOrDefault(id);

    public async Task<Job> StartAsync(string kind, string title, PanelUser? startedBy, Func<JobContext, Task<bool>> work)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();

        if (_runningKinds.ContainsKey(kind))
        {
            throw new JobConflictException($"A {kind} job is already running.");
        }

        var job = new Job { Kind = kind, Title = title, StartedByUserId = startedBy?.Id, StartedBy = startedBy?.Username };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        if (!_runningKinds.TryAdd(kind, job.Id))
        {
            throw new JobConflictException($"A {kind} job is already running.");
        }

        var running = new RunningJob(job.Id);
        _running[job.Id] = running;
        _ = Task.Run(() => RunAsync(job.Id, kind, running, work));

        return job;
    }

    private async Task RunAsync(long id, string kind, RunningJob running, Func<JobContext, Task<bool>> work)
    {
        var status = JobStatus.Failed;

        try
        {
            using var scope = scopes.CreateScope();
            var succeeded = await work(new JobContext(running, scope.ServiceProvider, lifetime.ApplicationStopping));
            status = succeeded ? JobStatus.Succeeded : JobStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            running.Append("Cancelled: the panel is shutting down.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Job {Id} ({Kind}) failed", id, kind);
            running.Append($"Error: {e.Message}");
        }
        finally
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, status)
                    .SetProperty(j => j.FinishedAt, DateTimeOffset.UtcNow)
                    .SetProperty(j => j.Output, running.Output));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Could not save the result of job {Id}", id);
            }

            running.Complete();
            _running.TryRemove(id, out _);
            _runningKinds.TryRemove(kind, out _);
        }
    }
}

public sealed class JobConflictException(string message) : Exception(message);
