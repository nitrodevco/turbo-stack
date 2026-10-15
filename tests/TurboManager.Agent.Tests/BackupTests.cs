using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Backups;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Databases;
using TurboManager.Agent.Deploy;
using TurboManager.Agent.Files;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Tests;

public sealed class BackupTests : IDisposable
{
    private const string Password = "correct-horse-battery-staple-42";

    private readonly FakeHostRoot _host = new FakeHostRoot()
        .WithFile("etc/passwd", "root:x:0:0:root:/root:/bin/bash\nhotel:x:1001:1001::/home/hotel:/bin/bash\n")
        .WithFile("etc/group", "root:x:0:\nts-apps:x:999:hotel\n")
        .WithFile("home/hotel/sites/assets/figure.nitro", "nitro");

    private readonly FakeCommandRunner _commands = new();
    private readonly BackupManager _backups;

    public BackupTests()
    {
        var options = Options.Create(new AgentOptions { HostRoot = _host.Path, BackupRoot = "/var/lib/ts-backups" });
        var deploy = new DeployManager(_commands, options, NullLogger<DeployManager>.Instance);
        _backups = new BackupManager(_commands, new DatabaseManager(_commands, options, TimeProvider.System, NullLogger<DatabaseManager>.Instance),
            new FilesManager(deploy, _commands), options, TimeProvider.System, NullLogger<BackupManager>.Instance);
    }

    public void Dispose() => _host.Dispose();

    private static BackupTarget Local(string path = "/var/backups/turbo-stack") => new() { Id = "t1", Kind = "local", Path = path, Password = Password };

    [Theory]
    [InlineData("/var/backups/turbo-stack", true)]
    [InlineData("/mnt/backup-disk/hotel", true)]
    [InlineData("/var/backups", false)]
    [InlineData("/home/hotel/backups", false)]
    [InlineData("/etc/turbo-stack", false)]
    [InlineData("/var/backups/../../etc", false)]
    [InlineData("relative/path", false)]
    public void Local_repositories_only_go_in_backup_locations(string path, bool allowed)
    {
        if (allowed)
        {
            Assert.Equal(path, _backups.Repository(Local(path)).Location);
        }
        else
        {
            Assert.Throws<RpcException>(() => _backups.Repository(Local(path)));
        }
    }

