# sql-mcp

A lightweight .NET 10 MCP server that proxies read-only SQL queries to multiple databases. Designed for AI agents in the [fuseraft](https://github.com/fuseraft/fuseraft-cli) orchestration framework.

Supported databases: **SQL Server**, **PostgreSQL**, **MySQL**, **Oracle**

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build) or .NET 10 Runtime (run)
- Access to at least one configured database

---

## Quick Start

```bash
git clone <repo>
cd sql-mcp
dotnet restore

# Set password env vars for any databases that need them
export DB_SAMPLESQL_PASSWORD=yourpassword
export DB_SAMPLEPG_PASSWORD=yourpassword

dotnet run
# Listening on http://*:8080
```

---

## Configuration

All settings live in `appsettings.json`.

### App settings

| Key | Default | Description |
|-----|---------|-------------|
| `App:Urls` | `http://*:8080` | Bind address. Override with env var `App__Urls`. |
| `App:MaxRows` | `1000` | Maximum rows returned per query. Response includes `"truncated": true` when the limit is hit. |
| `App:QueryTimeoutSeconds` | `30` | Per-query timeout in seconds. |

### Database entries

Each entry under `Databases` has:

| Field | Required | Description |
|-------|----------|-------------|
| `Name` | yes | Identifier used in API calls. |
| `Provider` | yes | One of `SqlServer`, `Postgres`, `MySql`, `Oracle`. |
| `ConnectionString` | yes | Standard ADO.NET connection string. Use `{password}` as a placeholder if using `PasswordEnvVar`. |
| `PasswordEnvVar` | no | Environment variable name whose value is substituted for `{password}` in the connection string. |

Example:

```json
{
  "App": {
    "Urls": "http://*:8080",
    "MaxRows": 1000,
    "QueryTimeoutSeconds": 30
  },
  "Databases": [
    {
      "Name": "prod",
      "Provider": "Postgres",
      "ConnectionString": "Host=db.example.com;Database=myapp;Username=readonly;Password={password};",
      "PasswordEnvVar": "DB_PROD_PASSWORD"
    }
  ]
}
```

For local development, put real connection strings in `appsettings.Development.json` (gitignored).

### Environment variable overrides

Any `appsettings.json` value can be overridden with an environment variable using `__` as the section separator:

```bash
App__Urls=http://*:9090
App__MaxRows=500
```

---

## API

### MCP tool endpoints

**`GET /tools`** — list available tools

**`POST /tools/{toolName}/invoke`** — invoke a tool

#### `list_databases`

Returns the names of all configured databases.

```bash
curl -X POST http://localhost:8080/tools/list_databases/invoke \
  -H "Content-Type: application/json" \
  -d '{}'
```

```json
{
  "content": [{ "type": "text", "text": "[\"prod\",\"reporting\"]" }]
}
```

#### `query_database`

Executes a SELECT query. Only `SELECT` and `WITH` (CTE) statements are accepted.

```bash
curl -X POST http://localhost:8080/tools/query_database/invoke \
  -H "Content-Type: application/json" \
  -d '{"dbName": "prod", "sql": "SELECT id, name FROM users LIMIT 10"}'
```

```json
{
  "content": [{
    "type": "text",
    "text": "{\"columns\":[\"id\",\"name\"],\"rows\":[...],\"truncated\":false}"
  }]
}
```

### Direct query endpoint

**`POST /api/query`** — same as `query_database` but returns the result directly without MCP envelope.

```json
{ "dbName": "prod", "sql": "SELECT 1 AS test" }
```

### Health check

**`GET /health`** — returns `200 {"status":"healthy"}`. Suitable for load balancer and orchestrator probes.

---

## Security

- Only `SELECT` and `WITH` (CTE) queries are executed. All other statements are rejected at the application layer.
- **Grant database users read-only access.** This is the primary enforcement layer — the application-level check is defence in depth.
- Passwords are never stored in the process environment directly; the `{password}` substitution pattern keeps connection strings safe to commit while keeping credentials out.

---

## Deployment

### Build a self-contained binary

```bash
# Linux x64
dotnet publish -p:PublishProfile=linux-x64
# Output: publish/linux-x64/

# Windows x64
dotnet publish -p:PublishProfile=win-x64
# Output: publish/win-x64/
```

No .NET runtime needed on the target machine.

### Docker

```bash
docker build -t sql-mcp .
docker run -p 8080:8080 \
  -e DB_PROD_PASSWORD=secret \
  -v /path/to/appsettings.json:/app/appsettings.json \
  sql-mcp
```

### Linux — systemd service

Create `/etc/systemd/system/sql-mcp.service`:

```ini
[Unit]
Description=sql-mcp
After=network.target

[Service]
ExecStart=/opt/sql-mcp/sql-mcp
WorkingDirectory=/opt/sql-mcp
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DB_PROD_PASSWORD=secret

[Install]
WantedBy=multi-user.target
```

```bash
systemctl enable --now sql-mcp
```

### Windows — Windows Service

```powershell
sc create sql-mcp binPath="C:\sql-mcp\sql-mcp.exe"
sc description sql-mcp "sql-mcp MCP server"
sc start sql-mcp
```

Set passwords via system environment variables or an `appsettings.Production.json` placed alongside the executable.

---

## Project structure

```
sql-mcp/
├── Program.cs                        # App wiring, endpoints
├── Services/QueryService.cs          # Query execution, validation, row limiting
├── Models/
│   ├── DbConfig.cs                   # Database config model
│   ├── QueryRequest.cs               # Request DTO
│   └── QueryResult.cs                # Response DTO
├── Properties/PublishProfiles/
│   ├── linux-x64.pubxml
│   └── win-x64.pubxml
├── appsettings.json                  # Default config
├── appsettings.Production.json       # Production log levels
└── Dockerfile
```
