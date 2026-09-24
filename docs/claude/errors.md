# Error diagnostics: line number, server, procedure

How `SimulatedSqlException` / `SimulatedError` populate the three diagnostic fields real SqlClient surfaces on every error — `LineNumber`, `Server`, `Procedure` — plus the coupled `ERROR_LINE()` / `ERROR_PROCEDURE()` scalars and the TDS ERROR / INFO token fields.
All semantics below are probe-confirmed against SQL Server 2025.

## Probed matrix

| Shape | Real line | Notes |
| --- | --- | --- |
| Runtime error (divide-by-zero, conversion) | failing statement's **start** line | not the erroring expression's line — a SELECT spanning lines 3-4 with `5/0` on line 4 reports line 3 |
| Bind error (Msg 208 invalid object) | statement start line | |
| Constraint violation (INSERT/UPDATE) | the DML statement's line | |
| Syntax error (severity 15: Msg 102/156/…) | the **offending token's** line | differs from statement start for multi-line statements |
| Unclosed string (Msg 105) | the line the literal **opened** on | even when the body runs across several lines to end of input |
| Unclosed block comment (Msg 113) | the **end-of-input** line the comment ran to | *not* the line it opened on — the one asymmetry from Msg 105 |
| Two statements on one line | that shared line | |
| `THROW n, m, s` (value form) | the THROW statement's line | |
| `THROW;` (re-raise in CATCH) | the **original** error's line | not the re-raising statement's line |
| Procedure body error | line relative to the **batch that created it** — comments and blank lines ahead of the `CREATE` count | + `Procedure` = the name as the invoking `EXEC` spelled it, brackets dropped and case kept (`exec p` → `p`, `exec DBO.P` → `DBO.P`, probed 2026-09-23) |
| Procedure / `sp_executesql` argument that fails to convert | **0** | + `Procedure` for a procedure (Msg 8114, or an xml parse error) |
| Procedure call whose arguments don't bind (Msg 201 / 8144 / 8145) | **0** | + `Procedure` = the name as the `EXEC` spelled it (probed 2026-09-23) |
| `GOTO` to an undeclared label (Msg 133) / a duplicate label (Msg 132) | the `GOTO`'s line / the second label's line | raised while the batch compiles, ahead of anything running (probed 2026-09-23) |
| Trigger body error | creating-batch-relative line | + `Procedure = "<name>"` (**unqualified**) |
| **CREATE-time bind error** (the body error that aborts the CREATE) | batch line | + `Procedure = "<name>"` — **unqualified for every module kind**, procedures included; a `CREATE TRIGGER` naming a missing parent (Msg 8197) is attributed the same way |
| Scalar-UDF / inline-TVF / multi-statement-TVF / view body error | the **outer invoking** statement's line | no `Procedure` — real inlines these for attribution (even the multi-statement TVF) |
| Nested procedure call | **innermost** procedure/trigger frame's line + procedure | a UDF error inside a proc attributes to the **proc's** calling line, not the UDF |
| `EXEC('…')` / `sp_executesql` | line relative to the **dynamic batch** | no `Procedure` |
| PRINT / RAISERROR ≤ 10 (INFO) | statement start line | on `SqlError.LineNumber` |

`Server`: real SqlClient reports the **connection data source** on `SqlException.Server` / `SqlError.Server` (probe: `localhost,1433`), *not* the server's `@@SERVERNAME`.
The wire ERROR/INFO token's server-name field carries `@@SERVERNAME` instead — SqlClient ignores it and substitutes the data source; token-rendering clients (sqlcmd) display it verbatim.

`ERROR_PROCEDURE()` returns the same name as `SqlError.Procedure`; `ERROR_LINE()` returns the same line the exception carries.
A `PRINT` or low-severity `RAISERROR` in a procedure body carries the procedure too.

## The message stream

Every informational message — `PRINT`, a severity-0-10 `RAISERROR`, and the engine's own (Msg 3621, 8153, 5701, 5703, 11729, the procedures' severity-10 texts) — is a `SimulatedInfoOutcome` in the outcome stream, placed where real sends its INFO token.
The engine queues one on `SimulatedDbConnection.PendingMessages` as it happens, and the dispatch loop places the queue ahead of the statement's own outcomes.
Probed through SqlClient 7 against SQL Server 2025 (2026-09-23):

