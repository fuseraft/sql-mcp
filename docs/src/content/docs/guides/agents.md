---
title: Working with agents
description: How agents use sql-mcp's tools, and what you can do so their answers are right.
---

sql-mcp's tools are built around the way an agent works through an unfamiliar database: find the
database, find the table, learn its shape, check the cost, then query. This page covers what each
step gives the agent, and what you can do on the database side so the answers come out right.

## The usual sequence

1. **`list_databases`** returns each database's name and provider (`Postgres`, `SqlServer`,
   `MySql`, `Oracle`). The provider tells the agent which dialect to write: `LIMIT`, `TOP` or
   `FETCH FIRST`, and which date and string functions exist.
2. **`list_tables`** lists what the login can read, with each table's comment and estimated row
   count. On a large schema the agent passes `search` (`"order"`) to find candidates by name or
   comment instead of reading a thousand names.
3. **`describe_table`** gives the columns with their types, nullability, primary keys and
   comments, plus foreign keys in both directions. Joins come from `foreignKeys` and
   `referencedBy` rather than guesses from column names.
4. **`explain_query`** shows the estimated plan without running the query. It's worth it before
   querying a table whose `approxRows` is in the millions.
5. **`query_database`** runs one `SELECT`.

The [tools reference](/sql-mcp/reference/tools/) has every argument and result.

## Helping agents get it right

Most wrong answers from text-to-SQL come from the schema, not the SQL: a join on the wrong
column, or a status code read the wrong way. Three things on the database side help most.

**Write comments.** An agent can't know that `status = 3` means cancelled, or that `amount` is in
cents. A comment says so, and sql-mcp shows it on every `describe_table`:

```sql
COMMENT ON COLUMN sales.orders.status IS '1 open, 2 shipped, 3 cancelled';      -- PostgreSQL, Oracle
ALTER TABLE orders MODIFY status SMALLINT NOT NULL COMMENT '1 open, 2 shipped, 3 cancelled';  -- MySQL
EXEC sys.sp_addextendedproperty N'MS_Description', N'1 open, 2 shipped, 3 cancelled',
     N'SCHEMA', N'sales', N'TABLE', N'orders', N'COLUMN', N'status';            -- SQL Server
```

Table comments appear in `list_tables` too, shortened to 200 characters, which also makes
`search` find tables by what they hold.

**Declare foreign keys.** A key the database knows about is a join the agent doesn't have to
guess. Keys to tables the login can't read are left out, since the agent couldn't use them anyway.

**Keep statistics current.** `approxRows` comes from the database's statistics: `ANALYZE` on
PostgreSQL, `ANALYZE TABLE` on MySQL, `DBMS_STATS` on Oracle, and SQL Server's own partition
counts. A table that has never been analyzed shows no estimate.

## Values and parameters

Agents should pass values through `parameters` rather than writing them into the SQL:

```json
{ "dbName": "prod",
  "sql": "SELECT id, total FROM orders WHERE customer = @customer AND total >= @min",
  "parameters": { "customer": "O'Brien", "min": 100 } }
```

A bound value is sent separately from the query text, so `O'Brien` needs no escaping and a value
like `x' OR '1'='1` is only ever compared as a string. Placeholders are `@name` on SQL Server,
PostgreSQL and MySQL, and `:name` on Oracle.

## Large results

Results are capped at `App:MaxRows` (100) rows and `App:MaxResultBytes` (100,000 bytes, about
25,000 tokens) so one query can't flood the agent's context. A capped result says so:

```json
{ "columns": ["id", "total"], "rows": [[1, 20.5], …], "truncated": true,
  "note": "Only the first 100 rows are shown (App:MaxRows). Narrow the query with WHERE, summarize it with GROUP BY, or page through it with ORDER BY ... LIMIT 100 OFFSET 100." }
```

The paging clause in the note is written in the database's own syntax. Agents generally do better
asking the database to aggregate (`COUNT`, `SUM`, `GROUP BY`) than paging through raw rows.

## Errors are part of the conversation

Refusals and database errors come back as tool results marked `isError`, with a message the
agent can act on:

- `Only SELECT queries (including CTEs) are permitted.`
- `'INTO' is not permitted in a read-only query.`
- `Table 'order' was not found in database 'prod', or the login can't read it. Call list_tables to see what it can read.`
- `'orders' matches more than one table: sales.orders, archive.orders. Pass schema to choose one.`
- `Query timed out after 30 s (App:QueryTimeoutSeconds). Narrow it with a WHERE clause or an index, or ask for fewer rows.`
- `Query error: Unknown column 'totl' in 'field list'` (the database's own message, here MySQL's)

A capable agent reads the message and fixes its next call.

## Instructions worth giving the agent

If your client takes a system prompt or an `AGENTS.md`, a few lines help:

```md
- Use the sql tools to answer questions about data; never guess numbers.
- Check list_databases for each database's provider and write that dialect.
- Read describe_table before querying a table, and join on its foreign keys.
- Use explain_query before querying tables with more than a few million rows.
- Prefer one aggregate query to paging through rows. Say when a result was truncated.
```
