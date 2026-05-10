namespace SqlMcp.Models;

using System.Text.Json.Serialization;

public record QueryRequest(
    [property: JsonPropertyName("dbName")] string DbName,
    [property: JsonPropertyName("sql")] string Sql);