using SqlMcp.Models;
using System.Data;
using System.Data.Common;
using MySqlConnector;

namespace SqlMcp.Services;

public class QueryService
{
    private readonly List<DbConfig> _dbs;
    private readonly int _maxRows;
    private readonly int _queryTimeoutSeconds;

    public QueryService(IConfiguration config)
    {
        _dbs = config.GetSection("Databases").Get<List<DbConfig>>() ?? [];
        _maxRows = config.GetValue("App:MaxRows", 1000);
        _queryTimeoutSeconds = config.GetValue("App:QueryTimeoutSeconds", 30);
    }

    public async Task<Result<QueryResult, string>> ExecuteAsync(QueryRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DbName) || req.Sql is null)
            return new(null!, "Both dbName and sql are required.");

        var db = _dbs.FirstOrDefault(d => d.Name == req.DbName);
        if (db == null)
            return new(null!, $"Unknown database '{req.DbName}'. Call list_databases to see available names.");

        var verdict = SqlGuard.Validate(req.Sql, db.Provider);
        if (!verdict.IsAllowed)
            return new(null!, verdict.Error!);

        var connStr = ResolveConnectionString(db);

        try
        {
            var factory = db.GetFactory();
            await using var conn = factory.CreateConnection()!;
            conn.ConnectionString = connStr;
            await conn.OpenAsync(ct);

            await using var tx = await BeginReadOnlyTransactionAsync(conn, db.Provider, ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = verdict.Sql;
                cmd.Transaction = tx;
                cmd.CommandTimeout = _queryTimeoutSeconds;

                return new(await ReadResultAsync(cmd, ct), null!);
            }
            finally
            {
                // Nothing a query does is ever committed.
                await RollbackQuietlyAsync(tx);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null!, $"Query error: {ex.Message}");
        }
    }

    public string[] ListDatabases() => [.. _dbs.Select(d => d.Name)];

    private async Task<QueryResult> ReadResultAsync(DbCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == _maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>();
            for (int i = 0; i < columns.Length; i++)
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new QueryResult(columns, [.. rows], truncated);
    }

    /// <summary>
    /// Opens a transaction the database itself refuses to write in, where the provider
    /// has such a mode. SQL Server has none, so there the guarantee is SqlGuard plus the
    /// unconditional rollback (and, as everywhere, a read-only login).
    /// </summary>
    private static async Task<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection conn, DbProvider provider, CancellationToken ct)
    {
        if (conn is MySqlConnection mysql)
            return await mysql.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, ct);

        var tx = await conn.BeginTransactionAsync(ct);
        if (provider is DbProvider.Postgres or DbProvider.Oracle)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SET TRANSACTION READ ONLY";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return tx;
    }

    private static async Task RollbackQuietlyAsync(DbTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // The connection may already be broken; disposing it discards the transaction.
        }
    }

    private static string ResolveConnectionString(DbConfig db)
    {
        if (string.IsNullOrEmpty(db.PasswordEnvVar))
            return db.ConnectionString;

        var password = Environment.GetEnvironmentVariable(db.PasswordEnvVar) ?? string.Empty;
        return db.ConnectionString.Replace("{password}", password, StringComparison.OrdinalIgnoreCase);
    }

    public record Result<TValue, TError>(TValue? Value, TError? Error)
    {
        public bool IsError => Error is not null;
    }
}
