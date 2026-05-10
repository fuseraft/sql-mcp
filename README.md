# sql-mcp

sql-mcp is a lightweight .NET Minimal API server for AI agents in the FuseRaft orchestration framework. It proxies SQL queries to multiple databases securely, using env vars for credentials.

## Supported Databases
- SQL Server (e.g., `samplesql`)
- Oracle (Kerberos authentication)
- PostgreSQL
- MySQL

Configs in `appsettings.json`; passwords via env vars like `DB_SAMPLESQL_PASSWORD`.

## Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## Quick Start
1. Clone repo:
   ```
   git clone &lt;repo&gt;
   cd sql-mcp
   ```
2. Restore:
   ```
   dotnet restore
   ```
3. Set DB password env vars.
4. Run:
   ```
   dotnet run
   ```
   Server: `http://*:8080`

## API
**`POST /api/query`**

**Body:**
```json
{
  \"dbName\": \"samplesql\",
  \"sql\": \"SELECT 1 as test;\"
}
```

**Success:**
```json
{
  \"rows\": [{\"test\":1}],
  \"columns\": [\"test\"]
}
```

**Error:**
```json
{
  \"error\": \"Details...\"
}
```

## Structure
- `Program.cs`: App setup, endpoint.
- `Services/QueryService.cs`: Executes queries via DbProviderFactories.
- `Models/`: DTOs.
- `sql-mcp.sln`: Solution.

See `task.md` for origins.

## Build & Test
```
dotnet build
dotnet test  # if tests added
```