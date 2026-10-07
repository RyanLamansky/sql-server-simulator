# Transactions

Three entry points share one per-connection undo log: implicit (statement atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).
A fourth arrives over the network: TDS Transaction Manager requests map onto the SqlClient-API path — see [`tds-endpoint.md`](tds-endpoint.md).

- **Statement-level atomicity**: a mutation throwing mid-execution rolls back its partial writes.
  Multi-row INSERT failing on row 3 leaves zero rows.
- **Cancel (TDS attention / `CommandTimeout` / in-process `Cancel()`) vs. an open tx**: probed against SQL Server 2025 — under the default `SET XACT_ABORT OFF` the transaction **survives** the cancel intact and usable; under `SET XACT_ABORT ON` the cancel **rolls it back** (`@@TRANCOUNT` → 0).
  Both front doors apply it as the cancelled batch unwinds (`SimulatedDbConnection.SettleCancelledExecution`, gated by `XactAbort`).
  Already-committed statements' effects persist and un-run statements never fire, while a statement the cancel lands inside rolls back like one a mid-statement *error* ends — the row loops poll for it, so it doesn't run to completion first (see [`control-flow.md`](control-flow.md#waitfor-delay)).
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` commits; `ROLLBACK` zeroes `TranCount` and walks the whole log.
  `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx.
  Savepoints form a stack, as real's do (probed 2026-09-28 against SQL Server 2025): saving a name again stacks a second savepoint rather than moving the first, and `ROLLBACK TRAN <name>` returns to the newest of that name and consumes it with every later one, so repeating it reaches the older one and then Msg 6401.
  A savepoint of the transaction's own name wins over it; `ROLLBACK TRAN <name>` naming the *outermost* `BEGIN TRAN`'s name otherwise rolls the whole transaction back; the name is matched case-sensitively (real refuses `outer1` for `Outer1` under a case-insensitive collation), while a savepoint name matches case-insensitively, and a nested `BEGIN TRAN`'s name is never recorded, so naming it is Msg 6401 (probed 2026-09-24).
  A doomed transaction refuses a rollback to a savepoint with Msg 3931, which ends the batch and rolls back as Msg 3930 does.
  A rollback to a savepoint also discards the version-store entries written after it.
  A name held in a variable is cut to 32 characters; a written one past 32 is Msg 103 while compiling, on every statement that takes one.
  `BEGIN`, `SAVE`, `COMMIT` and `ROLLBACK TRAN` all take a variable, which must be a string (Msg 3914 otherwise), and `COMMIT … WITH (DELAYED_DURABILITY = ON | OFF)` is accepted and discarded (probed 2026-10-02).
  How `BeginTransaction` meets a transaction SQL text opened is [its own section](#begintransaction-and-sql-text-transactions).
  `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both advance through rollback.
  (A rolled-back INSERT's off-row LOB chain + heap bytes are reclaimed — rollback is terminal, so an uncommitted insert is invisible to every snapshot.)
- **DDL participates in the log** (probed 2026-09-25: real undoes a rolled-back `CREATE TABLE`, view, procedure, `ALTER TABLE … ADD`, `CREATE INDEX`, and restores a rolled-back `DROP TABLE` with its rows).
  Temp tables ride their own `TempTableCreation` / `TempTableRemoval` entries; a permanent object's create, alter, drop or rename records a `SchemaChange` entry through `Simulation.RecordDdlUndo`, whose closure puts the catalog slot, foreign-key wiring and cascaded triggers back and then bumps `SchemaVersion` so no cached plan keeps the rolled-back shape.
  The `ALTER TABLE` family, index DDL and column / index / constraint renames capture the table whole first (`HeapTableSnapshot`): those statements always swap in a fresh column array and `Heap` rather than rewriting in place, so keeping the old references restores the rows, and the flags and lists they do mutate are copied value by value.
  Schemas, types, `ALTER SCHEMA … TRANSFER` and extended properties log their own slot changes; the security statements — users, roles, logins, memberships, `GRANT` / `REVOKE` / `DENY` at either scope — snapshot the database's (or server's) principals, memberships and permissions before changing them (`RecordSecurityUndo` / `RecordServerSecurityUndo`), real rolling all of them back (probed 2026-09-25).
  Not logged yet: an indexed view's own index, full-text catalogs and indexes, assemblies, and `ALTER DATABASE`.
- Locking + MVCC: full 8-mode matrix, row-X writers + row-mode readers per hints/iso, RR/SER/UPDLOCK/XLOCK/TABLOCK/HOLDLOCK/REPEATABLEREAD/NOLOCK/READPAST hints, per-statement escalation at real's threshold, Msg 1205 deadlock / Msg 1222 timeout, SNAPSHOT + RCSI (version chains + GC + DMVs).
  See [`locking.md`](locking.md).
- **Sessions can share one transaction** — bound through `sp_bindsession`, or a loopback linked server's call enlisted in its caller's — each nesting on a `@@TRANCOUNT` of its own, any one's outermost `COMMIT` or `ROLLBACK` ending it for all, the others hearing Msg 3926 at their next batch; see [`locking.md`](locking.md#sessions-sharing-a-transaction).
- Table-variable mutations use a statement-only undo log disjoint from the tx-scoped one, so `ROLLBACK TRAN` skips `@t` (the `CurrentTableVarUndoLog` / `CurrentUndoLog` split on `BatchContext`).
- **One transaction spans every database it wrote to.**
  The undo log is per-connection and its entries reference their `Heap` directly, so a write through a three-part name rolls back with the rest of the transaction with no extra routing; `@@TRANCOUNT` / `XACT_STATE()` never reflect the crossing (probe-confirmed).
  What *is* per-database — the rowversion counter, the version store's commit-Xid counter, trigger dispatch — follows the target table rather than the session; see the cross-database-writes section of [`schemas.md`](schemas.md#cross-database-writes).

## The statement's own transaction

With no user transaction, real opens one for any statement that touches data, and `XACT_STATE()` read inside the statement reports it as 1 while `@@TRANCOUNT` stays 0 (probed 2026-09-28 against SQL Server 2025).
A statement touches data when it writes rows, or when its compile meets any of these — the parse sites mark `StatementContext.OpensTransaction`, which the statement's `XACT_STATE()` calls share through one mark, so the read costs nothing per row:
- a FROM source other than a derived table or `VALUES` — a table of any kind (permanent, `#temp`, a table variable), a view, a CTE, a catalog view or DMV, a TVF, a built-in rowset function;
- a function or sequence object: a scalar UDF call, `NEXT VALUE FOR`;
- a CLR type: a method or property of `xml`, `hierarchyid`, `geography`, `geometry` or a user type, a `type::` static member, a `CAST` / `CONVERT` to one, a variable declared as one;
- a built-in of the metadata, security or session families (`OBJECT_ID`, `DB_NAME`, `USER_NAME`, `SESSION_CONTEXT`, `CURRENT_USER`, …), every `@@` function but `@@ROWCOUNT`, `@@ERROR`, `@@TRANCOUNT`, `@@FETCH_STATUS` and `@@CURSOR_ROWS`, and a scattering real classes with them — `CONCAT`, `CHOOSE`, `DATENAME`, `GREATEST`, the `…FROMPARTS` constructors — where their neighbours `IIF`, `DATEPART`, `GETDATE` and `LEN` open none.

The rule is per statement: `IF EXISTS (SELECT * FROM t) SELECT XACT_STATE()` reads 0 in its body, an `IF` or `WHILE` condition reads its own, and a cursor's rows read the statement its query was declared in.
A function body always reads 1, since its caller named the function; a procedure body's statements read their own.

**Not modeled yet**: a built-in outside the probed families reads 0.

## `SET IMPLICIT_TRANSACTIONS`

With the option on and no transaction open, a statement that reads or writes an object opens a transaction that stays open after it — `@@TRANCOUNT` reads 1 — until a `COMMIT` or `ROLLBACK` ends it, and the next such statement opens another (probed 2026-09-28 against SQL Server 2025).
The set that opens one is narrower than the one `XACT_STATE()` reads above: `BatchContext.BeginImplicitTransaction` is called by the sites that meet an object, not by the built-ins.
- A FROM source naming a table, view, `#temp` table, table variable, catalog view, DMV or table-valued function; a CTE or derived table only through a source of its own, and a built-in rowset function (`OPENJSON`, `STRING_SPLIT`, `GENERATE_SERIES`) or `VALUES` not at all.
- A write, a table variable's included; object DDL — `CREATE`, `ALTER`, `DROP` (a missing object's too), `TRUNCATE`, `UPDATE STATISTICS` — and `GRANT` / `DENY` / `REVOKE`, the database-level forms excepted.
- A cursor's `DECLARE` (whatever its query reads), `OPEN` and `FETCH`; `NEXT VALUE FOR` anywhere.
- A user function called by a query — a `SELECT` returning rows, or a subquery — where one called from a `SET`, a `DECLARE` initializer, a `PRINT` or a `SELECT` assigning variables with no FROM opens none.
- A system procedure written in T-SQL over the catalog — the `sp_help` family, the ODBC catalog set, `sp_rename`, `sp_who`, `sp_configure`, the extended-property and bind procedures — even when it then fails, as `sp_help` of a missing object does.
  The ones that run no catalog query of their own open none: `sp_describe_first_result_set` and `sp_describe_undeclared_parameters` (which run nothing, where a `SET FMTONLY ON` query does open one), the application-lock, session-context, XML-document, role-member, application-role and linked-server procedures, and the `xp_` ones; `sp_executesql` leaves it to the statements it runs, and a call missing a parameter (Msg 201) never starts (`Simulation.OpensImplicitTransaction`).
- `BEGIN TRANSACTION`, which opens the implicit transaction first and then nests in it: `@@TRANCOUNT` reads 2, and it takes two `COMMIT`s to end.

An `IF` or `WHILE` condition opens none, whatever it reads (`BatchContext.ConditionDepth`), nor does an `EXEC` of a procedure or dynamic SQL by itself — the statements inside do their own opening.
Opening happens before the statement runs, so the statement's own `@@TRANCOUNT` counts it: `INSERT … SELECT @@TRANCOUNT` stores 2, the write's own transaction on top.
A statement that fails keeps the transaction it opened when its error ends only the statement (Msg 2627, Msg 515, Msg 8134, a missing object's `DROP`), and loses it to the rollback when its error rolls back (the string-conversion family, anything under `XACT_ABORT`); a compile error of a statement the batch deferred — a missing table's Msg 208 — takes back the transaction the statement opened, real opening it only once the statement compiled.

The option scopes like `XACT_ABORT` — a body's `SET` applies inside it and reverts on return — and a procedure or dynamic batch that ends with the option on raises no Msg 266 for the transaction count it changed, whoever set it, while one that turned it off before returning does (probed 2026-09-28).
`@@OPTIONS & 2` reports it; `SESSIONPROPERTY` doesn't.
`SET ANSI_DEFAULTS ON` turns it on — see [`session-options.md`](session-options.md).
While it is on, neither the plan cache nor the compiled-batch cache is consulted, since a replayed plan would skip the parse that opens the transaction.

`BeginTransaction` nests inside the transaction the option opened, as it does in one `BEGIN TRANSACTION` opened — see the next section.

## `BeginTransaction` and SQL-text transactions

The in-process surface follows what SqlClient 7 does against SQL Server 2025 (probed 2026-09-28), which over the wire is SqlClient's own client-side bookkeeping on top of the server's transaction-manager requests:
- **Nesting.**
  Over a transaction SQL text opened — `BEGIN TRANSACTION` or `IMPLICIT_TRANSACTIONS` — `BeginTransaction` nests one level (`SimulatedDbTransaction.Nest`): `@@TRANCOUNT` rises, the API commit ends only that level, and the API rollback or a dispose rolls the whole transaction back.
  Over one it began itself, or while a nested one is still pending, it is SqlClient's parallel-transactions `InvalidOperationException`.
- **Pending.**
  While a transaction `BeginTransaction` began or nested is pending, a command without it is refused with SqlClient's "requires the command to have a transaction" `InvalidOperationException` (`HoldsApiTransaction`).
  An API commit that ends only an inner level SQL text nested inside the API's own transaction leaves it pending, so the connection refuses both until SQL text ends it; one that ends the level it nested frees the connection.
- **Completion.**
  Once committed or rolled back through the API, or ended by SQL text or the engine, the object's `Connection` reads null and a further `Commit`, `Save` or `Rollback(name)` is SqlClient's "This SqlTransaction has completed" `InvalidOperationException`.
  `Rollback()` is the exception, SqlClient's partial zombie: the first one after SQL text or the engine ended the transaction — a `ROLLBACK` or `COMMIT` statement, an `XACT_ABORT` error, a doomed transaction's Msg 3998, a trigger's or procedure's `ROLLBACK` — succeeds silently, whether or not a command ran in between, unless a `Commit` / `Save` / `Rollback(name)` came first (probed 2026-09-28 through SqlClient 7).
- **Savepoints.**
  `Save(name)` and `Rollback(name)` are SqlClient's transaction-manager save and named rollback, sharing the SQL-text savepoint stack: a null or empty name is SqlClient's own `ArgumentException`, a name past 32 characters Msg 103 at state 30 (state 2 from SQL text), an unknown one Msg 6401 with the transaction kept, and a name no savepoint carries but the transaction a `BEGIN TRANSACTION` named rolls it all back.
- **Isolation.**
  An unspecified level begins at read committed, and the level a `BeginTransaction` requests — nested or not — becomes the session's and stays after the transaction ends, which is real's own behavior for the transaction-manager begin.

Over the wire the server half is the TDS endpoint's (see [`tds-endpoint.md`](tds-endpoint.md#transaction-manager-requests)) and SqlClient supplies the rest itself.

## Database-level DDL inside a user transaction

`CREATE` / `ALTER` / `DROP DATABASE` and `ALTER DATABASE SCOPED CONFIGURATION` are refused inside a user transaction (Msg 226, or Msg 574 for the drop) before any of the statement runs; the error ends only its statement, a TRY catches it, and the transaction stays open and committable — save the scoped-configuration refusal, which acts as under `XACT_ABORT`: uncaught it ends the batch and rolls the transaction back, caught it dooms it (probed 2026-09-27 against SQL Server 2025).
The dispatch loop parses the refused statement in skip mode first, so the recovery scan resumes past it rather than at a keyword inside it.

## The transaction-aborting error class

Almost every error is statement-aborting: it ends its statement, leaves `@@TRANCOUNT` where it was, and a `BEGIN TRY` frame catches it.
A small class is different — it rolls the session's whole transaction stack back before anyone sees it, refuses to be caught, and takes the rest of the batch with it.
`SimulatedSqlException.AbortsTransaction` marks a factory as belonging to it; the statement dispatcher rolls the session's transaction back at the same point it already does for a deadlock victim (class 13), and skips the TRY-frame arm.

**Msg 8728** (a RANGE-framed window ordering by a MAX-typed expression — see [`query.md`](query.md#range-frame-order-by-msg-8728)) is the modeled member.
Probed against SQL Server 2025 (2026-08-05), with a transaction opened in an earlier batch:

- `@@TRANCOUNT` 1 → 0 and `XACT_STATE()` 1 → 0, and a row inserted inside the transaction is gone afterwards.
- `@@TRANCOUNT` 2 → **0**, not 1 — the whole stack, not one level.
- A surrounding `BEGIN TRY` never reaches its `CATCH`, and a `PRINT` after the failing statement in the same batch never runs.
- The neighbours all leave the transaction standing at 1: Msg 8134 (divide by zero), 208 (invalid object), 207 (invalid column), 306 (legacy LOB sorted), 4104 (multi-part identifier), 8120 (not in GROUP BY), 4194 (RANGE numeric offset).

Real settles Msg 8728 while *compiling*, so on real it also fires inside a branch the batch never takes (`IF 1 = 0 BEGIN <the query> END` raises and the batch never starts).
The simulator raises it while parsing the statement, which reaches the same place for the shapes that matter and additionally fires under the dispatch loop's skip mode.

## `BEGIN DISTRIBUTED TRANSACTION`

`BEGIN DISTRIBUTED { TRAN | TRANSACTION } [name | @var]` asks the coordinator for a transaction remote resources could enlist in.
The statement opens the ordinary local transaction, which is exactly what real does until something actually enlists — and when a linked server does, the coordinator refuses out of the box (Msg 7391, or 3910 for a loopback; see [`linked-servers.md`](linked-servers.md#transactions)).
What the keyword changes is which work enlists: under it a linked server's read does, where under an ordinary transaction only a write or a remote call does.
Probe-confirmed against SQL Server 2025 (2026-08-08) that the two spellings match on `@@TRANCOUNT`, on nesting in either order, on `XACT_STATE()`, on `COMMIT` / `ROLLBACK`, and even on the `WITH MARK` diagnostics below; the parser shares `TryParseBeginTransaction` with the local form, consuming `DISTRIBUTED` and continuing.

## `WITH MARK`

`BEGIN TRAN <name> WITH MARK ['description']` labels a point in the transaction log for a point-in-time restore to name.
There is no log here, so the description is parsed and discarded — the name may be a variable, the description a literal or a variable, and both `WITH MARK 'm'` and the bare `WITH MARK` are accepted.
Two consequences *are* observable and both are modeled:

- The transaction has to be **named**: an unnamed `BEGIN TRANSACTION WITH MARK` is **Msg 3901** (`The transaction name must be specified when it is used with the mark option.`) raised at run time, so `@@TRANCOUNT` stays where it was rather than the transaction opening anyway.
- A second `WITH MARK` under a transaction that already carries one raises the severity-10 **Msg 3920** (`The WITH MARK option only applies to the first BEGIN TRAN WITH MARK statement. The option is ignored.`) through the `InfoMessage` surface, at class 0 on the wire.
  Real emits it only for the second *marked* BEGIN — a `WITH MARK` nested under an unmarked transaction is silent — so `SimulatedDbTransaction.IsMarked` is what the check reads.

Everything else about a marked transaction is an ordinary one: `@@TRANCOUNT` nests the same way, savepoints and `ROLLBACK` behave the same, and nothing reads the mark back.

## `SET XACT_ABORT`

The option generalizes that plumbing conditionally, and the two shapes are **not** the same: Msg 8728 refuses a `BEGIN TRY` frame outright, while an XACT_ABORT-promoted error is caught normally and leaves the transaction *doomed* rather than rolled back.
`SimulatedDbConnection.XactAbort` holds the setting; `Simulation.ApplyXactAbortPromotion` applies it once, at the innermost dispatch frame, and marks the exception so an outer frame re-raising it doesn't ask twice.
Probed against SQL Server 2025 (2026-08-06).

**Uncaught**, the promotion covers the statement-terminating run-time family — Msg 245, 515, 547, 1222, 2601 / 2627, 2628, 8115, 8134, deferred-name 208, and an uncaught `THROW`:

| | `XACT_ABORT OFF` | `XACT_ABORT ON` |
| --- | --- | --- |
| the rest of the batch | runs (bar the batch-aborting name-resolution family) | never runs |
| `@@TRANCOUNT` | unchanged | 0, whatever the depth — 2 reads 0, not 1 |
| the transaction's writes | stand | rolled back |
| `XACT_STATE()` | 1 | 0 |

The batch ends even with no transaction open — the option is not conditional on one.
An error raised inside a procedure body ends the **calling** batch, not just the body.

**A statement that changes a table's or index's structure behaves this way whatever the option says** — ALTER TABLE, CREATE / ALTER INDEX, CREATE / UPDATE STATISTICS, DROP TABLE and TRUNCATE TABLE: their severity-16 run-time errors (and ALTER INDEX's severity-11 Msg 2727) end the batch and roll the transaction back, while the two ALTER TABLE raises compiling, Msg 4902 and 2705, end the batch alone, and a severity-11 miss such as DROP INDEX's Msg 3701 carries on (probed 2026-09-26 against SQL Server 2025; `StatementContext.ChangesTableStructure`).
A statistic's computed key failing to evaluate as CREATE / UPDATE STATISTICS builds it (Msg 8115) is the exception: it ends only its statement, the batch and the transaction going on (probed 2026-10-06).
The other DDL — roles, synonyms, schema transfers, `GRANT` — doesn't.

**`RAISERROR` is the exemption**, at every severity and with or without `WITH LOG`: uncaught under the option it reports, the batch runs on, and the transaction stays committable at `XACT_STATE()` 1.
So is any severity-11 error but a structural one — a `DROP` of a missing object's Msg 3701 — and a syntax error that ended a called dynamic batch (probed 2026-10-02 against SQL Server 2025).
`THROW` is promoted like everything else, which is the observable split between the two.
`SimulatedSqlException.RaisedByRaiserror` marks the factory.

**Caught by a `TRY` frame**, every error including `RAISERROR` behaves the same way instead: the `CATCH` runs, the batch carries on past `END CATCH`, `@@TRANCOUNT` is untouched — and the transaction is doomed, `XACT_STATE()` reading `-1` (`SimulatedDbTransaction.Doomed`).
Caught, the two exemptions above doom it too.
Whether an error rolls back or dooms is a question about the **whole session stack**, not one batch frame: a procedure with no `TRY` of its own, called from inside the caller's, dooms.
`SimulatedDbConnection.OpenTryFrames` is the session-wide counter that answers it, less the frames of the scope a compile error deferred to run time is raised in — they can't catch it, so it rolls back (probed 2026-10-02).
A caught Msg 266, a procedure or dynamic batch returning with its transaction count changed, dooms the transaction whatever the option says, where uncaught it leaves it committable.

A doomed transaction then:

- refuses any statement that writes to the log with **Msg 3930** class 16 state 1 (*"The current transaction cannot be committed and cannot support operations that write to the log file. Roll back the transaction."*) — DML, object DDL, `SAVE TRANSACTION` and `COMMIT` alike, plus the catalog writers whose leading token is neither (the `GRANT` / `REVOKE` / `DENY` family, `sp_rename`, the extended-property procedures), while a `SELECT`, a `DECLARE` and a `SET` complete normally.
  Msg 3930 is itself batch-aborting and rolls the transaction back, and a nested `TRY` can catch it.
  A `BEGIN TRANSACTION` is refused the same way, while a write to a table variable, which stands outside the transaction, goes through (probed 2026-10-02).
  The refusal precedes the statement's own name resolution — a `GRANT` on a missing object, an `sp_rename` of a missing one and an `sp_addextendedproperty` naming a missing table all report Msg 3930 rather than their own not-found error, which is the opposite of where the read-only gate sits for the latter two — and it precedes that gate as well: a doomed transaction writing to a read-only database reports Msg 3930, not Msg 3906 (probe-confirmed against SQL Server 2025, 2026-08-08).
- is rolled back at end of batch with **Msg 3998** class 16 state 1 (*"Uncommittable transaction is detected at the end of the batch. The transaction is rolled back."*), emitted after the batch's own results.
- is cleared only by `ROLLBACK`, after which the batch runs on normally.

**Scoping.** Unlike the six ANSI toggles (which a module body ignores), `SET XACT_ABORT` inside a procedure, trigger or dynamic-SQL body takes effect for that body and reverts when it returns; a body with no `SET` of its own inherits the caller's.
`SimulatedDbConnection.SessionOptionScope` captures and restores it — together with `ROWCOUNT` and `DATEFIRST`, which scope identically — at the three invocation seams and around a parameterized ad-hoc command.
`@@OPTIONS & 16384` reports the setting (`OptionsExpression`); a fresh session reads 5432 with the bit clear under SqlClient and 5176 under sqlcmd, whose difference is `QUOTED_IDENTIFIER` alone.

The option also decides whether a client attention rolls an open transaction back — see the cancel bullet above.

A few errors take this shape with the option **off** too, marked by `SimulatedSqlException.AbortsAsUnderXactAbort` — probed 2026-09-23: uncaught they end the batch and leave `@@TRANCOUNT` 0 with the transaction's writes undone, and caught they read `XACT_STATE() = -1`.
They are the string-conversion failures — Msg 245, 241, 295, 8169, 8170, 235, and Msg 8114 when a `CAST` of a string to a number raises it — and the XML parsing family (Msg 9400–9465 and Msg 6359, see [`xml.md`](xml.md#well-formedness)).
Probed 2026-09-24 and flagged alongside them: the `*FROMPARTS` builders' Msg 289, the JSON path and document errors (Msg 13607, 13608, 13609, 13621, 13623, 13624), and the run-time name collisions — Msg 2714 for a table, view, procedure, sequence, constraint or `SELECT … INTO` target, Msg 219 for a type, and Msg 1505 for a unique index over duplicate keys.
So does an identity value past its column's type (Msg 8115's IDENTITY wording, probed 2026-09-28), from a procedure or dynamic batch as well, the nesting limit's Msg 217, and a dynamic batch's `USE` of a missing database, Msg 911 (both probed 2026-10-02).
Their overflow neighbours (Msg 220, 232, 242, 248, and 8115 for an expression) and Msg 9807 end only their statement, as do Msg 8114 raised binding a procedure or `sp_executesql` argument and Msg 2714 for a synonym.

A trigger body runs under the option whatever the session says — see [`triggers.md`](triggers.md#errors-in-a-trigger-body).
