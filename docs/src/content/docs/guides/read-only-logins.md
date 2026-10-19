---
title: Read-only logins
description: Create a login for each database that passes sql-mcp's audit, and what the audit refuses.
---

Before serving a database, sql-mcp checks what its login can do. If the login could change
anything, or reach outside the database, every call on that database is refused and the server
log lists why. So each database needs a login that can only read.

## Creating a login that passes

**PostgreSQL**

```sql
CREATE ROLE agent LOGIN PASSWORD '...';
GRANT USAGE ON SCHEMA app TO agent;
GRANT SELECT ON ALL TABLES IN SCHEMA app TO agent;
-- or, to read everything: GRANT pg_read_all_data TO agent;
```

`GRANT SELECT ON ALL TABLES` covers only the tables that exist now. For tables created later, also
run `ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO agent;` as the role that
creates them.

**MySQL**

```sql
CREATE USER 'agent'@'%' IDENTIFIED BY '...';
GRANT SELECT ON app.* TO 'agent'@'%';
```

**SQL Server**

```sql
CREATE LOGIN agent WITH PASSWORD = '...';
-- in the database:
CREATE USER agent FOR LOGIN agent;
ALTER ROLE db_datareader ADD MEMBER agent;
```

**Oracle**

```sql
CREATE USER agent IDENTIFIED BY "...";
GRANT CREATE SESSION TO agent;
GRANT SELECT ON app.orders TO agent;
GRANT SELECT ON app.customers TO agent;
```

Grant `SELECT` only on what agents should see. An agent can read anything its login can read.

## What the audit refuses

| Provider | Refused when the login |
|----------|------------------------|
| PostgreSQL | is `SUPERUSER`, `CREATEROLE` or `CREATEDB`; is in `pg_write_all_data`, `pg_write_server_files`, `pg_read_server_files`, `pg_execute_server_program` or `pg_signal_backend`; can insert, update, delete or truncate any table; can execute `dblink` or functions in an untrusted language |
| MySQL | holds any privilege beyond `SELECT`, `SHOW VIEW`, `SHOW DATABASES`, `PROCESS`, `REPLICATION CLIENT`, `EXECUTE` and `USAGE`, directly or through its default roles. So `INSERT`, `FILE`, `SUPER` and `ALL` are refused. |
| SQL Server | is `sysadmin` or has `CONTROL SERVER`; has database-level `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `CONTROL`, `TAKE OWNERSHIP` or any `CREATE ...` or `ALTER ANY ...`; can insert, update, delete or alter any table, view or sequence |
| Oracle | has a system privilege beyond `CREATE SESSION`, `SET CONTAINER`, `SELECT ANY TABLE`, `READ ANY TABLE` and `SELECT ANY DICTIONARY`; has a non-read object privilege (such as `EXECUTE` on a function) other than through `PUBLIC`; owns tables, sequences or code; can see a database link |

Why it matters even though queries are already checked and rolled back: neither of those can see
inside a function. On Oracle, a function with `PRAGMA AUTONOMOUS_TRANSACTION` commits its own
writes even when it's called from a read-only transaction that is then rolled back. The audit
makes sure the login has no such function to call. [How queries stay
read-only](/sql-mcp/security/) explains how the layers fit together.

A login that can't be checked, for example because a catalog query fails, is treated as writable.

## When it's checked

The audit runs on the first call to a database and its result is kept for five minutes. A later
`GRANT` is noticed within five minutes, without a restart. `GET /health/ready` runs it for every
database and reports which ones pass.

When a login fails, the call returns:

```
Database 'prod' is disabled because its login can modify data. The server log lists why.
```

and the server log names each problem it found:

```
fail: SqlMcp.Services.QueryService[0]
      Refusing queries on database prod: its login is not read-only. It can modify tables, views or sequences: [dbo].[orders]. Use a login that can only read, or set AllowWritableLogin to true for this database to accept the risk.
```

## Serving a writable login anyway

If a database has no read-only login and you accept the risk, set `AllowWritableLogin` on it:

```json
{ "Name": "legacy", "Provider": "SqlServer", "ConnectionString": "...", "AllowWritableLogin": true }
```

The audit is skipped for that database, and a warning is logged at startup. Statement checks and
the rolled-back transaction still apply, but a function that writes can no longer be ruled out.

## What the audit doesn't see

- **Grants made after a passing audit**, until its five minutes are up.
- **SQL Server CLR functions** created with `EXTERNAL_ACCESS` or `UNSAFE` that the login can call.
- **SQL Server extended stored procedures called from a function.** The SQL Server audit doesn't
  check which functions the login can execute. Running `xp_cmdshell` this way still needs
  `sysadmin`, which the audit refuses, or a proxy account someone has set up.
