using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;

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
    string? PasswordEnvVar = null,
    bool AllowWritableLogin = false)
{
    public DbProviderFactory GetFactory() => Provider switch
    {
        DbProvider.SqlServer => SqlClientFactory.Instance,
        DbProvider.Oracle => OracleClientFactory.Instance,
        DbProvider.Postgres => NpgsqlFactory.Instance,
        DbProvider.MySql => MySqlConnectorFactory.Instance,
        _ => throw new ArgumentException("Unknown provider")
    };
}