# Programmable objects — UDFs, TVFs, views

## Scalar user-defined functions
`CREATE FUNCTION schema.name(@p type [= default], ...) RETURNS <type> [WITH RETURNS NULL ON NULL INPUT] AS BEGIN ... END`, called as `SELECT schema.fn(args)`. Body source captured between outer `BEGIN`/`END` (BEGIN TRAN/TRANSACTION/DISTRIBUTED skipped during nesting) and re-tokenized per call; parameters seed a child `BatchContext.Variables`, value-form RETURN lands in `BatchContext.UdfFrame.ReturnedValue`. Probed against SQL Server 2025 (2026-05-11).

- **2-part-name required.** Bare `fn(x)` → **Msg 195** ("not a recognized built-in function name") — real SQL Server treats unqualified UDF calls as built-in misses. Schema-qualified miss → **Msg 4121** ("Cannot find either column or the user-defined function or aggregate").
- **BEGIN/END required.** Inline `as return @x*10` → Msg 102.
- **Body content**: `DECLARE` / `SET` / `SELECT @v = expr` / `IF` / `WHILE` / `BEGIN…END` / `BREAK` / `CONTINUE` / `RETURN <value>`. Variables declared inside are function-scoped.
- **`RETURN <value>`** legal only inside a UDF body — outside raises **Msg 178** at parse time; inside, value coerces to declared return type.
- **Arity errors**: too few → **Msg 313**; too many → **Msg 8144**.
- **DEFAULT keyword required for omission.** `fn()` raises Msg 313 even when every parameter has a declared default — the `DEFAULT` keyword is the only legal omission (re-evaluated per call in the child batch).
- **WITH RETURNS NULL ON NULL INPUT**: any non-DEFAULT NULL arg short-circuits the body and returns typed NULL.
- **Recursion cap: 32.** Tracked by `SimulatedDbConnection.NestingLevel`; exceeding → **Msg 217**. Shared with future stored procs / triggers / views.
- **Catalog surface**: `sys.objects` `type='FN'` / `type_desc='SQL_SCALAR_FUNCTION'`. `sys.parameters` emits one row per declared parameter (parameter_id 1+, `is_output=0`) plus a `parameter_id=0` return-type row (empty name, `is_output=1`). `max_length` is 0 across the board — pending wiring `GetSysColumnMetadata` to bare `SqlType`.
- **`OBJECT_ID(name, 'FN')`** routes to function resolution; no-filter form tries function then table. Other codes (V / P / TF / ...) return NULL pending those features.
- **DROP FUNCTION [IF EXISTS] schema.name[, ...]**: same shape as DROP TABLE; missing target → **Msg 3701** with "function" wording variant.

**Fidelity gaps**:
- **No CREATE-time body validation** (Msg 455 missing-RETURN, Msg 443 side-effects, Msg 444 result-set SELECT in body). Deferred to runtime — fall-through body returns typed NULL, side-effecting statements surface their own errors.
- **No Msg 111 batch-first enforcement** — `IF OBJECT_ID(…) IS NOT NULL DROP FUNCTION …; CREATE FUNCTION …` works here, real SQL Server requires separate batches (no `GO` support).
- **`WITH SCHEMABINDING` / `ENCRYPTION` / `EXECUTE AS`** → `NotSupportedException`.
- **`@@ROWCOUNT` inside a UDF body** isn't isolated — body statements overwrite the caller's `LastStatementRowCount`. Real SQL Server preserves it across the call.

## Inline table-valued functions
`CREATE FUNCTION schema.name(@p type [= default], ...) RETURNS TABLE [WITH SCHEMABINDING | ENCRYPTION] AS RETURN [(] <SELECT> [)]`, called from a FROM clause. Stored as `InlineTableValuedFunction` alongside `ScalarFunction` under the abstract `UserDefinedFunction` base in `Schema.Functions`. Body re-parsed per call inside a child `BatchContext` with parameters seeded as variables, returned as a synthetic `Selection.ForInlineTvf` wrapped in a `FromSource.LateralPlan`. Same 32-level recursion cap (Msg 217) as scalar UDFs. Probed against SQL Server 2025 (2026-05-12).

