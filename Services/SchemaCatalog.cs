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
    // Each returns schema_name, table_name, table_type ('table' or 'view'), is_system,
    // table_comment and approx_rows: the planner's estimate, null for views and for
    // tables whose statistics were never gathered.
    private const string PostgresTables =
        """
        SELECT n.nspname AS schema_name, c.relname AS table_name,
               CASE WHEN c.relkind IN ('v', 'm') THEN 'view' ELSE 'table' END AS table_type,
               CASE WHEN n.nspname = 'information_schema' OR n.nspname LIKE 'pg\_%' THEN 1 ELSE 0 END AS is_system,
               obj_description(c.oid, 'pg_class') AS table_comment,
               CASE WHEN c.relkind IN ('r', 'p', 'm') AND c.reltuples >= 0 THEN CAST(c.reltuples AS bigint) END AS approx_rows
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f') AND NOT c.relispartition
          AND has_schema_privilege(n.oid, 'USAGE') AND has_any_column_privilege(c.oid, 'SELECT')
        """;

    private const string SqlServerTables =
        """
        SELECT SCHEMA_NAME(o.schema_id) AS schema_name, o.name AS table_name,
               CASE o.type WHEN 'V' THEN 'view' ELSE 'table' END AS table_type,
               CASE WHEN o.is_ms_shipped = 1 OR SCHEMA_NAME(o.schema_id) IN ('sys', 'INFORMATION_SCHEMA') THEN 1 ELSE 0 END AS is_system,
               (SELECT CAST(ep.value AS nvarchar(4000)) FROM sys.extended_properties ep
                WHERE ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description') AS table_comment,
               (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS approx_rows
        FROM sys.objects o
        WHERE o.type IN ('U', 'V')
        """;

    private const string MySqlTables =
        """
        SELECT table_schema AS schema_name, table_name AS table_name,
               CASE WHEN table_type LIKE '%VIEW' THEN 'view' ELSE 'table' END AS table_type,
               CASE WHEN table_schema IN ('mysql', 'information_schema', 'performance_schema', 'sys') THEN 1 ELSE 0 END AS is_system,
               CASE WHEN table_type LIKE '%VIEW' THEN NULL ELSE NULLIF(table_comment, '') END AS table_comment,
               CASE WHEN table_type LIKE '%VIEW' THEN NULL ELSE table_rows END AS approx_rows
        FROM information_schema.tables
        """;

    private const string OracleTables =
        """
        SELECT o.owner AS schema_name, o.object_name AS table_name, LOWER(o.object_type) AS table_type,
               CASE WHEN u.oracle_maintained = 'Y' THEN 1 ELSE 0 END AS is_system,
               tc.comments AS table_comment, t.num_rows AS approx_rows
        FROM all_objects o JOIN all_users u ON u.username = o.owner
        LEFT JOIN all_tab_comments tc ON tc.owner = o.owner AND tc.table_name = o.object_name
        LEFT JOIN all_tables t ON t.owner = o.owner AND t.table_name = o.object_name
        WHERE o.object_type IN ('TABLE', 'VIEW') AND o.secondary = 'N' AND o.object_name NOT LIKE 'BIN$%'
        """;

    // Each returns name, data type, nullable (1/0), primary key (1/0) and comment for one table.
    private const string PostgresColumns =
        """
        SELECT a.attname, format_type(a.atttypid, a.atttypmod),
               CASE WHEN a.attnotnull THEN 0 ELSE 1 END,
               CASE WHEN EXISTS (SELECT 1 FROM pg_index i
                                 WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY (i.indkey)) THEN 1 ELSE 0 END,
               col_description(c.oid, a.attnum)
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
                    THEN 1 ELSE 0 END,
               (SELECT CAST(ep.value AS nvarchar(4000)) FROM sys.extended_properties ep
                WHERE ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description')
        FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(QUOTENAME(@p_schema) + '.' + QUOTENAME(@p_table))
        ORDER BY c.column_id
        """;

    private const string MySqlColumns =
        """
        SELECT column_name, column_type,
               CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END,
               CASE WHEN column_key = 'PRI' THEN 1 ELSE 0 END,
               NULLIF(column_comment, '')
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
                                   AND cc.column_name = c.column_name) THEN 1 ELSE 0 END,
               (SELECT cc.comments FROM all_col_comments cc
                WHERE cc.owner = c.owner AND cc.table_name = c.table_name AND cc.column_name = c.column_name)
        FROM all_tab_columns c
        WHERE c.owner = :p_schema AND c.table_name = :p_table
        ORDER BY c.column_id
        """;

    // Each returns, for one table, a row per column of each foreign key that touches it:
    // direction (1 when this table holds the key, 0 when another table's key references
    // it), constraint name, the other table's schema and name, this table's column, the
    // other table's column, and the column's position in the key. A key from a table to
    // itself is returned once, as direction 1. Keys whose other table the login can't
    // read are left out.
    private const string PostgresForeignKeys =
        """
        SELECT CASE WHEN k.conrelid = t.oid THEN 1 ELSE 0 END, k.conname, n2.nspname, c2.relname, a1.attname, a2.attname, x.ord
        FROM pg_class t
        JOIN pg_namespace tn ON tn.oid = t.relnamespace
        JOIN pg_constraint k ON k.contype = 'f' AND k.conparentid = 0 AND (k.conrelid = t.oid OR k.confrelid = t.oid)
        CROSS JOIN LATERAL unnest(k.conkey, k.confkey) WITH ORDINALITY AS x(fk_att, ref_att, ord)
        JOIN pg_class c2 ON c2.oid = CASE WHEN k.conrelid = t.oid THEN k.confrelid ELSE k.conrelid END
        JOIN pg_namespace n2 ON n2.oid = c2.relnamespace
        JOIN pg_attribute a1 ON a1.attrelid = t.oid AND a1.attnum = CASE WHEN k.conrelid = t.oid THEN x.fk_att ELSE x.ref_att END
        JOIN pg_attribute a2 ON a2.attrelid = c2.oid AND a2.attnum = CASE WHEN k.conrelid = t.oid THEN x.ref_att ELSE x.fk_att END
        WHERE tn.nspname = @p_schema AND t.relname = @p_table
          AND has_schema_privilege(n2.oid, 'USAGE') AND has_any_column_privilege(c2.oid, 'SELECT')
        ORDER BY 1 DESC, 3, 4, 2, 7
        """;

    private const string SqlServerForeignKeys =
        """
        SELECT CASE WHEN fk.parent_object_id = t.object_id THEN 1 ELSE 0 END, fk.name,
               SCHEMA_NAME(o2.schema_id), o2.name, c1.name, c2.name, fkc.constraint_column_id
        FROM (SELECT OBJECT_ID(QUOTENAME(@p_schema) + '.' + QUOTENAME(@p_table)) AS object_id) t
        JOIN sys.foreign_keys fk ON fk.parent_object_id = t.object_id OR fk.referenced_object_id = t.object_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.objects o2 ON o2.object_id = CASE WHEN fk.parent_object_id = t.object_id THEN fk.referenced_object_id ELSE fk.parent_object_id END
        JOIN sys.columns c1 ON c1.object_id = t.object_id
             AND c1.column_id = CASE WHEN fk.parent_object_id = t.object_id THEN fkc.parent_column_id ELSE fkc.referenced_column_id END
        JOIN sys.columns c2 ON c2.object_id = o2.object_id
             AND c2.column_id = CASE WHEN fk.parent_object_id = t.object_id THEN fkc.referenced_column_id ELSE fkc.parent_column_id END
        WHERE HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o2.schema_id)) + '.' + QUOTENAME(o2.name), 'OBJECT', 'SELECT') = 1
        ORDER BY 1 DESC, 3, 4, 2, 7
        """;

    // information_schema shows a key only to a login with privileges on the table that
    // holds it, so only the referenced side needs checking.
    private const string MySqlForeignKeys =
        """
        SELECT 1, k.constraint_name, k.referenced_table_schema, k.referenced_table_name,
               k.column_name, k.referenced_column_name, k.ordinal_position
        FROM information_schema.key_column_usage k
        WHERE k.table_schema = @p_schema AND k.table_name = @p_table AND k.referenced_table_name IS NOT NULL
          AND EXISTS (SELECT 1 FROM information_schema.tables r
                      WHERE r.table_schema = k.referenced_table_schema AND r.table_name = k.referenced_table_name)
        UNION ALL
        SELECT 0, k.constraint_name, k.table_schema, k.table_name, k.referenced_column_name, k.column_name, k.ordinal_position
        FROM information_schema.key_column_usage k
        WHERE k.referenced_table_schema = @p_schema AND k.referenced_table_name = @p_table
          AND NOT (k.table_schema = @p_schema AND k.table_name = @p_table)
        ORDER BY 1 DESC, 3, 4, 2, 7
        """;

    // all_constraints shows only constraints on tables the login can access, so joining
    // both ends leaves out keys whose other table it can't read.
    private const string OracleForeignKeys =
        """
        SELECT 1, k.constraint_name, r.owner, r.table_name, kc.column_name, rc.column_name, kc.position
        FROM all_constraints k
        JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
        JOIN all_constraints r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
        JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = kc.position
        WHERE k.constraint_type = 'R' AND k.owner = :p_schema AND k.table_name = :p_table
        UNION ALL
        SELECT 0, k.constraint_name, k.owner, k.table_name, rc.column_name, kc.column_name, kc.position
        FROM all_constraints k
        JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
        JOIN all_constraints r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
        JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = kc.position
        WHERE k.constraint_type = 'R' AND r.owner = :p_schema AND r.table_name = :p_table
          AND NOT (k.owner = r.owner AND k.table_name = r.table_name)
        ORDER BY 1 DESC, 3, 4, 2, 7
        """;

    /// <summary>
    /// Lists tables and views, filtered case-insensitively by schema and/or name when
    /// given, and by search: text the name or comment contains. Without a schema, system
    /// schemas are left out. Reads up to limit + 1 rows so callers can tell when the list
    /// was cut short.
    /// </summary>
    public static async Task<List<TableInfo>> FindTablesAsync(
        Func<DbCommand> newCommand, DbProvider provider, string? schema, string? table, int limit, CancellationToken ct, string? search = null)
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
            SELECT schema_name, table_name, table_type, table_comment, approx_rows FROM ({source}) t
            WHERE (({p}p_any_schema = 1 AND is_system = 0) OR LOWER(schema_name) = LOWER({p}p_schema))
              AND ({p}p_any_table = 1 OR LOWER(table_name) = LOWER({p}p_table))
              AND ({p}p_any_search = 1 OR LOWER(table_name) LIKE {p}p_search ESCAPE '!' OR LOWER(table_comment) LIKE {p}p_search ESCAPE '!')
            ORDER BY schema_name, table_name
            """;
        QueryParameters.Add(cmd, provider, "p_any_schema", schema is null ? 1 : 0);
        QueryParameters.Add(cmd, provider, "p_schema", schema ?? "");
        QueryParameters.Add(cmd, provider, "p_any_table", table is null ? 1 : 0);
        QueryParameters.Add(cmd, provider, "p_table", table ?? "");
        QueryParameters.Add(cmd, provider, "p_any_search", search is null ? 1 : 0);
        QueryParameters.Add(cmd, provider, "p_search", search is null ? "" : ContainsPattern(search, provider));

        var tables = new List<TableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (tables.Count <= limit && await reader.ReadAsync(ct))
            tables.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return tables;
    }

    // A LIKE pattern matching text anywhere, with the text's own wildcards escaped by '!'
    // (a backslash means different things depending on server settings). '[' is a
    // wildcard only on SQL Server, and Oracle refuses an escape before anything else.
    private static string ContainsPattern(string text, DbProvider provider)
    {
        var escaped = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is '!' or '%' or '_' || (c == '[' && provider == DbProvider.SqlServer))
                escaped.Append('!');
            escaped.Append(c);
        }
        return $"%{escaped}%";
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
        QueryParameters.Add(cmd, provider, "p_schema", table.Schema);
        QueryParameters.Add(cmd, provider, "p_table", table.Name);

        var columns = new List<ColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2)) == 1,
                Convert.ToInt32(reader.GetValue(3)) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return [.. columns];
    }

    /// <summary>
    /// Returns the foreign keys a table holds, and those of other tables that reference
    /// it, keeping only keys whose other table the login can read.
    /// </summary>
    public static async Task<(ForeignKey[] Outgoing, ReferencingKey[] Incoming)> GetForeignKeysAsync(
        Func<DbCommand> newCommand, DbProvider provider, TableInfo table, CancellationToken ct)
    {
        await using var cmd = newCommand();
        cmd.CommandText = provider switch
        {
            DbProvider.Postgres => PostgresForeignKeys,
            DbProvider.SqlServer => SqlServerForeignKeys,
            DbProvider.MySql => MySqlForeignKeys,
            DbProvider.Oracle => OracleForeignKeys,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        QueryParameters.Add(cmd, provider, "p_schema", table.Schema);
        QueryParameters.Add(cmd, provider, "p_table", table.Name);

        // Rows arrive ordered by direction, other table, constraint and position; group them into keys.
        var keys = new List<(bool Outgoing, string Other, List<string> Columns, List<string> OtherColumns)>();
        string? constraint = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var outgoing = Convert.ToInt32(reader.GetValue(0)) == 1;
            var name = $"{outgoing}:{reader.GetString(2)}.{reader.GetString(3)}:{reader.GetString(1)}";
            if (name != constraint)
            {
                keys.Add((outgoing, $"{reader.GetString(2)}.{reader.GetString(3)}", [], []));
                constraint = name;
            }
            keys[^1].Columns.Add(reader.GetString(4));
            keys[^1].OtherColumns.Add(reader.GetString(5));
        }

        return (
            [.. keys.Where(k => k.Outgoing).Select(k => new ForeignKey([.. k.Columns], k.Other, [.. k.OtherColumns]))],
            [.. keys.Where(k => !k.Outgoing).Select(k => new ReferencingKey(k.Other, [.. k.OtherColumns], [.. k.Columns]))]);
    }
}
