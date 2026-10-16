using Microsoft.AspNetCore.Mvc.Testing;
using System.Text;
using System.Text.Json;

namespace SqlMcp.Tests;

public class McpToolsTests
{
    private static async Task<JsonElement> ListToolsAsync()
    {
        using var client = new WebApplicationFactory<Program>().CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // The response is either plain JSON or a single server-sent event.
        var json = body.Split('\n').FirstOrDefault(l => l.StartsWith("data: "))?["data: ".Length..] ?? body;
        return JsonDocument.Parse(json).RootElement.GetProperty("result").GetProperty("tools");
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
}
