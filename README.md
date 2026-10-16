# sql-mcp

A lightweight .NET 10 [Model Context Protocol](https://modelcontextprotocol.io) server that proxies read-only SQL queries to multiple databases. Designed for AI agents in the [fuseraft](https://github.com/fuseraft/fuseraft-cli) orchestration framework.

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

# Streamable HTTP transport
dotnet run
# MCP endpoint at http://localhost:8080/mcp

# stdio transport
dotnet run -- --stdio
```

The server speaks MCP over two transports, selected at startup:

| Mode | Start with | Use when |
|------|-----------|----------|
| Streamable HTTP | `sql-mcp` | Running as a shared service (Docker, systemd, Windows Service). MCP endpoint is `/mcp`. |
| stdio | `sql-mcp --stdio` | The MCP client launches the server itself as a subprocess. |

`appsettings.json` is read from the directory containing the executable, not the current working directory, so stdio clients can launch it from anywhere.

### Connecting a client

stdio (e.g. Claude Code, Cline, fuseraft):

```json
{
  "mcpServers": {
    "sql": {
      "command": "/opt/sql-mcp/sql-mcp",
      "args": ["--stdio"],
      "env": { "DB_PROD_PASSWORD": "secret" }
    }
  }
}
```

Streamable HTTP:

```json
{
  "mcpServers": {
    "sql": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

With Claude Code: `claude mcp add --transport http sql http://localhost:8080/mcp`, or `claude mcp add sql -- /opt/sql-mcp/sql-mcp --stdio`.

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

### MCP tools

Both tools are annotated `readOnlyHint: true`. Errors (unknown database, rejected statement, database errors) are returned as tool results with `isError: true`.

#### `list_databases`

Returns the names of all configured databases as a JSON array, e.g. `["prod","reporting"]`.

#### `query_database`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `sql` | string | A single `SELECT` or `WITH` query. See [Security](#security). |

Returns the result as JSON text:

```json
{ "columns": ["id", "name"], "rows": [{ "id": 1, "name": "Ada" }], "truncated": false }
```

Example over Streamable HTTP (the server runs stateless, so no `initialize` handshake is needed for a one-off call):

```bash
curl -s http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query_database","arguments":{"dbName":"prod","sql":"SELECT 1 AS test"}}}'
```

### Direct query endpoint

**`POST /api/query`** (HTTP mode only) — plain REST equivalent of `query_database`, for non-MCP callers.

```json
{ "dbName": "prod", "sql": "SELECT 1 AS test" }
```

### Health check

**`GET /health`** (HTTP mode only) — returns `200 {"status":"healthy"}`. Suitable for load balancer and orchestrator probes.

---

## Security

Queries come from AI agents, so every query passes three layers before anything reaches your data. The first two are built in. The third is yours to configure, and it matters most.

1. **Statement validation.** Before connecting, each query is tokenized using that database's own rules for comments, string literals and quoted identifiers (Postgres dollar quotes and nested comments, MySQL backticks and `#` comments, Oracle `q'[...]'` quotes, SQL Server `[brackets]`). A query is accepted only if it:
   - is a single statement starting with `SELECT` or `WITH` (one trailing `;` is allowed and stripped),
   - contains no data-modifying or session-control keywords outside literals, such as `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `INTO`, `FOR UPDATE`, `EXEC`, `SET`, `COMMIT` or `GRANT`, so `SELECT * INTO copy ...`, `WITH d AS (DELETE ...)` and SQL Server batches like `SELECT 1 DROP TABLE t` are all rejected,
   - has no unterminated literal or comment, and no MySQL executable comment (`/*! ... */`).

   Where a server setting changes how backslashes inside literals are read (MySQL `NO_BACKSLASH_ESCAPES`, Postgres `standard_conforming_strings`), the query must pass under both readings, so it can't hide text from the check.

2. **Read-only transaction, always rolled back.** Every query runs inside a transaction that is rolled back afterwards, whatever happens. On Postgres, MySQL and Oracle that transaction is also read-only (`SET TRANSACTION READ ONLY` / `START TRANSACTION READ ONLY`), so the database itself refuses writes that validation can't see, such as `SELECT my_function_that_inserts()`. SQL Server has no read-only transaction mode, so there the rollback is the backstop. (On Oracle, a read-only transaction reads from a consistent snapshot, so a query on a table altered within the last few seconds can fail with `ORA-01466`. Retrying shortly afterwards works.)

3. **A read-only database login.** This is the primary control. Some things neither layer above can stop: a function with its own autonomous transaction (Oracle `PRAGMA AUTONOMOUS_TRANSACTION`), a sequence increment on Oracle or SQL Server (`NEXTVAL`, `NEXT VALUE FOR`), or a call that has effects outside the database (`dblink`, extended procedures, file functions like `LOAD_FILE`). Grant the configured user `SELECT` on what agents should see and nothing else:
   - Postgres: the `pg_read_all_data` role, or `GRANT SELECT` per schema
   - MySQL: `GRANT SELECT ON db.* TO ...`
   - SQL Server: the `db_datareader` role
   - Oracle: `GRANT SELECT` on specific objects; avoid `SELECT ANY TABLE`

**Credentials.** Connection strings can be committed safely: put `{password}` where the password goes and name the environment variable to fill it from with `PasswordEnvVar`.

**Network.** The HTTP transport has no authentication of its own. Bind it to localhost (`App__Urls=http://127.0.0.1:8080`) or put it behind a proxy that authenticates. The stdio transport is only reachable by the process that launched it.

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
├── Program.cs                        # Transport selection (stdio / HTTP), endpoints
├── Tools/SqlTools.cs                 # MCP tool definitions
├── Services/QueryService.cs          # Query execution, read-only transaction, row limiting
├── Services/SqlGuard.cs              # Provider-aware statement validation
├── Models/
│   ├── DbConfig.cs                   # Database config model
│   ├── QueryRequest.cs               # Request DTO
│   └── QueryResult.cs                # Response DTO
├── Properties/PublishProfiles/
│   ├── linux-x64.pubxml
│   └── win-x64.pubxml
├── appsettings.json                  # Default config
├── appsettings.Production.json       # Production log levels
├── Dockerfile
└── tests/SqlMcp.Tests/               # Validator unit tests, database integration tests
```

---

## Testing

```bash
dotnet test
```

The validator unit tests always run. The integration tests run against each database whose connection string is set, and are skipped otherwise. The login needs permission to create a table and, for Postgres and MySQL, a function:

```bash
export SQLMCP_TEST_POSTGRES='Host=localhost;Username=postgres;Password=pw'
export SQLMCP_TEST_MYSQL='Server=localhost;Database=test;User Id=root;Password=pw'
export SQLMCP_TEST_SQLSERVER='Server=localhost;User Id=sa;Password=pw;TrustServerCertificate=true'
export SQLMCP_TEST_ORACLE='Data Source=localhost:1521/FREEPDB1;User Id=test;Password=pw'
dotnet test
```

(MySQL with binary logging enabled also needs `log_bin_trust_function_creators=1` to create the test function.)
