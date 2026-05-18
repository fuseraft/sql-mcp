using SqlMcp.Models;
using SqlMcp.Services;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using Npgsql;
using MySqlConnector;

DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
DbProviderFactories.RegisterFactory("Oracle.ManagedDataAccess.Client", OracleClientFactory.Instance);
DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);
DbProviderFactories.RegisterFactory("MySqlConnector", MySqlConnectorFactory.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(o => o.ServiceName = "sql-mcp");
builder.Host.UseSystemd();

builder.Services.AddSingleton<QueryService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/tools", () => Results.Ok(new object[]
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
        description = "Execute a read-only SELECT query on the specified database and return results as JSON.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                dbName = new { type = "string", description = "Database name from list_databases." },
                sql = new { type = "string", description = "SELECT query to execute." }
            },
            required = new object[] { "dbName", "sql" }
        }
    }
}));

app.MapPost("/tools/{toolName}/invoke", async (string toolName, JsonElement input, QueryService svc, ILogger<Program> logger) =>
{
    try
    {
        if (toolName == "list_databases")
        {
            var dbs = svc.ListDatabases();
            var text = JsonSerializer.Serialize(dbs);
            return Results.Ok(new { content = new object[] { new { type = "text", text } } });
        }

        if (toolName == "query_database")
        {
            if (!input.TryGetProperty("dbName", out var dbProp) || !input.TryGetProperty("sql", out var sqlProp))
                return Results.BadRequest("Missing required fields: dbName, sql");

            var req = new QueryRequest(dbProp.GetString()!, sqlProp.GetString()!);
            var result = await svc.ExecuteAsync(req);
            if (result.IsError)
                return Results.Problem(result.Error);

            var text = JsonSerializer.Serialize(result.Value);
            return Results.Ok(new { content = new object[] { new { type = "text", text } } });
        }

        return Results.NotFound($"Unknown tool: {toolName}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error invoking tool {ToolName}", toolName);
        return Results.Problem("An unexpected error occurred.");
    }
});

app.MapPost("/api/query", async (QueryService svc, QueryRequest req) =>
{
    var result = await svc.ExecuteAsync(req);
    if (result.IsError)
        return Results.Problem(result.Error);
    return Results.Ok(result.Value);
});

var urls = app.Configuration["App:Urls"] ?? "http://*:8080";
app.Run(urls);