- **Body grammar**: exactly one SELECT. Parens optional. Multi-statement inside parens → Msg 102.
- **WITH-clause options**: `SCHEMABINDING` / `ENCRYPTION` parse-and-ignore. `RETURNS NULL ON NULL INPUT` → **Msg 487** (scalar-only).
- **CREATE-time validation**: body parses once with parameters seeded as typed variables; `OutputColumns` derives from the resulting projection. Unnamed column → **Msg 4514** (distinct from SELECT INTO's Msg 1038). Duplicate column name → **Msg 4506** (distinct from SELECT INTO's Msg 2705).
- **Argument parsing** shares `UserFunctionCall.ParseFunctionArguments` with scalar UDFs — same `DEFAULT` rule (omission → Msg 313), same Msg 313 / 8144 arity errors.
- **Kind-vs-position routing**: `ScalarFunction` in FROM → **Msg 208** (treated as missing-object, not kind-mismatch). `InlineTableValuedFunction` in expression position → **Msg 4121** through the existing factory.
- **CROSS APPLY / OUTER APPLY**: right side must be a parenthesized derived table OR an inline TVF. `ParseLateralFromSource` peeks; bare table after APPLY → syntax error (matching real SQL Server).
- **Catalog surface**: `sys.objects` `type='IF'` / `type_desc='SQL_INLINE_TABLE_VALUED_FUNCTION'`. `sys.columns` emits one row per output column (`is_identity=0`, `is_computed=0`). `sys.parameters` skips the `parameter_id=0` return-row (the TABLE shape lives in sys.columns). `OBJECT_ID(name, 'IF')` resolves TVFs only; `'FN'` filter no longer matches; no-filter form tries both kinds.

**Fidelity gaps**:
- **`is_nullable` always True** in `sys.columns` for TVF output. Real SQL Server propagates per-projection nullability via the same rules SELECT INTO uses (`Expression.ResultIsNullable`); wiring it through requires exposing projection expressions on `Selection` post-parse. Apps reading TVF rows through raw SQL aren't affected.
- **No SCHEMABINDING enforcement** — DROP TABLE on a TVF-referenced table succeeds (real SQL Server raises Msg 3729); the TVF later fails at call time when re-parsing.
- **No CREATE-time body validation for forward refs** — self-recursive inline TVFs fail at CREATE with Msg 208 (real SQL Server also rejects, different error path).
- **No Msg 111 batch-first enforcement** (same as scalar UDFs).
- **Multi-statement TVFs** (`RETURNS @t TABLE ... BEGIN ... END`, `type='TF'`) not modeled.

## Views
`CREATE VIEW schema.name [(col_list)] [WITH SCHEMABINDING | ENCRYPTION | VIEW_METADATA] AS <SELECT> [WITH CHECK OPTION]`, referenced from FROM as `FROM schema.view [alias]` (or unqualified `FROM view`). Stored as `View` in `Schema.Views`. Body re-parsed per call inside a child `BatchContext`, returned as `Selection.ForView` wrapped in a `FromSource.LateralPlan`. Same 32-level recursion cap (Msg 217) as scalar UDFs / inline TVFs. Probed against SQL Server 2025 (2026-05-12).

