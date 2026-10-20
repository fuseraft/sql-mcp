---
title: Development
description: Build sql-mcp, run its tests against all four databases, and preview these docs.
---

## Build and test

```sh
dotnet build
dotnet test
```

The validator, configuration and HTTP tests always run. The integration tests run against each
database whose connection string is set, and are skipped for the others. Use an administrator
login: the tests create their own tables, a view, functions, and a read-only `sqlmcp_ro` login to
test the audit against.

```sh
export SQLMCP_TEST_POSTGRES='Host=localhost;Port=55432;Username=postgres;Password=pw'
export SQLMCP_TEST_MYSQL='Server=localhost;Port=53306;Database=t;User Id=root;Password=pw'
export SQLMCP_TEST_SQLSERVER='Server=localhost,51433;User Id=sa;Password=Str0ng!Passw0rd;TrustServerCertificate=true'
export SQLMCP_TEST_ORACLE='Data Source=localhost:51521/FREEPDB1;User Id=system;Password=pw'
dotnet test
```

## Test databases in containers

These match CI's service containers, on local ports:

```sh
podman run -d --name sqlmcp-pg  -p 55432:5432 -e POSTGRES_PASSWORD=pw docker.io/library/postgres:16-alpine
podman run -d --name sqlmcp-my  -p 53306:3306 -e MYSQL_ROOT_PASSWORD=pw -e MYSQL_DATABASE=t docker.io/library/mysql:8.4
podman run -d --name sqlmcp-ms  -p 51433:1433 -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Str0ng!Passw0rd' mcr.microsoft.com/mssql/server:2022-latest
podman run -d --name sqlmcp-ora -p 51521:1521 -e ORACLE_PASSWORD=pw docker.io/gvenzl/oracle-free:23-slim-faststart
```

Oracle takes a minute or so to accept connections the first time. With binary logging on (the
default in MySQL 8), MySQL refuses to create the tests' function unless you allow it, and the
setting doesn't survive a restart:

```sh
podman exec sqlmcp-my mysql -uroot -ppw -e "SET GLOBAL log_bin_trust_function_creators = 1"
```

## CI

`.github/workflows/ci.yml` runs on every push to `master` and every pull request:

- the full suite against Postgres 16, MySQL 8.4, SQL Server 2022 and Oracle 23 Free service
  containers. The job fails if any test was skipped, so a database that didn't start can't let it
  pass.
- a build with warnings treated as errors, and a check for vulnerable packages.
- a smoke test of the Docker image: it refuses to start without a token, serves `/health`,
  returns 401 on `/mcp` without the token, and runs as non-root.

## Project layout

| Path | Holds |
|------|-------|
| `Program.cs` | Transport selection (stdio or HTTP) and the HTTP endpoints |
| `Tools/SqlTools.cs` | The MCP tool definitions |
| `Services/QueryService.cs` | Configuration, the login audit cache, read-only transactions, result limits |
| `Services/SqlGuard.cs` | Provider-aware statement validation |
| `Services/LoginAudit.cs` | The per-provider check that a login can only read |
| `Services/SchemaCatalog.cs` | Catalog queries behind `list_tables` and `describe_table` |
| `Services/QueryPlans.cs` | Estimated plans for `explain_query` |
| `Services/QueryParameters.cs` | Parsing and binding parameters |
| `Services/ResultValues.cs` | Converting database values to JSON |
| `Http/HttpSecurity.cs` | Bearer tokens, the Origin check and safe binding |
| `Http/HealthCheck.cs` | `--healthcheck` for container probes |
| `Models/` | Request and result types |
| `tests/SqlMcp.Tests/` | Unit, HTTP and integration tests |
| `docs/` | This site |

## These docs

The site is built with [Starlight](https://starlight.astro.build) from `docs/`:

```sh
cd docs
npm install
npm run dev
```

Pull requests that touch `docs/` build it, and pushes to `master` deploy it to GitHub Pages.
