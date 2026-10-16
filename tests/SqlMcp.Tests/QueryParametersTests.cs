using SqlMcp.Services;
using System.Text.Json;

namespace SqlMcp.Tests;

public class QueryParametersTests
{
    private static Dictionary<string, JsonElement> Json(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Converts_json_values()
    {
        var (parameters, error) = QueryParameters.Parse(Json(
            """{"s": "O'Brien", "i": 42, "big": 9007199254740993, "d": 2.5, "e": 1e400, "t": true, "f": false, "n": null}"""));

        Assert.Null(error);
        var values = parameters.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("O'Brien", values["s"]);
        Assert.Equal(42L, values["i"]);
        Assert.Equal(9007199254740993L, values["big"]);
        Assert.Equal(2.5m, values["d"]);
        Assert.Equal(true, values["t"]);
        Assert.Equal(false, values["f"]);
        Assert.Null(values["n"]);
        Assert.IsType<double>(values["e"]);
    }

    [Fact]
    public void Drops_a_leading_placeholder_prefix()
    {
        var (parameters, error) = QueryParameters.Parse(Json("""{"@a": 1, ":b": 2, "c_3": 3}"""));

        Assert.Null(error);
        Assert.Equal(["a", "b", "c_3"], parameters.Select(p => p.Key));
    }

    [Fact]
    public void Accepts_no_parameters()
    {
        Assert.Empty(QueryParameters.Parse(null).Parameters);
        Assert.Empty(QueryParameters.Parse(Json("{}")).Parameters);
    }

    [Theory]
    [InlineData("""{"a b": 1}""", "invalid")]
    [InlineData("""{"1a": 1}""", "invalid")]
    [InlineData("""{"a;DROP": 1}""", "invalid")]
    [InlineData("""{"": 1}""", "invalid")]
    [InlineData("""{"a": 1, "@a": 2}""", "more than once")]
    [InlineData("""{"a": 1, "A": 2}""", "more than once")]
    [InlineData("""{"a": [1, 2]}""", "not array")]
    [InlineData("""{"a": {"b": 1}}""", "not object")]
    public void Rejects_bad_names_and_values(string json, string expected)
    {
        var (_, error) = QueryParameters.Parse(Json(json));

        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Limits_the_count()
    {
        var many = Enumerable.Range(0, QueryParameters.MaxCount + 1)
            .ToDictionary(i => $"p{i}", i => JsonSerializer.SerializeToElement(i));

        Assert.Contains("At most", QueryParameters.Parse(many).Error);
    }
}
