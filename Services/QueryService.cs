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

    // list_tables shortens table comments to this many characters; describe_table has them in full.
    private const int ListedCommentLength = 200;

    // ORA-01466: a read-only transaction started too soon after the table's last DDL.
    private const int OracleTableDefinitionChanged = 1466;
    private static readonly TimeSpan[] OracleRetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly List<DbConfig> _dbs;
    private readonly int _maxRows;
    private readonly int _maxTables;
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

        _maxRows = Positive(config, "App:MaxRows", 100, errors);
        _maxTables = Positive(config, "App:MaxTables", 1000, errors);
        _queryTimeoutSeconds = Positive(config, "App:QueryTimeoutSeconds", 30, errors);
        _maxValueLength = Positive(config, "App:MaxValueLength", 10_000, errors);
        // Results go into a model's context: 100 KB is roughly 25,000 tokens.
        _maxResultBytes = Positive(config, "App:MaxResultBytes", 100_000, errors);
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

    /// <summary>
    /// Runs a read-only query. agent names the caller in the log; null means it wasn't
    /// authenticated (stdio, or HTTP without App:AgentTokens).
    /// </summary>
    public async Task<Result<QueryResult, string>> ExecuteAsync(QueryRequest req, string? agent = null, CancellationToken ct = default)
    {
        var (db, sql, parameters, operation, error) = Prepare(req, "query", agent);
        if (error is not null)
            return new(default, error);

        return await RunAsync(db!, operation, agent, async newCommand =>
        {
            await using var cmd = newCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                QueryParameters.Add(cmd, db!.Provider, name, value);
            return Ok(await ReadResultAsync(cmd, db!.Provider, ct));
        }, ct);
    }

    /// <summary>
    /// Returns the database's estimated plan for a query without running it. The query
    /// passes the same checks as ExecuteAsync.
    /// </summary>
    public async Task<Result<QueryPlan, string>> ExplainAsync(QueryRequest req, string? agent = null, CancellationToken ct = default)
    {
        var (db, sql, parameters, operation, error) = Prepare(req, "explain", agent);
        if (error is not null)
            return new(default, error);

        // Oracle's EXPLAIN PLAN writes the plan to PLAN_TABLE, which a read-only
        // transaction refuses. The query itself is only parsed, and the plan rows are
        // rolled back with everything else.
        return await RunAsync(db!, operation, agent, async newCommand =>
        {
            var plan = await QueryPlans.ExplainAsync(newCommand, db!.Provider, sql, parameters, ct);
            return Ok(new QueryPlan(plan.Length > _maxResultBytes ? $"{plan[..(int)_maxResultBytes]}… [truncated: {plan.Length} characters in total]" : plan));
        }, ct, readOnly: db!.Provider != DbProvider.Oracle);
    }

    /// <summary>
    /// Checks a query request before connecting: the database exists, the SQL passes
    /// SqlGuard and the parameters parse. A refusal is logged and returned as error.
    /// </summary>
    private (DbConfig? Db, string Sql, List<KeyValuePair<string, object?>> Parameters, string Operation, string? Error) Prepare(
        QueryRequest req, string kind, string? agent)
    {
        (DbConfig?, string, List<KeyValuePair<string, object?>>, string, string?) Refused(string? dbName, string error) =>
            (null, "", [], "", Refuse<bool>($"{kind}: {req.Sql}", dbName, agent, error).Error);

        if (string.IsNullOrWhiteSpace(req.DbName) || req.Sql is null)
            return Refused(req.DbName, "Both dbName and sql are required.");

        var db = FindDatabase(req.DbName);
        if (db == null)
            return Refused(req.DbName, UnknownDatabase(req.DbName));

        var verdict = SqlGuard.Validate(req.Sql, db.Provider);
        if (!verdict.IsAllowed)
            return Refused(db.Name, verdict.Error!);

        var (parameters, parameterError) = QueryParameters.Parse(req.Parameters);
        if (parameterError is not null)
            return Refused(db.Name, parameterError);

        var operation = parameters.Count == 0
            ? $"{kind}: {verdict.Sql}"
            : $"{kind} with parameters {string.Join(", ", parameters.Select(p => p.Key))}: {verdict.Sql}";
        return (db, verdict.Sql, parameters, operation, null);
    }

    /// <summary>
    /// Lists the tables and views the login can read, optionally in one schema, and
    /// optionally only those whose name or comment contains search.
    /// </summary>
    public async Task<Result<TableList, string>> ListTablesAsync(
        string dbName, string? schema, string? search = null, string? agent = null, CancellationToken ct = default)
    {
        schema = NullIfBlank(schema);
        search = NullIfBlank(search);
        var operation = $"list_tables{(schema is null ? "" : $" in {schema}")}{(search is null ? "" : $" matching {search}")}";
        var db = FindDatabase(dbName);
        if (db == null)
            return Refuse<TableList>(operation, dbName, agent, UnknownDatabase(dbName));

        return await RunAsync(db, operation, agent, async newCommand =>
        {
            var tables = (await SchemaCatalog.FindTablesAsync(newCommand, db.Provider, schema, null, _maxTables, ct, search))
                .Select(t => t.Comment?.Length > ListedCommentLength ? t with { Comment = t.Comment[..ListedCommentLength] + "…" } : t)
                .ToList();
            return Ok(tables.Count > _maxTables
                ? new TableList([.. tables.Take(_maxTables)], Truncated: true,
                    $"Only the first {_maxTables} tables are shown (App:MaxTables). Pass search to find tables by name or comment" +
                    $"{(schema is null ? ", or schema to list one schema at a time" : "")}.")
                : new TableList([.. tables]));
        }, ct);
    }

    /// <summary>
    /// Returns a table's columns. Names match case-insensitively; without a schema the
    /// table is looked up across all non-system schemas, and "schema.table" is accepted.
    /// </summary>
    public async Task<Result<TableDescription, string>> DescribeTableAsync(
        string dbName, string table, string? schema, string? agent = null, CancellationToken ct = default)
    {
        var db = FindDatabase(dbName);
        if (db == null)
            return Refuse<TableDescription>($"describe_table {table}", dbName, agent, UnknownDatabase(dbName));
        if (string.IsNullOrWhiteSpace(table))
            return Refuse<TableDescription>("describe_table", db.Name, agent, "table is required.");

        table = table.Trim();
        schema = NullIfBlank(schema);
        int dot = table.IndexOf('.');
        if (schema is null && dot > 0 && dot < table.Length - 1)
            (schema, table) = (table[..dot], table[(dot + 1)..]);

        return await RunAsync(db, $"describe_table {(schema is null ? table : $"{schema}.{table}")}", agent, async newCommand =>
        {
            var matches = await SchemaCatalog.FindTablesAsync(newCommand, db.Provider, schema, table, 50, ct);

            // "Orders" and "orders" can both exist; an exact match settles it.
            if (matches.Count > 1 && matches.Count(m => m.Name == table && (schema is null || m.Schema == schema)) == 1)
                matches = [matches.Single(m => m.Name == table && (schema is null || m.Schema == schema))];

            if (matches.Count != 1)
            {
                var name = schema is null ? table : $"{schema}.{table}";
                return new Result<TableDescription, string>(null, matches.Count == 0
                    ? $"Table '{name}' was not found in database '{db.Name}', or the login can't read it. Call list_tables to see what it can read."
                    : $"'{name}' matches more than one table: {string.Join(", ", matches.Select(m => $"{m.Schema}.{m.Name}"))}. Pass schema to choose one.");
            }

            var match = matches[0];
            var columns = await SchemaCatalog.GetColumnsAsync(newCommand, db.Provider, match, ct);
            var (foreignKeys, referencedBy) = await SchemaCatalog.GetForeignKeysAsync(newCommand, db.Provider, match, ct);
            return Ok(new TableDescription(match.Schema, match.Name, match.Type, match.Comment, match.ApproxRows, columns, foreignKeys, referencedBy));
        }, ct);
    }

    /// <summary>
    /// Connects, checks the login, and runs work inside a transaction that is always
    /// rolled back, and read-only unless readOnly is false. Commands from newCommand are
    /// bound to that transaction. work returns an error for an outcome that isn't a
    /// database error but still failed, such as a table that wasn't found, so it's
    /// logged as a failure.
    /// </summary>
    private async Task<Result<T, string>> RunAsync<T>(DbConfig db, string operation, string? agent, Func<Func<DbCommand>, Task<Result<T, string>>> work, CancellationToken ct,
        bool log = true, bool readOnly = true)
    {
        if (MissingPassword(db) is { } missing)
        {
            var unavailable = $"Database '{db.Name}' is unavailable: {missing}";
            return log ? Refuse<T>(operation, db.Name, agent, unavailable) : new(default, unavailable);
        }

        // Bounds the connections one busy agent can hold open against a database. Callers
        // wait up to the query timeout for a free slot.
        var slot = _slots[db.Name];
        if (!await slot.WaitAsync(TimeSpan.FromSeconds(_queryTimeoutSeconds), ct))
        {
            _logger.LogWarning("Rejected {Operation} on {Database} for {Agent}: all query slots stayed busy for {Seconds} s.", operation, db.Name, Who(agent), _queryTimeoutSeconds);
            return new(default, $"Database '{db.Name}' is busy: too many queries are already running. Try again shortly.");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await RunWithRetryAsync(db, work, readOnly, ct);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!log)
                return result;
            if (result.IsError)
                _logger.LogWarning("Failed {Operation} on {Database} for {Agent} after {ElapsedMs:F0} ms: {Error}", operation, db.Name, Who(agent), elapsed, result.Error);
            else
                _logger.LogInformation("Ran {Operation} on {Database} for {Agent} in {ElapsedMs:F0} ms, {Rows} rows{Truncated}.",
                    operation, db.Name, Who(agent), elapsed, CountRows(result.Value), IsTruncated(result.Value) ? " (truncated)" : "");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cancelled {Operation} on {Database} for {Agent} after {ElapsedMs:F0} ms.", operation, db.Name, Who(agent), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>
    /// Logs a call turned away before it reached the database, such as a statement
    /// SqlGuard rejected, so the log shows what each agent tried as well as what ran.
    /// </summary>
    private Result<T, string> Refuse<T>(string operation, string? dbName, string? agent, string error)
    {
        _logger.LogWarning("Refused {Operation} on {Database} for {Agent}: {Error}", operation, dbName, Who(agent), error);
        return new(default!, error);
    }

    private static string Who(string? agent) => agent ?? "unauthenticated";

    private static Result<T, string> Ok<T>(T value) => new(value, null);

    private static int CountRows(object? value) => value switch
    {
        QueryResult q => q.Rows.Length,
        TableList t => t.Tables.Length,
        TableDescription d => d.Columns.Length,
        QueryPlan p => p.Plan.Split('\n').Length,
        _ => 0
    };

    private static bool IsTruncated(object? value) => value is QueryResult { Truncated: true } or TableList { Truncated: true };

    private async Task<Result<T, string>> RunWithRetryAsync<T>(DbConfig db, Func<Func<DbCommand>, Task<Result<T, string>>> work, bool readOnly, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(db, work, readOnly, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OracleException ex) when (ex.Number == OracleTableDefinitionChanged && attempt < OracleRetryDelays.Length)
            {
                await Task.Delay(OracleRetryDelays[attempt], ct);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                return new(default, $"Query timed out after {_queryTimeoutSeconds} s (App:QueryTimeoutSeconds). Narrow it with a WHERE clause or an index, or ask for fewer rows.");
            }
            catch (Exception ex)
            {
                return new(default, $"Query error: {ex.Message}");
            }
        }
    }

    // How each driver reports a command that ran past CommandTimeout. Cancellation the
    // caller asked for is rethrown before this is reached, so a cancel here is a timeout.
    private static bool IsTimeout(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => true,
        Npgsql.NpgsqlException { InnerException: TimeoutException } => true,
        Microsoft.Data.SqlClient.SqlException { Number: -2 } => true,
        MySqlException { ErrorCode: MySqlErrorCode.CommandTimeoutExpired } => true,
        OracleException { Number: 1013 } => true,
        _ => false,
    };

    private async Task<Result<T, string>> RunOnceAsync<T>(DbConfig db, Func<Func<DbCommand>, Task<Result<T, string>>> work, bool readOnly, CancellationToken ct)
    {
        await using var conn = db.GetFactory().CreateConnection()!;
        conn.ConnectionString = ResolveConnectionString(db);
        await conn.OpenAsync(ct);

        if (!await IsLoginReadOnlyAsync(db, conn, ct))
            return new(default, $"Database '{db.Name}' is disabled because its login can modify data. The server log lists why.");

        await using var tx = readOnly ? await BeginReadOnlyTransactionAsync(conn, db.Provider, ct) : await conn.BeginTransactionAsync(ct);
        try
        {
            return await work(() =>
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = _queryTimeoutSeconds;
                return cmd;
            });
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

    public DatabaseInfo[] ListDatabases(string? agent = null)
    {
        _logger.LogInformation("Listed {Count} databases for {Agent}.", _dbs.Count, Who(agent));
        return [.. _dbs.Select(d => new DatabaseInfo(d.Name, d.Provider.ToString()))];
    }

    /// <summary>
    /// Connects to every database, runs the login audit and opens the read-only
    /// transaction, without running a query. Returns "ok" or the error for each.
    /// </summary>
    public async Task<Dictionary<string, string>> CheckDatabasesAsync(CancellationToken ct = default)
    {
        var checks = _dbs.Select(async db =>
        {
            var result = await RunAsync(db, "readiness check", null, _ => Task.FromResult(Ok(true)), ct, log: false);
            return (db.Name, Status: result.Error ?? "ok");
        });
        return (await Task.WhenAll(checks)).ToDictionary(c => c.Name, c => c.Status);
    }

    private async Task<QueryResult> ReadResultAsync(DbCommand cmd, DbProvider provider, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        ResultValues.Prepare(reader);

        var columns = UniqueColumnNames(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));

        var rows = new List<object?[]>();
        string? note = null;
        long size = columns.Sum(c => c.Length + 4L);
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == _maxRows)
            {
                note = $"Only the first {_maxRows} rows are shown (App:MaxRows). Narrow the query with WHERE, " +
                       $"summarize it with GROUP BY, or page through it with {PagingClause(provider, _maxRows)}.";
                break;
            }

            var row = new object?[columns.Length];
            long rowSize = 2;
            for (int i = 0; i < columns.Length; i++)
            {
                row[i] = LimitLength(ResultValues.Read(reader, i));
                rowSize += EstimateSize(row[i]) + 1;
            }

            // Stop before the response outgrows App:MaxResultBytes; the first row is
            // always returned, since its values are already capped.
            if (rows.Count > 0 && size + rowSize > _maxResultBytes)
            {
                note = $"Stopped after {rows.Count} rows: the result reached about {_maxResultBytes} bytes (App:MaxResultBytes). " +
                       "Select fewer or shorter columns, or fewer rows.";
                break;
            }
            size += rowSize;
            rows.Add(row);
        }

        return new QueryResult(columns, [.. rows], Truncated: note is not null, note);
    }

    private static string PagingClause(DbProvider provider, int pageSize) => provider is DbProvider.Postgres or DbProvider.MySql
        ? $"ORDER BY ... LIMIT {pageSize} OFFSET {pageSize}"
        : $"ORDER BY ... OFFSET {pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY";

    /// <summary>
    /// Gives every column a distinct name, so "SELECT 1, 2" or "SELECT a.id, b.id" can
    /// be told apart. Unnamed columns become column1, column2, ... and repeats get a
    /// suffix: id, id_2.
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
