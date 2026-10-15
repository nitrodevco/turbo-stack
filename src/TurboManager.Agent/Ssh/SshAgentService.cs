using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Security;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Ssh;

public sealed class SshAgentService(ICommandRunner commands, IOptions<AgentOptions> options, ILogger<SshAgentService> logger)
    : SshAgent.SshAgentBase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly HashSet<string> NoLoginShells = ["/usr/sbin/nologin", "/sbin/nologin", "/bin/false", "/usr/bin/false", ""];

    private string HostPath(string relativePath) => Path.Combine(options.Value.HostRoot, relativePath);

    private string DropInPath => HostPath($"etc/ssh/sshd_config.d/00-{Branding.Slug}.conf");

    public override Task<LoginUsers> ListLoginUsers(Empty request, ServerCallContext context)
    {
        var sudoers = SudoMembers();
        var reply = new LoginUsers();

        foreach (var user in ReadLoginUsers())
        {
            reply.Users.Add(new LoginUser
            {
                Name = user.Name,
                Uid = user.Uid,
                Home = user.Home,
                Sudo = user.Uid == 0 || sudoers.Contains(user.Name),
                KeyCount = ReadKeys(user).Count,
            });
        }

        return Task.FromResult(reply);
    }

    public override Task<AuthorizedKeys> ListAuthorizedKeys(SshUserRef request, ServerCallContext context) =>
        Task.FromResult(ToReply(RequireLoginUser(request.User)));

    public override async Task<AuthorizedKeys> AddAuthorizedKey(AddAuthorizedKeyRequest request, ServerCallContext context)
    {
        var user = RequireLoginUser(request.User);

        if (!AuthorizedKeysParser.TryParse(request.PublicKey, out var key, out var error))
        {
            throw Validate.Invalid(error);
        }

        await Gate.WaitAsync(context.CancellationToken);

        try
        {
            var keys = ReadKeys(user);

            if (keys.Any(k => k.Fingerprint == key.Fingerprint))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"{user.Name} already has this key ({key.Fingerprint})."));
            }

            keys.Add(key);
            WriteKeys(user, keys);
            logger.LogInformation("Added SSH key {Fingerprint} for {User}", key.Fingerprint, user.Name);
        }
        finally
        {
            Gate.Release();
        }

        return ToReply(user);
    }

    public override async Task<AuthorizedKeys> RemoveAuthorizedKey(RemoveAuthorizedKeyRequest request, ServerCallContext context)
    {
        var user = RequireLoginUser(request.User);

        await Gate.WaitAsync(context.CancellationToken);

        try
        {
            var keys = ReadKeys(user);
            var removed = keys.RemoveAll(k => k.Fingerprint == request.Fingerprint);

            if (removed == 0)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Key not found."));
            }

            // Removing the last key while password login is off would lock everyone out.
            if (keys.Count == 0 && !await PasswordLoginEnabledAsync(context.CancellationToken) && CountAdminKeys(except: user.Name) == 0)
            {
                throw Validate.FailedPrecondition("This is the last SSH key of any admin user and password login is disabled. Add another key or enable password login first.");
            }

            WriteKeys(user, keys);
            logger.LogInformation("Removed SSH key {Fingerprint} for {User}", request.Fingerprint, user.Name);
        }
        finally
        {
            Gate.Release();
        }

        return ToReply(user);
    }

    public override async Task<SshdSettings> GetSshdSettings(Empty request, ServerCallContext context) =>
        await ReadEffectiveAsync(context.CancellationToken);

    public override async Task<SshdSettings> UpdateSshdSettings(SshdSettingsUpdate request, ServerCallContext context)
    {
        if (request.PermitRootLogin.Length > 0)
        {
            Validate.OneOf(request.PermitRootLogin, "PermitRootLogin", "yes", "no", "prohibit-password");
        }

        await Gate.WaitAsync(context.CancellationToken);

        try
        {
            if (request is { HasPasswordAuthentication: true, PasswordAuthentication: false } && CountAdminKeys(except: null) == 0)
            {
                throw Validate.FailedPrecondition("No admin user (root or sudo) has an SSH key yet. Add a key before disabling password login.");
            }

            var previous = File.Exists(DropInPath) ? await File.ReadAllTextAsync(DropInPath, context.CancellationToken) : null;
            var (currentPassword, currentRoot) = SshdConfig.ParseDropIn(previous ?? "");
            var content = SshdConfig.RenderDropIn(
                request.HasPasswordAuthentication ? request.PasswordAuthentication : currentPassword,
                request.PermitRootLogin.Length > 0 ? request.PermitRootLogin : currentRoot,
                Branding.ProductName);

            Directory.CreateDirectory(Path.GetDirectoryName(DropInPath)!);
            await File.WriteAllTextAsync(DropInPath, content, context.CancellationToken);

            EnsurePrivilegeSeparationDirectory();
            var test = await commands.RunAsync("sshd", ["-t"], cancellationToken: context.CancellationToken);

            if (!test.Success)
            {
                if (previous is null)
                {
                    File.Delete(DropInPath);
                }
                else
                {
                    await File.WriteAllTextAsync(DropInPath, previous, context.CancellationToken);
                }

                throw new RpcException(new Status(StatusCode.Aborted, $"sshd rejected the configuration and it was not applied: {test.ErrorText}"));
            }

            // Ubuntu 24.04+ may run sshd socket-activated; a reload of a stopped service is a no-op there
            // and new connections pick up the config anyway.
            await commands.RunAsync("systemctl", ["try-reload-or-restart", "ssh.service"], cancellationToken: context.CancellationToken);
        }
        finally
        {
            Gate.Release();
        }

        return await ReadEffectiveAsync(context.CancellationToken);
    }

    private async Task<SshdSettings> ReadEffectiveAsync(CancellationToken cancellationToken)
    {
        EnsurePrivilegeSeparationDirectory();
        var result = await commands.RunCheckedAsync("sshd", ["-T"], cancellationToken: cancellationToken);

        return SshdConfig.ParseEffective(result.StdOut);
    }

    /// <summary>
    /// sshd -T/-t refuse to run without /run/sshd, which only exists while ssh.service runs; with
    /// socket activation (Ubuntu 24.04+) it is often absent.
    /// </summary>
    private void EnsurePrivilegeSeparationDirectory()
    {
        var path = HostPath("run/sshd");

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
    }

    private async Task<bool> PasswordLoginEnabledAsync(CancellationToken cancellationToken) =>
        (await ReadEffectiveAsync(cancellationToken)).PasswordAuthentication;

    private int CountAdminKeys(string? except)
    {
        var sudoers = SudoMembers();

        return ReadLoginUsers()
            .Where(u => (u.Uid == 0 || sudoers.Contains(u.Name)) && u.Name != except)
            .Sum(u => ReadKeys(u).Count);
    }

    private List<PasswdEntry> ReadLoginUsers()
    {
        var path = HostPath("etc/passwd");

        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadLines(path)
            .Select(PasswdFile.ParseLine)
            .OfType<PasswdEntry>()
            .Where(u => u.Uid == 0 || (u.Uid is >= 1000 and < 60000 && !NoLoginShells.Contains(u.Shell)))
            .ToList();
    }

    private HashSet<string> SudoMembers()
    {
        var path = HostPath("etc/group");

        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadLines(path)
            .Select(l => l.Split(':'))
            .Where(p => p.Length >= 4 && p[0] is "sudo" or "admin")
            .SelectMany(p => p[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    private PasswdEntry RequireLoginUser(string name)
    {
        Validate.UserName(name);

        return ReadLoginUsers().FirstOrDefault(u => u.Name == name)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"{name} is not a login user on this server."));
    }

    private string KeysPath(PasswdEntry user) => Path.Combine(HostPath(user.Home.TrimStart('/')), ".ssh", "authorized_keys");

    private List<ParsedKey> ReadKeys(PasswdEntry user)
    {
        var path = KeysPath(user);

        return File.Exists(path) ? AuthorizedKeysParser.ParseFile(File.ReadAllText(path)) : [];
    }

    /// <summary>
    /// Rewrites authorized_keys keeping every line that isn't one of the removed keys (comments and
    /// unrecognised lines survive), atomically and with the ownership and modes OpenSSH requires.
    /// </summary>
    private void WriteKeys(PasswdEntry user, List<ParsedKey> keys)
    {
        var path = KeysPath(user);
        var directory = Path.GetDirectoryName(path)!;
        var keep = keys.Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);

        // Never follow links a user planted in their home: root would write wherever they point.
        if (new DirectoryInfo(directory).LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
        {
            throw Validate.FailedPrecondition($"{directory} or its authorized_keys is a symbolic link; refusing to modify it.");
        }

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        lines.RemoveAll(line => AuthorizedKeysParser.TryParse(line, out var existing, out _) && !keep.Contains(existing.Fingerprint));

        var present = lines.Select(l => AuthorizedKeysParser.TryParse(l, out var k, out _) ? k.Fingerprint : null).ToHashSet();
        lines.AddRange(keys.Where(k => !present.Contains(k.Fingerprint)).Select(k => k.ToLine()));

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            SetModeAndOwner(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, user);
        }

        var temp = path + ".ts-tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n");
        SetModeAndOwner(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite, user);
        File.Move(temp, path, overwrite: true);
    }

    private static void SetModeAndOwner(string path, UnixFileMode mode, PasswdEntry user)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, mode);
        UnixFileOwnership.SetOwner(path, user.Uid, user.Gid);
    }

    private AuthorizedKeys ToReply(PasswdEntry user)
    {
        var reply = new AuthorizedKeys { User = user.Name };
        reply.Keys.AddRange(ReadKeys(user).Select(k => k.ToContract()));

        return reply;
    }
}
