using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Files;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Ssh;

/// <summary>
/// SFTP-only accounts. sshd locks members of ts-sftp into /srv/ts-sftp/&lt;account&gt; (root-owned, as chroot requires)
/// with internal-sftp and no shell, forwarding or TTY; the account's folder is bind-mounted at …/files by a systemd
/// mount unit. Default ACLs keep everything uploaded usable by the app user (and readable by nginx).
/// </summary>
public sealed partial class SftpManager(ICommandRunner commands, FilesManager files, IOptions<AgentOptions> options, ILogger<SftpManager> logger)
{
    public const string Group = "ts-sftp";

    public const string ChrootRoot = "/srv/ts-sftp";

    public static readonly string KeysDirectory = $"/etc/{Branding.Slug}/sftp-keys";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private string HostPath(string path) => Path.Combine(options.Value.HostRoot, path.TrimStart('/'));

    private string DropInPath => HostPath($"/etc/ssh/sshd_config.d/99-{Branding.Slug}-sftp.conf");

    public static string RenderDropIn() =>
        $"# Managed by {Branding.ProductName}: SFTP-only accounts (group {Group}).\n" +
        $"Match Group {Group}\n" +
        $"    ChrootDirectory {ChrootRoot}/%u\n" +
        "    ForceCommand internal-sftp -d /files\n" +
        $"    AuthorizedKeysFile {KeysDirectory}/%u\n" +
        "    PasswordAuthentication yes\n" +
        "    PermitTTY no\n" +
        "    AllowTcpForwarding no\n" +
        "    AllowAgentForwarding no\n" +
        "    X11Forwarding no\n" +
        "    PermitTunnel no\n";

