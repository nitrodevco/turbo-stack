using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TurboManager.Contracts.Agent;
using TurboManager.Core.Firewall;

namespace TurboManager.Panel.Tests;

/// <summary>In-memory firewall that behaves like the agent's (rules keyed by spec, pending transactions).</summary>
public sealed class FakeFirewallState
{
    public bool Enabled { get; set; } = true;

    public string DefaultIncoming { get; set; } = "deny";

    /// <summary>Extra TCP ports reported as listening (on 127.0.0.1).</summary>
    public List<int> ListeningPorts { get; } = [];

    public List<FirewallRuleSpec> Rules { get; } =
    [
        new() { Action = "limit", Protocol = "tcp", Ports = "22", Source = "any", Comment = "tm:ssh" },
        new() { Action = "allow", Protocol = "tcp", Ports = "80", Source = "any", Comment = "tm:http" },
        new() { Action = "allow", Protocol = "tcp", Ports = "443", Source = "any", Comment = "tm:https" },
        new() { Action = "allow", Protocol = "tcp", Ports = "8443", Source = "any", Comment = "tm:setup" },
        new() { Action = "allow", Protocol = "udp", Ports = "51820", Source = "any", Comment = "" },
    ];

    public List<FirewallChange> Changes { get; } = [];

    public Dictionary<string, string> Transactions { get; } = [];

    public FirewallState ToState()
    {
        var state = new FirewallState { Available = true, Enabled = Enabled, DefaultIncoming = DefaultIncoming, DefaultOutgoing = "allow" };
        state.Rules.AddRange(Rules.Select(r => new FirewallRule
        {
            Action = r.Action,
            Protocol = r.Protocol,
            Ports = r.Ports,
            Source = r.Source,
            Comment = r.Comment,
            Direction = "in",
        }));

        return state;
    }

    public static bool Same(FirewallRuleSpec a, FirewallRuleSpec b) =>
        a.Action == b.Action && a.Protocol == b.Protocol && a.Ports == b.Ports
        && FirewallSource.Normalize(a.Source) == FirewallSource.Normalize(b.Source);
}

public sealed class FakeFirewallAgent(FakeFirewallState state) : FirewallAgent.FirewallAgentBase
{
    public override Task<FirewallState> GetFirewallState(Empty request, ServerCallContext context) => Task.FromResult(state.ToState());

    public override Task<FirewallChangeResult> ApplyFirewallChange(FirewallChange request, ServerCallContext context)
    {
        lock (state)
        {
            state.Changes.Add(request);
            state.Rules.RemoveAll(r => request.Delete.Any(d => FakeFirewallState.Same(r, d)));
            state.Rules.AddRange(request.Add);

            if (request.HasSetEnabled)
            {
                state.Enabled = request.SetEnabled;
            }

            if (request.SetDefaultIncoming.Length > 0)
            {
                state.DefaultIncoming = request.SetDefaultIncoming;
            }

            var result = new FirewallChangeResult { State = state.ToState() };

            if (request.ConfirmWithinSeconds > 0)
            {
                result.TransactionId = $"tx{state.Changes.Count}";
                result.RevertAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(request.ConfirmWithinSeconds).ToUnixTimeMilliseconds();
                state.Transactions[result.TransactionId] = "pending";
            }

            return Task.FromResult(result);
        }
    }

    public override Task<FirewallTransactionStatus> ConfirmFirewallChange(FirewallTransactionRef request, ServerCallContext context)
    {
        lock (state)
        {
            if (state.Transactions.GetValueOrDefault(request.TransactionId) == "pending")
            {
                state.Transactions[request.TransactionId] = "confirmed";
            }

            return GetFirewallTransaction(request, context);
        }
    }

    public override Task<FirewallTransactionStatus> GetFirewallTransaction(FirewallTransactionRef request, ServerCallContext context) =>
        Task.FromResult(new FirewallTransactionStatus
        {
            TransactionId = request.TransactionId,
            Status = state.Transactions.GetValueOrDefault(request.TransactionId, "unknown"),
        });

    public override Task<ListeningSockets> ListListeningSockets(Empty request, ServerCallContext context)
    {
        var reply = new ListeningSockets();
        reply.Sockets.Add(new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 22, Process = "sshd" });
        reply.Sockets.Add(new ListeningSocket { Protocol = "tcp", Address = "127.0.0.1", Port = 7800, Process = "tm-panel" });
        reply.Sockets.Add(new ListeningSocket { Protocol = "tcp", Address = "0.0.0.0", Port = 3306, Process = "mysqld" });
        reply.Sockets.AddRange(state.ListeningPorts.Select(p => new ListeningSocket { Protocol = "tcp", Address = "127.0.0.1", Port = p, Process = "other" }));

