---
title: Quickstart
description: Point sql-mcp at a database with a read-only login, start it, and connect an agent.
---

This walks through one PostgreSQL database end to end. The other providers differ only in the
connection string and the SQL that creates the login; see
[Read-only logins](/sql-mcp/guides/read-only-logins/) for those.

## 1. Create a read-only login

sql-mcp refuses a database whose login can write (see
[How queries stay read-only](/sql-mcp/security/)), so start with a login that can only read:

```sql
CREATE ROLE agent LOGIN PASSWORD 'choose-a-password';
GRANT USAGE ON SCHEMA app TO agent;
GRANT SELECT ON ALL TABLES IN SCHEMA app TO agent;
```

Grant `SELECT` only on what agents should see. Whatever this login can read, an agent can read.

## 2. Configure the database

Replace the sample `Databases` in `appsettings.json` with yours:

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

`{password}` is filled in from the environment variable named by `PasswordEnvVar`, so the file can
be committed. `Name` is what agents pass as `dbName`. Every setting is in the
[configuration reference](/sql-mcp/reference/config/).

## 3. Start it

```sh
export DB_PROD_PASSWORD='choose-a-password'
dotnet run
```

Check that the database connects and its login passes the audit:

```sh
curl -s http://localhost:8080/health/ready
```

```json
{ "status": "ready", "databases": { "prod": "ok" } }
```

If the login can write, the entry says so instead, and the server log lists exactly which
privileges it found.

## 4. Connect an agent

With Claude Code:

```sh
claude mcp add --transport http sql http://localhost:8080/mcp
```

Without agent tokens the server only listens on localhost. To serve other machines, give each
agent its own token; [Connect a client](/sql-mcp/start/clients/) covers tokens, stdio, and other
clients such as anchor.

## 5. Ask a question

Ask something like "How many orders were placed last week, by status?". A capable agent usually:

1. calls `list_databases` and learns `prod` is Postgres,
2. calls `list_tables` (with `search` on a large schema) to find the orders table,
3. calls `describe_table` for its columns, comments and foreign keys,
4. maybe calls `explain_query` if the table is large,
5. and runs one `SELECT` with `query_database`.

Each of those calls appears in the server log under the agent's name. The
[tools reference](/sql-mcp/reference/tools/) shows exactly what each one returns.