    [Fact]
    public void S3_secrets_travel_in_the_environment_never_in_arguments()
    {
        var repository = _backups.Repository(new BackupTarget
        {
            Id = "t2", Kind = "s3", Endpoint = "https://s3.eu-central-1.amazonaws.com", Path = "hotel-backups/server-1",
            AccessKeyId = "AKIAEXAMPLE", SecretAccessKey = "s3cr3t", Region = "eu-central-1", Password = Password,
        });

        Assert.Equal("s3:https://s3.eu-central-1.amazonaws.com/hotel-backups/server-1", repository.Location);
        Assert.Empty(repository.Options);
        Assert.Equal("s3cr3t", repository.Environment["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal(Password, repository.Environment["RESTIC_PASSWORD"]);
        Assert.Throws<RpcException>(() => _backups.Repository(new BackupTarget { Kind = "s3", Endpoint = "https://s3.example.com/x?y", Path = "b", Password = Password }));
    }

    [Fact]
    public void Sftp_needs_a_confirmed_host_key_and_uses_only_its_own_key()
    {
        var target = new BackupTarget { Id = "t3", Kind = "sftp", Endpoint = "backup.example.com", Port = 2222, User = "hotel", Path = "restic", Password = Password };

        Assert.Throws<RpcException>(() => _backups.Repository(target));

        target.HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
        _host.WithFile("etc/turbo-stack/backup-keys/t3", "PRIVATE");
        var repository = _backups.Repository(target);

        Assert.Equal("sftp:hotel@backup.example.com:restic", repository.Location);
        Assert.Equal("-o", repository.Options[0]);
        Assert.Contains("-i /etc/turbo-stack/backup-keys/t3 -p 2222 -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes", repository.Options[1]);
        Assert.Equal($"[backup.example.com]:2222 {target.HostKey}\n", File.ReadAllText(Path.Combine(_host.Path, "etc/turbo-stack/backup-keys/t3.known_hosts")));
    }

    [Fact]
    public async Task A_run_dumps_the_databases_backs_up_with_the_folders_and_applies_retention()
    {
        _commands.StreamOutput = (_, _) => "-- dump";
        _commands.Handler = (file, args) => file == "restic" && args.Contains("backup")
            ? new CommandResult(0, "{\"message_type\":\"status\",\"percent_done\":0.5,\"bytes_done\":10,\"total_bytes\":20}\n"
                + "{\"message_type\":\"summary\",\"files_new\":3,\"files_changed\":0,\"files_unmodified\":0,\"data_added\":2048,\"snapshot_id\":\"0123456789abcdef\"}\n", "")
            : new CommandResult(0, "", "");
        var lines = new List<string>();

        var snapshot = await _backups.RunAsync(new BackupRunRequest
        {
            Target = Local(), PlanId = "p1", PlanName = "Nightly",
            Databases = { new DatabaseItem { Engine = "mariadb", Name = "turbo" } },
            Folders = { new FolderItem { User = "hotel", Path = "sites/assets" } },
            Retention = new Retention { Daily = 7, Weekly = 4 },
        }, line =>
        {
            lines.Add(line);

            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal("0123456789abcdef", snapshot);
        Assert.Contains("apt-get install -y -q --no-install-recommends restic", _commands.Calls);
        var backup = Assert.Single(_commands.Calls, c => c.StartsWith("restic", StringComparison.Ordinal) && c.Contains(" backup ", StringComparison.Ordinal));
        Assert.EndsWith("backup --json --tag ts --tag plan:p1 /var/lib/ts-backups/staging/p1 /home/hotel/sites/assets", backup);
        Assert.Contains(_commands.Calls, c => c.EndsWith("forget --tag plan:p1 --group-by tags --keep-daily 7 --keep-weekly 4 --prune", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Snapshot 01234567 saved: 3 new", StringComparison.Ordinal));
        Assert.DoesNotContain(_commands.Calls, c => c.Contains(Password, StringComparison.Ordinal));
        Assert.All(_commands.Environments.Where(e => e.Call.StartsWith("restic --repo", StringComparison.Ordinal)),
            e => Assert.Equal(Password, e.Environment["RESTIC_PASSWORD"]));
        Assert.False(Directory.Exists(Path.Combine(_host.Path, "var/lib/ts-backups/staging/p1")), "staging is cleaned up");
    }

    [Fact]
    public async Task Restored_folders_land_next_to_the_original_and_dumps_join_the_backups_list()
    {
        const string staging = "/var/lib/ts-backups/staging/p1";
        _commands.Handler = (file, args) =>
        {
            if (file != "restic")
            {
                return new CommandResult(0, "", "");
            }

            if (args.Contains("snapshots"))
            {
                return new CommandResult(0, "[{\"id\":\"0123456789abcdef\",\"short_id\":\"01234567\",\"time\":\"2026-10-12T03:00:00Z\",\"tags\":[\"ts\",\"plan:p1\"]}]", "");
            }

            if (args.Contains("dump"))
            {
                return new CommandResult(0, "{\"planId\":\"p1\",\"planName\":\"Nightly\",\"created\":\"2026-10-12T03:00:00Z\","
                    + "\"databases\":[{\"engine\":\"mariadb\",\"name\":\"turbo\"}],\"folders\":[{\"user\":\"hotel\",\"path\":\"sites/assets\"}],\"panelState\":false}", "");
            }

            if (args.Contains("restore"))
            {
                // What restic would have put in the scratch folder.
                var target = args[args.ToList().IndexOf("--target") + 1];
                _host.WithFile($"{target.TrimStart('/')}{staging}/databases/mariadb/turbo/turbo.sql.gz", "dump")
                    .WithFile($"{target.TrimStart('/')}/home/hotel/sites/assets/figure.nitro", "old nitro");
            }

            return new CommandResult(0, "", "");
        };

        await _backups.RestoreAsync(new RestoreSnapshotRequest
        {
            Target = Local(), SnapshotId = "01234567",
            Databases = { new DatabaseItem { Engine = "mariadb", Name = "turbo" } },
            Folders = { new FolderItem { User = "hotel", Path = "sites/assets" } },
        }, _ => Task.CompletedTask, CancellationToken.None);

        var restore = Assert.Single(_commands.Calls, c => c.Contains(" restore ", StringComparison.Ordinal));
        Assert.Contains($"--include {staging}/databases/mariadb/turbo --include /home/hotel/sites/assets", restore);
        Assert.Single(Directory.GetFiles(Path.Combine(_host.Path, "var/lib/turbo-stack-backups/databases/mariadb/turbo"), "turbo-*.sql.gz"));
        Assert.Contains(_commands.Calls, c => c.StartsWith("cp -a -- ", StringComparison.Ordinal) && c.Contains("home/hotel/restored-", StringComparison.Ordinal));
        Assert.Contains(_commands.Calls, c => c.StartsWith("chown -R --no-dereference 1001:1001 ", StringComparison.Ordinal));
        Assert.Equal("nitro", File.ReadAllText(Path.Combine(_host.Path, "home/hotel/sites/assets/figure.nitro")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_host.Path, "var/lib/ts-backups/restores")));
    }

    [Fact]
    public async Task Restoring_the_panel_hands_the_unpacked_state_to_ts_panel_restore()
    {
        _commands.Handler = (file, args) =>
        {
            if (file == "systemctl")
            {
                return new CommandResult(3, "", ""); // no restore running
            }

            if (file == "restic" && args.Contains("snapshots"))
            {
                return new CommandResult(0, "[{\"id\":\"0123456789abcdef\",\"time\":\"2026-10-12T03:00:00Z\",\"tags\":[\"plan:p1\"]}]", "");
            }

            if (file == "restic" && args.Contains("dump"))
            {
                return new CommandResult(0, "{\"planId\":\"p1\",\"planName\":\"n\",\"created\":\"\",\"databases\":[],\"folders\":[],\"panelState\":true}", "");
            }

            if (file == "restic" && args.Contains("restore"))
            {
                var target = args[args.ToList().IndexOf("--target") + 1];
                _host.WithFile($"{target.TrimStart('/')}/var/lib/turbo-stack/backup/panel.db", "db")
                    .WithFile($"{target.TrimStart('/')}/var/lib/turbo-stack/keys/key-1.xml", "key");
            }

            return new CommandResult(0, "", "");
        };
        var request = new SnapshotRef { Target = Local(), SnapshotId = "0123456789abcdef" };

        await Assert.ThrowsAsync<RpcException>(() => _backups.RestorePanelStateAsync(request, _ => Task.CompletedTask, CancellationToken.None));
        Assert.DoesNotContain(_commands.Calls, c => c.Contains(" restore ", StringComparison.Ordinal));

        _host.WithFile("usr/local/sbin/ts-panel-restore", "#!/bin/bash");
        await _backups.RestorePanelStateAsync(request, _ => Task.CompletedTask, CancellationToken.None);

        var run = Assert.Single(_commands.Calls, c => c.StartsWith("systemd-run", StringComparison.Ordinal));
        Assert.Matches(@"--unit ts-panel-restore .* /bin/bash /usr/local/sbin/ts-panel-restore /var/lib/ts-backups/restores/panel-state-\d{8}-\d{6}$", run);
    }

    [Fact]
    public async Task Only_what_the_snapshot_holds_can_be_restored()
    {
        _commands.Handler = (file, args) => file == "restic" && args.Contains("snapshots")
            ? new CommandResult(0, "[{\"id\":\"0123456789abcdef\",\"time\":\"2026-10-12T03:00:00Z\",\"tags\":[\"plan:p1\"]}]", "")
            : file == "restic" && args.Contains("dump")
                ? new CommandResult(0, "{\"planId\":\"p1\",\"planName\":\"n\",\"created\":\"\",\"databases\":[],\"folders\":[],\"panelState\":false}", "")
                : new CommandResult(0, "", "");

        await Assert.ThrowsAsync<RpcException>(() => _backups.RestoreAsync(new RestoreSnapshotRequest
        {
            Target = Local(), SnapshotId = "0123456789abcdef", Folders = { new FolderItem { User = "hotel", Path = "." } },
        }, _ => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<RpcException>(() => _backups.RestoreAsync(new RestoreSnapshotRequest
        {
            Target = Local(), SnapshotId = "0123456789abcdef", Databases = { new DatabaseItem { Engine = "mariadb", Name = "turbo" } },
        }, _ => Task.CompletedTask, CancellationToken.None));
        Assert.DoesNotContain(_commands.Calls, c => c.Contains(" restore ", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshots_carry_their_plan()
    {
        var snapshot = Assert.Single(BackupManager.ParseSnapshots(
            "[{\"id\":\"0123456789abcdef0123\",\"time\":\"2026-10-12T03:00:00Z\",\"hostname\":\"hotel\",\"tags\":[\"ts\",\"plan:p7\"]}]"));

        Assert.Equal("p7", snapshot.PlanId);
        Assert.Equal("01234567", snapshot.ShortId);
    }

    [Fact]
    public void Host_key_fingerprints_match_ssh_keygen()
    {
        // ssh-keygen -lf on this key prints SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU
        Assert.Equal("SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU",
            BackupManager.Fingerprint("AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"));
    }
}
