using Microsoft.Extensions.Configuration;
using SqlMcp.Models;
using SqlMcp.Services;
using System.Data.Common;

namespace SqlMcp.Tests;

/// <summary>
/// Runs against real databases. Each provider is skipped unless its connection string
/// is set, e.g. SQLMCP_TEST_POSTGRES="Host=localhost;Username=postgres;Password=pw".
/// The login must be able to create tables; the tests set up their own fixtures.
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

    private static QueryService Service(DbConfig db, int maxRows = 1000) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:MaxRows"] = maxRows.ToString(),
            ["Databases:0:Name"] = db.Name,
            ["Databases:0:Provider"] = db.Provider.ToString(),
            ["Databases:0:ConnectionString"] = db.ConnectionString,
        }).Build());

    private static string From(DbProvider provider) => provider == DbProvider.Oracle ? " FROM dual" : "";

    /// <summary>
    /// Resets the fixture table to ids 1..3 using a direct, writable connection. The table
    /// is created once and then reseeded with DML only: Oracle refuses a read-only
    /// transaction on a table whose definition changed moments earlier (ORA-01466).
    /// </summary>
    private static async Task SeedAsync(DbConfig db, params string[] extra)
    {
        await using var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = db.ConnectionString;
        await conn.OpenAsync();

        var created = await TryExecAsync(conn, $"CREATE TABLE {Table} (id INT, name VARCHAR(20))");
        await ExecAsync(conn, $"DELETE FROM {Table}");
        foreach (var id in new[] { 1, 2, 3 })
            await ExecAsync(conn, $"INSERT INTO {Table} (id, name) VALUES ({id}, 'row {id}')");
        foreach (var sql in extra)
            await ExecAsync(conn, sql);

        if (created && db.Provider == DbProvider.Oracle)
            await WaitForOracleSnapshotAsync(conn);
    }

    private static async Task WaitForOracleSnapshotAsync(DbConnection conn)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SET TRANSACTION READ ONLY";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = $"SELECT COUNT(*) FROM {Table}";
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (DbException ex) when (ex.Message.Contains("ORA-01466") && attempt < 40)
            {
                await Task.Delay(250);
            }
            finally
            {
                await tx.RollbackAsync();
            }
        }
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
}
