# Extended properties

Pure metadata — no semantic effect on queries.
The sproc trio, `sys.extended_properties` catalog view, and `fn_listextendedproperty` system TVF all ship.

## Storage

`Database.ExtendedProperties` is a `ConcurrentDictionary<ExtendedPropertyKey, SqlValue>` keyed by `(byte class, int major_id, int minor_id, string name)`.
`ExtendedPropertyKey` is a readonly struct overriding `Equals` / `GetHashCode` so the name comparison routes through `Collation.Baseline` (case-insensitive).
Per-DB flat dict mirrors `sys.extended_properties`'s catalog shape — not per-schema.

Real removes a property along with whatever it describes — a table, column, index, constraint, trigger, parameter, type or user — on every drop path (probed 2026-09-26 against SQL Server 2025).
The store never deletes on a drop; every reader filters entries through `ExtendedPropertyTargets` instead, which covers every drop path and a rolled-back drop at once, and relies on object, type, principal and column ids never being reused.
A table column is keyed by its stable `column_id`, so an earlier column's drop doesn't shift it.

An `ALTER VIEW` / `PROCEDURE` / `FUNCTION` carries the module's column and parameter properties **by name** (probed 2026-09-26): one whose column or parameter survives follows it to its new position, one whose name is gone goes with it (`RebindExtendedProperties`, undone with the transaction).

## Sproc trio

`Simulation.ExtendedProperties.cs` (partial).
`Simulation.Exec.cs` dispatches three branches after the `sp_executesql` route — each forwards to the shared `InvokeSpExtendedProperty(batch, ExtendedPropertyOp)` body:

- `sp_addextendedproperty` — add (Msg 15233 on duplicate)
- `sp_updateextendedproperty` — update (Msg 15217 on missing)
- `sp_dropextendedproperty` — drop (Msg 15217 on missing)

