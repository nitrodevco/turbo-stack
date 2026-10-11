using TurboManager.Agent.Commands;

namespace TurboManager.Agent.Tests;

/// <summary>Records every command and answers from a handler (default: success with empty output).</summary>
public sealed class FakeCommandRunner : ICommandRunner
{
    public List<string> Calls { get; } = [];

    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } = (_, _) => new CommandResult(0, "", "");

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add($"{fileName} {string.Join(' ', arguments)}");
        }

        return Task.FromResult(Handler(fileName, arguments));
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
