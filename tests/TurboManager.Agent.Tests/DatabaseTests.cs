using System.IO.Compression;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Commands;
using TurboManager.Agent.Databases;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Tests;

public sealed class DatabaseSqlTests
{
    [Theory]
    [InlineData("turbo", true)]
    [InlineData("turbo_db2", true)]
    [InlineData("Turbo", false)]
    [InlineData("2turbo", false)]
    [InlineData("turbo-db", false)]
    [InlineData("turbo`; DROP DATABASE x; --", false)]
    [InlineData("mysql", false)]
    [InlineData("root", false)]
    [InlineData("postgres", false)]
    [InlineData("a_name_that_is_far_too_long_for_mysql", false)]
    public void Names_are_strict(string name, bool valid)
    {
        if (valid)
        {
            Assert.Equal(name, DatabaseSql.Name(name, "database"));
        }
        else
        {
            Assert.Throws<RpcException>(() => DatabaseSql.Name(name, "database"));
        }
    }

    [Theory]
    [InlineData("Str0ng-Passw0rd!", true)]
    [InlineData("short", false)]
    [InlineData("has'quote1234567", false)]
    [InlineData("has\\backslash123", false)]
    [InlineData("has space 1234567", false)]
    [InlineData("has$dollar1234567", false)]
    public void Passwords_never_need_escaping(string password, bool valid)
    {
        if (valid)
        {
            Assert.Equal(password, DatabaseSql.Password(password));
        }
        else
        {
            Assert.Throws<RpcException>(() => DatabaseSql.Password(password));
        }
    }

    [Fact]
    public void Statements_quote_identifiers_per_engine()
    {
        Assert.Equal("CREATE DATABASE `turbo` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;", DatabaseSql.CreateDatabase(DatabaseEngine.Mysql, "turbo"));
        Assert.Contains("CREATE DATABASE \"turbo\" ENCODING 'UTF8'", DatabaseSql.CreateDatabase(DatabaseEngine.Postgres, "turbo"));
        Assert.Contains("REVOKE CONNECT ON DATABASE \"turbo\" FROM PUBLIC;", DatabaseSql.CreateDatabase(DatabaseEngine.Postgres, "turbo"));
        Assert.Equal("CREATE USER 'turbo'@'localhost' IDENTIFIED BY 'Str0ng-Passw0rd!';", DatabaseSql.CreateUser(DatabaseEngine.Mariadb, "turbo", "Str0ng-Passw0rd!"));
        Assert.Equal("CREATE ROLE \"turbo\" LOGIN PASSWORD 'Str0ng-Passw0rd!';", DatabaseSql.CreateUser(DatabaseEngine.Postgres, "turbo", "Str0ng-Passw0rd!"));
    }

    [Fact]
    public void Mysql_grants_escape_underscore_wildcards()
    {
        var (revoke, grant) = DatabaseSql.MySqlGrant("turbo", "turbo_db", "readonly");

        Assert.Equal("REVOKE ALL PRIVILEGES ON `turbo\\_db`.* FROM 'turbo'@'localhost';", revoke);
        Assert.Equal("GRANT SELECT, SHOW VIEW ON `turbo\\_db`.* TO 'turbo'@'localhost';", grant);
        Assert.Null(DatabaseSql.MySqlGrant("turbo", "turbo_db", "none").Grant);
    }

    [Fact]
    public void Schema_privileges_collapse_to_access_levels()
    {
        var grants = DatabaseSql.ParseMySqlGrants(
        [
            ["'turbo'@'localhost'", "turbo\\_db", "SELECT"],
            ["'turbo'@'localhost'", "turbo\\_db", "INSERT"],
            ["'reader'@'localhost'", "turbo\\_db", "SELECT"],
            ["'reader'@'localhost'", "turbo\\_db", "SHOW VIEW"],
            ["'remote'@'%'", "turbo\\_db", "SELECT"],
        ]);

        Assert.Equal("all", Assert.Single(grants["turbo"]).Level);
        Assert.Equal("turbo_db", grants["turbo"][0].Database);
        Assert.Equal("readonly", Assert.Single(grants["reader"]).Level);
        Assert.False(grants.ContainsKey("remote"));
    }

    [Fact]
    public void Mysql_and_mariadb_exclude_each_other()
    {
        Assert.NotNull(DatabaseManager.BlockedReason(DatabaseEngine.Mysql, e => e == DatabaseEngine.Mariadb));
        Assert.NotNull(DatabaseManager.BlockedReason(DatabaseEngine.Mariadb, e => e == DatabaseEngine.Mysql));
        Assert.Null(DatabaseManager.BlockedReason(DatabaseEngine.Postgres, _ => true));
    }

    [Fact]
    public void Clients_authenticate_over_the_socket_without_passwords()
    {
        var (mysql, mysqlArgs) = DatabaseManager.ClientCommand(DatabaseManager.Spec(DatabaseEngine.Mysql), "turbo");
        var (postgres, postgresArgs) = DatabaseManager.ClientCommand(DatabaseManager.Spec(DatabaseEngine.Postgres), null);

        Assert.Equal("mysql", mysql);
        Assert.Contains("--protocol=socket", mysqlArgs);
        Assert.Equal("turbo", mysqlArgs[^1]);
        Assert.DoesNotContain(mysqlArgs, a => a.StartsWith("--password", StringComparison.Ordinal));
        Assert.Equal("runuser", postgres);
        Assert.Equal(["-u", "postgres", "--", "psql"], postgresArgs.Take(4));
        Assert.Contains("ON_ERROR_STOP=1", postgresArgs);
    }
}

