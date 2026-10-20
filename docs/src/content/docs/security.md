---
title: How queries stay read-only
description: The three layers every query passes before it reaches your data, and what an agent can still do.
---

Queries come from AI agents, which can be wrong, confused, or steered by text they read. So
sql-mcp doesn't rely on any one check. Every query passes three layers, and each one covers what
the others can't see.

```
agent's SQL
  │
  ├─ 1. Statement validation    one SELECT, nothing that writes, calls out, or runs SQL in a string
  │
  ├─ 2. Login audit             the login itself can't write or reach outside the database
  │
  └─ 3. Transaction             read-only where the database supports it, and always rolled back
        │
        └─ your data
```

## 1. Statement validation

Before connecting, sql-mcp tokenizes the query with that database's own rules for comments,
string literals and quoted identifiers: PostgreSQL dollar quotes and nested comments, MySQL
backticks and `#` comments, Oracle `q'[...]'` quotes, SQL Server `[brackets]`. Words inside
literals and comments can't fool it, and words outside them can't hide. A query is accepted only
if it:

- is a single statement starting with `SELECT` or `WITH`. One trailing `;` is allowed and
  stripped.
- contains no data-modifying or session-control keyword outside literals, such as `INSERT`,
  `UPDATE`, `DELETE`, `MERGE`, `INTO`, `FOR UPDATE`, `EXEC`, `SET`, `COMMIT` or `GRANT`. So
  `SELECT * INTO copy ...`, `WITH d AS (DELETE ...)` and SQL Server batches like
  `SELECT 1 DROP TABLE t` are all rejected.
- doesn't advance a sequence (`NEXTVAL`, `nextval()`, `NEXT VALUE FOR`), since that isn't undone
  by a rollback.
- calls none of the known functions that reach outside the database, read server files or run SQL
  held in a string: `dblink_exec`, `pg_read_file`, `query_to_xml`, `LOAD_FILE`, `UTL_HTTP`,
  `DBMS_XMLGEN`, `OPENROWSET`, `sp_executesql` and others, including when the name is quoted
  (`"dblink_exec"(...)`).
- has no unterminated literal or comment, no MySQL executable comment (`/*! ... */`) and no
  PostgreSQL `U&"..."` escaped identifier.

Where a server setting changes how backslashes in literals are read (MySQL's
`NO_BACKSLASH_ESCAPES`, PostgreSQL's `standard_conforming_strings`), the query must pass under
both readings, so it can't hide text from the check.

This rules out stored procedures too: `EXEC`, `CALL` and Oracle `BEGIN ... END` blocks can't start
a query, and `EXEC` is rejected anywhere in one.

## 2. Login audit

Validation can't see inside a function. `SELECT my_function()` is a valid read, whatever the
function does. So before serving a database, sql-mcp checks what its login can do, and refuses
every call on that database if the login could change anything or reach outside it.

That closes the gap the other layers leave. An Oracle function with
`PRAGMA AUTONOMOUS_TRANSACTION` commits its own writes even inside a read-only transaction that's
rolled back, but a login that passes the audit has no such function to call.

[Read-only logins](/sql-mcp/guides/read-only-logins/) has what each provider's audit refuses, how
to create a login that passes, and the few things it can't see.

## 3. Read-only transaction, always rolled back

Every call runs inside a transaction that is rolled back afterwards, whatever happens. On
PostgreSQL, MySQL and Oracle the transaction is also read-only (`SET TRANSACTION READ ONLY` or
`START TRANSACTION READ ONLY`), so the database itself refuses writes that validation couldn't
see. SQL Server has no read-only transaction mode, so there the rollback is the backstop.

Two details:

- On Oracle, a read-only transaction started within seconds of a table's last DDL fails with
  `ORA-01466`. sql-mcp retries those for a few seconds.
- Oracle's `EXPLAIN PLAN` writes the plan to `PLAN_TABLE`, which a read-only transaction refuses.
  So `explain_query` on Oracle runs in an ordinary transaction, still rolled back. The query is
  only parsed, never run, and the login audit still applies.

## What an agent can still do

Read anything the login can read. Grant `SELECT` only on what agents should see. Bind values
through `parameters` so they can't change a query's structure; validation still applies to the
SQL text itself, so it catches statements injected into SQL an agent built by concatenating
strings.

An agent can also run expensive reads. `App:QueryTimeoutSeconds`, `App:MaxConcurrentQueries` and
the row and size limits bound what one agent can cost a database. `explain_query` lets it check
first.

## The HTTP transport

- By default the server listens on localhost only, and it refuses to listen beyond it without
  agent tokens unless `App:AllowUnauthenticated` is set.
- Each agent has its own token, so every call is attributed and one agent can be revoked by
  removing its entry and restarting.
- Requests with an `Origin` header not listed in `App:AllowedOrigins` are rejected, so a web page
  can't reach a local server through DNS rebinding.
- The stdio transport is reachable only by the process that launched it.

## Credentials

Connection strings can be committed: put `{password}` where the password goes and name the
environment variable to fill it from with `PasswordEnvVar`. Agent tokens come from the
environment too. Neither is ever written to the log.
