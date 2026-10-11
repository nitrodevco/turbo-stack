using System.Text.RegularExpressions;
using TurboManager.Agent.Validation;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Databases;

/// <summary>
/// Builds the SQL the agent runs. Names and passwords are validated against strict patterns first,
/// so they can be embedded in SQL without escaping beyond the identifier/string quotes.
/// </summary>
public static partial class DatabaseSql
{
    public static readonly HashSet<string> SystemDatabases =
        ["mysql", "information_schema", "performance_schema", "sys", "postgres", "template0", "template1"];

    public static readonly HashSet<string> SystemUsers =
        ["root", "mysql.sys", "mysql.session", "mysql.infoschema", "mariadb.sys", "debian-sys-maint", "postgres"];

    public static bool IsMySqlFamily(DatabaseEngine engine) => engine is DatabaseEngine.Mysql or DatabaseEngine.Mariadb;

    /// <summary>Database and user names: lowercase letters, digits and underscores, starting with a letter.</summary>
    public static string Name(string name, string what) =>
        NamePattern().IsMatch(name) && !SystemDatabases.Contains(name) && !SystemUsers.Contains(name)
            ? name
            : throw Validate.Invalid($"Invalid {what} name \"{name}\": use 1-32 lowercase letters, digits or underscores, starting with a letter.");

    /// <summary>Passwords exclude quotes, backslashes and shell/SQL metacharacters so they never need escaping.</summary>
    public static string Password(string password) =>
        PasswordPattern().IsMatch(password)
            ? password
            : throw Validate.Invalid("Passwords must be 12-128 characters of letters, digits and !#%+,.:=?@^_~-");

    public static string ListDatabases(DatabaseEngine engine) => IsMySqlFamily(engine)
        ? """
          SELECT s.SCHEMA_NAME, COALESCE(SUM(t.DATA_LENGTH + t.INDEX_LENGTH), 0), COUNT(t.TABLE_NAME)
          FROM information_schema.SCHEMATA s
          LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME
          WHERE s.SCHEMA_NAME NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
          GROUP BY s.SCHEMA_NAME ORDER BY s.SCHEMA_NAME;
          """
        : "SELECT datname, pg_database_size(datname), -1 FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres' ORDER BY datname;";

    public static string CreateDatabase(DatabaseEngine engine, string name) => IsMySqlFamily(engine)
        ? $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
        // Other users can't connect unless granted (PostgreSQL lets everyone connect by default).
        : $"CREATE DATABASE \"{name}\" ENCODING 'UTF8' TEMPLATE template0;\nREVOKE CONNECT ON DATABASE \"{name}\" FROM PUBLIC;";

    public static string DropDatabase(DatabaseEngine engine, string name) => IsMySqlFamily(engine)
        ? $"DROP DATABASE `{name}`;"
        : $"DROP DATABASE \"{name}\" WITH (FORCE);";

    public static string ListUsers(DatabaseEngine engine) => IsMySqlFamily(engine)
        ? "SELECT User FROM mysql.user WHERE Host = 'localhost' AND User NOT IN ('root', 'mysql.sys', 'mysql.session', 'mysql.infoschema', 'mariadb.sys', 'debian-sys-maint') AND User <> '' ORDER BY User;"
        : "SELECT rolname FROM pg_roles WHERE rolcanlogin AND NOT rolsuper AND rolname NOT LIKE 'pg\\_%' ORDER BY rolname;";

    /// <summary>MySQL: (grantee, schema, privilege) rows. PostgreSQL: (database, owner) rows (ownership = full access).</summary>
    public static string ListGrants(DatabaseEngine engine) => IsMySqlFamily(engine)
        ? "SELECT GRANTEE, TABLE_SCHEMA, PRIVILEGE_TYPE FROM information_schema.SCHEMA_PRIVILEGES;"
        : "SELECT d.datname, r.rolname FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE NOT d.datistemplate AND d.datname <> 'postgres';";

    public static string CreateUser(DatabaseEngine engine, string name, string password) => IsMySqlFamily(engine)
        ? $"CREATE USER '{name}'@'localhost' IDENTIFIED BY '{password}';"
        : $"CREATE ROLE \"{name}\" LOGIN PASSWORD '{password}';";

    public static string SetPassword(DatabaseEngine engine, string name, string password) => IsMySqlFamily(engine)
        ? $"ALTER USER '{name}'@'localhost' IDENTIFIED BY '{password}';"
        : $"ALTER ROLE \"{name}\" PASSWORD '{password}';";

    public static string DropMySqlUser(string name) => $"DROP USER '{name}'@'localhost';";

    /// <summary>
    /// MySQL grant statements. "_" is a wildcard in grant database names, so it is escaped to grant
    /// exactly this database. Revoking first makes the call idempotent (errors from it are ignored).
    /// </summary>
    public static (string Revoke, string? Grant) MySqlGrant(string user, string database, string level)
    {
        var target = $"`{database.Replace("_", "\\_", StringComparison.Ordinal)}`.*";
        var revoke = $"REVOKE ALL PRIVILEGES ON {target} FROM '{user}'@'localhost';";

        return level switch
        {
            "all" => (revoke, $"GRANT ALL PRIVILEGES ON {target} TO '{user}'@'localhost';"),
            "readonly" => (revoke, $"GRANT SELECT, SHOW VIEW ON {target} TO '{user}'@'localhost';"),
            "none" => (revoke, null),
            _ => throw Validate.Invalid("Access level must be all, readonly or none."),
        };
    }

    /// <summary>Turns SCHEMA_PRIVILEGES rows into one grant per (user, database): anything beyond SELECT/SHOW VIEW is "all".</summary>
    public static Dictionary<string, List<DatabaseGrant>> ParseMySqlGrants(IEnumerable<string[]> rows)
    {
        var grants = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);

        foreach (var row in rows.Where(r => r.Length >= 3))
        {
            var match = GranteePattern().Match(row[0]);

            if (!match.Success || match.Groups["host"].Value != "localhost")
            {
                continue;
            }

            var user = match.Groups["user"].Value;
            var database = row[1].Replace("\\_", "_", StringComparison.Ordinal).Replace("\\%", "%", StringComparison.Ordinal);

            if (!grants.TryGetValue(user, out var databases))
            {
                grants[user] = databases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            }

            if (!databases.TryGetValue(database, out var privileges))
            {
                databases[database] = privileges = [];
            }

            privileges.Add(row[2]);
        }

        return grants.ToDictionary(
            u => u.Key,
            u => u.Value.Select(d => new DatabaseGrant
            {
                Database = d.Key,
                Level = d.Value.All(p => p is "SELECT" or "SHOW VIEW") ? "readonly" : "all",
            }).OrderBy(g => g.Database, StringComparer.Ordinal).ToList());
    }

    /// <summary>Splits client batch output (tab-separated, one row per line).</summary>
    public static List<string[]> Rows(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\t')).ToList();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9!#%+,.:=?@^_~-]{12,128}$")]
    private static partial Regex PasswordPattern();

    [GeneratedRegex("^'(?<user>[^']*)'@'(?<host>[^']*)'$")]
    private static partial Regex GranteePattern();
}
