using SqlMcp.Http;
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

    var host = builder.Build();
    ExitOnInvalidConfig(() => host.Services.GetRequiredService<QueryService>());
    await host.RunAsync();
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

    ExitOnInvalidConfig(() =>
    {
        HttpSecurity.ValidateStartup(app.Configuration);
        app.Services.GetRequiredService<QueryService>();
    });
    app.UseHttpSecurity();

    app.MapMcp("/mcp");

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

    app.MapPost("/api/query", async (QueryService svc, QueryRequest req, CancellationToken ct) =>
    {
        var result = await svc.ExecuteAsync(req, ct);
        if (result.IsError)
            return Results.Problem(result.Error);
        return Results.Ok(result.Value);
    });

    var urls = app.Configuration["App:Urls"] ?? HttpSecurity.DefaultUrls;
    await app.RunAsync(urls);
}

// Resolving QueryService validates the config, so a bad one stops the server before it
// serves anything, with the problems listed and no stack trace.
static void ExitOnInvalidConfig(Action validate)
{
    try
    {
        validate();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.Exit(1);
    }
}

// Visible to WebApplicationFactory in the tests.
public partial class Program;
