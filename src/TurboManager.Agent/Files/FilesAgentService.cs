using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Deploy;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Files;

/// <summary>
/// The file manager. Everything runs as the app user (runuser), so permissions are the user's own; paths are
/// relative to their home and may not climb out of it with "..".
/// </summary>
public sealed class FilesManager(DeployManager deploy, ICommandRunner commands)
{
    public const int MaxRead = 2 * 1024 * 1024;

    public const long MaxUpload = 512L * 1024 * 1024;

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    public (PasswdEntry User, string Absolute, string Relative) Resolve(string user, string? path)
    {
        var entry = deploy.RequireAppUser(user);
        var relative = (path ?? "").Replace('\\', '/').Trim().Trim('/');

        if (relative.Length > 4096 || relative.Contains('\0') || relative.Split('/').Any(part => part is ".." or "."))
        {
            throw Validate.Invalid("Paths are relative to the home folder, without \"..\".");
        }

        while (relative.Contains("//", StringComparison.Ordinal))
        {
            relative = relative.Replace("//", "/", StringComparison.Ordinal);
        }

        return (entry, relative.Length == 0 ? entry.Home : $"{entry.Home}/{relative}", relative);
    }

    public async Task<DirectoryListing> ListAsync(FilePath request, CancellationToken cancellationToken)
    {
        var (user, absolute, relative) = Resolve(request.User, request.Path);
        // NUL-separated, so any file name survives.
        var result = await RunAsync(user, "find", [absolute, "-mindepth", "1", "-maxdepth", "1", "-printf", @"%y\t%s\t%m\t%T@\t%f\0"], null, null, cancellationToken);
        var listing = new DirectoryListing { Path = relative };

        foreach (var line in result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(5000))
        {
            var parts = line.Split('\t', 5);

            if (parts.Length < 5)
            {
                continue;
            }

            listing.Entries.Add(new FileEntry
            {
                Type = parts[0] is "d" or "f" or "l" ? parts[0] : "o",
                Size = long.TryParse(parts[1], out var size) ? size : 0,
                Mode = parts[2],
                ModifiedUnixMs = double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? (long)(seconds * 1000) : 0,
                Name = parts[4],
            });
        }

        return listing;
    }

    public async Task<FileContent> ReadAsync(FilePath request, CancellationToken cancellationToken)
    {
        var (user, absolute, relative) = Resolve(request.User, request.Path);
        using var output = new MemoryStream();
        await RunAsync(user, "head", ["-c", (MaxRead + 1).ToString(CultureInfo.InvariantCulture), "--", absolute], null, output, cancellationToken);

        if (output.Length > MaxRead)
        {
            throw Validate.FailedPrecondition("The file is larger than 2 MB: download it instead.");
        }

        return new FileContent { User = user.Name, Path = relative, Content = ByteString.CopyFrom(output.ToArray()) };
    }

    public async Task WriteAsync(string user, string? path, Stream content, CancellationToken cancellationToken)
    {
        var (entry, absolute, relative) = Resolve(user, path);

        if (relative.Length == 0)
        {
            throw Validate.Invalid("Choose a file name.");
        }

        await RunAsync(entry, "bash", ["-c", "cat > \"$1\"", "bash", absolute], content, null, cancellationToken, TimeSpan.FromMinutes(30));
    }

    public async Task DeleteAsync(FilePath request, CancellationToken cancellationToken)
    {
        var (user, absolute, relative) = Resolve(request.User, request.Path);

        if (relative.Length == 0)
        {
            throw Validate.Invalid("The home folder itself can't be deleted.");
        }

        await RunAsync(user, "rm", ["-rf", "--", absolute], null, null, cancellationToken, TimeSpan.FromMinutes(10));
    }

    public async Task MakeDirectoryAsync(FilePath request, CancellationToken cancellationToken)
    {
        var (user, absolute, _) = Resolve(request.User, request.Path);
        await RunAsync(user, "mkdir", ["-p", "--", absolute], null, null, cancellationToken);
    }