- **One event per message**, fired as the reader reaches it: during `ExecuteReader` for what precedes the first result set, during `NextResult` for what follows.
- **Severity 10 arrives as class 0**; severities 1-9 keep their number.
- **An error carries the messages of its stretch of the batch.**
  `ExecuteNonQuery` / `ExecuteScalar` read the whole batch and, if anything failed, throw one exception holding every error and then every message, in order — a `PRINT` ahead of the first error included.
  `ExecuteReader` fires the messages ahead of its error as events and throws the error with the messages after it, up to the next result set.
  `NextResult` and `Read` throw the error alone; what follows fires on the next advance.
  `Message` joins every entry with `Environment.NewLine`, as SqlClient's does.
- **Msg 3621** (`The statement has been terminated.`, class 0, state 0) follows an execution error that ends a row-writing statement — `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `SELECT … INTO`, and `ALTER TABLE … ALTER COLUMN`'s rewrite — but not a compilation error (Msg 206 / 213 / 544), a `SELECT`'s own error, a batch-ending one (a conversion failure, anything under `XACT_ABORT ON`) or one a `TRY` / `CATCH` handles.
  Which numbers count is an explicit list (`Simulation.IsStatementTerminationNoticed`); it goes out after the error's DONE, which is why `NextResult` doesn't carry it.
- **Msg 8153** (`Warning: Null value is eliminated by an aggregate or other SET operation.`) goes out once per statement whose aggregate skipped a NULL with `ANSI_WARNINGS` on, after the rows and before the statement's DONE — ahead of the body for an `IF` / `WHILE` condition.
  Every aggregate warns but `COUNT(*)`, `STRING_AGG` and the JSON aggregates, window aggregates and a scalar subquery's included; an `EXISTS` body's and a `PIVOT`'s don't.
- **Msg 5701** follows every `USE`, even of the current database, and **Msg 5703** every `SET LANGUAGE`.

### Not modeled yet

- **Msg 5703 is English whatever the language**; real words it in the language being switched to (`Die Spracheneinstellung wurde in Deutsch geändert.`).
- **Msg 8153 over a constant `VALUES` source grouped into single-row groups** isn't sent by real (`SELECT x, SUM(y) FROM (VALUES (1, NULL), (2, 3)) v(x, y) GROUP BY x`), which evaluates those groups while compiling; the same data in a table warns on both.

## Bind errors in a deferred statement are catchable here and aren't on real

Real compiles a whole batch before running any of it, so an error the binder raises kills the batch outright — a `TRY` / `CATCH` wrapping the failing statement never reaches the CATCH.
Probe-confirmed for the collation-conflict pair (**Msg 468** / **457**) and for the legacy-LOB argument gate (**Msg 8116**), each raised over an empty rowset inside a `BEGIN TRY`: the batch dies with the error and the CATCH block's `PRINT` never runs.
The simulator compiles the batch first too ([`control-flow.md`](control-flow.md#batch-compilation)), so those match.

A statement naming an object that doesn't exist when the batch compiles binds only when it runs, on real and here alike, and there the two part: real's error from that late bind is still uncatchable in its own scope and ends the batch, while here it is an ordinary run-time error a `TRY` catches (`BEGIN TRY SELECT * FROM nope END TRY …` reaches its CATCH here).
Coverage locking the compiled case: `PredicateCompileTimeBindTests.BindError_IsNotCatchable`.

## Capture design

The static exception factories (`SimulatedSqlException.*Errors.cs`) can't reach the executing batch, so line / procedure are **stamped at the dispatch frame's catch boundary** — the ambient-capture point — rather than being threaded through hundreds of factory signatures.

- **`SimulatedError.Server`** defaults to `SimulatedDbConnection.DataSourceName` (`"simulator"`) at construction — no per-error work.
  Matches the in-process `DataSource` and the info-message path.
- **`SimulatedError.LineNumber` / `.Procedure`** gain an `internal set` (public contract stays get-only, mirroring `SqlError`) so the boundary can stamp them.
- **`SimulatedSqlException.ResolveDiagnostics(baseLine, lineOffset, procedure)`** runs once per exception, guarded by a `diagnosticsResolved` flag so the **innermost** dispatch frame — where the error was born — wins as it propagates outward (matching SQL Server's innermost-frame attribution).
  - `baseLine`: chosen at the boundary in `Simulation.DispatchOneStatement` — the parser's **current-token line** for severity-15 (syntax) errors, else the failing statement's `StatementContext.StartLine`.
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

- **`THROW; re-raise inside a proc body`** preserves the original line but not a body-relative offset re-application; top-level re-raise is exact.

Database-scope DDL trigger bodies run through the same child-batch dispatch DML trigger bodies do, so the `LineOffset` / `ErrorProcedureName` threading above covers them too — a body-side `THROW` reports its CREATE-relative line and the trigger's unqualified name (see [`triggers.md`](triggers.md)).
