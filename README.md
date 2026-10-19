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
      "headers": { "Authorization": "Bearer <the agent's token from App:AgentTokens>" }
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
| `App:AgentTokens` | `{}` | Each agent's name and the bearer token it sends on `/mcp` and `/api/*`, e.g. `{"reporting": "<token>"}`. Calls are logged under the agent's name (see [Logging](#logging)). Set tokens with env vars such as `App__AgentTokens__reporting` rather than in the file, and keep names to letters, digits and underscores so they work as variable names everywhere. A blank token, or one shared by two agents, stops the server at startup. Required to listen beyond localhost. |
| `App:AllowedOrigins` | `[]` | Browser origins allowed to call the server. Requests carrying any other `Origin` header are rejected. |
| `App:AllowUnauthenticated` | `false` | Allow listening beyond localhost without `AgentTokens`, for when a proxy in front authenticates. |
| `App:MaxRows` | `100` | Maximum rows returned per query. When the limit is hit the response says `"truncated": true`, with a `note` on how to narrow or page the query. |
| `App:MaxTables` | `1000` | Maximum tables and views returned per `list_tables`. |
| `App:QueryTimeoutSeconds` | `30` | Per-query timeout in seconds; a query that runs longer fails with "Query timed out after N s". Also how long a query waits for a free slot when `MaxConcurrentQueries` are already running. |
| `App:MaxValueLength` | `10000` | Longest string returned for a single value, in characters. Longer text and binary values are cut off and marked `… [truncated: N characters in total]`. |
| `App:MaxResultBytes` | `100000` | Approximate size limit for one query's result, about 25,000 tokens. Rows stop being added once it's reached, and the response says `"truncated": true`. |
| `App:MaxConcurrentQueries` | `8` | Queries that may run at once against each database. Further queries wait up to `QueryTimeoutSeconds`, then fail with "Database 'x' is busy". |

The configuration is checked at startup. An invalid limit, a duplicate or missing database name, a missing or unknown `Provider`, an unparseable connection string, or a `PasswordEnvVar` without a `{password}` placeholder stops the server with every problem listed. A database whose `PasswordEnvVar` isn't set is logged as a warning and only that database is unavailable.

### Database entries

Each entry under `Databases` has:

| Field | Required | Description |
|-------|----------|-------------|
| `Name` | yes | Identifier used in API calls. Unique, ignoring case; agents may use any case. |
| `Provider` | yes | One of `SqlServer`, `Postgres`, `MySql`, `Oracle`. |
| `ConnectionString` | yes | Standard ADO.NET connection string. Use `{password}` as a placeholder if using `PasswordEnvVar`. |
| `PasswordEnvVar` | no | Environment variable name whose value is substituted for `{password}` in the connection string. |
| `AllowWritableLogin` | no | `true` serves this database even if its login can write. Logged as a warning at startup. Default `false`. |

Example:

```json
{
  "App": {
    "Urls": "http://localhost:8080",
    "MaxRows": 100,
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

### Logging

Each tool call and `/api/query` request is logged by `SqlMcp.Services.QueryService`: the agent, the database, the SQL (or the schema lookup), the names of any parameters, how long it took and how many rows it returned (for `list_databases`, the agent and how many databases there are). Parameter values are never logged.

```
2026-10-18T14:02:11.384Z info: SqlMcp.Services.QueryService[0]
      Ran query: SELECT id FROM orders on prod for reporting in 12 ms, 3 rows.
2026-10-18T14:02:40.019Z warn: SqlMcp.Services.QueryService[0]
      Refused query: DELETE FROM orders on prod for reporting: Only SELECT queries (including CTEs) are permitted.
```

The agent is the name its token has in `App:AgentTokens`. Over stdio, or over HTTP without agent tokens, it is `unauthenticated`. Calls refused before they reach the database (an unknown database, a statement that isn't a read, invalid parameters) are logged as warnings with the SQL, so the log shows what each agent tried as well as what ran. Failures are warnings too, including a `describe_table` for a table that doesn't exist or matches more than one. A request with a missing or unknown token is logged as a warning by `SqlMcp.Http.HttpSecurity`, with its path and remote address but not the token.

`appsettings.Production.json` keeps `QueryService` at `Information` while everything else is at `Warning`; set it to `Warning` to log only refusals and failures. It also starts each entry with the UTC time; under systemd, journald records the time instead. In stdio mode all logs go to stderr.

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

Returns each configured database with its provider, which tells the agent which SQL dialect to write:

```json
[{ "name": "prod", "provider": "Postgres" }, { "name": "legacy", "provider": "SqlServer" }]
```

#### `list_tables`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `schema` | string, optional | Only list this schema (on MySQL, this is a database name). Matched case-insensitively. |

Returns the tables and views the login can read, sorted by schema and name. System schemas (`pg_catalog`, `information_schema`, MySQL's `mysql` and `sys`, Oracle-maintained schemas such as `SYS`) are left out unless you name one with `schema`. On SQL Server only the database in the connection string is listed. At most `App:MaxTables` entries are returned; beyond that the response says `"truncated": true` with a `note`:

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

Returns the result as JSON text, each row an array of values in the order of `columns`:

```json
{ "columns": ["id", "name"], "rows": [[1, "Ada"], [2, "Grace"]], "truncated": false }
```

A result cut short by `App:MaxRows` or `App:MaxResultBytes` says so, and its `note` says what to do next, with paging in the database's own syntax:

```json
{ "columns": ["id"], "rows": [[1], [2]], "truncated": true,
  "note": "Only the first 100 rows are shown (App:MaxRows). Narrow the query with WHERE, summarize it with GROUP BY, or page through it with ORDER BY ... LIMIT 100 OFFSET 100." }
```

Every column has a distinct name: unnamed columns are called `column1`, `column2`, ... and repeated names get a suffix (`SELECT a.id, b.id` returns `id` and `id_2`). Values JSON can't hold are converted:
- `NaN` and infinite floats become the strings `"NaN"`, `"Infinity"`, `"-Infinity"`.
- Oracle `NUMBER`s beyond 28 significant digits are rounded (`1/3`), and integers too large for a decimal come back as strings.
- Binary values are base64.
- Types without a JSON form (Postgres `inet`, ranges, geometric types; Oracle intervals) are returned as their text.

A value the driver can't read at all, such as a SQL Server `geography` column (whose .NET types aren't shipped), is replaced by a note saying so, and the rest of the result is unaffected. Select it as text instead, e.g. `geo.ToString()`.

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

**`GET /health`** (HTTP mode only): liveness. Returns `200 {"status":"healthy"}` while the process is up, and needs no token, so it suits load balancer and liveness probes.

**`GET /health/ready`** (HTTP mode only): readiness. Connects to every configured database, runs the login audit and opens the read-only transaction, without running a query. It returns `200` when all of them succeed, otherwise `503`, with each database's status:

```json
{ "status": "unavailable", "databases": { "prod": "ok", "reporting": "Database 'reporting' is disabled because its login can modify data. The server log lists why." } }
```

It requires an agent's token when `App:AgentTokens` is set, since it names the databases. Readiness checks aren't written to the query log.

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

**HTTP transport.** By default the server listens on `localhost` only. To listen beyond it, give each agent its own token under `App:AgentTokens`: clients then send `Authorization: Bearer <token>` on `/mcp` and `/api/*` (`/health` stays open for probes), and their calls are logged under the agent's name. Revoking an agent means removing its entry and restarting. The server refuses to start on a non-loopback address without tokens unless `App:AllowUnauthenticated` is `true`. Requests that carry an `Origin` header not listed in `App:AllowedOrigins` are rejected, so a web page can't reach a local server through DNS rebinding. The stdio transport is only reachable by the process that launched it.

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
  -e App__AgentTokens__reporting="$(openssl rand -hex 32)" \
  -e DB_PROD_PASSWORD=secret \
  -v /path/to/appsettings.json:/app/appsettings.json \
  sql-mcp
```

The image listens on all interfaces inside the container, so it won't start without at least one `App__AgentTokens__<name>`. It runs as the base image's unprivileged `app` user (uid 1654), so a mounted `appsettings.json` must be readable by that user. Its `HEALTHCHECK` runs `dotnet sql-mcp.dll --healthcheck`, which calls `/health` on the configured port and exits 0 or 1. Use the same command for health checks anywhere curl isn't installed.

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
Environment=App__AgentTokens__reporting=<long random token>

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
├── Services/ResultValues.cs          # Converts database values to plain JSON
├── Services/SchemaCatalog.cs         # Per-provider catalog queries for list_tables / describe_table
├── Services/SqlGuard.cs              # Provider-aware statement validation
├── Services/LoginAudit.cs            # Per-provider check that the login can only read
├── Http/HttpSecurity.cs              # Bearer token, Origin check, safe binding
├── Http/HealthCheck.cs               # --healthcheck mode for container probes
├── Models/
│   ├── DbConfig.cs                   # Database config model
│   ├── DatabaseInfo.cs               # list_databases entry: name and provider
│   ├── QueryRequest.cs               # Request DTO
│   ├── QueryResult.cs                # Response DTO
│   └── SchemaInfo.cs                 # list_tables / describe_table results
├── Properties/PublishProfiles/
│   ├── linux-x64.pubxml
│   └── win-x64.pubxml
├── appsettings.json                  # Default config
├── appsettings.Production.json       # Production log levels
├── Dockerfile
├── .github/workflows/ci.yml          # Build, tests against all four databases, image smoke test
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

CI (`.github/workflows/ci.yml`) runs the same suite on every push and pull request, against Postgres 16, MySQL 8.4, SQL Server 2022 and Oracle 23 Free service containers. The job fails if any test is skipped, so a database that didn't start can't let it pass. CI also builds with warnings treated as errors, fails on vulnerable packages, and checks that the Docker image:
- refuses to start without a token
- serves `/health` and returns 401 on `/mcp` without the token
- runs as non-root
