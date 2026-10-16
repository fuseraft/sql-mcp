using Npgsql;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using SqlMcp.Models;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlMcp.Services;

/// <summary>
/// Bind parameters for agent queries. Values travel to the database separately from the
/// SQL text, so a value can't end a string literal or add a statement, and quotes in
/// values need no escaping. Placeholders use the provider's own syntax: @name on SQL
/// Server, Postgres and MySQL, :name on Oracle.
/// </summary>
public static partial class QueryParameters
{
    public const int MaxCount = 100;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex ValidName();

    /// <summary>
    /// Converts JSON values to CLR values: strings, numbers (long, else decimal, else
    /// double), booleans and null. A leading @ or : on a name is dropped.
    /// </summary>
    public static (List<KeyValuePair<string, object?>> Parameters, string? Error) Parse(IReadOnlyDictionary<string, JsonElement>? raw)
    {
        var parameters = new List<KeyValuePair<string, object?>>();
        if (raw is null || raw.Count == 0)
            return (parameters, null);
        if (raw.Count > MaxCount)
            return (parameters, $"At most {MaxCount} parameters are allowed.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in raw)
        {
            var name = key.TrimStart('@', ':');
            if (!ValidName().IsMatch(name))
                return (parameters, $"Parameter name '{key}' is invalid: use letters, digits and underscores, starting with a letter or underscore.");
            if (!seen.Add(name))
                return (parameters, $"Parameter '{name}' is given more than once.");

            object? value = json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Number when json.TryGetInt64(out var l) => l,
                JsonValueKind.Number when json.TryGetDecimal(out var m) => m,
                JsonValueKind.Number => json.GetDouble(),
                _ => Invalid.Value
            };
            if (value == Invalid.Value)
                return (parameters, $"Parameter '{name}' must be a string, number, boolean or null, not {json.ValueKind.ToString().ToLowerInvariant()}.");

            parameters.Add(new(name, value));
        }
        return (parameters, null);
    }

    private sealed class Invalid
    {
        public static readonly Invalid Value = new();
    }

    public static void Add(DbCommand cmd, DbProvider provider, string name, object? value)
    {
        if (cmd is OracleCommand oracle)
            oracle.BindByName = true;

        var parameter = cmd.CreateParameter();
        parameter.ParameterName = provider == DbProvider.Oracle ? name : "@" + name;
        parameter.Value = value ?? DBNull.Value;

        // Npgsql sends strings as text, and Postgres won't compare text with a date,
        // uuid or numeric column. Sent untyped, the server infers the type from context
        // as it does for a quoted literal.
        if (parameter is NpgsqlParameter npgsql && value is string or null)
            npgsql.NpgsqlDbType = NpgsqlDbType.Unknown;

        cmd.Parameters.Add(parameter);
    }
}
