# Row-level security

`CREATE` / `ALTER` / `DROP SECURITY POLICY`, filter predicates that hide rows from every read and write, and block predicates that refuse the writes a row may not take.
Everything below was probed 2026-10-04 against SQL Server 2025 unless it says otherwise; the corpus is the local edge-probe harness's `gen_zzrls.py` (722 cases).

The pieces: `SecurityPolicy` (`Schemas/`) is the schema object, its alterable state one immutable `SecurityPolicyState` swapped whole; `Simulation.SecurityPolicy.cs` parses and binds the DDL and compiles a predicate; `RowSecurity` (`Parser/`) answers which predicates a table carries and applies them; `SecurityPredicateRunner` evaluates one predicate for one batch.

## DDL

A policy lives in the schema's object namespace (`sys.objects` type `SP`, Msg 2714 state 8 on a clash) and may hold no predicate at all.
`STATE` defaults to `ON` and `SCHEMABINDING` to `ON`; both are `CREATE` options, `ALTER` takes `WITH (STATE = …)` alone, and `NOT FOR REPLICATION` follows the `WITH`, never precedes it.
A predicate names an inline table-valued function and passes it one argument per parameter — columns of the target, constants, any scalar expression over them; a variable is Msg 112 and a subquery Msg 1046.
The target is a user table or, for a filter predicate, a schema-bound view; a block predicate on a view is Msg 33503, on a temporal history table Msg 33510.
A block predicate written without an operation guards all four, so it overlaps any other block predicate on its table (Msg 33262).
One table takes predicates from one enabled policy at a time (Msg 33264), and none from a table an indexed view reads (Msg 33265), whose index in turn is refused over a table any policy names (Msg 33266).

**Binding happens while the batch compiles.**
Every refusal about the policy's own shape — a missing or wrong-kind function (33270) or target (33268, 33263), a one-part name under schema binding (4512), a function or view that isn't schema bound (4513), a column (207), the argument count (8144 / 313), an overlap (33262), an `ALTER` naming a predicate the policy doesn't hold (33261) — stops the batch before any statement in it runs, an `IF` branch never taken included, and a policy over a table the same batch creates fails the same way.
The bind runs again as the statement runs, against what the batch has changed since.
The run-time refusals are the permissions, the name's uniqueness and the one-enabled-policy rule; Msg 33264 rolls an open transaction back as `XACT_ABORT` would.

An `ALTER`'s clauses apply in written order against the predicates the policy held when it began: `ALTER` and `DROP` name kind, table and operation exactly, and an `ADD` clashing with a predicate the policy still holds is Msg 33262 state 2, one the statement added or dropped state 1.
Predicate ids only grow: one added after a drop takes the next id past every id the policy used.

Permissions: `CREATE` takes `ALTER ANY SECURITY POLICY` (Msg 262 then 15247 state 7), `ALTER` on the schema (Msg 2760) and `SELECT` on each predicate's function (Msg 229 states 5 and 9); `ALTER` and `DROP` take `ALTER ANY SECURITY POLICY` and `ALTER` on the policy, and refuse as Msg 33268 state 7 (batch-ending) and Msg 3701 class 14 state 20.
`db_ddladmin` holds no `ALTER ANY SECURITY POLICY`.

A schema-bound policy pins its function, its tables and the columns its arguments read: `DROP FUNCTION` / `DROP TABLE` is Msg 3729, `ALTER COLUMN` / `DROP COLUMN` Msg 5074 + 4922, `sp_rename` Msg 15336, `ALTER SCHEMA … TRANSFER` of the table Msg 15348 — the `SchemaBinding` gate, which counts policies among its modules.
A policy that isn't schema bound still pins its table against `DROP TABLE` (Msg 3729 state 3) and follows a transfer; its function may be altered or dropped, which the next read meets (below).
`DROP SCHEMA` over a policy is Msg 3729, which ends the batch.

## Catalog

`sys.security_policies` (`uses_database_collation` follows `is_schema_bound`) and `sys.security_predicates`, both visible to a principal who may see the policy's definition; `predicate_definition` is the call in a CHECK constraint's canonical form, names spelled as written — `([dbo].[fp]([owner]+''))`, `([dbo].[fp](CONVERT([sysname],[a])))`.
`OBJECTPROPERTY` answers a policy as an object of no other kind (`BaseType` `SP`); `OBJECT_DEFINITION` is NULL and `sys.sql_modules` has no row.
A schema-bound policy reports one dependency entity per predicate, the predicate's id as `referencing_minor_id`, naming the target, the columns its arguments read and the function; one that isn't schema bound reports none.
`sp_depends` on the policy lists them; on its function it sends the "referenced by" header over an empty set, and `sp_help` on the policy an empty first result set — real joins both to a type label a policy lacks.

## Filter predicates

A filter predicate hides every row its function returns no row for, from **every principal, `dbo` and `sysadmin` included**: the function decides, and a policy meant to exempt the owner says so in its body (`or is_member('db_owner') = 1`).
The function is semi-joined, so a body returning two rows admits a row once.

Where it applies — each place a statement reads the table's rows for itself:
- the scan a query source enumerates (`LockCheckedScanRows`, `UnlockedScanRows`) and every seek and ordered scan the access-path choice substitutes (`SeekedSource`), so joins, subqueries, CTEs, views, inline and multi-statement functions, procedures, cursors and dynamic SQL all read the filtered rows;
- an `UPDATE` / `DELETE` target, single-table or joined, and a `MERGE` target, whose hidden rows are neither matched nor `NOT MATCHED BY SOURCE` — so a `MERGE` inserting a hidden row's key is Msg 2627, as an `INSERT` of one is;
- a `MERGE`'s bare-table source, a partitioned view's members, a `FOR SYSTEM_TIME` read's current rows (the history rows answer only to a policy on the history table), `CONTAINSTABLE`'s matches, and a schema-bound view's rows under a filter on the view itself.