        return Task.FromResult(reply);
    }
}

public sealed class FakeServicesAgent : ServicesAgent.ServicesAgentBase
{
    public List<ControlServiceRequest> Controls { get; } = [];

    /// <summary>Units listed besides nginx.service.</summary>
    public List<ServiceUnit> Units { get; } = [];

    public override Task<ListServicesReply> ListServices(ListServicesRequest request, ServerCallContext context)
    {
        var reply = new ListServicesReply();
        reply.Services.Add(new ServiceUnit { Name = "nginx.service", ActiveState = "active", SubState = "running", UnitFileState = "enabled" });
        reply.Services.AddRange(Units);

        return Task.FromResult(reply);
    }

    public override Task<ServiceUnit> ControlService(ControlServiceRequest request, ServerCallContext context)
    {
        Controls.Add(request);

        return request.Name == "missing.service"
            ? throw new RpcException(new Status(StatusCode.NotFound, "missing.service not found"))
            : Task.FromResult(new ServiceUnit { Name = request.Name, ActiveState = "active" });
    }
}

public sealed class FakeMetricsAgent : MetricsAgent.MetricsAgentBase
{
    public MetricsSnapshot Snapshot { get; set; } = new() { MemoryTotalBytes = 8L << 30, MemoryAvailableBytes = 6L << 30, Disks = { new DiskUsage { Mount = "/", FsType = "ext4", TotalBytes = 100L << 30, FreeBytes = 60L << 30 } } };

    public override Task<MetricsSnapshot> GetMetrics(MetricsRequest request, ServerCallContext context) => Task.FromResult(Snapshot);
}

public sealed class FakePackagesAgent : PackagesAgent.PackagesAgentBase
{
    public int SecurityUpdates { get; set; }

    public override Task<UpgradablePackages> ListUpgradable(ListUpgradableRequest request, ServerCallContext context)
    {
        var reply = new UpgradablePackages();
        reply.Packages.AddRange(Enumerable.Range(0, SecurityUpdates).Select(i => new UpgradablePackage { Name = $"lib{i}", Security = true }));

        return Task.FromResult(reply);
    }

    public override async Task UpgradePackages(UpgradeRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        await responseStream.WriteAsync(new OutputLine { Line = "Reading package lists..." });
        await responseStream.WriteAsync(new OutputLine { Line = $"Upgrading {request.Mode}" });
        await responseStream.WriteAsync(new OutputLine { Finished = true, ExitCode = 0 });
    }
}

public sealed class FakeNginxAgent : NginxAgent.NginxAgentBase
{
    public Dictionary<string, string> Sites { get; } = [];

    public Dictionary<string, string> Certificates { get; } = [];

    public override Task<NginxApplyResult> ApplyNginxConfig(NginxConfig request, ServerCallContext context)
    {
        // Stand-in for nginx -t failing.
        if (request.Content.Contains("BROKEN", StringComparison.Ordinal))
        {
            return Task.FromResult(new NginxApplyResult { Applied = false, Error = "[emerg] unknown directive \"BROKEN\"" });
        }

        lock (Sites)
        {
            Sites[request.Name] = request.Content;
        }

        return Task.FromResult(new NginxApplyResult { Applied = true });
    }

    public override Task<NginxApplyResult> RemoveNginxConfig(NginxConfigRef request, ServerCallContext context)
    {
        lock (Sites)
        {
            Sites.Remove(request.Name);
        }

        return Task.FromResult(new NginxApplyResult { Applied = true });
    }

    public bool SetupClosed { get; private set; }

    public override Task<NginxApplyResult> CloseSetupAddress(Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
        SetupClosed = true;

        return Task.FromResult(new NginxApplyResult { Applied = true });
    }

    public override Task<WebRootResult> EnsureWebRoot(WebRootRequest request, ServerCallContext context) =>
        Task.FromResult(new WebRootResult { Path = request.Path, Created = true });

