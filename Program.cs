using SqlMcp.Models;
using SqlMcp.Services;

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

app.Run("http://*:8080");