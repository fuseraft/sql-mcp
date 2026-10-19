using SqlMcp.Models;
using System.Data.Common;
using System.Text;

namespace SqlMcp.Services;

/// <summary>
/// Asks each database for its estimated plan without running the query: EXPLAIN on
/// Postgres and MySQL, SHOWPLAN_ALL on SQL Server, EXPLAIN PLAN and DBMS_XPLAN on
/// Oracle. The SQL has already passed SqlGuard.
/// </summary>
public static class QueryPlans
{
    public static async Task<string> ExplainAsync(
        Func<DbCommand> newCommand, DbProvider provider, string sql, List<KeyValuePair<string, object?>> parameters, CancellationToken ct) =>
        provider switch
        {
            DbProvider.Postgres => await ReadLinesAsync(newCommand, $"EXPLAIN {sql}", provider, parameters, ct),
            DbProvider.MySql => await ReadLinesAsync(newCommand, $"EXPLAIN FORMAT=TREE {sql}", provider, parameters, ct),
            DbProvider.SqlServer => await ExplainSqlServerAsync(newCommand, sql, parameters, ct),
            DbProvider.Oracle => await ExplainOracleAsync(newCommand, sql, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    private static async Task<string> ReadLinesAsync(
        Func<DbCommand> newCommand, string sql, DbProvider provider, List<KeyValuePair<string, object?>> parameters, CancellationToken ct)
    {
        await using var cmd = newCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            QueryParameters.Add(cmd, provider, name, value);

        var plan = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            plan.AppendLine(reader.IsDBNull(0) ? "" : reader.GetString(0)); // Oracle's blank lines are NULL
        return plan.ToString().TrimEnd();
    }

    /// <summary>
    /// SHOWPLAN plans a parameterized command as the sp_executesql call that carries it,
    /// so parameters are declared in the batch instead, typed from their values. Under
    /// SHOWPLAN nothing in the batch runs, so the values themselves aren't needed.
    /// </summary>
    private static async Task<string> ExplainSqlServerAsync(
        Func<DbCommand> newCommand, string sql, List<KeyValuePair<string, object?>> parameters, CancellationToken ct)
    {
        await SetAsync(newCommand, "SET SHOWPLAN_ALL ON", ct);
        try
        {
            await using var cmd = newCommand();
            // Names were checked by QueryParameters.Parse: letters, digits and underscores.
            var declarations = string.Concat(parameters.Select(p => $"DECLARE @{p.Key} {SqlServerType(p.Value)};\n"));
            cmd.CommandText = declarations + sql;

            // A result set for each statement: the statement, then its plan's operators.
            // The query's is the last.
            var plan = new StringBuilder();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            do
            {
                plan.Clear();
                int text = reader.GetOrdinal("StmtText"), rows = reader.GetOrdinal("EstimateRows"), cost = reader.GetOrdinal("TotalSubtreeCost");
                while (await reader.ReadAsync(ct))
                {
                    plan.Append(reader.GetString(text).TrimEnd());
                    if (!reader.IsDBNull(rows))
                        plan.Append($"  (rows={Convert.ToDouble(reader.GetValue(rows)):G4} cost={Convert.ToDouble(reader.GetValue(cost)):G4})");
                    plan.AppendLine();
                }
            } while (await reader.NextResultAsync(ct));
            return plan.ToString().TrimEnd();
        }
        finally
        {
            await SetAsync(newCommand, "SET SHOWPLAN_ALL OFF", CancellationToken.None);
        }
    }

    private static string SqlServerType(object? value) => value switch
    {
        long => "bigint",
        decimal => "decimal(38, 10)",
        double => "float",
        bool => "bit",
        _ => "nvarchar(4000)"
    };

    /// <summary>
    /// Binds aren't given values: EXPLAIN PLAN plans :name placeholders as unknown values.
    /// The plan rows it writes to PLAN_TABLE are rolled back with the transaction.
    /// </summary>
    private static async Task<string> ExplainOracleAsync(Func<DbCommand> newCommand, string sql, CancellationToken ct)
    {
        var id = $"sqlmcp_{Guid.NewGuid():N}"[..30];
        await SetAsync(newCommand, $"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {sql}", ct);
        return await ReadLinesAsync(newCommand, $"SELECT plan_table_output FROM TABLE(DBMS_XPLAN.DISPLAY('PLAN_TABLE', '{id}', 'TYPICAL'))",
            DbProvider.Oracle, [], ct);
    }

    private static async Task SetAsync(Func<DbCommand> newCommand, string sql, CancellationToken ct)
    {
        await using var cmd = newCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
