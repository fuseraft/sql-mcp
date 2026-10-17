using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;
using SqlMcp.Models;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

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
    private readonly int _maxValueLength;
    private readonly long _maxResultBytes;
    private readonly ILogger<QueryService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime CheckedAt, IReadOnlyList<string> Findings)> _audits = new();
    private readonly Dictionary<string, SemaphoreSlim> _slots;

    /// <summary>
    /// Reads and validates the configuration; throws InvalidOperationException listing
    /// every problem, so a bad config stops the server at startup.
    /// </summary>
    public QueryService(IConfiguration config, ILogger<QueryService>? logger = null)
    {
        _logger = logger ?? NullLogger<QueryService>.Instance;

        var errors = new List<string>();
        try
        {
            _dbs = config.GetSection("Databases").Get<List<DbConfig>>() ?? [];
        }
        catch (InvalidOperationException ex)
        {
            // e.g. an unknown Provider; the binder names the bad value.
            throw new InvalidOperationException($"Invalid configuration: {ex.Message} {ex.InnerException?.Message}".Trim(), ex);
        }

        _maxRows = Positive(config, "App:MaxRows", 1000, errors);
        _queryTimeoutSeconds = Positive(config, "App:QueryTimeoutSeconds", 30, errors);
        _maxValueLength = Positive(config, "App:MaxValueLength", 10_000, errors);
        _maxResultBytes = Positive(config, "App:MaxResultBytes", 4_000_000, errors);
        int maxConcurrent = Positive(config, "App:MaxConcurrentQueries", 8, errors);

        // The binder turns a missing or misspelled Provider into SqlServer, the enum's
        // default, so check the raw values.
        foreach (var (entry, i) in config.GetSection("Databases").GetChildren().Select((e, i) => (e, i)))
        {
            var provider = entry["Provider"];
            if (!Enum.TryParse<DbProvider>(provider, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(provider, out _))
                errors.Add($"{(string.IsNullOrWhiteSpace(entry["Name"]) ? $"Databases:{i}" : $"Database '{entry["Name"]}'")} has " +
                           $"{(string.IsNullOrWhiteSpace(provider) ? "no Provider" : $"unknown Provider '{provider}'")}; " +
                           $"use one of {string.Join(", ", Enum.GetNames<DbProvider>())}.");
        }

        ValidateDatabases(errors);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration:" + string.Concat(errors.Select(e => $"{Environment.NewLine}  - {e}")));

        _slots = _dbs.ToDictionary(d => d.Name, _ => new SemaphoreSlim(maxConcurrent, maxConcurrent));

        if (_dbs.Count == 0)
            _logger.LogWarning("No databases are configured; add entries under Databases.");
        foreach (var db in _dbs.Where(d => d.AllowWritableLogin))
            _logger.LogWarning("Database {Database} has AllowWritableLogin set; its login is not checked for write access.", db.Name);
        foreach (var db in _dbs.Where(d => MissingPassword(d) is not null))
            _logger.LogWarning("Database {Database} can't be queried: {Problem}", db.Name, MissingPassword(db));
    }

    private static int Positive(IConfiguration config, string key, int fallback, List<string> errors)
    {
        int value;
        try
        {
            value = config.GetValue(key, fallback);
        }
        catch (InvalidOperationException)
        {
            errors.Add($"{key} must be a whole number, not '{config[key]}'.");
            return fallback;
        }
        if (value < 1)
            errors.Add($"{key} must be at least 1, not {value}.");
        return value;
    }

    private void ValidateDatabases(List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _dbs.Count; i++)
        {
            var db = _dbs[i];
            var label = string.IsNullOrWhiteSpace(db.Name) ? $"Databases:{i}" : $"Database '{db.Name}'";
            if (string.IsNullOrWhiteSpace(db.Name))
                errors.Add($"{label} has no Name.");
            else if (!names.Add(db.Name))
                errors.Add($"{label} is configured more than once (names are compared ignoring case).");

            if (string.IsNullOrWhiteSpace(db.ConnectionString))
            {
                errors.Add($"{label} has no ConnectionString.");
                continue;
            }

            try
            {
                var builder = db.GetFactory().CreateConnectionStringBuilder()!;
                builder.ConnectionString = db.ConnectionString;
            }
            catch (Exception ex)
            {
                errors.Add($"{label} has an invalid {db.Provider} ConnectionString: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(db.PasswordEnvVar) && !db.ConnectionString.Contains("{password}", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label} sets PasswordEnvVar but its ConnectionString has no {{password}} placeholder.");
        }
    }

    /// <summary>
    /// A missing password variable disables only its own database, so the others stay up.
    /// </summary>
    private static string? MissingPassword(DbConfig db) =>
        !string.IsNullOrEmpty(db.PasswordEnvVar) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(db.PasswordEnvVar))
            ? $"environment variable {db.PasswordEnvVar}, which holds its password, is not set."
            : null;

    public async Task<Result<QueryResult, string>> ExecuteAsync(QueryRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DbName) || req.Sql is null)
            return new(null!, "Both dbName and sql are required.");

        var db = FindDatabase(req.DbName);
        if (db == null)
            return new(null!, UnknownDatabase(req.DbName));

        var verdict = SqlGuard.Validate(req.Sql, db.Provider);
        if (!verdict.IsAllowed)
            return new(null!, verdict.Error!);

        var (parameters, parameterError) = QueryParameters.Parse(req.Parameters);
        if (parameterError is not null)
            return new(null!, parameterError);

        var operation = parameters.Count == 0
            ? $"query: {verdict.Sql}"
            : $"query with parameters {string.Join(", ", parameters.Select(p => p.Key))}: {verdict.Sql}";
        return await RunAsync(db, operation, async newCommand =>
        {
            await using var cmd = newCommand();
            cmd.CommandText = verdict.Sql;
            foreach (var (name, value) in parameters)
                QueryParameters.Add(cmd, db.Provider, name, value);
            return await ReadResultAsync(cmd, ct);
        }, ct);
    }

    /// <summary>
    /// Lists the tables and views the login can read, optionally in one schema.
    /// </summary>
    public async Task<Result<TableList, string>> ListTablesAsync(string dbName, string? schema, CancellationToken ct = default)
    {
        var db = FindDatabase(dbName);
        if (db == null)
            return new(null!, UnknownDatabase(dbName));

        return await RunAsync(db, schema is null ? "list_tables" : $"list_tables in {schema}", async newCommand =>
        {
            var tables = await SchemaCatalog.FindTablesAsync(newCommand, db.Provider, NullIfBlank(schema), null, _maxRows, ct);
            return tables.Count > _maxRows
                ? new TableList([.. tables.Take(_maxRows)], Truncated: true)
                : new TableList([.. tables]);
        }, ct);
    }

    /// <summary>
    /// Returns a table's columns. Names match case-insensitively; without a schema the
    /// table is looked up across all non-system schemas, and "schema.table" is accepted.
    /// </summary>
    public async Task<Result<TableDescription, string>> DescribeTableAsync(
        string dbName, string table, string? schema, CancellationToken ct = default)
    {
        var db = FindDatabase(dbName);
        if (db == null)
            return new(null!, UnknownDatabase(dbName));
        if (string.IsNullOrWhiteSpace(table))
            return new(null!, "table is required.");

        table = table.Trim();
        schema = NullIfBlank(schema);
        int dot = table.IndexOf('.');
        if (schema is null && dot > 0 && dot < table.Length - 1)
            (schema, table) = (table[..dot], table[(dot + 1)..]);

        string? notFound = null;
        var result = await RunAsync(db, $"describe_table {(schema is null ? table : $"{schema}.{table}")}", async newCommand =>
        {
            var matches = await SchemaCatalog.FindTablesAsync(newCommand, db.Provider, schema, table, 50, ct);

            // "Orders" and "orders" can both exist; an exact match settles it.
            if (matches.Count > 1 && matches.Count(m => m.Name == table && (schema is null || m.Schema == schema)) == 1)
                matches = [matches.Single(m => m.Name == table && (schema is null || m.Schema == schema))];

            if (matches.Count != 1)
            {
                var name = schema is null ? table : $"{schema}.{table}";
                notFound = matches.Count == 0
                    ? $"Table '{name}' was not found in database '{db.Name}', or the login can't read it. Call list_tables to see what it can read."
                    : $"'{name}' matches more than one table: {string.Join(", ", matches.Select(m => $"{m.Schema}.{m.Name}"))}. Pass schema to choose one.";
                return null!;
            }

            var match = matches[0];
            var columns = await SchemaCatalog.GetColumnsAsync(newCommand, db.Provider, match, ct);
            return new TableDescription(match.Schema, match.Name, match.Type, columns);
        }, ct);

        return notFound is null ? result : new(null!, notFound);
    }

    /// <summary>
    /// Connects, checks the login, and runs work inside a read-only transaction that is
    /// always rolled back. Commands from newCommand are bound to that transaction.
    /// </summary>
    private async Task<Result<T, string>> RunAsync<T>(DbConfig db, string operation, Func<Func<DbCommand>, Task<T>> work, CancellationToken ct)
    {
        if (MissingPassword(db) is { } missing)
            return new(default, $"Database '{db.Name}' is unavailable: {missing}");

        // Bounds the connections one busy agent can hold open against a database. Callers
        // wait up to the query timeout for a free slot.
        var slot = _slots[db.Name];
        if (!await slot.WaitAsync(TimeSpan.FromSeconds(_queryTimeoutSeconds), ct))
        {
            _logger.LogWarning("Rejected {Operation} on {Database}: all query slots stayed busy for {Seconds} s.", operation, db.Name, _queryTimeoutSeconds);
            return new(default, $"Database '{db.Name}' is busy: too many queries are already running. Try again shortly.");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await RunWithRetryAsync(db, work, ct);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result.IsError)
                _logger.LogWarning("Failed {Operation} on {Database} after {ElapsedMs:F0} ms: {Error}", operation, db.Name, elapsed, result.Error);
            else
                _logger.LogInformation("Ran {Operation} on {Database} in {ElapsedMs:F0} ms, {Rows} rows{Truncated}.",
                    operation, db.Name, elapsed, CountRows(result.Value), IsTruncated(result.Value) ? " (truncated)" : "");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cancelled {Operation} on {Database} after {ElapsedMs:F0} ms.", operation, db.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        finally
        {
            slot.Release();
        }
    }

    private static int CountRows(object? value) => value switch
    {
        QueryResult q => q.Rows.Length,
        TableList t => t.Tables.Length,
        TableDescription d => d.Columns.Length,
        _ => 0
    };

    private static bool IsTruncated(object? value) => value is QueryResult { Truncated: true } or TableList { Truncated: true };

    private async Task<Result<T, string>> RunWithRetryAsync<T>(DbConfig db, Func<Func<DbCommand>, Task<T>> work, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(db, work, ct);
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
                return new(default, $"Query error: {ex.Message}");
            }
        }
    }

    private async Task<Result<T, string>> RunOnceAsync<T>(DbConfig db, Func<Func<DbCommand>, Task<T>> work, CancellationToken ct)
    {
        await using var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = ResolveConnectionString(db);
        await conn.OpenAsync(ct);

        if (!await IsLoginReadOnlyAsync(db, conn, ct))
            return new(default, $"Database '{db.Name}' is disabled because its login can modify data. The server log lists why.");

        await using var tx = await BeginReadOnlyTransactionAsync(conn, db.Provider, ct);
        try
        {
            return new(await work(() =>
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _queryTimeoutSeconds;
                return cmd;
            }), null!);
        }
        finally
        {
            // Nothing a query does is ever committed.
            await RollbackQuietlyAsync(tx);
        }
    }

    private DbConfig? FindDatabase(string? name) =>
        _dbs.FirstOrDefault(d => d.Name == name) ?? _dbs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string UnknownDatabase(string? name) =>
        $"Unknown database '{name}'. Call list_databases to see available names.";

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
        ResultValues.Prepare(reader);

        var columns = UniqueColumnNames(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));

        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        long size = columns.Sum(c => c.Length + 4L);
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == _maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(columns.Length);
            long rowSize = 2;
            for (int i = 0; i < columns.Length; i++)
            {
                var value = LimitLength(ResultValues.Read(reader, i));
                row[columns[i]] = value;
                rowSize += columns[i].Length + 4 + EstimateSize(value);
            }

            // Stop before the response outgrows App:MaxResultBytes; the first row is
            // always returned, since its values are already capped.
            if (rows.Count > 0 && size + rowSize > _maxResultBytes)
            {
                truncated = true;
                break;
            }
            size += rowSize;
            rows.Add(row);
        }

        return new QueryResult(columns, [.. rows], truncated);
    }

    /// <summary>
    /// Rows are keyed by column name, so "SELECT 1, 2" or "SELECT a.id, b.id" would
    /// otherwise lose all but one value. Unnamed columns become column1, column2, ...
    /// and repeats get a suffix: id, id_2.
    /// </summary>
    public static string[] UniqueColumnNames(IEnumerable<string?> names)
    {
        var given = names.Select((n, i) => string.IsNullOrWhiteSpace(n) || n == "?column?" ? $"column{i + 1}" : n).ToArray();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new string[given.Length];
        for (int i = 0; i < given.Length; i++)
        {
            var name = given[i];
            for (int n = 2; !used.Add(name); n++)
                name = $"{given[i]}_{n}";
            result[i] = name;
        }
        return result;
    }

    /// <summary>Caps long strings and binary values at App:MaxValueLength characters.</summary>
    private object? LimitLength(object? value)
    {
        switch (value)
        {
            case string s when s.Length > _maxValueLength:
                return $"{s[.._maxValueLength]}… [truncated: {s.Length} characters in total]";
            case byte[] b when (b.Length + 2) / 3 * 4 > _maxValueLength:
                var keep = _maxValueLength / 4 * 3;
                return $"{Convert.ToBase64String(b, 0, keep)}… [truncated: {b.Length} bytes in total, base64]";
            default:
                return value;
        }
    }

    private static long EstimateSize(object? value) => value switch
    {
        null => 4,
        string s => s.Length + 2,
        byte[] b => (b.Length + 2) / 3 * 4 + 2,
        System.Collections.IEnumerable => JsonSerializer.Serialize(value).Length,
        _ => 24
    };

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
