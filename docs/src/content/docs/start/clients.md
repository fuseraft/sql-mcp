---
title: Connect a client
description: Connect Claude Code, anchor, fuseraft or any MCP client over stdio or Streamable HTTP, with per-agent tokens.
---

sql-mcp speaks MCP over two transports. You pick one when it starts:

| Mode | Start with | Use when |
|------|-----------|----------|
| Streamable HTTP | `sql-mcp` | It runs as a shared service (Docker, systemd, Windows Service). The endpoint is `/mcp`. |
| stdio | `sql-mcp --stdio` | The MCP client launches it as a subprocess, one per client. |

## stdio

The client starts sql-mcp itself and passes the password variables in its environment. Calls
over stdio are logged under the agent name `unauthenticated`, because only the process that
launched the server can reach it.

**Claude Code**

```sh
claude mcp add sql --env DB_PROD_PASSWORD=secret -- /opt/sql-mcp/sql-mcp --stdio
```

**anchor and other clients that read `.mcp.json`**

```json
{
  "mcpServers": {
    "sql": {
      "command": "/opt/sql-mcp/sql-mcp",
      "args": ["--stdio"],
      "env": { "DB_PROD_PASSWORD": "${DB_PROD_PASSWORD}" }
    }
  }
}
```

**fuseraft**

```yaml
McpServers:
  - Name: Sql
    Transport: stdio
    Command: /opt/sql-mcp/sql-mcp
    Args: ["--stdio"]
    Env:
      DB_PROD_PASSWORD: "${DB_PROD_PASSWORD}"
```

## Streamable HTTP

Run sql-mcp once and point each agent at it. Give every agent its own token under
`App:AgentTokens`, set from the environment rather than the file:

```sh
export App__AgentTokens__reporting="$(openssl rand -hex 32)"
export App__AgentTokens__support="$(openssl rand -hex 32)"
```

Each agent then sends its own token, and its calls are logged under its name (`reporting`,
`support`). Keep agent names to letters, digits and underscores so they work as environment
variable names everywhere.

**Claude Code**

```sh
claude mcp add --transport http sql http://localhost:8080/mcp --header "Authorization: Bearer $SQL_MCP_TOKEN"
```

**anchor and other clients that read `.mcp.json`**

```json
{
  "mcpServers": {
    "sql": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "Authorization": "Bearer ${SQL_MCP_TOKEN}" }
    }
  }
}
```

**fuseraft**

```yaml
McpServers:
  - Name: Sql
    Transport: http
    Url: http://localhost:8080/mcp
    Headers:
      Authorization: "Bearer ${SQL_MCP_TOKEN}"
```

All five tools are marked `readOnlyHint: true`, so clients that skip approval for read-only tools,
such as anchor and fuseraft's REPL, run them without asking.

### Without tokens

With no `App:AgentTokens`, sql-mcp accepts any request, so it refuses to start on anything but
localhost. If a proxy in front of it authenticates callers, set `App:AllowUnauthenticated` to
`true` to listen beyond localhost anyway. Calls are then logged as `unauthenticated`.

### Browsers

Requests that carry an `Origin` header are rejected unless the origin is listed in
`App:AllowedOrigins`. That keeps a web page from reaching a server on your machine through DNS
rebinding. MCP clients that aren't browsers don't send `Origin` and aren't affected.

## Trying it with curl

The server is stateless, so a one-off call needs no `initialize` handshake:

```sh
curl -s http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Authorization: Bearer $SQL_MCP_TOKEN" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_databases","arguments":{}}}'
```

For callers that don't speak MCP, `POST /api/query` runs a query as plain JSON; see the
[HTTP reference](/sql-mcp/reference/http/).