    public override Task<CertificateInfo> InstallCertificate(InstallCertificateRequest request, ServerCallContext context)
    {
        var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(request.FullchainPem, request.PrivateKeyPem);

        lock (Certificates)
        {
            Certificates[request.Name] = request.FullchainPem;
        }

        return Task.FromResult(new CertificateInfo
        {
            Name = request.Name,
            NotAfterUnixMs = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeMilliseconds(),
        });
    }
}

/// <summary>In-memory databases, users and backup files.</summary>
public sealed class FakeDatabaseAgent : DatabaseAgent.DatabaseAgentBase
{
    public HashSet<string> Databases { get; } = [];

    public Dictionary<string, string> Users { get; } = [];

    public Dictionary<(string User, string Database), string> Grants { get; } = [];

    public Dictionary<string, byte[]> Backups { get; } = [];

    private static readonly Google.Protobuf.WellKnownTypes.Empty Done = new();

    public override Task<DatabaseEngines> GetEngines(Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
        var reply = new DatabaseEngines();
        reply.Engines.Add(new DatabaseEngineStatus { Engine = DatabaseEngine.Mysql, Installed = true, Running = true, Version = "8.0.43", Port = 3306 });
        reply.Engines.Add(new DatabaseEngineStatus { Engine = DatabaseEngine.Mariadb, BlockedReason = "MySQL is installed", Port = 3306 });
        reply.Engines.Add(new DatabaseEngineStatus { Engine = DatabaseEngine.Postgres, Port = 5432 });

        return Task.FromResult(reply);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> CreateDatabase(DatabaseRequest request, ServerCallContext context) =>
        Databases.Add(request.Name)
            ? Task.FromResult(Done)
            : throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Can't create database '{request.Name}'; database exists"));

    public override Task<Google.Protobuf.WellKnownTypes.Empty> DropDatabase(DatabaseRequest request, ServerCallContext context)
    {
        Databases.Remove(request.Name);

        return Task.FromResult(Done);
    }

    public override Task<DatabaseList> ListDatabases(EngineRef request, ServerCallContext context)
    {
        var reply = new DatabaseList();
        reply.Databases.AddRange(Databases.Order().Select(d => new DatabaseInfo { Name = d, SizeBytes = 1024, Tables = 3 }));

        return Task.FromResult(reply);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> CreateUser(DatabaseUserRequest request, ServerCallContext context)
    {
        if (request.Name == "taken" || !Users.TryAdd(request.Name, request.Password))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Operation CREATE USER failed"));
        }

        return Task.FromResult(Done);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> SetUserPassword(DatabaseUserRequest request, ServerCallContext context)
    {
        Users[request.Name] = request.Password;

        return Task.FromResult(Done);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> DropUser(DatabaseUserRequest request, ServerCallContext context)
    {
        Users.Remove(request.Name);

        return Task.FromResult(Done);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> SetGrant(GrantRequest request, ServerCallContext context)
    {
        if (request.Level == "none")
        {
            Grants.Remove((request.User, request.Database));
        }
        else
        {
            Grants[(request.User, request.Database)] = request.Level;
        }

        return Task.FromResult(Done);
    }

    public override Task<DatabaseUserList> ListUsers(EngineRef request, ServerCallContext context)
    {
        var reply = new DatabaseUserList();

        foreach (var user in Users.Keys.Order())
        {
            var entry = new DatabaseUser { Name = user };
            entry.Grants.AddRange(Grants.Where(g => g.Key.User == user).Select(g => new DatabaseGrant { Database = g.Key.Database, Level = g.Value }));
            reply.Users.Add(entry);
        }

        return Task.FromResult(reply);
    }

    public override Task<BackupFile> BackupDatabase(DatabaseRequest request, ServerCallContext context)
    {
        var id = $"mysql/{request.Name}/{request.Name}-{Backups.Count:D3}.sql.gz";
        Backups[id] = System.Text.Encoding.UTF8.GetBytes($"-- dump of {request.Name}");

        return Task.FromResult(new BackupFile { Id = id, Engine = DatabaseEngine.Mysql, Database = request.Name, SizeBytes = Backups[id].Length });
    }

    public override Task<BackupList> ListBackups(EngineRef request, ServerCallContext context)
    {
        var reply = new BackupList();
        reply.Backups.AddRange(Backups.Select((b, i) => new BackupFile
        {
            Id = b.Key,
            Engine = b.Key.StartsWith("imports/", StringComparison.Ordinal) ? DatabaseEngine.Unspecified : DatabaseEngine.Mysql,
            Database = b.Key.Split('/') is { Length: 3 } parts ? parts[1] : "",
            SizeBytes = b.Value.Length,
            CreatedUnixMs = i,
            Import = b.Key.StartsWith("imports/", StringComparison.Ordinal),
        }));

        return Task.FromResult(reply);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> DeleteBackup(BackupRef request, ServerCallContext context)
    {
        Backups.Remove(request.Id);

        return Task.FromResult(Done);
    }

    public override async Task DownloadBackup(BackupRef request, IServerStreamWriter<FileChunk> responseStream, ServerCallContext context)
    {
        if (!Backups.TryGetValue(request.Id, out var data))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Backup not found."));
        }

        await responseStream.WriteAsync(new FileChunk { Data = Google.Protobuf.ByteString.CopyFrom(data) });
    }

    public override async Task<BackupFile> UploadImport(IAsyncStreamReader<FileChunk> requestStream, ServerCallContext context)
    {
        var name = "";
        var data = new MemoryStream();

        while (await requestStream.MoveNext())
        {
            name = name.Length == 0 ? requestStream.Current.Name : name;
            requestStream.Current.Data.WriteTo(data);
        }

        var id = $"imports/{name}";
        Backups[id] = data.ToArray();

        return new BackupFile { Id = id, SizeBytes = data.Length, Import = true };
    }
}

/// <summary>In-memory app users, keys, releases and services. A build script containing "fail" fails.</summary>
public sealed class FakeDeployAgent : DeployAgent.DeployAgentBase
{
    public HashSet<string> Users { get; } = [];

    public Dictionary<string, string> Keys { get; } = [];

    public Dictionary<string, List<string>> Releases { get; } = [];

    public Dictionary<string, string> Current { get; } = [];

    public Dictionary<string, AppServiceRequest> Services { get; } = [];

    public List<GitSyncRequest> Syncs { get; } = [];

    public List<BuildRequest> Builds { get; } = [];

    public string Commit { get; set; } = "abcdef1234567890abcdef1234567890abcdef12";

    public override Task<AppUserInfo> EnsureAppUser(AppUserRequest request, ServerCallContext context)
    {
        Users.Add(request.User);

        return Task.FromResult(new AppUserInfo { User = request.User, Home = $"/home/{request.User}", Uid = 1001 });
    }

    public override Task<DeployKey> EnsureDeployKey(DeployKeyRequest request, ServerCallContext context)
    {
        Keys.TryAdd(request.KeyName, $"ssh-ed25519 AAAAFAKE{request.KeyName} turbo-manager {request.User}/{request.KeyName}");

        return Task.FromResult(new DeployKey { KeyName = request.KeyName, PublicKey = Keys[request.KeyName], Fingerprint = $"SHA256:{request.KeyName}" });
    }

    public override Task<GitAccessResult> TestRepositoryAccess(GitSyncRequest request, ServerCallContext context) =>
        Task.FromResult(new GitAccessResult { Ok = true, Head = Commit });

    public override async Task SyncRepository(GitSyncRequest request, IServerStreamWriter<DeployOutput> responseStream, ServerCallContext context)
    {
        Syncs.Add(request);
        await responseStream.WriteAsync(new DeployOutput { Line = "$ git fetch" });
        await responseStream.WriteAsync(new DeployOutput { Finished = true, Success = true, Commit = Commit, CommitAuthor = "Bill", CommitMessage = "Update" });
    }

    public override async Task CreateRelease(ReleaseRequest request, IServerStreamWriter<DeployOutput> responseStream, ServerCallContext context)
    {
        if (!Releases.TryGetValue(request.App, out var list))
        {
            Releases[request.App] = list = [];
        }

        list.Insert(0, request.Release);
        await responseStream.WriteAsync(new DeployOutput { Finished = true, Success = true, Release = request.Release });
    }

    public override async Task RunBuild(BuildRequest request, IServerStreamWriter<DeployOutput> responseStream, ServerCallContext context)
    {
        Builds.Add(request);
        var failed = request.Script.Contains("make fail", StringComparison.Ordinal); // a real script may mention "fail" in a comment
        await responseStream.WriteAsync(new DeployOutput { Line = failed ? "error: build broke" : "built" });
        await responseStream.WriteAsync(new DeployOutput { Finished = true, Success = !failed });
    }

    public override Task<ReleaseState> ActivateRelease(ReleaseRequest request, ServerCallContext context)
    {
        Current[request.App] = request.Release;

        return GetReleaseState(new AppRef { User = request.User, App = request.App }, context);
    }

    public override Task<ReleaseState> GetReleaseState(AppRef request, ServerCallContext context)
    {
        var state = new ReleaseState { Current = Current.GetValueOrDefault(request.App, ""), AppDir = $"/home/{request.User}/apps/{request.App}" };
        state.Releases.AddRange(Releases.GetValueOrDefault(request.App) ?? []);

        return Task.FromResult(state);
    }

    public override Task<ReleaseState> PruneReleases(PruneRequest request, ServerCallContext context)
    {
        if (Releases.TryGetValue(request.App, out var list))
        {
            list.RemoveAll(r => list.IndexOf(r) >= request.Keep && r != Current.GetValueOrDefault(request.App));
        }

        return GetReleaseState(new AppRef { User = request.User, App = request.App }, context);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> WriteAppService(AppServiceRequest request, ServerCallContext context)
    {
        Services[request.App] = request;

        return Task.FromResult(new Google.Protobuf.WellKnownTypes.Empty());
    }

    public List<ConsoleInput> ConsoleInput { get; } = [];

    public override Task<Google.Protobuf.WellKnownTypes.Empty> SendConsoleInput(ConsoleInput request, ServerCallContext context)
    {
        ConsoleInput.Add(request);

        return Task.FromResult(new Google.Protobuf.WellKnownTypes.Empty());
    }

    public HashSet<string> InstalledRuntimes { get; } = [];

    public override Task<Runtimes> ListRuntimes(Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
        var reply = new Runtimes();
        reply.Items.AddRange(new[] { "dotnet", "node" }.Select(n => new RuntimeInfo { Name = n, Installed = InstalledRuntimes.Contains(n) }));

        return Task.FromResult(reply);
    }

    public override async Task InstallRuntime(RuntimeRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        InstalledRuntimes.Add(request.Name);
        await responseStream.WriteAsync(new OutputLine { Line = $"{request.Name} {request.Channel} installed" });
        await responseStream.WriteAsync(new OutputLine { Finished = true, ExitCode = 0 });
    }

    public HashSet<string> SiteDirectories { get; } = [];

    public override Task<SiteDirectory> EnsureSiteDirectory(AppRef request, ServerCallContext context)
    {
        var path = $"/home/{request.User}/sites/{request.App}";
        SiteDirectories.Add(path);

        return Task.FromResult(new SiteDirectory { Path = path });
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> RemoveApp(AppRef request, ServerCallContext context)
    {
        Releases.Remove(request.App);
        Services.Remove(request.App);

        return Task.FromResult(new Google.Protobuf.WellKnownTypes.Empty());
    }
}

public sealed class FakeLogsAgent : LogsAgent.LogsAgentBase
{
    public List<JournalEntry> Entries { get; } = [];

    public override Task<JournalEntries> QueryJournal(JournalQuery request, ServerCallContext context)
    {
        var reply = new JournalEntries();
        reply.Entries.AddRange(Entries.Where(e => (request.Unit.Length == 0 || e.Unit == request.Unit)
            && (request.Grep.Length == 0 || e.Message.Contains(request.Grep, StringComparison.Ordinal))));

        return Task.FromResult(reply);
    }
}

/// <summary>Echoes keystrokes back; "exit" ends the shell with code 0.</summary>
public sealed class FakeTerminalAgent : TerminalAgent.TerminalAgentBase
{
    public List<string> OpenedAs { get; } = [];

    public List<TerminalSize> Resizes { get; } = [];

    public override async Task OpenTerminal(IAsyncStreamReader<TerminalInput> requestStream, IServerStreamWriter<TerminalOutput> responseStream,
        ServerCallContext context)
    {
        await requestStream.MoveNext();
        OpenedAs.Add(requestStream.Current.Open.User);

        while (await requestStream.MoveNext())
        {
            var input = requestStream.Current;

            if (input.KindCase == TerminalInput.KindOneofCase.Resize)
            {
                Resizes.Add(input.Resize);
                continue;
            }

            if (input.Data.ToStringUtf8() == "exit\n")
            {
                await responseStream.WriteAsync(new TerminalOutput { ExitCode = 0 });

                return;
            }

            await responseStream.WriteAsync(new TerminalOutput { Data = input.Data });
        }
    }
}

/// <summary>An in-memory home folder per user ("user:path" → content).</summary>
public sealed class FakeFilesAgent : FilesAgent.FilesAgentBase
{
    public Dictionary<string, byte[]> Files { get; } = [];

    private static readonly Google.Protobuf.WellKnownTypes.Empty Done = new();

    public override Task<DirectoryListing> ListDirectory(FilePath request, ServerCallContext context)
    {
        var prefix = $"{request.User}:{(request.Path.Length > 0 ? request.Path + "/" : "")}";
        var reply = new DirectoryListing { Path = request.Path };
        reply.Entries.AddRange(Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
            .Select(f => new FileEntry { Name = f.Key[prefix.Length..], Type = "f", Size = f.Value.Length, Mode = "644" }));

        return Task.FromResult(reply);
    }

    public override Task<FileContent> ReadFile(FilePath request, ServerCallContext context) =>
        Files.TryGetValue($"{request.User}:{request.Path}", out var content)
            ? Task.FromResult(new FileContent { User = request.User, Path = request.Path, Content = Google.Protobuf.ByteString.CopyFrom(content) })
            : throw new RpcException(new Status(StatusCode.FailedPrecondition, "No such file or directory"));

    public override Task<Google.Protobuf.WellKnownTypes.Empty> WriteFile(FileContent request, ServerCallContext context)
    {
        Files[$"{request.User}:{request.Path}"] = request.Content.ToByteArray();

        return Task.FromResult(Done);
    }

    public override Task<Google.Protobuf.WellKnownTypes.Empty> DeletePath(FilePath request, ServerCallContext context)
    {
        Files.Remove($"{request.User}:{request.Path}");

        return Task.FromResult(Done);
    }

    public override async Task<Google.Protobuf.WellKnownTypes.Empty> UploadFile(IAsyncStreamReader<FileChunk> requestStream, ServerCallContext context)
    {
        await requestStream.MoveNext();
        var name = requestStream.Current.Name;
        using var content = new MemoryStream();

        while (await requestStream.MoveNext())
        {
            content.Write(requestStream.Current.Data.Span);
        }

        Files[name] = content.ToArray();

        return Done;
    }

    public override async Task DownloadFile(FilePath request, IServerStreamWriter<FileChunk> responseStream, ServerCallContext context)
    {
        var content = Files[$"{request.User}:{request.Path}"];

        foreach (var chunk in content.Chunk(4))
        {
            await responseStream.WriteAsync(new FileChunk { Data = Google.Protobuf.ByteString.CopyFrom(chunk) });
        }
    }
}

/// <summary>A gRPC server on a unix socket implementing the agent services the panel tests need.</summary>
public sealed class FakeUpdateAgent : UpdateAgent.UpdateAgentBase
{
    public UpdateInfo Info { get; set; } = new() { CurrentVersion = "0.1.0", Channel = "stable", LatestVersion = "0.1.0" };

    public UpdateStatus Status { get; set; } = new();

    public List<string> Started { get; } = [];

    public string Channel { get; private set; } = "stable";

    public override Task<UpdateInfo> CheckUpdate(Empty request, ServerCallContext context) => Task.FromResult(Info.Clone());

    public override Task<Empty> StartUpdate(UpdateRequest request, ServerCallContext context)
    {
        if (request.Version != Info.LatestVersion)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The channel now offers {Info.LatestVersion}, not {request.Version}."));
        }

        Started.Add(request.Version);

        return Task.FromResult(new Empty());
    }

    public override Task<UpdateStatus> GetUpdateStatus(Empty request, ServerCallContext context) => Task.FromResult(Status.Clone());

    public override Task<Empty> SetUpdateChannel(UpdateChannel request, ServerCallContext context)
    {
        Channel = request.Channel;
        Info.Channel = request.Channel;

        return Task.FromResult(new Empty());
    }
}

public sealed class FakeBackupAgent : BackupAgent.BackupAgentBase
{
    public List<BackupTarget> Prepared { get; } = [];

    public List<BackupRunRequest> Runs { get; } = [];

    public List<RestoreSnapshotRequest> Restores { get; } = [];

    /// <summary>Whether the panel's database copy existed while the run was in the agent's hands.</summary>
    public List<bool> PanelCopySeen { get; } = [];

    public string? PanelCopyPath { get; set; }

    public string? FailPrepare { get; set; }

    public override Task<TargetStatus> PrepareTarget(BackupTarget request, ServerCallContext context)
    {
        if (FailPrepare is not null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, FailPrepare));
        }

        Prepared.Add(request);

        return Task.FromResult(new TargetStatus { Created = Prepared.Count == 1, ResticVersion = "restic 0.16.4" });
    }

    public override async Task RunBackup(BackupRunRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        Runs.Add(request);
        PanelCopySeen.Add(PanelCopyPath is not null && File.Exists(PanelCopyPath));
        await responseStream.WriteAsync(new OutputLine { Line = "Snapshot 0123abcd saved: 3 new, 0 changed, 0 unchanged files" });
        await responseStream.WriteAsync(new OutputLine { Line = "snapshot:0123abcd4567", Finished = true });
    }

    public override Task<SnapshotList> ListSnapshots(SnapshotsRequest request, ServerCallContext context)
    {
        var list = new SnapshotList();
        list.Snapshots.Add(new Snapshot { Id = "0123abcd4567", ShortId = "0123abcd", Time = "2026-10-12T03:00:00Z", PlanId = "p1" });

        return Task.FromResult(list);
    }

    public override Task<BackupManifest> ReadManifest(SnapshotRef request, ServerCallContext context)
    {
        var manifest = new BackupManifest { PlanId = "p1", PlanName = "Nightly", PanelState = true };
        manifest.Databases.Add(new DatabaseItem { Engine = "mariadb", Name = "turbo" });
        manifest.Folders.Add(new FolderItem { User = "hotel", Path = "sites/assets" });

        return Task.FromResult(manifest);
    }

    public override async Task RestoreSnapshot(RestoreSnapshotRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        Restores.Add(request);
        await responseStream.WriteAsync(new OutputLine { Line = "restored" });
        await responseStream.WriteAsync(new OutputLine { Finished = true });
    }

    public override Task<TargetKey> CreateTargetKey(TargetKeyRequest request, ServerCallContext context) =>
        Task.FromResult(new TargetKey { PublicKey = $"ssh-ed25519 AAAAC3Nz turbo-manager-backup-{request.TargetId}" });

    public override Task<HostKeyReply> ScanHostKey(HostKeyRequest request, ServerCallContext context) =>
        Task.FromResult(new HostKeyReply { HostKey = "ssh-ed25519 AAAAhost", Fingerprint = "SHA256:abc" });
}

public sealed class FakeAgent : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FakeFirewallState Firewall { get; } = new();

    public FakeServicesAgent Services { get; } = new();

    public FakeNginxAgent Nginx { get; } = new();

    public FakeDatabaseAgent Databases { get; } = new();

    public FakeDeployAgent Deploy { get; } = new();

    public FakeLogsAgent Logs { get; } = new();

    public FakeMetricsAgent Metrics { get; } = new();

    public FakeTerminalAgent Terminal { get; } = new();

    public FakeFilesAgent Files { get; } = new();

    public FakePackagesAgent Packages { get; } = new();

    public FakeUpdateAgent Updates { get; } = new();

    public FakeBackupAgent Backups { get; } = new();

    private FakeAgent(string socketPath)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(Firewall);
        builder.Services.AddSingleton(Services);
        builder.Services.AddSingleton(Nginx);
        builder.Services.AddSingleton(Databases);
        builder.Services.AddSingleton(Deploy);
        builder.Services.AddSingleton(Logs);
        builder.Services.AddSingleton(Metrics);
        builder.Services.AddSingleton(Terminal);
        builder.Services.AddSingleton(Files);
        builder.Services.AddSingleton(Packages);
        builder.Services.AddSingleton(Updates);
        builder.Services.AddSingleton(Backups);
        _app = builder.Build();
        _app.MapGrpcService<FakeFirewallAgent>();
        _app.MapGrpcService<FakeServicesAgent>();
        _app.MapGrpcService<FakePackagesAgent>();
        _app.MapGrpcService<FakeNginxAgent>();
        _app.MapGrpcService<FakeDatabaseAgent>();
        _app.MapGrpcService<FakeDeployAgent>();
        _app.MapGrpcService<FakeLogsAgent>();
        _app.MapGrpcService<FakeMetricsAgent>();
        _app.MapGrpcService<FakeTerminalAgent>();
        _app.MapGrpcService<FakeFilesAgent>();
        _app.MapGrpcService<FakeUpdateAgent>();
        _app.MapGrpcService<FakeBackupAgent>();
    }

    public static async Task<FakeAgent> StartAsync(string socketPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        var agent = new FakeAgent(socketPath);
        await agent._app.StartAsync();

        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
