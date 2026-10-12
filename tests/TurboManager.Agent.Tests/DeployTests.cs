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
        Assert.Equal("tm-turbo-cloud:billsonnn/turbo-cloud.git", GitSsh.RemoteUrl(Request().Repository, "https://github.com"));
        Assert.Equal("https://github.com/nitrodevco/turbo-web.git", GitSsh.RemoteUrl(Request().SubmoduleAccess[1], "https://github.com"));
    }

    [Fact]
    public void Each_submodule_url_style_is_rewritten_to_its_own_key()
    {
        var arguments = GitSsh.SubmoduleRewrites(Request());

        Assert.Contains("url.tm-turbo-admin:nitrodevco/turbo-admin.git.insteadOf=https://github.com/nitrodevco/turbo-admin.git", arguments);
        Assert.Contains("url.tm-turbo-admin:nitrodevco/turbo-admin.git.insteadOf=git@github.com:nitrodevco/turbo-admin.git", arguments);
        Assert.Contains("url.tm-turbo-admin:nitrodevco/turbo-admin.git.insteadOf=tm-turbo-cloud:nitrodevco/turbo-admin.git", arguments);
        Assert.Contains("url.https://github.com/nitrodevco/turbo-web.git.insteadOf=git@github.com:nitrodevco/turbo-web.git", arguments);
        Assert.All(arguments.Where((_, i) => i % 2 == 0), a => Assert.Equal("-c", a));
    }

    [Fact]
    public void Ssh_config_pins_host_keys_and_isolates_identities()
    {
        var config = GitSsh.RenderConfig(["turbo-cloud", "turbo-admin"], "/home/hotel", "github.com", 22);

        Assert.Contains("Host tm-turbo-cloud", config);
        Assert.Contains("IdentityFile /home/hotel/.ssh/tm/turbo-admin", config);
        Assert.Contains("IdentitiesOnly yes", config);
        Assert.Contains("StrictHostKeyChecking yes", config);
        Assert.Equal("[fake-github]:2222 ssh-ed25519 AAAA\n", GitSsh.RenderKnownHosts("fake-github", 2222, ["ssh-ed25519 AAAA"]));
    }
}

public sealed class DeployManagerTests : IDisposable
{
    private readonly FakeHostRoot _host = new FakeHostRoot()
        .WithFile("etc/passwd", "root:x:0:0:root:/root:/bin/bash\nhotel:x:1001:1001::/home/hotel:/bin/bash\nbill:x:1000:1000::/home/bill:/bin/bash\n")
        .WithFile("etc/group", "root:x:0:\ntm-apps:x:999:hotel\nsudo:x:27:bill\n");

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

        var call = Assert.Single(_commands.Calls);
        Assert.StartsWith("runuser -u hotel -- env -i --chdir=/home/hotel/apps/web/releases/20261011120000-abcdef1/packages/nitro-react", call);
        Assert.Contains("VITE_SOCKET_URL=wss://ws.hotel.test", call);
        Assert.Contains("PATH=/opt/turbo-manager/runtimes/node/bin:/usr/local/sbin", call);
        Assert.Contains("TM_SHARED_DIR=/home/hotel/apps/web/shared", call);
        Assert.EndsWith("bash -euo pipefail -c yarn build", call);
    }

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
        Assert.Contains("ExecStart=/etc/turbo-manager/apps/turbo.run", unit);
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

        Assert.StartsWith("/opt/turbo-manager/runtimes/dotnet:/opt/turbo-manager/runtimes/node/bin:", environment["PATH"]);
        Assert.Equal("/opt/turbo-manager/runtimes/dotnet", environment["DOTNET_ROOT"]);
        Assert.Throws<RpcException>(() => DeployManager.RuntimeEnvironment(["php"]));
    }

    public void Dispose() => _host.Dispose();
}
