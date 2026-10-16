using Oracle.ManagedDataAccess.Client;
using SqlMcp.Models;
using System.Data.Common;

namespace SqlMcp.Services;

/// <summary>
/// Catalog queries behind list_tables and describe_table. They are written here, not by
/// the agent, so they skip SqlGuard; names arrive only as parameters. Each provider's
/// catalog shows only what the login has some privilege on, so agents see what they
/// can read. System schemas are hidden unless asked for by name.
/// </summary>
public static class SchemaCatalog
{
    // Each returns schema_name, table_name, table_type ('table' or 'view') and is_system.
    private const string PostgresTables =
        """
        SELECT n.nspname AS schema_name, c.relname AS table_name,
               CASE WHEN c.relkind IN ('v', 'm') THEN 'view' ELSE 'table' END AS table_type,
               CASE WHEN n.nspname = 'information_schema' OR n.nspname LIKE 'pg\_%' THEN 1 ELSE 0 END AS is_system
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND NOT c.relispartition
          AND has_schema_privilege(n.oid, 'USAGE') AND has_any_column_privilege(c.oid, 'SELECT')
        """;

    private const string SqlServerTables =
        """
        SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name AS table_name,
               CASE o.type WHEN 'V' THEN 'view' ELSE 'table' END AS table_type,
               CASE WHEN o.is_ms_shipped = 1 OR SCHEMA_NAME(o.schema_id) IN ('sys', 'INFORMATION_SCHEMA') THEN 1 ELSE 0 END AS is_system
        FROM sys.objects o
        WHERE o.type IN ('U', 'V')
        """;

    private const string MySqlTables =
        """
        SELECT table_schema AS schema_name, table_name AS table_name,
               CASE WHEN table_type LIKE '%VIEW' THEN 'view' ELSE 'table' END AS table_type,
               CASE WHEN table_schema IN ('mysql', 'information_schema', 'performance_schema', 'sys') THEN 1 ELSE 0 END AS is_system
        FROM information_schema.tables
        """;

    private const string OracleTables =
        """
        SELECT o.owner AS schema_name, o.object_name AS table_name, LOWER(o.object_type) AS table_type,
               CASE WHEN u.oracle_maintained = 'Y' THEN 1 ELSE 0 END AS is_system
        FROM all_objects o JOIN all_users u ON u.username = o.owner
        WHERE o.object_type IN ('TABLE', 'VIEW') AND o.secondary = 'N' AND o.object_name NOT LIKE 'BIN$%'
        """;

    // Each returns name, data type, nullable (1/0) and primary key (1/0) for one table.
    private const string PostgresColumns =
        """
        SELECT a.attname, format_type(a.atttypid, a.atttypmod),
               CASE WHEN a.attnotnull THEN 0 ELSE 1 END,
               CASE WHEN EXISTS (SELECT 1 FROM pg_index i
                                 WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY (i.indkey)) THEN 1 ELSE 0 END
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @p_schema AND c.relname = @p_table AND a.attnum > 0 AND NOT a.attisdropped
          AND has_column_privilege(c.oid, a.attnum, 'SELECT')
        ORDER BY a.attnum
        """;

    private const string SqlServerColumns =
        """
        SELECT c.name,
               t.name + CASE
                   WHEN t.name IN ('varchar', 'char', 'varbinary', 'binary')
                       THEN '(' + CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                   WHEN t.name IN ('nvarchar', 'nchar')
                       THEN '(' + CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                   WHEN t.name IN ('decimal', 'numeric')
                       THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                   ELSE '' END,
               CASE WHEN c.is_nullable = 1 THEN 1 ELSE 0 END,
               CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic
                                 JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                 WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                    THEN 1 ELSE 0 END
        FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(QUOTENAME(@p_schema) + '.' + QUOTENAME(@p_table))
        ORDER BY c.column_id
        """;

    private const string MySqlColumns =
        """
        SELECT column_name, column_type,
               CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END,
               CASE WHEN column_key = 'PRI' THEN 1 ELSE 0 END
        FROM information_schema.columns
        WHERE table_schema = @p_schema AND table_name = @p_table
        ORDER BY ordinal_position
        """;

