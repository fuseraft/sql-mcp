using System.Text.Json.Serialization;

namespace SqlMcp.Models;

/// <summary>
/// A table or view. Type is "table" or "view". ApproxRows is the database's own
/// estimate from its statistics, absent for views and for tables never analyzed.
/// </summary>
public record TableInfo(
    string Schema,
    string Name,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ApproxRows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment = null);

public record TableList(
    TableInfo[] Tables,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

public record ColumnInfo(
    string Name,
    string DataType,
    bool Nullable,
    bool PrimaryKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment = null);

/// <summary>This table's columns that reference another table, which is named schema.table.</summary>
public record ForeignKey(string[] Columns, string ReferencedTable, string[] ReferencedColumns);

/// <summary>Another table, named schema.table, whose columns reference this table's ReferencedColumns.</summary>
public record ReferencingKey(string Table, string[] Columns, string[] ReferencedColumns);

public record TableDescription(
    string Schema,
    string Name,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ApproxRows,
    ColumnInfo[] Columns,
    ForeignKey[] ForeignKeys,
    ReferencingKey[] ReferencedBy);
