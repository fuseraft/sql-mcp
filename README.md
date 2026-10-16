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
      "url": "http://localhost:8080/mcp",
      "headers": { "Authorization": "Bearer <App:AuthToken>" }
    }
  }
}
```

With Claude Code: `claude mcp add --transport http sql http://localhost:8080/mcp --header "Authorization: Bearer <token>"`, or `claude mcp add sql -- /opt/sql-mcp/sql-mcp --stdio`.

> The sample `appsettings.json` uses admin logins (`sa`, `root`, `postgres`). sql-mcp refuses databases whose login can write, so point it at read-only logins (see [Security](#security)).

---

## Configuration

All settings live in `appsettings.json`.

### App settings

| Key | Default | Description |
|-----|---------|-------------|
| `App:Urls` | `http://localhost:8080` | Bind address(es), `;`-separated. Override with env var `App__Urls`. |
| `App:AuthToken` | _(none)_ | Bearer token required on `/mcp` and `/api/*`. Set it with env var `App__AuthToken` rather than in the file. Required to listen beyond localhost. |
| `App:AllowedOrigins` | `[]` | Browser origins allowed to call the server. Requests carrying any other `Origin` header are rejected. |
| `App:AllowUnauthenticated` | `false` | Allow listening beyond localhost without `AuthToken`, for when a proxy in front authenticates. |
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
| `AllowWritableLogin` | no | `true` serves this database even if its login can write. Logged as a warning at startup. Default `false`. |

Example:

```json
{
  "App": {
    "Urls": "http://localhost:8080",
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
App__Urls=http://localhost:9090
App__MaxRows=500
```

---

## API

### MCP tools

All tools are annotated `readOnlyHint: true`. Every call that touches a database goes through the same login audit and read-only transaction (see [Security](#security)). Errors (unknown database, unknown table, rejected statement, database errors) are returned as tool results with `isError: true`.

#### `list_databases`

Returns the names of all configured databases as a JSON array, e.g. `["prod","reporting"]`.

#### `list_tables`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `schema` | string, optional | Only list this schema (on MySQL, this is a database name). Matched case-insensitively. |

Returns the tables and views the login can read, sorted by schema and name. System schemas (`pg_catalog`, `information_schema`, MySQL's `mysql` and `sys`, Oracle-maintained schemas such as `SYS`) are left out unless you name one with `schema`. On SQL Server only the database in the connection string is listed. At most `App:MaxRows` entries are returned:

```json
{ "tables": [{ "schema": "sales", "name": "orders", "type": "table" }, { "schema": "sales", "name": "open_orders", "type": "view" }], "truncated": false }
```

#### `describe_table`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `table` | string | Table or view name, matched case-insensitively. `schema.table` is accepted. |
| `schema` | string, optional | Needed only when the name exists in more than one schema. Without it, every non-system schema is searched. |

Returns the columns in order, with the type as the database spells it (`character varying(20)`, `nvarchar(max)`, `NUMBER(10,2)`):

```json
{ "schema": "sales", "name": "orders", "type": "table", "columns": [
  { "name": "id", "dataType": "integer", "nullable": false, "primaryKey": true },
  { "name": "customer", "dataType": "character varying(100)", "nullable": true, "primaryKey": false } ] }
```

A table the login can't read is reported as not found.

#### `query_database`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `sql` | string | A single `SELECT` or `WITH` query. See [Security](#security). |
| `parameters` | object, optional | Values for placeholders in `sql`, by name. Values may be strings, numbers, booleans or `null`. |

Put values in `parameters` instead of writing them into the SQL. They are sent to the database separately from the query text, so a value like `O'Brien` needs no escaping and a value like `x' OR '1'='1` is only ever compared as a string. Placeholders use each database's own syntax:

| Provider | Placeholder |
|----------|-------------|
| SQL Server, Postgres, MySQL | `@name` |
| Oracle | `:name` |

```json
{ "dbName": "prod", "sql": "SELECT id, total FROM orders WHERE customer = @customer AND total >= @min", "parameters": { "customer": "O'Brien", "min": 100 } }
```

Names are letters, digits and underscores (a leading `@` or `:` in the key is ignored), up to 100 per query. A placeholder can be used more than once. Whole numbers are bound as 64-bit integers, other numbers as decimals. On Postgres, strings are sent untyped so the server infers the type from where the placeholder is used, as it does for a quoted literal: `WHERE created_at > @since` with `"2024-01-01"` compares dates. Where Postgres can't infer a type (`@x IS NULL`), cast the placeholder: `CAST(@x AS text) IS NULL`.

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
{ "dbName": "prod", "sql": "SELECT name FROM users WHERE id = @id", "parameters": { "id": 7 } }
```

### Health check

**`GET /health`** (HTTP mode only) — returns `200 {"status":"healthy"}`. Suitable for load balancer and orchestrator probes.

---

## Security

Queries come from AI agents, so every query passes three layers before anything reaches your data.

1. **Statement validation.** Before connecting, each query is tokenized using that database's own rules for comments, string literals and quoted identifiers (Postgres dollar quotes and nested comments, MySQL backticks and `#` comments, Oracle `q'[...]'` quotes, SQL Server `[brackets]`). A query is accepted only if it:
   - is a single statement starting with `SELECT` or `WITH` (one trailing `;` is allowed and stripped),
   - contains no data-modifying or session-control keywords outside literals, such as `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `INTO`, `FOR UPDATE`, `EXEC`, `SET`, `COMMIT` or `GRANT`, so `SELECT * INTO copy ...`, `WITH d AS (DELETE ...)` and SQL Server batches like `SELECT 1 DROP TABLE t` are all rejected,
   - doesn't advance a sequence (`NEXTVAL`, `nextval()`, `NEXT VALUE FOR`),
   - calls none of the known functions that reach outside the database, read server files or run SQL held in a string (`dblink_exec`, `pg_read_file`, `query_to_xml`, `LOAD_FILE`, `UTL_HTTP`, `DBMS_XMLGEN`, `OPENROWSET`, ...), including when the name is quoted (`"dblink_exec"(...)`),
   - has no unterminated literal or comment, no MySQL executable comment (`/*! ... */`) and no Postgres `U&"..."` escaped identifier.

   Where a server setting changes how backslashes inside literals are read (MySQL `NO_BACKSLASH_ESCAPES`, Postgres `standard_conforming_strings`), the query must pass under both readings, so it can't hide text from the check.

2. **Read-only transaction, always rolled back.** Every query runs inside a transaction that is rolled back afterwards, whatever happens. On Postgres, MySQL and Oracle that transaction is also read-only (`SET TRANSACTION READ ONLY` / `START TRANSACTION READ ONLY`), so the database itself refuses writes that validation can't see, such as `SELECT my_function_that_inserts()`. SQL Server has no read-only transaction mode, so there the rollback is the backstop. On Oracle, a read-only transaction started within seconds of a table's last DDL fails with `ORA-01466`; sql-mcp retries those for up to a few seconds.

3. **Login audit.** Neither layer above can see inside a function. An Oracle function with `PRAGMA AUTONOMOUS_TRANSACTION` commits its own writes even when called from a read-only transaction that is then rolled back. So before serving a database, sql-mcp checks what its login can do. If the login could change anything or reach outside the database, every query on that database is refused, and the reasons are logged:

   | Provider | Refused when the login |
   |----------|------------------------|
   | Postgres | is `SUPERUSER`, `CREATEROLE` or `CREATEDB`; is in `pg_write_all_data`, `pg_write_server_files`, `pg_read_server_files`, `pg_execute_server_program` or `pg_signal_backend`; can insert, update, delete or truncate any table; can execute `dblink` or untrusted-language functions |
   | MySQL | holds any privilege beyond `SELECT`, `SHOW VIEW`, `SHOW DATABASES`, `PROCESS`, `REPLICATION CLIENT`, `EXECUTE` and `USAGE`, directly or through its default roles (so `INSERT`, `FILE`, `SUPER` and `ALL` are refused) |
   | SQL Server | is `sysadmin` or has `CONTROL SERVER`; has database-level `INSERT`/`UPDATE`/`DELETE`/`ALTER`/`CONTROL`/`CREATE ...`; can modify any table, view or sequence |
   | Oracle | has any system privilege beyond `CREATE SESSION`, `SET CONTAINER`, `SELECT ANY TABLE`, `READ ANY TABLE` and `SELECT ANY DICTIONARY`; has a non-read object privilege (e.g. `EXECUTE` on a function) other than through `PUBLIC`; owns tables, sequences or code; can see a database link |

   The result is cached for five minutes, so a later `GRANT` is noticed without a restart. A login that can't be checked is treated as writable. To serve a database whose login can write anyway, set `AllowWritableLogin: true` on it, and accept that layers 1 and 2 are then all that protects it.

   A login that passes:
   - Postgres: `CREATE ROLE agent LOGIN PASSWORD '...'; GRANT SELECT ON ALL TABLES IN SCHEMA app TO agent;` (or the `pg_read_all_data` role)
   - MySQL: `CREATE USER 'agent'@'%' IDENTIFIED BY '...'; GRANT SELECT ON app.* TO 'agent'@'%';`
   - SQL Server: `CREATE LOGIN agent WITH PASSWORD = '...'; CREATE USER agent FOR LOGIN agent; ALTER ROLE db_datareader ADD MEMBER agent;`
   - Oracle: `CREATE USER agent IDENTIFIED BY "..."; GRANT CREATE SESSION TO agent; GRANT SELECT ON app.orders TO agent;`

**What agents can still do:** read anything the login can read. Grant `SELECT` only on what agents should see. Two cases fall outside the audit: grants made after a passing audit (noticed within five minutes), and on SQL Server, CLR functions created with `EXTERNAL_ACCESS` or `UNSAFE` that the login can call.

**HTTP transport.** By default the server listens on `localhost` only. To listen beyond it, set `App:AuthToken`: clients then send `Authorization: Bearer <token>` on `/mcp` and `/api/*` (`/health` stays open for probes). The server refuses to start on a non-loopback address without a token unless `App:AllowUnauthenticated` is `true`. Requests that carry an `Origin` header not listed in `App:AllowedOrigins` are rejected, so a web page can't reach a local server through DNS rebinding. The stdio transport is only reachable by the process that launched it.

**Values in queries.** Agents should pass values through `parameters` (see [`query_database`](#query_database)) rather than splicing them into the SQL. Bound values can't change a query's structure, whatever quotes or keywords they contain. Statement validation still applies to the SQL text itself, and catches injected statements in SQL an agent built by concatenating strings.

**Credentials.** Connection strings can be committed safely: put `{password}` where the password goes and name the environment variable to fill it from with `PasswordEnvVar`.

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
  -e App__AuthToken="$(openssl rand -hex 32)" \
  -e DB_PROD_PASSWORD=secret \
  -v /path/to/appsettings.json:/app/appsettings.json \
  sql-mcp
```

The image listens on all interfaces inside the container, so it won't start without `App__AuthToken`.

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
Environment=App__AuthToken=<long random token>

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
├── Services/QueryParameters.cs       # Bind-parameter parsing and binding
├── Services/SchemaCatalog.cs         # Per-provider catalog queries for list_tables / describe_table
├── Services/SqlGuard.cs              # Provider-aware statement validation
├── Services/LoginAudit.cs            # Per-provider check that the login can only read
├── Http/HttpSecurity.cs              # Bearer token, Origin check, safe binding
├── Models/
│   ├── DbConfig.cs                   # Database config model
│   ├── QueryRequest.cs               # Request DTO
│   ├── QueryResult.cs                # Response DTO
│   └── SchemaInfo.cs                 # list_tables / describe_table results
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

The validator and HTTP tests always run. The integration tests run against each database whose connection string is set, and are skipped otherwise. Use an administrator login: the tests create their own tables, a view, functions, and a read-only `sqlmcp_ro` login to test the audit against.

```bash
export SQLMCP_TEST_POSTGRES='Host=localhost;Username=postgres;Password=pw'
export SQLMCP_TEST_MYSQL='Server=localhost;Database=test;User Id=root;Password=pw'
export SQLMCP_TEST_SQLSERVER='Server=localhost;User Id=sa;Password=pw;TrustServerCertificate=true'
export SQLMCP_TEST_ORACLE='Data Source=localhost:1521/FREEPDB1;User Id=system;Password=pw'
dotnet test
```

(MySQL with binary logging enabled also needs `log_bin_trust_function_creators=1` to create the test function.)
