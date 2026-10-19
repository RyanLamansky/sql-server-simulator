# Error diagnostics: line number, server, procedure

How `SimulatedSqlException` / `SimulatedError` populate the three diagnostic fields real SqlClient surfaces on every error — `LineNumber`, `Server`, `Procedure` — plus the coupled `ERROR_LINE()` / `ERROR_PROCEDURE()` scalars and the TDS ERROR / INFO token fields.
All semantics below are probe-confirmed against SQL Server 2025.

## Probed matrix

| Shape | Real line | Notes |
| --- | --- | --- |
| Runtime error (divide-by-zero, conversion) | failing statement's **start** line | not the erroring expression's line — a SELECT spanning lines 3-4 with `5/0` on line 4 reports line 3 |
| Bind error (Msg 208 invalid object) | statement start line | |
| Binder error on a reference (Msg 207 / 4104 / 209, a GROUP BY violation) | the **reference's** line | the statement's first line when real parameterizes it; see [the whole report](#a-statements-whole-binder-report) |
| Constraint violation (INSERT/UPDATE) | the DML statement's line | |
| Syntax error (severity 15: Msg 102/156/…) | the **offending token's** line | differs from statement start for multi-line statements; at the end of the text, the last token's line (probed 2026-10-15) |
| Unclosed string (Msg 105) | the line the literal **opened** on | even when the body runs across several lines to end of input |
| Unclosed block comment (Msg 113) | the **end-of-input** line the comment ran to | *not* the line it opened on — the one asymmetry from Msg 105 |
| Two statements on one line | that shared line | |
| `THROW n, m, s` (value form) | the THROW statement's line | |
| `THROW;` (re-raise in CATCH) | the **original** error's line | not the re-raising statement's line |
| Procedure body error | line relative to the **batch that created it** — comments and blank lines ahead of the `CREATE` count | + `Procedure` = the name as the invoking `EXEC` spelled it, brackets dropped and case kept (`exec p` → `p`, `exec DBO.P` → `DBO.P`, probed 2026-09-23) |
| Procedure / `sp_executesql` argument that fails to convert | **0** | + `Procedure` for a procedure (Msg 8114, or an xml parse error) |
| `sp_executesql` arguments that don't bind (Msg 8144 / 8146 / 8178) | **0** — but the statement's line for a declared parameter missing from a call that supplied no arguments at all | Msg 214 (a non-Unicode statement or declaration) keeps the statement's line and names `sp_executesql` as `Procedure` (probed 2026-09-26) |
| Procedure call whose arguments don't bind (Msg 201 / 8144 / 8145) | **0** | + `Procedure` = the name as the `EXEC` spelled it (probed 2026-09-23) |
| `GOTO` to an undeclared label (Msg 133) / a duplicate label (Msg 132) | the batch's last line / the second label's line | raised while the batch compiles, ahead of anything running (probed 2026-09-23 and 2026-10-02); in a module's `CREATE` they name the module |
| Trigger body error | creating-batch-relative line | + `Procedure = "<name>"` (**unqualified**) |
| **CREATE-time bind error** (the body error that aborts the CREATE) | batch line | + `Procedure = "<name>"` — **unqualified for every module kind**, procedures included; a `CREATE TRIGGER` naming a missing parent (Msg 8197) is attributed the same way |
| Scalar-UDF / inline-TVF / multi-statement-TVF / view body error | the **outer invoking** statement's line | no `Procedure` — real inlines these for attribution (even the multi-statement TVF) |
| Nested procedure call | **innermost** procedure/trigger frame's line + procedure | a UDF error inside a proc attributes to the **proc's** calling line, not the UDF |
| `EXEC('…')` / `sp_executesql` | line relative to the **dynamic batch** | no `Procedure` |
| PRINT / RAISERROR ≤ 10 (INFO) | statement start line | on `SqlError.LineNumber` |
| NOLOCK / READUNCOMMITTED on a DML target (Msg 1065) | **15**, wherever the statement sits — batch, dynamic SQL, or a `CREATE PROC` body (which still attributes `Procedure`) | real's own constant; the sibling hint errors (Msg 1047 / 1069) report the statement's line (probed 2026-09-26); `SimulatedSqlException.PinLine` |

`Server`: real SqlClient reports the **connection data source** on `SqlException.Server` / `SqlError.Server` (probe: `localhost,1433`), *not* the server's `@@SERVERNAME`.
The wire ERROR/INFO token's server-name field carries `@@SERVERNAME` instead — SqlClient ignores it and substitutes the data source; token-rendering clients (sqlcmd) display it verbatim.

`ERROR_PROCEDURE()` returns the same name as `SqlError.Procedure`; `ERROR_LINE()` returns the same line the exception carries.
A `PRINT` or low-severity `RAISERROR` in a procedure body carries the procedure too.

A **system procedure's own error** names the procedure by the name it was called by (`sp_help` / `sys.sp_help`) at the line of real's source that raises it — a missing parameter's Msg 201 at line 0 — through `AttributedToSystemProcedure` and its probed `SystemProcedureErrorSite` table, some naming an inner procedure instead (`sys.sp_refreshsqlmodule_internal`, the application-lock procedures' `sys.xp_userlock`); an error in evaluating an argument stays the batch's (probed 2026-09-26).
An error the table doesn't list keeps the batch's attribution.
The informational messages a system procedure prints from its own body take the same attribution through `SimulatedSqlException.SystemProcedureMessage`, each at its probed line (`sp_help`'s section blanks, `sp_depends`' headings, `sp_configure`'s change note, `sp_recompile`'s confirmation).

