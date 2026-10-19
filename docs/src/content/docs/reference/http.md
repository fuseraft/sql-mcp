---
title: HTTP and command line
description: The HTTP endpoints, authentication and Origin rules, and the command-line flags.
---

## Command line

```sh
sql-mcp                 # Streamable HTTP, at App:Urls
sql-mcp --stdio         # stdio
sql-mcp --healthcheck   # call the running server's /health; exit 0 if it answers 200, else 1
```

Any setting can also be passed on the command line, such as `sql-mcp --App:MaxRows=50`.

## Authentication

When `App:AgentTokens` has entries, every path except `/health` needs
`Authorization: Bearer <token>` with one agent's token. A missing or unknown token gets
`401 Unauthorized` with `WWW-Authenticate: Bearer`, and a warning in the log. Tokens are compared
in constant time, as hashes, so timing reveals neither the token nor which agent's it was.

Any request with an `Origin` header whose origin isn't listed in `App:AllowedOrigins` gets
`403 Origin not allowed.`, whatever the path. Clients that aren't browsers don't send `Origin`.

## Endpoints

### `POST /mcp`

The MCP endpoint, using Streamable HTTP. The server is stateless: each request stands alone, so a
one-off `tools/call` needs no `initialize` first. Send
`Accept: application/json, text/event-stream`.

### `POST /api/query`

A plain JSON version of `query_database`, for callers that don't speak MCP. It goes through the
same checks, audit, transaction and limits, and is logged the same way.

```json
{ "dbName": "prod", "sql": "SELECT name FROM users WHERE id = @id", "parameters": { "id": 7 } }
```

A successful query returns `200` with the same result as the tool:

```json
{ "columns": ["name"], "rows": [["Ada"]], "truncated": false }
```

A refused or failed query returns `500` as an `application/problem+json` response, with the
reason in `detail`:

```json
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1", "title": "An error occurred while processing your request.",
  "status": 500, "detail": "Only SELECT queries (including CTEs) are permitted." }
```

### `GET /health`

Liveness. `200 {"status":"healthy"}` while the process is up. It never needs a token, so it suits
load balancer and container probes.

### `GET /health/ready`

Readiness. For every database, connects, runs the login audit and opens the read-only
transaction, without running a query. Returns `200` when all of them succeed, otherwise `503`:

```json
{ "status": "unavailable", "databases": { "prod": "ok", "reporting": "Database 'reporting' is disabled because its login can modify data. The server log lists why." } }
```

It needs a token when tokens are set, since it names the databases and why any are down. Its
checks aren't written to the query log.
