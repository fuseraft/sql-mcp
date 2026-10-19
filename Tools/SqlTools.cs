using ModelContextProtocol.Protocol;
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
    [Description("List the databases this server can query, with each one's provider (SqlServer, Postgres, MySql or Oracle). " +
                 "Write each database's SQL in its provider's dialect.")]
    public string ListDatabases(RequestContext<CallToolRequestParams> context) =>
        JsonSerializer.Serialize(svc.ListDatabases(Agent(context)), JsonOptions);

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true)]
    [Description("List the tables and views the database login can read, as JSON, with each table's estimated row count " +
                 "and comment where the database has them. System schemas are left out unless named.")]
    public async Task<CallToolResult> ListTables(
        RequestContext<CallToolRequestParams> context,
        [Description("Database name from list_databases.")] string dbName,
        [Description("Only list this schema (on MySQL, the database). Optional.")] string? schema = null,
        CancellationToken cancellationToken = default) =>
        Serialize(await svc.ListTablesAsync(dbName, schema, Agent(context), cancellationToken));

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true)]
    [Description("Describe a table or view as JSON: its comment and estimated row count, each column's name, data type, " +
                 "nullability, primary key and comment, its foreign keys, and the foreign keys of other tables that reference it. " +
                 "Use the foreign keys to choose join columns.")]
    public async Task<CallToolResult> DescribeTable(
        RequestContext<CallToolRequestParams> context,
        [Description("Database name from list_databases.")] string dbName,
        [Description("Table or view name, optionally schema-qualified (\"sales.orders\"). Case-insensitive.")] string table,
        [Description("Schema containing the table. Optional; needed only when the name exists in more than one schema.")] string? schema = null,
        CancellationToken cancellationToken = default) =>
        Serialize(await svc.DescribeTableAsync(dbName, table, schema, Agent(context), cancellationToken));

    [McpServerTool(Name = "query_database", ReadOnly = true)]
    [Description("Execute a read-only SELECT query on the specified database and return the columns and rows as JSON, " +
                 "each row an array of values in column order. Results are capped in rows and size; a truncated result " +
                 "has a note saying how to narrow or page the query. " +
                 "Pass values through parameters rather than writing them into the SQL: they are bound by the database, " +
                 "so quotes in values need no escaping and can't change the query.")]
    public async Task<CallToolResult> QueryDatabase(
        RequestContext<CallToolRequestParams> context,
        [Description("Database name from list_databases.")] string dbName,
        [Description("SELECT query to execute. Refer to parameters as @name on SQL Server, Postgres and MySQL, and as :name on Oracle.")] string sql,
        [Description("Values for the query's placeholders, by name without the @ or :, e.g. {\"customer\": \"O'Brien\", \"minTotal\": 100}. " +
                     "Values may be strings, numbers, booleans or null. Optional.")] Dictionary<string, JsonElement>? parameters = null,
        CancellationToken cancellationToken = default) =>
        Serialize(await svc.ExecuteAsync(new QueryRequest(dbName, sql, parameters), Agent(context), cancellationToken));

    // The agent HttpSecurity matched the bearer token to; null over stdio or without tokens.
    private static string? Agent(RequestContext<CallToolRequestParams> context) => context.User?.Identity?.Name;

    // An expected failure (unknown database, rejected statement, refused login) is an isError result, not an
    // exception: the SDK would log a thrown one as unhandled, with a stack trace, and prefix its message.
    private static CallToolResult Serialize<T>(QueryService.Result<T, string> result) => new()
    {
        Content = [new TextContentBlock { Text = result.IsError ? result.Error! : JsonSerializer.Serialize(result.Value, JsonOptions) }],
        IsError = result.IsError,
    };
}
