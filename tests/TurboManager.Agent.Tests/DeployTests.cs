using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Deploy;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Tests;

public sealed class DeployValidationTests
{
    [Theory]
    [InlineData("hotel", true)]
    [InlineData("turbo-app", true)]
    [InlineData("root", true)] // valid shape; RequireAppUser rejects it later
    [InlineData("Hotel", false)]
    [InlineData("a", false)]
    [InlineData("-x", false)]
    [InlineData("x;rm", false)]
    public void Users(string user, bool valid) => Check(() => DeployValidation.User(user), valid);

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/1.2", true)]
    [InlineData("feature-x", true)]
    [InlineData("-upload-pack=evil", false)]
    [InlineData("a..b", false)]
    [InlineData("x.lock", false)]
    [InlineData("a b", false)]
    public void Branches(string branch, bool valid) => Check(() => DeployValidation.Branch(branch), valid);

    [Theory]
    [InlineData("packages/nitro-react", true)]
    [InlineData("dist/", true)]
    [InlineData("../etc", false)]
    [InlineData("/etc/passwd", true)] // trimmed to "etc/passwd": relative to the release, never absolute
    [InlineData("a/./b", false)]
    [InlineData("a b", false)]
    public void Relative_paths(string path, bool valid) => Check(() => DeployValidation.RelativePath(path), valid);

    [Fact]
    public void Environment_rejects_bad_names_and_line_breaks()
    {
        Check(() => DeployValidation.Environment(new Dictionary<string, string> { ["TURBO__Turbo__Name"] = "value with spaces" }), true);
        Check(() => DeployValidation.Environment(new Dictionary<string, string> { ["1BAD"] = "x" }), false);
        Check(() => DeployValidation.Environment(new Dictionary<string, string> { ["OK"] = "line\nbreak" }), false);
    }

    [Fact]
    public void Service_commands_are_single_lines() =>
        Check(() => DeployValidation.Command("dotnet Turbo.Main.dll\nrm -rf /"), false);

    private static void Check(Action action, bool valid)
    {
        if (valid)
        {
            action();
        }
        else
        {
            Assert.Throws<RpcException>(action);
        }
    }
}

public sealed class GitSshTests
{
    private static GitSyncRequest Request() => new()
    {
        Repository = new RepositoryAccess { Owner = "billsonnn", Name = "turbo-cloud", KeyName = "turbo-cloud" },
        SshHost = "github.com",
        SshPort = 22,
        HttpsBase = "https://github.com",
        SubmoduleAccess =
        {
            new RepositoryAccess { Owner = "nitrodevco", Name = "turbo-admin", KeyName = "turbo-admin" },
            new RepositoryAccess { Owner = "nitrodevco", Name = "turbo-web", KeyName = "" },
        },
    };

    [Fact]
    public void Remote_urls_use_the_key_alias_or_https()
    {
        Assert.Equal("ts-turbo-cloud:billsonnn/turbo-cloud.git", GitSsh.RemoteUrl(Request().Repository, "https://github.com"));
        Assert.Equal("https://github.com/nitrodevco/turbo-web.git", GitSsh.RemoteUrl(Request().SubmoduleAccess[1], "https://github.com"));
    }

    [Fact]
    public void Each_submodule_url_style_is_rewritten_to_its_own_key()
    {
        var arguments = GitSsh.SubmoduleRewrites(Request());

        Assert.Contains("url.ts-turbo-admin:nitrodevco/turbo-admin.git.insteadOf=https://github.com/nitrodevco/turbo-admin.git", arguments);
        Assert.Contains("url.ts-turbo-admin:nitrodevco/turbo-admin.git.insteadOf=git@github.com:nitrodevco/turbo-admin.git", arguments);
        Assert.Contains("url.ts-turbo-admin:nitrodevco/turbo-admin.git.insteadOf=ts-turbo-cloud:nitrodevco/turbo-admin.git", arguments);
        Assert.Contains("url.https://github.com/nitrodevco/turbo-web.git.insteadOf=git@github.com:nitrodevco/turbo-web.git", arguments);
        Assert.All(arguments.Where((_, i) => i % 2 == 0), a => Assert.Equal("-c", a));
    }

