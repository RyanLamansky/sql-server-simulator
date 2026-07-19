# Triggers (DML + DDL)

`CREATE [OR ALTER] TRIGGER [schema.]name ON [schema.]parent { AFTER | FOR | INSTEAD OF } { INSERT | UPDATE | DELETE } [, ...] AS body`, mutated via `ALTER TRIGGER`, dropped via `DROP TRIGGER [IF EXISTS]`, toggled via `{ DISABLE | ENABLE } TRIGGER { name | ALL } ON parent`, fired automatically by the matching DML against the parent.
Body source is captured between `AS` and end-of-batch; re-tokenized per fire inside a child `BatchContext` with a [`TriggerFrame`](../../src/SqlServerSimulator/Parser/TriggerFrame.cs) seeded with the `INSERTED` / `DELETED` pseudo-tables.
AFTER (and its `FOR` synonym) attaches to heap tables only; INSTEAD OF attaches to heap tables and views.
Probed against SQL Server 2025 (2026-05-13).

Database-scope DDL triggers (`CREATE TRIGGER … ON DATABASE`) ship at the parse-and-store fidelity tier — see the DDL triggers section below.

## What ships

- **CREATE / ALTER / CREATE OR ALTER TRIGGER** — same upsert pattern as procedures (ObjectId preserved across ALTER).
- **DROP TRIGGER [IF EXISTS] name [, ...]** — comma-list form supported via the shared DROP parser.
- **DISABLE / ENABLE TRIGGER { name | ALL } ON parent** — toggles `Trigger.IsDisabled`.
  Disabled triggers stay in the schema and surface in `sys.triggers.is_disabled` but don't fire.
  Works on both table and view parents.
- **AFTER INSERT / UPDATE / DELETE** plus the **FOR-synonym-for-AFTER** spelling — table parents only.
  AFTER on a view raises Msg 8197 (probe-confirmed).
- **INSTEAD OF INSERT / UPDATE / DELETE** — replaces the would-be DML with the trigger body.
  The heap-write phase is skipped; identity allocation is skipped (INSERTED's identity column shows the type's typed default — 0 for int — rather than the next sequential value); NOT NULL / CHECK / key constraints are not enforced on the suppressed write; AFTER triggers on the same action don't fire.
  DEFAULT-clause evaluation and computed columns still run so INSERTED carries the would-be values (probe-confirmed).
  Parent can be a heap table or a view.
- **INSTEAD OF on views** — the primary real-world use case: makes a non-updatable view (join / aggregate / etc.) writable.
  INSERTED / DELETED are shaped to the view's `OutputColumns`; for an updatable view the simulator projects base-table rows through `View.BaseColumnOrdinals` (derived projection slots = typed NULL) so UPDATE / DELETE INSTEAD OF can build the pseudo-tables from base heap rows.
  INSTEAD OF UPDATE / DELETE on a *non-updatable* view (no `BaseTable`) raises `NotSupportedException` — the would-be-affected row enumeration requires executing the view's selection and tracking row identity, which is a follow-up.
- **At most one INSTEAD OF per action per target** (Msg 2111, probe-confirmed verbatim).
  A second INSTEAD OF trigger whose Actions overlap an existing one raises at CREATE TRIGGER time.
  ALTER / CREATE OR ALTER replacing the same trigger by name is permitted (the self-collision is excluded from the check).
  The diagnostic wording uses `table` vs `view` based on the parent kind.
- **Multi-action triggers** (`AFTER INSERT, UPDATE`, `INSTEAD OF INSERT, UPDATE`) — single trigger handles multiple events; the body discriminates via `IF EXISTS (SELECT 1 FROM inserted) ...` or join shape.
- **INSERTED / DELETED pseudo-tables** — bare 1-part names resolve through the new `TriggerFrame.Inserted` / `TriggerFrame.Deleted` slots ahead of the schema / temp-table dispatch.
  Both pseudo-tables are always materialized (matching real SQL Server): an INSERT trigger sees an empty `deleted`, a DELETE trigger sees an empty `inserted`, an UPDATE trigger sees both populated.
  Pseudo-tables are `HeapTable` instances flagged `IsTableVariable` so writes don't touch the regular transaction undo log; columns are shared by reference from the parent table (for table parents) or the view's `OutputColumns` (for view parents).
- **Multiple triggers per table** — all enabled AFTER triggers matching the firing action run in registration order (schema-dict insertion order).
  At most one INSTEAD OF per action per target.
