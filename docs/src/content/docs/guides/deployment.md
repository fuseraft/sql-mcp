---
title: Deployment
description: Run sql-mcp as a shared service in Docker, under systemd or as a Windows Service, with tokens and health checks.
---

For more than one agent, run sql-mcp once over Streamable HTTP and give each agent a token. The
same rules apply wherever it runs:

- **Bind address.** `App:Urls` sets where it listens, `http://localhost:8080` by default.
  Separate several addresses with `;`.
- **Tokens.** To listen beyond localhost, set at least one `App:AgentTokens` entry, or set
  `App:AllowUnauthenticated` to `true` when a proxy in front authenticates. Otherwise the server
  refuses to start. A blank token, or one shared by two agents, also stops it.
- **Passwords and tokens come from the environment**, never from the file:
  `DB_<NAME>_PASSWORD` for whatever `PasswordEnvVar` names, and `App__AgentTokens__<agent>`.
- **TLS.** sql-mcp serves plain HTTP. Beyond a single host, put it behind a reverse proxy or load
  balancer that terminates TLS, so tokens never cross the network in the clear.

## Docker

```sh
docker build -t sql-mcp .
docker run -d --name sql-mcp -p 8080:8080 \
  -e App__AgentTokens__reporting="$(openssl rand -hex 32)" \
  -e DB_PROD_PASSWORD=secret \
  -v /path/to/appsettings.json:/app/appsettings.json:ro \
  sql-mcp
```

- The image listens on all interfaces inside the container (`App__Urls=http://*:8080`), so it
  won't start without a token.
- It runs as the base image's unprivileged `app` user (uid 1654). A mounted `appsettings.json`
  must be readable by that user.
- `ASPNETCORE_ENVIRONMENT=Production` is set, so the production log settings apply (see
  [Logging](/sql-mcp/guides/logging/)).
- Its `HEALTHCHECK` runs `dotnet sql-mcp.dll --healthcheck`, which calls `/health` on the configured
  port and exits 0 or 1. The image has no curl or wget, so use the same command for health checks
  elsewhere.

## Linux: systemd

Publish a self-contained binary (see [Install](/sql-mcp/start/install/#self-contained-binary)), copy
it to `/opt/sql-mcp`, and create `/etc/systemd/system/sql-mcp.service`:

```ini
[Unit]
Description=sql-mcp
After=network.target

[Service]
Type=notify
ExecStart=/opt/sql-mcp/sql-mcp
WorkingDirectory=/opt/sql-mcp
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=/etc/sql-mcp/env

[Install]
WantedBy=multi-user.target
```

Keep the secrets in `/etc/sql-mcp/env`, readable only by root:

```sh
DB_PROD_PASSWORD=secret
App__AgentTokens__reporting=<long random token>
App__Urls=http://0.0.0.0:8080
```

```sh
systemctl enable --now sql-mcp
journalctl -u sql-mcp -f
```

With `Type=notify`, sql-mcp tells systemd when it has started, so `systemctl start` waits until
it's listening and reports a configuration error as a failed start.

## Windows Service

```powershell
sc.exe create sql-mcp binPath= "C:\sql-mcp\sql-mcp.exe" start= auto
sc.exe description sql-mcp "sql-mcp MCP server"
sc.exe start sql-mcp
```

Set passwords and tokens as system environment variables, or in an `appsettings.Production.json`
next to the executable with `ASPNETCORE_ENVIRONMENT=Production` set for the service. Restrict that
file to the service's account.

## Health checks

| Endpoint | Token | Answers |
|----------|-------|---------|
| `GET /health` | never | `200 {"status":"healthy"}` while the process is up. Use it for liveness and load balancer probes. |
| `GET /health/ready` | when tokens are set | `200` when every database connects, passes the login audit and opens its read-only transaction, otherwise `503`, with each database's status. Use it for readiness. |

```json
{ "status": "unavailable", "databases": { "prod": "ok", "reporting": "Database 'reporting' is disabled because its login can modify data. The server log lists why." } }
```

`/health/ready` needs a token because it names the databases and why any are down. Its checks
aren't written to the query log.

## Limits worth tuning

The defaults suit one agent working interactively. For a shared server, look at:

- `App:MaxConcurrentQueries` (8 per database). Further queries wait up to `QueryTimeoutSeconds`
  for a slot, then fail with "Database 'x' is busy".
- `App:QueryTimeoutSeconds` (30). A query that runs longer is cancelled.
- `App:MaxRows` and `App:MaxResultBytes`, which bound what one call returns.

All of them are in the [configuration reference](/sql-mcp/reference/config/).
