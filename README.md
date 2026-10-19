# sql-mcp

[![CI](https://github.com/fuseraft/sql-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/fuseraft/sql-mcp/actions/workflows/ci.yml)

A [Model Context Protocol](https://modelcontextprotocol.io) server that lets AI agents read your
SQL Server, PostgreSQL, MySQL and Oracle databases, and nothing more. Every query is checked, run
in a transaction that is rolled back, and refused outright if the login could write.

**Documentation: [fuseraft.ai/sql-mcp](https://fuseraft.ai/sql-mcp/)**

- **Read-only, three times over.** Each query must be a single `SELECT`, checked with the
  database's own rules for quotes and comments. It runs in a transaction that is always rolled
  back, read-only where the database supports it. And a database whose login could write is
  refused altogether.
- **Schema context for agents.** Each database's provider, so agents write the right dialect;
  foreign keys in both directions, table and column comments, and estimated row counts.
- **Results that fit in a context.** Compact rows, capped by count and size, with a note on how to
  narrow or page a query that was cut off. `explain_query` shows the plan without running anything.
- **Every call attributed.** Each agent has its own bearer token, and every call is logged under
  its name, refusals included.
- **Runs where you need it.** stdio for a client that launches it, or Streamable HTTP as a shared
  service in Docker, under systemd or as a Windows Service.

## Install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
git clone https://github.com/fuseraft/sql-mcp.git && cd sql-mcp
dotnet publish -p:PublishProfile=linux-x64   # or win-x64; self-contained, in publish/
docker build -t sql-mcp .                    # or as an image
```

See [Install](https://fuseraft.ai/sql-mcp/start/install/).

## Configure

Point it at a database with a login that can only read:

```json
{
  "Databases": [
    {
      "Name": "prod",
      "Provider": "Postgres",
      "ConnectionString": "Host=db.example.com;Database=myapp;Username=agent;Password={password};",
      "PasswordEnvVar": "DB_PROD_PASSWORD"
    }
  ]
}
```

[Read-only logins](https://fuseraft.ai/sql-mcp/guides/read-only-logins/) shows how to create one
for each database, and the [configuration reference](https://fuseraft.ai/sql-mcp/reference/config/)
lists every setting.

## Connect

```sh
claude mcp add sql -- /opt/sql-mcp/sql-mcp --stdio                                     # stdio
claude mcp add --transport http sql http://localhost:8080/mcp \
  --header "Authorization: Bearer $SQL_MCP_TOKEN"                                     # HTTP
```

Agents get five tools: `list_databases`, `list_tables`, `describe_table`, `explain_query` and
`query_database`. See [Connect a client](https://fuseraft.ai/sql-mcp/start/clients/) for anchor,
fuseraft and tokens.

## Learn more

[Quickstart](https://fuseraft.ai/sql-mcp/start/quickstart/) ·
[How queries stay read-only](https://fuseraft.ai/sql-mcp/security/) ·
[Working with agents](https://fuseraft.ai/sql-mcp/guides/agents/) ·
[Deployment](https://fuseraft.ai/sql-mcp/guides/deployment/) ·
[Logging](https://fuseraft.ai/sql-mcp/guides/logging/) ·
[Tools](https://fuseraft.ai/sql-mcp/reference/tools/) ·
[HTTP](https://fuseraft.ai/sql-mcp/reference/http/)

## Develop

`dotnet test` runs the tests. The integration tests run against each database whose
`SQLMCP_TEST_*` connection string is set; [Development](https://fuseraft.ai/sql-mcp/guides/development/)
shows how to start all four in containers. The docs site in `docs/` is built with
[Starlight](https://starlight.astro.build): `npm install && npm run dev` there to preview, and
pushes to `master` deploy it.
