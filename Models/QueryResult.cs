namespace McpServer.Models;

public record QueryResult(
    string[] Columns,
    Dictionary<string, object?>[] Rows);