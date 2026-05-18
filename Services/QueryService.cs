using SqlMcp.Models;
using System.Data;
using System.Data.Common;

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

    public async Task<Result<QueryResult, string>> ExecuteAsync(QueryRequest req)
    {
        if (!IsReadOnlyQuery(req.Sql))
            return new(null!, "Only SELECT queries (including CTEs) are permitted.");

        var db = _dbs.FirstOrDefault(d => d.Name == req.DbName);
        if (db == null)
            return new(null!, $"Unknown database '{req.DbName}'. Call list_databases to see available names.");

        var connStr = ResolveConnectionString(db);

        try
        {
            var factory = DbProviderFactories.GetFactory(db.GetInvariantName());
            await using var conn = factory.CreateConnection()!;
            conn.ConnectionString = connStr;
            await conn.OpenAsync();

            await using var cmd = factory.CreateCommand()!;
            cmd.CommandText = req.Sql;
            cmd.Connection = conn;
            cmd.CommandTimeout = _queryTimeoutSeconds;

            await using var reader = await cmd.ExecuteReaderAsync();

            var schemaTable = reader.GetSchemaTable()!;
            var columns = schemaTable.Rows.Cast<DataRow>()
                .Select(r => (string)r["ColumnName"]!)
                .ToArray();

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                if (rows.Count >= _maxRows)
                    break;

                var row = new Dictionary<string, object?>();
                for (int i = 0; i < columns.Length; i++)
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetFieldValue<object>(i);
                rows.Add(row);
            }

            return new(new QueryResult(columns, [.. rows], rows.Count >= _maxRows), null!);
        }
        catch (Exception ex)
        {
            return new(null!, $"Query error: {ex.Message}");
        }
    }

    public string[] ListDatabases() => [.. _dbs.Select(d => d.Name)];

    private static string ResolveConnectionString(DbConfig db)
    {
        if (string.IsNullOrEmpty(db.PasswordEnvVar))
            return db.ConnectionString;

        var password = Environment.GetEnvironmentVariable(db.PasswordEnvVar) ?? string.Empty;
        return db.ConnectionString.Replace("{password}", password, StringComparison.OrdinalIgnoreCase);
    }

    // Allows SELECT and WITH (CTEs). Real enforcement happens at the DB user permission level.
    private static bool IsReadOnlyQuery(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    public record Result<TValue, TError>(TValue? Value, TError? Error)
    {
        public bool IsError => Error is not null;
    }
}
