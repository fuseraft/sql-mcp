---
title: Logging
description: What sql-mcp logs for each call, refusals and rejected tokens, and how to set the levels.
---

Every tool call and `/api/query` request is logged by the `SqlMcp.Services.QueryService`
category, so the log is an audit trail of what each agent asked for and what ran.

## What a call logs

Each entry names the agent, the database, the SQL (or the schema lookup), the names of any
parameters, how long it took, and how many rows came back:

```
2026-10-18T14:02:11.384Z info: SqlMcp.Services.QueryService[0]
      Ran query with parameters customer: SELECT id FROM orders WHERE customer = @customer on prod for reporting in 12 ms, 3 rows.
2026-10-18T14:02:15.020Z info: SqlMcp.Services.QueryService[0]
      Ran describe_table orders on prod for reporting in 8 ms, 6 rows.
2026-10-18T14:02:40.019Z warn: SqlMcp.Services.QueryService[0]
      Refused query: DELETE FROM orders on prod for reporting: Only SELECT queries (including CTEs) are permitted.
```

- **Parameter values are never logged**, only their names.
- The agent is the name its token has in `App:AgentTokens`. Over stdio, or over HTTP without
  tokens, it's `unauthenticated`.
- `explain_query` calls are logged as `explain`, `list_tables` as `list_tables` with any schema
  and search, and `list_databases` with the number of databases.
- A truncated result is marked `(truncated)`.

## Warnings and errors

| Level | When |
|-------|------|
| `warn` | A call refused before it reached the database: unknown database, a statement that isn't a read, invalid parameters, a missing password variable. The SQL is included, so the log shows what each agent tried as well as what ran. |
| `warn` | A call that failed: a database error, a timeout, a busy database, a `describe_table` that found no table or more than one. |
| `warn` | A request with a missing or unknown bearer token, logged by `SqlMcp.Http.HttpSecurity` with its method, path and remote address, never the token. |
| `fail` | A database whose login failed the audit, with each privilege found. Logged when the audit runs, at most every five minutes. |

At startup, sql-mcp also warns about databases set to `AllowWritableLogin`, databases whose
password variable isn't set, and a configuration with no databases.

## Levels and format

`appsettings.Production.json` applies in the `Production` environment, which is the default when no
environment is set, and what the Docker image sets. It keeps `QueryService` at `Information` and everything else at `Warning`, and
starts each entry with the UTC time:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning",
      "SqlMcp.Services.QueryService": "Information"
    },
    "Console": {
      "FormatterName": "simple",
      "FormatterOptions": { "TimestampFormat": "yyyy-MM-ddTHH:mm:ss.fffZ ", "UseUtcTimestamp": true }
    }
  }
}
```

- To log only refusals and failures, set `SqlMcp.Services.QueryService` to `Warning`.
- Under systemd, journald records its own timestamps, so you can drop the timestamp format.

In stdio mode every log line goes to stderr, because stdout carries the MCP stream.