- **Body grammar**: a single SELECT (CTE-prefixed bodies via `WITH cte AS (...) SELECT ...` work — the body parse runs at depth 0). ORDER BY without TOP / OFFSET / FETCH → **Msg 1033** (same factory CTE bodies use).
- **Column-rename list**: `CREATE VIEW v(a, b) AS SELECT ...` renames the projection. Count mismatch → **Msg 8158** (too few listed) / **Msg 8159** (too many) — shared factories with CTE rename lists.
- **CREATE-time validation**: body parses once to derive `OutputColumns`. Unnamed projection → **Msg 4511** (distinct from inline TVF's Msg 4514 and SELECT INTO's Msg 1038 — different wording too: `"Create View or Function failed because no column name was specified for column N."`). Duplicate column name → **Msg 4506** (shared with inline TVFs).
- **WITH-clause options**: `SCHEMABINDING` / `ENCRYPTION` / `VIEW_METADATA` parse-and-ignore. **`WITH CHECK OPTION`** (trailing the body) parses and records on `View.WithCheckOption` but isn't enforced (only matters for DML, which v1 doesn't model).
- **Routing in expression position**: a view name used as a scalar value raises **Msg 4104** (`"The multi-part identifier '...' could not be bound."`), NOT Msg 4121 — views look like tables to the expression parser.
- **Unqualified names work**: `FROM v1` falls back to `dbo.v1` (probe-confirmed real SQL Server accepts both).
- **Catalog surface**:
  - `sys.objects` `type='V '` (char(2) padded) / `type_desc='VIEW'`.
  - `sys.views` (load-bearing subset): `object_id`, `name`, `schema_id`, `with_check_option`, `is_date_correlation_view` (always False).
  - `sys.columns` emits one row per output column (`is_identity=0`, `is_computed=0`; `is_nullable` always True — same fidelity gap as inline TVFs).
  - `INFORMATION_SCHEMA.VIEWS` (full ISO 6-col shape): `VIEW_DEFINITION` surfaces the stored body text; `CHECK_OPTION` is `'CASCADE'` / `'NONE'`; **`IS_UPDATABLE` always `'NO'`** — probe-confirmed real SQL Server hardcodes this regardless of actual updatability.
  - `INFORMATION_SCHEMA.TABLES` includes views with `TABLE_TYPE='VIEW'`.
  - `OBJECT_ID(name, 'V')` resolves views only; no-filter form falls through both functions and views before tables.
- **DROP VIEW [IF EXISTS] schema.name[, ...]**: same shape as DROP TABLE / DROP FUNCTION; missing target → **Msg 3701** with `view` wording variant.
- **EF Core integration**: `ToView()` mapping works end-to-end. Keyless entities (`HasNoKey().ToView("name")`) project rows from CREATE VIEW-produced views; the simulator's per-call body re-parse handles correlated LINQ-emitted WHERE clauses against the view's projection.

**Fidelity gaps**:
- **No SCHEMABINDING enforcement** — DROP TABLE on a view-referenced table succeeds (real SQL Server raises Msg 3729); the view later fails at call time when re-parsing against the missing name.
- **`VIEW_DEFINITION` always surfaces body text** even for WITH ENCRYPTION views (real SQL Server returns NULL for ENCRYPTION views).
- **`is_nullable` always True** in `sys.columns` for view output — same gap as inline TVFs.

## Updatable views (DML through views)
INSERT / UPDATE / DELETE through a view route to the view's eventual base `HeapTable` with view-aware column-name translation, visibility filtering, and (optional) WITH CHECK OPTION enforcement. Captured at CREATE VIEW time via `AnalyzeViewUpdatability` (in `Simulation.CreateView.Updatability.cs`) onto five `View` fields: `BaseTable` / `BaseColumnOrdinals` / `RejectionReason` / `VisibilityCheck` / `CheckOptionCheck`. Probed against SQL Server 2025 (2026-05-12).

**Eligible shape** (each level in a view-on-view chain must satisfy all):
- Exactly one FROM source (a heap table OR another updatable view).
- No JOINs, no DISTINCT, no aggregates, no GROUP BY, no HAVING, no window functions, no set-op chain. TOP / OFFSET / FETCH / ORDER BY are allowed (they only affect reads).
- Every column referenced in any WHERE clause up the chain maps to a real base-table column (no WHERE that references an upstream derived projection).

Selection-side capture is `Selection.UpdatabilityProfile` (set in `BuildSqlProjection` when shape-eligible) + `Selection.UpdatabilityRejection` (drives Msg 4403 vs Msg 4405 vs Msg 4406 at the DML site). `FromSource.BackingView` mirrors `FromSource.BackingTable` so the chained-view analyzer can recurse to find the eventual base.