    public async Task CreateAsync(SftpAccountRequest request, CancellationToken cancellationToken)
    {
        var name = Name(request.Name);
        var (appUser, folder, relative) = files.Resolve(request.AppUser, request.Path);

        if (relative.Length == 0)
        {
            throw Validate.Invalid("Choose a folder inside the home folder (not the home folder itself).");
        }

        if (PasswdFile.FindUser(name, HostPath("/etc/passwd")) is not null || GroupExists(name))
        {
            throw Validate.FailedPrecondition($"There is already a user or group called {name}.");
        }

        await Gate.WaitAsync(cancellationToken);

        try
        {
            await EnsureSetupAsync(cancellationToken);
            await files.MakeDirectoryAsync(new FilePath { User = appUser.Name, Path = relative }, cancellationToken);

            var chroot = $"{ChrootRoot}/{name}";
            // The account *is* the app user (same uid and gid), confined to one folder by the chroot and to SFTP by sshd: whatever it
            // uploads belongs to the app, with no permission bits or ACL masks between them (sftp clients choose the file modes).
            await commands.RunCheckedAsync("useradd", ["--non-unique", "--uid", $"{appUser.Uid}", "--gid", $"{appUser.Gid}", "--no-user-group",
                "--no-create-home", "--home-dir", chroot, "--shell", "/usr/sbin/nologin", "--groups", Group,
                "--comment", Comment(appUser.Name, relative), name], cancellationToken: cancellationToken);
            // Locked until a password or key is set.
            await commands.RunAsync("passwd", ["-l", name], cancellationToken: cancellationToken);

            // The chroot and the mount point must belong to root and not be writable by the account.
            Directory.CreateDirectory(HostPath($"{chroot}/files"));
            await commands.RunCheckedAsync("chmod", ["755", HostPath(chroot), HostPath($"{chroot}/files")], cancellationToken: cancellationToken);

            var unit = await MountUnitNameAsync(name, cancellationToken);
            await File.WriteAllTextAsync(HostPath($"/etc/systemd/system/{unit}"),
                $"# Managed by {Branding.ProductName}: the folder SFTP account {name} sees.\n" +
                $"[Unit]\nDescription={Branding.ProductName} SFTP folder for {name}\n\n" +
                $"[Mount]\nWhat={folder}\nWhere={chroot}/files\nType=none\nOptions=bind\n\n" +
                "[Install]\nWantedBy=multi-user.target\n", cancellationToken);
            await commands.RunCheckedAsync("systemctl", ["daemon-reload"], cancellationToken: cancellationToken);
            await commands.RunCheckedAsync("systemctl", ["enable", "--now", unit], cancellationToken: cancellationToken);
            logger.LogInformation("SFTP account {Name} created for {Folder}", name, folder);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SetPasswordAsync(SftpCredentials request, CancellationToken cancellationToken)
    {
        var name = RequireAccount(request.Name);

        if (request.Password.Length == 0)
        {
            await commands.RunCheckedAsync("passwd", ["-l", name], cancellationToken: cancellationToken);

            return;
        }

        if (request.Password.Length is < 12 or > 128 || request.Password.Any(c => char.IsControl(c) || c == ':'))
        {
            throw Validate.Invalid("Passwords are 12-128 characters, without ':' or control characters.");
        }

        await commands.RunCheckedAsync("chpasswd", [], stdin: $"{name}:{request.Password}\n", cancellationToken: cancellationToken);
    }

    public async Task SetKeysAsync(SftpCredentials request, CancellationToken cancellationToken)
    {
        var name = RequireAccount(request.Name);
        var keys = request.PublicKeys.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

        if (keys.FirstOrDefault(k => !KeyPattern().IsMatch(k)) is { } bad)
        {
            throw Validate.Invalid($"Not an SSH public key: {bad[..Math.Min(40, bad.Length)]}…");
        }

        Directory.CreateDirectory(HostPath(KeysDirectory));
        // Root-owned: the account can't change its own keys (sshd reads the file before entering the chroot).
        await File.WriteAllTextAsync(HostPath($"{KeysDirectory}/{name}"), string.Join('\n', keys) + (keys.Count > 0 ? "\n" : ""), cancellationToken);
    }

    public async Task DeleteAsync(SftpAccountRef request, CancellationToken cancellationToken)
    {
        var name = RequireAccount(request.Name);
        var chroot = $"{ChrootRoot}/{name}";
        var unit = await MountUnitNameAsync(name, cancellationToken);

        await commands.RunAsync("systemctl", ["disable", "--now", unit], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);

        if (IsMounted($"{chroot}/files"))
        {
            // Never delete anything under a mount that's still there: that would be the real folder.
            throw Validate.FailedPrecondition($"{chroot}/files is still mounted; nothing was deleted.");
        }

        File.Delete(HostPath($"/etc/systemd/system/{unit}"));
        await commands.RunCheckedAsync("systemctl", ["daemon-reload"], cancellationToken: cancellationToken);
        // Only this account's SSH sessions: "pkill -u" would match the uid, and so every process of the app.
        await commands.RunAsync("pkill", ["-KILL", "-f", $"^sshd(-session|-auth)?: {name}( |@|$)"], cancellationToken: cancellationToken);
        // --force: the shared uid is always busy with the app's own processes. Without --remove the home (the empty chroot) stays for rmdir.
        await commands.RunCheckedAsync("userdel", ["--force", name], cancellationToken: cancellationToken);
        File.Delete(HostPath($"{KeysDirectory}/{name}"));
        // rmdir only removes empty folders: the real files can't be touched even if something went wrong above.
        await commands.RunAsync("rmdir", [HostPath($"{chroot}/files"), HostPath(chroot)], cancellationToken: cancellationToken);
        logger.LogInformation("SFTP account {Name} deleted", name);
    }

    public SftpAccounts List()
    {
        var reply = new SftpAccounts();
        var shadow = File.Exists(HostPath("/etc/shadow")) ? File.ReadAllLines(HostPath("/etc/shadow")) : [];

        foreach (var name in GroupMembers())
        {
            var chroot = $"{ChrootRoot}/{name}";
            var unit = Directory.Exists(HostPath("/etc/systemd/system"))
                ? Directory.GetFiles(HostPath("/etc/systemd/system"), "*.mount")
                    .Select(File.ReadAllText).FirstOrDefault(c => c.Contains($"Where={chroot}/files\n", StringComparison.Ordinal))
                : null;
            var hash = shadow.Select(l => l.Split(':')).FirstOrDefault(p => p.Length > 1 && p[0] == name)?[1] ?? "!";
            var keys = HostPath($"{KeysDirectory}/{name}");

            reply.Accounts.Add(new SftpAccount
            {
                Name = name,
                Folder = unit is null ? "" : Regex.Match(unit, "^What=(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim(),
                Mounted = IsMounted($"{chroot}/files"),
                Password = hash.Length > 1 && !hash.StartsWith('!') && !hash.StartsWith('*'),
                Keys = File.Exists(keys) ? File.ReadAllLines(keys).Count(l => l.Trim().Length > 0) : 0,
            });
        }

        return reply;
    }

    private async Task EnsureSetupAsync(CancellationToken cancellationToken)
    {
        await commands.RunCheckedAsync("groupadd", ["-f", Group], cancellationToken: cancellationToken);
        Directory.CreateDirectory(HostPath(ChrootRoot));
        Directory.CreateDirectory(HostPath(KeysDirectory));

        var content = RenderDropIn();
        var previous = File.Exists(DropInPath) ? await File.ReadAllTextAsync(DropInPath, cancellationToken) : null;

        if (previous == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DropInPath)!);
        await File.WriteAllTextAsync(DropInPath, content, cancellationToken);
        Directory.CreateDirectory(HostPath("/run/sshd"));
        var test = await commands.RunAsync("sshd", ["-t"], cancellationToken: cancellationToken);

        if (!test.Success)
        {
            if (previous is null)
            {
                File.Delete(DropInPath);
            }
            else
            {
                await File.WriteAllTextAsync(DropInPath, previous, cancellationToken);
            }

            throw new RpcException(new Status(StatusCode.Aborted, $"sshd rejected the SFTP settings: {test.ErrorText}"));
        }

        await commands.RunAsync("systemctl", ["try-reload-or-restart", "ssh.service"], cancellationToken: cancellationToken);
    }

    private async Task<string> MountUnitNameAsync(string name, CancellationToken cancellationToken) =>
        (await commands.RunCheckedAsync("systemd-escape", ["--path", "--suffix=mount", $"{ChrootRoot}/{name}/files"], cancellationToken: cancellationToken))
        .StdOut.Trim();

    private bool IsMounted(string path)
    {
        var mounts = HostPath("/proc/self/mountinfo");

        return File.Exists(mounts) && File.ReadLines(mounts).Any(l => l.Split(' ').ElementAtOrDefault(4) == path);
    }

    private bool GroupExists(string name) =>
        File.Exists(HostPath("/etc/group")) && File.ReadLines(HostPath("/etc/group")).Any(l => l.Split(':')[0] == name);

    private IEnumerable<string> GroupMembers()
    {
        var groups = HostPath("/etc/group");

        return File.Exists(groups)
            ? File.ReadLines(groups).Select(l => l.Split(':')).FirstOrDefault(p => p.Length >= 4 && p[0] == Group)?[3].Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []
            : [];
    }

    private string RequireAccount(string name) =>
        GroupMembers().Contains(Name(name)) ? name : throw Validate.FailedPrecondition($"{name} is not an SFTP account.");

    private static string Name(string name) =>
        NamePattern().IsMatch(name ?? "") ? name! : throw Validate.Invalid("Account names: 2-32 lowercase letters, digits or dashes, starting with a letter.");

    /// <summary>The passwd comment (GECOS) field: no colons, commas or control characters, or useradd refuses it.</summary>
    public static string Comment(string appUser, string folder) =>
        new string($"{Branding.ProductName} SFTP for {appUser} ({folder})".Select(c => c is ':' or ',' || char.IsControl(c) ? ' ' : c).ToArray());

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(ssh-(ed25519|rsa)|ecdsa-sha2-nistp(256|384|521)|sk-(ssh-ed25519|ecdsa-sha2-nistp256)@openssh\.com) [A-Za-z0-9+/=]+( [^\r\n]*)?$")]
    private static partial Regex KeyPattern();
}

public sealed class SftpAgentService(SftpManager sftp) : SftpAgent.SftpAgentBase
{
    private static readonly Empty Done = new();

    public override Task<SftpAccounts> ListSftpAccounts(Empty request, ServerCallContext context) => Task.FromResult(sftp.List());

    public override async Task<Empty> CreateSftpAccount(SftpAccountRequest request, ServerCallContext context)
    {
        await sftp.CreateAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> SetSftpPassword(SftpCredentials request, ServerCallContext context)
    {
        await sftp.SetPasswordAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> SetSftpKeys(SftpCredentials request, ServerCallContext context)
    {
        await sftp.SetKeysAsync(request, context.CancellationToken);

        return Done;
    }

    public override async Task<Empty> DeleteSftpAccount(SftpAccountRef request, ServerCallContext context)
    {
        await sftp.DeleteAsync(request, context.CancellationToken);

        return Done;
    }
}