What it doesn't reach: `TRUNCATE TABLE`, `CHANGETABLE` (every changed key shows), a foreign key's existence check, `sys.partitions` row counts and `sp_spaceused`.

The predicate runs **ahead of anything the statement evaluates over the row**, as real's plan applies it at the scan: `WHERE id = 4 AND 1 / a = 1` raises nothing for a principal the row is hidden from, where a visible row's divide by zero does.
The function's body reads its own tables — the predicate's target included — without row-level security.

A policy with `SCHEMABINDING = OFF` binds its predicates afresh at each statement: the reading principal needs `SELECT` on the function (Msg 229 state 5, checked again for each principal a batch runs as), and a dropped function, a changed parameter list, a dropped column or a body that no longer binds is that binding error followed by Msg 33512, ending the batch.
A query reading the table meets it as the batch compiles (`RowSecurity.BindAtCompile`), so nothing in the batch runs — one never taken by an `IF` included — unless the batch also runs DDL; a write binds every predicate on its target as it runs, a filter-only policy's included (probed 2026-10-06 against SQL Server 2025).
An `INSTEAD OF UPDATE` trigger spares the statement it replaces its block predicates, NOT NULL and CHECK constraints alike: only the body's own write is judged.

**A statement that applies a predicate redacts its conversion and truncation errors** — Msg 245, 220, 232, 248 and 2628 read `******` for every value, type and name, whichever row raised them.
"Applies" is a read of a table carrying a filter predicate, or a write to a table carrying any enabled predicate: a `SELECT` over a block-only table keeps its error text.
The session counts each application (`SimulatedDbConnection.RowSecurityMarks`), and the statement lifecycle compares the count with the one it began at, so a nested body's application counts for the statement that ran it.

## Block predicates

| Operation | Judges | Reached by |
|---|---|---|
| `AFTER INSERT` | the new row | `INSERT`, `MERGE … INSERT`, `BULK INSERT` and the TDS bulk load |
| `AFTER UPDATE` | the new row, whether or not a written column changed | `UPDATE`, `MERGE … UPDATE` |
| `BEFORE UPDATE` | the row as it stood | `UPDATE`, `MERGE … UPDATE` |
| `BEFORE DELETE` | the row as it stood | `DELETE`, `MERGE … DELETE` |

A refused row is Msg 33504 naming the base table as `database.schema.table` — through a view or a procedure alike — followed by Msg 3621; the statement rolls back and the batch goes on.
A block predicate applies to `dbo` too, and to a write an `INSTEAD OF INSERT` trigger's body makes rather than the statement it replaced.
A foreign key's cascade passes every block predicate.

## Plan cache and cost

Nothing about row-level security is decided while a statement parses: a plan holds no predicate, and each execution asks `RowSecurity.For` what the table carries as of the current `SchemaVersion`.
That is what keeps cached plans principal-independent ([`plan-cache.md`](plan-cache.md#principal-independence)) — the predicate's `USER_NAME()`, `IS_MEMBER()` and `SESSION_CONTEXT` read the executing session — and the Debug principal-read watch holds it so.
Policy `CREATE` / `ALTER` / `DROP` still bump `SchemaVersion` through the DDL dispatch arm, which also invalidates the catalog-row cache and the per-table memo (`SchemaObject.RowSecurity`).

A runner compiles its predicate's function body once per batch (`BatchContext.RowSecurityRunners`) and per row sets the parameter slots and asks the body for a row.
A simulation that never created a policy pays one field read (`Simulation.DeclaresSecurityPolicies`) per table scan and per written row: a 10,000-row scan, an EF Core keyed `SELECT` and a keyed `UPDATE` measured within noise of the commit before (about 950 µs, 3 µs and 6.3 µs per execution, measured 2026-10-04), with a policy on another table or without.
An access path that would count rows past the predicate — the ordered scan's `OFFSET` skip — declines for a filtered table.

## Locking

A filter predicate is part of the scan, so a REPEATABLE READ or `UPDLOCK` read lets go again of the row lock it took on a row the predicate hides (`RowSecurity.FilterLockedRows`): the transaction keeps the rows it returned.
A predicate function's own reads lock as any read at the statement's isolation does.

## Divergences

- The lock DMVs show no `PAGE` or `METADATA` rows, the simulator's lock model taking neither, and a SERIALIZABLE predicate's equality lookup into a keyed table takes the range locks a scan would where real takes one key `S`.
- A write meeting a non-schema-bound predicate that no longer binds reports the body's Msg 4413 at line 12, where real's is at the writing statement's line (probed 2026-10-06 against SQL Server 2025).
- `CONTAINSTABLE` ranks follow the simulator's ranking, not real's (see [`full-text.md`](full-text.md#not-modeled-yet)).

## Not modeled yet

- Block predicates on a memory-optimized table's natively compiled writes, and the replication behavior `NOT FOR REPLICATION` names.
- A filter predicate over a graph table (probed 2026-10-06 against SQL Server 2025): real refuses a `SHORTEST_PATH` walk over a filtered node or edge table with Msg 4104 state 3 naming the `FOR PATH` node alias a graph path aggregate reads, and a plain `MATCH` whose node row the filter hides keeps the edge, its node's columns NULL — the walk here reads the tables unfiltered, and a hidden node drops the match.