The arguments bind positionally or by name to each procedure's own signature: `@name`, `@value` (absent from the drop), then the three `@levelNtype` / `@levelNname` pairs, with `@value` defaulting to NULL (probed 2026-09-25 against SQL Server 2025).
Argument-name comparison drops the `@` prefix (the `AtPrefixedString` token's `Value` is already `@`-stripped).
Target resolution routes through `ResolveExtendedPropertyTarget`.

### Recognized level types

**Level 0 (schema-container or database-scoped host):** `SCHEMA`, plus the two terminal database-scoped hosts `TRIGGER` (a database DDL trigger → class 1 OBJECT_OR_COLUMN, major_id = trigger object_id resolved through `Database.DdlTriggers`) and `FILEGROUP` (→ **class 20 DATASPACE**, major_id = `data_space_id` resolved through `Database.Filegroups`).
Both are terminal — later levels don't apply — and probe-confirmed against SQL Server 2025 (`sp_addextendedproperty @name=…, @value=…, @level0type=N'TRIGGER'|N'FILEGROUP', @level0name=…`).
`class_desc` for 20 is `DATASPACE`.
These two land through the BACPAC loader (AW's `[SqlDatabaseDdlTrigger].[ddlDatabaseTriggerLog].[MS_Description]` + `[SqlFilegroup].[PRIMARY].[MS_Description]`); a filegroup EP is only reachable from public SQL after a bacpac registers a filegroup (no `CREATE FILEGROUP`), while a DDL-trigger EP is reachable directly (`CREATE TRIGGER … ON DATABASE` + the sproc).

**Level 0** may also be `USER` (class 4 DATABASE_PRINCIPAL, a database user — never a built-in principal or a role, and never with a level 1 beneath it).

**Level 1** under a schema: `TABLE` / `VIEW` / `PROCEDURE` / `FUNCTION` / `SEQUENCE` / `SYNONYM` / `RULE` / `DEFAULT` (class 1), `TYPE` (class 6 TYPE, major_id = `user_type_id`, alias and table types alike), and `XML SCHEMA COLLECTION` (class 10, major_id = `xml_collection_id`).

**Level 2:** `COLUMN` of a table, view or table-valued function (class 1) or of a table type (class 8 TYPE_COLUMN); `PARAMETER` of a procedure or function (class 2, minor_id = `parameter_id`); a table's `CONSTRAINT` (class 1, the constraint's own object_id); a table's or view's `TRIGGER` (class 1, the trigger's object_id); and a table's or view's `INDEX` (class 7, minor_id = `index_id` from `IndexIdentities()`, XML and spatial ids included).

## Errors

Every refusal of a target carries the state real's resolution raises it with; the grid lives on `ResolveExtendedPropertyTarget` and in `ExtendedPropertyTargetTests` (probed 2026-09-26 against SQL Server 2025).
Three things about it are not what the numbers suggest:

- A missing **index** is Msg 15600 state 17, where every other missing level-2 name is Msg 15135.
- The severity-15 Msg 15600 of a NULL `@name` is the T-SQL argument check (line 22, or 14 in the drop, whose message misspells it `sp_dropeextendedproperty`) and leaves the caller's batch running.
  Everything the target resolution raises — Msg 15096 / 15135 / 15217 / 15233 and a severity-16 Msg 15600 — comes from inside the engine at one line per procedure (37 / 36 / 28), and ends the batch and rolls the transaction back as under `SET XACT_ABORT ON`.
- A level-2 name beneath an **alias type** kills real's session (Msg 596, then a severe error); the simulator accepts it and records nothing.

**Target-label convention** for Msg 15233 / 15217:
- DB-level → `'object specified'`
- Schema → `'<schema>'`
- Table / view / proc / func → `'<schema>.<name>'`
- Beneath an object or type → `'<schema>.<name>.<leaf>'` (a parameter keeps its `@`)

## `sys.extended_properties`

`BuiltInResources.cs::EnumerateSysExtendedProperties` ships the 6-column subset:

| Column | Notes |
|---|---|
| `class` / `class_desc` | The classes the level grid above lands in |
| `major_id` (int) | DB=0, schema=schema_id, object=object_id, type=user_type_id, … (see `ExtendedPropertyKey`) |
| `minor_id` (int) | 0 for the object itself; the `column_id`, `parameter_id` or `index_id` beneath it |
| `name` (sysname) | Property name |
| `value` (sql_variant) | Wraps the stored value's own `SqlValue`, so its base type survives (nvarchar for an `N'…'` input, varchar for a plain literal) — probe-confirmed real behavior: `@value=N'…'` stores base type nvarchar, `@value='…'` stores varchar. DacFx reads the base type off the wire (via `SQL_VARIANT_PROPERTY` / the sql_variant TDS form) to re-script the value with the correct N-prefix, so a BACPAC round-trip preserves nvarchar. |

### Value base-type fidelity

The dict holds the raw `SqlValue` the sproc stored (`sp_addextendedproperty` assigns `arg.Value` verbatim — no coercion), so an `N'…'` literal keeps its `NVarcharSqlType` and a plain `'…'` literal its `VarcharSqlType`.
`sys.extended_properties.value` and `fn_listextendedproperty.value` both wrap that value in a `sql_variant` (`SqlValue.FromVariant`), matching real SQL Server's column.

## `fn_listextendedproperty`

`Selection.ListExtendedProperty.cs` is a built-in system TVF dispatched alongside `OPENJSON` / `STRING_SPLIT` in `ParseSingleFromSource`, with real's shape: `objtype varchar(128)`, `objname` / `name` `nvarchar(128)` in `Latin1_General_CI_AI`, `value sql_variant`.

Each live entry is described back to its `(level type, name)` chain (`ExtendedPropertyTargets.TryDescribe`) and matched against the arguments, so the TVF lists exactly what the procedures can address.
The rules, which are not the ones the argument names suggest (probed 2026-09-26 against SQL Server 2025):

- The level types given from level 0 down fix the listing's depth; `objtype` / `objname` are the deepest level's, and NULL for the database's own properties.
- Every name above the deepest must be given; the deepest's may be NULL — or the `DEFAULT` keyword, which is NULL — for all of that kind.
- Anything else lists nothing rather than raising: a NULL name above the deepest, a name or type past it, an unknown level type, a missing target, and the string `'default'`, which is a name like any other.
- Too few arguments is Msg 313.

## Not modeled yet

- **Level-1 kinds real accepts beyond the modeled ones** (`AGGREGATE`, `QUEUE`, …) raise Msg 15600 state 5.
