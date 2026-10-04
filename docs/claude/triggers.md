# Triggers (DML + DDL)

`CREATE [OR ALTER] TRIGGER [schema.]name ON [schema.]parent { AFTER | FOR | INSTEAD OF } { INSERT | UPDATE | DELETE } [, ...] AS body`, mutated via `ALTER TRIGGER`, dropped via `DROP TRIGGER [IF EXISTS]`, toggled via `{ DISABLE | ENABLE } TRIGGER { name | ALL } ON parent`, fired automatically by the matching DML against the parent.
Body source is captured between `AS` and end-of-batch; re-tokenized per fire inside a child `BatchContext` with a [`TriggerFrame`](../../src/SqlServerSimulator/Parser/TriggerFrame.cs) seeded with the `INSERTED` / `DELETED` pseudo-tables.
It is also bound once at `CREATE` / `ALTER` against *empty* pseudo-tables of the same shape, so a bad column — on the parent, on `INSERTED` / `DELETED`, or inside `UPDATE(col)` — reports Msg 207 there and the trigger isn't created; the parent's own Msg 8197 still comes first → [`programmable.md`](programmable.md#create-time-body-binding).
The body's *parse* comes before the parent, though: a syntax error in it (and what recovery finds past it, Msg 4145 and Msg 137 among them) outranks a missing parent's Msg 8197, which the simulator gets by binding the body against a column-less stand-in parent and keeping only its severity-15 errors (probed 2026-09-30 against SQL Server 2025).
A `#` or `##` temporary parent is **Msg 167** ahead of the body's syntax errors, and a word leading the action list is **Msg 1084** (an invalid event type) where one after a comma is Msg 102.
AFTER (and its `FOR` synonym) attaches to heap tables only; INSTEAD OF attaches to heap tables and views.
Probed against SQL Server 2025.

Database-scope DDL triggers (`CREATE TRIGGER … ON DATABASE`) fire on the DDL the simulator models — see the [DDL triggers](#ddl-triggers--create-trigger--on-database) section below.
Server-scope triggers (`CREATE TRIGGER … ON ALL SERVER`) — logon triggers and server-scope DDL triggers — are in [Server-scope triggers](#server-scope-triggers--on-all-server).

## What's modeled

- **CREATE / ALTER / CREATE OR ALTER TRIGGER** — same upsert pattern as procedures (ObjectId preserved across ALTER), and the same replacement gates: **Msg 2010** when the name holds another object kind, **Msg 2110** when it holds a trigger on a different parent, **Msg 208** when it holds nothing (bare ALTER), **Msg 2714** on a plain CREATE over a taken name (state 2, or state 5 for a CLR trigger; probed 2026-09-28 against SQL Server 2025), and **Msg 166** for a database-qualified trigger name — see [`programmable.md`](programmable.md#replacing-a-module--alter--create-or-alter).
  A missing `ON` target reports its Msg 8197 ahead of all of them.
- **CREATE refusals** — a trigger qualified by a schema other than its parent's is **Msg 2103** (an unqualified one takes the parent's schema), an action listed twice **Msg 1034**, `WITH APPEND` after the action list Msg 195, and `INSTEAD OF UPDATE` / `DELETE` on a view declared `WITH CHECK OPTION` **Msg 2112** (probed 2026-10-04 against SQL Server 2025).
- **DROP TRIGGER [IF EXISTS] name [, ...]** — comma-list form supported via the shared DROP parser.
- **DISABLE / ENABLE TRIGGER { name [, …] | ALL } ON parent** — toggles `Trigger.IsDisabled`.
  Disabled triggers stay in the schema and surface in `sys.triggers.is_disabled` but don't fire.
  A name the parent lacks is **Msg 1088** state 119 naming it as written, and toggles none of the list; the toggle rolls back with an enclosing transaction, and an `ALTER TRIGGER` keeps a disabled trigger disabled (probed 2026-10-04 against SQL Server 2025).
  `ALTER TABLE t { DISABLE | ENABLE } TRIGGER { ALL | name [, …] }` is the table-scoped form; a name the table lacks is Msg 4920 and toggles none of the list (probed 2026-09-25).
  Works on both table and view parents.
- **AFTER INSERT / UPDATE / DELETE** plus the **FOR-synonym-for-AFTER** spelling — table parents only.
  AFTER on a view raises Msg 8197 at state 6, a missing parent's state 4 (probed 2026-09-30).
- **INSTEAD OF INSERT / UPDATE / DELETE** — replaces the would-be DML with the trigger body.
  The heap-write phase is skipped; identity allocation is skipped (INSERTED's identity column shows the type's typed default — 0 for int — rather than the next sequential value); NOT NULL / CHECK / key constraints are not enforced on the suppressed write; AFTER triggers on the same action don't fire.
  DEFAULT-clause evaluation and computed columns still run so INSERTED carries the would-be values (probe-confirmed).
  The pseudo-tables keep an identity column `NOT NULL`, null a `rowversion` column only for an `INSERT`, and an `INSTEAD OF UPDATE` on a table reports the SET list through `UPDATE(col)` / `COLUMNS_UPDATED()` as an AFTER trigger does; `INSERT … DEFAULT VALUES` through a view fires its `INSTEAD OF INSERT` over one row (probed 2026-10-04 against SQL Server 2025).
  Parent can be a heap table or a view.
- **INSTEAD OF on views** — the primary real-world use case: makes a non-updatable view (join / aggregate / etc.) writable → [INSTEAD OF on views](#instead-of-on-views).
- **At most one INSTEAD OF per action per target** (Msg 2111, probe-confirmed verbatim).
  A second INSTEAD OF trigger whose Actions overlap an existing one raises at CREATE TRIGGER time.
  ALTER / CREATE OR ALTER replacing the same trigger by name is permitted (the self-collision is excluded from the check).
  The diagnostic wording uses `table` vs `view` based on the parent kind.
- **Multi-action triggers** (`AFTER INSERT, UPDATE`, `INSTEAD OF INSERT, UPDATE`) — single trigger handles multiple events; the body discriminates via `IF EXISTS (SELECT 1 FROM inserted) ...` or join shape.
- **INSERTED / DELETED pseudo-tables** — bare 1-part names resolve through the new `TriggerFrame.Inserted` / `TriggerFrame.Deleted` slots ahead of the schema / temp-table dispatch.
  Both pseudo-tables are always materialized (matching real SQL Server): an INSERT trigger sees an empty `deleted`, a DELETE trigger sees an empty `inserted`, an UPDATE trigger sees both populated.
  Pseudo-tables are `HeapTable` instances flagged `IsTableVariable` so writes don't touch the regular transaction undo log; columns are shared by reference from the parent table (for table parents) or the view's `OutputColumns` (for view parents).
  Read without an `ORDER BY`, an AFTER trigger's pseudo-tables yield the rows in the **reverse** of the order the statement wrote them and an INSTEAD OF trigger's in that order (probed 2026-09-28 against SQL Server 2025 across heap and keyed targets, `INSERT … VALUES` / `SELECT`, `UPDATE`, `DELETE`, `MERGE`, a cursor, `TOP (1)` and a thousand rows) — unordered on both engines, but a body that logs row by row shows it.
- **The joined shapes a production body is written in** — a body rarely reads one row.
  It reaches its own parent table through an alias and *joins* the pseudo-table: `UPDATE n SET n.tag = dbo.f(n.tag) FROM t n JOIN INSERTED i ON n.id = i.id`, the same family as the aliased `DELETE <alias> FROM …` form.
  An **OR in that join's ON clause** (`ON n.id = i.id OR n.id = i.parent_id`) is the idiom for reaching an inserted row *and* the row it names as parent, and it drives only from the INSERTED rows a `WHERE` leaves standing.
  A **scalar UDF in the SET** evaluates per row, and a set-based `INSERT … SELECT FROM INSERTED JOIN <gate>` writes one row per inserted row — carrying INSERTED's values, which are the rows as written rather than as a later statement in the same body leaves them.
  The body's own UPDATEs dispatch the parent's AFTER UPDATE trigger once per statement over that statement's DELETED set, a no-op self-assignment included.
- **Multiple triggers per table** — every enabled AFTER trigger matching the firing action runs, ordered by `sp_settriggerorder` at the two ends (see [Firing order](#firing-order)).
  Unpinned triggers fire in creation (object id) order, which SQL Server documents as unspecified but was observed to follow (probed 2026-09-28 against SQL Server 2025).
  At most one INSTEAD OF per action per target.
- **TRIGGER_NESTLEVEL()** — the current trigger nesting depth (0 outside any trigger, 1 at top-level DML's first trigger fire, 2+ when nested); with an object id, how many frames on the stack are that trigger's, and with a type and category only frames of that kind, a DDL trigger counting as AFTER (probed 2026-09-26 against SQL Server 2025).
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
  An `UPDATE` whose SET assigns only variables fires the trigger too, over empty pseudo-tables (probed 2026-10-04 against SQL Server 2025).
- **Nesting and recursion gating** — `RECURSIVE_TRIGGERS` (per database) and the `nested triggers` server option decide whether a trigger fires while other triggers are running; see [Nesting and recursion options](#nesting-and-recursion-options).
- **CLR triggers** — `AS EXTERNAL NAME assembly.class.method`, DML (AFTER and INSTEAD OF) and DDL, fire where a T-SQL body would, read `SqlContext.TriggerContext`, and read `INSERTED` / `DELETED` and `EVENTDATA()` through the context connection, whose commands carry the trigger's frame → [`clr-assemblies.md`](clr-assemblies.md#triggers).
- **MERGE routing through INSTEAD OF** — the actions a MERGE's `WHEN` clauses perform must all have an INSTEAD OF trigger on the target, or none: some but not all is **Msg 5316**, raised while compiling, so an un-taken branch's MERGE ends its batch and a `DISABLE TRIGGER` earlier in the same batch hasn't run yet when it's judged (probed 2026-09-27 against SQL Server 2025, table and view).
  A covered action routes through its trigger (no heap write, no identity allocation, no constraint check), and one the triggers don't reach writes normally.

## Implementation map

- **Storage**: [`Trigger`](../../src/SqlServerSimulator/Schemas/Trigger.cs) class (Schema / Name / ObjectId / **Parent** (`object` — HeapTable or View) / Actions flags / Timing / BodyText / IsDisabled / CreateDate / `ParentObjectId` accessor), [`Schema.Triggers`](../../src/SqlServerSimulator/Schema.cs) per-schema dict.
- **Parser**: [`Simulation.CreateTrigger.cs`](../../src/SqlServerSimulator/Simulation/Simulation.CreateTrigger.cs) (CREATE + ALTER + CREATE OR ALTER + DISABLE/ENABLE), routed from `Simulation.Create.cs` / `Simulation.Alter.cs`.
  `DROP TRIGGER` routed through the shared `Simulation.Drop.cs` dispatch (which also cascade-drops triggers when DROP TABLE / DROP VIEW removes the parent).
- **Frame**: [`TriggerFrame`](../../src/SqlServerSimulator/Parser/TriggerFrame.cs) holds the per-fire pseudo-table instances.
  Set on the child `BatchContext` via the new trigger-body constructor; read by [`BatchContext.TryResolveTable`](../../src/SqlServerSimulator/Parser/BatchContext.Resolution.cs) ahead of the temp / `@t` / schema dispatch.
- **Dispatch**: [`Simulation.InvokeTrigger.cs`](../../src/SqlServerSimulator/Simulation/Simulation.InvokeTrigger.cs) — `FireTriggers` walks every schema's `Triggers` dict, materializes the pseudo-tables once per fire, allocates a child `BatchContext`, runs the body via `DispatchStatementsUntil`.
  `TryFireInsteadOfTrigger` is the single-trigger INSTEAD OF dispatch; returns `true` if a trigger fired.
  `HasAfterTrigger` / `HasInsteadOfTrigger` are the fast-path predicates DML sites call first to avoid per-row snapshot capture when no trigger is attached.
  Both predicates route through `CanFireTrigger`, so a trigger the nesting rules suppress reads as absent.
  `MaterializePseudoTable` takes a `HeapColumn[]` directly so the same machinery works for table parents (parent's `Columns`) and view parents (view's `OutputColumns`).
- **DML hooks**: `Simulation.Insert.cs` (INSERT + INSERT … SELECT + INSERT … OUTPUT) detects INSTEAD OF on either the destination view or the destination table and either routes through `ProcessInsteadOfInsertOnView` (for view targets — view INSERT may include non-updatable views) or threads an `insteadOfActive` flag through `ProcessHeapInsert` (for table targets, which skips identity allocation, constraint enforcement, and heap write).
  `Simulation.Update.cs` and `Simulation.Delete.cs` route a view target with INSTEAD OF to `Simulation.InsteadOfView.cs` and thread the table-target detection through their `CommitUpdate` / `CommitDelete` helpers.
  `Simulation.Merge.cs` settles Msg 5316 once the `WHEN` clauses parse, and `CommitMerge` (`Simulation.Merge.Execution.cs`) routes each pending list (inserts, updates, deletes) through trigger-fire or heap-write paths.
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
The sibling option `server trigger recursion` (id 116) round-trips through the catalog like any other but carries no behavior: a server-scope trigger takes the same innermost-frame rule a database-scope DDL trigger does.
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

## INSTEAD OF on views

INSERTED / DELETED are the view's own columns, derived ones computed as the view computes them (probed 2026-09-27 against SQL Server 2025).
Over a table or a view alike they read every column but a computed one as **nullable**, the rows never having been written — a NOT NULL column the INSERT left out reads NULL, which on the wire is a nullable column — and a rowversion as **NULL** (`TryFireInsteadOfTrigger`, probed 2026-10-01 against SQL Server 2025).

- **INSERT** hands the trigger the statement's rows shaped to the view, unspecified columns NULL.
- **UPDATE and DELETE read the view itself** (`Simulation.InsteadOfView.cs`), whatever its shape — an aggregate, `DISTINCT`, a set operation, a join, or an updatable view alike: the `WHERE` picks the view's rows and may name a derived column, those rows are `DELETED`, and for an UPDATE the rows with the `SET` list applied are `INSERTED`.
  A `SET` may target a derived column, and a column it leaves alone keeps its old value — a derived column isn't recomputed from the new ones.
  `UPDATE(col)` / `COLUMNS_UPDATED()` read the view's column positions, `@@ROWCOUNT` is the rows picked, and the trigger fires even when none qualify.
  An UPDATE with a `FROM` clause is **Msg 414**.
  A positioned UPDATE / DELETE (`WHERE CURRENT OF`) through an updatable view keeps the base-row path, where a derived column reads NULL in the pseudo-tables.
- **MERGE** into a view whose INSTEAD OF triggers take its actions matches against the view's rows under its column names, and hands the triggers view-shaped rows the same way (`Simulation.Merge.cs` and `Simulation.Merge.Execution.cs`, the view-rows target).
  A MERGE into a view real can't write through, with no trigger to take it, binds its `WHEN` clauses against the view's columns first: a derived `UPDATE SET` target or `INSERT` column — listed or implied — is Msg 4406, anything else the view's Msg 4403 / 4405.
- **A level over the triggered view** — a view, CTE or derived table reading it as its single source — hands the trigger the rows it shows, its statement naming the level's columns → [`programmable.md`](programmable.md#writes-through-a-cte-or-derived-table).
- **OUTPUT**: to the client it is Msg 334 as on any triggered target; `OUTPUT … INTO` lands its rows before the body runs.
  INSERT's `INSERTED` reads the rows handed to the trigger, `DELETED` reads the view's rows, and `INSERTED` under an UPDATE — or under any MERGE into a triggered view, an INSERT-only one included — is **Msg 404** per column, the would-be row never being formed.
  A table target with an INSTEAD OF UPDATE / DELETE trigger writes its `OUTPUT … INTO` rows too, and fires on an UPDATE matching no row.

## Firing order

`sp_settriggerorder @triggername, @order, @stmttype [, @namespace]` pins a trigger to the front or back of the AFTER triggers a given action runs on its table.
Named and positional argument forms both bind, `@order` / `@stmttype` are case-insensitive, and the name may be bare or schema-qualified.
`@namespace = 'DATABASE' | 'SERVER'` orders a database- or server-scope trigger instead, per event (`CREATE_TABLE`, `LOGON`) — see [Ordering scoped triggers](#ordering-scoped-triggers).

Only the two ends are pinned: `First` runs first, `Last` runs last, and everything between runs in creation (object id) order, which real documents as unspecified but was observed to follow (probed 2026-09-28 against SQL Server 2025).
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
`RunTriggerBodies` buffers them on `BatchContext.PendingTriggerOutcomes`, with the body's messages and the errors it ran past, in the order the body sent them, and `DispatchOneStatement` drains that ahead of the statement's own outcome.
The body's row counts travel the same way, ahead of the firing statement's own: real sends each body statement's count, so `ExecuteNonQuery` over an `INSERT` of two rows whose trigger writes two more returns 4 and raises `StatementCompleted` for both, unless the body (or the session, which it inherits) sets `NOCOUNT` — EF Core's trigger-safe shape does (probed 2026-09-26 against SQL Server 2025).

Several triggers contribute theirs in their firing order (see [Firing order](#firing-order)).

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

### A body `ROLLBACK` — Msg 3609

A DML trigger body fired by an auto-commit statement runs inside that statement's own transaction, so `@@TRANCOUNT` reads 1 there (probed 2026-09-26 against SQL Server 2025).
A `ROLLBACK` in the body ends it — the user's transaction when there is one, else that auto-commit unit, undoing the firing statement's writes and the body's so far — and `@@TRANCOUNT` reads 0 after it.
The body runs on, what it writes afterwards commits on its own (a body `INSERT` after the `ROLLBACK` survives), and its `RAISERROR`s reach the client; when it returns, **Msg 3609** (`The transaction ended in the trigger. The batch has been aborted.`) ends the batch, attributed to the firing statement.
`SimulatedDbConnection.TriggerTransactionEnded` carries the fact from the `ROLLBACK` to the body's return.
A `COMMIT` that ends the same transaction — the auto-commit unit, or the user's when it brings `@@TRANCOUNT` to 0 — does the same with the writes kept: the firing statement's rows and the body's so far commit, and Msg 3609 follows; one that leaves a nested user transaction open ends nothing (probed 2026-09-28 against SQL Server 2025).
Msg 3609 needs the body to return with no transaction open: one that ended the transaction and began another leaves the firing statement standing (`SimulatedDbConnection.TriggerReplacedTransaction`).
Over an auto-commit statement the statement's end then takes one level off the transaction the body began, as it would have off the unit it replaced — one `BEGIN` commits, two leave `@@TRANCOUNT` at 1 — and over a user transaction the body's transaction stays open as the user's (probed 2026-09-28 against SQL Server 2025).
An error after it ends the batch with its own number, and what was committed stays.
`XACT_STATE()` reads 1 in the unit, as it does in any statement writing a table or table variable.

The unit takes savepoints: `SAVE TRAN` in a body over an auto-commit statement names a point a later `ROLLBACK TRAN <name>` returns to, keeping the firing statement's rows and undoing only the body's writes since (`SimulatedDbConnection.TriggerUnitSavepoints`).
An error the body catches with its forced `XACT_ABORT` on dooms the unit as it would a user transaction: `XACT_STATE()` reads -1, the next write is **Msg 3930**, and Msg 3621 follows for the firing statement (`SimulatedDbConnection.TriggerUnitDoomed`; probed 2026-10-04 against SQL Server 2025).

### Msg 3616 — the body's own TRY / CATCH doesn't rescue it

An error of severity **11 or higher** raised while a body runs aborts the batch and rolls the unit back *even when the body's own `TRY` / `CATCH` handled it*, surfacing:

> Msg 3616, Level 16, State 1 — `An error was raised during trigger execution. The batch has been aborted and the user transaction, if any, has been rolled back.`

Severity ≤ 10 is informational and leaves the unit intact (a caught `RAISERROR(…, 10, 1)` keeps both the body's writes and the firing statement's).
So does any error caught after the body's own `SET XACT_ABORT OFF`, which dooms nothing (probed 2026-09-28 against SQL Server 2025).
An error the body leaves *un*handled propagates with its own number instead — an outer `CATCH` sees `ERROR_NUMBER()` 51000 for a body-side `THROW 51000`, with `ERROR_PROCEDURE()` naming the trigger — so Msg 3616 fires only for the swallowed case.
An error caught inside a stored procedure the body called counts too, which is why `SimulatedDbConnection.TriggerBodyErrorRaised` is connection-scoped; it's saved and cleared per body so a handled error in one trigger doesn't condemn the next.

## Writing the pseudo-tables

A body's `INSERT` / `UPDATE` / `DELETE` of `INSERTED` or `DELETED` is **Msg 286** at `CREATE TRIGGER` (probed 2026-10-01 against SQL Server 2025).
An AFTER trigger's body reading a `text` / `ntext` / `image` column of either is **Msg 311** at `CREATE TRIGGER` (`HeapTable.RefusesLegacyLobReads`); an INSTEAD OF trigger's reads them (probed 2026-10-04 against SQL Server 2025).

## Errors in a trigger body

A body starts under `SET XACT_ABORT ON` whatever the session says — `@@OPTIONS & 16384` reads 16384 inside it — so its errors follow that option's rules (probed 2026-09-24 against SQL Server 2025; [`transactions.md`](transactions.md#set-xact_abort)):
- An error the body leaves unhandled ends the firing batch and rolls the transaction back, and so does one from a procedure or dynamic SQL the body calls, which inherit the option.
  The firing statement still sends Msg 3621 after it, at line 1 and unattributed, which an error ending the batch from the statement itself doesn't (`SimulatedSqlException.EndedTriggerBody`; probed 2026-10-04 against SQL Server 2025).
- Caught by a `TRY` in the firing batch, it dooms the transaction.
  A `TRY` catches the body's binder errors too — those of the compile as the trigger first fires and a missing object the body names when it runs — which otherwise end the batch the same way (probed 2026-10-01 against SQL Server 2025).
- `RAISERROR`, which the option exempts, lets the body run on the way a procedure body does ([`control-flow.md`](control-flow.md#procedure-and-dynamic-sql-bodies)): the error reaches the client among the body's output, and the firing statement keeps its rows.
  So does any error after the body's own `SET XACT_ABORT OFF`.

### Not modeled yet

- **Msg 3621 after a non-writing statement's error in a body that turned `XACT_ABORT` off** — real sends it for the firing statement; here only an error escaping the body earns it.
- **Msg 3621 after an error escaping the body** carries the trigger as its `Procedure` and the failing statement's line on real when that statement writes nothing (Msg 8134 from `SELECT 1/0`), and isn't sent at all after a Msg 208 a function the body calls raises as it runs; here it follows at line 1 unattributed in both (probed 2026-10-04 against SQL Server 2025).
  A nested trigger's error ending a chain of two is the same: real attributes the 3621 to the inner trigger.
- **A `SELECT` the body began before a caught error** — real sends nothing for it; here its empty result set reaches the client ahead of Msg 3930.
- **`COLUMNS_UPDATED()` in a function a trigger body calls** reads the trigger's mask on real; here it reads NULL, as outside any trigger (probed 2026-10-04 against SQL Server 2025).

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

Error paths (probe-confirmed): an unknown column raises **Msg 207**, a computed column **Msg 2114** and `UPDATE(col)` in a DDL trigger **Msg 1097**, all at `CREATE TRIGGER` (probed 2026-10-04 against SQL Server 2025); use outside any trigger raises **Msg 140** (`"Can only use IF UPDATE within a CREATE TRIGGER statement."`); a qualified name (`UPDATE(t.c1)`) raises Msg 102 near `'.'` and the no-arg `UPDATE()` raises Msg 102 near `')'`.
`COLUMNS_UPDATED()` is deliberately asymmetric — outside a trigger it returns **NULL** rather than raising.


## DDL triggers — `CREATE TRIGGER … ON DATABASE`

Database-scope DDL triggers fire on the DDL the simulator models, with `EVENTDATA()` describing the statement.
AW's `[ddlDatabaseTriggerLog]` (`FOR DDL_DATABASE_LEVEL_EVENTS`) loads end-to-end and surfaces in `sys.triggers` with the probe-confirmed shape: `parent_class=0`, `parent_class_desc='DATABASE'`, `parent_id=0`, `type_desc='SQL_TRIGGER'`, `is_ms_shipped=0`, `is_instead_of_trigger=0`.
The full `CREATE TRIGGER` text lands in `sys.sql_modules.definition` via `SchemaObject.DefinitionText`; `is_ms_shipped`'s absence was one gate (Msg 207 aborted the whole DDL-trigger populator).

DacFx's `SqlDatabaseDdlTrigger` element carries an `EventType` relationship of `SqlTriggerEventTypeSpecifier` entries built from `sys.trigger_events` — **not** reverse-engineered from the module definition.
Without those rows DacFx drops the whole element silently (AW's `[ddlDatabaseTriggerLog]` vanished from re-exports).
The simulator expands them: a trigger created `FOR DDL_DATABASE_LEVEL_EVENTS` surfaces one `sys.trigger_events` row per **leaf** event in the group's transitive closure — 158 rows, each carrying the group's id/desc in `event_group_type`(`_desc`) = `10016` / `DDL_DATABASE_LEVEL_EVENTS`, `is_first`/`is_last` = 0 unless `sp_settriggerorder … 'DATABASE'` pinned that event (see [Ordering scoped triggers](#ordering-scoped-triggers)), `is_trigger_event` = 1 (probe-confirmed against SQL Server 2025's AW).
The closure is computed from a hard-coded copy of SQL Server's static `sys.trigger_event_types` catalog (`src/SqlServerSimulator/TriggerEventTypes.cs`, 312 rows: `type` / `type_name` / `parent_type`), also surfaced as the `sys.trigger_event_types` catalog view.
Individual-event names (`FOR CREATE_TABLE`) emit a single row with a NULL group.

**Storage**: `DdlTrigger` class (`src/SqlServerSimulator/Schemas/DdlTrigger.cs`) carries name + object_id + event-type list + body source + body line offset + `is_disabled` flag, plus the `Covers` predicate that expands the declared events to their leaf closure once.
`Database.DdlTriggers` is the per-database `ConcurrentDictionary<string, DdlTrigger>` (case-insensitive keys); not per-schema because DDL triggers belong to the database itself.
The class extends `SchemaObject` for the object-id + create-date pattern but doesn't participate in any schema's shared namespace: its name clashes only with another database-scope trigger (Msg 2714 state 2), a same-named DML trigger, table or procedure is no conflict in either direction, and an `ALTER` of a missing one is Msg 208 state 6 (probed 2026-09-28 against SQL Server 2025).

**Parser**: `Simulation.CreateTrigger.cs::TryParseCreateTrigger` — after `ON`, if the next token is `DATABASE`, dispatch to `ParseDdlTriggerBody` which handles `[WITH options] {FOR|AFTER} <event_type_list> AS <body>`.
Event types parse as bare identifiers and store verbatim in `DdlTrigger.EventTypes`; matching at fire time is case-insensitive.
A name the event catalog doesn't carry is **Msg 1084**, and one the scope can't raise — a DML action, `LOGON`, or a server-level event such as `CREATE_LOGIN` on `ON DATABASE` — **Msg 1098**; a schema-qualified trigger name is **Msg 1094** and `WITH EXECUTE AS OWNER` **Msg 1083**, on either scope (all probed 2026-09-28 against SQL Server 2025).
`DROP TRIGGER name [, …] ON { DATABASE | ALL SERVER }` lives in `Simulation.Drop.cs::DropOneTrigger`, which looks past the rest of the comma list for the scope trailer (`PeekTriggerDropScope`) — the trailer scopes every name in it.
`{ DISABLE | ENABLE } TRIGGER { name | ALL } ON { DATABASE | ALL SERVER }` routes through the same `TryParseEnableOrDisableTrigger` the DML form uses, branching on the scope after `ON`; a disabled trigger stays in its catalog with `is_disabled = 1` and doesn't fire, and a name the scope doesn't hold is **Msg 1088** state 119.
Creating, altering and dropping a database- or server-scope trigger rolls back with an enclosing transaction.

**Catalog**: `sys.triggers` enumerator in `BuiltInResources.cs::EnumerateSysTriggers` yields rows for `Database.DdlTriggers` after the per-schema DML trigger loop, with the `parent_class=0` shape above.
`sys.trigger_events` (`BuiltInResources.ConstraintsAndTriggers.cs::EnumerateSysTriggerEvents`) yields the expanded leaf-event rows for each DDL trigger after the DML-trigger loop; `sys.trigger_event_types` is a server-scoped view over `TriggerEventTypes.All`.

### Firing

`Simulation.RecordDdlEvent` is called by each modeled DDL processor once its own work succeeded, appending a `DdlEventInfo` to `StatementContext.PendingDdlEvents`; `Simulation.FireDdlTriggers` drains that from the dispatch loop right after `DispatchOneStatementCore` returns.
Recording after success and firing after the statement is what gives the probe-confirmed shape: **a failed DDL raises no event**, an un-taken `IF` branch raises none, and the body already sees the finished change (`OBJECT_ID` of the new table resolves inside a `CREATE_TABLE` body).
The fire sits inside the dispatcher's own `try`, so a body error becomes the statement's error — reaching an enclosing `TRY` / `CATCH`, tripping Msg 3616 for a swallowed one, and carrying the trigger's unqualified name as `ERROR_PROCEDURE`.
A body `SELECT` becomes the firing statement's result set through the same `PendingTriggerOutcomes` buffer DML bodies use.

Matching is on the **expanded leaf event set**, so `FOR DDL_TABLE_EVENTS` fires on exactly the `CREATE_TABLE` / `ALTER_TABLE` / `DROP_TABLE` rows it projects into `sys.trigger_events`.
One statement can raise several events — `DROP TABLE a, b` raises one `DROP_TABLE` per name, each carrying the whole statement as `CommandText` (probe-confirmed) — and `SELECT … INTO` raises `CREATE_TABLE` while a `#temp` destination raises nothing.

Events raised, by object kind: **table** (CREATE / ALTER / DROP, plus `SELECT … INTO`), **view**, **procedure**, **function**, **trigger** (both the DML and the DDL flavor), **index** (CREATE / ALTER / DROP), **schema** (CREATE / DROP, and `ALTER SCHEMA … TRANSFER` → `ALTER_SCHEMA`), **sequence**, **synonym** (CREATE / DROP — T-SQL has no `ALTER SYNONYM`), **type**, **user**, **role** and **application role** (CREATE / ALTER / DROP), **XML schema collection** (CREATE / DROP), **full-text catalog** and **full-text index** (CREATE / ALTER / DROP), `sp_rename` → **RENAME**, a database-scope `GRANT` / `DENY` / `REVOKE` → **`GRANT_DATABASE`** / **`DENY_DATABASE`** / **`REVOKE_DATABASE`** (one per statement, whatever it names), `sp_addextendedproperty` / `sp_updateextendedproperty` / `sp_dropextendedproperty` → **`CREATE_`** / **`ALTER_`** / **`DROP_EXTENDED_PROPERTY`**, and `sp_bindefault` / `sp_bindrule` / `sp_unbindefault` / `sp_unbindrule` → **`BIND_`** / **`UNBIND_DEFAULT`** / **`_RULE`** (probed 2026-09-28 against SQL Server 2025 for the last six kinds).
Each lands in the event groups `sys.trigger_event_types` gives it, so `DDL_DATABASE_LEVEL_EVENTS` sees them all and `DDL_GDR_DATABASE_EVENTS`, `DDL_EXTENDED_PROPERTY_EVENTS` and their siblings see their own.

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
`SchemaName` is **omitted entirely** for `CREATE_USER` / `CREATE_ROLE` and their siblings, an application role's and a full-text catalog's, matching real; `TargetObjectName` / `TargetObjectType` follow `ObjectType` for the index and trigger events (naming the parent table or view — for a database-scope trigger an empty `SchemaName` and `TargetObjectName` with `TargetObjectType` `Database`), for `UPDATE_STATISTICS` (which carries no `ObjectName`) and for an `sp_rename` of a column or index (naming its table; an object's carries an empty pair), and a synonym event carries `TargetObjectName` alone (probed 2026-10-04 against SQL Server 2025).
`ObjectType` uses real's spellings — `TABLE`, `VIEW`, `INDEX`, `SCHEMA`, `TRIGGER`, `SEQUENCE`, `SYNONYM`, `TYPE`, `ROLE`, `SQL USER`, `APPLICATION ROLE`, `XML SCHEMA COLLECTION`, `FULLTEXT CATALOG`; a full-text index event names its table.

Three kinds carry elements of their own after the common ones (probed 2026-09-28 against SQL Server 2025):
- A **permission** event names the securable — an object under its schema, a schema, principal or the database itself under an empty `SchemaName` — then `Grantor` (the session's user, except that a user or application-role securable is its own grantor and a role its owner), `Permissions` (each in lower case, a column list dropped), `Grantees`, `AsGrantor` (the `AS` principal, else empty), `GrantOption` (1 for `WITH GRANT OPTION` and `REVOKE GRANT OPTION FOR`) and `CascadeOption` (1 only when a grantee held the grant option a written `CASCADE` reaches).
- An **extended-property** event names the deepest level given — a level-2 object with its level-1 host as `TargetObjectName` / `TargetObjectType`, a schema or the database under an empty `SchemaName` — with the target pair always present, then `PropertyName`, `PropertyValue` (not for a drop) and `Parameters`, one `Param` per procedure parameter in order, as written.
- A **binding** event names the default or rule for `BIND_*`, and for `UNBIND_*` the column (under its table's schema) or alias type, then `Parameters`.
`ServerName` is `SIMULATED`, matching `@@SERVERNAME`; `LoginName` / `UserName` read the session's effective principal, so `EXECUTE AS` shows through.
`QUOTED_IDENTIFIER` reflects the session setting; the other `SetOptions` attributes are fixed.

`CommandText` is the statement's source, over an extent real settles by the statement's kind (probed 2026-09-28 against SQL Server 2025; `Simulation.CommandTextExtentOf` reads it off the leading words):
- a table, index, statistics, database or XML-schema-collection statement (comments after its last token excluded), an `EXEC` of a procedure that raises one, and the `DROP` of a table, view, module, index, sequence, synonym, user, default, rule or partition function, reports its own tokens, without its `;`;
- a login, user (bar `DROP USER`), role, application-role, `GRANT` / `DENY` / `REVOKE`, `CREATE SYNONYM`, `CREATE` / `ALTER SEQUENCE`, `CREATE` / `DROP TYPE`, `DROP SCHEMA`, `ALTER AUTHORIZATION`, partition-scheme, `CREATE PARTITION FUNCTION`, full-text catalog or index, or `ALTER DATABASE SCOPED CONFIGURATION` statement reports everything up to the next statement's first token — its `;`, the whitespace and comments after it — or to the end of the batch;
- a statement a batch must hold alone — `CREATE` / `ALTER` of a view, procedure, function or trigger, `CREATE DEFAULT` / `RULE` / `SCHEMA` — reports the whole batch, leading comments and whitespace included.

### Not modeled yet

- **Per-event extra elements**: `AlterTableActionList` (which columns / constraints an `ALTER TABLE` touched), a principal's `SID` / `DefaultSchema` / `DefaultLanguage`, an application role's included (the role-member events carry `RoleName` but not these two, and `sp_addrolemember`'s event reports its own `EXEC` text where real reports the `ALTER ROLE` it runs), a schema's `OwnerName`, `sp_rename`'s `NewObjectName`, and the empty `TargetServerName` / `TargetDatabaseName` / `TargetSchemaName` trio real puts ahead of a synonym's `TargetObjectName`.
  The common header plus `TSQLCommand` is what an audit body reads.
- **`ALTER SCHEMA … TRANSFER`'s `ObjectType`** reports `OBJECT` / `TYPE` — the transfer's own name class — where real reports the moved object's actual kind (`SYNONYM`, `TABLE`, …).
- **A binding procedure's rows-affected count** — an `EXEC sp_bindefault` a DDL trigger fires for reports the body's writes as its own count, where real reports its internal statements' (probed 2026-09-28 against SQL Server 2025).
- **A body `ROLLBACK` vetoing the DDL** — real undoes the DDL and raises **Msg 3609** (`The transaction ended in the trigger. The batch has been aborted.`), leaving `@@TRANCOUNT` 0 and skipping the rest of the batch.
  An auto-commit DDL statement runs with no transaction open here, so nothing logs it for undo: a body error rolls back what the bodies wrote but leaves the DDL in place, and `@@TRANCOUNT` in a body reads 0 where real reads 1.
  A server-scope trigger's is the same — Msg 3609 is raised, but a `CREATE LOGIN` it vetoed stays (probed 2026-09-28 against SQL Server 2025).

## Server-scope triggers — `ON ALL SERVER`

`CREATE [OR ALTER] TRIGGER name ON ALL SERVER [WITH …] { FOR | AFTER } <event> [, …] AS body` stores a server-scope trigger on `Simulation.ServerTriggers` — a `DdlTrigger` with `IsServerScoped` set, its object id drawn from `master` — and `sys.server_triggers` (`parent_class` 100, `SERVER`), `sys.server_trigger_events` and `sys.server_sql_modules` project it from every database.
It isn't in `sys.triggers`, `sys.objects` or `OBJECT_ID`, and its name lives in a namespace of its own: a table, or a database-scope trigger, of the same name is no conflict, while a second server trigger is **Msg 2714**.
The event list may mix `LOGON` with DDL events and groups, the server-level ones (`DDL_SERVER_LEVEL_EVENTS`, `DDL_LOGIN_EVENTS`, `CREATE_DATABASE`) included; `LOGON` isn't in `sys.trigger_event_types`, and its `sys.server_trigger_events` row is type 147.
Creating, altering and dropping one takes a sysadmin — real's `CONTROL SERVER` — else **Msg 2104**, and the body binds at CREATE in `master` as a database-scope trigger's does.
`WITH EXECUTE AS` names a **login**: a missing one is **Msg 15151** (`Cannot execute as the login …`), `SELF` is the login running the CREATE, and `sys.server_sql_modules.execute_as_principal_id` records its server principal id.
The body runs as that login's user in `master`, `SUSER_SNAME()` reading the login and `ORIGINAL_LOGIN()` the session's.
All probed 2026-09-28 against SQL Server 2025.

### Logon triggers

A trigger naming `LOGON` fires every time a session opens: an in-process `SimulatedDbConnection.Open`, a TDS login once its credentials and database have settled, and a pooled connection's reset (`sp_reset_connection`), which real treats as a login again.
`Simulation.FireLogonTriggers` is the one entry point; with no server trigger it costs a single read of the `ServerTriggerRegistry.All` snapshot, which is rebuilt on each change for exactly that reason.

The body runs in `master` whatever database the login asked for, as the login's user there (`guest` for a login mapped nowhere else) unless `EXECUTE AS` names another, one trigger nesting level deep, and inside one unit that reads `@@TRANCOUNT` 1 under `XACT_ABORT ON` — the DML-trigger body rules of [Errors in a trigger body](#errors-in-a-trigger-body) and [Msg 3616](#msg-3616--the-bodys-own-try--catch-doesnt-rescue-it).
`ORIGINAL_DB_NAME()` reads the requested database (empty when none was named), `APP_NAME()` / `HOST_NAME()` the client's, and `sys.dm_exec_sessions.status` reads `preconnect` while it runs.
`NOCOUNT` is on and `SET NOCOUNT OFF` does nothing, `SET` options and a `#temp` table don't follow the session out, and `SESSION_CONTEXT` / `CONTEXT_INFO` do.

A `COMMIT` commits that unit: `@@TRANCOUNT` and `XACT_STATE()` read 0 after it, what the body wrote before and after it stays, and from then on nothing refuses the login — not the Msg 3609 it earns, a later error, a result set, a `ROLLBACK`, nor a transaction left open, which commits (probed 2026-09-28 against SQL Server 2025).
Short of that, any of these fails the trigger, and the login is refused with **Msg 17892** (`Logon failed for login '…' due to trigger execution.`, class 14 as the client sees it; real logs it at 20):

- a `ROLLBACK` — what the body writes after it commits on its own, as in a DML trigger;
- an error the body leaves unhandled, or one it swallowed while `XACT_ABORT` was still on;
- a result set, from the body or from dynamic SQL it runs (real's Msg 575 goes only to its error log);
- a transaction the body opened and left open.

`RAISERROR` at any severity leaves the login standing, and nothing the body prints reaches the client — real writes `PRINT` and `RAISERROR` text to its error log.
The in-process `Open` throws the refusal and leaves the connection closed.
Over the wire the client sees it after the login's database and language notices, with no `LOGINACK`, so SqlClient reports Msg 17892 with Msg 5701 and 5703 beside it; at a pooled reset it is followed by **Msg 596** (class 21, the session killed) and `DONE_SRVERROR`, which SqlClient reports as its own severe error, and the connection closes.

`EVENTDATA()` for `LOGON` is its own document, in real's element order:

```
<EVENT_INSTANCE><EventType>LOGON</EventType><PostTime>…</PostTime><SPID>53</SPID><ServerName>SIMULATED</ServerName>
<LoginName>probe_x</LoginName><LoginType>SQL Login</LoginType><SID>…</SID><ClientHost>127.0.0.1</ClientHost><IsPooled>0</IsPooled></EVENT_INSTANCE>
```

`SID` is the login's sid in base64 (`AQ==` for `sa`); `ClientHost` is the peer's address over TDS and real's shared-memory spelling `<local machine>` in-process; `IsPooled` is 1 for a pooled reset.

### Server-scope DDL triggers

A server-scope trigger naming DDL events fires on the database-level events of **every** database — ahead of that database's own triggers for the same event — and on the server-level events the simulator raises: `CREATE_LOGIN` / `ALTER_LOGIN` / `DROP_LOGIN` and `CREATE_DATABASE` / `ALTER_DATABASE` / `DROP_DATABASE` (`Simulation.RecordServerDdlEvent`).
Its body runs in `master` (`DB_NAME()` reads `master`, `EVENTDATA()`'s `DatabaseName` the event's), and a body result set is the firing statement's as for a database-scope trigger.

A server-level event's document carries no `UserName`: a database event names its `DatabaseName`, and a login event `ObjectName` / `ObjectType` `LOGIN` followed by `DefaultLanguage`, `DefaultDatabase`, `LoginType` and `SID` — the server defaults, since no per-login default is kept — with the password literal in `CommandText` masked as `'******'`.

### Ordering scoped triggers

`sp_settriggerorder … @namespace = 'SERVER' | 'DATABASE'` pins a server- or database-scope trigger first or last among those one event fires, recorded on `DdlTrigger.FirstForEvents` / `LastForEvents` and read back as `is_first` / `is_last` in `sys.server_trigger_events` / `sys.trigger_events`.
The name resolves in the named scope only (**Msg 15165**), the event must be a single event rather than a group (**Msg 15600**) and one the trigger fires on (**Msg 15125**), and a slot another trigger holds is **Msg 15130** — which, unlike a table trigger's, lowercases both words (probed 2026-09-28 against SQL Server 2025).
Unpinned triggers of one scope fire in creation order.

### Not modeled yet

- **CLR server-scope triggers** (`ON ALL SERVER … AS EXTERNAL NAME`) raise `NotSupportedException`, and `sys.server_assembly_modules` isn't projected.
- **Server-level events past logins and databases** — endpoints, credentials, server roles and permissions, linked servers and the rest of `DDL_SERVER_LEVEL_EVENTS` raise none.
- **A logon trigger's `COMMIT`** is Msg 3902 here and refuses the login; real lets the login stand.
- **A login's default database** — `ALTER LOGIN … DEFAULT_DATABASE` is discarded, so a login lands where the connection asks or on the simulator's default.
- **Login-event `CommandText`** — real keeps the statement's `;` and the newline after it; the simulator trims them.

## Not modeled yet

- **The `nested triggers` option read per batch** — real reads it as a batch compiles, so a `RECONFIGURE` in the same batch takes effect from the next batch; here the firing statement reads the live value (probed 2026-10-04 against SQL Server 2025).

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
