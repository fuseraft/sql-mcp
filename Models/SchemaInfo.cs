using System.Text.Json.Serialization;

namespace SqlMcp.Models;

/// <summary>A table or view. Type is "table" or "view".</summary>
public record TableInfo(string Schema, string Name, string Type);

public record TableList(
    TableInfo[] Tables,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

public record ColumnInfo(string Name, string DataType, bool Nullable, bool PrimaryKey);

public record TableDescription(string Schema, string Name, string Type, ColumnInfo[] Columns);