public sealed class DatabaseManagerTests : IDisposable
{
    private readonly FakeHostRoot _host = new();
    private readonly FakeCommandRunner _commands = new();
    private readonly DatabaseManager _databases;

    public DatabaseManagerTests() => _databases = new DatabaseManager(_commands,
        Options.Create(new AgentOptions { HostRoot = _host.Path }), TimeProvider.System, NullLogger<DatabaseManager>.Instance);

    [Fact]
    public async Task Passwords_travel_on_stdin_not_in_arguments()
    {
        await _databases.CreateUserAsync(new DatabaseUserRequest { Engine = DatabaseEngine.Mysql, Name = "turbo", Password = "Str0ng-Passw0rd!" }, CancellationToken.None);

        Assert.DoesNotContain(_commands.Calls, c => c.Contains("Str0ng-Passw0rd!"));
        Assert.Contains("IDENTIFIED BY 'Str0ng-Passw0rd!'", Assert.Single(_commands.Stdins));
    }

    [Fact]
    public async Task Sql_errors_become_readable_failures()
    {
        _commands.Handler = (_, _) => new CommandResult(1, "", "ERROR 1007 (HY000) at line 1: Can't create database 'turbo'; database exists");

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            _databases.CreateDatabaseAsync(new DatabaseRequest { Engine = DatabaseEngine.Mysql, Name = "turbo" }, CancellationToken.None));

        Assert.Equal("Can't create database 'turbo'; database exists", error.Status.Detail);
    }

    [Fact]
    public async Task Databases_are_listed_from_batch_output()
    {
        _commands.Handler = (_, _) => new CommandResult(0, "turbo\t1048576\t42\nshop\t0\t0\n", "");

        var list = await _databases.ListDatabasesAsync(DatabaseEngine.Mysql, CancellationToken.None);

        Assert.Equal(["turbo", "shop"], list.Databases.Select(d => d.Name));
        Assert.Equal(1_048_576, list.Databases[0].SizeBytes);
        Assert.Equal(42, list.Databases[0].Tables);
    }

    [Fact]
    public async Task Backup_is_gzipped_private_and_listed_then_restored_through_stdin()
    {
        _commands.StreamOutput = (file, _) => file == "mysqldump" ? "CREATE TABLE players (id int);\n" : "";

        var backup = await _databases.BackupAsync(new DatabaseRequest { Engine = DatabaseEngine.Mysql, Name = "turbo" }, CancellationToken.None);

        Assert.StartsWith("mysql/turbo/turbo-", backup.Id);
        Assert.EndsWith(".sql.gz", backup.Id);
        var path = Path.Combine(_host.Path, "var/lib/turbo-manager-backups/databases", backup.Id);
        await using (var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            Assert.Equal("CREATE TABLE players (id int);\n", await reader.ReadToEndAsync());
        }

        Assert.Equal(backup.Id, Assert.Single(_databases.ListBackups(DatabaseEngine.Mysql).Backups).Id);
        Assert.Empty(_databases.ListBackups(DatabaseEngine.Postgres).Backups);

        var output = new List<string>();
        await _databases.RestoreAsync(new RestoreRequest { BackupId = backup.Id, Engine = DatabaseEngine.Mysql, Database = "turbo" },
            line => { output.Add(line); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal("CREATE TABLE players (id int);\n", _commands.LastStdin);
        Assert.Contains("DROP DATABASE `turbo`;", _commands.Stdins);
        Assert.Contains(_commands.Stdins, s => s.StartsWith("CREATE DATABASE `turbo`", StringComparison.Ordinal));
        Assert.Equal("Restore finished.", output[^1]);
    }

    [Fact]
    public async Task Failed_dump_leaves_no_file()
    {
        _commands.Handler = (file, _) => file == "mysqldump" ? new CommandResult(2, "", "mysqldump: Got error: 1049: Unknown database") : new CommandResult(0, "", "");

        await Assert.ThrowsAsync<RpcException>(() => _databases.BackupAsync(new DatabaseRequest { Engine = DatabaseEngine.Mysql, Name = "missing" }, CancellationToken.None));

        Assert.Empty(_databases.ListBackups(DatabaseEngine.Unspecified).Backups);
    }

    [Theory]
    [InlineData("../../etc/shadow")]
    [InlineData("mysql/turbo/../../../etc/passwd.sql")]
    [InlineData("/etc/passwd")]
    [InlineData("mysql/turbo/notes.txt")]
    public void Backup_ids_cannot_escape_the_backup_root(string id) =>
        Assert.Throws<RpcException>(() => _databases.DeleteBackup(id));

    [Fact]
    public async Task Postgres_readonly_is_refused_clearly()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => _databases.SetGrantAsync(
            new GrantRequest { Engine = DatabaseEngine.Postgres, User = "turbo", Database = "turbo", Level = "readonly" }, CancellationToken.None));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    public void Dispose() => _host.Dispose();
}
