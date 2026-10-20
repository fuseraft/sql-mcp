---
title: Tools
description: Every MCP tool sql-mcp offers, with its arguments and what it returns.
---

sql-mcp offers five tools. All are annotated `readOnlyHint: true`, and every call that touches a
database goes through the login audit and a transaction that is rolled back (see
[How queries stay read-only](/sql-mcp/security/)).

Results are JSON text. A call that can't be answered (an unknown database or table, a rejected
statement, invalid parameters, a database error) returns a result with `isError: true` and a
message the agent can act on.

## `list_databases`

No arguments. Returns each configured database with its provider:

```json
[{ "name": "prod", "provider": "Postgres" }, { "name": "legacy", "provider": "SqlServer" }]
```

The provider is one of `SqlServer`, `Postgres`, `MySql` and `Oracle`, and tells the agent which
dialect to write.

## `list_tables`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `schema` | string, optional | Only list this schema (on MySQL, a database name). Matched ignoring case. |
| `search` | string, optional | Only list tables whose name or comment contains this text, ignoring case. `%`, `_` and `[` are matched as text, not wildcards. |

Returns the tables and views the login can read, sorted by schema and name:

```json
{ "tables": [
  { "schema": "sales", "name": "orders", "type": "table", "approxRows": 1250000, "comment": "One row per order placed" },
  { "schema": "sales", "name": "open_orders", "type": "view" } ], "truncated": false }
```

- `type` is `table` or `view`.
- `approxRows` is the database's estimate from its statistics: `reltuples` on PostgreSQL,
  `sys.partitions` on SQL Server, `table_rows` on MySQL, `num_rows` on Oracle. It's left out for
  views and for tables whose statistics were never gathered.
- `comment` is the table's comment, shortened to 200 characters, and left out when there is none.
- System schemas (`pg_catalog`, `information_schema`, MySQL's `mysql` and `sys`, Oracle-maintained
  schemas such as `SYS`) are left out unless you name one with `schema`. On SQL Server only the
  database in the connection string is listed.
- At most `App:MaxTables` (1000) entries are returned. Beyond that, `truncated` is `true` and a
  `note` suggests `search` or `schema`.

## `describe_table`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `table` | string | Table or view name, matched ignoring case. `schema.table` is accepted. |
| `schema` | string, optional | Needed only when the name exists in more than one schema. Without it, every non-system schema is searched. |

Returns the table's comment and estimated row count (as in `list_tables`, with the comment in
full), its columns in order, and its foreign keys in both directions:

```json
{ "schema": "sales", "name": "orders", "type": "table", "comment": "One row per order placed", "approxRows": 1250000,
  "columns": [
    { "name": "id", "dataType": "integer", "nullable": false, "primaryKey": true },
    { "name": "customer_id", "dataType": "integer", "nullable": false, "primaryKey": false, "comment": "Who placed the order" },
    { "name": "status", "dataType": "smallint", "nullable": false, "primaryKey": false, "comment": "1 open, 2 shipped, 3 cancelled" } ],
  "foreignKeys": [{ "columns": ["customer_id"], "referencedTable": "sales.customers", "referencedColumns": ["id"] }],
  "referencedBy": [{ "table": "sales.order_lines", "columns": ["order_id"], "referencedColumns": ["id"] }] }
```

- `dataType` is spelled as the database spells it: `character varying(20)`, `nvarchar(max)`,
  `NUMBER(10,2)`.
- `foreignKeys` are the keys this table holds. `referencedBy` are other tables' keys that point at
  this table, where `referencedColumns` are this table's columns. Multi-column keys keep their
  column order.
- A key is listed only if the login can read the table on its other end. A key from a table to
  itself is listed once, under `foreignKeys`.
- A table the login can't read is reported as not found. A name in more than one schema is an
  error listing the matches, so the agent can pass `schema`.

## `query_database`

| Argument | Type | Description |
|----------|------|-------------|
| `dbName` | string | Database name from `list_databases`. |
| `sql` | string | One `SELECT` or `WITH` query. A single trailing `;` is allowed. |
| `parameters` | object, optional | Values for placeholders in `sql`, by name: strings, numbers, booleans or `null`. |

