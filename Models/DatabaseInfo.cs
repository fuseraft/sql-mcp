namespace SqlMcp.Models;

/// <summary>A configured database. Provider tells an agent which SQL dialect to write.</summary>
public record DatabaseInfo(string Name, string Provider);
