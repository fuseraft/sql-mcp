using Microsoft.Extensions.Configuration;
using SqlMcp.Services;
using System.Collections;
using System.Net;
using System.Numerics;

namespace SqlMcp.Tests;

public class QueryServiceConfigTests
{
    private static QueryService Create(params (string Key, string? Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    private static (string, string?)[] Db(int i, string name, string provider = "Postgres", string cs = "Host=localhost;Username=agent") =>
        [($"Databases:{i}:Name", name), ($"Databases:{i}:Provider", provider), ($"Databases:{i}:ConnectionString", cs)];

    [Fact]
    public void Accepts_a_valid_config() =>
        Create([.. Db(0, "a"), .. Db(1, "b", "SqlServer", "Server=localhost;User Id=agent;Password={password}"), ("Databases:1:PasswordEnvVar", "SQLMCP_TEST_UNSET_VAR")]);

    [Theory]
    [InlineData("App:MaxRows", "0", "App:MaxRows must be at least 1")]
    [InlineData("App:MaxTables", "0", "App:MaxTables must be at least 1")]
    [InlineData("App:QueryTimeoutSeconds", "0", "App:QueryTimeoutSeconds must be at least 1")]
    [InlineData("App:MaxConcurrentQueries", "-1", "App:MaxConcurrentQueries must be at least 1")]
    [InlineData("App:MaxValueLength", "lots", "App:MaxValueLength must be a whole number")]
    public void Rejects_bad_limits(string key, string value, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create([.. Db(0, "a"), (key, value)]));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Lists_each_database_with_its_provider()
    {
        var svc = Create([.. Db(0, "prod"), .. Db(1, "legacy", "SqlServer", "Server=localhost;User Id=agent")]);

        Assert.Equal([new("prod", "Postgres"), new("legacy", "SqlServer")], svc.ListDatabases());
    }

    [Fact]
    public void Lists_every_database_problem_at_once()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(
        [
            .. Db(0, "prod"),
            .. Db(1, "PROD"),
            .. Db(2, ""),
            .. Db(3, "empty", cs: ""),
            .. Db(4, "garbled", cs: "Hots=localhost"),
            .. Db(5, "noplaceholder"), ("Databases:5:PasswordEnvVar", "PW"),
        ]));

        Assert.Contains("Database 'PROD' is configured more than once", ex.Message);
        Assert.Contains("Databases:2 has no Name", ex.Message);
        Assert.Contains("Database 'empty' has no ConnectionString", ex.Message);
        Assert.Contains("Database 'garbled' has an invalid Postgres ConnectionString", ex.Message);
        Assert.Contains("Database 'noplaceholder' sets PasswordEnvVar but its ConnectionString has no {password} placeholder", ex.Message);
    }

    [Theory]
    [InlineData("Sqlite", "Database 'a' has unknown Provider 'Sqlite'; use one of SqlServer, Oracle, Postgres, MySql.")]
    [InlineData("1", "Database 'a' has unknown Provider '1'")]
    [InlineData("", "Database 'a' has no Provider")]
    public void Rejects_a_missing_or_unknown_provider(string provider, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(Db(0, "a", provider)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Accepts_a_provider_in_any_case() =>
        Create(Db(0, "a", "postgres"));

    [Fact]
    public async Task A_missing_password_variable_disables_only_its_database()
    {
        var svc = Create([.. Db(0, "a", cs: "Host=localhost;Username=agent;Password={password}"), ("Databases:0:PasswordEnvVar", "SQLMCP_TEST_UNSET_VAR")]);

        var result = await svc.ExecuteAsync(new("a", "SELECT 1"));

        Assert.Equal("Database 'a' is unavailable: environment variable SQLMCP_TEST_UNSET_VAR, which holds its password, is not set.", result.Error);
    }

    [Fact]
    public async Task Matches_database_names_ignoring_case()
    {
        var svc = Create([.. Db(0, "Reporting", cs: "Host=localhost;Username=agent;Password={password}"), ("Databases:0:PasswordEnvVar", "SQLMCP_TEST_UNSET_VAR")]);

        var result = await svc.ExecuteAsync(new("reporting", "SELECT 1"));

        Assert.StartsWith("Database 'Reporting' is unavailable", result.Error);
    }

    [Theory]
    [InlineData(new[] { "id", "name" }, new[] { "id", "name" })]
    [InlineData(new[] { "id", "id", "ID" }, new[] { "id", "id_2", "ID_3" })]
    [InlineData(new[] { "", "?column?", "column1" }, new[] { "column1", "column2", "column1_2" })]
    [InlineData(new[] { "a", "a_2", "a" }, new[] { "a", "a_2", "a_3" })]
    public void Makes_column_names_unique(string[] names, string[] expected) =>
        Assert.Equal(expected, QueryService.UniqueColumnNames(names));

    [Fact]
    public void Normalizes_values_json_cannot_represent()
    {
        Assert.Equal("NaN", ResultValues.Normalize(double.NaN));
        Assert.Equal("-Infinity", ResultValues.Normalize(float.NegativeInfinity));
        Assert.Equal(1.5, ResultValues.Normalize(1.5));
        Assert.Equal("123456789012345678901234567890", ResultValues.Normalize(BigInteger.Parse("123456789012345678901234567890")));
        Assert.Equal("10.0.0.1", ResultValues.Normalize(IPAddress.Parse("10.0.0.1")));
        Assert.Equal("101", ResultValues.Normalize(new BitArray(new[] { true, false, true })));
        Assert.Equal(new List<object?> { 1, "NaN", null }, ResultValues.Normalize(new object?[] { 1, double.NaN, DBNull.Value }));
        Assert.Equal(new Dictionary<string, object?> { ["k"] = "v" }, ResultValues.Normalize(new Hashtable { ["k"] = "v" }));
        Assert.Equal("text", ResultValues.Normalize("text"));
        Assert.Equal(new byte[] { 1, 2 }, ResultValues.Normalize(new byte[] { 1, 2 }));
    }
}