Returns the result, each row an array of values in the order of `columns`:

```json
{ "columns": ["id", "name"], "rows": [[1, "Ada"], [2, "Grace"]], "truncated": false }
```

A result cut short by `App:MaxRows` (100 rows) or `App:MaxResultBytes` (about 100 KB) has
`truncated: true` and a `note` saying which limit stopped it and how to narrow or page the query,
with paging in the database's own syntax:

```json
{ "columns": ["id"], "rows": [[1], [2]], "truncated": true,
  "note": "Only the first 100 rows are shown (App:MaxRows). Narrow the query with WHERE, summarize it with GROUP BY, or page through it with ORDER BY ... LIMIT 100 OFFSET 100." }
```

### Parameters

Values in `parameters` are sent to the database separately from the query text, so they can't
change its structure. Placeholders use each database's own syntax:

| Provider | Placeholder |
|----------|-------------|
| SQL Server, PostgreSQL, MySQL | `@name` |
| Oracle | `:name` |

```json
{ "dbName": "prod", "sql": "SELECT id, total FROM orders WHERE customer = @customer AND total >= @min",
  "parameters": { "customer": "O'Brien", "min": 100 } }
```

- Names are letters, digits and underscores, starting with a letter or underscore. A leading `@` or
  `:` in the key is ignored. Up to 100 per query, and a placeholder can be used more than once.
- Whole numbers are bound as 64-bit integers, other numbers as decimals.
- On PostgreSQL, strings are sent untyped, so the server infers the type from where the
  placeholder is used, as it does for a quoted literal: `WHERE created_at > @since` with
  `"2024-01-01"` compares dates. Where it can't infer a type (`@x IS NULL`), cast the placeholder:
  `CAST(@x AS text) IS NULL`.

### Column names and values

Every column has a distinct name. Unnamed columns are `column1`, `column2`, …, and repeated names
get a suffix, so `SELECT a.id, b.id` returns `id` and `id_2`.

Values JSON can't hold are converted:

- `NaN` and infinite floats become the strings `"NaN"`, `"Infinity"` and `"-Infinity"`.
- Oracle `NUMBER`s beyond 28 significant digits are rounded (`1/3`), and integers too large for a
  decimal come back as strings.
- Binary values are base64.
- Types without a JSON form (PostgreSQL `inet`, ranges and geometric types; Oracle intervals) come
  back as their text.
- Text and binary values longer than `App:MaxValueLength` (10,000 characters) are cut off and
  marked `… [truncated: N characters in total]`.

A value the driver can't read at all, such as a SQL Server `geography` column, is replaced by a
note saying so, and the rest of the result is unaffected. Select it as text instead, for example
`geo.ToString()`.

## `explain_query`

Takes the same arguments as `query_database`, and the query passes the same checks, but it isn't
run. The database's estimated plan comes back instead:

```json
{ "plan": "Index Scan using orders_customer_idx on orders  (cost=0.43..8.45 rows=12 width=24)\n  Index Cond: (customer_id = '42'::bigint)" }
```

| Provider | Plan from |
|----------|-----------|
| PostgreSQL | `EXPLAIN`, never `EXPLAIN ANALYZE`, which would run the query |
| MySQL | `EXPLAIN FORMAT=TREE` |
| SQL Server | `SET SHOWPLAN_ALL ON`, under which nothing in the batch runs. Each operator is shown with its estimated rows and cost. |
| Oracle | `EXPLAIN PLAN`, read back with `DBMS_XPLAN.DISPLAY` |

- On PostgreSQL and MySQL, the parameters' values are bound as for a query, so the plan reflects
  them.
- SQL Server and Oracle plan the query without the values. SQL Server declares each parameter
  with a type taken from its value, and Oracle plans its `:name` placeholders as unknown.
- A plan longer than `App:MaxResultBytes` is cut off.
- On Oracle, `EXPLAIN PLAN` writes the plan to `PLAN_TABLE`, which a read-only transaction
  refuses, so there it runs in an ordinary transaction that is still rolled back. The query is
  only parsed.
