using TurboManager.Agent.Commands;

namespace TurboManager.Agent.Tests;

/// <summary>Records every command and answers from a handler (default: success with empty output).</summary>
public sealed class FakeCommandRunner : ICommandRunner
{
    public List<string> Calls { get; } = [];

    /// <summary>Everything passed on stdin to RunAsync (e.g. SQL), in order.</summary>
    public List<string> Stdins { get; } = [];

    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } = (_, _) => new CommandResult(0, "", "");

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add($"{fileName} {string.Join(' ', arguments)}");

            if (stdin is not null)
            {
                Stdins.Add(stdin);
            }
        }

        return Task.FromResult(Handler(fileName, arguments));
    }

    /// <summary>Data written to stdin by the last streaming call, and what streamed calls should print.</summary>
    public string LastStdin { get; private set; } = "";

    public Func<string, IReadOnlyList<string>, string> StreamOutput { get; set; } = (_, _) => "";

    public async Task<CommandResult> RunWithStreamsAsync(string fileName, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (stdin is not null)
        {
            using var reader = new StreamReader(stdin, leaveOpen: true);
            LastStdin = await reader.ReadToEndAsync(cancellationToken);
        }

        var result = await RunAsync(fileName, arguments, cancellationToken: cancellationToken);

        if (stdout is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(StreamOutput(fileName, arguments));
            await stdout.WriteAsync(bytes, cancellationToken);
        }

        return result;
    }

    public async Task<int> StreamAsync(string fileName, IReadOnlyList<string> arguments, Func<string, ValueTask> onLine,
        bool mergeStdErr = true, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, arguments, cancellationToken: cancellationToken);

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            await onLine(line);
        }

        return result.ExitCode;
    }
}
