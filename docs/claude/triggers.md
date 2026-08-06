# Triggers (DML + DDL)

`CREATE [OR ALTER] TRIGGER [schema.]name ON [schema.]parent { AFTER | FOR | INSTEAD OF } { INSERT | UPDATE | DELETE } [, ...] AS body`, mutated via `ALTER TRIGGER`, dropped via `DROP TRIGGER [IF EXISTS]`, toggled via `{ DISABLE | ENABLE } TRIGGER { name | ALL } ON parent`, fired automatically by the matching DML against the parent.
Body source is captured between `AS` and end-of-batch; re-tokenized per fire inside a child `BatchContext` with a [`TriggerFrame`](../../src/SqlServerSimulator/Parser/TriggerFrame.cs) seeded with the `INSERTED` / `DELETED` pseudo-tables.
It is also bound once at `CREATE` / `ALTER` against *empty* pseudo-tables of the same shape, so a bad column — on the parent, on `INSERTED` / `DELETED`, or inside `UPDATE(col)` — reports Msg 207 there and the trigger isn't created; the parent's own Msg 8197 still comes first → [`programmable.md`](programmable.md#create-time-body-binding).
AFTER (and its `FOR` synonym) attaches to heap tables only; INSTEAD OF attaches to heap tables and views.
Probed against SQL Server 2025.

Database-scope DDL triggers (`CREATE TRIGGER … ON DATABASE`) fire on the DDL the simulator models — see the [DDL triggers](#ddl-triggers--create-trigger--on-database) section below.

## What's modeled

- **CREATE / ALTER / CREATE OR ALTER TRIGGER** — same upsert pattern as procedures (ObjectId preserved across ALTER), and the same replacement gates: **Msg 2010** when the name holds another object kind, **Msg 2110** when it holds a trigger on a different parent, **Msg 208** when it holds nothing (bare ALTER), **Msg 2714** on a plain CREATE over a taken name, and **Msg 166** for a database-qualified trigger name — see [`programmable.md`](programmable.md#replacing-a-module--alter--create-or-alter).
  A missing `ON` target reports its Msg 8197 ahead of all of them.
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
- **The joined shapes a production body is written in** — a body rarely reads one row.
  It reaches its own parent table through an alias and *joins* the pseudo-table: `UPDATE n SET n.tag = dbo.f(n.tag) FROM t n JOIN INSERTED i ON n.id = i.id`, the same family as the aliased `DELETE <alias> FROM …` form.
  An **OR in that join's ON clause** (`ON n.id = i.id OR n.id = i.parent_id`) is the idiom for reaching an inserted row *and* the row it names as parent, and it drives only from the INSERTED rows a `WHERE` leaves standing.
  A **scalar UDF in the SET** evaluates per row, and a set-based `INSERT … SELECT FROM INSERTED JOIN <gate>` writes one row per inserted row — carrying INSERTED's values, which are the rows as written rather than as a later statement in the same body leaves them.
  The body's own UPDATEs dispatch the parent's AFTER UPDATE trigger once per statement over that statement's DELETED set, a no-op self-assignment included.
- **Multiple triggers per table** — every enabled AFTER trigger matching the firing action runs, ordered by `sp_settriggerorder` at the two ends (see [Firing order](#firing-order)).
  Unpinned triggers follow the per-schema dictionary's enumeration, which is **not** guaranteed to be creation order and isn't asserted anywhere; SQL Server leaves the middle unspecified too.
  At most one INSTEAD OF per action per target.
- **TRIGGER_NESTLEVEL()** — no-arg form only; returns the current trigger nesting depth (0 outside any trigger, 1 at top-level DML's first trigger fire, 2+ when nested).
  One-arg form (filter by trigger object id) deferred.
- **`sys.triggers` catalog view** with the documented load-bearing column subset (`name`, `object_id`, `parent_class=1`, `parent_class_desc='OBJECT_OR_COLUMN'`, `parent_id`, `type='TR'`, `type_desc='SQL_TRIGGER'`, `create_date`, `modify_date`, `is_disabled`, `is_instead_of_trigger`, `is_not_for_replication=0`).
  `parent_id` is the table's `object_id` or the view's `object_id` depending on parent kind.
  Triggers also appear in `sys.objects` with `type='TR'` and `parent_object_id` set accordingly.
- **Trigger-error rollback** — a body-side `THROW` (or any uncaught exception) propagates up.
  For AFTER triggers, the firing DML's statement-atomic undo log walks back, reverting the heap insert/update/delete.
  For INSTEAD OF, the heap was never written, so propagation simply surfaces the error to the caller.
- **The body runs inside the firing statement's atomic scope** — see [Trigger atomic scope](#trigger-atomic-scope).
- **The body runs in its own table's database.**
  A trigger fired by a write through a three-part name (`INSERT other.dbo.t …`) is found in the target's schemas and its body executes with the connection's current database switched to the target for the body's duration, so `DB_NAME()` inside reads the target and unqualified body writes land there — probe-confirmed against SQL Server 2025, which also reports the firing session's database as `ORIGINAL_DB_NAME()`.
  The switch is restored in a `finally` and is invisible to the firing batch (not a `USE`) → [`schemas.md`](schemas.md#cross-database-writes).
- **`UPDATE(col)` / `COLUMNS_UPDATED()`** — see [Change-detection intrinsics](#change-detection-intrinsics).
- **AFTER triggers fire on a zero-row DML** — an UPDATE / DELETE matching nothing, an `INSERT … SELECT` producing nothing, and a MERGE with no source rows all still run the body, with empty `INSERTED` / `DELETED` and `@@ROWCOUNT` 0 (probe-confirmed for all four shapes).
  `UPDATE(col)` still reports the SET-clause columns there, because the reading is a property of the statement rather than of the rows.
- **Nesting and recursion gating** — `RECURSIVE_TRIGGERS` (per database) and the `nested triggers` server option decide whether a trigger fires while other triggers are running; see [Nesting and recursion options](#nesting-and-recursion-options).
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
  Both predicates route through `CanFireTrigger`, so a trigger the nesting rules suppress reads as absent.
  `MaterializePseudoTable` takes a `HeapColumn[]` directly so the same machinery works for table parents (parent's `Columns`) and view parents (view's `OutputColumns`).
- **DML hooks**: `Simulation.Insert.cs` (INSERT + INSERT … SELECT + INSERT … OUTPUT) detects INSTEAD OF on either the destination view or the destination table and either routes through `ProcessInsteadOfInsertOnView` (for view targets — view INSERT may include non-updatable views) or threads an `insteadOfActive` flag through `ProcessHeapInsert` (for table targets, which skips identity allocation, constraint enforcement, and heap write).
  `Simulation.Update.cs` and `Simulation.Delete.cs` thread the per-target INSTEAD OF detection through their `CommitUpdate` / `CommitDelete` helpers; for view targets with INSTEAD OF, INSERTED / DELETED are projected through `View.BaseColumnOrdinals` via a `ProjectThroughView` helper.
  `Simulation.Merge.cs` detects per-action INSTEAD OF at the top of `CommitMerge` and routes each pending list (inserts, updates, deletes) independently through trigger-fire or heap-write paths.
- **Connection state**: [`SimulatedDbConnection.FiringTriggers`](../../src/SqlServerSimulator/SimulatedDbConnection.cs) (the in-flight trigger stack the gating reads) + `TriggerNestLevel` (surfaced by `TRIGGER_NESTLEVEL()`).
- **Gating**: `Simulation.CanFireTrigger` — the one predicate behind both nesting rules, called from `FireTriggers`' match loop, `TryFireInsteadOfTrigger`'s, and `HasTrigger`.

## Nesting and recursion options

Two knobs decide whether a trigger fires while other triggers are already running on the connection.
Both are read at fire time by `Simulation.CanFireTrigger`, the single predicate `FireTriggers`, `TryFireInsteadOfTrigger` and `HasTrigger` all filter through, against the connection's `FiringTriggers` stack (one frame per in-flight trigger, carrying its ObjectId and whether it's an AFTER trigger).
Everything below is probe-confirmed against SQL Server 2025.

### `RECURSIVE_TRIGGERS` — a trigger re-firing itself

`ALTER DATABASE <db> SET RECURSIVE_TRIGGERS { ON | OFF }`, per database, default OFF, surfaced as `sys.databases.is_recursive_triggers_on` and stored on `Database.RecursiveTriggers`.

Off, an AFTER trigger whose body's DML would re-fire that same trigger is skipped and the DML reaches the heap.
On, the re-fire happens, bounded only by the 32-level nesting cap — an unbounded self-insert runs 32 bodies and then raises **Msg 217** (`Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).`), rolling the whole statement back.

Three rules the option's name doesn't convey:

- The test is the **innermost** firing trigger, not the whole stack.
  Indirect recursion fires either way: T1's trigger writes T2, whose trigger writes T1, whose trigger runs again — with its outer frame still on the stack.
  This is why `FiringTriggers` is a stack rather than a set of in-flight ids.
- A **stored procedure between the body and the DML doesn't launder the recursion** — the innermost *trigger* frame is still the trigger's own, so the re-fire stays suppressed.
- **INSTEAD OF triggers never self-recurse**, whatever the setting: real processes an INSTEAD OF body's DML against its own target as if the table had no INSTEAD OF trigger.
  The `HasInsteadOfTrigger` presence-check excluding the innermost frame is what makes the nested INSERT reach the heap — without the filter it would skip both the trigger fire *and* the heap write, becoming a no-op.

### `nested triggers` — an AFTER trigger under another AFTER trigger

The server option (`sp_configure 'nested triggers', 0` + `RECONFIGURE`; `sys.configurations` id 115), default 1, server-scoped on `Simulation.ServerConfiguration` and read through `Simulation.NestedTriggersEnabled`.
`sys.databases.is_nested_triggers_on` stays NULL — real reports it only for contained databases.

Off, **an AFTER trigger doesn't fire while any AFTER trigger is running anywhere up the stack**: only the first AFTER level runs.
The cascading write still lands; it just doesn't fire the next trigger.
Consequences worth stating separately, each probed:

- **INSTEAD OF triggers are exempt** and nest normally — an INSTEAD OF chain runs to full depth even with the option off.
- The AFTER rule reads the **whole stack, not the frame above**: an AFTER trigger's body still reaches an INSTEAD OF trigger, but the AFTER trigger one level below *that* stays suppressed.
  Conversely an AFTER trigger under nothing but INSTEAD OF frames does fire.
- **Sibling triggers on one table all fire** — they're all first-level, not nested.
- It **also disables direct recursion**, whatever `RECURSIVE_TRIGGERS` says, because a trigger re-firing itself is an AFTER trigger under an AFTER trigger.
  The server option wins.

The staged / installed split matters here: a `sp_configure` write alone changes nothing, because the dispatcher reads the *installed* value (`value_in_use`) that only `RECONFIGURE` moves.
The sibling option `server trigger recursion` (id 116) round-trips through the catalog like any other but carries no behavior, since server-scope triggers aren't modeled at all.
See [`catalog-views.md`](catalog-views.md) for the `sp_configure` surface itself.

## `OUTPUT` on a triggered target — Msg 334

A DML statement whose `OUTPUT` clause returns rows **to the client** (no `INTO`) can't target a table carrying an enabled trigger: both would be the statement's result set, and real refuses the combination rather than interleaving them.

> Msg 334, Level 16 — `The target table 'dbo.m' of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause.`

Probe-confirmed rules, two of which the message text doesn't say:

- The gate is a trigger for the statement's **own action**, not "any enabled trigger" as the wording claims — an INSERT-only trigger blocks `INSERT … OUTPUT` but leaves `UPDATE … OUTPUT` alone.
- The target is echoed **as written**: `dbo.m` when qualified, `m` when bare, and MERGE reports its *alias*.
- INSTEAD OF counts alongside AFTER; a disabled trigger doesn't; `OUTPUT … INTO` is exempt.
- It's **compile-time** — it fires from an un-taken `IF` branch.

`Simulation.RejectClientOutputOnTriggeredTarget` is the shared gate, called from the INSERT / UPDATE / DELETE / MERGE parse sites (MERGE checks once per WHEN clause, since its actions are per-branch).
Every one of them tests the same `OutputProjection.HasTarget`, so `OUTPUT … INTO` is the escape on all four — including MERGE, which only gained it when the projections converged (see [`dml.md`](dml.md)).

This is the rule behind EF Core's `HasTrigger` annotation: declaring a trigger makes EF abandon its `OUTPUT INSERTED` emit shape, because that shape is illegal against a triggered table.

## Firing order

`sp_settriggerorder @triggername, @order, @stmttype [, @namespace]` pins a trigger to the front or back of the AFTER triggers a given action runs on its table.
Named and positional argument forms both bind, `@order` / `@stmttype` are case-insensitive, and the name may be bare or schema-qualified.
`@namespace` (DATABASE / SERVER scope, for DDL triggers) is accepted and ignored — DDL-trigger ordering isn't modeled, and a DDL trigger's name doesn't resolve here.

Only the two ends are ordered: `First` runs first, `Last` runs last, and everything between keeps the dictionary's arbitrary order, which real leaves unspecified as well.
Ordering is **per action** and independent — pinning a multi-action trigger first for INSERT leaves its UPDATE position alone — and `@order = 'None'` clears both slots for that action.
`ALTER TRIGGER` replaces the object and so resets its order (probe-confirmed).

State lives on `Trigger.FirstForActions` / `LastForActions`; `Simulation.TriggerOrderRank` turns it into the sort `FireTriggers` applies.

**Read-back** is `OBJECTPROPERTY(id, 'ExecIsFirstInsertTrigger')` and its five siblings (`Last`, and the `Update` / `Delete` actions) — 1 / 0 for a trigger, NULL for anything else.
Note the `Last…` spellings are one character shorter than the `First…` ones, which matters because the property dispatch switches on name length.

Rejections, all probe-confirmed against SQL Server 2025:

| Situation | Error |
| --- | --- |
| Slot already held by a *different* trigger | **Msg 15130** — `There already exists a 'First' trigger for 'INSERT'.`, echoing **both words as the caller wrote them** |
| Trigger doesn't handle that action | **Msg 15125** — `Trigger 'tr_a' is not a trigger for 'update'.`, **lowercasing** the action |
| INSTEAD OF trigger | **Msg 15133** — at most one exists per action, so ordering is meaningless |
| Name doesn't resolve | **Msg 15165** — folds "missing" and "no permission" into one message |
| `@order` / `@stmttype` outside the accepted set | **Msg 15600** |

Re-pinning the trigger that already holds a slot is not a conflict.

## Trigger-body result sets

A `SELECT` in a trigger body **is** the firing statement's result set — real hands it to the client, and several body SELECTs (or several firing triggers) each contribute one, so a plain `INSERT` can return rows.

The body runs inside the DML executor, which returns a single outcome, so the sets can't be yielded in place.
`RunTriggerBodies` buffers them on `BatchContext.PendingTriggerResultSets` and `DispatchOneStatement` drains that after the statement's own outcome.
Only **query** results are buffered: the body's rows-affected counts stay discarded, because forwarding them would inflate the total the firing statement reports — the number an ORM reads back from `SaveChanges`.

Order across several triggers isn't asserted anywhere: SQL Server leaves it unspecified without `sp_settriggerorder`, which isn't modeled.

**This is why a trigger body shouldn't SELECT.** A body of `SELECT 1` interleaves an extra result set with whatever the caller expected, and that breaks EF Core's trigger-safe `SaveChanges` shape (`SET NOCOUNT ON; INSERT …; SELECT [Id] …`) on real SQL Server just as it does here — verified against SQL Server 2025, which returns four result sets for a two-entity batch under such a trigger.
`EFCoreTriggers.HasTrigger_SaveChanges_RetrievesGeneratedIdentity` used exactly that body and passed only while the simulator was dropping body result sets; its trigger is now a no-op.

## Trigger atomic scope

A trigger body has no atomic scope of its own.
Real rolls back the firing statement and everything its triggers wrote as a single unit, so an audit-log INSERT in a body whose later statement throws does **not** survive — and neither does one written by a stored procedure the body called.

Mechanically, `Simulation.RunMutation` gives every mutation statement an undo log and commits it on the statement's own success.
Inside a trigger that would let each body statement commit independently, so the body instead **joins the firing statement's log**: `SimulatedDbConnection.TriggerStatementUndoLog` (paired with `TriggerStatementVersionEntries` for the MVCC side) is published by `RunTriggerBodies` for the duration of the bodies and consumed by `RunMutation`, which then skips both the commit and the version-finalize — the firing statement does those once, for the whole unit.
The state is session-scoped rather than per-`BatchContext` precisely because it has to reach modules the body calls, each of which runs in a child batch of its own.
A nested fire re-publishes the same log it already joined, so the save/restore nests harmlessly.

Only the auto-commit path needed this: under an explicit transaction every statement already shares `SimulatedDbTransaction.UndoLog`, and the firing statement's marker covers the trigger's writes.
That path was already correct and is locked down by `TriggerAtomicScopeTests` alongside the rest.

### Msg 3616 — the body's own TRY / CATCH doesn't rescue it

An error of severity **11 or higher** raised while a body runs aborts the batch and rolls the unit back *even when the body's own `TRY` / `CATCH` handled it*, surfacing:

> Msg 3616, Level 16, State 1 — `An error was raised during trigger execution. The batch has been aborted and the user transaction, if any, has been rolled back.`

Severity ≤ 10 is informational and leaves the unit intact (a caught `RAISERROR(…, 10, 1)` keeps both the body's writes and the firing statement's).
An error the body leaves *un*handled propagates with its own number instead — an outer `CATCH` sees `ERROR_NUMBER()` 51000 for a body-side `THROW 51000`, with `ERROR_PROCEDURE()` naming the trigger — so Msg 3616 fires only for the swallowed case.
An error caught inside a stored procedure the body called counts too, which is why `SimulatedDbConnection.TriggerBodyErrorRaised` is connection-scoped; it's saved and cleared per body so a handled error in one trigger doesn't condemn the next.

## Change-detection intrinsics

`UPDATE(column)` and `COLUMNS_UPDATED()` report **which columns the firing statement named**, not which values actually changed:

| Firing action | `UPDATE(col)` | `COLUMNS_UPDATED()` |
| --- | --- | --- |
| INSERT (any column list) | true for every column | every bit through the watermark |
| UPDATE | true for SET-clause columns | those columns' bits |
| DELETE | false for every column | **zero-length** varbinary (`DATALENGTH` 0) |

Probe-confirmed consequences: `UPDATE SET a = a` reports `a` updated; an UPDATE matching **no rows** still fires the trigger and still reports its SET columns; an INSERT naming one column reports every column; and a MERGE reports per branch (its INSERT branch behaves like an INSERT, its UPDATE branch like an UPDATE).
For MERGE the mask is the union of every `WHEN MATCHED THEN UPDATE` clause's targets whether or not that clause fired — consistent with the reading being statement-static.

**Bitmask layout.** Column_id *N* occupies bit `(N-1) % 8` of byte `(N-1) / 8`, least-significant bit first, over `ceil(MaxColumnIdUsed / 8)` bytes.
The mask is keyed on the **stable `column_id`**, so a dropped column keeps its bit position and the length doesn't shrink — see [stable column ids](catalog-views.md#stable-column-ids).

**Where they live.** `COLUMNS_UPDATED()` is a value expression and resolves through `ResolveBuiltIn` ([`ColumnsUpdated.cs`](../../src/SqlServerSimulator/Parser/Expressions/ColumnsUpdated.cs)); `UPDATE(col)` is a **`BooleanExpression`** ([`UpdatePredicate.cs`](../../src/SqlServerSimulator/Parser/Expressions/UpdatePredicate.cs)) dispatched from `BooleanExpression.ParseAtom`, because real raises **Msg 156** for `SELECT UPDATE(c1)` — modeling it as a bit-returning built-in would accept a shape real rejects.
The per-fire mask rides on `TriggerFrame.ColumnsUpdatedMask`, built by `Simulation.BuildColumnsUpdatedMask` at fire time.

Error paths (probe-confirmed): an unknown column raises **Msg 207**; use outside any trigger raises **Msg 140** (`"Can only use IF UPDATE within a CREATE TRIGGER statement."`); a qualified name (`UPDATE(t.c1)`) raises Msg 102 near `'.'` and the no-arg `UPDATE()` raises Msg 102 near `')'`.
`COLUMNS_UPDATED()` is deliberately asymmetric — outside a trigger it returns **NULL** rather than raising.

`UPDATE(col)` resolves its column to a `column_id` when the body parses, which for the simulator is each fire rather than at CREATE TRIGGER; real resolves at CREATE and raises Msg 207 there.
That's the same deferred module-body validation every other trigger-body name reference has.

## DDL triggers — `CREATE TRIGGER … ON DATABASE`

Database-scope DDL triggers fire on the DDL the simulator models, with `EVENTDATA()` describing the statement.
AW's `[ddlDatabaseTriggerLog]` (`FOR DDL_DATABASE_LEVEL_EVENTS`) loads end-to-end and surfaces in `sys.triggers` with the probe-confirmed shape: `parent_class=0`, `parent_class_desc='DATABASE'`, `parent_id=0`, `type_desc='SQL_TRIGGER'`, `is_ms_shipped=0`, `is_instead_of_trigger=0`.
The full `CREATE TRIGGER` text lands in `sys.sql_modules.definition` via `SchemaObject.DefinitionText`; `is_ms_shipped`'s absence was one gate (Msg 207 aborted the whole DDL-trigger populator).

DacFx's `SqlDatabaseDdlTrigger` element carries an `EventType` relationship of `SqlTriggerEventTypeSpecifier` entries built from `sys.trigger_events` — **not** reverse-engineered from the module definition.
Without those rows DacFx drops the whole element silently (AW's `[ddlDatabaseTriggerLog]` vanished from re-exports).
The simulator expands them: a trigger created `FOR DDL_DATABASE_LEVEL_EVENTS` surfaces one `sys.trigger_events` row per **leaf** event in the group's transitive closure — 158 rows, each carrying the group's id/desc in `event_group_type`(`_desc`) = `10016` / `DDL_DATABASE_LEVEL_EVENTS`, `is_first`/`is_last` = 0 (a DDL trigger takes no `sp_settriggerorder` ordering), `is_trigger_event` = 1 (probe-confirmed against SQL Server 2025's AW).
The closure is computed from a hard-coded copy of SQL Server's static `sys.trigger_event_types` catalog (`src/SqlServerSimulator/TriggerEventTypes.cs`, 312 rows: `type` / `type_name` / `parent_type`), also surfaced as the `sys.trigger_event_types` catalog view.
Individual-event names (`FOR CREATE_TABLE`) emit a single row with a NULL group.

**Storage**: `DdlTrigger` class (`src/SqlServerSimulator/Schemas/DdlTrigger.cs`) carries name + object_id + event-type list + body source + body line offset + `is_disabled` flag, plus the `Covers` predicate that expands the declared events to their leaf closure once.
`Database.DdlTriggers` is the per-database `ConcurrentDictionary<string, DdlTrigger>` (case-insensitive keys); not per-schema because DDL triggers belong to the database itself.
The class extends `SchemaObject` for the object-id + create-date pattern but doesn't participate in any schema's shared namespace except for name collision detection at CREATE time (probe-confirmed: a DDL trigger named `foo` collides with a same-named DML trigger / table / view / proc in the same schema).

**Parser**: `Simulation.CreateTrigger.cs::TryParseCreateTrigger` — after `ON`, if the next token is `DATABASE`, dispatch to `ParseDdlTriggerBody` which handles `[WITH options] {FOR|AFTER} <event_type_list> AS <body>`.
Event types parse as bare identifiers and store verbatim in `DdlTrigger.EventTypes`; matching at fire time is case-insensitive.
`DROP TRIGGER name ON DATABASE` lives in `Simulation.Drop.cs::DropOneTrigger`, which peeks the next tokens via `SaveCheckpoint` / `RestoreCheckpoint` to decide between the DML-trigger and DDL-trigger paths.
`{ DISABLE | ENABLE } TRIGGER { name | ALL } ON DATABASE` routes through the same `TryParseEnableOrDisableTrigger` the DML form uses, branching on the `DATABASE` keyword after `ON`; a disabled DDL trigger stays in `sys.triggers` with `is_disabled = 1` and doesn't fire.

**Catalog**: `sys.triggers` enumerator in `BuiltInResources.cs::EnumerateSysTriggers` yields rows for `Database.DdlTriggers` after the per-schema DML trigger loop, with the `parent_class=0` shape above.
`sys.trigger_events` (`BuiltInResources.ConstraintsAndTriggers.cs::EnumerateSysTriggerEvents`) yields the expanded leaf-event rows for each DDL trigger after the DML-trigger loop; `sys.trigger_event_types` is a server-scoped view over `TriggerEventTypes.All`.

### Firing

`Simulation.RecordDdlEvent` is called by each modeled DDL processor once its own work succeeded, appending a `DdlEventInfo` to `StatementContext.PendingDdlEvents`; `Simulation.FireDdlTriggers` drains that from the dispatch loop right after `DispatchOneStatementCore` returns.
Recording after success and firing after the statement is what gives the probe-confirmed shape: **a failed DDL raises no event**, an un-taken `IF` branch raises none, and the body already sees the finished change (`OBJECT_ID` of the new table resolves inside a `CREATE_TABLE` body).
The fire sits inside the dispatcher's own `try`, so a body error becomes the statement's error — reaching an enclosing `TRY` / `CATCH`, tripping Msg 3616 for a swallowed one, and carrying the trigger's unqualified name as `ERROR_PROCEDURE`.
A body `SELECT` becomes the firing statement's result set through the same `PendingTriggerResultSets` buffer DML bodies use.

Matching is on the **expanded leaf event set**, so `FOR DDL_TABLE_EVENTS` fires on exactly the `CREATE_TABLE` / `ALTER_TABLE` / `DROP_TABLE` rows it projects into `sys.trigger_events`.
One statement can raise several events — `DROP TABLE a, b` raises one `DROP_TABLE` per name, each carrying the whole statement as `CommandText` (probe-confirmed) — and `SELECT … INTO` raises `CREATE_TABLE` while a `#temp` destination raises nothing.

Events raised, by object kind: **table** (CREATE / ALTER / DROP, plus `SELECT … INTO`), **view**, **procedure**, **function**, **trigger** (both the DML and the DDL flavor), **index** (CREATE / ALTER / DROP), **schema** (CREATE / DROP, and `ALTER SCHEMA … TRANSFER` → `ALTER_SCHEMA`), **sequence**, **synonym** (CREATE / DROP — T-SQL has no `ALTER SYNONYM`), **type**, **user**, **role** (CREATE / ALTER / DROP), and `sp_rename` → **RENAME**.

**A brand-new DDL trigger doesn't fire for its own `CREATE TRIGGER`**, though a sibling trigger does see that `CREATE_TRIGGER` event — and an `ALTER TRIGGER` *does* run the replaced body for its own `ALTER_TRIGGER`, because the trigger already existed (both probe-confirmed).
`StatementContext.DdlTriggerCreatedThisStatement` carries the one excluded object id.

**Nesting.** DDL triggers nest: a `CREATE_VIEW` trigger runs at `TRIGGER_NESTLEVEL()` 2 for a view a `CREATE_TABLE` body created.
A trigger doesn't re-fire itself for DDL its own body issues — `Simulation.CanFireDdlTrigger` is the innermost-frame test, matching real's default (`RECURSIVE_TRIGGERS` off).
The 32-level nesting cap applies (Msg 217), and DDL frames push `IsAfter = false` so they don't count toward the AFTER-DML `nested triggers` rule.

**Atomic scope.** The bodies run inside one `RunMutation` scope, so everything they wrote rolls back together when a later body throws — the same firing-statement-atomic unit DML triggers get.

`Simulation.ImportBacpac` suppresses firing wholesale via `SimulatedDbConnection.SuppressDdlTriggers`: a bacpac can carry a DDL trigger of its own, and running an audit body against half-built schema would fail the load (real's import path disables DDL triggers for the same reason).

### `EVENTDATA()`

A no-arg built-in returning the `<EVENT_INSTANCE>` document as `xml`, or **NULL** outside a database-scope DDL trigger body — including inside a DML trigger (probe-confirmed).
The document is built once per fire and carried on the body's `TriggerFrame`, so every call within one body returns the same instance, `PostTime` included.

```
<EVENT_INSTANCE><EventType>CREATE_TABLE</EventType><PostTime>2026-07-31T22:39:11.550</PostTime><SPID>53</SPID>
<ServerName>…</ServerName><LoginName>sa</LoginName><UserName>dbo</UserName><DatabaseName>ddlprobe</DatabaseName>
<SchemaName>dbo</SchemaName><ObjectName>t1</ObjectName><ObjectType>TABLE</ObjectType>
<TSQLCommand><SetOptions ANSI_NULLS="ON" ANSI_NULL_DEFAULT="ON" ANSI_PADDING="ON" QUOTED_IDENTIFIER="ON" ENCRYPTED="FALSE"/>
<CommandText>CREATE TABLE t1 (a int)</CommandText></TSQLCommand></EVENT_INSTANCE>
```

Element order is real's.
`SchemaName` is **omitted entirely** for `CREATE_USER` / `CREATE_ROLE` and their siblings, matching real; `TargetObjectName` / `TargetObjectType` follow `ObjectType` for the index and trigger events (naming the parent table or view), and a synonym event carries `TargetObjectName` alone.
`ObjectType` uses real's spellings — `TABLE`, `VIEW`, `INDEX`, `SCHEMA`, `TRIGGER`, `SEQUENCE`, `SYNONYM`, `TYPE`, `ROLE`, `SQL USER`.
`ServerName` is `SIMULATED`, matching `@@SERVERNAME`; `LoginName` / `UserName` read the session's effective principal, so `EXECUTE AS` shows through.
`QUOTED_IDENTIFIER` reflects the session setting; the other `SetOptions` attributes are fixed.

`CommandText` is the statement's own source span, trailing whitespace trimmed.
For a statement whose body runs to end of batch (`CREATE VIEW` / `PROCEDURE` / `TRIGGER`) real keeps the batch's trailing newline and the simulator trims it — a cosmetic divergence.

### Not modeled yet

- **Per-event extra elements**: `AlterTableActionList` (which columns / constraints an `ALTER TABLE` touched), a principal's `SID` / `DefaultSchema` / `DefaultLanguage`, a schema's `OwnerName`, `sp_rename`'s `NewObjectName`, and the empty `TargetServerName` / `TargetDatabaseName` / `TargetSchemaName` trio real puts ahead of a synonym's `TargetObjectName`.
  The common header plus `TSQLCommand` is what an audit body reads.
- **`ALTER SCHEMA … TRANSFER`'s `ObjectType`** reports `OBJECT` / `TYPE` — the transfer's own name class — where real reports the moved object's actual kind (`SYNONYM`, `TABLE`, …).
- **`GRANT` / `DENY` / `REVOKE`** → `GRANT_DATABASE` / `DENY_DATABASE` / `REVOKE_DATABASE`, whose document carries a distinct `Grantor` / `Permissions` / `Grantees` / `GrantOption` block.
- **A body `ROLLBACK` vetoing the DDL** — real undoes the DDL and raises **Msg 3609** (`The transaction ended in the trigger. The batch has been aborted.`), leaving `@@TRANCOUNT` 0 and skipping the rest of the batch.
  The simulator's DDL isn't undoable (schema changes don't enter the undo log — the same asymmetry `CREATE TABLE` has under `ROLLBACK TRAN`), so a body error rolls back what the bodies wrote but leaves the DDL in place.
  `@@TRANCOUNT` in a body reads 0 under auto-commit where real reads 1, for the same reason.
- **`sp_settriggerorder` for DDL triggers** — `@namespace` is accepted and ignored, and the name resolves against DML triggers only.
  Firing order across several DDL triggers is by `object_id` (creation order), which is what real ran them in unpinned.
- **Server-scope triggers** (`ON ALL SERVER`, `sys.server_triggers`, `parent_class = 100`) — neither stored nor fired; only `ON DATABASE` scope exists.

## Not modeled

- **INSTEAD OF UPDATE / DELETE on non-updatable views** — INSTEAD OF INSERT on any view ships; INSTEAD OF UPDATE / DELETE on an updatable (single-base, no DISTINCT / JOIN / aggregate) view ships.
  INSTEAD OF UPDATE / DELETE on a join / aggregate / DISTINCT view raises `NotSupportedException` — implementing it requires executing the view's selection to enumerate would-be-affected rows, which loses heap-row identity and bypasses the existing visibility-filter machinery.
  Deferred.
- **Logon / server triggers** (`ON ALL SERVER`) — only DML triggers and database-scope DDL triggers ship.
- **`@@NESTLEVEL` independence** — the simulator collapses UDF / procedure / trigger depth into a single counter (`SimulatedDbConnection.NestingLevel`).
  `TRIGGER_NESTLEVEL()` reads its own dedicated `TriggerNestLevel` counter, so it's accurate, but `@@NESTLEVEL` (not modeled at all) wouldn't have the right value if added.

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
