using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using TurboManager.Agent.Deploy;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Terminal;

/// <summary>
/// Interactive shells. A pseudo-terminal comes from libc's openpty; a small bash wrapper makes its other end the
/// shell's stdin/stdout/stderr and (setsid -c) controlling terminal, then becomes `runuser -l &lt;app user&gt;` or a
/// root login shell. No forking from .NET itself.
/// </summary>
public sealed class TerminalAgentService(DeployManager deploy, ILogger<TerminalAgentService> logger) : TerminalAgent.TerminalAgentBase
{
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(12);

    public override async Task OpenTerminal(IAsyncStreamReader<TerminalInput> requestStream, IServerStreamWriter<TerminalOutput> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.KindCase != TerminalInput.KindOneofCase.Open)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Open the terminal first."));
        }

        var open = requestStream.Current.Open;
        string[] command = open.User == "root"
            ? ["env", "-i", "TERM=xterm-256color", "LANG=C.UTF-8", "HOME=/root", "USER=root", "LOGNAME=root", "SHELL=/bin/bash",
                $"PATH={DeployManager.SystemPath}", "/bin/bash", "--login"]
            : ["env", "-i", "TERM=xterm-256color", "LANG=C.UTF-8", $"PATH={DeployManager.SystemPath}", "runuser", "-l", deploy.RequireAppUser(open.User).Name];

        using var pty = Pty.Open(open.Size);
        using var process = Start(pty.SlaveName, command);
        logger.LogInformation("Terminal opened as {User} (pid {Pid})", open.User, process.Id);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        session.CancelAfter(MaxSession);
        var output = Channel.CreateBounded<TerminalOutput>(256);

        // Output: blocking reads on the master end until the shell is gone (EIO once nothing holds the other end).
        var reader = Task.Factory.StartNew(async () =>
        {
            var buffer = new byte[8192];

            try
            {
                while (true)
                {
                    var read = pty.Read(buffer);

                    if (read <= 0)
                    {
                        break;
                    }

                    await output.Writer.WriteAsync(new TerminalOutput { Data = ByteString.CopyFrom(buffer, 0, read) }, session.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // the shell exited (EIO), or the session ended
            }
            catch (Exception e)
            {
                logger.LogError(e, "Reading the terminal of {User} failed", open.User);
            }
            finally
            {
                // However it ends, the browser hears about it.
                output.Writer.TryComplete();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var exited = Task.Run(async () =>
        {
            await process.WaitForExitAsync(CancellationToken.None);
            pty.CloseSlave(); // the reader now ends
            await reader;
        });

        var input = Task.Run(async () =>
        {
            try
            {
                while (await requestStream.MoveNext(session.Token))
                {
                    var message = requestStream.Current;

                    if (message.KindCase == TerminalInput.KindOneofCase.Data)
                    {
                        pty.Write(message.Data.Span);
                    }
                    else if (message.KindCase == TerminalInput.KindOneofCase.Resize)
                    {
                        pty.Resize(message.Resize);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or RpcException)
            {
            }
            catch (Exception e)
            {
                logger.LogError(e, "Writing to the terminal of {User} failed", open.User);
            }
            finally
            {
                // The browser went away: end the shell and everything it started.
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        });

        try
        {
            await foreach (var message in output.Reader.ReadAllAsync(session.Token))
            {
                await responseStream.WriteAsync(message, session.Token);
            }

            // Output is done: the exit code follows once the shell has gone.
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5), session.Token));

            if (process.HasExited)
            {
                await responseStream.WriteAsync(new TerminalOutput { ExitCode = process.ExitCode }, session.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await Task.WhenAny(Task.WhenAll(exited, input), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            logger.LogInformation("Terminal as {User} closed", open.User);
        }
    }

    private static Process Start(string slave, string[] command)
    {
        // bash opens the terminal as stdin/stdout/stderr, then setsid -c gives the shell a session with it as its
        // controlling terminal (so Ctrl-C, job control and full-screen programs work).
        var info = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, WorkingDirectory = "/" };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exec setsid -c \"$@\" <\"$0\" >\"$0\" 2>&1");
        info.ArgumentList.Add(slave);

        foreach (var part in command)
        {
            info.ArgumentList.Add(part);
        }

        info.Environment.Clear();

        return Process.Start(info) ?? throw new RpcException(new Status(StatusCode.Internal, "Starting the shell failed."));
    }
}

/// <summary>A pseudo-terminal pair from libc (glibc ≥ 2.34 has openpty in libc itself).</summary>
internal sealed partial class Pty : IDisposable
{
    private const string Libc = "libc.so.6";
    private const uint TIOCSWINSZ = 0x5414;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int EINTR = 4;

    private int _slave;

    private int _master;

    public string SlaveName { get; }

    private Pty(int master, int slave, string slaveName)
    {
        _slave = slave;
        SlaveName = slaveName;
        _master = master;
    }

    public static Pty Open(TerminalSize? size)
    {
        var window = WindowSize(size);

        if (openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, ref window) != 0)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"openpty failed ({Marshal.GetLastPInvokeError()})."));
        }

        // Neither end may leak into the shell (it opens the slave by name).
        fcntl(master, F_SETFD, FD_CLOEXEC);
        fcntl(slave, F_SETFD, FD_CLOEXEC);

        var name = new byte[128];

        if (ptsname_r(master, name, (nuint)name.Length) != 0)
        {
            close(master);
            close(slave);

            throw new RpcException(new Status(StatusCode.Internal, "ptsname failed."));
        }

        return new Pty(master, slave, System.Text.Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0)));
    }

    public void Resize(TerminalSize size)
    {
        var window = WindowSize(size);
        ioctl(_master, TIOCSWINSZ, ref window);
    }

    /// <summary>Blocking read from the master end: bytes read, or 0 once the shell is gone (EIO) or the PTY is closed.</summary>
    public int Read(Span<byte> buffer)
    {
        while (true)
        {
            var read = read_(_master, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);

            if (read >= 0)
            {
                return (int)read;
            }

            if (Marshal.GetLastPInvokeError() != EINTR)
            {
                return 0;
            }
        }
    }

    /// <summary>Writes everything to the master end (the shell's keyboard).</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var written = write_(_master, in MemoryMarshal.GetReference(data), (nuint)data.Length);

            if (written < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR)
                {
                    continue;
                }

                throw new IOException($"Writing to the terminal failed ({Marshal.GetLastPInvokeError()}).");
            }

            data = data[(int)written..];
        }
    }

    /// <summary>Our copy of the slave end kept it open while the shell started; dropping it lets reads end with the shell.</summary>
    public void CloseSlave()
    {
        if (_slave >= 0)
        {
            close(_slave);
            _slave = -1;
        }
    }

    public void Dispose()
    {
        CloseSlave();

        if (_master >= 0)
        {
            close(_master);
            _master = -1;
        }
    }

    private static Winsize WindowSize(TerminalSize? size) => new()
    {
        Rows = (ushort)Math.Clamp(size?.Rows ?? 24, 5, 500),
        Cols = (ushort)Math.Clamp(size?.Cols ?? 80, 10, 1000),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int openpty(out int master, out int slave, IntPtr name, IntPtr termp, ref Winsize winp);

    [LibraryImport(Libc, EntryPoint = "read", SetLastError = true)]
    private static partial nint read_(int fd, ref byte buffer, nuint count);

    [LibraryImport(Libc, EntryPoint = "write", SetLastError = true)]
    private static partial nint write_(int fd, in byte buffer, nuint count);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, ref Winsize winp);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int ptsname_r(int fd, byte[] buf, nuint buflen);
}
