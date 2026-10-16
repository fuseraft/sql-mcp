using SqlMcp.Models;
using SqlMcp.Services;

// MCP clients launch stdio servers from their own working directory, so resolve
// appsettings.json next to the executable rather than relative to the cwd.
var contentRoot = AppContext.BaseDirectory;

if (args.Contains("--stdio"))
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = contentRoot
    });

    // stdout carries the JSON-RPC stream; all logging must go to stderr.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton<QueryService>();
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}
else
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = contentRoot
    });

    builder.Host.UseWindowsService(o => o.ServiceName = "sql-mcp");
    builder.Host.UseSystemd();

    builder.Services.AddSingleton<QueryService>();
    builder.Services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly();

    var app = builder.Build();

    app.MapMcp("/mcp");

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

    app.MapPost("/api/query", async (QueryService svc, QueryRequest req, CancellationToken ct) =>
    {
        var result = await svc.ExecuteAsync(req, ct);
        if (result.IsError)
            return Results.Problem(result.Error);
        return Results.Ok(result.Value);
    });

    var urls = app.Configuration["App:Urls"] ?? "http://*:8080";
    await app.RunAsync(urls);
}
