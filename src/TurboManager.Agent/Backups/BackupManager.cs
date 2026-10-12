using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Databases;
using TurboManager.Agent.Files;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;
using TurboManager.Core;

namespace TurboManager.Agent.Backups;

/// <summary>A restic repository: where it is, the options to reach it, and the secrets it needs (environment only).</summary>
public sealed record ResticRepository(string Location, IReadOnlyList<string> Options, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Backups with restic. A run dumps the plan's databases into a staging folder next to a manifest (what the snapshot
/// holds), then backs that up together with the plan's app folders and, optionally, the panel's own state. Restores
/// never overwrite anything: dumps join the database's backups list, folders come back next to the original.
/// </summary>
public sealed partial class BackupManager(ICommandRunner commands, DatabaseManager databases, FilesManager files, IOptions<AgentOptions> options,
    TimeProvider time, ILogger<BackupManager> logger)
{
    public const string ManifestName = "manifest.json";

    public static readonly string KeysDirectory = $"/etc/{Branding.Slug}/backup-keys";

    /// <summary>Written by the panel (VACUUM INTO) before a run that includes its state.</summary>
    public static readonly string PanelDatabaseCopy = $"{ManagerPaths.StateRoot}/backup/panel.db";

    public static readonly string[] PanelStatePaths = [PanelDatabaseCopy, $"{ManagerPaths.StateRoot}/keys", $"/etc/{Branding.Slug}"];

    /// <summary>Local repositories live under one of these (never inside app homes or the system's own directories).</summary>
    public static readonly string[] LocalRoots = ["/var/backups/", "/srv/", "/mnt/", "/media/", "/backup/", "/backups/", "/var/lib/turbo-manager-backups/repositories/"];

    private static readonly TimeSpan ResticTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan LongTimeout = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string HostPath(string path) => Path.Combine(options.Value.HostRoot, path.TrimStart('/'));

    public string StagingDirectory(string planId) => $"{options.Value.BackupRoot}/staging/{PlanId(planId)}";

    private string CacheDirectory => $"/var/cache/{Branding.Slug}-restic";

    public async Task<TargetStatus> PrepareAsync(BackupTarget target, CancellationToken cancellationToken)
    {
        var version = await EnsureResticAsync(cancellationToken);
        var repository = Repository(target);

        if (target.Kind == "local")
        {
            Directory.CreateDirectory(HostPath(target.Path));
            SetPrivate(HostPath(target.Path));
        }

        // "init" first: it refuses to touch an existing repository, and it answers at once either way. (Probing a repository
        // that doesn't exist yet makes restic ≥ 0.17 retry for up to 15 minutes, e.g. while an S3 bucket is missing.)
        var created = await ResticAsync(repository, ["init"], cancellationToken, PrepareTimeout);

        if (!created.Success && !created.ErrorText.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The repository can't be created: {Last(created.ErrorText)}"));
        }

        // It exists: does it open with this password?
        var opened = await ResticAsync(repository, ["cat", "config"], cancellationToken, PrepareTimeout);

        if (!opened.Success)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The repository can't be opened: {Last(opened.ErrorText)}"));
        }

        if (!created.Success)
        {
            return new TargetStatus { ResticVersion = version };
        }

        logger.LogInformation("Created the backup repository {Location}", repository.Location);

        return new TargetStatus { Created = true, ResticVersion = version };
    }

    public async Task<string> RunAsync(BackupRunRequest request, Func<string, Task> write, CancellationToken cancellationToken)
    {
        await EnsureResticAsync(cancellationToken);
        var repository = Repository(request.Target);
        var planId = PlanId(request.PlanId);
        var staging = StagingDirectory(planId);
        var local = HostPath(staging);
        var paths = new List<string> { staging };
        var manifest = new BackupManifest { PlanId = planId, PlanName = request.PlanName, Created = time.GetUtcNow().ToString("O"), PanelState = request.PanelState };

        if (Directory.Exists(local))
        {
            Directory.Delete(local, recursive: true);
        }

        Directory.CreateDirectory(local);
        SetPrivate(HostPath(options.Value.BackupRoot));
        SetPrivate(local);

        try
        {
            foreach (var item in request.Databases)
            {
                var spec = Engine(item.Engine);
                var name = DatabaseSql.Name(item.Name, "database");
                var file = Path.Combine(local, "databases", spec.Slug, name, $"{name}.sql.gz");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await write($"Dumping the {spec.Slug} database {name}…");
                await databases.DumpAsync(new DatabaseRequest { Engine = spec.Engine, Name = name }, file, cancellationToken);
                await write($"  {Size(new FileInfo(file).Length)}");
                manifest.Databases.Add(new DatabaseItem { Engine = spec.Slug, Name = name });
            }

            foreach (var item in request.Folders)
            {
                var (user, absolute, relative) = files.Resolve(item.User, item.Path);

                if (!Directory.Exists(HostPath(absolute)))
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{absolute} doesn't exist."));
                }

                paths.Add(absolute);
                manifest.Folders.Add(new FolderItem { User = user.Name, Path = relative });
            }

            if (request.PanelState)
            {
                if (!File.Exists(HostPath(PanelDatabaseCopy)))
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "The panel didn't leave a copy of its database to back up."));
                }

                paths.AddRange(PanelStatePaths.Where(p => File.Exists(HostPath(p)) || Directory.Exists(HostPath(p))));
            }

            await File.WriteAllTextAsync(Path.Combine(local, ManifestName), JsonSerializer.Serialize(ToJson(manifest), Json), cancellationToken);

            await write($"Backing up to {Describe(request.Target)}…");
            string? snapshot = null;
            var lastPercent = -10;
            var exit = await commands.StreamAsync("restic", [.. Arguments(repository), "backup", "--json", "--tag", Branding.ShortName, "--tag", $"plan:{planId}",
                .. paths], async line =>
            {
                if (ParseBackupLine(line) is not { } message)
                {
                    return;
                }

                if (message.Snapshot is not null)
                {
                    snapshot = message.Snapshot;
                }

                if (message.Percent is { } percent)
                {
                    if (percent < lastPercent + 10)
                    {
                        return;
                    }

                    lastPercent = percent;
                }

                await write(message.Text);
            }, mergeStdErr: true, cancellationToken, repository.Environment);

            // 3: a few files couldn't be read, but the snapshot was saved.
            if (exit is not 0 and not 3 || snapshot is null)
            {
                throw new RpcException(new Status(StatusCode.Aborted, $"restic backup failed (exit code {exit})."));
            }

            await ForgetAsync(repository, planId, request.Retention, write, cancellationToken);

            return snapshot;
        }
        finally
        {
            if (Directory.Exists(local))
            {
                Directory.Delete(local, recursive: true);
            }

            if (request.PanelState && File.Exists(HostPath(PanelDatabaseCopy)))
            {
                File.Delete(HostPath(PanelDatabaseCopy));
            }
        }
    }

    public async Task<SnapshotList> ListSnapshotsAsync(SnapshotsRequest request, CancellationToken cancellationToken)
    {
        await EnsureResticAsync(cancellationToken);
        var repository = Repository(request.Target);
        string[] filter = request.PlanId.Length > 0 ? ["--tag", $"plan:{PlanId(request.PlanId)}"] : [];
        var result = await ResticAsync(repository, ["snapshots", "--json", .. filter], cancellationToken);

        if (!result.Success)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Listing snapshots failed: {Last(result.ErrorText)}"));
        }

        var reply = new SnapshotList();
        reply.Snapshots.AddRange(ParseSnapshots(result.StdOut).OrderByDescending(s => s.Time, StringComparer.Ordinal));

        return reply;
    }

    public async Task<BackupManifest> ReadManifestAsync(SnapshotRef request, CancellationToken cancellationToken)
    {
        await EnsureResticAsync(cancellationToken);
        var repository = Repository(request.Target);
        var snapshot = await FindSnapshotAsync(repository, request.SnapshotId, cancellationToken);
        var result = await ResticAsync(repository, ["dump", snapshot.Id, $"{StagingDirectory(snapshot.PlanId)}/{ManifestName}"], cancellationToken);

        if (!result.Success)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"The snapshot has no manifest: {Last(result.ErrorText)}"));
        }

        return FromJson(result.StdOut);
    }

    public async Task RestoreAsync(RestoreSnapshotRequest request, Func<string, Task> write, CancellationToken cancellationToken)
    {
        await EnsureResticAsync(cancellationToken);
        var repository = Repository(request.Target);
        var snapshot = await FindSnapshotAsync(repository, request.SnapshotId, cancellationToken);
        var manifest = await ReadManifestAsync(new SnapshotRef { Target = request.Target, SnapshotId = snapshot.Id }, cancellationToken);
        var made = DateTimeOffset.TryParse(snapshot.Time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : time.GetUtcNow();
        var stamp = made.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var staging = StagingDirectory(snapshot.PlanId);
        var includes = new List<string>();
        var folders = new List<(FolderItem Item, string Absolute)>();

        foreach (var item in request.Databases)
        {
            var spec = Engine(item.Engine);

            if (!manifest.Databases.Any(d => d.Engine == spec.Slug && d.Name == item.Name))
            {
                throw Validate.Invalid($"The snapshot has no {spec.Slug} database {item.Name}.");
            }

            includes.Add($"{staging}/databases/{spec.Slug}/{item.Name}");
        }

        foreach (var item in request.Folders)
        {
            var (user, absolute, relative) = files.Resolve(item.User, item.Path);

            if (!manifest.Folders.Any(f => f.User == user.Name && f.Path == relative))
            {
                throw Validate.Invalid($"The snapshot has no folder {relative} of {user.Name}.");
            }

            includes.Add(absolute);
            folders.Add((new FolderItem { User = user.Name, Path = relative }, absolute));
        }

        if (request.PanelState)
        {
            if (!manifest.PanelState)
            {
                throw Validate.Invalid("The snapshot doesn't hold the panel's state.");
            }

            includes.AddRange(PanelStatePaths);
        }

        if (includes.Count == 0)
        {
            throw Validate.Invalid("Choose what to restore.");
        }

        // Root-only scratch space: nothing the restore writes is reachable by an app user until it's in place.
        var scratch = $"{options.Value.BackupRoot}/restores/{stamp}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        Directory.CreateDirectory(HostPath(scratch));
        SetPrivate(HostPath($"{options.Value.BackupRoot}/restores"));
        SetPrivate(HostPath(scratch));

        try
        {
            await write($"Restoring from snapshot {snapshot.ShortId} ({made.ToLocalTime():yyyy-MM-dd HH:mm})…");
            var exit = await commands.StreamAsync("restic", [.. Arguments(repository), "restore", snapshot.Id, "--target", scratch,
                .. includes.SelectMany(i => new[] { "--include", i })], line => new ValueTask(write($"  {line}")), mergeStdErr: true,
                cancellationToken, repository.Environment);

            if (exit != 0)
            {
                throw new RpcException(new Status(StatusCode.Aborted, $"restic restore failed (exit code {exit})."));
            }

            foreach (var item in request.Databases)
            {
                var spec = Engine(item.Engine);
                var dump = HostPath($"{scratch}{staging}/databases/{spec.Slug}/{item.Name}/{item.Name}.sql.gz");
                var file = databases.ImportDump(spec.Engine, item.Name, dump, made);
                await write($"The {spec.Slug} database {item.Name} as of {made.ToLocalTime():yyyy-MM-dd HH:mm}: {file.Id} is in its backups list; restore it from there.");
            }

            foreach (var (item, absolute) in folders)
            {
                await write($"{item.Path} of {item.User}: {await PlaceFolderAsync(item, $"{scratch}{absolute}", stamp, cancellationToken)}");
            }

            if (request.PanelState)
            {
                var kept = $"{options.Value.BackupRoot}/restores/panel-state-{stamp}";
                Directory.CreateDirectory(HostPath(kept));

                foreach (var path in PanelStatePaths.Where(p => File.Exists(HostPath($"{scratch}{p}")) || Directory.Exists(HostPath($"{scratch}{p}"))))
                {
                    var target = HostPath($"{kept}{path}");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(HostPath($"{scratch}{path}"), target);
                }

                await write($"The panel's state is in {kept} (root-only). The documentation's \"Restoring the panel\" page explains how to put it back.");
            }
        }
        finally
        {
            if (Directory.Exists(HostPath(scratch)))
            {
                Directory.Delete(HostPath(scratch), recursive: true);
            }
        }
    }

    /// <summary>~/restored-&lt;time&gt;/&lt;path&gt;: root-only while it fills, then handed to the app user.</summary>
    private async Task<string> PlaceFolderAsync(FolderItem item, string restored, string stamp, CancellationToken cancellationToken)
    {
        var (user, _, _) = files.Resolve(item.User, "");
        var top = $"{user.Home}/restored-{stamp}";
        var destination = $"{top}/{item.Path}";

        if (!Directory.Exists(HostPath(top)))
        {
            Directory.CreateDirectory(HostPath(top));
            SetPrivate(HostPath(top));
        }

        Directory.CreateDirectory(HostPath(Path.GetDirectoryName(destination)!.Replace('\\', '/')));
        await commands.RunCheckedAsync("cp", ["-a", "--", HostPath(restored), HostPath(destination)], timeout: LongTimeout, cancellationToken: cancellationToken);
        await commands.RunCheckedAsync("chown", ["-R", "--no-dereference", $"{user.Uid}:{user.Gid}", HostPath(top)], timeout: LongTimeout,
            cancellationToken: cancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(HostPath(top), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        return $"restored to {destination} (the original is untouched).";
    }

    public async Task<TargetKey> CreateTargetKeyAsync(string targetId, CancellationToken cancellationToken)
    {
        var key = HostPath($"{KeysDirectory}/{TargetId(targetId)}");
        Directory.CreateDirectory(HostPath(KeysDirectory));
        SetPrivate(HostPath(KeysDirectory));

        if (!File.Exists(key))
        {
            await commands.RunCheckedAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", $"{Branding.Slug}-backup-{targetId}", "-f", key],
                cancellationToken: cancellationToken);
        }

        return new TargetKey { PublicKey = (await File.ReadAllTextAsync(key + ".pub", cancellationToken)).Trim() };
    }

    public async Task<HostKeyReply> ScanHostKeyAsync(HostKeyRequest request, CancellationToken cancellationToken)
    {
        var host = Host(request.Host);
        var port = request.Port is 0 ? 22 : request.Port is > 0 and < 65536 ? request.Port : throw Validate.Invalid("Ports are 1-65535.");
        var result = await commands.RunAsync("ssh-keyscan", ["-T", "10", "-p", $"{port}", "-t", "ed25519", host], timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
        var line = result.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        var parts = line?.Split(' ');

        if (parts is not { Length: >= 3 })
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{host}:{port} didn't answer with an SSH host key."));
        }

        return new HostKeyReply { HostKey = $"{parts[1]} {parts[2]}", Fingerprint = Fingerprint(parts[2]) };
    }

    /// <summary>Where the repository is and how restic reaches it. Everything from the panel is checked here.</summary>
    public ResticRepository Repository(BackupTarget target)
    {
        if (target.Password.Length < 16)
        {
            throw Validate.Invalid("The repository password is missing.");
        }

        var environment = new Dictionary<string, string> { ["RESTIC_PASSWORD"] = target.Password };

        switch (target.Kind)
        {
            case "local":
                return new ResticRepository(LocalPath(target.Path), [], environment);

            case "s3":
            {
                var endpoint = target.Endpoint.Length == 0 ? "https://s3.amazonaws.com" : target.Endpoint.TrimEnd('/');

                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.AbsolutePath != "/"
                    || !BucketPattern().IsMatch(target.Path))
                {
                    throw Validate.Invalid("S3: an https:// endpoint and a bucket name (optionally bucket/folder).");
                }

                if (target.AccessKeyId.Length == 0 || target.SecretAccessKey.Length == 0)
                {
                    throw Validate.Invalid("S3: the access key and secret are required.");
                }

                environment["AWS_ACCESS_KEY_ID"] = target.AccessKeyId;
                environment["AWS_SECRET_ACCESS_KEY"] = target.SecretAccessKey;

                if (target.Region.Length > 0)
                {
                    environment["AWS_DEFAULT_REGION"] = target.Region;
                }

                return new ResticRepository($"s3:{endpoint}/{target.Path}", [], environment);
            }

            case "sftp":
            {
                var host = Host(target.Endpoint);
                var port = target.Port is 0 ? 22 : target.Port is > 0 and < 65536 ? target.Port : throw Validate.Invalid("Ports are 1-65535.");

                if (!UserPattern().IsMatch(target.User) || !RemotePathPattern().IsMatch(target.Path) || target.Path.Contains("..", StringComparison.Ordinal))
                {
                    throw Validate.Invalid("SFTP: a user name and a folder on the server.");
                }

                if (!HostKeyPattern().IsMatch(target.HostKey))
                {
                    throw Validate.Invalid("SFTP: the server's host key must be confirmed first.");
                }

                var id = TargetId(target.Id);
                var key = $"{KeysDirectory}/{id}";

                if (!File.Exists(HostPath(key)))
                {
                    throw Validate.FailedPrecondition("SFTP: this target has no key yet.");
                }

                // The pinned host key, rewritten every time (it lives in the panel).
                var knownHosts = $"{key}.known_hosts";
                File.WriteAllText(HostPath(knownHosts), $"{(port == 22 ? host : $"[{host}]:{port}")} {target.HostKey}\n");
                SetPrivate(HostPath(knownHosts));

                var ssh = $"ssh -i {key} -p {port} -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile={knownHosts} "
                    + $"{target.User}@{host} -s sftp";

                return new ResticRepository($"sftp:{target.User}@{host}:{target.Path}", ["-o", $"sftp.command={ssh}"], environment);
            }

            default:
                throw Validate.Invalid("Targets are local, s3 or sftp.");
        }
    }

    private async Task ForgetAsync(ResticRepository repository, string planId, Retention? retention, Func<string, Task> write, CancellationToken cancellationToken)
    {
        var keep = new List<string>();

        if (retention?.Daily > 0) keep.AddRange(["--keep-daily", $"{retention.Daily}"]);
        if (retention?.Weekly > 0) keep.AddRange(["--keep-weekly", $"{retention.Weekly}"]);
        if (retention?.Monthly > 0) keep.AddRange(["--keep-monthly", $"{retention.Monthly}"]);

        if (keep.Count == 0)
        {
            return;
        }

        await write("Applying the retention policy…");
        var removed = 0;
        var exit = await commands.StreamAsync("restic", [.. Arguments(repository), "forget", "--tag", $"plan:{planId}", "--group-by", "tags", .. keep, "--prune"],
            line =>
            {
                if (line.StartsWith("remove ", StringComparison.Ordinal) && int.TryParse(line.Split(' ')[1], out var count))
                {
                    removed += count;
                }

                return ValueTask.CompletedTask;
            }, mergeStdErr: true, cancellationToken, repository.Environment);

        // A failed prune leaves extra snapshots behind; the backup itself is fine.
        await write(exit == 0 ? $"  {removed} old snapshot(s) removed." : $"  Retention failed (exit code {exit}); old snapshots were kept.");
    }

    private async Task<Snapshot> FindSnapshotAsync(ResticRepository repository, string id, CancellationToken cancellationToken)
    {
        if (!SnapshotIdPattern().IsMatch(id))
        {
            throw Validate.Invalid("Not a snapshot id.");
        }

        var result = await ResticAsync(repository, ["snapshots", "--json", id], cancellationToken);
        var snapshot = result.Success ? ParseSnapshots(result.StdOut).FirstOrDefault() : null;

        return snapshot is { PlanId.Length: > 0 } ? snapshot : throw new RpcException(new Status(StatusCode.NotFound, "Snapshot not found."));
    }

    private async Task<string> EnsureResticAsync(CancellationToken cancellationToken)
    {
        // Starting a program that isn't there throws, so look for it first.
        if (!File.Exists(HostPath("/usr/bin/restic")))
        {
            logger.LogInformation("Installing restic");
            await commands.RunCheckedAsync("apt-get", ["install", "-y", "-q", "--no-install-recommends", "restic"], timeout: TimeSpan.FromMinutes(10),
                cancellationToken: cancellationToken);
        }

        return (await commands.RunCheckedAsync("restic", ["version"], cancellationToken: cancellationToken)).StdOut.Trim();
    }

    private async Task<CommandResult> ResticAsync(ResticRepository repository, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        try
        {
            return await commands.RunAsync("restic", [.. Arguments(repository), .. arguments], timeout: timeout ?? ResticTimeout,
                cancellationToken: cancellationToken, environment: repository.Environment);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                $"restic {arguments[0]} got no answer from {repository.Location} within {(timeout ?? ResticTimeout).TotalMinutes:0} minutes: check the address and credentials."));
        }
    }

    private IReadOnlyList<string> Arguments(ResticRepository repository) =>
        ["--repo", repository.Location, "--cache-dir", CacheDirectory, .. repository.Options];

    /// <summary>One line of <c>restic backup --json</c> as something to show (null: nothing worth showing).</summary>
    public static (string Text, int? Percent, string? Snapshot)? ParseBackupLine(string line)
    {
        if (!line.StartsWith('{'))
        {
            return line.Trim().Length > 0 ? (line.Trim(), null, null) : null;
        }

        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;

            switch (root.TryGetProperty("message_type", out var type) ? type.GetString() : null)
            {
                case "status":
                    var percent = root.TryGetProperty("percent_done", out var done) ? (int)(done.GetDouble() * 100) : 0;

                    return ($"  {percent}% ({Size(Long(root, "bytes_done"))} of {Size(Long(root, "total_bytes"))})", percent, null);

                case "summary":
                    var id = root.TryGetProperty("snapshot_id", out var snapshot) ? snapshot.GetString() : null;

                    return ($"Snapshot {id?[..Math.Min(8, id.Length)]} saved: {Long(root, "files_new")} new, {Long(root, "files_changed")} changed, "
                        + $"{Long(root, "files_unmodified")} unchanged files; {Size(Long(root, "data_added"))} added to the repository.", null, id);

                case "error":
                    var item = root.TryGetProperty("item", out var path) ? path.GetString() : "";
                    var message = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text) ? text.GetString() : "";

                    return ($"  ! {item}: {message}", null, null);

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return (line, null, null);
        }

        static long Long(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    }

    public static IEnumerable<Snapshot> ParseSnapshots(string json)
    {
        using var document = JsonDocument.Parse(json.Trim().Length == 0 ? "[]" : json);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var snapshot = new Snapshot
            {
                Id = item.GetProperty("id").GetString() ?? "",
                ShortId = item.TryGetProperty("short_id", out var shortId) ? shortId.GetString() ?? "" : "",
                Time = item.TryGetProperty("time", out var when) ? when.GetString() ?? "" : "",
                Hostname = item.TryGetProperty("hostname", out var host) ? host.GetString() ?? "" : "",
            };

            if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                snapshot.Tags.AddRange(tags.EnumerateArray().Select(t => t.GetString() ?? ""));
            }

            snapshot.PlanId = snapshot.Tags.FirstOrDefault(t => t.StartsWith("plan:", StringComparison.Ordinal))?[5..] ?? "";
            snapshot.ShortId = snapshot.ShortId.Length > 0 ? snapshot.ShortId : snapshot.Id[..Math.Min(8, snapshot.Id.Length)];

            yield return snapshot;
        }
    }

    private sealed record ManifestJson(string PlanId, string PlanName, string Created, List<DatabaseJson> Databases, List<FolderJson> Folders, bool PanelState);

    private sealed record DatabaseJson(string Engine, string Name);

    private sealed record FolderJson(string User, string Path);

    private static ManifestJson ToJson(BackupManifest manifest) => new(manifest.PlanId, manifest.PlanName, manifest.Created,
        [.. manifest.Databases.Select(d => new DatabaseJson(d.Engine, d.Name))], [.. manifest.Folders.Select(f => new FolderJson(f.User, f.Path))],
        manifest.PanelState);

    public static BackupManifest FromJson(string json)
    {
        var parsed = JsonSerializer.Deserialize<ManifestJson>(json, Json) ?? throw new RpcException(new Status(StatusCode.DataLoss, "Unreadable manifest."));
        var manifest = new BackupManifest { PlanId = parsed.PlanId, PlanName = parsed.PlanName, Created = parsed.Created, PanelState = parsed.PanelState };
        manifest.Databases.AddRange(parsed.Databases.Select(d => new DatabaseItem { Engine = d.Engine, Name = d.Name }));
        manifest.Folders.AddRange(parsed.Folders.Select(f => new FolderItem { User = f.User, Path = f.Path }));

        return manifest;
    }

    private static EngineSpec Engine(string slug) =>
        DatabaseManager.Engines.FirstOrDefault(e => e.Slug == slug) ?? throw Validate.Invalid("Engines: mysql, mariadb, postgres.");

    private static string LocalPath(string path)
    {
        var normalized = "/" + string.Join('/', (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries));

        if (!path!.StartsWith('/') || normalized.Split('/').Any(p => p is ".." or ".") || !LocalPathPattern().IsMatch(normalized)
            || !LocalRoots.Any(root => (normalized + "/").StartsWith(root, StringComparison.Ordinal) && normalized.Length + 1 > root.Length))
        {
            throw Validate.Invalid($"Local repositories go in a folder under {string.Join(", ", LocalRoots.Select(r => r.TrimEnd('/')))}.");
        }

        return normalized;
    }

    private static string Host(string host) =>
        HostPattern().IsMatch(host ?? "") ? host! : throw Validate.Invalid("Not a host name or IP address.");

    private static string PlanId(string id) => PlanIdPattern().IsMatch(id ?? "") ? id! : throw Validate.Invalid("Not a plan id.");

    private static string TargetId(string id) => PlanIdPattern().IsMatch(id ?? "") ? id! : throw Validate.Invalid("Not a target id.");

    private static string Describe(BackupTarget target) => target.Kind switch
    {
        "s3" => $"S3 {target.Path}",
        "sftp" => $"{target.User}@{target.Endpoint}:{target.Path}",
        _ => target.Path,
    };

    public static string Fingerprint(string base64Key) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64Key))).TrimEnd('=');

    private static string Last(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    private static void SetPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex PlanIdPattern();

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex SnapshotIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9](/[A-Za-z0-9._-]+)*$")]
    private static partial Regex BucketPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$")]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[a-z_][a-z0-9_.-]{0,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex("^/?[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*/?$")]
    private static partial Regex RemotePathPattern();

    [GeneratedRegex("^/[A-Za-z0-9._/-]+$")]
    private static partial Regex LocalPathPattern();

    [GeneratedRegex("^(ssh-ed25519|ecdsa-sha2-nistp(256|384|521)|ssh-rsa) [A-Za-z0-9+/=]+$")]
    private static partial Regex HostKeyPattern();
}
