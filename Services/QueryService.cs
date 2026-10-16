using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;
using SqlMcp.Models;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;

namespace SqlMcp.Services;

public class QueryService
{
    // How long a login audit result is trusted before the login is checked again, so a
    // later GRANT is noticed without a restart.
    private static readonly TimeSpan AuditLifetime = TimeSpan.FromMinutes(5);

    // ORA-01466: a read-only transaction started too soon after the table's last DDL.
    private const int OracleTableDefinitionChanged = 1466;
    private static readonly TimeSpan[] OracleRetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly List<DbConfig> _dbs;
    private readonly int _maxRows;
    private readonly int _queryTimeoutSeconds;
    private readonly ILogger<QueryService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime CheckedAt, IReadOnlyList<string> Findings)> _audits = new();

    public QueryService(IConfiguration config, ILogger<QueryService>? logger = null)
    {
        _dbs = config.GetSection("Databases").Get<List<DbConfig>>() ?? [];
        _maxRows = config.GetValue("App:MaxRows", 1000);
        _queryTimeoutSeconds = config.GetValue("App:QueryTimeoutSeconds", 30);
        _logger = logger ?? NullLogger<QueryService>.Instance;

        foreach (var db in _dbs.Where(d => d.AllowWritableLogin))
            _logger.LogWarning("Database {Database} has AllowWritableLogin set; its login is not checked for write access.", db.Name);
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

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(db, verdict.Sql, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OracleException ex) when (ex.Number == OracleTableDefinitionChanged && attempt < OracleRetryDelays.Length)
            {
                await Task.Delay(OracleRetryDelays[attempt], ct);
            }
            catch (Exception ex)
            {
                return new(null!, $"Query error: {ex.Message}");
            }
        }
    }

    private async Task<Result<QueryResult, string>> ExecuteOnceAsync(DbConfig db, string sql, CancellationToken ct)
    {
        await using var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = ResolveConnectionString(db);
        await conn.OpenAsync(ct);

        if (!await IsLoginReadOnlyAsync(db, conn, ct))
            return new(null!, $"Database '{db.Name}' is disabled because its login can modify data. The server log lists why.");

        await using var tx = await BeginReadOnlyTransactionAsync(conn, db.Provider, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
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

    private async Task<bool> IsLoginReadOnlyAsync(DbConfig db, DbConnection conn, CancellationToken ct)
    {
        if (db.AllowWritableLogin)
            return true;

        if (!_audits.TryGetValue(db.Name, out var audit) || DateTime.UtcNow - audit.CheckedAt > AuditLifetime)
        {
            var findings = await LoginAudit.FindWriteAccessAsync(conn, db.Provider, ct);
            if (findings.Count > 0)
            {
                _logger.LogError(
                    "Refusing queries on database {Database}: its login is not read-only. It {Findings}. " +
                    "Use a login that can only read, or set AllowWritableLogin to true for this database to accept the risk.",
                    db.Name, string.Join("; it ", findings));
            }
            audit = (DateTime.UtcNow, findings);
            _audits[db.Name] = audit;
        }

        return audit.Findings.Count == 0;
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
