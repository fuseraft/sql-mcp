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

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true)]
    [Description("List the tables and views the database login can read, as JSON. System schemas are left out unless named.")]
    public async Task<string> ListTables(
        [Description("Database name from list_databases.")] string dbName,
        [Description("Only list this schema (on MySQL, the database). Optional.")] string? schema = null,
        CancellationToken cancellationToken = default) =>
        Serialize(await svc.ListTablesAsync(dbName, schema, cancellationToken));

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true)]
    [Description("Describe a table or view's columns: name, data type, nullability and primary key, as JSON.")]
    public async Task<string> DescribeTable(
        [Description("Database name from list_databases.")] string dbName,
        [Description("Table or view name, optionally schema-qualified (\"sales.orders\"). Case-insensitive.")] string table,
        [Description("Schema containing the table. Optional; needed only when the name exists in more than one schema.")] string? schema = null,
        CancellationToken cancellationToken = default) =>
        Serialize(await svc.DescribeTableAsync(dbName, table, schema, cancellationToken));

    [McpServerTool(Name = "query_database", ReadOnly = true)]
    [Description("Execute a read-only SELECT query on the specified database and return the columns and rows as JSON.")]
    public async Task<string> QueryDatabase(
        [Description("Database name from list_databases.")] string dbName,
        [Description("SELECT query to execute.")] string sql,
        CancellationToken cancellationToken = default) =>
        Serialize(await svc.ExecuteAsync(new QueryRequest(dbName, sql), cancellationToken));

    private static string Serialize<T>(QueryService.Result<T, string> result)
    {
        // McpException messages are returned to the client as an isError tool result.
        if (result.IsError)
            throw new McpException(result.Error!);

        return JsonSerializer.Serialize(result.Value, JsonOptions);
    }
}
