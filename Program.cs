using SqlMcp.Models;
using SqlMcp.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton&lt;QueryService&gt;(provider =&gt; new QueryService(provider.GetRequiredService&lt;IConfiguration&gt;()));

var app = builder.Build();

app.MapPost("/api/query", async (QueryService svc, QueryRequest req) =&gt;
{
    var result = await svc.ExecuteAsync(req);
    if (result.IsError)
        return Results.Problem(result.Error);

    return Results.Ok(result.Value);
});

app.MapGet("/tools", () =&gt; Results.Ok(new[]
{
    new
    {
        name = "list_databases",
        description = "List available database names configured in appsettings.json.",
        inputSchema = new { type = "object", properties = new { }, required = new string[0] }
    },
    new
    {
        name = "query_database",
        description = "Execute a SELECT query on the specified database and return results as JSON.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                dbName = new { type = "string", description = "Database name from list_databases." },
                sql = new { type = "string", description = "SELECT query." }
            },
            required = new[] { "dbName", "sql" }
        }
    }
}));

app.MapPost("/tools/{toolName}/invoke", async (string toolName, JsonElement input, QueryService svc) =&gt;
{
    try
    {
        if (toolName == "list_databases")
        {
            var dbs = svc.ListDatabases();
            var text = JsonSerializer.Serialize(dbs);
            var resp = new { content = new[] { new { type = "text", text } } };
            return Results.Ok(resp);
        }
        if (toolName == "query_database")
        {
            var req = JsonSerializer.Deserialize&lt;QueryRequest&gt;(input.GetRawText()!)!;
            var result = await svc.ExecuteAsync(req);
            if (result.IsError)
                return Results.Problem(result.Error);
            var text = JsonSerializer.Serialize(result.Value);
            var resp = new { content = new[] { new { type = "text", text } } };
            return Results.Ok(resp);
        }
        return Results.NotFound();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run("http://*:8080");