    public async Task MoveAsync(FileMove request, CancellationToken cancellationToken)
    {
        var (user, from, fromRelative) = Resolve(request.User, request.From);
        var (_, to, toRelative) = Resolve(request.User, request.To);

        if (fromRelative.Length == 0 || toRelative.Length == 0)
        {
            throw Validate.Invalid("The home folder itself can't be moved.");
        }

        await RunAsync(user, "mv", ["-T", "--", from, to], null, null, cancellationToken);
    }

    public async Task DownloadAsync(FilePath request, Stream destination, CancellationToken cancellationToken)
    {
        var (user, absolute, _) = Resolve(request.User, request.Path);
        await RunAsync(user, "cat", ["--", absolute], null, destination, cancellationToken, TimeSpan.FromMinutes(30));
    }

    private async Task<CommandResult> RunAsync(PasswdEntry user, string program, IEnumerable<string> arguments, Stream? stdin, Stream? stdout,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var (command, args) = deploy.AsUser(user, user.Home, new Dictionary<string, string>(), program, arguments);
        var result = await commands.RunWithStreamsAsync(command, args, stdin, stdout, timeout ?? Quick, cancellationToken);

        if (result.ExitCode != 0)
        {
            // The command's own words (No such file or directory, Permission denied, ...), without the home path.
            var message = result.StdErr.Trim().Split('\n').FirstOrDefault() ?? "";
            throw Validate.FailedPrecondition(message.Replace(user.Home + "/", "", StringComparison.Ordinal).Replace(user.Home, "~", StringComparison.Ordinal));
        }

        return result;
    }
}

public sealed class FilesAgentService(FilesManager files) : FilesAgent.FilesAgentBase
{
    public override Task<DirectoryListing> ListDirectory(FilePath request, ServerCallContext context) => files.ListAsync(request, context.CancellationToken);

    public override Task<FileContent> ReadFile(FilePath request, ServerCallContext context) => files.ReadAsync(request, context.CancellationToken);

    public override async Task<Empty> WriteFile(FileContent request, ServerCallContext context)
    {
        using var content = new MemoryStream(request.Content.ToByteArray());
        await files.WriteAsync(request.User, request.Path, content, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> DeletePath(FilePath request, ServerCallContext context)
    {
        await files.DeleteAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> MakeDirectory(FilePath request, ServerCallContext context)
    {
        await files.MakeDirectoryAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> MovePath(FileMove request, ServerCallContext context)
    {
        await files.MoveAsync(request, context.CancellationToken);

        return new Empty();
    }

    public override async Task<Empty> UploadFile(IAsyncStreamReader<FileChunk> requestStream, ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.Name.Split(':', 2) is not [var user, var path])
        {
            throw Validate.Invalid("Name the upload \"<user>:<path>\".");
        }

        // Buffered in the agent's private temp space, then written as the user in one go.
        var temp = Path.GetTempFileName();

        try
        {
            await using (var buffer = File.Create(temp))
            {
                await buffer.WriteAsync(requestStream.Current.Data.Memory, context.CancellationToken);

                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    if (buffer.Length > FilesManager.MaxUpload)
                    {
                        throw Validate.Invalid("Uploads are limited to 512 MB.");
                    }

                    await buffer.WriteAsync(requestStream.Current.Data.Memory, context.CancellationToken);
                }
            }

            await using var content = File.OpenRead(temp);
            await files.WriteAsync(user, path, content, context.CancellationToken);
        }
        finally
        {
            File.Delete(temp);
        }

        return new Empty();
    }

    public override async Task DownloadFile(FilePath request, IServerStreamWriter<FileChunk> responseStream, ServerCallContext context)
    {
        await using var chunks = new ChunkWriter(responseStream, context.CancellationToken);
        await files.DownloadAsync(request, chunks, context.CancellationToken);
    }

    /// <summary>A write-only stream that sends what's written as gRPC chunks.</summary>
    private sealed class ChunkWriter(IServerStreamWriter<FileChunk> stream, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            await stream.WriteAsync(new FileChunk { Data = ByteString.CopyFrom(buffer.Span) }, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, cancellationToken).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
