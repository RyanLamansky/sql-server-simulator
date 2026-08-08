# Variables and control flow

## Variables: `DECLARE` / `SET` / `SELECT @v = expr`
Per-batch scalar variables.
`DECLARE @v TYPE [= expr] [, @w TYPE [= expr] ...]` registers slots; `SET @v = expr` and `SELECT @v = expr [, @w = expr2 ...]` mutate them.
SqlClient parameters seed the same store as if pre-DECLAREd, so a parameter and a DECLARE can't share a name (Msg 134).

Variable references resolve at runtime via a captured `VariableSlot` — required because mutations between statements have to be visible to subsequent reads.
Assignment coercion routes through `Cast.ApplyCoercion` so the slot's declared type is honored: `SET @v(varchar(3)) = 'hello'` truncates to `'hel'`; `SET @v(int) = 'abc'` raises Msg 245.

**SELECT-assign quirks**:
- All-or-nothing: `SELECT @v = 1, 2` (mixing assign and projection) → Msg 141.
- Empty result-set keeps prior value (no rows iterate, slot unchanged); `SET @v = (SELECT no rows)` differs — assigns NULL.
- Multi-row last-row-wins post-ORDER-BY (per-row evaluation, last write wins).
- The dispatch drains rows for side-effects and yields a `SimulatedNonQuery` rather than a result set (matches SQL Server's no-result-set-envelope behavior for SELECT-assign).

**Re-execution is not re-declaration.**
A loop body re-dispatches its statements, so the same `DECLARE` runs once per pass — which is legal, because T-SQL hoists the declaration to the batch and leaves only the assignment behind.
The two are told apart by *where the declaration was written*: `BatchContext.VariableDeclarationSites` records each variable against the batch-text offset of the `DECLARE` that introduced it, so a re-execution reports the same offset and a second textual `DECLARE` a different one.
Offset alone can't settle `DECLARE @a int, @a int` — one statement naming the same variable twice, which real still refuses — so the parse also tracks the names the current statement has already introduced.
On a re-execution the slot is kept and only the initializer runs again, which is exactly what real does (all probe-confirmed against SQL Server 2025):

| shape | result | why |
| --- | --- | --- |
| `DECLARE @q int = 5` in a 3-pass loop, `@q + 1` summed | **18** | the initializer re-runs every pass (6+6+6), rather than running once and carrying (6+7+8) |
| `DECLARE @q int` in a 3-pass loop, incremented | **6** | no initializer means no reset — the value carries (1+2+3) |
| `DECLARE @t TABLE` in a 3-pass loop, one insert each | **3 rows** | a re-executed declaration does not start an empty table |

The initializer is execution-scoped, so it must not run under `IsSkipping` — the pass that *ends* a `WHILE` still walks the body to get past it, and assigning there would overwrite the last real iteration's value with NULL.

**Errors**: Msg 137 use-before-declare (existing factory); Msg 134 duplicate DECLARE (also fires for parameter+DECLARE collision, and stays an error however unreachable the second declaration is — the check is compile-scoped over the batch text); Msg 141 mixed assignment + retrieval; standard CAST errors propagate from coercion (Msg 245, Msg 8115, etc.).
`DECLARE @v INT NOT NULL` and `DECLARE @v INT = DEFAULT` raise Msg 102 / 156 respectively (DECLARE doesn't accept column-style constraints — falls out of grammar mismatch).
The optional `AS` before the type (`DECLARE @v AS INT`) is accepted, and an initializer-less DECLARE may end the batch (`DECLARE @x int`, `DECLARE @x varchar(20)`) — the type-spec parse tolerates end-of-input rather than requiring a following token.

**Output-parameter write-back**: at end of batch, the dispatch walks the parameter list and copies each `InputOutput` / `Output` direction parameter's final slot value back to `DbParameter.Value`.
Mirrors SqlClient's round-trip behavior for hand-rolled scripts that mutate parameters.

**`@@ROWCOUNT`**: tracks the most-recently-completed statement's row count via `SimulatedDbConnection.LastStatementRowCount`.
SELECT row counts populate after the dispatch materializes rows up-front (so the next statement in the batch sees the final count); DML mutations write their affected count; `SET` / `DECLARE @v = init` write 1; bare `DECLARE @v` (no initializer) preserves the prior count; transaction / DDL statements reset to 0.

**`@@ERROR`**: error number of the most-recently-completed statement; `int`.
Backed by `SimulatedDbConnection.LastErrorNumber`, which the per-statement TRY/CATCH dispatch wrapper sets to the caught error's number on failure and resets to 0 on successful completion.
See the TRY/CATCH section for the live tracking details.
Outside any TRY/CATCH the value stays 0 except after a `RAISERROR(..., sev ≤ 10, ...) WITH SETERROR` (which forces 50000 — the only path that surfaces a non-zero `@@ERROR` to the batch without entering CATCH; `StatementContext.SuppressErrorReset` skips the wrapper's reset for that one statement).
Uncaught errors terminate the batch, so no path reads @@ERROR after a failure there.

**`@@TRANCOUNT`** / **`XACT_STATE()`**: transaction-state surface.
`@@TRANCOUNT` reads `SimulatedDbConnection.CurrentTransaction?.TranCount` as `int` (0 when no transaction is active).
`XACT_STATE()` (`Parser/Expressions/TransactionScalarFunctions.cs`) returns a tristate `smallint`: `0` = no active transaction, `1` = active and committable, `-1` = doomed (active but uncommittable).
The simulator doesn't model the doomed state, so values collapse to `0` / `1`.
`@@TRANCOUNT > 0` and `XACT_STATE() = 1` are equivalent observables under the modeled scope.

**Compound assignment** (`SET @v += expr` / `-=` / `*=` / `/=` / `%=` / `&=` / `|=` / `^=`) is a parse-time desugar: `@v += rhs` is rewritten as `FromCompoundOp('+', VariableReference(@v), rhs)` and routed through the existing assignment path, so the arithmetic / string-concat dispatch handles three-valued logic (NULL on either side propagates), string `+=` concatenates `varchar` / `nvarchar`, decimal / money widening matches plain `+`, and divide-by-zero raises Msg 8134 from the decimal path (or surfaces a raw `DivideByZeroException` on the integer path — same simulator gap as plain `select 10/0`).
The two characters of a compound op must be adjacent in source — probe-confirmed: `SET @v + = 5` (with a space) raises Msg 102 in real SQL Server, so an `EndIndex == StartIndex` adjacency check in `TryConsumeAssignmentOperator` matches.
The same helper drives `UPDATE t SET col op= expr` (both bare and `t.col` qualified forms; mixed plain/compound across multiple SET-list entries).
**Table variables** (`DECLARE @t TABLE (...)`) ship — see [Table variables](table-variables.md) for the storage scope, grammar coverage, non-transactional semantics, and `OUTPUT … INTO @t`.

## T-SQL control flow: `IF` / `BEGIN…END` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN` / `GOTO`
`IF <boolean-expr> <stmt> [ELSE <stmt>]`, `BEGIN <stmt>+ END` compound blocks, `WHILE <boolean-expr> <stmt>` loops with `BREAK` / `CONTINUE`, bare `RETURN` for batch-level early-exit, and `GOTO <label>` (its own section below).
TRY/CATCH + THROW + ERROR_*() functions ship as a separate section below.
Value-form `RETURN N` ships inside scalar-UDF bodies (see [`programmable.md`](programmable.md)) and raises Msg 178 in batch / proc scope; a stored procedure's return value isn't modeled.
Probed against SQL Server 2025.

- **Body grammar**: exactly one statement.
  The famous T-SQL footgun `IF cond SELECT 'a' SELECT 'b'` runs *both* SELECTs — only the first is the IF body; the second escapes the IF as a subsequent batch-level statement.
  Replicated.
- **Dangling-else binds to the inner IF** (standard rule).
  `IF 1=0 IF 1=1 stmt ELSE stmt` → outer skips the entire inner-IF including its ELSE; no output.
- **Cond must be a Boolean predicate** (`BooleanExpression`).
  Bare values raise **Msg 4145** (`"An expression of non-boolean type specified in a context where a condition is expected, near 'X'"`): `IF 1`, `IF NULL`, `IF 'abc'`, `IF (cast(null as bit))` — bit is *not* boolean in SQL Server's static type check.
  Implemented by changing `BooleanExpression.ParseComparison`'s default (atom-without-comparison-op) case from Msg 102 to Msg 4145 (cross-cuts WHERE / HAVING / ON / CHECK too; probe-confirmed they share the wording).
  Slight positional gap on paren-wrapped value cases (`IF (1) select` — simulator reports "near ')'" where SQL Server reports "near 'select'") — wording correct, near-token off by one.
- **Three-valued cond**: only an explicit `true` takes THEN; both `false` and UNKNOWN go to ELSE (`IF 1 = null …` → ELSE).
- **`BEGIN` disambiguation**: peek the token after `BEGIN`.
  `TRAN`/`TRANSACTION` → existing `TryParseBeginTransaction`.
  `DISTRIBUTED` → the same `TryParseBeginTransaction` path, since nothing here is remote and real behaves identically until something enlists — see [`transactions.md`](transactions.md).
  `TRY` (unquoted) → `ParseTryCatch` (see the TRY/CATCH section).
  `ATOMIC` → a compound block whose options map onto the session (the natively-compiled-procedure boundary is modeled at parser fidelity).
  Everything else → compound block.
  Implemented via `ParserContext.SaveCheckpoint`/`RestoreCheckpoint` so the transaction-start case re-parses through the unchanged `TryParseBeginTransaction` path.
- **Empty `BEGIN END`** (and `BEGIN ; END` with only separators) → **Msg 102** near `'end'`.
  Variables declared inside a block are batch-scoped, not block-scoped (visible after `END`) — matches existing batch-scope model on `BatchContext.Variables`.
- **`@@ROWCOUNT`**: an IF that ran no branch (cond false, no ELSE) resets `@@ROWCOUNT` to 0.
  An IF whose body ran lets the body's last statement set `@@ROWCOUNT` normally.
  Probe-confirmed.

**WHILE specifics**: `BooleanExpression.Parse` for cond (Msg 4145 on non-boolean, same path as IF).
`ParserContext.SaveCheckpoint` captures the body-start; `RestoreCheckpoint` before each iteration so the body re-parses from scratch (variable references hold live `VariableSlot` references, so cond / body mutations between iterations are visible).
After every exit path (cond initially false, cond goes false mid-loop, BREAK) `@@ROWCOUNT` resets to 0 — probe-confirmed, independent of what the body's last statement produced.
Empty `BEGIN END` body raises Msg 102 (same rule as IF).
One-statement-body footgun: `WHILE @i<2 set @i=@i+1 select @i` — the `SELECT` is *not* part of the body; it runs once after the loop exits.

**BREAK / CONTINUE — flag-based, not exception-based.**
`BatchContext.LoopControl` enum (`None` / `Break` / `Continue`).
The BREAK parser sets it to `Break`; the CONTINUE parser sets it to `Continue`.
The innermost WHILE consumes and clears the flag.
The `IsSkipping` property OR's the flag into the skip predicate, so subsequent statements in the body block naturally no-op (`set @sum = @sum + 100;` after a `BREAK` doesn't run — probe-confirmed).
Nested loops work because each WHILE clears its own flag before returning to its caller, so the outer never sees the inner's break/continue.
This composes cleanly with iterator-based dispatch in a way exception-based signaling doesn't — see `feedback_no_exceptions_for_control_flow.md`.

**BREAK / CONTINUE outside a loop** raises **Msg 135** / **Msg 136** verbatim: `"Cannot use a BREAK statement outside the scope of a WHILE statement."` / `"Cannot use a CONTINUE statement outside the scope of a WHILE statement."`.
The check on `BatchContext.LoopDepth == 0` fires *unconditionally* — real SQL Server applies the loop-scope check at compile time, so the simulator does too.
**This is distinct from the un-taken-branch deferred-name-resolution behavior** described below: name resolution defers in skip mode, but BREAK's structural scope check does not — `IF 1=0 BREAK` at batch top level fires Msg 135 even though the branch is un-taken.
Inside a real WHILE, `LoopDepth > 0` lets BREAK in an un-taken IF body just no-op (because the `!IsSkipping` gate on the flag *write* prevents the actual control transfer).

**Iteration cap** — simulator-only safety net at `BatchContext.LoopIterationLimit = 100_000` total iterations per batch.
Real SQL Server has no such cap (timeouts handle runaway loops).
The simulator throws `InvalidOperationException` so a buggy test doesn't hang CI.

**`LoopDepth` is bumped unconditionally** (even when the WHILE itself is in skip mode) so BREAK / CONTINUE inside the body — including inside un-taken IF branches — never see Msg 135 / 136 fire incorrectly.
The flag-write gate (`!IsSkipping`) handles the runtime "BREAK in skipped-IF inside WHILE" case.

**`RETURN` — bare-form only, batch-exit propagation.**
Set `BatchContext.ReturnSignaled = true` (gated on `!IsSkipping`); `IsSkipping` OR's it in, and the dispatch loop's `DispatchStatementsUntil` checks the flag at the top of every iteration to `yield break`.
The WHILE iteration loop checks after every body dispatch (RETURN propagates *through* WHILE — only `BREAK` / `CONTINUE` are caught by the innermost loop).
`ParseBeginBlock` short-circuits its "expect END" check when the flag is set, since RETURN may fire mid-block before the cursor reaches END.
End result: bare RETURN exits the entire batch — through any nesting of IF / BEGIN…END / WHILE — and any code after it (including `SELECT 'after'` follow-ups or unreached `END` terminators) never executes.

**`RETURN <value>` raises Msg 178** verbatim (`"A RETURN statement with a return value cannot be used in this context."`) at parse time, regardless of skip mode — outside a scalar-UDF body (gated on `BatchContext.UdfFrame != null`; see [`programmable.md`](programmable.md)).
Stored-proc scope, where the value form would also be legal, isn't modeled.
Compile-time check (same pattern as BREAK Msg 135): `IF 1=0 RETURN 5` raises Msg 178 even though the branch is un-taken.
The simulator detects "value follows" via `IsStatementBoundary(context.Token)` — any non-boundary token after RETURN (operators, variables, literals, parens, non-statement-start keywords) triggers Msg 178; boundary tokens (`;`, EOB, statement-start keywords like SELECT/INSERT/IF/etc.) leave RETURN bare.

**Un-taken-branch skip mode** (`BatchContext.SkipModeFlag` + `IsSkipping` computed property).
The IF parser sets `SkipModeFlag` around dispatch of the un-taken branch (THEN if cond false, ELSE if cond true), then restores in a `finally`.
`IsSkipping = SkipModeFlag || LoopControl != None || ReturnSignaled` is the combined predicate every statement parser reads.
Skip-mode propagates through nested IF/BEGIN/WHILE automatically — a nested IF inside a skipped block reads `IsSkipping=true`, short-circuits cond eval entirely (so a divide-by-zero inside un-evaluated cond doesn't fire), and dispatches both its branches in skip mode.
A WHILE in skip mode never iterates; it skip-dispatches its body once to advance the cursor and exits.

Each statement parser still runs its full parse — the cursor advances normally, names resolve, expressions parse — but gates its state mutation on `!batch.IsSkipping`.
Touchpoints: SELECT `Execute` call in the dispatch, `ProcessHeapInsert`'s heap insert + `LastIdentity` update, `CommitUpdate`'s heap delete+insert, `CommitDelete`'s heap delete, MERGE's INSERT branch, `TryParseCreate`'s dict add + Msg 2714 existence check, `DropOneTable`'s lookup + Msg 3701 + dict remove, `ExecuteSelectInto`'s create + bulk-insert, `TryParseSetVariable`'s `slot.Value =` (plus its RHS evaluation), `TryParseSetIdentityInsert`'s state change, `TryParseDeclare`'s dict add + Msg 134 duplicate check + initializer evaluation, `TryParseBeginTransaction` / `TryParseCommit` / `TryParseRollbackTransaction` / `TryParseSavepoint`'s state changes (including their no-active-tx error checks), `TryParseAlter`'s database property write, `TryParseDbcc`'s trace-flag mutation.
The dispatch loop also suppresses `yield return` for SELECT results and the `LastStatementRowCount` update on skipped statements.

**Dispatch refactor**: extracted `DispatchOneStatement(batch, requireSemicolonBeforeCte)` and `DispatchStatementsUntil(batch, endKeyword)` from `CreateResultSetsForCommand`.
The top-level loop calls `DispatchStatementsUntil(null)`; `ParseBeginBlock` calls `DispatchStatementsUntil(Keyword.End)`; `ParseIfStatement` and `ParseWhileStatement` call `DispatchOneStatement` directly (the body of each is exactly one statement).
`IsStatementBoundary` includes `If` / `Else` / `End` / `While` / `Break` / `Continue` / `Return` so the cursor-normalization at the end of each dispatch correctly recognizes nested-control terminators.
`Selection.ParseInner`'s projection-list terminator switch lists the same set plus `Drop` so `SELECT ... ELSE` / `SELECT ... END` / `SELECT ... BREAK` / etc. correctly stop at the keyword instead of throwing Msg 102.

**Deferred name resolution in skipped branches** — the compile-vs-defer rule (probe-confirmed against SQL Server 2025).
Real SQL Server binds *base object* names lazily but binds *columns of a resolvable table* eagerly at compile time.
So deferral is scoped to a missing object, and the matrix is:

| shape in an un-taken branch | real SQL Server |
| --- | --- |
| missing **table** in FROM (incl. `EXISTS` / scalar subquery inside an `IF` cond) | **defers** — compiles, discarded, no error |
| missing **schema-qualified (2+part) function** call | **defers** (Msg 4121 only when *taken*) |
| missing **column on a resolvable table** | **Msg 207 at compile**, batch dead — even in the dead branch |
| bare **1-part unresolved function** | **Msg 195 at compile** (a missing built-in, not deferred) |
| ambiguous column (Msg 209) / unbindable multi-part (Msg 4104) | compile error, unless a missing table is also in the statement (then the whole statement's binding defers) |
| syntax / structural error (Msg 102 / 156) | compile error, always |

The simulator has no compile/run split — it resolves inline with parsing — so it reproduces this two ways:

- **Placeholder parse-continuation (the primary mechanism).**
  A skip-mode FROM-clause table miss (`Selection.cs`, the `TryResolveTable` fail) substitutes a **`FromSource.DeferredPlaceholder`** (`FromSource.IsPlaceholder`) instead of throwing Msg 208; a skip-mode schema-qualified function miss (`Expression.cs`, `ParseDeferredCallAndDiscard`) parses-and-discards the argument list and yields a placeholder `Value` instead of throwing Msg 4121.
  The statement then parses to completion and is discarded whole (skip mode gates its execution).
  This is what stops the **orphaned-fragment cascade**: without it, an `EXISTS (SELECT … FROM <missing>)` inside a skipped `IF` condition throws mid-parse and the recovery scan orphans the trailing THEN / `ELSE` / `END` into bare statements (spurious Msg 102 / 156, and over the wire an infinite error stream — an SSMS Query Store probe hits exactly this).
  With the placeholder the inner IF parses its full THEN+ELSE and the whole thing skip-completes.
  Column references across a source set that contains a placeholder bind leniently (`Selection.ResolveColumnTypeAcrossSources` returns a placeholder type; `FindSourceColumn`'s Msg 209 is suppressed) — matching "any missing object defers the whole statement's binding." Without a placeholder in scope, a missing column on a resolvable table still raises Msg 207.
- **Residual object-name swallow.**
  The remaining object-resolution sites that still resolve inline — DML target tables (INSERT / UPDATE / DELETE / MERGE), `NEXT VALUE FOR` sequences, XML schema collections — throw Msg 208 in skip mode, caught in `DispatchOneStatement`'s materialize-then-catch wrapper.
  `IsDeferrableNameResolutionError` is `{208}` (Msg 207 removed — it must reach the batch-aborting path per the matrix); the wrapper swallows the 208, advances the cursor to the next statement boundary, and drops the statement with **no** `@@ERROR` / `InFlightError` mutation.
  Runs *ahead* of the TRY-frame path so a skipped `BEGIN TRY` body's missing-name error doesn't activate its CATCH.
  Residual divergence: because this path uses the flat recovery scan rather than placeholder parse-continuation, the astronomically-rare shape of a deferred *DML / sequence / XML-collection* reference immediately followed by an orphan-prone `ELSE` / `END` can still mis-navigate — the common table / column / function shapes are fully covered by the placeholder path.
  It is also why a CREATE-time module bind stops at its first swallowed 208 rather than binding on from an unreliable cursor ([`programmable.md`](programmable.md#what-defers)).

**Skip mode parses; it must not execute.**
Several statement processors used to run per-row work in skip mode, which was invisible while the only skip-mode client was a dead branch over literal values — and became wrong the moment [CREATE-time module binding](programmable.md#create-time-body-binding) started binding every body in skip mode with its parameters standing in as typed NULLs.
Each of these is now gated on `IsSkipping`, and each is a fidelity gain for dead branches too (real runs neither):
`INSERT`'s per-row loop (DEFAULT evaluation, identity / rowversion allocation, computed columns, NOT NULL / CHECK / key enforcement), the evaluation of an `INSERT`'s `VALUES` tuples (a `NEXT VALUE FOR` cell burned a sequence value) and the execution of an `INSERT … SELECT`'s source query;
`UPDATE` / `DELETE`'s row source and `MERGE`'s whole match walk;
and `UserFunctionCall.Run` / `ClrFunctionCall.Run`, which return a typed NULL — load-bearing rather than an optimization, since the FROM-less-`SELECT` fast path bakes its projection values during the *parse* and would otherwise dispatch a scalar UDF's body from a statement that never ran.

A missing-column Msg 207 (or ambiguity / unbindable-identifier) surfacing from a skipped statement is **batch-aborting** (`IsBatchAbortingNameResolution`), so it stops the batch even from a dead branch, matching real.
`ParseBeginBlock` / `ParseBeginAtomicBlock` short-circuit their "expect END" check on `BatchContext.BatchAborted` (alongside `ReturnSignaled`) so a batch-abort mid-block surfaces the real error instead of a spurious Msg 102 near the abandoned token.

Variable declarations are the counterpart rule (probe-confirmed): `DECLARE` is compile-scoped batch-wide, so a DECLARE in an un-taken branch still registers its slot (scalar, `@t TABLE`, and table-type forms alike) — only the initializer is execution-scoped (skipped → the variable stays NULL).
Consequently duplicate names raise Msg 134 even when either declaration sits in a dead branch, and `SET @undeclared` raises Msg 137 even in a skipped branch — variable-name resolution never defers, unlike table/function names.
SSMS's server-properties batch relies on exactly this split (dead Managed-Instance branch declaring and assigning variables while referencing `master.sys.server_resource_stats`).
The shared column-list parser signals skip mode to its CREATE FUNCTION caller so a skipped `CREATE FUNCTION … RETURNS @r TABLE` still doesn't register the function (DDL stays execution-scoped).

Only name resolution defers — syntax / structural errors carry other numbers (Msg 102, etc.) and still propagate from skipped branches, matching real SQL Server.
BREAK / CONTINUE / RETURN / THROW scope checks (Msg 135 / 136 / 178 / 10704) also still fire in skip mode — those are compile-time *structural* checks, not name resolution, so they don't defer.

**Fidelity gap — `IF` cond divide-by-zero**: real SQL Server surfaces `IF 1/0 = 0 …` as Msg 8134; the simulator surfaces the raw `DivideByZeroException` from .NET decimal arithmetic (same gap as `TRY_CAST(1/0 AS INT)`).

**Fidelity gap — `IF (value-expr) …` positional**: `IF (1) select` raises Msg 4145 near `')'`; real SQL Server reports the post-paren token (`'select'`).
Wording is correct (Msg 4145, non-boolean type); only the "near 'X'" suffix differs.
Applies to any paren-wrapped non-boolean `IF` cond.

**Fidelity gap — CREATE/ALTER inside a control-flow body raises Msg 111, not Msg 156**: the must-be-first-statement check for `CREATE/ALTER PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA` is enforced at parse time.
Inside `IF` / `WHILE` / `BEGIN…END`, `BatchContext.BlockDepth > 0` triggers Msg 111; real SQL Server's parser surfaces Msg 156 ("Incorrect syntax near 'procedure'") at the same position.
Same end state (statement rejected), different code.
Inner CommandText-equivalent contexts (procedure / function / trigger / dynamic-SQL bodies) get a fresh `BatchContext` and the flag resets, so a CREATE PROCEDURE as the first statement of a proc body succeeds (real SQL Server raises Msg 156 here — related minor divergence; no real application emits nested CREATE PROCEDUREs).

### `GOTO` and labels

`GOTO <label>` jumps to a `label:` declaration elsewhere in the same batch or module body.
A label is an **unquoted** identifier followed by a single `:` — real refuses the delimited spelling (`[my label]:` is Msg 102) — and label names are matched under the database collation, so `GOTO L` finds `l:`.

**The label pass runs while the batch compiles**, ahead of every statement, which is what makes three refusals compile-phase (all class 15, all probe-confirmed against SQL Server 2025 on 2026-08-08):

- **Msg 133** — `A GOTO statement references the label '<n>' but the label has not been declared.`
  Fires even for a `GOTO` under an untaken branch, and a `PRINT` written before it produces no output.
- **Msg 132** — `The label '<n>' has already been declared. Label names must be unique within a query batch or stored procedure.`
  Fires with no `GOTO` referencing the label at all.
- **Msg 1026** — `GOTO cannot be used to jump into a TRY or CATCH scope.`
  Only *entry* is refused; jumping out of a TRY block is legal.

`Simulation.ScanBatchLabels` is that pass.
It walks the token stream once, tracking parenthesis depth and a stack of open `CASE` / `BEGIN…END` / `BEGIN TRY` / `BEGIN CATCH` constructs, and records each label's position plus the two nesting counts the jump needs.
Reading a lone `:` at parenthesis depth zero is what separates a label from the `::` of `hierarchyid::Parse` / `SCHEMA::x` (two adjacent operators) and from `JSON_OBJECT('a': 1)`'s key separator (always inside parentheses).
The whole walk is skipped unless the batch's raw text carries a `:` or the letters `goto` — two vectorized text searches almost every batch fails — so an ordinary batch pays nothing for the feature.

**The jump is a flag, not an exception**, following the same rule as BREAK / CONTINUE / RETURN.
`BatchContext.PendingGotoLabel` makes `IsSkipping` true, so every enclosing dispatch loop unwinds without demanding its own terminator, and the jump is serviced by the innermost loop the label is inside — compared on `BatchContext.DispatchLoopDepth`, which counts only the loops `BEGIN…END` / `BEGIN TRY` / `BEGIN CATCH` open (distinct from `BlockDepth`, which an `IF` / `WHILE` over a single statement also bumps).
That is what lets `WHILE … BEGIN … GOTO l; l: … END` keep iterating while `WHILE … BEGIN … GOTO l; END l: …` leaves the loop.
Jumping *into* a block — legal on real, which simply runs on from the label — leaves that block's opening `BEGIN` unexecuted, so `BatchContext.PendingBlockEnds` counts the `END`s the loop then steps over.

Labels are scoped to their batch or module body: a procedure carries its own set, and reusing the caller's name is not a collision.

### Separators between the THEN branch and `ELSE`

Real allows statement separators in that slot: `IF @o = 1 PRINT 'a'; ELSE PRINT 'b'` parses, as does a run of them and the `BEGIN … END; ELSE` form — AdventureWorks' `ddlDatabaseTriggerLog` writes the first shape.
The separators are consumed only when an `ELSE` actually follows, so an IF with none leaves its terminator for the dispatch loop and the next statement still runs.
An `ELSE IF` chain threads the same rule at every arm.

## Statement-terminating vs batch-aborting errors (unified continue-on-error)

In SQL Server most errors are **statement-terminating, not batch-terminating**: the failed statement ends but the batch continues to the next one (unless `SET XACT_ABORT ON`, or a batch/connection-aborting severity).
The engine models this as its **only** mode — there is no fail-fast fork.
Every top-level batch continues past a statement-terminating error, emitting a **`SimulatedErrorOutcome`** into one shared outcome stream, and **two renderers** consume it: the TDS wire writes error tokens; the in-process ADO surface converts outcomes to SqlClient-shaped exceptions.
Behavior was probed against real SQL Server 2025 + `Microsoft.Data.SqlClient` and treated as ground truth.

`Simulation.CreateResultSetsForCommand(command, continueOnError = true)` defaults its flag to `true`; both the in-process front door (`SimulatedDbCommand`) and `TdsSession.StreamOutcomesAsync` set it, so both render the same stream.
The flag marks a **top-level batch** (threaded onto `BatchContext.ContinueOnError`): child batches (proc / trigger / UDF / dynamic-SQL bodies) construct their own `BatchContext` and leave it `false`, so their errors **throw** and surface at the invoking statement rather than being emitted as outcomes (the parameter survives only because `TdsSession` — which must not be edited — passes it by name).

**The seam** is `DispatchOneStatement`'s catch (`Simulation.cs`).
Its materialize-then-catch wrapper (a) rolls back on deadlock class 13, (b) defers name-resolution errors in skip mode, (c) records the error into a `CATCH` frame when `TryFrameDepth > 0`.
Continuation adds: when `TryFrameDepth == 0` and `ContinueOnError` is set, a statement-terminating error is captured into a local and — after the `finally` — the cursor is advanced to the next statement boundary (the same recovery scan the TRY-caught and deferred-name paths use), `@@ERROR` (`connection.LastErrorNumber`) is set to the error number, and a `SimulatedErrorOutcome` carrying the exception (and a **`RowReturning`** flag, below) is `yield return`ed before `yield break`.
This path deliberately does **not** touch `InFlightError` / `ErrorSignaled` — those are TRY/CATCH-only state; outside a TRY the error goes to the client, not a CATCH block.

**Batch-aborting errors** — a path sits *before* the statement-terminating one and stops the whole batch (emit the one error, set `BatchContext.BatchAborted`, `DispatchStatementsUntil` breaks on the flag, **no** cursor-recovery scan).
Two kinds:
- **Bind-class name-resolution misses** (`IsBatchAbortingNameResolution`: Msg 208 invalid object, 207 invalid column, 209 ambiguous column, 4104 unbindable multi-part identifier, 4121 unfound column/function, 195 unrecognized function).
  Real SQL Server aborts the remaining batch (probe-confirmed: `SELECT 1; SELECT * FROM missing; SELECT 2` streams `1`, surfaces one Msg 208, never runs `SELECT 2` — contrast Msg 3701 / 8134 / a severity-16 RAISERROR, which continue).
- **An uncaught `THROW`** (`SimulatedSqlException.TerminatesBatch`, set by the THROW factories).
  Real SQL Server's `THROW` terminates the batch even though it shares class 16 with a *continuing* `RAISERROR` — probe-confirmed (`… RAISERROR('x',16,1); INSERT; THROW 50001,'y',1; INSERT` runs two inserts, aggregates Msg 50000 + 50001, and skips the third).
  The flag on the exception is what distinguishes the two.

Skipping the recovery scan is load-bearing: it kills the **abandoned-mid-parse cascade** — the error is thrown mid-parse (e.g. inside a DacFx `SELECT * FROM (…) AS [_results] OPTION (USE HINT('FORCE_LEGACY_CARDINALITY_ESTIMATION'))`), and the scan would otherwise stop on the OPTION clause's leading `USE` token and re-dispatch `USE HINT(…)` as a `USE <database>` statement → spurious Msg 911 / 319 / 102.
Regression: `BatchErrorRecoveryTests` (both `SqlServerSimulator.Tests` and `.Tests.SqlClient`).

**Classification** — `IsStatementTerminating`: continue when `ex.Class is >= 11 and <= 16 && ex.Number != 1205`, unless the error is batch-aborting (name-resolution set or `TerminatesBatch`, checked in an earlier branch).
Severity ≤ 10 are informational (not raised as errors — they flow to `InfoMessage`); severity ≥ 17 are batch/connection-terminating → abort; deadlock (Msg 1205, class 13) is the one in-range exception → abort (its class-13 rollback fires first).
No factory produces class ≥ 17, so the reachable batch-aborting cases are deadlock (concurrent sessions), `NotSupportedException` (any unmodeled feature; it propagates out of the stream to the caller / wire top-level `catch`), the name-resolution set, and an uncaught THROW.

**In-process rendering** — `SimulatedDbCommand` + `SimulatedDbDataReader` convert the outcome stream to exceptions ([`data-reader.md`](data-reader.md) has the reader detail).
`ExecuteNonQuery` / `ExecuteScalar` drain the whole batch (all side effects persist), then throw one `SimulatedSqlException` whose `Errors` collection aggregates every statement error in batch order (`SimulatedSqlException.FromErrors`; a lone error is rethrown as-is).
`ExecuteScalar` returns the first result set's first value only when the batch produced no error.
The reader surfaces errors **positionally**: a row-returning statement's error (SELECT / VALUES — `SimulatedErrorOutcome.RowReturning`, set from the leading token via `StatementContext.LeadingKeywordReturnsRows`) throws on the first `Read` and the reader survives to the next result set; a non-row-returning error (INSERT / UPDATE / DELETE / DDL) throws eagerly on the advance onto it (`ExecuteReader` or `NextResult`), matching how SqlClient surfaces an error token that no COLMETADATA precedes — and what lets EF Core's no-OUTPUT modification batches, which never call `Read`, still observe a failure.
Reader `Dispose` drains the batch's remaining statements (side effects persist) and swallows their errors.

**Wire framing** — `StreamOutcomesAsync` has an arm for `SimulatedErrorOutcome`: write the error token(s) via `WriteErrors`, then a DONE carrying `DONE_ERROR` OR'd with the normal more/final bit.
See [`tds-endpoint.md`](tds-endpoint.md).

**Known divergences** (accepted):
- **A genuine syntax / compile error mid-batch continues** rather than failing the whole batch.
  Real SQL Server fails the batch at compile time (Msg 102 / 156 / 108 / 116, etc.), but the simulator interleaves parse and execution — it never modeled a compile-then-run split — and the classification can't distinguish a parse-origin from a runtime-origin error (both are `SimulatedSqlException`s with a class in 11-16).
  A consequence for the reader: a compile-error SELECT (`ORDER BY 0`, `TOP` before `DISTINCT`) is `RowReturning`, so it surfaces at `Read`, whereas real SQL Server throws it at `ExecuteReader`.
  Real tooling never sends invalid batches; the batches that rely on continuation (`DROP #tmp` cleanup) are all runtime errors.
- **Row materialization**: a SELECT that errors mid-scan (`SELECT 10/id …`) materializes its rows up front, so the error fires before any partial row is yielded — real SQL Server streams the rows preceding the failing one, then throws.
  The positional shape (Read throws, reader survives, tail clean) matches; the pre-error row count does not.
  Continuation is also what `SET XACT_ABORT ON` suspends: under the option a statement-terminating run-time error ends the batch and rolls the transaction back instead of continuing, and caught by a `TRY` frame it leaves the transaction doomed — see [`transactions.md`](transactions.md#set-xact_abort).

## TRY/CATCH + ERROR_*() + live @@ERROR + THROW
`BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH` blocks parse via `Simulation.TryCatch.cs:ParseTryCatch`.
TRY and CATCH aren't reserved keywords (contextual identifiers), so the BEGIN dispatch site peeks the next token: `Tran`/`Transaction` routes to `TryParseBeginTransaction`, `TRY` (unquoted) routes here, `ATOMIC` raises `NotSupportedException`, anything else falls through to `ParseBeginBlock`.
Probed against SQL Server 2025.

**Catch boundary mechanism.**
`DispatchOneStatement` is split into an outer wrapper + a `DispatchOneStatementCore` iterator (yield-return inside try/catch isn't legal in C#, so the wrapper materializes Core's outcomes via `[.. Core(...)]` and runs the C# `try { ... } catch (SimulatedSqlException ex) when (batch.TryFrameDepth > 0) { ... }` around that).
On catch: captures into `BatchContext.InFlightError` (struct: number / message / severity / state / line / procedure), sets `BatchContext.ErrorSignaled = true` so `IsSkipping` picks it up, writes `Connection.LastErrorNumber = ex.Number` (backs live `@@ERROR`), then advances the cursor forward to the next statement boundary (`IsStatementBoundary`-token / `;` / EOB) so the outer dispatch loop can resume cleanly instead of re-dispatching the same partially-parsed statement (which infinite-loops).
Successful statements clear `LastErrorNumber` back to 0.
**This is exception-handling for actual error handling, not control-flow-via-exceptions** — the in-band signal "now run CATCH" still flows through the existing skip-mode flag plumbing.

**Scope tracking.**
`BatchContext.TryFrameDepth` counts active TRY *bodies* (decremented when CATCH starts, since CATCH isn't inside its own TRY).
`BatchContext.CatchDepth` counts active CATCH bodies (gates `THROW;` re-raise legality and the in-CATCH detection for `ERROR_*()`).
`BatchContext.InFlightError` is set on catch and read by the error-functions; nested TRY/CATCH saves+restores it around inner CATCH dispatch — if the inner CATCH re-throws (`THROW;`), the throw propagates through the outer TRY's still-active wrap which captures the re-thrown error into `InFlightError`, so the restore is gated on "is the post-CATCH state still signaled" (don't restore if so — the outer wrap already updated to the re-thrown values).

**ERROR_*() scalars** (`Parser/Expressions/ErrorFunctions.cs`): zero-arg `ERROR_NUMBER` / `ERROR_MESSAGE` / `ERROR_SEVERITY` / `ERROR_STATE` / `ERROR_LINE` / `ERROR_PROCEDURE`.
All return typed NULL when `BatchContext.InFlightError` is null (outside CATCH); inside CATCH they project the captured fields.
The `CaughtError` captures the caught exception's *resolved* `LineNumber` / `Procedure`, so `ERROR_LINE()` and `ERROR_PROCEDURE()` report exactly what the exception carries — a schema-qualified `dbo.<name>` inside a stored-procedure body, NULL for top-level / dynamic-SQL scopes.
Line-number semantics (statement-start for runtime/bind, token line for syntax, CREATE-relative for proc bodies) live in [`errors.md`](errors.md).

**THROW statement** (`Simulation.Throw.cs`): two forms, both raise `SimulatedSqlException` (caught at the wrap boundary if in a TRY, propagates out of the batch otherwise).
THROW is a **contextual** keyword in SQL Server's grammar (not in the reserved list — introduced with TRY/CATCH in SQL Server 2012, can still be used as a column alias / variable / identifier).
The dispatch case-matches `UnquotedString u when u.Span.Equals("THROW", ...)` rather than going through `ReservedKeyword`; `IsStatementBoundary` has a parallel UnquotedString branch so the post-dispatch cursor-normalization recognizes it.
Statement adjacency requires `;` before THROW (probe-confirmed: `select 1 throw 50000, 'msg', 1` is parsed as `SELECT 1 AS throw` then Msg 102 on the trailing `50000`, while `select 1; throw 50000, 'msg', 1` works).
- **`THROW;`** (no args) — re-raise current error from enclosing CATCH.
  Reconstructs the exception from `InFlightError` (number / message / state).
  Outside CATCH → **Msg 10704** verbatim.
  Compile-time check: fires even from un-taken IF branches (same pattern as Msg 178 / Msg 135) — but the check is **lexical**: a bare `THROW` inside a CATCH whose TRY body succeeded (the CATCH skip-parses) is legal, so the skip-dispatch branch bumps `CatchDepth` too (SSMS's Select-Top-1000 server-properties batch has exactly that shape).
- **`THROW <number>, <message>, <state>;`** — raise new error.
  Each arg is a full `Expression` (so literals, `@variables`, casts, etc. all work).
  Severity is fixed at class 16 regardless of number — probe-confirmed (`THROW 50001, 'custom', 7` reports Class 16, State 7).
  NULL on any arg surfaces a generic raised error; real SQL Server has more specific paths but apps rarely hit them.

**Live @@ERROR.**
`LastErrorExpression` reads `runtime.Batch.Connection.LastErrorNumber` instead of hardcoded 0.
The wrap maintains the value: caught error → `LastErrorNumber = ex.Number`, successful statement → reset to 0, with `StatementContext.SuppressErrorReset` as the one opt-out (used by `RAISERROR ... WITH SETERROR` at sev ≤ 10 to land 50000 into the next statement's read).
Outside any TRY/CATCH the value otherwise stays 0 because uncaught errors tear down the batch.

**Grammar edges.**
- Empty TRY body (`BEGIN TRY END TRY ...`) → **Msg 102** ("Incorrect syntax near 'try'") — probe-confirmed wording.
  Empty CATCH body is legal.
- TRY / CATCH are case-insensitive contextual identifiers (`BEGIN TrY ... END try ...` works).
- `BEGIN TRY` must be matched by `END TRY BEGIN CATCH ... END CATCH` — the parser strictly enforces this two-pair shape.
- Statement-level atomicity preserved in CATCH: a multi-row INSERT failing on row N rolls back its partial heap writes via the existing undo log before the caught error materializes (CATCH sees zero rows in the dest).
  The IF/WHILE skip-mode plumbing also composes: an `IF 1=0 BEGIN TRY ... END CATCH` skip-dispatches the TRY body (no runtime errors fire) and CATCH never activates.
- Transactions: caught errors don't auto-rollback explicit transactions (matches real SQL Server's XACT_ABORT OFF default).
  The standard `IF @@TRANCOUNT > 0 ROLLBACK` idiom in CATCH works.
  Under `SET XACT_ABORT ON` the same caught error leaves the transaction **doomed** — `@@TRANCOUNT` unchanged, `XACT_STATE()` reading `-1`, and the next statement that writes refused with Msg 3930; see [`transactions.md`](transactions.md#set-xact_abort).

**Fidelity gaps.**
- **Parse-time name-resolution errors ARE caught** by TRY/CATCH in the simulator — `BEGIN TRY SELECT * FROM nonexistent END TRY BEGIN CATCH ... END CATCH` runs the CATCH.
  Real SQL Server reports Msg 208 outside TRY/CATCH because name resolution fires during compile, before TRY's runtime activates; the simulator has no compile/runtime split, so an *active* TRY body's name-resolution error is caught rather than surfacing at compile time.
  (A *skipped* TRY body is handled separately — see the deferred-name-resolution note above — its missing-name error is swallowed and never reaches CATCH.)
  Apps that depend on the catch-or-not distinction here will diverge.
- **Divide-by-zero raises raw `DivideByZeroException`** (not `SimulatedSqlException` Msg 8134), so TRY/CATCH doesn't catch it.
  Gap independent of TRY/CATCH; will close when the arithmetic error path is converted to factory-emitted Msg 8134.
- **ERROR_LINE() / ERROR_PROCEDURE()** report the exception's resolved line / procedure (see [`errors.md`](errors.md)); residuals there (tokenizer-thrown multi-line literals; UDF/TVF/trigger/view bodies) are narrow.

## `RAISERROR`
`RAISERROR (msg, severity, state [, arg]…) [WITH option[, option]…]` — fires an error of the supplied severity / state, or surfaces an informational message at severity ≤ 10.
`msg` is either an inline format string (`varchar`/`nvarchar` literal or `@variable`) or a numeric `msg_id`; the per-arg substitution machinery is a C-runtime-style printf subset shipped via `Parser/MessageFormatter.cs`.

**Severity routing** (probe-confirmed against SQL Server 2025):
- Severity 0-10 → informational.
  Doesn't throw; doesn't enter TRY/CATCH; doesn't update `@@ERROR` unless `WITH SETERROR` forces it to 50000.
  NULL / negative severity treated as 0.
- Severity 11-18 → catchable error.
  Throws `SimulatedSqlException` with `Number=50000`, `Class=severity`, `State=state`.
  Caught by enclosing TRY/CATCH; outside TRY/CATCH, propagates out of the batch.
- Severity 19-25 → Msg 2754 ("Error severity levels greater than 18 can only be specified by members of the sysadmin role, using the WITH LOG option").
  The simulator has no principal model and uniformly applies the non-sysadmin gate here — apps connecting as non-sysadmin service accounts see the same wall on real SQL Server.
- Severity > 25 → Msg 2754 (same path).

**State clamping**: state values outside `0..255` (including NULL and negative) silently clamp to 0 (probe-confirmed; real SQL Server doesn't raise here).

**Format-specifier coverage** (`%[-][0][width][.precision][length]type`):
- Types: `%s` (string), `%d` / `%i` (signed int), `%u` (unsigned int — negative int32 renders as uint32), `%o` (octal), `%x` / `%X` (hex lower/upper), `%%` (literal `%`).
- Length modifiers: `l` (no-op — SQL Server's long is 32-bit), `I64` (bigint; bare `%d` with a bigint arg raises Msg 2786).
- Width / precision / flags: right-align (default), `-` left-align, `0` zero-pad, `.N` for string precision (max chars from source) — all probe-confirmed.
- NULL substitution: renders the literal text `(null)` regardless of specifier; same for missing args (more specifiers than supplied args).
  Extra args beyond the specifier count are silently ignored.
- Unsupported specifier letters (`%c`, `%p`, `%f`, trailing lone `%`) raise Msg 2787 with the offending spec text echoed.
  Real SQL Server's `%c` rejection was a probe surprise — it's documented in older references but not in SQL Server 2025's runtime.
- Arg-type mismatch raises Msg 2786 with the 1-based parameter index ("The data type of substitution parameter N does not match the expected type of the format specification").
- More than 20 substitution args raises Msg 2747 ("Too many substitution parameters for RAISERROR. Cannot exceed 20 substitution parameters") — applied even when the format string has no specifiers.

**msg_id matrix**: the simulator hasn't modeled the `sys.messages` registry or `sp_addmessage`, so every numeric `msg_id` falls into one of two error paths:
- `msg_id = 50000` (the reserved synthesized id for the inline-string form) or `msg_id < 13000` → Msg 2732 ("Error number N is invalid. The number must be from 13000 through 2147483647 and it cannot be 50000").
- Any other numeric id, including system message ids like 13001 that exist in real SQL Server's `sys.messages` → Msg 18054 ("Error N, severity S, state T was raised, but no message with that error number was found in sys.messages. If error is larger than 50000, make sure the user-defined message is added using sp_addmessage").
- Inline-string form (`RAISERROR('text', …)`) always uses msg id 50000.

**WITH options**: comma-separated list after the closing `)`.
`LOG` raises Msg 2778 ("Only System Administrator can specify WITH LOG option for RAISERROR command") uniformly — probe-confirmed against the non-sysadmin reference connection.
`NOWAIT` is accepted and ignored (no streaming model).
`SETERROR` is the load-bearing option for severity ≤ 10: it forces `@@ERROR` to 50000 for the next statement to read; without it, sev ≤ 10 leaves `@@ERROR` untouched.

**Grammar restriction**: `msg` / severity / state / sub args accept only literals, signed numeric literals, `@variable` references, and `NULL` — arbitrary expressions (`CAST(...)`, function calls, arithmetic) raise Msg 102 at parse time.
Matches real SQL Server's grammar (probe-confirmed).

**Fidelity gaps** (modeled deviations):
- Severity ≥ 20 is uniformly rejected via Msg 2754; the simulator has no principal model to distinguish sysadmin from non-sysadmin callers.
  Apps running as sysadmin on real SQL Server would see different behavior — but the simulator's non-sysadmin posture matches the typical production posture.
- `WITH LOG` is uniformly rejected via Msg 2778 for the same reason.
  Apps that depend on the message being logged (real SQL Server writes to the Windows event log + SQL Server error log) get neither logging nor the implicit sysadmin permission grant.
- System-message ids registered in real SQL Server's `sys.messages` (e.g. `RAISERROR(13001, 16, 1)` surfaces the system "file name" message text) fall through to Msg 18054 here.
- Severity ≤ 10 messages are informational and flow to `SimulatedDbConnection.InfoMessage` (verified for both `RAISERROR('m', 10, 1)` and `RAISERROR('m', 0, 1)`) rather than being raised — matching the severity table above.
  The behavioral effects (TRY/CATCH skip, `@@ERROR` via SETERROR) are preserved alongside the delivered text.
- `NOWAIT` is structurally ignored (no streaming model); real SQL Server flushes the buffer immediately.

## `PRINT`
`PRINT <expression>` parses + evaluates the operand and delivers the text to `SimulatedDbConnection.InfoMessage`, the simulator's stand-in for `SqlConnection.InfoMessage` (`DbConnection` defines no such event, so the shape is mirrored rather than inherited).
Multiple PRINTs in one batch coalesce into a single newline-joined event.
The evaluation isn't a no-op: operand-side errors still surface — `PRINT 'val=' + 5` raises Msg 245 because the `+` operator's int-side promotion tries to parse `'val='` as int (probe-confirmed against SQL Server 2025).

Probe-confirmed semantics:
- `PRINT NULL` and `PRINT ''` deliver a message whose whole body is a single U+0020 space — real emits exactly one character rather than an empty message.
- `PRINT` resets `@@ROWCOUNT` to 0 — applied by the dispatcher after the parser returns.
- Skip-mode (un-taken IF, after BREAK / CONTINUE / RETURN) suppresses operand evaluation entirely, so an error-bearing operand in a skipped branch doesn't fire.
  Standard pattern: parse the expression unconditionally to advance the cursor, then gate `expression.Run` on `!batch.IsSkipping`.
- Rollback doesn't undo a PRINT (real SQL Server's InfoMessage stream is non-transactional too), and the simulator's delivery is likewise outside the undo log.
- The message truncates at **8000** characters, or **4000** when the operand is one of the national string types (`nvarchar` / `nchar` / `ntext`).
  Real delivers exactly that many characters and drops the rest without a warning.

### The operand admits only scalar expressions

`PRINT` has no column scope and no rowset scope, so real refuses both a name and a subquery there, in whichever order the left-to-right reading meets them:

- A name — bare, bracketed or dotted — is **Msg 128**, class 15 state 1: `The name "a.b" is not permitted in this context. Valid expressions are constants, constant expressions, and (in some contexts) variables. Column names are not permitted.`
  The name is rendered as written, double-quoted, with brackets stripped.
- A subquery at any depth, a function argument or a `CASE` arm included, is **Msg 1046**: `Subqueries are not allowed in this context. Only scalar expressions are allowed.`
- `PRINT bbb + (SELECT 1)` reports Msg 128 and `PRINT (SELECT 1) + bbb` reports Msg 1046 — the reading order decides, not the construct.

Both are settled while parsing (`ParserContext.ScalarOnlyOperand` arms the recording; `Expression.Counted` notes the first reference and `ParseSubqueryRejectingNextValueFor` refuses a subquery), so an un-taken `IF` branch and a module body at CREATE raise where real raises.
A name the parser turns into a function call is withdrawn from the record at the same seam that un-counts it, which is why `PRINT DB_NAME()` still prints.

### Non-string operands

The rendering is the implicit conversion to a character string real applies, not a bare cast:

| Operand | Rendering |
| --- | --- |
| `datetime` / `smalldatetime` | style 0 — `Aug  6 2026  1:45PM` |
| `date` / `time` / `datetime2` / `datetimeoffset` | the type's own ISO layout — `2026-08-06`, `13:45:12.1234567`, `2026-08-06 13:45:12.345`, `2026-08-06 13:45:12.3450000 +05:30` |
| `money` / `smallmoney` | two decimals — `1.00` |
| `float` / `real` | style 0 — six significant digits, scientific with a three-digit exponent outside `[1e-4, 1e6)`: `1.23457e+018`, `1.234e-006`, `1e+006`, and `123456` unchanged |
| `binary` / `varbinary` / `image` | `0x`-prefixed hex, not style 0's byte reinterpretation |
| everything else | the ordinary coercion to `varchar` |

## `WAITFOR DELAY`
`WAITFOR DELAY '<time>'` and `WAITFOR DELAY @variable` block the calling thread via `Thread.Sleep(TimeSpan)`, matching real SQL Server's "blocks the connection" semantics.
Operand grammar is strict (matches probe of SQL Server 2025): only a varchar/nvarchar string literal or an `@-variable` reference.
`cast(...)`, integer literal, bare `NULL` literal all fail at parse (Msg 102/156); `time`-typed variable raises **Msg 9815** (`"Waitfor delay and waitfor time cannot be of type time."` — note SQL Server reserves the operand slot for *string-typed* values, not its own `time` type).
Empty string and NULL-valued variable both silently succeed as zero delay.
Bad-format string raises **Msg 148** with the offending value embedded.
`@@ROWCOUNT` resets to 0.
Skip-mode suppresses the sleep entirely (an `IF 1=0 WAITFOR DELAY '00:00:10'` returns instantly).
**`WAITFOR TIME`** (absolute-time wait) raises `NotSupportedException` — scheduling-style primitive not yet needed.
**Cancellation**: an `ExecuteReaderAsync` caller's `CancellationToken` *is* observed — the sleep waits on the per-execution `CancellationTokenSource`'s handle (see [`tds-endpoint.md`](tds-endpoint.md#mid-stream-attention-cancel)), so a token cancelled 400 ms into a 5-second `WAITFOR` ends the wait at 400 ms and aborts the batch at the statement boundary (a trailing `SELECT 42` in the same batch doesn't run).
The cancelled execution then surfaces as **Msg 0** (`SimulatedSqlException`) from `ExecuteReader` / `ExecuteNonQuery` / `ExecuteScalar` and their async forms — the exception real SqlClient manufactures for an attention, so a caller can't mistake a cancelled batch for a legitimately empty answer.
A token already cancelled *before* execute, and one observed while draining an already-open reader, both keep the ADO.NET base class's `TaskCanceledException` — matching real, which reserves the Msg 0 shape for the mid-execution case.

**`CommandTimeout`** is enforced in-process by the same machinery: `BeginExecutionScope` arms the execution's cancellation source with `CancelAfter`, so an expiry aborts through the identical safe-point path and surfaces **Msg -2, Class 11, State 0** (`Execution Timeout Expired.  …` — SqlClient's own wording, double space included).
The cause is recovered from a flag `CancelExecution` sets rather than a second token, so a caller-driven cancel stays Msg 0 while a deadline expiry reports Msg -2.
The default is **30 seconds** (SqlClient's) and `CommandTimeout = 0` is infinite.
Probe-confirmed against SQL Server 2025: the connection stays usable afterwards and an **open transaction survives** the timeout (`@@TRANCOUNT` unchanged), the same shape a cancel has under the default `SET XACT_ABORT OFF` — see [`transactions.md`](transactions.md).
The wire path needs none of this: SqlClient enforces its own `CommandTimeout` client-side by sending an attention, which the endpoint already answers.

**Enforcement is at safe points, not a hard deadline.**
A timeout is observed at a statement boundary, a `WHILE` iteration, or during a `WAITFOR` wait, so a *single* long-running statement still materializes to completion before the deadline is noticed — the same bound the cancel path documents.