- **TRIGGER_NESTLEVEL()** — no-arg form only; returns the current trigger nesting depth (0 outside any trigger, 1 at top-level DML's first trigger fire, 2+ when nested).
  One-arg form (filter by trigger object id) deferred.
- **`sys.triggers` catalog view** with the documented load-bearing column subset (`name`, `object_id`, `parent_class=1`, `parent_class_desc='OBJECT_OR_COLUMN'`, `parent_id`, `type='TR'`, `type_desc='SQL_TRIGGER'`, `create_date`, `modify_date`, `is_disabled`, `is_instead_of_trigger`, `is_not_for_replication=0`).
  `parent_id` is the table's `object_id` or the view's `object_id` depending on parent kind.
  Triggers also appear in `sys.objects` with `type='TR'` and `parent_object_id` set accordingly.
- **Trigger-error rollback** — a body-side `THROW` (or any uncaught exception) propagates up.
  For AFTER triggers, the firing DML's statement-atomic undo log walks back, reverting the heap insert/update/delete.
  For INSTEAD OF, the heap was never written, so propagation simply surfaces the error to the caller.
- **Direct-recursion suppression** — matches real SQL Server's default `RECURSIVE_TRIGGERS OFF`.
  The connection's `FiringTriggerIds` set tracks in-flight trigger ObjectIds; the dispatcher skips fires whose ObjectId is already in flight.
  Trigger T can still cause trigger U to fire via cross-table DML; only same-trigger recursion is blocked.
  **For INSTEAD OF**, the presence-check (`HasInsteadOfTrigger`) also excludes in-flight triggers, so a body's nested DML against its own target reaches the heap (probe-confirmed) — without this filter, the nested INSERT would skip both the trigger fire *and* the heap write, becoming a no-op.
- **MERGE routing through INSTEAD OF** — each WHEN branch's action is dispatched independently.
  A MERGE against a target with INSTEAD OF INSERT routes the `WHEN NOT MATCHED THEN INSERT` branch through the trigger (no heap write, no identity allocation, no constraint check); a mixed MERGE with INSTEAD OF INSERT + no INSTEAD OF UPDATE routes the INSERT branch through the trigger and the UPDATE branch through the heap normally.
  Per-action key validation excludes pending operations that bypass the heap.

## Implementation map

- **Storage**: [`Trigger`](../../src/SqlServerSimulator/Trigger.cs) class (Schema / Name / ObjectId / **Parent** (`object` — HeapTable or View) / Actions flags / Timing / BodyText / IsDisabled / CreateDate / `ParentObjectId` accessor), [`Schema.Triggers`](../../src/SqlServerSimulator/Schema.cs) per-schema dict.
- **Parser**: [`Simulation.CreateTrigger.cs`](../../src/SqlServerSimulator/Simulation/Simulation.CreateTrigger.cs) (CREATE + ALTER + CREATE OR ALTER + DISABLE/ENABLE), routed from `Simulation.Create.cs` / `Simulation.Alter.cs`.
  `DROP TRIGGER` routed through the shared `Simulation.Drop.cs` dispatch (which also cascade-drops triggers when DROP TABLE / DROP VIEW removes the parent).
- **Frame**: [`TriggerFrame`](../../src/SqlServerSimulator/Parser/TriggerFrame.cs) holds the per-fire pseudo-table instances.
  Set on the child `BatchContext` via the new trigger-body constructor; read by [`BatchContext.TryResolveTable`](../../src/SqlServerSimulator/Parser/BatchContext.cs) ahead of the temp / `@t` / schema dispatch.
- **Dispatch**: [`Simulation.InvokeTrigger.cs`](../../src/SqlServerSimulator/Simulation/Simulation.InvokeTrigger.cs) — `FireTriggers` walks every schema's `Triggers` dict, materializes the pseudo-tables once per fire, allocates a child `BatchContext`, runs the body via `DispatchStatementsUntil`.
  `TryFireInsteadOfTrigger` is the single-trigger INSTEAD OF dispatch; returns `true` if a trigger fired.
  `HasAfterTrigger` / `HasInsteadOfTrigger` are the fast-path predicates DML sites call first to avoid per-row snapshot capture when no trigger is attached.
  Both predicates exclude in-flight triggers via `Connection.FiringTriggerIds`.
  `MaterializePseudoTable` takes a `HeapColumn[]` directly so the same machinery works for table parents (parent's `Columns`) and view parents (view's `OutputColumns`).
- **DML hooks**: `Simulation.Insert.cs` (INSERT + INSERT … SELECT + INSERT … OUTPUT) detects INSTEAD OF on either the destination view or the destination table and either routes through `ProcessInsteadOfInsertOnView` (for view targets — view INSERT may include non-updatable views) or threads an `insteadOfActive` flag through `ProcessHeapInsert` (for table targets, which skips identity allocation, constraint enforcement, and heap write).
  `Simulation.Update.cs` and `Simulation.Delete.cs` thread the per-target INSTEAD OF detection through their `CommitUpdate` / `CommitDelete` helpers; for view targets with INSTEAD OF, INSERTED / DELETED are projected through `View.BaseColumnOrdinals` via a `ProjectThroughView` helper.
  `Simulation.Merge.cs` detects per-action INSTEAD OF at the top of `CommitMerge` and routes each pending list (inserts, updates, deletes) independently through trigger-fire or heap-write paths.
- **Connection state**: [`SimulatedDbConnection.FiringTriggerIds`](../../src/SqlServerSimulator/SimulatedDbConnection.cs) (recursion guard) + `TriggerNestLevel` (surfaced by `TRIGGER_NESTLEVEL()`).

## DDL triggers — `CREATE TRIGGER … ON DATABASE`

Parse-and-store-but-no-fire surface for database-scope DDL triggers.
AW's `[ddlDatabaseTriggerLog]` (`FOR DDL_DATABASE_LEVEL_EVENTS`) loads end-to-end and surfaces in `sys.triggers` with the probe-confirmed shape: `parent_class=0`, `parent_class_desc='DATABASE'`, `parent_id=0`, `type_desc='SQL_TRIGGER'`, `is_ms_shipped=0`, `is_instead_of_trigger=0`.
The full `CREATE TRIGGER` text lands in `sys.sql_modules.definition` via `SchemaObject.DefinitionText`; `is_ms_shipped`'s absence was one gate (Msg 207 aborted the whole DDL-trigger populator).

DacFx's `SqlDatabaseDdlTrigger` element carries an `EventType` relationship of `SqlTriggerEventTypeSpecifier` entries built from `sys.trigger_events` — **not** reverse-engineered from the module definition.
Without those rows DacFx drops the whole element silently (AW's `[ddlDatabaseTriggerLog]` vanished from re-exports).
The simulator now expands them: a trigger created `FOR DDL_DATABASE_LEVEL_EVENTS` surfaces one `sys.trigger_events` row per **leaf** event in the group's transitive closure — 158 rows, each carrying the group's id/desc in `event_group_type`(`_desc`) = `10016` / `DDL_DATABASE_LEVEL_EVENTS`, `is_first`/`is_last` = 0, `is_trigger_event` = 1 (probe-confirmed against SQL Server 2025's AW).
The closure is computed from a hard-coded copy of SQL Server's static `sys.trigger_event_types` catalog (`src/SqlServerSimulator/TriggerEventTypes.cs`, 312 rows: `type` / `type_name` / `parent_type`), also surfaced as the `sys.trigger_event_types` catalog view.
Individual-event names (`FOR CREATE_TABLE`) emit a single row with a NULL group.

**Storage**: `DdlTrigger` class (`src/SqlServerSimulator/DdlTrigger.cs`) carries name + object_id + event-type list + body source + `is_disabled` flag.
`Database.DdlTriggers` is the per-database `ConcurrentDictionary<string, DdlTrigger>` (case-insensitive keys); not per-schema because DDL triggers belong to the database itself.
The class extends `SchemaObject` for the object-id + create-date pattern but doesn't participate in any schema's shared namespace except for name collision detection at CREATE time (probe-confirmed: a DDL trigger named `foo` collides with a same-named DML trigger / table / view / proc in the same schema).

**Parser**: `Simulation.CreateTrigger.cs::TryParseCreateTrigger` — after `ON`, if the next token is `DATABASE`, dispatch to `ParseDdlTriggerBody` which handles `[WITH options] {FOR|AFTER} <event_type_list> AS <body>`.
Event types parse as bare identifiers and store verbatim in `DdlTrigger.EventTypes`.
`DROP TRIGGER name ON DATABASE` lives in `Simulation.Drop.cs::DropOneTrigger`, which peeks the next tokens via `SaveCheckpoint` / `RestoreCheckpoint` to decide between the DML-trigger and DDL-trigger paths.

**Catalog**: `sys.triggers` enumerator in `BuiltInResources.cs::EnumerateSysTriggers` yields rows for `Database.DdlTriggers` after the per-schema DML trigger loop, with the `parent_class=0` shape above.
`sys.trigger_events` (`BuiltInResources.ConstraintsAndTriggers.cs::EnumerateSysTriggerEvents`) yields the expanded leaf-event rows for each DDL trigger after the DML-trigger loop; `sys.trigger_event_types` is a server-scoped view over `TriggerEventTypes.All`.

**Deferred**:
- Trigger firing — the simulator doesn't dispatch DDL events to any trigger loop.
  Accepted as a documented behavior gap; AW's trigger body is an audit-log writer, not a load-bearing dependency.
- `DISABLE` / `ENABLE TRIGGER … ON DATABASE` — the per-schema disable/enable path doesn't extend to the per-database dict.

## Not modeled

- **INSTEAD OF UPDATE / DELETE on non-updatable views** — INSTEAD OF INSERT on any view ships; INSTEAD OF UPDATE / DELETE on an updatable (single-base, no DISTINCT / JOIN / aggregate) view ships.
  INSTEAD OF UPDATE / DELETE on a join / aggregate / DISTINCT view raises `NotSupportedException` — implementing it requires executing the view's selection to enumerate would-be-affected rows, which loses heap-row identity and bypasses the existing visibility-filter machinery.
  Deferred.
- **Logon / server triggers** — only DML triggers (DATABASE-scope and OBJECT-scope) ship.
- **`RECURSIVE_TRIGGERS ON`** — direct recursion is unconditionally suppressed.
  The database option to allow it isn't surfaced.
- **`is_nested_triggers_on = OFF`** — cross-table cascading triggers always fire (depth-limited only by `MaxNestingLevel`).
- **`@@NESTLEVEL` independence** — the simulator collapses UDF / procedure / trigger depth into a single counter (`SimulatedDbConnection.NestingLevel`).
  `TRIGGER_NESTLEVEL()` reads its own dedicated `TriggerNestLevel` counter, so it's accurate, but `@@NESTLEVEL` (not modeled at all) wouldn't have the right value if added.
- **Trigger body's DML inside the parent's atomic scope** — minor fidelity gap: when a trigger body runs multiple statements and the second one throws, the first statement's writes (e.g. into an audit log) don't roll back because the trigger body's child `BatchContext` allocates fresh per-statement undo logs rather than sharing the parent statement's log.
  Real SQL Server rolls back the entire parent + trigger atomic unit.
  Common idioms (single-statement triggers, body-side `THROW` before any side effects) work correctly; multi-statement bodies with mid-body throws after side effects are the gap.
- **Trigger-body result sets** — a `SELECT` inside a trigger body emits a result set in real SQL Server (probe-confirmed).
  The simulator's trigger invocation drains and discards yielded result sets at the call site (rare pattern in apps; revisit if needed).
- **`UPDATE()`** / `COLUMNS_UPDATED()` intrinsics — not modeled.
  Trigger bodies that need per-column change detection have to compare INSERTED vs DELETED manually.
- **`sp_settriggerorder`** — not modeled; firing order is registration order rather than user-controllable.

## EF Core reach

EF Core 7+ has one trigger-aware annotation: `entityType.ToTable(b => b.HasTrigger("name"))`.
Without it, EF's SaveChanges emits `INSERT … OUTPUT INSERTED.Id VALUES (…)` — fast but breaks under some trigger configurations in real SQL Server.
With it, EF switches to the trigger-safe shape: `SET NOCOUNT ON; INSERT … VALUES (…); SELECT [Id] FROM [t] WHERE @@ROWCOUNT = 1 AND [Id] = scope_identity();`.
Both shapes need to flow through the simulator's trigger dispatch and return the right identity to EF's per-entity tracker.

The `HasTrigger` shape relies on `SCOPE_IDENTITY()` returning the **outer** INSERT's identity, not the trigger body's last identity write.
Real SQL Server scopes SCOPE_IDENTITY per stored-context-scope: a trigger body's INSERT doesn't leak its identity to the caller's SCOPE_IDENTITY (probe-confirmed).
The simulator collapses SCOPE_IDENTITY and @@IDENTITY into one connection-level slot, so the trigger dispatcher saves the outer value before firing triggers and restores it after, preserving the EF-visible scope.
(Minor consequence: @@IDENTITY also reverts post-trigger, which is technically wrong — real SQL Server's @@IDENTITY is session-wide and would reflect the trigger's last identity.
Apps that read @@IDENTITY immediately after a trigger-firing DML to see the trigger's identity won't get the right value; the rarity of that pattern + EF's reliance on SCOPE_IDENTITY justifies the trade.)

The `EFCoreTriggers` fixture locks down compatibility with `HasTrigger` across EF Core upgrades — if a future EF version changes the trigger-safe emit shape to something the simulator doesn't support yet, the fixture catches it.
