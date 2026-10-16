using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlMcp.Models;
using SqlMcp.Services;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SqlMcp.Tools;

[McpServerToolType]
public sealed class SqlTools(QueryService svc)
{
    // Match /api/query's camelCase output; relaxed escaping keeps the text readable for models.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [McpServerTool(Name = "list_databases", ReadOnly = true, Idempotent = true)]
    [Description("List the names of the databases this server can query.")]
    public string ListDatabases() => JsonSerializer.Serialize(svc.ListDatabases(), JsonOptions);

    [McpServerTool(Name = "query_database", ReadOnly = true)]
    [Description("Execute a read-only SELECT query on the specified database and return the columns and rows as JSON.")]
    public async Task<string> QueryDatabase(
        [Description("Database name from list_databases.")] string dbName,
        [Description("SELECT query to execute.")] string sql,
        CancellationToken cancellationToken)
    {
        var result = await svc.ExecuteAsync(new QueryRequest(dbName, sql), cancellationToken);

        // McpException messages are returned to the client as an isError tool result.
        if (result.IsError)
            throw new McpException(result.Error!);

        return JsonSerializer.Serialize(result.Value, JsonOptions);
    }
}