    [Fact]
    public void Ssh_config_pins_host_keys_and_isolates_identities()
    {
        var config = GitSsh.RenderConfig(["turbo-cloud", "turbo-admin"], "/home/hotel", "github.com", 22);

        Assert.Contains("Host ts-turbo-cloud", config);
        Assert.Contains("IdentityFile /home/hotel/.ssh/ts/turbo-admin", config);
        Assert.Contains("IdentitiesOnly yes", config);
        Assert.Contains("StrictHostKeyChecking yes", config);
        Assert.Equal("[fake-github]:2222 ssh-ed25519 AAAA\n", GitSsh.RenderKnownHosts("fake-github", 2222, ["ssh-ed25519 AAAA"]));
    }
}

public sealed class DeployManagerTests : IDisposable
{
    private readonly FakeHostRoot _host = new FakeHostRoot()
        .WithFile("etc/passwd", "root:x:0:0:root:/root:/bin/bash\nhotel:x:1001:1001::/home/hotel:/bin/bash\nbill:x:1000:1000::/home/bill:/bin/bash\n")
        .WithFile("etc/group", "root:x:0:\nts-apps:x:999:hotel\nsudo:x:27:bill\n");

    private readonly FakeCommandRunner _commands = new();
    private readonly DeployManager _deploy;

    public DeployManagerTests() =>
        _deploy = new DeployManager(_commands, Options.Create(new AgentOptions { HostRoot = _host.Path }), NullLogger<DeployManager>.Instance);

    [Theory]
    [InlineData("root")]
    [InlineData("bill")]
    public async Task Never_acts_as_root_or_users_it_did_not_create(string user)
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => _deploy.BuildAsync(new BuildRequest
        {
            User = user, App = "web", Release = "20261011120000-abcdef1", Script = "id",
        }, _ => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Empty(_commands.Calls);
    }

