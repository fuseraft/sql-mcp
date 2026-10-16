using SqlMcp.Models;
using SqlMcp.Services;

namespace SqlMcp.Tests;

public class SqlGuardTests
{
    private static readonly DbProvider[] AllProviders = Enum.GetValues<DbProvider>();

    public static TheoryData<string> AllowedEverywhere() =>
    [
        "SELECT 1",
        "  select id, name from users where id = 3",
        "SELECT 1;",
        "SELECT 1;   ",
        "SELECT 1; -- trailing comment",
        "SELECT 1; /* trailing comment */",
        "WITH t AS (SELECT 1 AS x) SELECT x FROM t",
        "SELECT updated_at, deleted_flag, insert_count, created_by FROM audit",
        "SELECT 'delete; drop table users' AS msg",
        "SELECT 'it''s' AS s",
        "SELECT \"update\" FROM t",
        "SELECT 1 -- DROP TABLE users\n",
        "SELECT /* DELETE FROM users; */ 1",
        "SELECT REPLACE(name, 'a', 'b') FROM t",
        "SELECT COUNT(*) FROM orders WHERE status IN ('set', 'into', 'exec')",
    ];

    [Theory]
    [MemberData(nameof(AllowedEverywhere))]
    public void Allows_plain_reads(string sql)
    {
        foreach (var provider in AllProviders)
            Assert.True(SqlGuard.Validate(sql, provider).IsAllowed, $"{provider}: {sql}");
    }

    public static TheoryData<string> DeniedEverywhere() =>
    [
        "",
        "   ",
        "-- just a comment",
        "DELETE FROM users",
        "UPDATE users SET admin = 1",
        "INSERT INTO users VALUES (1)",
        "DROP TABLE users",
        "  /* hi */ TRUNCATE TABLE users",
        "EXPLAIN ANALYZE DELETE FROM users",
        "SELECT 1; DROP TABLE users",
        "SELECT 1; SELECT 2",
        "SELECT 1;;",
        "SELECT * INTO backup FROM users",
        "SELECT * FROM users FOR UPDATE",
        "WITH d AS (DELETE FROM users RETURNING *) SELECT * FROM d",
        "WITH x AS (SELECT 1) UPDATE users SET a = 1",
        "SELECT 'unterminated",
        "SELECT \"unterminated",
        "SELECT 1 /* unterminated",
        "SELECT 1 GRANT ALL ON users TO public",
    ];

    [Theory]
    [MemberData(nameof(DeniedEverywhere))]
    public void Denies_writes_and_malformed_input(string sql)
    {
        foreach (var provider in AllProviders)
            Assert.False(SqlGuard.Validate(sql, provider).IsAllowed, $"{provider}: {sql}");
    }

    [Theory]
    [InlineData("SELECT 1", "SELECT 1")]
    [InlineData("SELECT 1;", "SELECT 1")]
    [InlineData("SELECT 1 ; -- done", "SELECT 1 ")]
    [InlineData("SELECT ';' AS s", "SELECT ';' AS s")]
    public void Strips_only_a_trailing_terminator(string sql, string expected)
    {
        foreach (var provider in AllProviders)
            Assert.Equal(expected, SqlGuard.Validate(sql, provider).Sql);
    }

