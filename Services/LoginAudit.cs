using SqlMcp.Models;
using System.Data.Common;

namespace SqlMcp.Services;

/// <summary>
/// Inspects what the configured login is allowed to do. SqlGuard and the read-only
/// transaction can't see inside functions, so a login that can write could still be
/// made to write: an Oracle function with an autonomous transaction, a SQL Server
/// sequence, a Postgres dblink back into the same database. QueryService refuses a
/// database whose login has any finding here, unless its config opts out.
/// </summary>
public static class LoginAudit
{
    private sealed record Check(string Finding, string Sql);

    private static readonly Check[] Postgres =
    [
        new("has role attributes that allow administration",
            """
            SELECT a FROM pg_roles r,
                LATERAL (VALUES (CASE WHEN r.rolsuper THEN 'SUPERUSER' END),
                                (CASE WHEN r.rolcreaterole THEN 'CREATEROLE' END),
                                (CASE WHEN r.rolcreatedb THEN 'CREATEDB' END)) v(a)
            WHERE r.rolname = current_user AND a IS NOT NULL
            """),
        new("is a member of roles that write data, touch server files or signal other sessions",
            """
            SELECT rolname FROM pg_roles
            WHERE rolname IN ('pg_write_all_data', 'pg_write_server_files', 'pg_read_server_files',
                              'pg_execute_server_program', 'pg_signal_backend')
              AND pg_has_role(current_user, oid, 'USAGE')
            ORDER BY 1
            """),
        new("can modify tables",
            """
            SELECT format('%I.%I', n.nspname, c.relname)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'f')
              AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
              AND (has_table_privilege(c.oid, 'INSERT, UPDATE, DELETE, TRUNCATE')
                   OR has_any_column_privilege(c.oid, 'INSERT, UPDATE'))
            ORDER BY 1 LIMIT 5
            """),
        new("can run functions that reach outside the database (dblink, untrusted languages)",
            """
            SELECT p.oid::regprocedure::text
            FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang
            WHERE (p.proname LIKE 'dblink%' OR (l.lanispl AND NOT l.lanpltrusted))
              AND has_function_privilege(p.oid, 'EXECUTE')
            ORDER BY 1 LIMIT 5
            """),
    ];

    private static readonly Check[] SqlServer =
    [
        new("has server-level administrative rights",
            """
            SELECT 'sysadmin' WHERE IS_SRVROLEMEMBER('sysadmin') = 1
            UNION ALL SELECT 'CONTROL SERVER' WHERE HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER') = 1
            """),
        new("has database-level permissions that modify data or schema",
            """
            SELECT permission_name FROM fn_my_permissions(NULL, 'DATABASE')
            WHERE permission_name IN ('INSERT', 'UPDATE', 'DELETE', 'ALTER', 'CONTROL', 'TAKE OWNERSHIP')
               OR permission_name LIKE 'CREATE %' OR permission_name LIKE 'ALTER ANY %'
            ORDER BY 1
            """),
        new("can modify tables, views or sequences",
            """
            SELECT TOP 5 n.name FROM sys.objects o
            CROSS APPLY (SELECT QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name) AS name) n
            WHERE o.type IN ('U', 'V', 'SO') AND o.is_ms_shipped = 0
              AND (HAS_PERMS_BY_NAME(n.name, 'OBJECT', 'INSERT') = 1
                   OR HAS_PERMS_BY_NAME(n.name, 'OBJECT', 'UPDATE') = 1
                   OR HAS_PERMS_BY_NAME(n.name, 'OBJECT', 'DELETE') = 1
                   OR HAS_PERMS_BY_NAME(n.name, 'OBJECT', 'ALTER') = 1)
            ORDER BY 1
            """),
    ];

    private static readonly Check[] Oracle =
    [
        new("has system privileges beyond reading",
            """
            SELECT privilege FROM session_privs
            WHERE privilege NOT IN ('CREATE SESSION', 'SET CONTAINER', 'SELECT ANY TABLE', 'READ ANY TABLE',
                                    'SELECT ANY DICTIONARY')
            ORDER BY 1
            """),
        new("has object privileges beyond reading",
            """
            SELECT DISTINCT privilege || ' ON ' || table_schema || '.' || table_name
            FROM all_tab_privs
            WHERE grantee IN (SELECT USER FROM dual UNION ALL SELECT role FROM session_roles)
              AND privilege NOT IN ('SELECT', 'READ', 'REFERENCES', 'DEBUG', 'FLASHBACK',
                                    'QUERY REWRITE', 'ON COMMIT REFRESH')
            ORDER BY 1 FETCH FIRST 5 ROWS ONLY
            """),
        new("owns objects it can modify or code it can run",
            """
            SELECT object_type || ' ' || object_name FROM user_objects
            WHERE object_type IN ('TABLE', 'SEQUENCE', 'PROCEDURE', 'FUNCTION', 'PACKAGE', 'TYPE', 'TRIGGER')
              AND object_name NOT LIKE 'BIN$%'
            ORDER BY 1 FETCH FIRST 5 ROWS ONLY
            """),
        new("can reach other databases through database links",
            "SELECT owner || '.' || db_link FROM all_db_links ORDER BY 1 FETCH FIRST 5 ROWS ONLY"),
    ];

