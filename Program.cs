using SqlMcp.Models;
using SqlMcp.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<QueryService>(provider => new QueryService(provider.GetRequiredService<IConfiguration>()));

var app = builder.Build();

app.MapPost("/api/query", async (QueryService svc, QueryRequest req) =>
{
    var result = await svc.ExecuteAsync(req);
    if (result.IsError)
        return Results.Problem(result.Error);

    return Results.Ok(result.Value);
});

app.MapGet("/tools", () => Results.Ok(new[] { new { name = "list_databases", description = "List DBs.", inputSchema = new { type = "object", properties = new {}, required = new string[0] } }, new { name = "query_database", description = "Query DB.", inputSchema = new { type = "object", properties = new { dbName = new { type = "string" }, sql = new { type = "string" } }, required = new[] { "dbName", "sql" } } })); app.MapPost("/tools/{toolName}/invoke", async (string toolName, JsonElement input, QueryService svc) => { try { if (toolName == "list_databases") { var dbs = svc.ListDatabases(); var text = JsonSerializer.Serialize(dbs); return Results.Ok(new { content = new[] { new { type = "text", text } } }); } if (toolName == "query_database") { var req = JsonSerializer.Deserialize<QueryRequest>(input.GetRawText()!)!; var result = await svc.ExecuteAsync(req); if (result.IsError) return Results.Problem(result.Error); var text = JsonSerializer.Serialize(result.Value); return Results.Ok(new { content = new[] { new { type = "text", text } } }); } return Results.NotFound(); } catch { return Results.Problem("Invoke error"); } }); 
app.Run("http://*:8080");