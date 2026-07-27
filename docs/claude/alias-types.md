# Alias types (UDDTs) — `CREATE TYPE … FROM …`

Scalar alias types (also called UDDTs — user-defined data types) bind a name to an existing built-in type plus a nullability default.
The 6 AdventureWorks alias types (`AccountNumber` / `Flag` / `Name` / `NameStyle` / `OrderNumber` / `Phone`) drive the canonical shape.

## Storage

`AliasType` (`src/SqlServerSimulator/AliasType.cs`) carries the underlying `SqlType`, the alias's nullability default, name, schema, and `user_type_id`.
`Schema.AliasTypes` is the per-schema `ConcurrentDictionary<string, AliasType>` keyed by name (case-insensitive via `Collation.Baseline`).
Shares the type-name namespace with `TableTypes` — duplicate-name collision across either dict raises **Msg 219** verbatim.

`user_type_id` allocation: per-database counter starting at 256, advanced by `Database.AllocateAliasTypeId`.
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
| 222 | `The base type "X" is not a valid base type for the alias data type.` — also raised on alias-of-alias attempts (probe-confirmed). |
| 2716 St 3 | Length / precision / scale specified at the alias-usage site. |
| 218 | `DROP TYPE` on missing alias without `IF EXISTS`. |

## `sys.types` rows

Alias rows ship via `BuiltInResources.cs::EnumerateSysTypes`:
- `system_type_id` from the underlying built-in (e.g. 231 for nvarchar-backed, 56 for int-backed)
- `user_type_id` from the alias's per-database allocation (≥ 256)
- `schema_id` from the owning schema
- `is_user_defined = 1`
- `is_table_type = 0`
- `is_nullable` from the alias's stored marker

## Known gaps

- **`HeapColumn` doesn't carry a back-pointer to its declaring `AliasType`.**
  Consequence: `sys.columns.user_type_id` surfaces the underlying built-in's id (not the alias's) when a column is alias-typed, and `DROP TYPE` on an alias type doesn't enforce **Msg 3732** (referenced-by-object).
  Real bacpac load never drops alias types during import, so this is acceptable for the baseline.
- **Alias-type `max_length` not emitted in `sys.types`** — gap from the catalog view's shipped subset.
- **Alias-of-alias not modeled** — `CREATE TYPE T2 FROM T1` where T1 is an alias raises Msg 222 (matches probe behavior).

See [`table-valued-parameters.md`](table-valued-parameters.md) for the parallel `CREATE TYPE … AS TABLE` shape (table types share the namespace + collision check).
