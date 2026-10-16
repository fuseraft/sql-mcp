using Microsoft.Extensions.Configuration;
using SqlMcp.Models;
using SqlMcp.Services;
using System.Data.Common;

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
    private static QueryService Service(DbConfig db, int maxRows = 1000, bool allowWritableLogin = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:MaxRows"] = maxRows.ToString(),
            ["Databases:0:Name"] = db.Name,
            ["Databases:0:Provider"] = db.Provider.ToString(),
            ["Databases:0:ConnectionString"] = db.ConnectionString,
            ["Databases:0:AllowWritableLogin"] = allowWritableLogin.ToString(),
        }).Build());

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
}
