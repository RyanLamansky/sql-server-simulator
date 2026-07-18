# Error diagnostics: line number, server, procedure

How `SimulatedSqlException` / `SimulatedError` populate the three diagnostic
fields real SqlClient surfaces on every error — `LineNumber`, `Server`,
`Procedure` — plus the coupled `ERROR_LINE()` / `ERROR_PROCEDURE()` scalars and
the TDS ERROR / INFO token fields. All semantics below are probe-confirmed
against SQL Server 2025 (2026-07-18).

## Probed matrix

| Shape | Real line | Notes |
| --- | --- | --- |
| Runtime error (divide-by-zero, conversion) | failing statement's **start** line | not the erroring expression's line — a SELECT spanning lines 3-4 with `5/0` on line 4 reports line 3 |
| Bind error (Msg 208 invalid object) | statement start line | |
| Constraint violation (INSERT/UPDATE) | the DML statement's line | |
| Syntax error (severity 15: Msg 102/156/105/…) | the **offending token's** line | differs from statement start for multi-line statements |
| Two statements on one line | that shared line | |
| `THROW n, m, s` (value form) | the THROW statement's line | |
| `THROW;` (re-raise in CATCH) | the **original** error's line | not the re-raising statement's line |
| Procedure body error | line relative to the whole **CREATE** statement (header lines counted) | + `Procedure = "dbo.<name>"` (schema-qualified) |
| Nested procedure call | **innermost** frame's line + procedure | |
| `EXEC('…')` / `sp_executesql` | line relative to the **dynamic batch** | no `Procedure` |
| PRINT / RAISERROR ≤ 10 (INFO) | statement start line | on `SqlError.LineNumber` |

`Server`: real SqlClient reports the **connection data source** on
`SqlException.Server` / `SqlError.Server` (probe: `localhost,1433`), *not* the
server's `@@SERVERNAME`. The wire ERROR/INFO token's server-name field carries
`@@SERVERNAME` instead — SqlClient ignores it and substitutes the data source;
token-rendering clients (sqlcmd) display it verbatim.

`ERROR_PROCEDURE()` returns the same schema-qualified name as
`SqlError.Procedure` (`dbo.p1`); `ERROR_LINE()` returns the same line the
exception carries.

## Capture design

The static exception factories (`SimulatedSqlException.*Errors.cs`) can't reach
the executing batch, so line / procedure are **stamped at the dispatch frame's
catch boundary** — the ambient-capture point — rather than being threaded
through hundreds of factory signatures.

- **`SimulatedError.Server`** defaults to `SimulatedDbConnection.DataSourceName`
  (`"simulator"`) at construction — no per-error work. Matches the in-process
  `DataSource` and the info-message path.
- **`SimulatedError.LineNumber` / `.Procedure`** gain an `internal set` (public
  contract stays get-only, mirroring `SqlError`) so the boundary can stamp them.
- **`SimulatedSqlException.ResolveDiagnostics(baseLine, lineOffset, procedure)`**
  runs once per exception, guarded by a `diagnosticsResolved` flag so the
  **innermost** dispatch frame — where the error was born — wins as it
  propagates outward (matching SQL Server's innermost-frame attribution).
  - `baseLine`: chosen at the boundary in `Simulation.DispatchOneStatement` —
    the parser's **current-token line** for severity-15 (syntax) errors, else
    the failing statement's `StatementContext.StartLine`.
  - `lineOffset`: `BatchContext.LineOffset`, the newline count preceding a
    procedure body's start within its CREATE text, so body errors report a
    CREATE-relative line. Zero for top-level and dynamic-SQL batches.
  - `procedure`: `BatchContext.ErrorProcedureName`, the schema-qualified name,
    empty outside a stored-procedure body.
- **`ERROR_LINE()` / `ERROR_PROCEDURE()`**: the TRY-frame `CaughtError` captures
  the already-resolved `ex.LineNumber` / `ex.Procedure`, so the CATCH scalars
  report exactly what the exception carries.
- **`THROW;` re-raise** (`ThrowReRaised`) pre-stamps the in-flight error's
  captured line + procedure via `PreserveDiagnostics` and marks the exception
  resolved, so the enclosing frame leaves the preserved line alone.
- **TDS tokens**: `TdsSession.WriteErrors` / `FlushInfoMessages` write
  `TdsSession.ServerName` (`"SIMULATED"`, = `@@SERVERNAME`) as the token's
  server field, decoupled from `SimulatedError.Server` (the data source
  SqlClient surfaces).

### Where the state lives (context layers)

- `BatchContext.LineOffset` / `ErrorProcedureName` — per-body-dispatch context,
  set on the child batch at `Simulation.InvokeProcedure`.
- `Schemas.Procedure.BodyLineOffset` — computed once at CREATE
  (`Simulation.CountNewlines` over `[statement-start, body-start)`).
- `StatementContext.StartLine` — the per-statement frame's start line, already
  captured at dispatch entry (unchanged; now also the exception's baseLine for
  runtime/bind errors).

## Divergences / residuals

- **Tokenizer-thrown errors on a later token line** (unclosed string Msg 105,
  unclosed comment Msg 113): the parse frontier (`batch.Parser.Token`) lags the
  tokenizer's internal position, so a literal opened on line *n* and unclosed at
  EOF reports the prior token's line rather than the literal's. Off by the
  token's internal newline count. Narrow (multi-line unterminated literals).
- **Scalar-UDF / TVF / trigger / view body errors** report a **body-relative**
  line (no `BodyLineOffset`) and carry no `Procedure` — only stored procedures
  thread the offset + qualified name. Real reports a definition-relative line +
  the object name. The line is still non-zero and server correct.
- **`THROW; re-raise inside a proc body`** preserves the original line but not a
  body-relative offset re-application; top-level re-raise is exact.
- `ERROR_PROCEDURE()` / `SqlError.Procedure` are populated only for stored
  procedures (see above).