    // Privileges that only read or show metadata. Everything else MySQL can grant
    // (INSERT, FILE, SUPER, ALL PRIVILEGES, dynamic admin privileges, ...) is a finding.
    private static readonly HashSet<string> MySqlReadPrivileges = new(StringComparer.OrdinalIgnoreCase)
    {
        "USAGE", "SELECT", "SHOW VIEW", "SHOW DATABASES", "PROCESS", "REPLICATION CLIENT",
        "EXECUTE", "SHOW_ROUTINE"
    };

    /// <summary>
    /// Returns one line per way the current login could change data or reach outside
    /// the database; empty means read-only. An audit query that fails is a finding,
    /// since a login that can't be verified isn't trusted.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindWriteAccessAsync(DbConnection conn, DbProvider provider, CancellationToken ct)
    {
        if (provider == DbProvider.MySql)
            return await AuditMySqlAsync(conn, ct);

        var checks = provider switch
        {
            DbProvider.Postgres => Postgres,
            DbProvider.SqlServer => SqlServer,
            DbProvider.Oracle => Oracle,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        var findings = new List<string>();
        foreach (var check in checks)
        {
            try
            {
                var items = await ListAsync(conn, check.Sql, ct);
                if (items.Count > 0)
                    findings.Add($"{check.Finding}: {string.Join(", ", items)}");
            }
            catch (DbException ex)
            {
                findings.Add($"could not check whether it {check.Finding} ({ex.Message})");
            }
        }
        return findings;
    }

    private static async Task<IReadOnlyList<string>> AuditMySqlAsync(DbConnection conn, CancellationToken ct)
    {
        List<string> grants;
        try
        {
            grants = await ListAsync(conn, "SHOW GRANTS", ct);

            // Role grants ("GRANT `r`@`%` TO ...") have no ON clause; expand them.
            var roles = grants
                .Where(g => !g.Contains(" ON ", StringComparison.Ordinal))
                .Select(g => g["GRANT ".Length..g.LastIndexOf(" TO ", StringComparison.Ordinal)])
                .ToList();
            if (roles.Count > 0)
                grants = await ListAsync(conn, $"SHOW GRANTS FOR CURRENT_USER() USING {string.Join(", ", roles)}", ct);
        }
        catch (DbException ex)
        {
            return [$"could not read its grants ({ex.Message})"];
        }

        var excess = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in grants)
        {
            int on = grant.IndexOf(" ON ", StringComparison.Ordinal);
            if (!grant.StartsWith("GRANT ", StringComparison.Ordinal) || on < 0)
                continue;

            foreach (var privilege in SplitPrivileges(grant["GRANT ".Length..on]))
            {
                if (!MySqlReadPrivileges.Contains(privilege))
                    excess.Add(privilege);
            }
        }

        return excess.Count == 0
            ? []
            : [$"has MySQL privileges beyond reading: {string.Join(", ", excess.Take(8))}{(excess.Count > 8 ? ", ..." : "")}"];
    }

    /// <summary>Splits "SELECT, INSERT (a, b), SHOW VIEW" into names, dropping column lists.</summary>
    private static IEnumerable<string> SplitPrivileges(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i <= list.Length; i++)
        {
            if (i < list.Length && list[i] == '(') depth++;
            else if (i < list.Length && list[i] == ')') depth--;
            else if (i == list.Length || (list[i] == ',' && depth == 0))
            {
                var name = list[start..i];
                int paren = name.IndexOf('(');
                if (paren >= 0)
                    name = name[..paren];
                name = name.Trim();
                if (name.Length > 0)
                    yield return name;
                start = i + 1;
            }
        }
    }

    private static async Task<List<string>> ListAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<string>();
        while (await reader.ReadAsync(ct))
            items.Add(Convert.ToString(reader.GetValue(0))!);
        return items;
    }
}