    [Fact]
    public async Task Existing_non_app_user_is_not_adopted()
    {
        await Assert.ThrowsAsync<RpcException>(() => _deploy.EnsureUserAsync("bill", CancellationToken.None));
        Assert.DoesNotContain(_commands.Calls, c => c.StartsWith("useradd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Builds_run_as_the_app_user_with_a_clean_environment()
    {
        await _deploy.BuildAsync(new BuildRequest
        {
            User = "hotel",
            App = "web",
            Release = "20261011120000-abcdef1",
            WorkingDirectory = "packages/nitro-react",
            Script = "yarn build",
            Environment = { ["VITE_SOCKET_URL"] = "wss://ws.hotel.test" },
            Runtimes = { "node" },
        }, _ => Task.CompletedTask, CancellationToken.None);

        var call = Assert.Single(_commands.Calls, c => c.EndsWith("bash -euo pipefail -c yarn build", StringComparison.Ordinal));
        Assert.StartsWith("runuser -u hotel -- env -i --chdir=/home/hotel/apps/web/releases/20261011120000-abcdef1/packages/nitro-react", call);
        Assert.Contains("VITE_SOCKET_URL=wss://ws.hotel.test", call);
        Assert.Contains("PATH=/opt/turbo-stack/runtimes/node/bin:/usr/local/sbin", call);
        Assert.Contains("TS_SHARED_DIR=/home/hotel/apps/web/shared", call);
        // Its own temp directory (not the shared, possibly noexec /tmp), made before and removed after.
        Assert.Contains("TMPDIR=/home/hotel/.cache/ts-build/web", call);
        Assert.Contains("mkdir -p -- \"$1\" bash /home/hotel/.cache/ts-build/web", _commands.Calls[0]);
        Assert.EndsWith("rm -rf -- /home/hotel/.cache/ts-build/web", _commands.Calls[^1]);
    }

    [Fact]
    public async Task Site_directories_belong_to_the_app_user_and_stay_readable_by_nginx()
    {
        var directory = await _deploy.EnsureSiteDirectoryAsync(new AppRef { User = "hotel", App = "assets" }, CancellationToken.None);

        Assert.Equal("/home/hotel/sites/assets", directory.Path);
        Assert.StartsWith("runuser -u hotel -- env -i ", _commands.Calls[0]);
        Assert.EndsWith(" mkdir -p /home/hotel/sites/assets", _commands.Calls[0]);
        Assert.Contains("setfacl -R -m u:www-data:rX -m d:u:www-data:rX /home/hotel/sites/assets", _commands.Calls);
        await Assert.ThrowsAsync<RpcException>(() => _deploy.EnsureSiteDirectoryAsync(new AppRef { User = "hotel", App = "../x" }, CancellationToken.None));
    }

    [Fact]
    public async Task Turning_the_console_on_for_a_running_app_starts_the_socket_first()
    {
        Directory.CreateDirectory(Path.Combine(_host.Path, "etc/systemd/system"));
        // The app runs; its console socket doesn't yet (systemd ≥ 258 then refuses to start the socket).
        _commands.Handler = (_, args) => new CommandResult(args is ["is-active", "--quiet", "ts-app-turbo-console.socket"] ? 3 : 0, "", "");

        await _deploy.WriteServiceAsync(new AppServiceRequest { User = "hotel", App = "turbo", Command = "dotnet Turbo.Main.dll", Console = true },
            CancellationToken.None);

        var stop = _commands.Calls.IndexOf("systemctl stop ts-app-turbo.service");
        var socket = _commands.Calls.IndexOf("systemctl enable --now ts-app-turbo-console.socket");
        var start = _commands.Calls.IndexOf("systemctl start ts-app-turbo.service");
        Assert.True(stop >= 0 && stop < socket && socket < start, string.Join('\n', _commands.Calls));
    }

    [Fact]
    public void A_console_is_a_fifo_systemd_keeps_open_as_stdin()
    {
        var unit = DeployManager.RenderUnit(new PasswdEntry("hotel", 1001, 1001, "/home/hotel", "/bin/bash"), "turbo", "out",
            new AppServiceRequest { Console = true });
        var socket = DeployManager.RenderConsoleSocket("turbo");

        Assert.Contains("Requires=ts-app-turbo-console.socket", unit);
        Assert.Contains("Sockets=ts-app-turbo-console.socket\nStandardInput=fd:console\nStandardOutput=journal", unit);
        Assert.Contains("ListenFIFO=/run/turbo-stack/console/turbo", socket);
        Assert.Contains("SocketMode=0600", socket);
        Assert.Contains("FileDescriptorName=console", socket);
        Assert.DoesNotContain("StandardInput", DeployManager.RenderUnit(new PasswdEntry("hotel", 1001, 1001, "/home/hotel", "/bin/bash"), "web", "",
            new AppServiceRequest()));
    }

    [Theory]
    [InlineData("say hi\nshutdown")]
    [InlineData("\u001b[2J")]
    [InlineData("")]
    public async Task Console_input_is_one_printable_line(string line) =>
        await Assert.ThrowsAsync<RpcException>(() => _deploy.SendConsoleInputAsync(new ConsoleInput { App = "turbo", Line = line }, CancellationToken.None));

    [Fact]
    public async Task Console_input_needs_the_app_to_have_a_console()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            _deploy.SendConsoleInputAsync(new ConsoleInput { App = "turbo", Line = "about" }, CancellationToken.None));

        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Theory]
    [InlineData("apps/web/shared", "/home/hotel/apps/web/shared")]
    [InlineData("/apps//web/", "/home/hotel/apps/web")]
    [InlineData("", "/home/hotel")]
    public void File_paths_stay_in_the_home_folder(string path, string expected) =>
        Assert.Equal(expected, new TurboManager.Agent.Files.FilesManager(_deploy, _commands).Resolve("hotel", path).Absolute);

    [Theory]
    [InlineData("../other")]
    [InlineData("apps/../../etc")]
    [InlineData("./x")]
    public void File_paths_cannot_climb_out(string path) =>
        Assert.Throws<RpcException>(() => new TurboManager.Agent.Files.FilesManager(_deploy, _commands).Resolve("hotel", path));

    [Fact]
    public async Task File_operations_run_as_the_app_user_and_spare_the_home_folder()
    {
        var files = new TurboManager.Agent.Files.FilesManager(_deploy, _commands);

        await files.DeleteAsync(new FilePath { User = "hotel", Path = "apps/web/shared/old.log" }, CancellationToken.None);
        Assert.StartsWith("runuser -u hotel -- env -i --chdir=/home/hotel", _commands.Calls[^1]);
        Assert.EndsWith("rm -rf -- /home/hotel/apps/web/shared/old.log", _commands.Calls[^1]);

        await Assert.ThrowsAsync<RpcException>(() => files.DeleteAsync(new FilePath { User = "hotel", Path = "" }, CancellationToken.None));
        Assert.Throws<RpcException>(() => files.Resolve("root", "etc"));
    }

    [Fact]
    public void Node_runtime_never_prompts_for_downloads() =>
        Assert.Equal("0", DeployManager.RuntimeEnvironment(["node"])["COREPACK_ENABLE_DOWNLOAD_PROMPT"]);

    [Fact]
    public async Task Activation_is_an_atomic_symlink_swap()
    {
        Directory.CreateDirectory(Path.Combine(_host.Path, "home/hotel/apps/web/releases/20261011120000-abcdef1"));

        await _deploy.ActivateAsync(new ReleaseRequest { User = "hotel", App = "web", Release = "20261011120000-abcdef1" }, CancellationToken.None);

        Assert.Contains("ln -sfn \"releases/$2\" current.tmp && mv -Tf current.tmp current", Assert.Single(_commands.Calls));
    }

    [Fact]
    public async Task Activating_a_missing_release_fails()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            _deploy.ActivateAsync(new ReleaseRequest { User = "hotel", App = "web", Release = "20261011120000-abcdef1" }, CancellationToken.None));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public void Unit_runs_as_the_app_user_from_current()
    {
        var unit = DeployManager.RenderUnit(new PasswdEntry("hotel", 1001, 1001, "/home/hotel", "/bin/bash"), "turbo", "",
            new AppServiceRequest { MemoryMaxMb = 2048, StopTimeoutSeconds = 60 });

        Assert.Contains("User=hotel", unit);
        Assert.Contains("WorkingDirectory=/home/hotel/apps/turbo/current", unit);
        Assert.Contains("ExecStart=/etc/turbo-stack/apps/turbo.run", unit);
        Assert.Contains("MemoryMax=2048M", unit);
        Assert.Contains("TimeoutStopSec=60", unit);
        Assert.Contains("NoNewPrivileges=yes", unit);
    }

