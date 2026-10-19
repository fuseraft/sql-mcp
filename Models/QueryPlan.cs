namespace SqlMcp.Models;

/// <summary>The database's estimated plan for a query, as the database formats it.</summary>
public record QueryPlan(string Plan);
