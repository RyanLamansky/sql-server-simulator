# Extended properties

Pure metadata — no semantic effect on queries.
The sproc trio, `sys.extended_properties` catalog view, and `fn_listextendedproperty` system TVF all ship.

## Storage

`Database.ExtendedProperties` is a `ConcurrentDictionary<ExtendedPropertyKey, SqlValue>` keyed by `(byte class, int major_id, int minor_id, string name)`.
`ExtendedPropertyKey` is a readonly struct overriding `Equals` / `GetHashCode` so the name comparison routes through `Collation.Baseline` (case-insensitive).
Per-DB flat dict mirrors `sys.extended_properties`'s catalog shape — not per-schema.

## Sproc trio

`Simulation.ExtendedProperties.cs` (partial).
`Simulation.Exec.cs` dispatches three branches after the `sp_executesql` route — each forwards to the shared `InvokeSpExtendedProperty(batch, ExtendedPropertyOp)` body:

- `sp_addextendedproperty` — add (Msg 15233 on duplicate)
- `sp_updateextendedproperty` — update (Msg 15217 on missing)
- `sp_dropextendedproperty` — drop (Msg 15217 on missing)

Named-arg parsing handles 8 args: `@name`, `@value`, `@level0type` / `@level0name` / `@level1type` / `@level1name` / `@level2type` / `@level2name`.
Argument-name comparison drops the `@` prefix (the `AtPrefixedString` token's `Value` is already `@`-stripped).
Target resolution routes through `ResolveExtendedPropertyTarget`.

### Recognized level types

**Level 0 (schema-container or database-scoped host):** `SCHEMA`, plus the two terminal database-scoped hosts `TRIGGER` (a database DDL trigger → class 1 OBJECT_OR_COLUMN, major_id = trigger object_id resolved through `Database.DdlTriggers`) and `FILEGROUP` (→ **class 20 DATASPACE**, major_id = `data_space_id` resolved through `Database.Filegroups`).
Both are terminal — later levels don't apply — and probe-confirmed against SQL Server 2025 (`sp_addextendedproperty @name=…, @value=…, @level0type=N'TRIGGER'|N'FILEGROUP', @level0name=…`).
`class_desc` for 20 is `DATASPACE`.
These two land through the BACPAC loader (AW's `[SqlDatabaseDdlTrigger].[ddlDatabaseTriggerLog].[MS_Description]` + `[SqlFilegroup].[PRIMARY].[MS_Description]`); a filegroup EP is only reachable from public SQL after a bacpac registers a filegroup (no `CREATE FILEGROUP`), while a DDL-trigger EP is reachable directly (`CREATE TRIGGER … ON DATABASE` + the sproc).

**Level 1:** `TABLE` / `VIEW` / `PROCEDURE` / `FUNCTION` / `TYPE`.
**Level 2:** `COLUMN`.
The level-2 resolver also handles `CONSTRAINT` (reuses class=1 OBJECT_OR_COLUMN with the constraint's own object_id as major_id; walks `HeapTable.KeyConstraints` / `CheckConstraints` / `OutgoingForeignKeys` / `Columns[].DefaultConstraint`) and `INDEX` (class=7, `(major_id=table.object_id, minor_id=index_id)` via `ComputeIndexId` mirroring `sys.indexes`'s enumeration: PK=1, others sequential in ObjectId order).

## Errors enforced verbatim

Probe-confirmed against SQL Server 2025.

| Msg | When |
|---|---|
| 15233 | Duplicate add: `"Property cannot be added. Property 'X' already exists for 'Y'."` |
| 15217 | Update / drop on missing property, same target-label convention as 15233. |
| 15135 | Missing target object: `"Object is invalid. Extended properties are not permitted on '<target>', or the object does not exist."` |
| 15600 | Invalid parameters (positional arg, unknown @-name, missing required arg, unknown level type). |

**Target-label convention** for Msg 15233 / 15217:
- DB-level → `'object specified'`
- Schema → `'<schema>'`
- Table / view / proc / func → `'<schema>.<name>'`
- Column → `'<schema>.<table>.<col>'`

## `sys.extended_properties`

`BuiltInResources.cs::EnumerateSysExtendedProperties` ships the 6-column subset:

| Column | Notes |
|---|---|
| `class` (tinyint) | 0=DB, 1=OBJECT_OR_COLUMN, 3=SCHEMA, 7=INDEX, 20=DATASPACE |
| `class_desc` (sysname) | `DATABASE` / `OBJECT_OR_COLUMN` / `SCHEMA` / `INDEX` / `DATASPACE` |
| `major_id` (int) | DB=0, schema=schema_id, object=object_id |
| `minor_id` (int) | 0 for tables/views/procs/funcs; 1-based column ordinal for columns; index_id for INDEX class |
| `name` (sysname) | Property name |
| `value` (sql_variant) | Wraps the stored value's own `SqlValue`, so its base type survives (nvarchar for an `N'…'` input, varchar for a plain literal) — probe-confirmed real behavior: `@value=N'…'` stores base type nvarchar, `@value='…'` stores varchar. DacFx reads the base type off the wire (via `SQL_VARIANT_PROPERTY` / the sql_variant TDS form) to re-script the value with the correct N-prefix, so a BACPAC round-trip preserves nvarchar. |

### Value base-type fidelity

The dict holds the raw `SqlValue` the sproc stored (`sp_addextendedproperty` assigns `arg.Value` verbatim — no coercion), so an `N'…'` literal keeps its `NVarcharSqlType` and a plain `'…'` literal its `VarcharSqlType`.
`sys.extended_properties.value` wraps that value in a `sql_variant` at enumeration time (`SqlValue.FromVariant`), matching real SQL Server's `sql_variant` column.
`fn_listextendedproperty.value` still surfaces as `nvarchar(MAX)` (its TVF schema is a fixed shape; DacFx's export reads `sys.extended_properties`, not the TVF).

## `fn_listextendedproperty`

`Selection.ListExtendedProperty.cs` is a built-in system TVF dispatched alongside `OPENJSON` / `STRING_SPLIT` in `ParseSingleFromSource`.

```sql
fn_listextendedproperty(@name, @level0type, @level0name,
                                @level1type, @level1name,
                                @level2type, @level2name)
```

Each arg may be NULL; returns 4 columns: `objtype`, `objname`, `name`, `value`.
Pipeline: parse each arg expression → eval to nullable string → build `ExtendedPropertyListFilter` from the resolved target → walk `Database.ExtendedProperties` → project matches.

The `'default'` wildcard at any level-name slot fans out across every object of that level-type under the parent (probe-confirmed).
Missing target returns zero rows (distinct from the sproc path's Msg 15135).
Unknown level0/1/2 type raises `NotSupportedException`.

## Known gaps

- **PARAMETER level type** — not modeled (raises Msg 15600).
  AW doesn't exercise it.
  (`TRIGGER` / `FILEGROUP` level-0 hosts now ship — see [Recognized level types](#recognized-level-types).)
- **`fn_listextendedproperty` value type** — surfaced as nvarchar(MAX) rather than sql_variant (the TVF's schema is fixed for parse/plan parity).
  `sys.extended_properties.value` is a genuine sql_variant preserving the input base type; only the TVF read-path is lossy.
  DacFx's export uses `sys.extended_properties`, so this doesn't affect BACPAC round-trip.
