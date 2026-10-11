using System.Diagnostics;
using System.Text;

namespace TurboManager.Agent.Commands;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>Best human-readable failure text: stderr, else stdout.</summary>
    public string ErrorText => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

public sealed class CommandFailedException(string command, CommandResult result)
    : Exception($"{command} failed with exit code {result.ExitCode}: {result.ErrorText}")
{
    public CommandResult Result { get; } = result;
}

/// <summary>
/// Runs system tools. Arguments are passed as a list, never through a shell, so values from the
/// panel can't inject commands. Output is forced to the C locale for stable parsing.
/// </summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Streams output lines (stdout, plus stderr when <paramref name="mergeStdErr"/>) and returns the exit code.</summary>
    Task<int> StreamAsync(string fileName, IReadOnlyList<string> arguments, Func<string, ValueTask> onLine,
        bool mergeStdErr = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Feeds <paramref name="stdin"/> to the command and/or copies its stdout into <paramref name="stdout"/>
    /// (database dumps and restores). StdOut in the result is empty when it was redirected.
    /// </summary>
    Task<CommandResult> RunWithStreamsAsync(string fileName, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout,
        TimeSpan timeout, CancellationToken cancellationToken = default);
}

public static class CommandRunnerExtensions
{
    public static async Task<CommandResult> RunCheckedAsync(this ICommandRunner runner, string fileName,
        IReadOnlyList<string> arguments, string? stdin = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(fileName, arguments, stdin, timeout, cancellationToken);

        return result.Success ? result : throw new CommandFailedException($"{fileName} {string.Join(' ', arguments)}", result);
    }
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var process = Start(fileName, arguments, redirectStdin: stdin is not null);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);

            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    public async Task<int> StreamAsync(string fileName, IReadOnlyList<string> arguments, Func<string, ValueTask> onLine,
        bool mergeStdErr = true, CancellationToken cancellationToken = default)
    {
        using var process = Start(fileName, arguments, redirectStdin: false);
        var gate = new SemaphoreSlim(1, 1);

        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                await gate.WaitAsync(cancellationToken);

                try
                {
                    await onLine(line);
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        try
        {
            var stderrTask = mergeStdErr ? Pump(process.StandardError) : process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(Pump(process.StandardOutput), stderrTask);
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    public async Task<CommandResult> RunWithStreamsAsync(string fileName, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var process = Start(fileName, arguments, redirectStdin: stdin is not null);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        try
        {
            var stderr = process.StandardError.ReadToEndAsync(token);
            var output = stdout is null ? process.StandardOutput.ReadToEndAsync(token) : CopyOutputAsync();

            async Task<string> CopyOutputAsync()
            {
                await process.StandardOutput.BaseStream.CopyToAsync(stdout, token);

                return "";
            }

            if (stdin is not null)
            {
                try
                {
                    await stdin.CopyToAsync(process.StandardInput.BaseStream, token);
                }
                catch (IOException)
                {
                    // The command exited early (e.g. a SQL error); its exit code and stderr explain why.
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }

            await process.WaitForExitAsync(token);

            return new CommandResult(process.ExitCode, await output, await stderr);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static Process Start(string fileName, IReadOnlyList<string> arguments, bool redirectStdin)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            UseShellExecute = false,
            // A neutral directory: tools run as other users (runuser -u postgres) warn if they can't enter it.
            WorkingDirectory = OperatingSystem.IsWindows() ? null : "/",
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["LC_ALL"] = "C.UTF-8";
        info.Environment["LANG"] = "C.UTF-8";
        info.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        info.Environment["SYSTEMD_PAGER"] = "";
        info.Environment["SYSTEMD_COLORS"] = "0";

        // The agent's own systemd service variables must not leak into tools it runs.
        info.Environment.Remove("NOTIFY_SOCKET");
        info.Environment.Remove("WATCHDOG_PID");
        info.Environment.Remove("WATCHDOG_USEC");
        info.Environment.Remove("LISTEN_FDS");
        info.Environment.Remove("LISTEN_PID");

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
