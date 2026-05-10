namespace SqlMcp.Models;

public enum DbProvider
{
    SqlServer,
    Oracle,
    Postgres,
    MySql
}

public record DbConfig(
    string Name,
    DbProvider Provider,
    string ConnectionString,
    string? PasswordEnvVar = null)
{
    public string GetInvariantName() => Provider switch
    {
        DbProvider.SqlServer => "Microsoft.Data.SqlClient",
        DbProvider.Oracle => "Oracle.ManagedDataAccess.Client",
        DbProvider.Postgres => "Npgsql",
        DbProvider.MySql => "MySqlConnector",
        _ => throw new ArgumentException("Unknown provider")
    };
}