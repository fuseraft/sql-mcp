using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlMcp.Models;
using SqlMcp.Services;
using System.Data.Common;
using System.Text.Json;

namespace SqlMcp.Tests;

/// <summary>
/// Runs against real databases. Each provider is skipped unless its connection string
/// is set, e.g. SQLMCP_TEST_POSTGRES="Host=localhost;Username=postgres;Password=pw".
/// The login must be an administrator: the tests create their own fixtures and a
/// read-only login (sqlmcp_ro) to check the login audit against.
/// </summary>
public class QueryServiceIntegrationTests
{
    private const string Table = "sqlmcp_fixture";

    public static TheoryData<DbProvider> Providers() => [.. Enum.GetValues<DbProvider>()];

    private static string? ConnectionString(DbProvider provider) =>
        Environment.GetEnvironmentVariable($"SQLMCP_TEST_{provider.ToString().ToUpperInvariant()}");

    private static DbConfig RequireDb(DbProvider provider)
    {
        var cs = ConnectionString(provider);
        Skip.If(cs is null, $"Set SQLMCP_TEST_{provider.ToString().ToUpperInvariant()} to run.");
        return new DbConfig("test", provider, cs!);
    }

    // The admin logins the tests run as can write, so the audit is opted out of unless
    // a test is about the audit.
    private static QueryService Service(DbConfig db, int maxRows = 1000, bool allowWritableLogin = true,
        Dictionary<string, string?>? settings = null, ILogger<QueryService>? logger = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["App:MaxRows"] = maxRows.ToString(),
            ["Databases:0:Name"] = db.Name,
            ["Databases:0:Provider"] = db.Provider.ToString(),
            ["Databases:0:ConnectionString"] = db.ConnectionString,
            ["Databases:0:AllowWritableLogin"] = allowWritableLogin.ToString(),
        };
        foreach (var (key, value) in settings ?? [])
            config[key] = value;
        return new(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), logger);
    }

    private const string ReadOnlyUser = "sqlmcp_ro";
    private const string ReadOnlyPassword = "Ro_passw0rd1";

    /// <summary>
    /// Creates (once) a login that can only SELECT the fixture table, and returns its
    /// config plus the schema-qualified table name it should query.
    /// </summary>
    private static async Task<(DbConfig Db, string Table)> ReadOnlyLoginAsync(DbConfig admin)
    {
        await SeedAsync(admin);
        await using var conn = await OpenAsync(admin);

        string table;
        switch (admin.Provider)
        {
            case DbProvider.Postgres:
                await TryExecAsync(conn, $"CREATE ROLE {ReadOnlyUser} LOGIN PASSWORD '{ReadOnlyPassword}'");
                table = $"public.{Table}";
                break;
            case DbProvider.MySql:
                await ExecAsync(conn, $"CREATE USER IF NOT EXISTS '{ReadOnlyUser}'@'%' IDENTIFIED BY '{ReadOnlyPassword}'");
                table = $"{conn.Database}.{Table}";
                break;
            case DbProvider.SqlServer:
                await TryExecAsync(conn, $"CREATE LOGIN {ReadOnlyUser} WITH PASSWORD = '{ReadOnlyPassword}'");
                await TryExecAsync(conn, $"CREATE USER {ReadOnlyUser} FOR LOGIN {ReadOnlyUser}");
                table = $"dbo.{Table}";
                break;
            case DbProvider.Oracle:
                await TryExecAsync(conn, $"CREATE USER {ReadOnlyUser} IDENTIFIED BY \"{ReadOnlyPassword}\"");
                await ExecAsync(conn, $"GRANT CREATE SESSION TO {ReadOnlyUser}");
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT USER FROM dual";
                    table = $"{await cmd.ExecuteScalarAsync()}.{Table}";
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(admin));
        }
        var database = conn.Database;
        await ExecAsync(conn, $"GRANT SELECT ON {table} TO {(admin.Provider == DbProvider.MySql ? $"'{ReadOnlyUser}'@'%'" : ReadOnlyUser)}");

        var csb = admin.GetFactory().CreateConnectionStringBuilder()!;
        csb.ConnectionString = admin.ConnectionString;
        csb["User Id"] = ReadOnlyUser;
        csb["Password"] = ReadOnlyPassword;
        // Postgres otherwise connects to a database named after the user.
        if (admin.Provider == DbProvider.Postgres)
            csb["Database"] = database;
        return (admin with { ConnectionString = csb.ConnectionString }, table);
    }

    private static async Task<DbConnection> OpenAsync(DbConfig db)
    {
        var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = db.ConnectionString;
        await conn.OpenAsync();
        return conn;
    }

    private static string From(DbProvider provider) => provider == DbProvider.Oracle ? " FROM dual" : "";

    /// <summary>
    /// Resets the fixture table to ids 1..3 using a direct, writable connection. The table
    /// is created once and then reseeded with DML only. On a fresh Oracle database the
    /// first queries hit ORA-01466 (table definition changed moments earlier), which
    /// exercises QueryService's retry.
    /// </summary>
    private static async Task SeedAsync(DbConfig db, params string[] extra)
    {
        await using var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = db.ConnectionString;
        await conn.OpenAsync();

        await TryExecAsync(conn, $"CREATE TABLE {Table} (id INT, name VARCHAR(20))");
        await ExecAsync(conn, $"DELETE FROM {Table}");
        foreach (var id in new[] { 1, 2, 3 })
            await ExecAsync(conn, $"INSERT INTO {Table} (id, name) VALUES ({id}, 'row {id}')");
        foreach (var sql in extra)
            await ExecAsync(conn, sql);
    }

    private static async Task<long> CountAsync(DbConfig db)
    {
        await using var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = db.ConnectionString;
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TryExecAsync(DbConnection conn, string sql)
    {
        try
        {
            await ExecAsync(conn, sql);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Returns_columns_and_rows(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);

        var result = await Service(db).ExecuteAsync(new("test", $"SELECT id, name FROM {Table} ORDER BY id"));

        Assert.Null(result.Error);
        Assert.Equal(["id", "name"], result.Value!.Columns.Select(c => c.ToLowerInvariant()));
        Assert.Equal(3, result.Value.Rows.Length);
        Assert.False(result.Value.Truncated);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Accepts_a_trailing_semicolon(DbProvider provider)
    {
        var db = RequireDb(provider);

        var result = await Service(db).ExecuteAsync(new("test", $"SELECT 1 AS one{From(provider)};"));

        Assert.Null(result.Error);
        Assert.Single(result.Value!.Rows);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Truncates_only_when_more_rows_exist(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);

        var over = await Service(db, maxRows: 2).ExecuteAsync(new("test", $"SELECT id FROM {Table}"));
        Assert.Null(over.Error);
        Assert.Equal(2, over.Value!.Rows.Length);
        Assert.True(over.Value.Truncated);

        var exact = await Service(db, maxRows: 3).ExecuteAsync(new("test", $"SELECT id FROM {Table}"));
        Assert.Equal(3, exact.Value!.Rows.Length);
        Assert.False(exact.Value.Truncated);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Rejects_writes_before_reaching_the_database(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);

        var result = await Service(db).ExecuteAsync(new("test", $"DELETE FROM {Table}"));

        Assert.NotNull(result.Error);
        Assert.Equal(3, await CountAsync(db));
    }

    // SQL Server runs "SELECT ... DROP ..." as a two-statement batch.
    [SkippableFact]
    public async Task SqlServer_rejects_a_batch_without_separators()
    {
        var db = RequireDb(DbProvider.SqlServer);
        await SeedAsync(db);

        var result = await Service(db).ExecuteAsync(new("test", $"SELECT 1 AS one DELETE FROM {Table}"));

        Assert.NotNull(result.Error);
        Assert.Equal(3, await CountAsync(db));
    }

    // A function that writes is invisible to the lexer: "SELECT f()" looks like a read.
    // The read-only transaction is what stops it.
    [SkippableTheory]
    [InlineData(DbProvider.Postgres,
        $"CREATE OR REPLACE FUNCTION sqlmcp_write() RETURNS int LANGUAGE sql AS 'INSERT INTO {Table} VALUES (99, ''x'') RETURNING id'",
        "read-only transaction")]
    [InlineData(DbProvider.MySql,
        $"CREATE FUNCTION sqlmcp_write() RETURNS INT MODIFIES SQL DATA BEGIN INSERT INTO {Table} VALUES (99, 'x'); RETURN 1; END",
        "READ ONLY transaction")]
    public async Task Read_only_transaction_blocks_writes_hidden_in_functions(DbProvider provider, string createFunction, string expectedError)
    {
        var db = RequireDb(provider);
        await using (var conn = db.GetFactory().CreateConnection()!)
        {
            conn.ConnectionString = db.ConnectionString;
            await conn.OpenAsync();
            await TryExecAsync(conn, "DROP FUNCTION sqlmcp_write");
        }
        await SeedAsync(db, createFunction);

        var result = await Service(db).ExecuteAsync(new("test", "SELECT sqlmcp_write()"));

        Assert.NotNull(result.Error);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, await CountAsync(db));
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Audit_flags_an_admin_login(DbProvider provider)
    {
        var admin = RequireDb(provider);
        await using var conn = await OpenAsync(admin);

        var findings = await LoginAudit.FindWriteAccessAsync(conn, provider, CancellationToken.None);

        Assert.NotEmpty(findings);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Audit_passes_a_read_only_login(DbProvider provider)
    {
        var (readOnly, _) = await ReadOnlyLoginAsync(RequireDb(provider));
        await using var conn = await OpenAsync(readOnly);

        var findings = await LoginAudit.FindWriteAccessAsync(conn, provider, CancellationToken.None);

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Refuses_queries_when_the_login_can_write(DbProvider provider)
    {
        var admin = RequireDb(provider);

        var result = await Service(admin, allowWritableLogin: false).ExecuteAsync(new("test", $"SELECT 1 AS one{From(provider)}"));

        Assert.NotNull(result.Error);
        Assert.Contains("is disabled because its login can modify data", result.Error);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Serves_queries_for_a_read_only_login(DbProvider provider)
    {
        var (readOnly, table) = await ReadOnlyLoginAsync(RequireDb(provider));

        var result = await Service(readOnly, allowWritableLogin: false).ExecuteAsync(new("test", $"SELECT id FROM {table}"));

        Assert.Null(result.Error);
        Assert.Equal(3, result.Value!.Rows.Length);
    }

    // One grant too many on an otherwise read-only login. Each row is a way the
    // original gaps could be reached: a write privilege, a function that commits on
    // its own, a sequence, a way out of the database.
    [SkippableTheory]
    [InlineData(DbProvider.Postgres, "can modify tables",
        new[] { "GRANT INSERT ON public.sqlmcp_fixture TO sqlmcp_ro" },
        new[] { "REVOKE INSERT ON public.sqlmcp_fixture FROM sqlmcp_ro" })]
    [InlineData(DbProvider.Postgres, "pg_signal_backend",
        new[] { "GRANT pg_signal_backend TO sqlmcp_ro" },
        new[] { "REVOKE pg_signal_backend FROM sqlmcp_ro" })]
    [InlineData(DbProvider.Postgres, "dblink",
        new[] { "CREATE EXTENSION IF NOT EXISTS dblink" },
        new[] { "DROP EXTENSION IF EXISTS dblink" })]
    [InlineData(DbProvider.MySql, "INSERT",
        new[] { "GRANT INSERT ON sqlmcp_fixture TO 'sqlmcp_ro'@'%'" },
        new[] { "REVOKE INSERT ON sqlmcp_fixture FROM 'sqlmcp_ro'@'%'" })]
    [InlineData(DbProvider.MySql, "FILE",
        new[] { "GRANT FILE ON *.* TO 'sqlmcp_ro'@'%'" },
        new[] { "REVOKE FILE ON *.* FROM 'sqlmcp_ro'@'%'" })]
    [InlineData(DbProvider.MySql, "DELETE",
        new[] { "CREATE ROLE IF NOT EXISTS sqlmcp_writer", "GRANT DELETE ON sqlmcp_fixture TO sqlmcp_writer",
                "GRANT sqlmcp_writer TO 'sqlmcp_ro'@'%'", "SET DEFAULT ROLE sqlmcp_writer TO 'sqlmcp_ro'@'%'" },
        new[] { "DROP ROLE IF EXISTS sqlmcp_writer" })]
    [InlineData(DbProvider.SqlServer, "can modify tables",
        new[] { "ALTER ROLE db_datawriter ADD MEMBER sqlmcp_ro" },
        new[] { "ALTER ROLE db_datawriter DROP MEMBER sqlmcp_ro" })]
    [InlineData(DbProvider.SqlServer, "sqlmcp_seq",
        new[] { "CREATE SEQUENCE dbo.sqlmcp_seq", "GRANT UPDATE ON dbo.sqlmcp_seq TO sqlmcp_ro" },
        new[] { "DROP SEQUENCE dbo.sqlmcp_seq" })]
    [InlineData(DbProvider.Oracle, "CREATE PROCEDURE",
        new[] { "GRANT CREATE PROCEDURE TO sqlmcp_ro" },
        new[] { "REVOKE CREATE PROCEDURE FROM sqlmcp_ro" })]
    [InlineData(DbProvider.Oracle, "EXECUTE ON",
        new[] { """
                CREATE OR REPLACE FUNCTION sqlmcp_autonomous RETURN NUMBER IS
                  PRAGMA AUTONOMOUS_TRANSACTION;
                BEGIN
                  DELETE FROM sqlmcp_fixture;
                  COMMIT;
                  RETURN 1;
                END;
                """,
                "GRANT EXECUTE ON sqlmcp_autonomous TO sqlmcp_ro" },
        new[] { "DROP FUNCTION sqlmcp_autonomous" })]
    public async Task Audit_flags_a_read_only_login_with_one_extra_grant(
        DbProvider provider, string expected, string[] grant, string[] revoke)
    {
        var (readOnly, _) = await ReadOnlyLoginAsync(RequireDb(provider));
        await using var admin = await OpenAsync(RequireDb(provider));
        try
        {
            foreach (var sql in grant)
                await ExecAsync(admin, sql);

            await using var conn = await OpenAsync(readOnly);
            var findings = await LoginAudit.FindWriteAccessAsync(conn, provider, CancellationToken.None);

            Assert.Contains(findings, f => f.Contains(expected, StringComparison.OrdinalIgnoreCase));

            var result = await Service(readOnly, allowWritableLogin: false).ExecuteAsync(new("test", $"SELECT 1 AS one{From(provider)}"));
            Assert.Contains("is disabled", result.Error);
        }
        finally
        {
            foreach (var sql in revoke)
                await TryExecAsync(admin, sql);
        }
    }

    // The read-only setups the README recommends must pass.
    [SkippableTheory]
    [InlineData(DbProvider.Postgres, new[] { "GRANT pg_read_all_data TO sqlmcp_ro" }, new[] { "REVOKE pg_read_all_data FROM sqlmcp_ro" })]
    [InlineData(DbProvider.SqlServer, new[] { "ALTER ROLE db_datareader ADD MEMBER sqlmcp_ro" }, new[] { "ALTER ROLE db_datareader DROP MEMBER sqlmcp_ro" })]
    [InlineData(DbProvider.MySql, new[] { "GRANT SELECT ON t.* TO 'sqlmcp_ro'@'%'" }, new[] { "REVOKE SELECT ON t.* FROM 'sqlmcp_ro'@'%'" })]
    public async Task Audit_passes_the_documented_read_only_setups(DbProvider provider, string[] grant, string[] revoke)
    {
        var (readOnly, _) = await ReadOnlyLoginAsync(RequireDb(provider));
        await using var admin = await OpenAsync(RequireDb(provider));
        try
        {
            foreach (var sql in grant)
                await ExecAsync(admin, sql);

            await using var conn = await OpenAsync(readOnly);
            var findings = await LoginAudit.FindWriteAccessAsync(conn, provider, CancellationToken.None);

            Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
        }
        finally
        {
            foreach (var sql in revoke)
                await TryExecAsync(admin, sql);
        }
    }

    private const string KeyedTable = "sqlmcp_keyed";
    private const string View = "sqlmcp_fixture_v";

    /// <summary>
    /// Creates (once) a table with a primary key and a view for the schema tools, and
    /// returns the schema they are in.
    /// </summary>
    private static async Task<string> SeedSchemaAsync(DbConfig db)
    {
        await SeedAsync(db);
        await using var conn = await OpenAsync(db);
        await TryExecAsync(conn, $"CREATE TABLE {KeyedTable} (id INT NOT NULL PRIMARY KEY, label VARCHAR(30))");
        await TryExecAsync(conn, $"CREATE VIEW {View} AS SELECT id FROM {Table}");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = db.Provider switch
        {
            DbProvider.Postgres => "SELECT current_schema()",
            DbProvider.MySql => "SELECT DATABASE()",
            DbProvider.SqlServer => "SELECT SCHEMA_NAME()",
            DbProvider.Oracle => "SELECT USER FROM dual",
            _ => throw new ArgumentOutOfRangeException(nameof(db))
        };
        return Convert.ToString(await cmd.ExecuteScalarAsync())!;
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Lists_tables_and_views(DbProvider provider)
    {
        var db = RequireDb(provider);
        var schema = await SeedSchemaAsync(db);

        var result = await Service(db).ListTablesAsync("test", schema);

        Assert.Null(result.Error);
        var tables = result.Value!.Tables;
        Assert.All(tables, t => Assert.Equal(schema, t.Schema));
        Assert.Contains(tables, t => t.Name.Equals(KeyedTable, StringComparison.OrdinalIgnoreCase) && t.Type == "table");
        Assert.Contains(tables, t => t.Name.Equals(View, StringComparison.OrdinalIgnoreCase) && t.Type == "view");
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Describes_columns_and_primary_key(DbProvider provider)
    {
        var db = RequireDb(provider);
        var schema = await SeedSchemaAsync(db);

        var result = await Service(db).DescribeTableAsync("test", KeyedTable, schema);

        Assert.Null(result.Error);
        var table = result.Value!;
        Assert.Equal(schema, table.Schema);
        Assert.Equal("table", table.Type);
        Assert.Equal(["id", "label"], table.Columns.Select(c => c.Name.ToLowerInvariant()));

        var id = table.Columns[0];
        Assert.False(id.Nullable);
        Assert.True(id.PrimaryKey);
        Assert.Contains("int", id.DataType, StringComparison.OrdinalIgnoreCase);

        var label = table.Columns[1];
        Assert.True(label.Nullable);
        Assert.False(label.PrimaryKey);
        Assert.Contains("30", label.DataType);
    }

    // Oracle's test login is SYSTEM, an Oracle-maintained schema that's hidden unless named.
    [SkippableTheory]
    [InlineData(DbProvider.Postgres)]
    [InlineData(DbProvider.MySql)]
    [InlineData(DbProvider.SqlServer)]
    public async Task Finds_a_table_without_a_schema_in_any_case(DbProvider provider)
    {
        var db = RequireDb(provider);
        var schema = await SeedSchemaAsync(db);
        var svc = Service(db);

        foreach (var name in new[] { KeyedTable, KeyedTable.ToUpperInvariant(), $"{schema}.{KeyedTable}" })
        {
            var result = await svc.DescribeTableAsync("test", name, null);
            Assert.True(result.Error is null, $"{name}: {result.Error}");
            Assert.Equal(schema, result.Value!.Schema);
        }

        var all = await svc.ListTablesAsync("test", null);
        Assert.Contains(all.Value!.Tables, t => t.Schema == schema && t.Name == KeyedTable);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Hides_system_schemas_unless_named(DbProvider provider)
    {
        var db = RequireDb(provider);
        var system = provider switch
        {
            DbProvider.Postgres => "pg_catalog",
            DbProvider.MySql => "mysql",
            DbProvider.SqlServer => "sys",
            _ => "SYS"
        };

        var all = await Service(db, maxRows: 100_000).ListTablesAsync("test", null);
        Assert.Null(all.Error);
        Assert.DoesNotContain(all.Value!.Tables, t => t.Schema.Equals(system, StringComparison.OrdinalIgnoreCase));

        // SQL Server's sys catalog views aren't in sys.objects, so there's nothing to name.
        if (provider != DbProvider.SqlServer)
        {
            var named = await Service(db).ListTablesAsync("test", system);
            Assert.NotEmpty(named.Value!.Tables);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Truncates_the_table_list(DbProvider provider)
    {
        var db = RequireDb(provider);
        var schema = await SeedSchemaAsync(db);

        var result = await Service(db, maxRows: 1).ListTablesAsync("test", schema);

        Assert.Single(result.Value!.Tables);
        Assert.True(result.Value.Truncated);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Reports_a_missing_table(DbProvider provider)
    {
        var db = RequireDb(provider);

        var result = await Service(db).DescribeTableAsync("test", "sqlmcp_no_such_table", null);

        Assert.NotNull(result.Error);
        Assert.Contains("list_tables", result.Error);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Read_only_login_sees_only_what_it_can_read(DbProvider provider)
    {
        var admin = RequireDb(provider);
        var schema = await SeedSchemaAsync(admin);
        var (readOnly, _) = await ReadOnlyLoginAsync(admin);
        var svc = Service(readOnly, allowWritableLogin: false);

        var list = await svc.ListTablesAsync("test", schema);
        Assert.True(list.Error is null, list.Error);
        var names = list.Value!.Tables.Select(t => t.Name.ToLowerInvariant()).ToList();
        Assert.Contains(Table, names);
        Assert.DoesNotContain(KeyedTable, names);

        var described = await svc.DescribeTableAsync("test", Table, schema);
        Assert.True(described.Error is null, described.Error);
        Assert.Equal(2, described.Value!.Columns.Length);

        var hidden = await svc.DescribeTableAsync("test", KeyedTable, schema);
        Assert.Contains("list_tables", hidden.Error);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Schema_tools_refuse_a_login_that_can_write(DbProvider provider)
    {
        var admin = RequireDb(provider);
        var svc = Service(admin, allowWritableLogin: false);

        Assert.Contains("is disabled", (await svc.ListTablesAsync("test", null)).Error);
        Assert.Contains("is disabled", (await svc.DescribeTableAsync("test", Table, null)).Error);
    }

    private static string P(DbProvider provider, string name) => (provider == DbProvider.Oracle ? ":" : "@") + name;

    private static Dictionary<string, JsonElement> Params(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static QueryRequest Query(string sql, string parameters) => new("test", sql, Params(parameters));

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Binds_a_value_that_contains_quotes(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db, $"INSERT INTO {Table} (id, name) VALUES (4, 'O''Brien')");

        var result = await Service(db).ExecuteAsync(Query(
            $"SELECT id FROM {Table} WHERE name = {P(provider, "name")}", """{"name": "O'Brien"}"""));

        Assert.True(result.Error is null, result.Error);
        Assert.Equal(4, Convert.ToInt32(Assert.Single(result.Value!.Rows).Values.Single()));
    }

    // Written into the SQL, each of these would change the query. Bound, they are just
    // strings that no row's name equals.
    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Bound_values_cannot_change_the_query(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);
        var svc = Service(db);

        foreach (var attack in new[] { "x' OR '1'='1", "'; DELETE FROM sqlmcp_fixture; --", "x' UNION SELECT 99 FROM sqlmcp_fixture --", "\\'; DROP TABLE sqlmcp_fixture; --" })
        {
            var result = await svc.ExecuteAsync(new("test", $"SELECT id FROM {Table} WHERE name = {P(provider, "name")}",
                new() { ["name"] = JsonSerializer.SerializeToElement(attack) }));

            Assert.True(result.Error is null, $"{attack}: {result.Error}");
            Assert.Empty(result.Value!.Rows);
        }
        Assert.Equal(3, await CountAsync(db));
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Binds_numbers_and_null(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);
        var svc = Service(db);
        var sql = $"SELECT id FROM {Table} WHERE id >= {P(provider, "min")} ORDER BY id";

        Assert.Equal(2, (await svc.ExecuteAsync(Query(sql, """{"min": 2}"""))).Value!.Rows.Length);
        Assert.Single((await svc.ExecuteAsync(Query(sql, """{"min": 2.5}"""))).Value!.Rows);

        var none = await svc.ExecuteAsync(Query($"SELECT id FROM {Table} WHERE name = {P(provider, "name")}", """{"name": null}"""));
        Assert.True(none.Error is null, none.Error);
        Assert.Empty(none.Value!.Rows);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Binds_several_parameters_including_one_used_twice(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);

        var result = await Service(db).ExecuteAsync(Query(
            $"SELECT id FROM {Table} WHERE (id = {P(provider, "a")} OR id = {P(provider, "b")}) AND id <> {P(provider, "a")} + 10 ORDER BY id",
            """{"a": 1, "b": 3}"""));

        Assert.True(result.Error is null, result.Error);
        Assert.Equal([1, 3], result.Value!.Rows.Select(r => Convert.ToInt32(r.Values.Single())));
    }

    // Postgres won't compare text with a date; strings are sent untyped so it infers the type.
    [SkippableFact]
    public async Task Postgres_infers_the_type_of_a_string_value()
    {
        var db = RequireDb(DbProvider.Postgres);

        var result = await Service(db).ExecuteAsync(Query(
            "SELECT 1 AS one WHERE DATE '2024-01-02' > @d AND 5 > @n", """{"d": "2024-01-01", "n": "4"}"""));

        Assert.True(result.Error is null, result.Error);
        Assert.Single(result.Value!.Rows);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Reports_a_missing_parameter(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);

        var result = await Service(db).ExecuteAsync(Query(
            $"SELECT id FROM {Table} WHERE id = {P(provider, "missing")}", """{"other": 1}"""));

        Assert.NotNull(result.Error);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Rejects_bad_parameters_before_connecting(DbProvider provider)
    {
        var db = RequireDb(provider);

        var result = await Service(db).ExecuteAsync(Query($"SELECT 1 AS one{From(provider)}", """{"a b": 1}"""));

        Assert.Contains("invalid", result.Error);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Keeps_every_value_when_column_names_repeat(DbProvider provider)
    {
        var db = RequireDb(provider);

        var result = await Service(db).ExecuteAsync(new("test", $"SELECT 1 AS a, 2 AS a, 3 AS b{From(provider)}"));

        Assert.True(result.Error is null, result.Error);
        Assert.Equal(["a", "a_2", "b"], result.Value!.Columns.Select(c => c.ToLowerInvariant()));
        Assert.Equal([1, 2, 3], result.Value.Rows.Single().Values.Select(Convert.ToInt32));
    }

    [SkippableTheory]
    [InlineData(DbProvider.Postgres, "SELECT 1, 2")]
    [InlineData(DbProvider.SqlServer, "SELECT 1, 2")]
    public async Task Names_unnamed_columns(DbProvider provider, string sql)
    {
        var db = RequireDb(provider);

        var result = await Service(db).ExecuteAsync(new("test", sql));

        Assert.Equal(["column1", "column2"], result.Value!.Columns);
    }

    [SkippableTheory]
    [InlineData(DbProvider.Postgres, "SELECT repeat('x', 500) AS v")]
    [InlineData(DbProvider.MySql, "SELECT REPEAT('x', 500) AS v")]
    [InlineData(DbProvider.SqlServer, "SELECT REPLICATE('x', 500) AS v")]
    [InlineData(DbProvider.Oracle, "SELECT RPAD('x', 500, 'x') AS v FROM dual")]
    public async Task Truncates_long_values(DbProvider provider, string sql)
    {
        var db = RequireDb(provider);

        var result = await Service(db, settings: new() { ["App:MaxValueLength"] = "100" }).ExecuteAsync(new("test", sql));

        var value = Assert.IsType<string>(result.Value!.Rows.Single().Values.Single());
        Assert.StartsWith(new string('x', 100) + "…", value);
        Assert.Contains("[truncated: 500 characters in total]", value);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task Stops_adding_rows_at_the_result_size_limit(DbProvider provider)
    {
        var db = RequireDb(provider);
        await SeedAsync(db);

        var result = await Service(db, settings: new() { ["App:MaxResultBytes"] = "60" })
            .ExecuteAsync(new("test", $"SELECT id, name FROM {Table} ORDER BY id"));

        Assert.True(result.Error is null, result.Error);
        Assert.InRange(result.Value!.Rows.Length, 1, 2);
        Assert.True(result.Value.Truncated);
    }

    // Each of these failed the whole query, or threw while serializing it.
    [SkippableTheory]
    [InlineData(DbProvider.Oracle, "SELECT 1/3 AS v FROM dual", "0.33333")]
    [InlineData(DbProvider.Oracle, "SELECT 123456789012345678901234567890123456789 AS v FROM dual", "123456789012345678901234567890123456789")]
    [InlineData(DbProvider.Oracle, "SELECT BINARY_DOUBLE_NAN AS v FROM dual", "\"NaN\"")]
    [InlineData(DbProvider.Postgres, "SELECT 'NaN'::float8 AS v", "\"NaN\"")]
    [InlineData(DbProvider.Postgres, "SELECT '-infinity'::float8 AS v", "\"-Infinity\"")]
    [InlineData(DbProvider.Postgres, "SELECT '10.0.0.1'::inet AS v", "\"10.0.0.1\"")]
    [InlineData(DbProvider.Postgres, "SELECT ARRAY[1, 2] AS v", "[1,2]")]
    [InlineData(DbProvider.Postgres, "SELECT int4range(1, 5) AS v", "\"[1,5)\"")]
    [InlineData(DbProvider.Postgres, "SELECT B'101' AS v", "\"101\"")]
    [InlineData(DbProvider.Postgres, "SELECT 'NaN'::numeric AS v", "Convert it to a string in the query")]
    [InlineData(DbProvider.SqlServer, "SELECT geography::Point(1, 2, 4326) AS v", "Convert it to a string in the query")]
    public async Task Returns_awkward_values_as_json(DbProvider provider, string sql, string expected)
    {
        var db = RequireDb(provider);

        var result = await Service(db).ExecuteAsync(new("test", sql));

        Assert.True(result.Error is null, result.Error);
        var json = JsonSerializer.Serialize(result.Value!.Rows.Single().Values.Single());
        Assert.Contains(expected, json);
    }

    [SkippableFact]
    public async Task Rejects_queries_beyond_the_concurrency_limit()
    {
        var db = RequireDb(DbProvider.Postgres);
        var svc = Service(db, settings: new() { ["App:MaxConcurrentQueries"] = "1", ["App:QueryTimeoutSeconds"] = "3" });

        // One slot: the first holds it for 2.5 s, the second gets it after waiting, and
        // the third would have to wait past the 3 s timeout.
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            await Task.Delay(i * 100);
            return await svc.ExecuteAsync(new("test", "SELECT pg_sleep(2.5) AS s"));
        }));

        Assert.Null(results[0].Error);
        Assert.Contains(results, r => r.Error?.Contains("is busy") == true);
    }

    [SkippableFact]
    public async Task Logs_each_query_without_parameter_values()
    {
        var db = RequireDb(DbProvider.Postgres);
        var logger = new ListLogger();

        await Service(db, logger: logger).ExecuteAsync(Query("SELECT @secret AS v", """{"secret": "hunter2"}"""));
        await Service(db, logger: logger).ExecuteAsync(new("test", "SELECT * FROM sqlmcp_missing_table"));

        var ran = Assert.Single(logger.Messages, m => m.Level == LogLevel.Information);
        Assert.Contains("query with parameters secret: SELECT @secret AS v", ran.Text);
        Assert.Contains("1 rows", ran.Text);
        Assert.Contains(logger.Messages, m => m.Level == LogLevel.Warning && m.Text.Contains("sqlmcp_missing_table"));
        Assert.DoesNotContain(logger.Messages, m => m.Text.Contains("hunter2"));
    }

    private sealed class ListLogger : ILogger<QueryService>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
