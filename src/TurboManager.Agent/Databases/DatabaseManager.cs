using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Databases;

public sealed record EngineSpec(DatabaseEngine Engine, string Slug, string Package, string Service, int Port, string Client, string Dump);

/// <summary>Runs administrative SQL, dumps and restores for each engine as its local administrator.</summary>
public sealed partial class DatabaseManager(ICommandRunner commands, IOptions<AgentOptions> options, TimeProvider time, ILogger<DatabaseManager> logger)
{
    public static readonly IReadOnlyList<EngineSpec> Engines =
    [
        new(DatabaseEngine.Mysql, "mysql", "mysql-server", "mysql", 3306, "mysql", "mysqldump"),
        new(DatabaseEngine.Mariadb, "mariadb", "mariadb-server", "mariadb", 3306, "mariadb", "mariadb-dump"),
        new(DatabaseEngine.Postgres, "postgres", "postgresql", "postgresql", 5432, "psql", "pg_dump"),
    ];

    public static readonly TimeSpan SqlTimeout = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DumpTimeout = TimeSpan.FromHours(6);

    public const long MaxImportBytes = 4L * 1024 * 1024 * 1024;

    private string BackupRoot => Path.Combine(options.Value.HostRoot, options.Value.DatabaseBackupRoot.TrimStart('/'));

    public static EngineSpec Spec(DatabaseEngine engine) =>
        Engines.FirstOrDefault(e => e.Engine == engine) ?? throw Validate.Invalid("Choose a database engine.");

    public async Task<DatabaseEngines> GetEnginesAsync(CancellationToken cancellationToken)
    {
        var reply = new DatabaseEngines();
        var statuses = new Dictionary<DatabaseEngine, (bool Installed, string Version)>();

        foreach (var spec in Engines)
        {
            statuses[spec.Engine] = await PackageStatusAsync(spec.Package, cancellationToken);
        }

        var candidates = new Dictionary<DatabaseEngine, string>();

        foreach (var spec in Engines.Where(s => !statuses[s.Engine].Installed))
        {
            candidates[spec.Engine] = await CandidateVersionAsync(spec.Package, cancellationToken);
        }

        var mysqlVersion = statuses[DatabaseEngine.Mysql].Installed ? statuses[DatabaseEngine.Mysql].Version : candidates.GetValueOrDefault(DatabaseEngine.Mysql, "");
        var recommended = Recommend(mysqlVersion, e => statuses[e].Installed);

        foreach (var spec in Engines)
        {
            var (installed, version) = statuses[spec.Engine];
            var active = installed && (await commands.RunAsync("systemctl", ["is-active", spec.Service], cancellationToken: cancellationToken)).StdOut.Trim() == "active";

            reply.Engines.Add(new DatabaseEngineStatus
            {
                Engine = spec.Engine,
                Installed = installed,
                Running = active,
                Version = version,
                CandidateVersion = candidates.GetValueOrDefault(spec.Engine, ""),
                Service = $"{spec.Service}.service",
                Port = spec.Port,
                BlockedReason = BlockedReason(spec.Engine, e => statuses[e].Installed) ?? "",
                Recommended = spec.Engine == recommended,
                Note = Note(spec.Engine, mysqlVersion) ?? "",
            });
        }

        return reply;
    }

    /// <summary>
    /// Which of MySQL/MariaDB to use for Turbo: whichever is already installed; otherwise MySQL,
    /// unless the MySQL this Ubuntu release ships is past Oracle's support (8.0 and older), in which
    /// case MariaDB (Ubuntu 24.04).
    /// </summary>
    public static DatabaseEngine Recommend(string mysqlVersion, Func<DatabaseEngine, bool> installed) =>
        installed(DatabaseEngine.Mysql) ? DatabaseEngine.Mysql
        : installed(DatabaseEngine.Mariadb) ? DatabaseEngine.Mariadb
        : IsMySqlOutOfSupport(mysqlVersion) ? DatabaseEngine.Mariadb
        : DatabaseEngine.Mysql;

    public static string? Note(DatabaseEngine engine, string mysqlVersion) => engine switch
    {
        DatabaseEngine.Mysql when IsMySqlOutOfSupport(mysqlVersion) =>
            $"Ubuntu's MySQL {UpstreamVersion(mysqlVersion)} is past Oracle's end of support (April 2026). Use MariaDB for Turbo on this server.",
        DatabaseEngine.Postgres => "Turbo doesn't use PostgreSQL; install it for other apps that need it.",
        _ => null,
    };