    // SQL Server runs a batch without separators.
    [Theory]
    [InlineData("SELECT 1 EXEC xp_cmdshell 'whoami'")]
    [InlineData("SELECT 1 EXECUTE('DROP TABLE users')")]
    [InlineData("SELECT 1 DECLARE @x INT")]
    [InlineData("SELECT 1 COMMIT")]
    [InlineData("SELECT 1 WAITFOR DELAY '00:01:00'")]
    [InlineData("SELECT 1 SHUTDOWN")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [InlineData("SELECT 1 /* nested /* comment */ still comment */ DROP TABLE users")]
    public void SqlServer_denies_batch_tricks(string sql) =>
        Assert.False(SqlGuard.Validate(sql, DbProvider.SqlServer).IsAllowed);

    [Theory]
    [InlineData("SELECT [delete], [drop;table] FROM [my]]table]")]
    [InlineData("SELECT N'unicode; DELETE' AS s")]
    [InlineData("SELECT @@VERSION")]
    [InlineData("SELECT 1 /* outer /* inner */ DROP still inside */")]
    public void SqlServer_allows_its_quoting(string sql) =>
        Assert.True(SqlGuard.Validate(sql, DbProvider.SqlServer).IsAllowed);

    [Theory]
    [InlineData("SELECT $$ ; DROP TABLE users $$")]
    [InlineData("SELECT $tag$ it's ; DELETE $tag$")]
    [InlineData("SELECT $1, a$b FROM t")]
    [InlineData("SELECT E'it\\'s ; DELETE' AS s")]
    [InlineData("SELECT 1 /* outer /* inner */ DROP still inside */")]
    public void Postgres_allows_its_quoting(string sql) =>
        Assert.True(SqlGuard.Validate(sql, DbProvider.Postgres).IsAllowed);

    [Theory]
    // With standard_conforming_strings off, '\' does not end the literal.
    [InlineData("SELECT '\\'; DROP TABLE users; --'")]
    [InlineData("SELECT $x$ unterminated")]
    [InlineData("SELECT 1 /* nested /* comment */ DROP TABLE users")]
    public void Postgres_denies_ambiguous_or_unterminated(string sql) =>
        Assert.False(SqlGuard.Validate(sql, DbProvider.Postgres).IsAllowed);

    [Theory]
    [InlineData("SELECT `delete`, `a``b` FROM t")]
    [InlineData("SELECT \"a string; DELETE\" AS s")]
    [InlineData("SELECT 1 # DROP TABLE users")]
    [InlineData("SELECT 1--1")]
    [InlineData("SELECT 1 /*+ MAX_EXECUTION_TIME(1000) */")]
    public void MySql_allows_its_quoting(string sql) =>
        Assert.True(SqlGuard.Validate(sql, DbProvider.MySql).IsAllowed);

    [Theory]
    // MySQL only treats -- as a comment when followed by whitespace.
    [InlineData("SELECT 1 --x; DROP TABLE users")]
    // Backslash escapes depend on NO_BACKSLASH_ESCAPES; both readings must pass.
    [InlineData("SELECT '\\'; DROP TABLE users; -- '")]
    [InlineData("SELECT 'a\\' ; DELETE FROM users ; '")]
    [InlineData("SELECT 1 /*! ; DROP TABLE users */")]
    [InlineData("SELECT 1 /*!50000 DROP TABLE users */")]
    [InlineData("SELECT 1 /*M! DROP TABLE users */")]
    [InlineData("SELECT * FROM users INTO OUTFILE '/tmp/x'")]
    [InlineData("SELECT * FROM users LOCK IN SHARE MODE")]
    [InlineData("SELECT 1 /* non-nesting /* */ DROP TABLE users */")]
    public void MySql_denies_its_tricks(string sql) =>
        Assert.False(SqlGuard.Validate(sql, DbProvider.MySql).IsAllowed);

    [Theory]
    [InlineData("SELECT q'[it's ; DELETE]' FROM dual")]
    [InlineData("SELECT Q'{a}b}' FROM dual")]
    [InlineData("SELECT nq'!x ; DROP!' FROM dual")]
    [InlineData("SELECT q'<a>' AS s, 'b' FROM dual")]
    [InlineData("SELECT sys.dbms_random.value, my#col FROM dual")]
    public void Oracle_allows_its_quoting(string sql) =>
        Assert.True(SqlGuard.Validate(sql, DbProvider.Oracle).IsAllowed);

    [Theory]
    [InlineData("SELECT q'[ unterminated FROM dual")]
    [InlineData("SELECT q'[ x ]' ; DROP TABLE users")]
    [InlineData("SELECT 1 FROM dual /* non-nesting /* */ DROP TABLE users */")]
    public void Oracle_denies_its_tricks(string sql) =>
        Assert.False(SqlGuard.Validate(sql, DbProvider.Oracle).IsAllowed);

    [Fact]
    public void Error_names_the_denied_keyword()
    {
        var verdict = SqlGuard.Validate("SELECT * INTO t2 FROM t", DbProvider.Postgres);
        Assert.Equal("'INTO' is not permitted in a read-only query.", verdict.Error);
    }
}