## The message stream

Every informational message — `PRINT`, a severity-0-10 `RAISERROR`, and the engine's own (Msg 3621, 8153, 5701, 5703, 11729, the procedures' severity-10 texts) — is a `SimulatedInfoOutcome` in the outcome stream, placed where real sends its INFO token.
The engine queues one on `SimulatedDbConnection.PendingMessages` as it happens, and the dispatch loop places the queue ahead of the statement's own outcomes — for a statement that streams what it runs (a block, `TRY`, `IF`, `WHILE`, a call), ahead of the next outcome it sends, so a system procedure's header goes out with the result set it heads.
Probed through SqlClient 7 against SQL Server 2025 (2026-09-23):

- **One event per message**, fired as the reader reaches it: during `ExecuteReader` for what precedes the first result set, during `NextResult` for what follows.
- **Severity 10 arrives as class 0**; severities 1-9 keep their number.
- **An error carries the messages of its stretch of the batch.**
  `ExecuteNonQuery` / `ExecuteScalar` read the whole batch and, if anything failed, throw one exception holding every error and then every message, in order — a `PRINT` ahead of the first error included.
  `ExecuteReader` fires the messages ahead of its error as events and throws the error with everything the rest of the batch sends: SqlClient hands out no reader then and drains the response, so the later statements' errors and messages join the exception, result sets read past (probed 2026-09-30 against SQL Server 2025).
  `NextResult` and `Read` throw the error alone; what follows fires on the next advance.
  `Message` joins every entry with `Environment.NewLine`, as SqlClient's does.
