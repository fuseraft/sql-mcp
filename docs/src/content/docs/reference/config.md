---
title: Configuration
description: Every setting in appsettings.json, the database entries, and environment variable overrides.
---

sql-mcp reads `appsettings.json` from the folder that contains the executable, then
`appsettings.<environment>.json` (for example `appsettings.Production.json`), then the
environment. Later sources override earlier ones.

```json
{
  "App": {
    "Urls": "http://localhost:8080",
    "AgentTokens": {},
    "AllowedOrigins": [],
    "MaxRows": 100,
    "MaxTables": 1000,
    "QueryTimeoutSeconds": 30,
    "MaxValueLength": 10000,
    "MaxResultBytes": 100000,
    "MaxConcurrentQueries": 8
  },
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

## `App`

| Key | Default | Description |
|-----|---------|-------------|
| `Urls` | `http://localhost:8080` | Where the HTTP transport listens. Separate several with `;`. |
| `AgentTokens` | `{}` | Each agent's name and the bearer token it sends, e.g. `{"reporting": "<token>"}`. Every path except `/health` then requires one, and calls are logged under the agent's name. Set tokens from the environment (`App__AgentTokens__reporting`) rather than in the file. A blank token, or one shared by two agents, stops the server at startup. Required to listen beyond localhost. |
| `AllowedOrigins` | `[]` | Browser origins allowed to call the server. A request with any other `Origin` header is rejected with 403. |
| `AllowUnauthenticated` | `false` | Listen beyond localhost without `AgentTokens`, for when a proxy in front authenticates. |
| `MaxRows` | `100` | Most rows one query returns. Beyond it the result says `truncated: true`, with a `note` on narrowing or paging. |
| `MaxTables` | `1000` | Most tables and views one `list_tables` returns. |
| `QueryTimeoutSeconds` | `30` | How long a query may run before it's cancelled with "Query timed out after N s". Also how long a call waits for a free slot when `MaxConcurrentQueries` are running. |
| `MaxValueLength` | `10000` | Longest text value returned, in characters. Longer text and binary values are cut off and marked `… [truncated: N characters in total]`. |
| `MaxResultBytes` | `100000` | Approximate size limit for one result, about 25,000 tokens. Rows stop being added once it's reached. Also caps an `explain_query` plan. |
| `MaxConcurrentQueries` | `8` | Calls that may run at once against each database. Further calls wait up to `QueryTimeoutSeconds`, then fail with "Database 'x' is busy". |

The limits must be whole numbers of at least 1.

## `Databases`

Each entry has:

| Field | Required | Description |
|-------|----------|-------------|
| `Name` | yes | What agents pass as `dbName`. Unique, ignoring case, and agents may use any case. |
| `Provider` | yes | `SqlServer`, `Postgres`, `MySql` or `Oracle`. |
| `ConnectionString` | yes | An ADO.NET connection string for that provider. Put `{password}` where the password goes to fill it from `PasswordEnvVar`. |
| `PasswordEnvVar` | no | The environment variable whose value replaces `{password}`. |
| `AllowWritableLogin` | no | `true` serves this database even if its login can write, skipping the [login audit](/sql-mcp/guides/read-only-logins/). Logged as a warning at startup. Default `false`. |

Connection strings for each provider:

| Provider | Example |
|----------|---------|
| `Postgres` | `Host=db;Port=5432;Database=myapp;Username=agent;Password={password}` |
| `MySql` | `Server=db;Port=3306;Database=myapp;User Id=agent;Password={password}` |
| `SqlServer` | `Server=db,1433;Database=myapp;User Id=agent;Password={password};TrustServerCertificate=true` |
| `Oracle` | `Data Source=db:1521/ORCLPDB1;User Id=agent;Password={password}` |

On SQL Server, only the database in the connection string is listed by `list_tables`, so name it
with `Database=`. Use `TrustServerCertificate=true` only for a server whose certificate you can't
otherwise verify.

## Checked at startup

An invalid limit, a duplicate or missing `Name`, a missing or unknown `Provider`, a connection
string the provider can't parse, or a `PasswordEnvVar` without a `{password}` placeholder stops the
server, with every problem listed at once:

```
Invalid configuration:
  - Database 'PROD' is configured more than once (names are compared ignoring case).
  - Database 'reporting' has unknown Provider 'Postgress'; use one of SqlServer, Oracle, Postgres, MySql.
```

A database whose `PasswordEnvVar` isn't set doesn't stop the server. It's logged as a warning,
and only that database is unavailable.

## Environment variables

Any setting can come from the environment, with `__` separating the levels:

```sh
App__Urls=http://0.0.0.0:8080
App__MaxRows=500
App__AgentTokens__reporting=<token>
Databases__0__ConnectionString='Host=db;Username=agent;Password={password}'
```

The environment picks the `appsettings.<environment>.json` file. Over HTTP it comes from
`ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT`, and in stdio mode from `DOTNET_ENVIRONMENT` only. When neither is set it's
`Production`, which is also what the Docker image sets.

For local development, put real connection strings in `appsettings.Development.json`, which git
ignores, and run with `ASPNETCORE_ENVIRONMENT=Development` (or `DOTNET_ENVIRONMENT=Development`
with `--stdio`).