    private const string OracleColumns =
        """
        SELECT c.column_name,
               CASE
                   WHEN c.data_type IN ('VARCHAR2', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN c.data_type || '(' || c.char_length || ')'
                   WHEN c.data_type = 'RAW' THEN c.data_type || '(' || c.data_length || ')'
                   WHEN c.data_type = 'NUMBER' AND c.data_precision IS NOT NULL
                       THEN 'NUMBER(' || c.data_precision || ',' || c.data_scale || ')'
                   WHEN c.data_type = 'NUMBER' AND c.data_scale = 0 THEN 'INTEGER'
                   ELSE c.data_type END,
               CASE WHEN c.nullable = 'Y' THEN 1 ELSE 0 END,
               CASE WHEN EXISTS (SELECT 1 FROM all_constraints k
                                 JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
                                 WHERE k.constraint_type = 'P' AND k.owner = c.owner AND k.table_name = c.table_name
                                   AND cc.column_name = c.column_name) THEN 1 ELSE 0 END
        FROM all_tab_columns c
        WHERE c.owner = :p_schema AND c.table_name = :p_table
        ORDER BY c.column_id
        """;

    /// <summary>
    /// Lists tables and views, filtered case-insensitively by schema and/or name when
    /// given. Without a schema, system schemas are left out. Reads up to limit + 1 rows
    /// so callers can tell when the list was cut short.
    /// </summary>
    public static async Task<List<TableInfo>> FindTablesAsync(
        Func<DbCommand> newCommand, DbProvider provider, string? schema, string? table, int limit, CancellationToken ct)
    {
        var (source, p) = provider switch
        {
            DbProvider.Postgres => (PostgresTables, "@"),
            DbProvider.SqlServer => (SqlServerTables, "@"),
            DbProvider.MySql => (MySqlTables, "@"),
            DbProvider.Oracle => (OracleTables, ":"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        await using var cmd = newCommand();
        cmd.CommandText =
            $"""
            SELECT schema_name, table_name, table_type FROM ({source}) t
            WHERE (({p}p_any_schema = 1 AND is_system = 0) OR LOWER(schema_name) = LOWER({p}p_schema))
              AND ({p}p_any_table = 1 OR LOWER(table_name) = LOWER({p}p_table))
            ORDER BY schema_name, table_name
            """;
        AddParameter(cmd, provider, "p_any_schema", schema is null ? 1 : 0);
        AddParameter(cmd, provider, "p_schema", schema ?? "");
        AddParameter(cmd, provider, "p_any_table", table is null ? 1 : 0);
        AddParameter(cmd, provider, "p_table", table ?? "");

        var tables = new List<TableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (tables.Count <= limit && await reader.ReadAsync(ct))
            tables.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return tables;
    }

    /// <summary>Returns the columns of a table found by FindTablesAsync, in order.</summary>
    public static async Task<ColumnInfo[]> GetColumnsAsync(
        Func<DbCommand> newCommand, DbProvider provider, TableInfo table, CancellationToken ct)
    {
        await using var cmd = newCommand();
        cmd.CommandText = provider switch
        {
            DbProvider.Postgres => PostgresColumns,
            DbProvider.SqlServer => SqlServerColumns,
            DbProvider.MySql => MySqlColumns,
            DbProvider.Oracle => OracleColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        AddParameter(cmd, provider, "p_schema", table.Schema);
        AddParameter(cmd, provider, "p_table", table.Name);

        var columns = new List<ColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2)) == 1,
                Convert.ToInt32(reader.GetValue(3)) == 1));
        }
        return [.. columns];
    }

    private static void AddParameter(DbCommand cmd, DbProvider provider, string name, object value)
    {
        if (cmd is OracleCommand oracle)
            oracle.BindByName = true;

        var parameter = cmd.CreateParameter();
        parameter.ParameterName = provider == DbProvider.Oracle ? name : "@" + name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}
