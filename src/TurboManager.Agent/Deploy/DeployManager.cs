using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Security;
using TurboManager.Agent.Ssh;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Deploy;

/// <summary>Runs every app-related step as the app's own user. See deploy.proto for the layout.</summary>
public sealed class DeployManager(ICommandRunner commands, IOptions<AgentOptions> options, ILogger<DeployManager> logger)
{
    public const string AppsGroup = "tm-apps";

    public const string RuntimeRoot = "/opt/turbo-manager/runtimes";

    public const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private string HostPath(string absolutePath) => Path.Combine(options.Value.HostRoot, absolutePath.TrimStart('/'));

    private string UnitPath(string app) => HostPath($"/etc/systemd/system/tm-app-{app}.service");

    private string ConsoleSocketPath(string app) => HostPath($"/etc/systemd/system/tm-app-{app}-console.socket");

    public static string ConsoleFifo(string app) => $"/run/{Branding.Slug}/console/{app}";

    private string AppConfigDirectory => HostPath($"/etc/{Branding.Slug}/apps");

    public async Task<AppUserInfo> EnsureUserAsync(string user, CancellationToken cancellationToken)
    {
        DeployValidation.User(user);

        if (FindAppUser(user) is { } existing)
        {
            return new AppUserInfo { User = user, Home = existing.Home, Uid = existing.Uid };
        }

        if (PasswdFile.FindUser(user, HostPath("/etc/passwd")) is not null)
        {
            throw Validate.FailedPrecondition($"{user} already exists and isn't an app user created by {Branding.ProductName}.");
        }

        await commands.RunCheckedAsync("groupadd", ["-f", AppsGroup], cancellationToken: cancellationToken);
        await commands.RunCheckedAsync("useradd", ["--create-home", "--shell", "/bin/bash", "--groups", AppsGroup,
            "--comment", $"{Branding.ProductName} app user", user], cancellationToken: cancellationToken);

        var created = FindAppUser(user) ?? throw Validate.FailedPrecondition($"Creating {user} failed.");

        // Private home; nginx may only traverse it to reach releases it was given access to.
        await commands.RunCheckedAsync("chmod", ["750", created.Home], cancellationToken: cancellationToken);
        await commands.RunAsync("setfacl", ["-m", "u:www-data:x", created.Home], cancellationToken: cancellationToken);
        logger.LogInformation("Created app user {User}", user);

        return new AppUserInfo { User = user, Home = created.Home, Uid = created.Uid, Created = true };
    }

    public async Task<DeployKey> EnsureDeployKeyAsync(DeployKeyRequest request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var key = DeployValidation.KeyName(request.KeyName);
        var directory = $"{user.Home}/.ssh/tm";
        var path = $"{directory}/{key}";
        var created = false;

        if (!File.Exists(HostPath(path)))
        {
            await RunAsUserCheckedAsync(user, null, "bash", ["-c", "umask 077 && mkdir -p \"$1\"", "bash", directory], cancellationToken);
            await RunAsUserCheckedAsync(user, null, "ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", $"turbo-manager {user.Name}/{key}", "-f", path],
                cancellationToken);
            created = true;
        }

        var publicKey = (await File.ReadAllTextAsync(HostPath(path + ".pub"), cancellationToken)).Trim();

