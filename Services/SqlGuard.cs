using SqlMcp.Models;

namespace SqlMcp.Services;

/// <summary>
/// Lexical gate for agent-supplied SQL. It strips comments, string literals and quoted
/// identifiers using each provider's own rules, then requires a single SELECT/WITH
/// statement containing no data-modifying keywords.
///
/// This is the first of three layers: QueryService also runs every query inside a
/// read-only transaction (where the provider supports one) that is always rolled back,
/// and LoginAudit refuses databases whose login could write.
/// </summary>
public static class SqlGuard
{
    // Postgres, MySQL and Oracle only run one statement per command, which the
    // semicolon rule enforces, so for them these words matter only inside a SELECT:
    // SELECT ... INTO, FOR UPDATE, LOCK IN SHARE MODE, and data-modifying CTEs.
    // SQL Server runs a whole batch with no separators ("SELECT 1 DROP TABLE t"), so
    // the T-SQL statements that write, run code or end our transaction are listed too.
    // Matched as whole tokens outside literals: updated_at or 'delete me' are fine.
    private static readonly HashSet<string> DeniedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "INTO", "LOCK",
        "CREATE", "ALTER", "DROP", "TRUNCATE",
        "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "DECLARE", "SET",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "BULK", "WRITETEXT", "UPDATETEXT",
        "WAITFOR", "SHUTDOWN", "KILL", "DBCC", "BACKUP", "RESTORE", "RECONFIGURE",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE"
    };

    // Functions a SELECT can call that advance sequences (not undone by rollback),
    // reach outside the database, read server files, or run SQL held in a string,
    // which this check can't see into. Also matched inside quoted identifiers,
    // since "dblink_exec"(...) is a valid call.
    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Sequences: Oracle seq.NEXTVAL, Postgres nextval()/setval()
        "NEXTVAL", "SETVAL",
        // Postgres
        "DBLINK", "DBLINK_EXEC", "DBLINK_CONNECT", "DBLINK_CONNECT_U", "DBLINK_SEND_QUERY",
        "LO_IMPORT", "LO_EXPORT", "PG_READ_FILE", "PG_READ_BINARY_FILE", "PG_LS_DIR", "PG_STAT_FILE",
        "PG_TERMINATE_BACKEND", "PG_CANCEL_BACKEND", "PG_RELOAD_CONF", "PG_ROTATE_LOGFILE",
        "PG_LOGICAL_EMIT_MESSAGE", "PG_NOTIFY", "QUERY_TO_XML", "QUERY_TO_XML_AND_XMLSCHEMA",
        // MySQL
        "LOAD_FILE", "SYS_EXEC", "SYS_EVAL",
        // Oracle
        "UTL_HTTP", "UTL_TCP", "UTL_SMTP", "UTL_MAIL", "UTL_FILE", "UTL_INADDR", "DBMS_LDAP",
        "HTTPURITYPE", "DBMS_PIPE", "DBMS_SCHEDULER", "DBMS_JOB", "DBMS_SQL", "DBMS_XMLGEN",
        "DBMS_XMLQUERY", "DBMS_AQ", "DBMS_LOCK",
        // SQL Server
        "OPENXML", "SP_EXECUTESQL"
    };

    public sealed record Verdict(string? Error, string Sql)
    {
        public bool IsAllowed => Error is null;
    }

    /// <summary>
    /// Validates <paramref name="sql"/> for <paramref name="provider"/>. On success the
    /// returned SQL has any single trailing semicolon removed (Oracle rejects it).
    /// </summary>
    public static Verdict Validate(string sql, DbProvider provider)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Deny("Query is empty.");

        // Where a server setting changes how backslashes inside literals are read
        // (MySQL's NO_BACKSLASH_ESCAPES, Postgres' standard_conforming_strings), the
        // query must pass under both readings so it can't hide text from this check.
        bool[] backslashModes = provider is DbProvider.MySql or DbProvider.Postgres
            ? [false, true]
            : [false];

        Verdict? verdict = null;
        foreach (var backslashEscapes in backslashModes)
        {
            verdict = Check(sql, provider, backslashEscapes);
            if (!verdict.IsAllowed)
                return verdict;
        }
        return verdict!;
    }

    private static Verdict Check(string sql, DbProvider provider, bool backslashEscapes)
    {
        List<string> words;
        List<string> identifiers;
        int? terminator;
        try
        {
            (words, identifiers, terminator) = Tokenize(sql, provider, backslashEscapes);
        }
        catch (FormatException ex)
        {
            return Deny(ex.Message);
        }

        if (words.Count == 0)
            return Deny("Query contains no statement.");

        var first = words[0];
        if (!first.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return Deny("Only SELECT queries (including CTEs) are permitted.");

        var denied = words.FirstOrDefault(w => DeniedKeywords.Contains(w) || DeniedFunctions.Contains(w))
            ?? identifiers.FirstOrDefault(DeniedFunctions.Contains);
        if (denied is not null)
            return Deny($"'{denied.ToUpperInvariant()}' is not permitted in a read-only query.");

        // SQL Server: NEXT VALUE FOR seq advances a sequence. NEXT alone is fine
        // (OFFSET ... FETCH NEXT n ROWS).
        for (int w = 0; w + 2 < words.Count; w++)
        {
            if (words[w].Equals("NEXT", StringComparison.OrdinalIgnoreCase)
                && words[w + 1].Equals("VALUE", StringComparison.OrdinalIgnoreCase)
                && words[w + 2].Equals("FOR", StringComparison.OrdinalIgnoreCase))
                return Deny("'NEXT VALUE FOR' is not permitted in a read-only query.");
        }

        return new(null, terminator is int t ? sql[..t] : sql);
    }

    /// <summary>
    /// Returns the bare words outside comments and quotes, the contents of quoted
    /// identifiers, and the index of a single trailing semicolon if present. Throws <see cref="FormatException"/> for a second
    /// statement, an unterminated literal or comment, or a construct that executes code.
    /// </summary>
    private static (List<string> Words, List<string> Identifiers, int? Terminator) Tokenize(string sql, DbProvider provider, bool backslashEscapes)
    {
        var words = new List<string>();
        var identifiers = new List<string>();
        int? terminator = null;
        int i = 0;
        int n = sql.Length;

        while (i < n)
        {
            char c = sql[i];
            char next = i + 1 < n ? sql[i + 1] : '\0';

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // -- line comment. MySQL only treats it as a comment when followed by
            // whitespace or end of input; otherwise "1--1" is arithmetic.
            if (c == '-' && next == '-'
                && (provider != DbProvider.MySql || i + 2 >= n || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2])))
            {
                i = SkipToEndOfLine(sql, i);
                continue;
            }

            if (c == '#' && provider == DbProvider.MySql)
            {
                i = SkipToEndOfLine(sql, i);
                continue;
            }

            if (c == '/' && next == '*')
            {
                // MySQL runs the contents of /*! ... */ (MariaDB also /*M! ... */) as SQL.
                if (provider == DbProvider.MySql && i + 2 < n
                    && (sql[i + 2] == '!' || (sql[i + 2] is 'M' or 'm' && i + 3 < n && sql[i + 3] == '!')))
                    throw new FormatException("MySQL executable comments (/*! */) are not permitted.");

                i = SkipBlockComment(sql, i, nests: provider is DbProvider.Postgres or DbProvider.SqlServer);
                continue;
            }

            // Anything but whitespace and comments after a semicolon is a second statement.
            if (terminator is not null)
                throw new FormatException("Only a single statement is permitted.");

            if (c == ';')
            {
                terminator = i;
                i++;
                continue;
            }

            // Oracle alternative quoting: q'[...]', Q'{...}', nq'<...>', q'!...!'
            if (provider == DbProvider.Oracle && TryOracleQuote(sql, i, out var afterQuote))
            {
                i = afterQuote;
                continue;
            }

            // Postgres U&"..." spells identifiers with escapes (U&"\0064blink_exec"),
            // which would hide a denied function name.
            if (provider == DbProvider.Postgres && (c is 'u' or 'U') && next == '&'
                && i + 2 < n && sql[i + 2] == '"' && !IsWordChar(Prev(sql, i)))
                throw new FormatException("Unicode-escaped identifiers (U&\"...\") are not permitted.");

            // Postgres E'...' always honours backslash escapes.
            if (provider == DbProvider.Postgres && (c is 'e' or 'E') && next == '\'' && !IsWordChar(Prev(sql, i)))
            {
                i = SkipQuoted(sql, i + 1, '\'', backslash: true);
                continue;
            }

            if (c == '\'')
            {
                i = SkipQuoted(sql, i, '\'', backslash: backslashEscapes);
                continue;
            }

            if (c == '"')
            {
                // An identifier everywhere except MySQL, where it is a string unless
                // ANSI_QUOTES is set; either way it ends at the same place for a given
                // backslash mode.
                int end = SkipQuoted(sql, i, '"', backslash: provider == DbProvider.MySql && backslashEscapes);
                identifiers.Add(Unquote(sql, i, end, '"'));
                i = end;
                continue;
            }

            if (c == '`' && provider == DbProvider.MySql)
            {
                int end = SkipQuoted(sql, i, '`', backslash: false);
                identifiers.Add(Unquote(sql, i, end, '`'));
                i = end;
                continue;
            }

            if (c == '[' && provider == DbProvider.SqlServer)
            {
                int end = SkipQuoted(sql, i, ']', backslash: false, open: '[');
                identifiers.Add(Unquote(sql, i, end, ']'));
                i = end;
                continue;
            }

            if (c == '$' && provider == DbProvider.Postgres && !IsWordChar(Prev(sql, i))
                && TryDollarQuote(sql, i, out var afterDollar))
            {
                i = afterDollar;
                continue;
            }

            if (IsWordStart(c))
            {
                int start = i;
                while (i < n && IsWordChar(sql[i]))
                    i++;
                words.Add(sql[start..i]);
                continue;
            }

            i++;
        }

        return (words, identifiers, terminator);
    }

    private static int SkipToEndOfLine(string sql, int i)
    {
        int nl = sql.IndexOfAny(['\n', '\r'], i);
        return nl < 0 ? sql.Length : nl;
    }

    private static int SkipBlockComment(string sql, int i, bool nests)
    {
        int depth = 0;
        while (i < sql.Length - 1)
        {
            if (sql[i] == '/' && sql[i + 1] == '*' && (depth == 0 || nests))
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && sql[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                    return i;
            }
            else
            {
                i++;
            }
        }
        throw new FormatException("Unterminated block comment.");
    }

    /// <summary>
    /// Skips a literal or quoted identifier starting at the opening delimiter at
    /// <paramref name="i"/>. A doubled closing delimiter is an escaped one.
    /// </summary>
    private static int SkipQuoted(string sql, int i, char close, bool backslash, char? open = null)
    {
        i++; // opening delimiter
        while (i < sql.Length)
        {
            char c = sql[i];
            if (backslash && c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        throw new FormatException($"Unterminated {(open ?? close)}...{close} literal or identifier.");
    }

    private static string Unquote(string sql, int start, int end, char close) =>
        sql[(start + 1)..(end - 1)].Replace($"{close}{close}", close.ToString());

    private static bool TryDollarQuote(string sql, int i, out int after)
    {
        after = i;
        int j = i + 1;
        if (j < sql.Length && (char.IsLetter(sql[j]) || sql[j] == '_'))
        {
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
                j++;
        }
        if (j >= sql.Length || sql[j] != '$')
            return false; // $1 parameter or a bare $

        var tag = sql[i..(j + 1)];
        int end = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
        if (end < 0)
            throw new FormatException($"Unterminated {tag} dollar-quoted string.");

        after = end + tag.Length;
        return true;
    }

    private static bool TryOracleQuote(string sql, int i, out int after)
    {
        after = i;
        int j = i;
        if (j < sql.Length && (sql[j] is 'n' or 'N'))
            j++;
        if (j + 2 >= sql.Length || (sql[j] is not ('q' or 'Q')) || sql[j + 1] != '\'' || IsWordChar(Prev(sql, i)))
            return false;

        char open = sql[j + 2];
        char close = open switch
        {
            '[' => ']',
            '{' => '}',
            '(' => ')',
            '<' => '>',
            _ => open
        };
        if (char.IsWhiteSpace(open))
            throw new FormatException("Invalid Oracle q-quote delimiter.");

        var terminator = $"{close}'";
        int end = sql.IndexOf(terminator, j + 3, StringComparison.Ordinal);
        if (end < 0)
            throw new FormatException("Unterminated Oracle q-quoted string.");

        after = end + terminator.Length;
        return true;
    }

    private static char Prev(string sql, int i) => i > 0 ? sql[i - 1] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    // $, # and @ appear inside identifiers in Postgres, Oracle and SQL Server.
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    private static Verdict Deny(string error) => new(error, string.Empty);
}
