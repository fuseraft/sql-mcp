using Microsoft.AspNetCore.Mvc.Testing;
using System.Text;
using System.Text.Json;

namespace SqlMcp.Tests;

public class McpToolsTests
{
    private static async Task<JsonElement> ListToolsAsync() =>
        (await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""")).GetProperty("tools");

    private static async Task<JsonElement> PostAsync(string body)
    {
        using var client = new WebApplicationFactory<Program>().CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();

        // The response is either plain JSON or a single server-sent event.
        var json = text.Split('\n').FirstOrDefault(l => l.StartsWith("data: "))?["data: ".Length..] ?? text;
        return JsonDocument.Parse(json).RootElement.GetProperty("result");
    }

    [Theory]
    [InlineData("list_databases", new string[0])]
    [InlineData("list_tables", new[] { "dbName" })]
    [InlineData("describe_table", new[] { "dbName", "table" })]
    [InlineData("query_database", new[] { "dbName", "sql" })]
    public async Task Advertises_read_only_tools_with_their_required_arguments(string name, string[] required)
    {
        var tools = await ListToolsAsync();
        var tool = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var schema = tool.GetProperty("inputSchema");
        var actual = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
        Assert.Equal(required.Order(), actual.Order());
    }

    [Fact]
    public async Task A_refused_call_is_an_error_result_with_just_the_reason()
    {
        var result = await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query_database","arguments":{"dbName":"nope","sql":"SELECT 1"}}}""");

        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.StartsWith("Unknown database 'nope'", text);
    }
}