    /// <summary>MySQL older than 8.4 (the current LTS) no longer gets fixes from Oracle.</summary>
    public static bool IsMySqlOutOfSupport(string debianVersion)
    {
        var parts = UpstreamVersion(debianVersion).Split('.');

        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            && (major < 8 || (major == 8 && minor < 4));
    }

    /// <summary>"1:8.0.46-0ubuntu0.24.04.4" → "8.0.46".</summary>
    public static string UpstreamVersion(string debianVersion)
    {
        var version = debianVersion.Contains(':') ? debianVersion[(debianVersion.IndexOf(':') + 1)..] : debianVersion;

        return version.Split('-', '+', '~')[0];
    }

    private async Task<string> CandidateVersionAsync(string package, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commands.RunAsync("apt-cache", ["policy", package], cancellationToken: cancellationToken);
            var line = result.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Candidate:", StringComparison.Ordinal));
            var candidate = line?["Candidate:".Length..].Trim() ?? "";

            return candidate == "(none)" ? "" : candidate;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>MySQL and MariaDB use the same port and conflicting packages: only one of them per server.</summary>
    public static string? BlockedReason(DatabaseEngine engine, Func<DatabaseEngine, bool> installed) => engine switch
    {
        DatabaseEngine.Mysql when installed(DatabaseEngine.Mariadb) => "MariaDB is installed; MySQL and MariaDB can't run on the same server.",
        DatabaseEngine.Mariadb when installed(DatabaseEngine.Mysql) => "MySQL is installed; MySQL and MariaDB can't run on the same server.",
        _ => null,
    };

    public async Task InstallAsync(DatabaseEngine engine, Func<string, ValueTask> output, CancellationToken cancellationToken)
    {
        var spec = Spec(engine);
        var engines = await GetEnginesAsync(cancellationToken);

        if (engines.Engines.First(e => e.Engine == engine).BlockedReason is { Length: > 0 } reason)
        {
            throw Validate.FailedPrecondition(reason);
        }

        await output($"$ apt-get install -y {spec.Package}");
        var exit = await commands.StreamAsync("apt-get", ["install", "-y", "--no-install-recommends", spec.Package],
            line => output(line), mergeStdErr: true, cancellationToken);

        if (exit != 0)
        {
            throw new RpcException(new Status(StatusCode.Aborted, $"apt-get exited with code {exit}."));
        }

        await output($"$ systemctl enable --now {spec.Service}");
        await commands.RunCheckedAsync("systemctl", ["enable", "--now", spec.Service], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
    }

    public async Task<DatabaseList> ListDatabasesAsync(DatabaseEngine engine, CancellationToken cancellationToken)
    {
        var reply = new DatabaseList();

        foreach (var row in DatabaseSql.Rows(await QueryAsync(engine, DatabaseSql.ListDatabases(engine), null, cancellationToken)))
        {
            reply.Databases.Add(new DatabaseInfo
            {
                Name = row[0],
                SizeBytes = long.TryParse(row.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                Tables = int.TryParse(row.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tables) ? tables : -1,
            });
        }

        return reply;
    }

    public Task CreateDatabaseAsync(DatabaseRequest request, CancellationToken cancellationToken) =>
        QueryAsync(request.Engine, DatabaseSql.CreateDatabase(request.Engine, DatabaseSql.Name(request.Name, "database")), null, cancellationToken);

    public Task DropDatabaseAsync(DatabaseRequest request, CancellationToken cancellationToken) =>
        QueryAsync(request.Engine, DatabaseSql.DropDatabase(request.Engine, DatabaseSql.Name(request.Name, "database")), null, cancellationToken);

    public async Task<DatabaseUserList> ListUsersAsync(DatabaseEngine engine, CancellationToken cancellationToken)
    {
        var users = DatabaseSql.Rows(await QueryAsync(engine, DatabaseSql.ListUsers(engine), null, cancellationToken)).Select(r => r[0]).ToList();
        var grantRows = DatabaseSql.Rows(await QueryAsync(engine, DatabaseSql.ListGrants(engine), null, cancellationToken));
        var grants = DatabaseSql.IsMySqlFamily(engine)
            ? DatabaseSql.ParseMySqlGrants(grantRows)
            : grantRows.Where(r => r.Length >= 2).GroupBy(r => r[1])
                .ToDictionary(g => g.Key, g => g.Select(r => new DatabaseGrant { Database = r[0], Level = "all" }).ToList());

        var reply = new DatabaseUserList();

        foreach (var user in users)
        {
            var entry = new DatabaseUser { Name = user };
            entry.Grants.AddRange(grants.GetValueOrDefault(user) ?? []);
            reply.Users.Add(entry);
        }

        return reply;
    }

    public Task CreateUserAsync(DatabaseUserRequest request, CancellationToken cancellationToken) =>
        QueryAsync(request.Engine, DatabaseSql.CreateUser(request.Engine, DatabaseSql.Name(request.Name, "user"), DatabaseSql.Password(request.Password)),
            null, cancellationToken);

    public Task SetPasswordAsync(DatabaseUserRequest request, CancellationToken cancellationToken) =>
        QueryAsync(request.Engine, DatabaseSql.SetPassword(request.Engine, DatabaseSql.Name(request.Name, "user"), DatabaseSql.Password(request.Password)),
            null, cancellationToken);

    public async Task DropUserAsync(DatabaseUserRequest request, CancellationToken cancellationToken)
    {
        var name = DatabaseSql.Name(request.Name, "user");

        if (DatabaseSql.IsMySqlFamily(request.Engine))
        {
            await QueryAsync(request.Engine, DatabaseSql.DropMySqlUser(name), null, cancellationToken);

            return;
        }

        // A PostgreSQL role can only be dropped once nothing belongs to it: hand its databases and
        // objects to the postgres superuser first, database by database.
        foreach (var database in (await ListDatabasesAsync(request.Engine, cancellationToken)).Databases)
        {
            await QueryAsync(request.Engine, $"REASSIGN OWNED BY \"{name}\" TO postgres;\nDROP OWNED BY \"{name}\";", database.Name, cancellationToken);
        }

        await QueryAsync(request.Engine, $"REASSIGN OWNED BY \"{name}\" TO postgres;\nDROP OWNED BY \"{name}\";\nDROP ROLE \"{name}\";", null, cancellationToken);
    }

    public async Task SetGrantAsync(GrantRequest request, CancellationToken cancellationToken)
    {
        var user = DatabaseSql.Name(request.User, "user");
        var database = DatabaseSql.Name(request.Database, "database");

        if (DatabaseSql.IsMySqlFamily(request.Engine))
        {
            var (revoke, grant) = DatabaseSql.MySqlGrant(user, database, request.Level);

            // REVOKE fails when there was nothing to revoke; that's fine.
            await TryQueryAsync(request.Engine, revoke, cancellationToken);

            if (grant is not null)
            {
                await QueryAsync(request.Engine, grant, null, cancellationToken);
            }

            return;
        }

        var sql = request.Level switch
        {
            "all" => $"ALTER DATABASE \"{database}\" OWNER TO \"{user}\";",
            "none" => $"ALTER DATABASE \"{database}\" OWNER TO postgres;",
            "readonly" => throw Validate.Invalid("Read-only access isn't available for PostgreSQL yet; give full access or none."),
            _ => throw Validate.Invalid("Access level must be all, readonly or none."),
        };

        await QueryAsync(request.Engine, sql, null, cancellationToken);
    }

    /// <summary>Dumps a database to a gzip file under the backup root.</summary>
    public async Task<BackupFile> BackupAsync(DatabaseRequest request, CancellationToken cancellationToken)
    {
        var spec = Spec(request.Engine);
        var name = DatabaseSql.Name(request.Name, "database");
        var directory = Path.Combine(BackupRoot, spec.Slug, name);
        Directory.CreateDirectory(directory);
        SetPrivate(BackupRoot);

        var file = Path.Combine(directory, $"{name}-{time.GetLocalNow():yyyyMMdd-HHmmss}.sql.gz");
        var temp = file + ".partial";
        CommandResult result;

        await using (var output = File.Create(temp))
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            var (command, arguments) = DumpCommand(spec, name);
            result = await commands.RunWithStreamsAsync(command, arguments, stdin: null, stdout: gzip, DumpTimeout, cancellationToken);
        }

        if (!result.Success)
        {
            File.Delete(temp);

            throw new RpcException(new Status(StatusCode.Aborted, $"{spec.Dump} failed: {result.ErrorText}"));
        }

        SetPrivate(temp);
        File.Move(temp, file, overwrite: true);
        logger.LogInformation("Backed up {Engine} database {Database} to {File}", spec.Slug, name, file);

        return Describe(file);
    }

    public BackupList ListBackups(DatabaseEngine engine)
    {
        var reply = new BackupList();

        if (!Directory.Exists(BackupRoot))
        {
            return reply;
        }

        var files = Directory.EnumerateFiles(BackupRoot, "*.sql*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".partial", StringComparison.Ordinal))
            .Select(Describe)
            .Where(b => engine == DatabaseEngine.Unspecified || b.Engine == engine || b.Import)
            .OrderByDescending(b => b.CreatedUnixMs);

        reply.Backups.AddRange(files);

        return reply;
    }

    public void DeleteBackup(string id) => File.Delete(BackupPath(id));

    /// <summary>
    /// Restores a backup (or uploaded import) into a database, replacing it: the database is dropped
    /// and recreated (keeping its PostgreSQL owner; MySQL grants survive by name) and then loaded.
    /// </summary>
    public async Task RestoreAsync(RestoreRequest request, Func<string, ValueTask> output, CancellationToken cancellationToken)
    {
        var spec = Spec(request.Engine);
        var name = DatabaseSql.Name(request.Database, "database");
        var path = BackupPath(request.BackupId);

        if (!File.Exists(path))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Backup not found."));
        }

        string? owner = null;

        if (!DatabaseSql.IsMySqlFamily(request.Engine))
        {
            owner = DatabaseSql.Rows(await QueryAsync(request.Engine,
                $"SELECT r.rolname FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE d.datname = '{name}';", null, cancellationToken))
                .FirstOrDefault()?[0];
        }

        await output($"Recreating database {name}…");
        await TryQueryAsync(request.Engine, DatabaseSql.DropDatabase(request.Engine, name), cancellationToken);
        await QueryAsync(request.Engine, DatabaseSql.CreateDatabase(request.Engine, name), null, cancellationToken);

        if (owner is not null && owner != "postgres")
        {
            await QueryAsync(request.Engine, $"ALTER DATABASE \"{name}\" OWNER TO \"{owner}\";", null, cancellationToken);
        }

        await output($"Loading {Path.GetFileName(path)}…");
        await using var file = File.OpenRead(path);
        await using Stream input = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : file;
        var (command, arguments) = ClientCommand(spec, name);
        var result = await commands.RunWithStreamsAsync(command, arguments, input, stdout: null, DumpTimeout, cancellationToken);

        foreach (var line in result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(50))
        {
            await output(line.TrimEnd('\r'));
        }

        if (!result.Success)
        {
            throw new RpcException(new Status(StatusCode.Aborted, $"Loading the backup failed: {result.ErrorText}"));
        }

        await output("Restore finished.");
    }

    public async Task DownloadAsync(string id, IServerStreamWriter<FileChunk> stream, CancellationToken cancellationToken)
    {
        var path = BackupPath(id);

        if (!File.Exists(path))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Backup not found."));
        }

        await using var file = File.OpenRead(path);
        var buffer = new byte[128 * 1024];
        int read;

        while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await stream.WriteAsync(new FileChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }, cancellationToken);
        }
    }

    public async Task<BackupFile> UploadAsync(IAsyncStreamReader<FileChunk> stream, CancellationToken cancellationToken)
    {
        if (!await stream.MoveNext(cancellationToken))
        {
            throw Validate.Invalid("Empty upload.");
        }

        var original = Path.GetFileName(stream.Current.Name);

        if (!ImportNamePattern().IsMatch(original))
        {
            throw Validate.Invalid("Upload a .sql or .sql.gz file (letters, digits, '.', '_' and '-' in the name).");
        }

        var directory = Path.Combine(BackupRoot, "imports");
        Directory.CreateDirectory(directory);
        SetPrivate(BackupRoot);
        var file = Path.Combine(directory, $"{time.GetLocalNow():yyyyMMdd-HHmmss}-{original}");
        var temp = file + ".partial";
        long total = 0;

        try
        {
            await using (var output = File.Create(temp))
            {
                do
                {
                    total += stream.Current.Data.Length;

                    if (total > MaxImportBytes)
                    {
                        throw Validate.Invalid("The file is larger than 4 GB.");
                    }

                    stream.Current.Data.WriteTo(output);
                }
                while (await stream.MoveNext(cancellationToken));
            }

            SetPrivate(temp);
            File.Move(temp, file);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        return Describe(file);
    }

    private async Task<string> QueryAsync(DatabaseEngine engine, string sql, string? database, CancellationToken cancellationToken)
    {
        var spec = Spec(engine);
        var (command, arguments) = ClientCommand(spec, database);
        CommandResult result;

        try
        {
            result = await commands.RunAsync(command, arguments, stdin: sql, timeout: SqlTimeout, cancellationToken: cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw Validate.FailedPrecondition($"{spec.Package} is not installed.");
        }

        return result.Success
            ? result.StdOut
            : throw new RpcException(new Status(StatusCode.FailedPrecondition, CleanError(result.ErrorText)));
    }

    private async Task TryQueryAsync(DatabaseEngine engine, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await QueryAsync(engine, sql, null, cancellationToken);
        }
        catch (RpcException e)
        {
            logger.LogDebug("Ignored: {Message}", e.Status.Detail);
        }
    }

    /// <summary>Client invocation as the engine's local administrator (socket authentication).</summary>
    public static (string Command, List<string> Arguments) ClientCommand(EngineSpec spec, string? database)
    {
        if (DatabaseSql.IsMySqlFamily(spec.Engine))
        {
            List<string> arguments = ["--protocol=socket", "--user=root", "--batch", "--skip-column-names", "--raw", "--default-character-set=utf8mb4"];

            if (database is not null)
            {
                arguments.Add(database);
            }

            return (spec.Client, arguments);
        }

        return ("runuser", ["-u", "postgres", "--", "psql", "-X", "-q", "-A", "-t", "-F", "\t", "-v", "ON_ERROR_STOP=1", "-d", database ?? "postgres"]);
    }

    public static (string Command, List<string> Arguments) DumpCommand(EngineSpec spec, string database) =>
        DatabaseSql.IsMySqlFamily(spec.Engine)
            ? (spec.Dump, ["--protocol=socket", "--user=root", "--single-transaction", "--quick", "--routines", "--triggers", "--events", "--default-character-set=utf8mb4", database])
            : ("runuser", ["-u", "postgres", "--", "pg_dump", "--format=plain", "--no-password", "-d", database]);

    private string BackupPath(string id)
    {
        if (!BackupIdPattern().IsMatch(id) || id.Contains("..", StringComparison.Ordinal))
        {
            throw Validate.Invalid("Invalid backup id.");
        }

        return Path.Combine(BackupRoot, id);
    }

    private BackupFile Describe(string path)
    {
        var relative = Path.GetRelativePath(BackupRoot, path).Replace('\\', '/');
        var parts = relative.Split('/');
        var info = new FileInfo(path);
        var isImport = parts[0] == "imports";

        return new BackupFile
        {
            Id = relative,
            Engine = isImport ? DatabaseEngine.Unspecified : Engines.FirstOrDefault(e => e.Slug == parts[0])?.Engine ?? DatabaseEngine.Unspecified,
            Database = isImport || parts.Length < 3 ? "" : parts[1],
            SizeBytes = info.Length,
            CreatedUnixMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            Import = isImport,
        };
    }

    private async Task<(bool Installed, string Version)> PackageStatusAsync(string package, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commands.RunAsync("dpkg-query", ["-W", "-f=${db:Status-Status} ${Version}", package], cancellationToken: cancellationToken);
            var parts = result.StdOut.Trim().Split(' ', 2);

            return result.Success && parts[0] == "installed" ? (true, parts.ElementAtOrDefault(1) ?? "") : (false, "");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "");
        }
    }

    private static void SetPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, Directory.Exists(path)
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>"ERROR 1007 (HY000) at line 1: Can't create database..." → the message part.</summary>
    private static string CleanError(string error) =>
        ClientErrorPrefix().Replace(error.Split('\n').FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? error, "").Trim();

    [GeneratedRegex(@"^(imports|mysql|mariadb|postgres)/([a-z][a-z0-9_]{0,31}/)?[A-Za-z0-9._-]+\.sql(\.gz)?$")]
    private static partial Regex BackupIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,120}\.sql(\.gz)?$")]
    private static partial Regex ImportNamePattern();

    [GeneratedRegex(@"^(ERROR( \d+ \([0-9A-Z]+\))?( at line \d+)?:|psql:.*?ERROR:)\s*")]
    private static partial Regex ClientErrorPrefix();
}
