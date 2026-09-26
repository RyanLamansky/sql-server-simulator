# Alias types (UDDTs) — `CREATE TYPE … FROM …`

Scalar alias types (also called UDDTs — user-defined data types) bind a name to an existing built-in type plus a nullability default.
The 6 AdventureWorks alias types (`AccountNumber` / `Flag` / `Name` / `NameStyle` / `OrderNumber` / `Phone`) drive the canonical shape.

## Storage

`AliasType` (`src/SqlServerSimulator/AliasType.cs`) carries the underlying `SqlType`, the alias's nullability default, name, schema, and `user_type_id`.
`Schema.AliasTypes` is the per-schema `ConcurrentDictionary<string, AliasType>` keyed by name (case-insensitive via `Collation.Baseline`).
Shares the type-name namespace with `TableTypes` — duplicate-name collision across either dict raises **Msg 219** verbatim.

`user_type_id` allocation: per-database counter starting at 256, advanced by `Database.AllocateUserTypeId`.
The underlying built-in's `system_type_id` propagates through to `sys.types` (e.g. `nvarchar`-backed alias → `system_type_id=231`).

## Grammar

```
CREATE TYPE [schema.]name FROM <builtin>[(N[, S])] [NULL | NOT NULL]
DROP TYPE [IF EXISTS] [schema.]name
```

Nullability default:
- `CREATE TYPE T FROM int` and `FROM int NULL` both set `IsNullable=true` (probe-confirmed).
- `FROM int NOT NULL` sets `IsNullable=false`.

The alias default propagates when a consumer omits the explicit marker (column / variable / parameter).
Column-site explicit `NULL` / `NOT NULL` overrides the alias default.
The built-in `sysname` is itself such an alias, declared NOT NULL, so a bare `sysname` column is NOT NULL too — table variables included (probed 2026-09-25).

## Type-reference parsing at consumer sites

The simulator accepts 1- or 2-part dotted type names at every consumer site:
- `CREATE TABLE` column type
- `DECLARE @v <type>`
- `ALTER TABLE … ALTER COLUMN`
- `CREATE PROCEDURE` / `FUNCTION` / `SEQUENCE` parameter types
- `OPENJSON` columns clause
- `sp_executesql` parameter declarations

All route through `Simulation.ResolveTypeReference(BatchContext, MultiPartName, Name leaf, …)` which checks `Schema.AliasTypes` first and falls back to `SqlType.GetByName` for built-ins.

A length parameter at an alias-usage site (`c [dbo].[Name](100)`) raises **Msg 2716 St 3** verbatim — probe-confirmed against SQL Server 2025; distinct wording from the State-1 form that built-ins raise.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 219 | Duplicate type name (alias-vs-alias or alias-vs-table-type in the same schema). |
| 222 | The base isn't a built-in (an alias of an alias, an unknown name, any qualifier but `sys`, named as written) or is one of the built-ins real refuses — `hierarchyid`, `geography`, `geometry`, `rowversion` (named `timestamp`) and `sysname`, named canonically. |
| 15226 / 13657 / 42212 | An `xml` (with or without a schema collection), `json` or `vector` base, refused by name before its arguments are read. |
| 2716 St 3 | Length / precision / scale specified at the alias-usage site. |
| 218 | `DROP TYPE` on missing alias without `IF EXISTS`. |

The base-type refusals are raised when the statement runs, not while the batch compiles, and abort as under `SET XACT_ABORT ON` (probed 2026-09-26 against SQL Server 2025): earlier statements of the batch have run, a `TRY` catches the error with the transaction doomed, and uncaught it rolls the transaction back and ends the batch.
The base reads through the same multi-word synonym fold as every other type site (`national char varying(3)`, `double precision`), and an alias over `numeric` keeps that spelling everywhere a `numeric` column does — `sys.types.system_type_id` 108, `TYPE_NAME`, `sp_help`, and `DATA_TYPE` in `INFORMATION_SCHEMA.COLUMNS` / `PARAMETERS` / `ROUTINES` / `DOMAINS`.

## `sys.types` rows

Alias rows ship via `BuiltInResources.cs::EnumerateSysTypes`:
- `system_type_id` from the underlying built-in (e.g. 231 for nvarchar-backed, 56 for int-backed, 108 for one declared over `numeric`)
- `user_type_id` from the alias's per-database allocation (≥ 256)
- `schema_id` from the owning schema
- `is_user_defined = 1`
- `is_table_type = 0`
- `is_nullable` from the alias's stored marker

## Alias-typed columns and parameters

A declared column (`HeapColumn.AliasType`), procedure or function parameter and scalar return (`AliasType` / `ReturnAliasType`) keeps the alias it was written with, set wherever `ResolveTypeReference` resolves one — `CREATE TABLE`, `ALTER TABLE ADD` / `ALTER COLUMN`, a table variable, a table type, a multi-statement function's return table — and carried onto a temporal history table's copy.
The catalog then reports it as real does (probed 2026-09-26 against SQL Server 2025): `sys.columns` / `sys.parameters` `user_type_id` (so `TYPE_NAME` names the alias), `INFORMATION_SCHEMA.COLUMNS.DOMAIN_*`, `INFORMATION_SCHEMA.PARAMETERS.USER_DEFINED_TYPE_*`, `sp_help`'s Type column, and `syscolumns`' `xusertype` / `usertype`.
`DROP TYPE` of an alias still in use is **Msg 3732**, naming the referencing table, table type (by its `TT_…` backing name), procedure or function of lowest object id and the type as written.

Two places refuse an alias outright: a `#temp` table's column is **Msg 2715** (its types resolve in `tempdb`, where the database's aliases don't exist; a table variable's resolve here), and `CAST` / `CONVERT` to one is **Msg 243** at state 2 — state 1 for a name that isn't a type at all, which a qualified name reports whole.

Real treats the alias as part of an expression's type, so a projection carries it wherever the value passes through unchanged (`Expression.ResultAliasType`, probed 2026-09-26): a reference to an alias-typed column, variable or function return, `ISNULL`'s checked operand, CASE / IIF / GREATEST arms that agree, `MAX` / `MIN` / `SUM`, the value window functions and a scalar subquery's column — while arithmetic, CAST, a string function, COLLATE, `COALESCE`, `NULLIF` and `CHOOSE` drop it.
`Selection.ColumnAliasTypes` records it per column, so a view, derived table, CTE, UNION whose branches agree, and `SELECT … INTO` column keeps it, as does `sp_describe_first_result_set`'s `user_type_*` quartet; a `SELECT … INTO #temp` column doesn't, `tempdb` having no such type.

## Known gaps

- **Alias-type `max_length` not emitted in `sys.types`** — gap from the catalog view's shipped subset.
- **`vector`'s own argument check** — real validates `vector(n, base)`'s arguments before refusing the alias (`float16` is Msg 195); the simulator refuses by name first.
- **`DOMAIN_DEFAULT`** in `INFORMATION_SCHEMA.DOMAINS` is always NULL: it names a default bound with `sp_bindefault`, and `CREATE DEFAULT` / `sp_bindefault` aren't built yet.

See [`table-valued-parameters.md`](table-valued-parameters.md) for the parallel `CREATE TYPE … AS TABLE` shape (table types share the namespace + collision check).
