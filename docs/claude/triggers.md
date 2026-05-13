# DML triggers

`CREATE [OR ALTER] TRIGGER [schema.]name ON [schema.]parent_table { AFTER | FOR } { INSERT | UPDATE | DELETE } [, ...] AS body`, mutated via `ALTER TRIGGER`, dropped via `DROP TRIGGER [IF EXISTS]`, toggled via `{ DISABLE | ENABLE } TRIGGER { name | ALL } ON table`, fired automatically by the matching DML against the parent. Body source is captured between `AS` and end-of-batch; re-tokenized per fire inside a child `BatchContext` with a [`TriggerFrame`](../../SqlServerSimulator/Parser/TriggerFrame.cs) seeded with the `INSERTED` / `DELETED` pseudo-tables. Only AFTER (and its FOR synonym) is modeled — `INSTEAD OF` raises `NotSupportedException`. Probed against SQL Server 2025 (2026-05-13).

## What ships

- **CREATE / ALTER / CREATE OR ALTER TRIGGER** — same upsert pattern as procedures (ObjectId preserved across ALTER).
- **DROP TRIGGER [IF EXISTS] name [, ...]** — comma-list form supported via the shared DROP parser.
- **DISABLE / ENABLE TRIGGER { name | ALL } ON table** — toggles `Trigger.IsDisabled`. Disabled triggers stay in the schema and surface in `sys.triggers.is_disabled` but don't fire.
- **AFTER INSERT / UPDATE / DELETE** plus the **FOR-synonym-for-AFTER** spelling.
- **Multi-action triggers** (`AFTER INSERT, UPDATE`) — single trigger handles multiple events; the body discriminates via `IF EXISTS (SELECT 1 FROM inserted) ...` or join shape.
- **INSERTED / DELETED pseudo-tables** — bare 1-part names resolve through the new `TriggerFrame.Inserted` / `TriggerFrame.Deleted` slots ahead of the schema / temp-table dispatch. Both pseudo-tables are always materialized (matching real SQL Server): an INSERT trigger sees an empty `deleted`, a DELETE trigger sees an empty `inserted`, an UPDATE trigger sees both populated. Pseudo-tables are `HeapTable` instances flagged `IsTableVariable` so writes don't touch the regular transaction undo log; columns are shared by reference from the parent table.
- **Multiple triggers per table** — all enabled AFTER triggers matching the firing action run in registration order (schema-dict insertion order).
- **TRIGGER_NESTLEVEL()** — no-arg form only; returns the current trigger nesting depth (0 outside any trigger, 1 at top-level DML's first trigger fire, 2+ when nested). One-arg form (filter by trigger object id) deferred.
- **`sys.triggers` catalog view** with the documented load-bearing column subset (`name`, `object_id`, `parent_class=1`, `parent_class_desc='OBJECT_OR_COLUMN'`, `parent_id`, `type='TR'`, `type_desc='SQL_TRIGGER'`, `create_date`, `modify_date`, `is_disabled`, `is_instead_of_trigger`, `is_not_for_replication=0`). Triggers also appear in `sys.objects` with `type='TR'` and `parent_object_id` set to the parent table's `object_id`.
- **Trigger-error rollback** — a body-side `THROW` (or any uncaught exception) propagates up. The firing DML's statement-atomic undo log walks back, reverting the heap insert/update/delete.
- **Direct-recursion suppression** — matches real SQL Server's default `RECURSIVE_TRIGGERS OFF`. The connection's `FiringTriggerIds` set tracks in-flight trigger ObjectIds; the dispatcher skips fires whose ObjectId is already in flight. Trigger T can still cause trigger U to fire via cross-table DML; only same-trigger recursion is blocked.

## Implementation map

- **Storage**: [`Trigger`](../../SqlServerSimulator/Trigger.cs) class (Schema / Name / ObjectId / ParentTable / Actions flags / Timing / BodyText / IsDisabled / CreateDate), [`Schema.Triggers`](../../SqlServerSimulator/Schema.cs) per-schema dict.
- **Parser**: [`Simulation.CreateTrigger.cs`](../../SqlServerSimulator/Simulation/Simulation.CreateTrigger.cs) (CREATE + ALTER + CREATE OR ALTER + DISABLE/ENABLE), routed from `Simulation.Create.cs` / `Simulation.Alter.cs`. `DROP TRIGGER` routed through the shared `Simulation.Drop.cs` dispatch.
- **Frame**: [`TriggerFrame`](../../SqlServerSimulator/Parser/TriggerFrame.cs) holds the per-fire pseudo-table instances. Set on the child `BatchContext` via the new trigger-body constructor; read by [`BatchContext.TryResolveTable`](../../SqlServerSimulator/Parser/BatchContext.cs) ahead of the temp / `@t` / schema dispatch.
- **Dispatch**: [`Simulation.InvokeTrigger.cs`](../../SqlServerSimulator/Simulation/Simulation.InvokeTrigger.cs) — `FireTriggers` walks every schema's `Triggers` dict, materializes the pseudo-tables once per fire, allocates a child `BatchContext`, runs the body via `DispatchStatementsUntil`. `HasAfterTrigger` is the fast-path predicate the DML sites call first to avoid per-row snapshot capture when no trigger is attached.
- **DML hooks**: `Simulation.Insert.cs` (INSERT + INSERT … SELECT + INSERT … OUTPUT), `Simulation.Update.cs` (no-FROM and joined-source UPDATE), `Simulation.Delete.cs` (no-FROM and joined-source DELETE), `Simulation.Merge.cs` (the MERGE INSERT branch). UPDATE / DELETE force `FullOld` snapshot capture when a matching AFTER trigger exists so DELETED projects correctly.
- **Connection state**: [`SimulatedDbConnection.FiringTriggerIds`](../../SqlServerSimulator/SimulatedDbConnection.cs) (recursion guard) + `TriggerNestLevel` (surfaced by `TRIGGER_NESTLEVEL()`).

## Not modeled

- **INSTEAD OF triggers** — parser recognizes the keyword and routes to `NotSupportedException`. The semantic (DML routes through the trigger body INSTEAD of the table writer) is structurally different from AFTER; a follow-up bundle.
- **DDL / logon / server triggers** — only OBJECT-scoped DML triggers ship. `parent_class` is hardcoded to 1 in `sys.triggers`.
- **`RECURSIVE_TRIGGERS ON`** — direct recursion is unconditionally suppressed. The database option to allow it isn't surfaced.
- **`is_nested_triggers_on = OFF`** — cross-table cascading triggers always fire (depth-limited only by `MaxNestingLevel`).
- **`@@NESTLEVEL` independence** — the simulator collapses UDF / procedure / trigger depth into a single counter (`SimulatedDbConnection.NestingLevel`). `TRIGGER_NESTLEVEL()` reads its own dedicated `TriggerNestLevel` counter, so it's accurate, but `@@NESTLEVEL` (not modeled at all) wouldn't have the right value if added.
- **Trigger body's DML inside the parent's atomic scope** — minor fidelity gap: when a trigger body runs multiple statements and the second one throws, the first statement's writes (e.g. into an audit log) don't roll back because the trigger body's child `BatchContext` allocates fresh per-statement undo logs rather than sharing the parent statement's log. Real SQL Server rolls back the entire parent + trigger atomic unit. Common idioms (single-statement triggers, body-side `THROW` before any side effects) work correctly; multi-statement bodies with mid-body throws after side effects are the gap.
- **Trigger-body result sets** — a `SELECT` inside a trigger body emits a result set in real SQL Server (probe-confirmed). The simulator's trigger invocation drains and discards yielded result sets at the call site (rare pattern in apps; revisit if needed).
- **`UPDATE()`** / `COLUMNS_UPDATED()` intrinsics — not modeled. Trigger bodies that need per-column change detection have to compare INSERTED vs DELETED manually.
- **`sp_settriggerorder`** — not modeled; firing order is registration order rather than user-controllable.

## EF Core reach

EF Core 7+ has one trigger-aware annotation: `entityType.ToTable(b => b.HasTrigger("name"))`. Without it, EF's SaveChanges emits `INSERT … OUTPUT INSERTED.Id VALUES (…)` — fast but breaks under some trigger configurations in real SQL Server. With it, EF switches to the trigger-safe shape: `SET NOCOUNT ON; INSERT … VALUES (…); SELECT [Id] FROM [t] WHERE @@ROWCOUNT = 1 AND [Id] = scope_identity();`. Both shapes need to flow through the simulator's trigger dispatch and return the right identity to EF's per-entity tracker.

The `HasTrigger` shape relies on `SCOPE_IDENTITY()` returning the **outer** INSERT's identity, not the trigger body's last identity write. Real SQL Server scopes SCOPE_IDENTITY per stored-context-scope: a trigger body's INSERT doesn't leak its identity to the caller's SCOPE_IDENTITY (probe-confirmed). The simulator collapses SCOPE_IDENTITY and @@IDENTITY into one connection-level slot, so the trigger dispatcher saves the outer value before firing triggers and restores it after, preserving the EF-visible scope. (Minor consequence: @@IDENTITY also reverts post-trigger, which is technically wrong — real SQL Server's @@IDENTITY is session-wide and would reflect the trigger's last identity. Apps that read @@IDENTITY immediately after a trigger-firing DML to see the trigger's identity won't get the right value; the rarity of that pattern + EF's reliance on SCOPE_IDENTITY justifies the trade.)

The `EFCoreTriggers` fixture locks down compatibility with `HasTrigger` across EF Core upgrades — if a future EF version changes the trigger-safe emit shape to something the simulator doesn't support yet, the fixture catches it.