        return new DeployKey
        {
            KeyName = key,
            PublicKey = publicKey,
            Fingerprint = AuthorizedKeysParser.TryParse(publicKey, out var parsed, out _) ? parsed.Fingerprint : "",
            Created = created,
        };
    }

    public async Task<GitAccessResult> TestAccessAsync(GitSyncRequest request, CancellationToken cancellationToken)
    {
        var (user, url, gitConfig) = await PrepareGitAsync(request, cancellationToken);
        var result = await RunAsUserAsync(user, null, GitEnvironment(user), "git", [.. gitConfig, "ls-remote", url, $"refs/heads/{request.Branch}"],
            TimeSpan.FromSeconds(60), cancellationToken);

        if (!result.Success)
        {
            return new GitAccessResult { Ok = false, Error = CleanGitError(result.ErrorText) };
        }

        var head = result.StdOut.Split('\t', 2)[0].Trim();

        return head.Length == 0
            ? new GitAccessResult { Ok = false, Error = $"The branch \"{request.Branch}\" doesn't exist in this repository." }
            : new GitAccessResult { Ok = true, Head = head };
    }

    /// <summary>Clones or updates ~/repos/&lt;dir&gt; to the branch tip (submodules included), discarding local changes.</summary>
    public async Task SyncAsync(GitSyncRequest request, Func<DeployOutput, Task> write, CancellationToken cancellationToken)
    {
        var (user, url, gitConfig) = await PrepareGitAsync(request, cancellationToken);
        var repoDir = $"{user.Home}/repos/{DeployValidation.RepoDir(request.RepoDir)}";
        var env = GitEnvironment(user);
        var hostRepo = HostPath(repoDir);

        async Task Git(params string[] arguments)
        {
            await write(new DeployOutput { Line = $"$ git {string.Join(' ', arguments.Where(a => !a.StartsWith("url.", StringComparison.Ordinal) && a != "-c"))}" });
            var exit = await StreamAsUserAsync(user, null, env, "git", [.. gitConfig, .. arguments], line => write(new DeployOutput { Line = line }),
                TimeSpan.FromMinutes(20), cancellationToken);

            if (exit != 0)
            {
                throw new RpcException(new Status(StatusCode.Aborted, $"git {arguments.First(a => !a.StartsWith('-'))} failed (exit {exit})."));
            }
        }

        var currentRemote = Directory.Exists(Path.Combine(hostRepo, ".git"))
            ? (await RunAsUserAsync(user, null, env, "git", ["-C", repoDir, "remote", "get-url", "origin"], TimeSpan.FromSeconds(20), cancellationToken)).StdOut.Trim()
            : null;

        if (currentRemote != url)
        {
            if (currentRemote is not null)
            {
                await RunAsUserCheckedAsync(user, null, "rm", ["-rf", "--", repoDir], cancellationToken);
            }

            await RunAsUserCheckedAsync(user, null, "mkdir", ["-p", $"{user.Home}/repos"], cancellationToken);
            await Git("clone", "--branch", request.Branch, "--single-branch", "--no-tags", url, repoDir);
        }
        else
        {
            await Git("-C", repoDir, "fetch", "--prune", "--no-tags", "origin", $"+refs/heads/{request.Branch}:refs/remotes/origin/{request.Branch}");
            await Git("-C", repoDir, "checkout", "--force", "-B", request.Branch, $"origin/{request.Branch}");
            await Git("-C", repoDir, "clean", "-ffdx");
        }

        if (request.Submodules)
        {
            await Git("-C", repoDir, "submodule", "sync", "--recursive");
            await Git("-C", repoDir, "submodule", "update", "--init", "--recursive", "--force");
        }

        var log = await RunAsUserAsync(user, null, env, "git", ["-C", repoDir, "log", "-1", "--format=%H%n%an%n%s"], TimeSpan.FromSeconds(20), cancellationToken);
        var parts = log.StdOut.Split('\n', 3);

        await write(new DeployOutput
        {
            Finished = true,
            Success = true,
            Commit = parts.ElementAtOrDefault(0)?.Trim() ?? "",
            CommitAuthor = parts.ElementAtOrDefault(1)?.Trim() ?? "",
            CommitMessage = parts.ElementAtOrDefault(2)?.Trim() ?? "",
        });
    }

    /// <summary>Copies the checkout into a new release (without .git) and links its shared paths.</summary>
    public async Task CreateReleaseAsync(ReleaseRequest request, Func<DeployOutput, Task> write, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var app = DeployValidation.App(request.App);
        var release = DeployValidation.Release(request.Release);
        var repoDir = $"{user.Home}/repos/{DeployValidation.RepoDir(request.RepoDir)}";
        var appDir = AppDir(user, app);
        var releaseDir = $"{appDir}/releases/{release}";

        if (!Directory.Exists(HostPath(repoDir)))
        {
            throw Validate.FailedPrecondition("The repository hasn't been fetched yet.");
        }

        await RunAsUserCheckedAsync(user, null, "mkdir", ["-p", $"{appDir}/releases", $"{appDir}/shared"], cancellationToken);
        await write(new DeployOutput { Line = $"Copying {request.RepoDir} into release {release}…" });
        await RunAsUserCheckedAsync(user, null, "rsync", ["-a", "--delete", "--exclude=.git", "--exclude=.git/", $"{repoDir}/", $"{releaseDir}/"], cancellationToken,
            TimeSpan.FromMinutes(20));

        foreach (var shared in request.Shared)
        {
            var path = DeployValidation.RelativePath(shared.Path);
            await write(new DeployOutput { Line = $"Linking shared {(shared.Directory ? "directory" : "file")} {path}" });

            // Fixed script, validated arguments: seed shared/<path> from the release the first time,
            // then replace the release's copy with a symlink to it.
            await RunAsUserCheckedAsync(user, null, "bash",
            [
                "-euo", "pipefail", "-c",
                """
                release="$1"; shared="$2"; path="$3"; kind="$4"
                mkdir -p "$(dirname "$shared/$path")" "$(dirname "$release/$path")"
                if [ ! -e "$shared/$path" ]; then
                    if [ -e "$release/$path" ] && [ ! -L "$release/$path" ]; then cp -a "$release/$path" "$shared/$path"
                    elif [ "$kind" = dir ]; then mkdir -p "$shared/$path"
                    else touch "$shared/$path"; fi
                fi
                rm -rf "$release/$path"
                ln -s "$shared/$path" "$release/$path"
                """,
                "bash", releaseDir, $"{appDir}/shared", path, shared.Directory ? "dir" : "file",
            ], cancellationToken);
        }

        if (request.WebReadable)
        {
            await GrantWebReadAsync(user, appDir, releaseDir, cancellationToken);
        }

        await write(new DeployOutput { Finished = true, Success = true, Release = release });
    }

    public async Task<int> BuildAsync(BuildRequest request, Func<string, Task> write, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var app = DeployValidation.App(request.App);
        var release = DeployValidation.Release(request.Release);
        var appDir = AppDir(user, app);
        var releaseDir = $"{appDir}/releases/{release}";
        var workingDirectory = DeployValidation.RelativePath(request.WorkingDirectory, allowEmpty: true);
        var script = DeployValidation.Script(request.Script);
        var environment = new Dictionary<string, string>(DeployValidation.Environment(request.Environment))
        {
            ["TM_APP_DIR"] = appDir,
            ["TM_RELEASE_DIR"] = releaseDir,
            ["TM_SHARED_DIR"] = $"{appDir}/shared",
            ["CI"] = "true",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

        foreach (var (key, value) in RuntimeEnvironment(request.Runtimes))
        {
            environment[key] = value;
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            await write("No build script; nothing to build.");

            return 0;
        }

        var cwd = workingDirectory.Length == 0 ? releaseDir : $"{releaseDir}/{workingDirectory}";
        var timeout = TimeSpan.FromMinutes(request.TimeoutMinutes is > 0 and <= 240 ? request.TimeoutMinutes : 30);

        // A private temp directory: /tmp is shared with other users, and often mounted noexec, which breaks
        // tools that run helper scripts from it (Yarn puts its package-binary wrappers there).
        var temp = $"{user.Home}/.cache/tm-build/{app}";
        environment["TMPDIR"] = temp;
        await RunAsUserCheckedAsync(user, null, "bash", ["-c", "umask 077 && rm -rf -- \"$1\" && mkdir -p -- \"$1\"", "bash", temp], cancellationToken);

        try
        {
            return await StreamAsUserAsync(user, cwd, environment, "bash", ["-euo", "pipefail", "-c", script], line => write(line), timeout, cancellationToken);
        }
        finally
        {
            await RunAsUserAsync(user, null, new Dictionary<string, string>(), "rm", ["-rf", "--", temp], TimeSpan.FromMinutes(2), CancellationToken.None);
        }
    }

    /// <summary>Points current/ at a release in one atomic rename.</summary>
    public async Task<ReleaseState> ActivateAsync(ReleaseRequest request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var app = DeployValidation.App(request.App);
        var release = DeployValidation.Release(request.Release);
        var appDir = AppDir(user, app);

        if (!Directory.Exists(HostPath($"{appDir}/releases/{release}")))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Release {release} no longer exists."));
        }

        await RunAsUserCheckedAsync(user, null, "bash",
            ["-euo", "pipefail", "-c", "cd \"$1\" && ln -sfn \"releases/$2\" current.tmp && mv -Tf current.tmp current", "bash", appDir, release], cancellationToken);

        return GetState(request.User, app);
    }

    public ReleaseState GetState(string userName, string app)
    {
        var user = RequireAppUser(userName);
        var appDir = AppDir(user, DeployValidation.App(app));
        var releases = HostPath($"{appDir}/releases");
        var current = HostPath($"{appDir}/current");
        var state = new ReleaseState { AppDir = appDir };

        if (Directory.Exists(releases))
        {
            state.Releases.AddRange(Directory.GetDirectories(releases).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).Reverse());
        }

        var target = new FileInfo(current).LinkTarget;
        state.Current = target is null ? "" : Path.GetFileName(target.TrimEnd('/'));

        return state;
    }

    public async Task<ReleaseState> PruneAsync(PruneRequest request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var state = GetState(request.User, request.App);
        var keep = Math.Clamp(request.Keep, 1, 50);

        foreach (var old in state.Releases.Skip(keep).Where(r => r != state.Current))
        {
            await RunAsUserCheckedAsync(user, null, "rm", ["-rf", "--", $"{state.AppDir}/releases/{DeployValidation.Release(old)}"], cancellationToken);
        }

        return GetState(request.User, request.App);
    }

    public async Task<SiteDirectory> EnsureSiteDirectoryAsync(AppRef request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var directory = $"{user.Home}/sites/{DeployValidation.App(request.App)}";
        await RunAsUserCheckedAsync(user, null, "mkdir", ["-p", directory], cancellationToken);

        foreach (var parent in new[] { user.Home, $"{user.Home}/sites" })
        {
            await commands.RunAsync("setfacl", ["-m", "u:www-data:x", parent], cancellationToken: cancellationToken);
        }

        // Read for nginx on what's there now and (default ACL) on whatever gets uploaded later.
        await commands.RunCheckedAsync("setfacl", ["-R", "-m", "u:www-data:rX", "-m", "d:u:www-data:rX", directory], cancellationToken: cancellationToken,
            timeout: TimeSpan.FromMinutes(5));

        return new SiteDirectory { Path = directory };
    }

    public async Task RemoveAppAsync(AppRef request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        await RemoveServiceAsync(request, cancellationToken);
        await RunAsUserCheckedAsync(user, null, "rm", ["-rf", "--", AppDir(user, DeployValidation.App(request.App))], cancellationToken);
    }

    /// <summary>
    /// Writes tm-app-&lt;app&gt;.service. The command goes into a root-owned launcher script (no quoting
    /// issues in the unit file); environment goes into a root-only EnvironmentFile.
    /// </summary>
    public async Task WriteServiceAsync(AppServiceRequest request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var app = DeployValidation.App(request.App);
        var command = DeployValidation.Command(request.Command);
        var workingDirectory = DeployValidation.RelativePath(request.WorkingDirectory, allowEmpty: true);
        var environment = new Dictionary<string, string>(DeployValidation.Environment(request.Environment));

        foreach (var (key, value) in RuntimeEnvironment(request.Runtimes))
        {
            environment[key] = value;
        }

        Directory.CreateDirectory(AppConfigDirectory);
        var launcher = Path.Combine(AppConfigDirectory, $"{app}.run");
        var envFile = Path.Combine(AppConfigDirectory, $"{app}.env");

        await File.WriteAllTextAsync(launcher, $"#!/bin/bash\n# Managed by {Branding.ProductName}.\nexec {command}\n", cancellationToken);
        await File.WriteAllTextAsync(envFile, RenderEnvironmentFile(environment), cancellationToken);
        await File.WriteAllTextAsync(UnitPath(app), RenderUnit(user, app, workingDirectory, request), cancellationToken);

        if (request.Console)
        {
            await File.WriteAllTextAsync(ConsoleSocketPath(app), RenderConsoleSocket(app), cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(envFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await commands.RunCheckedAsync("systemctl", ["daemon-reload"], cancellationToken: cancellationToken);
        await commands.RunCheckedAsync("systemctl", ["enable", $"tm-app-{app}.service"], cancellationToken: cancellationToken);

        if (request.Console)
        {
            var socket = $"tm-app-{app}-console.socket";
            var service = $"tm-app-{app}.service";
            // systemd ≥ 258 refuses to start a socket whose service already runs (24.04's 255 allowed it): turning the console
            // on for a running app means stopping it, starting the socket, then starting the app on it.
            var restart = !(await commands.RunAsync("systemctl", ["is-active", "--quiet", socket], cancellationToken: cancellationToken)).Success
                && (await commands.RunAsync("systemctl", ["is-active", "--quiet", service], cancellationToken: cancellationToken)).Success;

            if (restart)
            {
                await commands.RunCheckedAsync("systemctl", ["stop", service], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
            }

            await commands.RunCheckedAsync("systemctl", ["enable", "--now", socket], cancellationToken: cancellationToken);

            if (restart)
            {
                await commands.RunCheckedAsync("systemctl", ["start", service], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
            }
        }
        else if (File.Exists(ConsoleSocketPath(app)))
        {
            await RemoveConsoleSocketAsync(app, cancellationToken);
            await commands.RunCheckedAsync("systemctl", ["daemon-reload"], cancellationToken: cancellationToken);
        }
    }

    public static string RenderConsoleSocket(string app) =>
        $"# Managed by {Branding.ProductName}: the console (stdin) of app \"{app}\".\n" +
        "[Unit]\n" +
        $"Description={Branding.ProductName} console for app {app}\n\n" +
        "[Socket]\n" +
        $"ListenFIFO={ConsoleFifo(app)}\n" +
        "FileDescriptorName=console\n" +
        // Only root (the agent) writes to it.
        "SocketMode=0600\n" +
        "RemoveOnStop=yes\n" +
        $"Service=tm-app-{app}.service\n\n" +
        "[Install]\nWantedBy=sockets.target\n";

    private async Task RemoveConsoleSocketAsync(string app, CancellationToken cancellationToken)
    {
        if (!File.Exists(ConsoleSocketPath(app)))
        {
            return;
        }

        await commands.RunAsync("systemctl", ["disable", "--now", $"tm-app-{app}-console.socket"], timeout: TimeSpan.FromMinutes(1),
            cancellationToken: cancellationToken);
        File.Delete(ConsoleSocketPath(app));
    }

    /// <summary>Types one line into an app's console. The app must have its console on (and its socket running).</summary>
    public async Task SendConsoleInputAsync(ConsoleInput request, CancellationToken cancellationToken)
    {
        var app = DeployValidation.App(request.App);
        var line = request.Line ?? "";

        if (line.Length is 0 or > 1000 || line.Any(char.IsControl))
        {
            throw Validate.Invalid("A console command is one line of up to 1000 printable characters.");
        }

        var fifo = HostPath(ConsoleFifo(app));

        if (!File.Exists(ConsoleSocketPath(app)) || !File.Exists(fifo) || new FileInfo(fifo).LinkTarget is not null)
        {
            throw Validate.FailedPrecondition($"{app} has no console (turn it on in the app's settings, then restart it).");
        }

        // systemd holds the reading end open, so opening it for writing doesn't wait.
        await using var stream = new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
        logger.LogInformation("Console input for {App}: {Line}", app, line);
    }

    public async Task RemoveServiceAsync(AppRef request, CancellationToken cancellationToken)
    {
        var app = DeployValidation.App(request.App);

        if (!File.Exists(UnitPath(app)))
        {
            return;
        }

        await commands.RunAsync("systemctl", ["disable", "--now", $"tm-app-{app}.service"], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);

        await RemoveConsoleSocketAsync(app, cancellationToken);

        foreach (var file in new[] { UnitPath(app), Path.Combine(AppConfigDirectory, $"{app}.run"), Path.Combine(AppConfigDirectory, $"{app}.env") })
        {
            File.Delete(file);
        }

        await commands.RunCheckedAsync("systemctl", ["daemon-reload"], cancellationToken: cancellationToken);
    }

    public static string RenderUnit(PasswdEntry user, string app, string workingDirectory, AppServiceRequest request)
    {
        var cwd = workingDirectory.Length == 0 ? $"{user.Home}/apps/{app}/current" : $"{user.Home}/apps/{app}/current/{workingDirectory}";
        var builder = new StringBuilder()
            .Append($"# Managed by {Branding.ProductName}: app \"{app}\". Edit it in the panel.\n")
            .Append("[Unit]\n")
            .Append($"Description={Branding.ProductName} app {app}\n")
            .Append("After=network-online.target\nWants=network-online.target\n")
            .Append(request.Console ? $"Requires=tm-app-{app}-console.socket\nAfter=tm-app-{app}-console.socket\n" : "")
            .Append("\n[Service]\n")
            .Append("Type=simple\n")
            .Append($"User={user.Name}\nGroup={user.Name}\n")
            .Append($"WorkingDirectory={cwd}\n")
            .Append($"EnvironmentFile=/etc/{Branding.Slug}/apps/{app}.env\n")
            .Append($"ExecStart=/etc/{Branding.Slug}/apps/{app}.run\n")
            // The console: stdin is the FIFO systemd holds open, so the app never sees end-of-input; output stays in the journal.
            .Append(request.Console
                ? $"Sockets=tm-app-{app}-console.socket\nStandardInput=fd:console\nStandardOutput=journal\nStandardError=journal\n"
                : "")
            .Append("Restart=always\nRestartSec=3\n")
            .Append($"TimeoutStopSec={(request.StopTimeoutSeconds is > 0 and <= 600 ? request.StopTimeoutSeconds : 30)}\n")
            .Append("KillSignal=SIGTERM\n")
            .Append("NoNewPrivileges=yes\nPrivateTmp=yes\nProtectSystem=full\n");

        if (request.MemoryMaxMb > 0)
        {
            builder.Append($"MemoryMax={request.MemoryMaxMb}M\n");
        }

        return builder.Append("\n[Install]\nWantedBy=multi-user.target\n").ToString();
    }

    /// <summary>systemd EnvironmentFile: KEY="value" with backslash and quote escaped (no expansion happens).</summary>
    public static string RenderEnvironmentFile(IReadOnlyDictionary<string, string> environment) =>
        $"# Managed by {Branding.ProductName}.\n" + string.Concat(environment.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key}=\"{e.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"\n"));

    public async Task<Runtimes> ListRuntimesAsync(CancellationToken cancellationToken)
    {
        var reply = new Runtimes();
        var dotnet = HostPath($"{RuntimeRoot}/dotnet/dotnet");
        var node = HostPath($"{RuntimeRoot}/node/bin/node");

        reply.Items.Add(new RuntimeInfo
        {
            Name = "dotnet",
            Path = $"{RuntimeRoot}/dotnet",
            Installed = File.Exists(dotnet),
            Version = File.Exists(dotnet) ? (await commands.RunAsync(dotnet, ["--version"], cancellationToken: cancellationToken)).StdOut.Trim() : "",
        });
        reply.Items.Add(new RuntimeInfo
        {
            Name = "node",
            Path = $"{RuntimeRoot}/node",
            Installed = File.Exists(node),
            Version = File.Exists(node) ? (await commands.RunAsync(node, ["--version"], cancellationToken: cancellationToken)).StdOut.Trim().TrimStart('v') : "",
        });

        return reply;
    }

    /// <summary>
    /// Installs a toolchain under /opt/turbo-manager/runtimes (readable by every app user): .NET via
    /// Microsoft's dotnet-install script, Node from nodejs.org with its published SHA-256 checked.
    /// </summary>
    public async Task InstallRuntimeAsync(RuntimeRequest request, Func<string, ValueTask> write, CancellationToken cancellationToken)
    {
        var channel = request.Channel.Trim();
        string script;

        switch (request.Name)
        {
            case "dotnet" when System.Text.RegularExpressions.Regex.IsMatch(channel, @"^\d{1,2}\.\d$"):
                script = """
                    set -euo pipefail
                    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
                    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh"
                    bash "$tmp/dotnet-install.sh" --channel "$1" --install-dir "$2/dotnet" --no-path
                    "$2/dotnet/dotnet" --info | head -n 20
                    """;
                break;
            case "node" when System.Text.RegularExpressions.Regex.IsMatch(channel, @"^\d{2}$"):
                script = """
                    set -euo pipefail
                    arch="$(uname -m)"; case "$arch" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
                    base="https://nodejs.org/dist/latest-v$1.x"
                    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
                    curl -fsSL "$base/SHASUMS256.txt" -o "$tmp/SHASUMS256.txt"
                    file="$(grep -oE "node-v[0-9.]+-linux-$arch\.tar\.xz" "$tmp/SHASUMS256.txt" | head -n1)"
                    echo "Downloading $file"
                    curl -fsSL "$base/$file" -o "$tmp/$file"
                    (cd "$tmp" && grep " $file\$" SHASUMS256.txt | sha256sum -c -)
                    mkdir -p "$2"
                    rm -rf "$2/node.new" && mkdir "$2/node.new"
                    tar -xJf "$tmp/$file" -C "$2/node.new" --strip-components=1 --no-same-owner
                    rm -rf "$2/node" && mv "$2/node.new" "$2/node"
                    "$2/node/bin/node" "$2/node/bin/corepack" enable --install-directory "$2/node/bin"
                    echo "node $("$2/node/bin/node" --version) installed"
                    """;
                break;
            default:
                throw Validate.Invalid("Choose dotnet (channel like 10.0) or node (major version like 24).");
        }

        var exit = await commands.StreamAsync("bash", ["-c", script, "bash", channel, RuntimeRoot], write, mergeStdErr: true, cancellationToken);

        if (exit != 0)
        {
            throw new RpcException(new Status(StatusCode.Aborted, $"Installing {request.Name} failed (exit {exit})."));
        }

        // Archives carry their builder's uid; shared toolchains must only be writable by root.
        await commands.RunAsync("chown", ["-R", "root:root", RuntimeRoot], cancellationToken: cancellationToken);
        await commands.RunAsync("chmod", ["-R", "a+rX,go-w", RuntimeRoot], cancellationToken: cancellationToken);
    }

    /// <summary>PATH and runtime variables for builds and services that use the panel's toolchains.</summary>
    public static Dictionary<string, string> RuntimeEnvironment(IEnumerable<string> runtimes)
    {
        var environment = new Dictionary<string, string>();
        var path = new List<string>();

        foreach (var runtime in runtimes.Distinct())
        {
            switch (runtime)
            {
                case "dotnet":
                    path.Add($"{RuntimeRoot}/dotnet");
                    environment["DOTNET_ROOT"] = $"{RuntimeRoot}/dotnet";
                    break;
                case "node":
                    path.Add($"{RuntimeRoot}/node/bin");
                    // Corepack would otherwise ask before downloading the Yarn version a project pins.
                    environment["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
                    break;
                default:
                    throw Validate.Invalid($"Unknown runtime \"{runtime}\".");
            }
        }

        environment["PATH"] = string.Join(':', path.Append(SystemPath));

        return environment;
    }

    private async Task<(PasswdEntry User, string Url, List<string> GitConfig)> PrepareGitAsync(GitSyncRequest request, CancellationToken cancellationToken)
    {
        var user = RequireAppUser(request.User);
        var host = DeployValidation.Host(string.IsNullOrEmpty(request.SshHost) ? "github.com" : request.SshHost);
        var port = request.SshPort is > 0 and < 65536 ? request.SshPort : 22;
        request.SshHost = host;
        request.SshPort = port;
        request.HttpsBase = string.IsNullOrEmpty(request.HttpsBase) ? "https://github.com" : request.HttpsBase;
        request.Branch = DeployValidation.Branch(request.Branch);

        if (!Uri.TryCreate(request.HttpsBase, UriKind.Absolute, out var https) || https.Scheme is not ("https" or "http"))
        {
            throw Validate.Invalid("Invalid HTTPS base URL.");
        }

        foreach (var access in request.SubmoduleAccess.Prepend(request.Repository))
        {
            DeployValidation.Owner(access.Owner);
            DeployValidation.RepositoryName(access.Name);

            if (access.KeyName.Length > 0)
            {
                DeployValidation.KeyName(access.KeyName);

                if (!File.Exists(HostPath($"{user.Home}/.ssh/tm/{access.KeyName}")))
                {
                    throw Validate.FailedPrecondition($"Deploy key {access.KeyName} hasn't been created yet.");
                }
            }
        }

        if (request.KnownHosts.Any(k => k.Contains('\n') || k.Split(' ').Length != 2))
        {
            throw Validate.Invalid("Known host keys must be \"<type> <base64>\".");
        }

        // Write the SSH config + pinned host keys for every key this user has.
        var keys = Directory.Exists(HostPath($"{user.Home}/.ssh/tm"))
            ? Directory.GetFiles(HostPath($"{user.Home}/.ssh/tm"), "*.pub").Select(f => Path.GetFileNameWithoutExtension(f)).ToList()
            : [];

        if (keys.Count > 0)
        {
            await WriteUserFileAsync(user, $"{user.Home}/.ssh/tm/config", GitSsh.RenderConfig(keys, user.Home, host, port), cancellationToken);
            await WriteUserFileAsync(user, $"{user.Home}/.ssh/tm/known_hosts", GitSsh.RenderKnownHosts(host, port, request.KnownHosts), cancellationToken);
        }

        return (user, GitSsh.RemoteUrl(request.Repository, request.HttpsBase), GitSsh.SubmoduleRewrites(request));
    }

    private static Dictionary<string, string> GitEnvironment(PasswdEntry user) => new()
    {
        ["GIT_SSH_COMMAND"] = $"ssh -F {user.Home}/.ssh/tm/config",
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    private async Task GrantWebReadAsync(PasswdEntry user, string appDir, string releaseDir, CancellationToken cancellationToken)
    {
        // Traverse-only on the directories above, read on the release itself.
        foreach (var directory in new[] { user.Home, $"{user.Home}/apps", appDir, $"{appDir}/releases", $"{appDir}/shared" })
        {
            await commands.RunAsync("setfacl", ["-m", "u:www-data:x", directory], cancellationToken: cancellationToken);
        }

        await commands.RunCheckedAsync("setfacl", ["-R", "-m", "u:www-data:rX", releaseDir], cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(5));
    }

    private async Task WriteUserFileAsync(PasswdEntry user, string path, string content, CancellationToken cancellationToken)
    {
        var host = HostPath(path);

        if (new FileInfo(host).LinkTarget is not null || new DirectoryInfo(Path.GetDirectoryName(host)!).LinkTarget is not null)
        {
            throw Validate.FailedPrecondition($"{path} is a symbolic link; refusing to write it.");
        }

        await File.WriteAllTextAsync(host, content, cancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            UnixFileOwnership.SetOwner(host, user.Uid, user.Gid);
        }
    }

    private string AppDir(PasswdEntry user, string app) => $"{user.Home}/apps/{app}";

    /// <summary>An existing user in the tm-apps group with a normal uid: never root or a system account.</summary>
    private PasswdEntry? FindAppUser(string name)
    {
        var user = PasswdFile.FindUser(name, HostPath("/etc/passwd"));

        if (user is null || user.Uid < 1000 || user.Uid >= 60000)
        {
            return null;
        }

        var groupFile = HostPath("/etc/group");
        var members = File.Exists(groupFile)
            ? File.ReadLines(groupFile).Select(l => l.Split(':')).FirstOrDefault(p => p.Length >= 4 && p[0] == AppsGroup)?[3].Split(',') ?? []
            : [];

        return members.Contains(name) ? user : null;
    }

    public PasswdEntry RequireAppUser(string name) =>
        FindAppUser(DeployValidation.User(name)) ?? throw Validate.FailedPrecondition($"{name} is not an app user created by {Branding.ProductName}.");

    public (string Command, List<string> Arguments) AsUser(PasswdEntry user, string? cwd, IReadOnlyDictionary<string, string> environment, string program,
        IEnumerable<string> arguments)
    {
        List<string> result = ["-u", user.Name, "--", "env", "-i"];

        if (cwd is not null)
        {
            result.Add($"--chdir={cwd}");
        }

        var env = new Dictionary<string, string>
        {
            ["HOME"] = user.Home,
            ["USER"] = user.Name,
            ["LOGNAME"] = user.Name,
            ["PATH"] = SystemPath,
            ["LANG"] = "C.UTF-8",
            ["SHELL"] = "/bin/bash",
        };

        foreach (var (key, value) in environment)
        {
            env[key] = value;
        }

        result.AddRange(env.Select(e => $"{e.Key}={e.Value}"));
        result.Add(program);
        result.AddRange(arguments);

        return ("runuser", result);
    }

    private async Task<CommandResult> RunAsUserAsync(PasswdEntry user, string? cwd, IReadOnlyDictionary<string, string> environment, string program,
        IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (command, args) = AsUser(user, cwd, environment, program, arguments);

        return await commands.RunAsync(command, args, timeout: timeout, cancellationToken: cancellationToken);
    }

    private async Task RunAsUserCheckedAsync(PasswdEntry user, string? cwd, string program, IEnumerable<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await RunAsUserAsync(user, cwd, new Dictionary<string, string>(), program, arguments, timeout ?? TimeSpan.FromMinutes(2), cancellationToken);

        if (!result.Success)
        {
            throw new RpcException(new Status(StatusCode.Aborted, $"{program} failed: {result.ErrorText}"));
        }
    }

    private async Task<int> StreamAsUserAsync(PasswdEntry user, string? cwd, IReadOnlyDictionary<string, string> environment, string program,
        IEnumerable<string> arguments, Func<string, Task> onLine, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (command, args) = AsUser(user, cwd, environment, program, arguments);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await commands.StreamAsync(command, args, line => new ValueTask(onLine(line)), mergeStdErr: true, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await onLine($"Timed out after {timeout.TotalMinutes:0} minutes.");

            return 124;
        }
    }

    private static string CleanGitError(string error)
    {
        if (error.Contains("Permission denied (publickey)", StringComparison.Ordinal) || error.Contains("Repository not found", StringComparison.Ordinal)
            || error.Contains("could not read Username", StringComparison.Ordinal))
        {
            return "GitHub refused access. For a private repository, add the deploy key to the repository (Settings → Deploy keys) and try again.";
        }

        if (error.Contains("Host key verification failed", StringComparison.Ordinal))
        {
            return "The server's SSH host key didn't match the pinned key for this host.";
        }

        return error.Split('\n').FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("ERROR", StringComparison.Ordinal))?.Trim()
            ?? error.Trim();
    }
}