**Per-output-column updatability**: `View.BaseColumnOrdinals[i]` is the base-table ordinal for column `i`, or `-1` when the projection is a derived expression (anything other than a `Reference` possibly wrapped in `NamedExpression` from `AS alias`). The map composes through view-on-view chains so a renamed column at level 1 referenced through level 2 still resolves to its base ordinal.

**DML routing**:
- **INSERT**: `ProcessViewInsert` validates `BaseTable` is non-null (else Msg 4403 / Msg 4405 by `RejectionReason`), then routes to `ProcessHeapInsert(baseTable, context, destinationView)`. Column-name lookups in the explicit list translate through `BaseColumnOrdinals`; touching a `-1` ordinal raises **Msg 4406**. Implicit column list (no `(cols)` after view name) expands to the view's writable projection columns mapped to base ordinals — derived columns and computed/identity/rowversion base columns drop out, defaults fire normally for unlisted base columns.
- **UPDATE**: `ParseUpdate` resolves the leading identifier as a view, threads `leadingView` into `ResolveSetAssignments` (view-name → base-ordinal translation, same Msg 4406 path) and `ExecuteUpdateAgainstTable`. The heap scan gates `VisibilityCheck` before the user's WHERE — UPDATE through a filtered view only affects rows visible through the view. WHERE column references resolve against view's output columns first (so an UPDATE against a renamed view uses the rename, not the base name).
- **DELETE**: `ParseDelete` mirrors the same shape. Same visibility filter + column-name remap.