    [Fact]
    public void Environment_file_escapes_quotes_and_backslashes()
    {
        var file = DeployManager.RenderEnvironmentFile(new Dictionary<string, string> { ["B"] = "say \"hi\" \\o/", ["A"] = "$HOME stays literal" });

        Assert.Contains("A=\"$HOME stays literal\"\nB=\"say \\\"hi\\\" \\\\o/\"\n", file);
    }

    [Fact]
    public async Task Git_needs_the_deploy_key_to_exist()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => _deploy.TestAccessAsync(new GitSyncRequest
        {
            User = "hotel",
            RepoDir = "turbo",
            Branch = "main",
            Repository = new RepositoryAccess { Owner = "billsonnn", Name = "turbo-cloud", KeyName = "turbo" },
        }, CancellationToken.None));

        Assert.Contains("hasn't been created", error.Status.Detail);
    }

    [Fact]
    public void Runtime_environment_prepends_toolchains()
    {
        var environment = DeployManager.RuntimeEnvironment(["dotnet", "node"]);

        Assert.StartsWith("/opt/turbo-stack/runtimes/dotnet:/opt/turbo-stack/runtimes/node/bin:", environment["PATH"]);
        Assert.Equal("/opt/turbo-stack/runtimes/dotnet", environment["DOTNET_ROOT"]);
        Assert.Throws<RpcException>(() => DeployManager.RuntimeEnvironment(["php"]));
    }

    public void Dispose() => _host.Dispose();
}

public sealed class SftpManagerTests : IDisposable
{
    private readonly FakeHostRoot _host = new FakeHostRoot()
        .WithFile("etc/passwd", "root:x:0:0:root:/root:/bin/bash\nhotel:x:1001:1001::/home/hotel:/bin/bash\nupload:x:1002:1002::/srv/ts-sftp/upload:/usr/sbin/nologin\n")
        .WithFile("etc/group", "root:x:0:\nts-apps:x:999:hotel\nts-sftp:x:998:upload\n")
        .WithFile("etc/shadow", "upload:$6$hash:19000:0:99999:7:::\n");

    private readonly FakeCommandRunner _commands = new();
    private readonly TurboManager.Agent.Ssh.SftpManager _sftp;