- **Msg 3621** (`The statement has been terminated.`, class 0, state 0) follows an execution error that ends a row-writing statement — `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `SELECT … INTO`, and `ALTER TABLE … ALTER COLUMN`'s rewrite — but not a compilation error (Msg 206 / 213 / 544), a `SELECT`'s own error, a batch-ending one (a conversion failure, anything under `XACT_ABORT ON`) or one a `TRY` / `CATCH` handles.
  Which numbers count is an explicit list (`Simulation.IsStatementTerminationNoticed`); on the wire it goes out ahead of the failing statement's DONE, where real sends it (captured 2026-09-28 against SQL Server 2025).
  A write in a function body — a multi-statement function filling its return table — ends the calling statement the same way, and earns the calling statement its Msg 3621 (`SimulatedSqlException.EndedFunctionWrite`).
  Inside a trigger body every statement runs within the firing one, so any statement's error earns it, a `SELECT`'s included — see [`triggers.md`](triggers.md#errors-in-a-trigger-body) for where it then points.
  An identity overflow takes **Msg 3606** (`Arithmetic overflow occurred.`, class 0, state 0) in its place (probed 2026-09-25 against SQL Server 2025); uncaught, the overflow ends the batch, and its 3606 then reports line 1 and no procedure (probed 2026-09-28).
- **Msg 8153** (`Warning: Null value is eliminated by an aggregate or other SET operation.`) goes out once per statement whose aggregate skipped a NULL with `ANSI_WARNINGS` on, after the rows and before the statement's DONE — ahead of the body for an `IF` / `WHILE` condition.
  Every aggregate warns but `COUNT(*)`, `STRING_AGG` and the JSON aggregates, window aggregates and a scalar subquery's included; an `EXISTS` body's and a `PIVOT`'s don't.
  Nor does one over groups that each provably hold a single row, which real reduces to the row's own values: a lone plain grouping set whose columns — closed over the `ON` and `WHERE` equalities to them or to constants — determine a key of every source, a table's primary key, unique constraint or enabled unfiltered unique index, a view's or derived table's key passed through, a `VALUES` list's or one-row-constant `UNION ALL`'s distinct constants (`Selection.GroupsHoldOneRow`, probed 2026-10-06 against SQL Server 2025).
  A grouping expression, `ROLLUP` and its kin, a filtered unique index, a join to a column no key covers and `VALUES` constants a collation might fold together leave the warning standing; a key equated across an outer join, a full one included, suppresses it, which is real's own reading even where a NULL-extended group holds several rows.
- **Msg 1708**, the warning a table whose largest row can pass 8060 bytes draws, goes out as `CREATE TABLE` (a `#temp` one included), `CREATE TYPE … AS TABLE` and `ALTER TABLE … ADD` complete, at state 2, and as `ALTER COLUMN` does at state 1, naming the table as written (probed 2026-10-06 against SQL Server 2025).
  The largest row is the smallest (Msg 1701's) plus, once there is a variable-length column, 2 bytes for their count, 2 per column and each one's largest value capped at 24 bytes — the pointer a value pushed off the row leaves, which a `max` or LOB type counts too — so two `varchar(8000)` columns draw nothing while `char(8000), char(50), varchar(10)` does; a sparse column adds nothing (`Simulation.MaximumRowExceedsLimit`).
  A table variable's goes out as its batch compiles, twice, ahead of anything the batch runs — once at a procedure's `CREATE` and twice as its first call compiles it, never from a plan it reuses (`BatchContext.CompileMessages`).
  The compile's second copies follow all its first ones and carry the line real's compile ends on rather than the `DECLARE`'s: the batch's (or body's) last statement's, or -1 when that statement is an `IF`, a `WHILE`, a `TRY`, a `GOTO`, a label or a `DECLARE` without an initializer (`BatchContext.LastCompiledLine`, probed 2026-10-17 against SQL Server 2025).
- **Msg 3607** (`Division by zero occurred.`) and **Msg 3606** (`Arithmetic overflow occurred.`), class 0 state 0, go out once each after the rows of a statement whose divide by zero or overflow answered NULL under `ARITHABORT OFF` with `ANSI_WARNINGS OFF` — a fresh session's `ARITHABORT` is off, so `SET ANSI_WARNINGS OFF` alone does it (probed 2026-09-25 against SQL Server 2025) — 3606 first whichever fault came first (probed 2026-09-26).
  The operators, `CAST` / `CONVERT` (a `real` target reads 0 rather than NULL) and the value an `INSERT` / `UPDATE` / `MERGE` writes take part; a conversion failure and an identity overflow still raise.
  So do `SUM` and `AVG`: an overflowing total NULLs only its own group, and a sliding window frame answers again once the overflowing row has left it.
  `SET ARITHIGNORE ON` keeps the NULLs and sends neither message ([`session-options.md`](session-options.md#arithignore-and-arithabort)).
- **Msg 5701** follows every `USE`, even of the current database, and **Msg 5703** every `SET LANGUAGE`.
- **A compile's non-aborting error** — the Msg 208 of a scalar function call real couldn't inline — goes out ahead of everything its batch runs with no DONE of its own, the next DONE carrying its error bit and losing its count flag; see [`programmable.md`](programmable.md#inlining-a-call-as-the-query-compiles).

### Not modeled yet

- **Deferred until requested** (with real's `sys.messages` rows, see [`backlog.md`](backlog.md#built-in-functions)): **every diagnostic but Msg 5703 and DBCC's Msg 2528 is English whatever the language**; real words its errors and messages in the session's language after `SET LANGUAGE` (`Fehler beim Konvertieren des varchar-Werts "x" in den int-Datentyp.` for Msg 245 under Deutsch, probed 2026-10-04 against SQL Server 2025).

## A statement's whole binder report

Real's binder reports every error a statement carries rather than its first: each unbindable reference once per occurrence (`SELECT x1, x1 FROM t WHERE x1 = 1` is three Msg 207s), in the binder's clause order, each at its own line (probed 2026-09-27 against SQL Server 2025).
The parse here stops at the first, so a statement whose bind fails is read again for the whole report: `Simulation.ReportEveryBindError` re-dispatches it in skip mode with a `BindErrorReport` on `BatchContext.BindErrors`, whose recording sites carry on past a miss with a stand-in type.
The report replaces the lone error before the dispatch arms judge it, so the batch compile walk, a module body's bind at `CREATE`, a statement bound late because the batch created its table, and a dynamic batch all report it alike; a statement that binds never allocates one.
What starts a report is `BindErrorReport.StartsReport`; the statements that take one are a query or DML statement, a `SET` / `RETURN` reading one, an `IF` / `WHILE` condition (whose branches then report as statements of their own) and a `CREATE` / `ALTER` of a view or function.

**Order** is positional within a clause and by clause between them.
A `SELECT` binds its `FROM` (joins' `ON` and derived tables included), `WHERE`, `GROUP BY`, `HAVING`, select list, `ORDER BY`, then `TOP`; a subquery's errors sit where its text does in the parent's clause.
An `UPDATE` binds `FROM`, `WHERE`, the `SET` targets, the `SET` values, `OUTPUT`; a `DELETE` `FROM`, `WHERE`, `OUTPUT`; an `INSERT` its source before its column list; a `MERGE` its source, `ON`, the insert column list, every `WHEN` condition, then every action.
Real binds some shapes by expansion and reports an operand once per copy — `COALESCE(x1, x2, x3)` is x1, x2, x1, x2, x3 — and reorders others: a `CASE`'s conditions before its results, a window's `OVER` clause before its arguments, an `IN` list last element first (`x1 IN (x2, x3)` is x1, x3, x1, x2); `BindErrorReport.Echo` and `Defer` place them.

**Where real stops** — each probed 2026-09-27:
- A failing CTE ends the report, the statement it leads unbound; an `UPDATE` stops after its `FROM` / `WHERE` and again after its `SET` targets; a `MERGE` after its `ON`.
- A FROM clause's name collision (Msg 1011 / 1012 / 1013) ends it: what bound ahead of the colliding source reports, then the collision.
- An `INSERT … SELECT` arity refusal (Msg 120 / 121) preempts everything, as a severity-15 parse error does; an `INSERT … VALUES` one (Msg 109 / 110 / 213 / 10709, and Msg 273 beside them) follows every name.
- An illegal conversion (Msg 529) met before any other error reports alone, at the statement's line; one met after an error is skipped, and no type check fails over an unbindable operand.
- Every other type check — Msg 206, 257, 402, 468, 8116, 8117 — reports where it sits and binding goes on past it, but only while nothing ahead of it failed, so a statement reports at most one: `SELECT a + d, x1` is Msg 206 then Msg 207, `SELECT x1, a + d` the Msg 207 alone (probed 2026-09-28).
  `BindErrorReport.CarriesPastTypeCheck` records it at the failing node's first column reference; the node types as `int` and its parent carries on, and a node typed again (its nullability) meets its check already recorded.
  A clause's term types through `Expression.TypeCarryingTypeChecks` and a predicate — each operand of an `AND` / `OR` too — through `BooleanExpression.BindCarryingTypeChecks`, so a check in one conjunct leaves the next one's names reported.
- Whether a written value suits its target — an `UPDATE`'s `SET`, an `INSERT`'s `VALUES` or `SELECT` source — is checked once every value has bound (`BindClause.Assignment`), and only when nothing failed: `UPDATE t SET a = d, s = x1` is the Msg 207 alone (probed 2026-09-28).
- An aggregate standing where real refuses one (Msg 147, 157, 1015, 4101, 5310, 5319, 8124) reports where the aggregate ends unless an error sorts ahead of it, and Msg 8124 — met binding the query's aggregates, before real judges its grouping — drops that query's GROUP BY violations wherever they are written (probed 2026-09-28).
- A `MERGE` reports its clause-order refusals (Msg 5324) after every name, then its columns assigned twice (Msg 264, once per extra assignment, at the statement's line), and binds its `OUTPUT` only when nothing failed; an `UPDATE` action assigning a column twice binds none of its values, an `INSERT` action's values still bind.
  An `UPDATE`'s Msg 264 follows its `OUTPUT` and drops when a `SET` target failed; an `INSERT`'s reports only when nothing ahead of it failed (probed 2026-10-19).
- A `MERGE` binds every `WHEN MATCHED` condition, then the `WHEN NOT MATCHED` ones, then the `WHEN NOT MATCHED BY SOURCE` ones, whatever order they are written in; the last two read only their own side, anything else being Msg 5333 / 5334 — a miss qualified by that side stays Msg 207 — while their actions miss the other side with the ordinary Msg 4104 / 207 (probed 2026-09-28).
- A `PIVOT` reports its aggregate's operand, then its `FOR` column, then a grouping column real can't compare (Msg 488); an `UNPIVOT` every listed column; past a miss nothing more binds against the rotated source, the outer select list included (probed 2026-09-28).
- A GROUP BY violation (Msg 8120 / 8121 / 8127) reports after its expression's own name errors and only when nothing ahead of the expression failed; an expression whose walk meets an unbindable name before its first violation reports none, one that found a violation first reports every violation it holds.
- Msg 130 and Msg 147 report where they sit unless an error sorts ahead of them — the aggregate's own operand included (`WHERE COUNT(x1) > 1` is the Msg 207 alone).
- Msg 8155 reports and binding goes on, the unnamed column simply unreadable.
- An `ORDER BY` name two select items share — two aliases, the same column twice, `SELECT *, a`, a set operation's output — is Msg 209 in `ORDER BY`'s place, even when both read one column and under `DISTINCT` (no Msg 145 follows it); an ordinal, a qualified name or an expression over the name reads the source instead (probed 2026-09-28).

**Lines**: each error reports its reference's own line (a GROUP BY violation its offending column's), except where real compiles the statement through simple parameterization, which reports every error at the statement's first line.
That applies to a single-table `SELECT` / `UPDATE` / `DELETE` / `INSERT` carrying a parameterizable literal and none of the constructs that disqualify it — `BindErrorReport.IsSimplyParameterizable` lists them, a heuristic over the shapes probed — and never in a module body.

A `CATCH` reads a compile-time report's first entry through `ERROR_NUMBER()` and its siblings, a multi-statement dynamic batch's included, where an error raised as several at run time shows its last (`SimulatedSqlException.CatchReadsFirstEntry`).

### Not modeled yet

- **A type check in a node reading no column** — `SELECT CAST('2020-01-01' AS date) + 1 + x1` — ends the re-read, so the term's later names go unreported, where real reports the Msg 207 after it (probed 2026-10-19 against SQL Server 2025); so does one raised outside the typing seams above.
  One over a column carries on in every node probed — a function's argument, a `CASE`, `IIF`, `COALESCE`, a unary minus, a scalar subquery, an aggregate.
- **A name only a run reaches** — one skip mode doesn't bind — reports alone.
- **A `MERGE` insert action's arity refusal** (Msg 109 / 110 / 213) reports alone, where real follows it with the statement's Msg 5324, and a `SET` target whose qualifier fails (Msg 4104) leaves the action's Msg 264 reported, where real drops it (probed 2026-10-19 against SQL Server 2025).
- **A `DECLARE` type spec its parser refuses** — a size past the type's (Msg 131, the decimal Msg 2717 state 1), a scale past the precision (192), a zero length (1001) or a bad scale (1002) — loses the whole statement's variables, the ones declared before the bad spec in it too, in a module body or at the top of a batch; real reports a Msg 137 at every later reference to one beside the first error, where the simulator reports the first error alone (probed 2026-10-07 and 2026-10-11 against SQL Server 2025).
  A missing type (Msg 2715), a width on a type that takes none (2716) and a missing XML schema collection (6314) still declare the variable.
  The same holds for a type name of three or more parts (Msg 117, `The type name … contains more than the maximum number of prefixes`), whose later references are Msg 137.

## Syntax-error recovery

A batch whose parse fails reports the syntax errors real's parser finds past the first, never a binder error (probed 2026-09-28 against SQL Server 2025).
Real recovers the way a yacc parser does: it restarts at the token it failed on, discards tokens that can't begin a statement, and reports a further Msg 102 / 156 only once three tokens have parsed since the last — `select 1 +; select 2 +;` reports both, `select 1 frm t; select * from where;` both, `select (1; select 2;` one.
An error a grammar action raises rather than the token stream — Msg 319 for a `WITH` after an unterminated statement, Msg 111 for a module `CREATE` not first in its batch, Msg 178 for the valued `RETURN` its body then holds — is reported however soon it comes, so a table hint the grammar refuses (`INSERT t (c) WITH (TABLOCK) …`) is Msg 156 then the Msg 319 its `WITH` raises read as a common table expression.
`Simulation.WithRecoveredSyntaxErrors` re-reads the batch from each restart point with the text before it blanked out (lines and positions stay as written), restarting only at a keyword, `;`, `THROW` or a `(` that opens a query, since real's grammar gives a bare name nothing to begin.
It walks the simulator's own statement parser rather than real's grammar, so where that parser reads a restart differently the report diverges; see [Divergences / residuals](#divergences--residuals).
What it carries past the blanking, each probed 2026-09-30 against SQL Server 2025:

- **A restart is never its batch's first statement**, so a module `CREATE` there is Msg 111 again: two misplaced `CREATE DEFAULT`s report one each, at their own lines, and so does one a syntax error stops at (`SELECT 1 + CREATE VIEW …`), while a `CREATE SCHEMA` element list's refusal stands on the word after its `CREATE`, where real resumes (probed 2026-10-19 against SQL Server 2025).
- **A module body recovers too.** A procedure's, trigger's or function's body — bound on its own child batch at `CREATE` — restarts on a child batch with the body's own frame, so a restart reads its `RETURN`, parameters and return table as the body does, and every variable and table variable the failed walk had declared.
  An inline function's body, parsed as one query, restarts as statements, which is what makes `RETURN SELECT 1 + FROM (SELECT 1 a) q` Msg 156 then Msg 102 at `q`.
- **An `END` closing a block the blanked text opened** is the block's end, not Msg 102, and an `END TRY`'s `BEGIN CATCH` opens with it; a `CASE` the error left open is abandoned, so its `END` closes the enclosing block instead.
  The three-token count runs on across such an `END`.
- **Msg 4145 and Msg 137 send the parser on in place** rather than into recovery, so the tokens after them count toward the next report.
  A Msg 4145 (a non-boolean expression where a condition belongs) is always reported and its own token counts, since the parser raised it on reading that token — which is what reports `SELECT 1 WHERE 1; SELECT 2 +`'s Msg 102.
  One raised on a `(` ended the condition before it, so the parser reads on from that `(` as a statement, query or not — `WHERE [abs](1) IS NULL` reports nothing more (probed 2026-10-01 against SQL Server 2025).
  A Msg 137 (an undeclared variable) counts through its variable, so `DECLARE @a int = @b + @c` reports only `@b` while `SELECT @nope; SELECT @nope2` reports both, and a variable whose `DECLARE` a syntax error cost is Msg 137 where it is next read.
  A binder error after either still waits for a batch that parses.
- **A column without a data type** (Msg 173 state 13, a table list's name followed by `,`, `)`, the batch's end or a column constraint's keyword) is a grammar-action error raised on the name: always reported, the parser reading on past the name, so `create table #t (b) with (x = 1)` adds the `WITH`'s Msg 319 and `create table #t (b, c)` reports `b` alone (probed 2026-10-16 against SQL Server 2025).
- **A `WITH` recovery reaches by discarding tokens** reads as a common table expression after an unterminated statement, Msg 319, whether or not the expression then parses (`select 1 + ) with x as (select 1 a) select a from x`); one past a `;` doesn't.
- **An error at the end of the text** counts every token before it and names the last one — `WITH CUBE` read as a common table expression after a refused `GROUP BY ALL` is its Msg 319 — except in a scalar or multi-statement function body, captured without its closing `END`, where it is Msg 156 near that `END`, as written and at its line.

A schema-bound body's Msg 1054 (a select-list star, `GROUP BY ALL`) is a grammar-action error too, and the schema-bound rule outlives the blanked header, since the body runs to the end of its batch ([`programmable.md`](programmable.md#schema-binding-with-schemabinding)).

A `SET` to an undeclared variable is Msg 137 at **state 1**, raised once the statement has parsed, so a syntax error or an undeclared variable (state 2, as every read is) on its right-hand side comes first.
A `SELECT @a = …` item's undeclared target is the same state-1 refusal, raised once its item has parsed (probed 2026-10-16 against SQL Server 2025), and a value the batch ends inside a string or comment after reports the lexer's error first (`select @a = 1 'x` is Msg 105, then 137).
Either is raised on the token after the assignment, at the line of the assignment's last token: recovery reads on from that next token, counting the last one, so `set @a = 1` then a later line's `select @b` reports both.

**An undeclared variable standing alone as an item of the statement's own select list** — an `INSERT … SELECT`'s and a `UNION` branch's included — with the batch ending inside the string or comment right after it reports the lexer's error first and the Msg 137 after it, at the variable's line (`select @a 'x`, `select @a /* x`); real's binder names the variable only once its item ends (probed 2026-10-16 against SQL Server 2025).
Anywhere else the Msg 137 comes first: `select x = @a 'x`, `select -@a 'x`, `select (@a 'x`, a subquery's or a derived table's item.
A table variable read as a scalar there, a binder error, isn't reported at all.

**An `@@` name no built-in claims** is a variable, so an unknown one is an undeclared variable everywhere `@a` is — Msg 137 naming it whole (`"@@nope"`), at state 2 as a read and state 1 as a `SET`'s or a `SELECT` item's target, in the same orders against a string or comment the batch ends inside (probed 2026-10-16 against SQL Server 2025).
A built-in matches only as real spells it: `@@LOCK_TIMEOUT` and its siblings need their underscores, so `@@LOCKTIMEOUT` and `@@CPU__BUSY` are undeclared variables too.

**A window or `WITHIN GROUP` key folding to a constant** (Msg 5308 / 5309) is a binder error, so any syntax error in the batch reports instead; the statement is read again for its binder report, which records the refusal and parses on.

**An unterminated list ending the batch** names its last token at that token's line, a `CREATE TABLE`'s and a table variable's column list as every other list does (`create table #t (a int,\nb int` is near `'int'` at line 2; probed 2026-10-16 against SQL Server 2025).

**A string or block comment the batch ends inside** (Msg 105 / 113) ends the parse where it opens, as if the text ended there (probed 2026-10-15 against SQL Server 2025): a statement complete before it reports the lexer's error alone (`select 1 'x`, `select 1 /*x`), while one that needs more — an `AS` awaiting its alias, an operator its operand, a derived table its `)`, a `BEGIN` its `END` — adds the Msg 102 its parser raises there, near the string's body or, for a comment, near the last token before it (`select 1 + /*x` is near `'+'`).
A recovering parser reports the lexer's error after its own (`select 1 in ('x` is Msg 156 then Msg 105).
The parser reads it as `Parser.Tokens.UnterminatedToken`, which is a statement boundary and nothing else, so a parse site that can end there does and one that can't raises on it.

## Bind errors in a deferred statement are catchable here and aren't on real

Real compiles a whole batch before running any of it, so an error the binder raises kills the batch outright — a `TRY` / `CATCH` wrapping the failing statement never reaches the CATCH.
Probe-confirmed for the collation-conflict pair (**Msg 468** / **457**) and for the legacy-LOB argument gate (**Msg 8116**), each raised over an empty rowset inside a `BEGIN TRY`: the batch dies with the error and the CATCH block's `PRINT` never runs.
The simulator compiles the batch first too ([`control-flow.md`](control-flow.md#batch-compilation)), so those match.

A statement naming an object that doesn't exist when the batch compiles binds only when it runs, on real and here alike, and its errors from that late bind are still uncatchable in their own scope and end the batch ([`control-flow.md`](control-flow.md#statement-terminating-vs-batch-aborting-errors-unified-continue-on-error)).
Coverage locking the compiled case: `PredicateCompileTimeBindTests.BindError_IsNotCatchable`.

## Capture design

The static exception factories (`SimulatedSqlException.*Errors.cs`) can't reach the executing batch, so line / procedure are **stamped at the dispatch frame's catch boundary** — the ambient-capture point — rather than being threaded through hundreds of factory signatures.

- **`SimulatedError.Server`** defaults to `SimulatedDbConnection.DataSourceName` (`"simulator"`) at construction — no per-error work.
  Matches the in-process `DataSource` and the info-message path.
- **`SimulatedError.LineNumber` / `.Procedure`** gain an `internal set` (public contract stays get-only, mirroring `SqlError`) so the boundary can stamp them.
- **`SimulatedSqlException.ResolveDiagnostics(baseLine, lineOffset, procedure)`** runs once per exception, guarded by a `diagnosticsResolved` flag so the **innermost** dispatch frame — where the error was born — wins as it propagates outward (matching SQL Server's innermost-frame attribution).
  - `baseLine`: chosen at the boundary in `Simulation.StatementLifecycle.SettleError` — the parser's **current-token line** for severity-15 (syntax) errors, else the failing statement's `StatementContext.StartLine`.
  - `lineOffset`: `BatchContext.LineOffset`, the newline count preceding a procedure/trigger body's start within the batch that created it, so body errors report that batch's line.
    Zero for top-level and dynamic-SQL batches.
  - `procedure`: `BatchContext.ErrorProcedureName` — the invocation's spelling for a stored-procedure body (`p` / `dbo.p`), the **unqualified** name for a trigger body (`tr`, matching real's `ERROR_PROCEDURE()` / `SqlError.Procedure` for triggers) and for **every** module kind's CREATE-time bind batch (see [`programmable.md`](programmable.md#create-time-body-binding)), empty otherwise.
- **Body-type attribution** hinges on which frame stamps.
  Procedures and triggers push their own attribution frame (they set `LineOffset` + `ErrorProcedureName` on the child batch); scalar UDFs, inline TVFs, multi-statement TVFs, and views **inline** — their child batch sets `BatchContext.SuppressDiagnosticsResolution`, so the dispatch catch skips `ResolveDiagnostics` and lets the error propagate unresolved to the enclosing invoking statement's frame (probe-confirmed: real reports the outer statement's line with no procedure, even for a multi-statement TVF's mid-body error).
  A UDF error inside a procedure body therefore attributes to the procedure's calling statement, not the UDF.
  `Procedure.BodyLineOffset` / `Trigger.BodyLineOffset` are each computed once at CREATE (`Simulation.CountNewlines` over `[statement-start, body-start)`).
- **Tokenizer-thrown line** (unclosed string Msg 105, unclosed block comment Msg 113): the parse frontier lags the tokenizer's internal position across a multi-line token, so these two factories carry the line explicitly, computed from the tokenizer's own index via `Token.LineAt`.
  Msg 105 stamps the **opening-quote** line (`ParseQuotedBody`'s captured open index); Msg 113 stamps the **end-of-input** line (`command.Length`), matching real's probed asymmetry.
  A pre-stamped non-zero `SimulatedError.LineNumber` survives `ResolveDiagnostics` (which only fills a zero line), so the enclosing frame leaves it intact.
- **`ERROR_LINE()` / `ERROR_PROCEDURE()`**: the TRY-frame `CaughtError` captures the already-resolved `ex.LineNumber` / `ex.Procedure`, so the CATCH scalars report exactly what the exception carries.
- **`THROW;` re-raise** (`ThrowReRaised`) pre-stamps the in-flight error's captured line + procedure via `PreserveDiagnostics` and marks the exception resolved, so the enclosing frame leaves the preserved line alone.
- **TDS tokens**: `TdsSession.WriteErrors` / `FlushInfoMessages` write `TdsSession.ServerName` (`"SIMULATED"`, = `@@SERVERNAME`) as the token's server field, decoupled from `SimulatedError.Server` (the data source SqlClient surfaces).

### Where the state lives (context layers)

- `BatchContext.LineOffset` / `ErrorProcedureName` — per-body-dispatch context, set on the child batch at `Simulation.InvokeProcedure`.
- `Schemas.Procedure.BodyLineOffset` — computed once at CREATE (`Simulation.CountNewlines` over `[statement-start, body-start)`).
- `StatementContext.StartLine` — the per-statement frame's start line, already captured at dispatch entry (also the exception's baseLine for runtime/bind errors).

## Divergences / residuals

- **Syntax-error recovery where the simulator's parser restarts differently from real's grammar** (probed 2026-09-28 against SQL Server 2025): `begin try end try begin catch select 1 end catch` on one line adds a Msg 102 near the last `catch` on real and nothing here, and the Msg 178 a misplaced `CREATE PROCEDURE`'s valued `RETURN` raises names the procedure on real and nothing here.
  Past a `CREATE FUNCTION`'s Msg 181 (an `OUTPUT` parameter), a `WITH SCHEMABINDING` after the return type is real's Msg 319 and Msg 156 near the body's `BEGIN` here (probed 2026-10-06).
- **Past a Msg 137 the simulator's recovery restarts at the next keyword**, where real's parser reads on in place: `select @a, row_number() over (order by 1) +` adds a Msg 156 near `over` here and nothing on real (probed 2026-10-16 against SQL Server 2025).
- **A table column's name before `NOT FOR REPLICATION`, `GENERATED`, `MASKED` or `ENCRYPTED`** is Msg 156 near `not` or a Msg 2715 for the word read as a type here, where real raises Msg 102 near `not`, near `always`, or Msg 156 near the `WITH` with its Msg 319 (probed 2026-10-16 against SQL Server 2025).
- **`THROW; re-raise inside a proc body`** preserves the original line but not a body-relative offset re-application; top-level re-raise is exact.

Database-scope DDL trigger bodies run through the same child-batch dispatch DML trigger bodies do, so the `LineOffset` / `ErrorProcedureName` threading above covers them too — a body-side `THROW` reports its CREATE-relative line and the trigger's unqualified name (see [`triggers.md`](triggers.md)).