**WITH CHECK OPTION** (Msg 550): a per-row post-construction check that fires after row construction (INSERT) or post-update value computation (UPDATE) and before the heap write. The closure on `View.CheckOptionCheck` AND's together every CHECK OPTION-bearing level's visibility (= that level's WHERE composed with all upstream WHEREs) up the chain — so a chained view-on-view with CHECK OPTION at one or both levels enforces both correctly. Cascade behavior: a CHECK OPTION at any level "spans" the upstream views, matching SQL Server's `"or spans a view that specifies WITH CHECK OPTION"` wording. DELETE never fires Msg 550 (a row leaving the view is fine).

**Errors** (all probe-confirmed verbatim against SQL Server 2025):
- **Msg 4403**: INSERT / UPDATE / DELETE through a view with aggregate / DISTINCT / GROUP BY. Body of the message names the view (`"Cannot update the view or function 'dbo.v' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator."`).
- **Msg 4405**: INSERT / UPDATE / DELETE through a JOIN view. **Simulator divergence**: real SQL Server allows single-base-table UPDATEs through JOIN views; the simulator rejects uniformly here (deferred to a follow-up bundle).
- **Msg 4406**: INSERT or UPDATE touched a derived projection column. Per-column gate — a view with mixed direct + derived columns accepts INSERT/UPDATE on the direct columns and DELETE through it works fine.
- **Msg 550**: WITH CHECK OPTION violation (covers chain spans).

**Catalog surface**: unchanged — `INFORMATION_SCHEMA.VIEWS.IS_UPDATABLE` stays hardcoded `'NO'` (probe-confirmed real SQL Server always reports `'NO'` here regardless of actual updatability, so the existing surface is correct).

**No-WITH-CHECK-OPTION quirk** (probe-confirmed): a filtered view without WITH CHECK OPTION accepts INSERTs that produce rows outside its WHERE, and UPDATEs that move rows out of view. The row lands in the base; the view's WHERE only filters reads. The simulator preserves this — `VisibilityCheck` gates UPDATE/DELETE *row selection* (which rows to mutate), not INSERT acceptance.

**Fidelity gaps**:
- **JOIN-view single-base UPDATE/DELETE** rejected with Msg 4405 — real SQL Server permits these when the modification affects exactly one base table. EF Core doesn't emit this shape; apps that hand-write it surface Msg 4405 prematurely.
- **OUTPUT through a view** raises `NotSupportedException` for INSERT / UPDATE / DELETE. Would need view-output-column rebinding for INSERTED.* / DELETED.* projection.
- **Multi-source UPDATE / DELETE** (alias-form `UPDATE alias SET ... FROM ...` where the alias resolves to a view) raises `NotSupportedException` — the alias-form FROM clause can't compose with the view's visibility predicate in the existing joined-update infrastructure.
- **WHERE referencing a derived upstream column** (a chained view's WHERE that references an expression-projected column from the level below) marks the view as not-updatable with `ViewUpdatabilityRejection.UnsupportedShape` → Msg 4403 at DML. Real SQL Server's behavior on this specific niche shape isn't probe-confirmed; the simulator errs on the side of rejection.

## Stored procedures
`CREATE [OR ALTER] PROCEDURE schema.name [(@p type [= default] [OUTPUT], ...)] [WITH options] AS body` lives in `Schema.Procedures`. Body source captured from `AS` to end-of-batch (BEGIN/END is optional — probe-confirmed), re-tokenized per call inside a child `BatchContext` with parameters seeded as variables and a `ProcFrame` carrying the return-code slot. EXEC's result sets propagate to the outer caller (distinct from scalar UDFs, which discard). Same 32-level recursion cap (Msg 217) shared with UDFs / views via `SimulatedDbConnection.NestingLevel`. Probed against SQL Server 2025 (2026-05-12).

**Grammar**:
- **Body capture**: from the first token after `AS` to end-of-batch, with empty bodies legal (`CREATE PROC p AS` with nothing after `AS` succeeds — probe-confirmed; the per-call invocation short-circuits when `BodyText` is empty so the parser doesn't reject empty `CommandText`).
- **Parens around parameter list optional**: `CREATE PROC p (@x int)` and `CREATE PROC p @x int` are equivalent.
- **WITH options** (`RECOMPILE`, `ENCRYPTION`, `EXECUTE AS CALLER|SELF|OWNER|'name'`, `FOR REPLICATION`) parse-and-ignore — the simulator doesn't model query-planner / security / replication semantics.
- **`CREATE OR ALTER`** is an upsert: creates when missing, replaces when present (preserving `Procedure.ObjectId`).
- **Bare `CREATE PROC` on existing name** raises **Msg 2714** (same factory as duplicate CREATE TABLE).
- **Bare `ALTER PROC` on missing name** raises **Msg 208** (NOT Msg 3701 — distinct from DROP).
- **`DROP PROCEDURE [IF EXISTS] schema.name[, name...]`**: comma-list form; missing target → **Msg 3701** with `"Cannot drop the procedure 'X', …"` wording variant (`CannotDropProcedureDoesNotExist` factory).
- **No Msg 111 batch-first enforcement** — real SQL Server requires CREATE/ALTER PROCEDURE to be the first statement in a query batch; the simulator (no `GO` support) doesn't enforce, matching the UDF / view stance.

**EXEC statement grammar** (`Simulation.Exec.cs`):
- **`EXEC [@rc =] target [args]`** where `target` is a procedure name (or `( <string-expr> )` for dynamic SQL). The optional `[@rc = ]` between EXEC and the proc name captures the return code into a caller variable — probe-confirmed position (NOT `@rc = EXEC ...` at statement start).
- **Argument forms**: positional / named (`@p = value`) / mixed positional-then-named. Once any named arg appears, every following arg must also be named (Msg 119). Each argument value must be a literal (numeric / string / NULL), an `@variable` (with optional `OUTPUT`/`OUT` suffix), or the `DEFAULT` keyword — probe-confirmed: arithmetic expressions like `EXEC p @x - 1` raise Msg 102 at parse.
- **Unqualified names work**: `EXEC p1` resolves to `dbo.p1` (probe-confirmed; matches view-routing relaxation).
- **EXEC missing proc** → **Msg 2812** (`"Could not find stored procedure 'X'."`) — distinct error from Msg 208 / 3701; State 62 verbatim.
- **EXEC in expression position** (`SELECT EXEC p`) → Msg 156 via the standard non-statement-start path.

**Error matrix at EXEC**:
- **Msg 201** (`"Procedure or function 'X' expects parameter '@Y', which was not supplied."`) for an unknown named arg OR a missing required parameter (no default). State 4.
- **Msg 8143** (`"Parameter '@X' was supplied multiple times."`) for duplicate named args.
- **Msg 8144** (`"Procedure or function X has too many arguments specified."`) for too many positional args (same factory as scalar UDFs, but the proc name renders without single-quote wrapping in real SQL Server; the simulator's existing factory is close enough).
- **Msg 119** (mixing named-then-positional) — verbatim wording probe-confirmed.

**OUTPUT parameters**:
- Procedure parameters declared with `OUTPUT` / `OUT` get `ProcedureParameter.IsOutput = true`; the EXEC argument's optional `OUTPUT` keyword binds the caller's `@variable` slot (captured live, not by value) into `ProcArgument.OutputSlot`.
- At proc exit, OUTPUT-declared params whose call-site also passed OUTPUT copy the child batch's final variable value back to the caller's slot. **Probe-confirmed quirks**:
  - Caller that omits `OUTPUT` on an OUTPUT-declared parameter: writeback is suppressed (caller's variable retains its pre-EXEC value).
  - Output param when proc throws after writing: the partial write is preserved (caller sees the mid-proc value).
- For `CommandType.StoredProcedure` callers (`SimulatedDbCommand.CommandType = StoredProcedure`), parameters with `ParameterDirection.Output` / `InputOutput` writeback to `DbParameter.Value` at end-of-call; `ParameterDirection.ReturnValue` captures the proc's return code (default 0).

**RETURN semantics**:
- Bare `RETURN` exits the procedure early; subsequent statements in the body don't execute (propagates through `IF` / `BEGIN…END` / `WHILE` via `BatchContext.ReturnSignaled`, same plumbing as bare-batch RETURN).
- `RETURN <expr>` evaluates the expression, coerces to `int`, lands in `ProcFrame.ReturnCode`. **Probe-confirmed: `RETURN NULL` yields 0 in the caller's `@rc`** — NULL coerces to 0 in this slot specifically (NOT propagated as `DBNull`), distinct from how NULL flows through other expression contexts.
- `RETURN 'abc'` (non-coercible string) raises **Msg 245** at the proc body's RETURN statement.
- Default return code (no explicit RETURN) is **0**.
- Value-form RETURN is also legal inside scalar UDF bodies (existing); the parse-time check now accepts either `BatchContext.UdfFrame` or `BatchContext.ProcFrame` being non-null.

**Multi-result-set forwarding**: a procedure body's `SELECT` statements yield result sets through the outer caller's iterator (`ExecuteReader().NextResult()` walks them). Unlike UDF bodies, the proc invocation iterates `DispatchStatementsUntil` and yields each outcome. Output parameter values populate AFTER reader close — probe-confirmed: real SQL Server holds OUTPUT param values until the response stream's done message, which `SimulatedDbDataReader` mirrors via the standard ADO.NET timing.

**Recursion**: each proc call increments `SimulatedDbConnection.NestingLevel`; entering a body at the cap raises Msg 217 (verbatim same wording as scalar UDFs / views). `@@NESTLEVEL` reads the counter as int.

**`CommandType.StoredProcedure` entrypoint**: `SimulatedDbCommand.CommandType` accepts `StoredProcedure`; on execute, `CreateResultSetsForCommand` short-circuits the parser path and routes directly to `InvokeProcedure` with arguments translated from `DbParameterCollection`. Each `DbParameter` binds to a proc parameter by name (the `@` prefix is stripped if present); `ParameterDirection.Output` / `InputOutput` writeback paths and the optional `ParameterDirection.ReturnValue` capture mirror the EXEC-text behavior.

**Catalog surface**:
- `sys.objects` `type='P '` (char(2) trailing-space padded) / `type_desc='SQL_STORED_PROCEDURE'`.
- `sys.procedures` (load-bearing subset): `object_id`, `name`, `schema_id`, `type`, `type_desc`, `create_date`, `modify_date`, `is_ms_shipped`.
- `sys.parameters` emits one row per declared parameter (parameter_id 1+); no `parameter_id=0` row (distinct from scalar UDFs — proc has no return-type slot in this view). `is_output` reflects the `OUTPUT`/`OUT` declaration.
- `INFORMATION_SCHEMA.ROUTINES` (5-col subset): `ROUTINE_TYPE='PROCEDURE'`, `DATA_TYPE=NULL` for procs.
- `INFORMATION_SCHEMA.PARAMETERS` (8-col subset): `PARAMETER_MODE='IN'`/`'INOUT'` (no `'OUT'`-only — procedures always reflect OUTPUT as INOUT, probe-confirmed).
- `OBJECT_ID(name, 'P')` resolves procedures only; no-filter form tries function → view → procedure → table in order.

**Fidelity gaps**:
- **`sys.parameters.has_default_value`** is hardcoded `False` (matches probed real SQL Server behavior: the column reflects CLR-side DEFAULT_VALUE metadata, not the `= value` parameter default — even `@x int = 5` shows `has_default_value=False`).
- **`sys.procedures.modify_date`** mirrors `create_date` (real SQL Server bumps `modify_date` on each ALTER; the simulator preserves the original create_date through ALTER but doesn't track separate modify timestamps).
- **No Msg 111 batch-first enforcement** (same gap as scalar UDFs / views).
- **EXEC argument value-grammar limited to literals + `@var` + `DEFAULT`** — matches real SQL Server (Msg 102 on arithmetic), but the *type* of the literal is taken from the source token, not coerced through any inference like real SQL Server's procedure-call binding.
- **`@@ROWCOUNT` inside a proc body** isn't isolated from the caller — same gap documented for UDF bodies.
- **`OUTPUT` parameter timing**: the simulator's `SimulatedDbDataReader` populates output `DbParameter.Value` after the reader closes (via the synthesized `WriteBackOutputParameters` path), matching real SqlClient's general behavior; pre-close access reads the pre-EXEC value.
- **No `WITH RESULT SETS`** (EXEC option to override result-set schema). Parses fall through to syntax error.
- **No `INSERT ... EXEC`** wiring through the catalog yet (the `INSERT` parser doesn't recognize `EXEC` as a row source). Apps that emit this surface Msg 102.

## Dynamic SQL (`EXEC (@sql)` / `sp_executesql`)
Two re-tokenizing paths in `Simulation.ExecDynamicSql.cs`. Both run the dynamic batch inside its own child `BatchContext` (`ProcFrame` set for `RETURN` legality but the return code is discarded), share the outer connection's database / transaction state, and forward result sets to the outer caller. **Outer `@`-variables are NOT visible** — probe-confirmed: a dynamic batch referencing an undeclared `@x` raises Msg 137.

**`EXEC (<string-expr>)`**:
- Operand evaluates in the outer batch's context (so `EXEC ('SELECT ' + @col + ' FROM t')` works), then the resulting string is dispatched as a fresh batch.
- NULL string operand → silent no-op (matches real SQL Server's permissive handling).
- The dynamic-SQL form doesn't expose a meaningful return code; `@rc = EXEC ('...')` writes 0 unconditionally.

**`EXEC sp_executesql N'sql', N'@p1 type [OUTPUT], ...', @p1 = value, @p2 = @callervar OUTPUT, ...`**:
- First argument is the SQL text; second (optional) is a parameter-declaration string parsed by `ParseSpExecuteSqlParamDefinitions` (mini-parser: `@name type [OUTPUT]` entries, comma-separated).
- Remaining arguments bind values to declared params (positional or named); `OUTPUT` keyword on an `@variable`-valued arg writes the dynamic batch's final variable value back to the caller's slot at exit.
- The pre-declared `@`-variables exist as the dynamic batch's own `Variables` dict — they don't leak into the outer scope.
- Probe-confirmed: `sp_executesql` works with no parameters (`EXEC sp_executesql N'SELECT 42'`).