    public SftpManagerTests()
    {
        var options = Options.Create(new AgentOptions { HostRoot = _host.Path });
        var deploy = new DeployManager(_commands, options, NullLogger<DeployManager>.Instance);
        _sftp = new TurboManager.Agent.Ssh.SftpManager(_commands, new TurboManager.Agent.Files.FilesManager(deploy, _commands), options,
            NullLogger<TurboManager.Agent.Ssh.SftpManager>.Instance);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Account_comments_are_valid_passwd_fields()
    {
        // useradd refuses a comment with a colon (exit 3); a comma would split the GECOS field.
        var comment = TurboManager.Agent.Ssh.SftpManager.Comment("hotel", "sites/a:b,c\nd");

        Assert.Equal("Turbo Stack SFTP for hotel (sites/a b c d)", comment);
    }

    [Fact]
    public void Accounts_are_chrooted_sftp_only_without_tunnels_or_a_terminal()
    {
        var config = TurboManager.Agent.Ssh.SftpManager.RenderDropIn();

        Assert.Contains("Match Group ts-sftp", config);
        Assert.Contains("ChrootDirectory /srv/ts-sftp/%u", config);
        Assert.Contains("ForceCommand internal-sftp -d /files", config);
        Assert.Contains("PermitTTY no", config);
        Assert.Contains("AllowTcpForwarding no", config);
        Assert.Contains("AuthorizedKeysFile /etc/turbo-stack/sftp-keys/%u", config);
    }

    [Fact]
    public async Task Keys_are_checked_and_written_where_the_account_cannot_change_them()
    {
        await Assert.ThrowsAsync<RpcException>(() =>
            _sftp.SetKeysAsync(new SftpCredentials { Name = "upload", PublicKeys = { "command=\"sh\" ssh-ed25519 AAAA" } }, CancellationToken.None));
        await _sftp.SetKeysAsync(new SftpCredentials { Name = "upload", PublicKeys = { "ssh-ed25519 AAAAC3Nz me@laptop" } }, CancellationToken.None);

        Assert.Equal("ssh-ed25519 AAAAC3Nz me@laptop\n", File.ReadAllText(Path.Combine(_host.Path, "etc/turbo-stack/sftp-keys/upload")));
        Assert.Equal(1, Assert.Single(_sftp.List().Accounts).Keys);
        Assert.True(_sftp.List().Accounts[0].Password);
    }

    [Fact]
    public async Task Deleting_never_touches_a_folder_that_is_still_mounted()
    {
        _host.WithFile("proc/self/mountinfo", "1 2 0:1 / /srv/ts-sftp/upload/files rw - ext4 /dev/sda1 rw\n");

        await Assert.ThrowsAsync<RpcException>(() => _sftp.DeleteAsync(new SftpAccountRef { Name = "upload" }, CancellationToken.None));
        Assert.DoesNotContain(_commands.Calls, c => c.StartsWith("userdel", StringComparison.Ordinal) || c.StartsWith("rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accounts_act_as_the_app_user_and_deleting_one_spares_the_apps_processes()
    {
        _commands.Handler = (file, _) => new CommandResult(0, file == "systemd-escape" ? "srv-ts_sftp-assets-files.mount\n" : "", "");
        Directory.CreateDirectory(Path.Combine(_host.Path, "etc/systemd/system"));

        await _sftp.CreateAsync(new SftpAccountRequest { Name = "assets", AppUser = "hotel", Path = "sites/assets" }, CancellationToken.None);

        // Uploads belong to the app user whatever modes the client picks: same uid and gid, no group of its own.
        Assert.Contains(_commands.Calls, c => c.StartsWith("useradd --non-unique --uid 1001 --gid 1001 --no-user-group ", StringComparison.Ordinal)
            && c.EndsWith(" assets", StringComparison.Ordinal));

        _host.WithFile("etc/group", "root:x:0:\nts-apps:x:999:hotel\nts-sftp:x:998:upload,assets\n");
        await _sftp.DeleteAsync(new SftpAccountRef { Name = "assets" }, CancellationToken.None);

        Assert.DoesNotContain(_commands.Calls, c => c.StartsWith("pkill", StringComparison.Ordinal) && c.Contains(" -u ", StringComparison.Ordinal));
        Assert.Contains("pkill -KILL -f ^sshd(-session|-auth)?: assets( |@|$)", _commands.Calls);
        Assert.Contains("userdel --force assets", _commands.Calls);
    }

    [Fact]
    public async Task Only_ts_sftp_members_are_accounts() =>
        await Assert.ThrowsAsync<RpcException>(() => _sftp.SetPasswordAsync(new SftpCredentials { Name = "hotel", Password = "a long enough one" }, CancellationToken.None));
}
