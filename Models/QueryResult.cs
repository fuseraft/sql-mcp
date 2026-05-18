namespace SqlMcp.Models;

public record QueryResult(
    string[] Columns,
    Dictionary<string, object?>[] Rows,
    bool Truncated = false);
