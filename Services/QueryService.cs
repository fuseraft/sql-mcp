using SqlMcp.Models;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Collections.Generic;

namespace SqlMcp.Services;

public class QueryService
{
    private readonly List<DbConfig> _dbs;

    public QueryService(IConfiguration config)
    {
        _dbs = config.GetSection("Databases").Get<List<DbConfig>>() ?? new();
    }

    public async Task<Result<QueryResult, string>> ExecuteAsync(QueryRequest req)
    {
        var db = _dbs.FirstOrDefault(d => d.Name == req.DbName);
        if (db == null) return new(null!, $"Unknown database: {req.DbName}");

        var password = Environment.GetEnvironmentVariable(db.PasswordEnvVar ?? "");
        var connStr = db.ConnectionString.Replace("{password}", password ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        try
        {
            var factory = DbProviderFactories.GetFactory(db.GetInvariantName());
            await using var conn = factory.CreateConnection();
            conn!.ConnectionString = connStr;
            await conn.OpenAsync();

            await using var cmd = factory.CreateCommand();
            cmd!.CommandText = req.Sql;
            cmd.Connection = conn;

            await using var reader = await cmd.ExecuteReaderAsync();

            var schemaTable = reader.GetSchemaTable()!;
            var columns = schemaTable.Rows.Cast<DataRow>()
                .Select(r => (string)r["ColumnName"]!)
                .ToArray();

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < columns.Length; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetFieldValue<object>(i);
                }
                rows.Add(row);
            }

            return new(new QueryResult(columns, rows.ToArray()), null!);
        }
        catch (Exception ex)
        {
            return new(null!, $"Error executing query: {ex.Message}");
        }
    }

    public record Result<TValue, TError>(TValue? Value, TError? Error)
    {
        public bool IsError => Error is not null;

    }
}
