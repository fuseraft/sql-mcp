using System.Text.Json.Serialization;

namespace SqlMcp.Models;

/// <summary>
/// Each row is an array of values in the order of Columns, so column names aren't
/// repeated on every row. Note says why a truncated result stopped and what to do next.
/// </summary>
public record QueryResult(
    string[] Columns,
    object?[][] Rows,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);
