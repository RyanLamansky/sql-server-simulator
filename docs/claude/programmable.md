# Programmable objects — UDFs, TVFs, views

## CREATE-time body binding

A module's body is **bound when the module is created**, and a binder error aborts the `CREATE` — the module isn't created, and an `ALTER` / `CREATE OR ALTER` leaves the previous body standing.
Only *missing-object* resolution defers, which is real's deferred name resolution.
Probe-confirmed end to end against SQL Server 2025 (2026-08-01) for procedures, scalar UDFs, multi-statement TVFs and DML / DDL triggers; views and inline TVFs never deferred anything (real binds them fully, Msg 208 included) and already did so here through their output-column inference.

`Simulation.BindModuleBodyAtCreate` (in `Simulation.BindModuleBody.cs`) is the one implementation.
It re-tokenizes the captured body on a throwaway child `BatchContext` built by the same per-kind constructor the invocation uses — so the body sees the frame it will run under — and runs it through the normal dispatch loop with **skip mode** on (`BatchContext.SkipModeFlag`) plus `BatchContext.CreateTimeBinding`.
Skip mode is the existing "parse and resolve, don't execute" machinery the un-taken-`IF` path uses ([`control-flow.md`](control-flow.md)); `CreateTimeBinding` adds the behaviors specific to binding — permission enforcement off (a bind reads nothing, and real binds under the module's own ownership chain), stop-at-first-deferral (below), and the **`SET`-option gates off** (real accepts `CREATE PROCEDURE … AS INSERT <gated table>` under `QUOTED_IDENTIFIER OFF` and raises Msg 1934 only when the body runs, though a never-taken `IF` branch at top level *does* raise — see [`grammar.md`](grammar.md#set-option-gates--msg-1934--msg-1935)).

The bind runs under the **creating** session's `QUOTED_IDENTIFIER`, which is also the setting the module captures, so the text is read the same way here as it will be at every later invocation.
Per-call re-tokenization then swaps the session flag to that capture for the body's duration ([`grammar.md`](grammar.md#per-object-creation-time-capture)) — the reason a module body is immune to whatever the caller set.

Each kind seeds the bind batch the way its invocation would:

| Kind | Seeded | Frame |
| --- | --- | --- |
| Procedure | parameters as typed NULL slots; a TVP parameter as an empty **READONLY** clone of its type; a cursor parameter as an unallocated cursor variable | `ProcFrame` |
| Scalar UDF | parameters as typed NULL slots | `UdfFrame` (what makes value-form `RETURN` legal) |
| Multi-statement TVF | parameters, plus the `@r` return table | **none** — the absence is what raises Msg 178 on a value-form `RETURN`, which real also reports at CREATE |
| DML trigger | empty `INSERTED` / `DELETED` shaped like the parent | `TriggerFrame` over a stand-in `Trigger` (object id 0, never registered) carrying the parent, which `UPDATE(col)` resolves against |
| DDL trigger | — | `TriggerFrame` over a stand-in `DdlTrigger`, empty `EVENTDATA()` |

**Ordering**: the bind runs before the schema dict is touched and before the name-collision gates, matching real — probe-confirmed that a body error beats Msg 2714 for a plain `CREATE` over a taken name and Msg 208 for a bare `ALTER` of a name nothing holds.
`CREATE TRIGGER` is the one exception in the other direction: real reports **Msg 8197** for a missing parent *ahead* of the body error, so parent resolution stays first.

**Error shape**: severity / state as usual, the body's **CREATE-relative line** (Msg 111 forces the CREATE to open its batch, so that is also real's batch-relative line), and the module's **unqualified** name as `Procedure` / `ERROR_PROCEDURE()` — `pshape`, not `dbo.pshape`.
That is the opposite of the schema-qualified attribution an *invocation*-time procedure-body error carries; see [`errors.md`](errors.md).
The error is ordinary and catchable — a `CREATE` issued through dynamic SQL inside `TRY` / `CATCH` lands in the CATCH.

### What defers

The statement's whole binding defers as soon as any object in it is missing — real's rule, and the simulator reaches it two ways.
A FROM-clause table or schema-qualified function that doesn't resolve becomes a placeholder source, which also makes unresolved column references across that statement's sources bind leniently; a missing DML / DROP target raises Msg 208, which the dispatch loop swallows.
Probe-confirmed deferrals, all creating successfully on real and here: a missing table, a missing column on a missing table, a column qualified to a missing source, a missing table-valued function, a missing INSERT / MERGE / DROP target, a `#temp` or `##temp` that doesn't exist yet (including a bad column on a `#temp` the body creates itself), a table the body itself creates or `SELECT … INTO`s, a missing database, `EXEC` of a missing procedure, argument errors on an `EXEC` of an existing one, a not-yet-created scalar UDF (which is what makes a **recursive** UDF creatable), and anything inside a dynamic-SQL string.

Statement granularity is real's: a body whose first statement names a missing table and whose second names a bad column on an existing one still reports Msg 207, in either order.

**Stop-at-first-deferral is the divergence.**
When the deferral arrives as a *swallowed Msg 208* — the DML / DROP-target case, not the placeholder one — the parser threw mid-statement and the only recovery is a scan to the next statement-boundary token, which can still land inside the failed statement (`INSERT INTO missing SELECT …` throws with the cursor already on `SELECT`).
Binding on from there would report errors against fragments, so `CreateTimeBinding` sets `BatchContext.BatchAborted` and the rest of the body falls back to binding at first invocation.
Real keeps binding, because it knows where the statement ended.

### What binds

Everything else the parser and the static type path check.
Probe-confirmed as CREATE-time on real and matched here: **Msg 207** (invalid column on an existing table, including on `INSERTED` / `DELETED`, on a body-declared table variable, inside `UPDATE(col)`, and in a `WHERE` / `HAVING` / `MERGE`-`ON` / `SET`-value position), **209**, **8120**, **306**, **108**, **156** / **102**, **137**, **134**, **135**, **402**, **8144**, **178**, **10700** (a body writing its own READONLY TVP — see [`table-valued-parameters.md`](table-valued-parameters.md)), **8116** (a legacy-LOB string-scalar argument — see [`scalars.md`](scalars.md#legacy-lob-arguments)), and **468** / **457** (a cross-collation comparison or unification — see [`collations.md`](collations.md#compile-time-binding)).
A never-taken `IF` or `WHILE` branch binds too, on both sides.

### Divergences

- **An aggregate whose only column reference doesn't resolve locally isn't reached.**
  `HAVING MAX(nosuchcol) = 1` is taken for an aggregate over an enclosing query — unmodeled, so it raises `NotSupportedException`, which the bind swallows (below) rather than refusing a module real accepts.
  Real reports Msg 207 at CREATE.
  The rest of that family closed: `WHERE` / `HAVING` / a `MERGE`'s `ON` / the *value* side of a `SET` now bind through the static type path — see [`collations.md`](collations.md#compile-time-binding) for the drive sites.
- **One error per CREATE.**
  Real reports every binder error it finds in the body (probed: two Msg 207s from two statements); the simulator throws on the first.
- **Msg 455** (last statement must be `RETURN`), **Msg 444** (a body `SELECT` returning to the client) and **Msg 443** (a side-effecting operator inside a function) are still unchecked — they want body-shape analysis rather than a parse, so a function real refuses is created here.
- **A `NotSupportedException` never blocks a CREATE.** An unmodeled feature in the body is a simulator gap rather than real's binder speaking; refusing the module would be strictly worse than the status quo, so the bind swallows it and the gap resurfaces at invocation.

### Skip mode had to stop executing

The bind exposed places where skip mode parsed *and ran*, which for a body whose parameters stand in as typed NULLs produced errors real never reports.
Each is now gated, and each is a fidelity gain for un-taken branches too (real runs neither):

- `INSERT`'s per-row work — DEFAULT evaluation, identity / rowversion allocation, computed columns, and the NOT NULL / CHECK / key enforcement — is skipped, as is evaluating a `VALUES` tuple (a `NEXT VALUE FOR` cell was burning a sequence value) and executing an `INSERT … SELECT`'s source query.
- `UPDATE` / `DELETE` drop their row source and `MERGE` returns before its match walk, so a body's `WHERE` / `SET` / `ON` can't raise a runtime error over the table's current contents.
- `UserFunctionCall.Run` / `ClrFunctionCall.Run` return a typed NULL.
  This one is load-bearing rather than an optimization: the FROM-less-`SELECT` fast path bakes its projection values during the **parse**, so without it every `SELECT dbo.f()` in a body would dispatch the function's own body while the module was being created.

## Body batches and the per-statement freeze

Every module body runs on a child `BatchContext`, and the per-statement current-time freeze (`BatchContext.CurrentStatement.UtcNow`, read by `GETDATE` / `GETUTCDATE` / `CURRENT_TIMESTAMP` / `SYSDATETIME` / `SYSUTCDATETIME` / `SYSDATETIMEOFFSET` / `CURRENT_DATE`) lives on that frame — so *which* batch a body runs on decides which instant it reads.
Two regimes, matching real:

- **Bodies that dispatch statements of their own** — procedure, trigger, scalar-UDF, multi-statement-TVF bodies — go through the dispatch loop, which stamps a fresh `UtcNow` per body statement.
  Probe-confirmed: a scalar-UDF body that spins for 1.2 seconds between two `SYSDATETIME()` calls reads two values 1.2 seconds apart.
- **Bodies real inlines into the referencing statement's plan** — view and inline-TVF bodies — never reach the dispatch loop (they parse and execute a single `Selection` directly), so they adopt the referencing statement's freeze via `BatchContext.AdoptStatementFreezeFrom`.
  Probe-confirmed: a view projecting `SYSDATETIME()`, read once per row across a 300,000-row scan, yields one constant value equal to the referencing statement's own `SYSDATETIME()`; an inline TVF applied per row does the same.
  The same call sites cover the indexed-view helpers (body materialization for enforcement, dependency collection, shape analysis) and the CREATE-time inline-TVF output-column inference, all of which parse a body on a child batch.

Adopting rather than re-stamping also keeps the value live: because the body re-parses per call, a baked projection value can't go stale, and a later statement reading the same view sees a later instant.

**Why the seam is load-bearing**: an inlined body whose batch inherits nothing reads `default(DateTime)`, which makes `SYSDATETIME()` in a view return `0001-01-01` and `GETDATE()` — whose `datetime` range starts at 1753 — raise **Msg 242** (`"The conversion of a varchar data type to a datetime data type resulted in an out-of-range value."`) at first read for a view, at CREATE for an inline TVF.
`StatementContext.UtcNow` is seeded at construction as the floor against that, so an un-inherited body batch still serves a live instant rather than year 1; the adoption on top of it is what makes the instant the *right* one.
Regression coverage: `CurrentTimeFunctionTests`.

## Scalar user-defined functions
`CREATE FUNCTION schema.name(@p type [= default], ...) RETURNS <type> [WITH RETURNS NULL ON NULL INPUT] AS BEGIN ... END`, called as `SELECT schema.fn(args)`.
Body source captured between outer `BEGIN`/`END` (BEGIN TRAN/TRANSACTION/DISTRIBUTED skipped during nesting) and re-tokenized per call; parameters seed a child `BatchContext.Variables`, value-form RETURN lands in `BatchContext.UdfFrame.ReturnedValue`.
Probed against SQL Server 2025.

- **2-part-name required.**
  Bare `fn(x)` → **Msg 195** ("not a recognized built-in function name") — real SQL Server treats unqualified UDF calls as built-in misses.
  Schema-qualified miss → **Msg 4121** ("Cannot find either column or the user-defined function or aggregate").
- **BEGIN/END required.**
  Inline `as return @x*10` → Msg 102.
- **Body content**: `DECLARE` / `SET` / `SELECT @v = expr` / `IF` / `WHILE` / `BEGIN…END` / `BREAK` / `CONTINUE` / `RETURN <value>`.
  Variables declared inside are function-scoped.
- **`RETURN <value>`** legal only inside a UDF body — outside raises **Msg 178** at parse time; inside, value coerces to declared return type.
- **Arity errors**: too few → **Msg 313**; too many → **Msg 8144**.
- **DEFAULT keyword required for omission.**
  `fn()` raises Msg 313 even when every parameter has a declared default — the `DEFAULT` keyword is the only legal omission (re-evaluated per call in the child batch).
- **WITH RETURNS NULL ON NULL INPUT**: any non-DEFAULT NULL arg short-circuits the body and returns typed NULL.
- **WITH SCHEMABINDING** records on `UserDefinedFunction.IsSchemaBound`, surfacing through `sys.sql_modules.is_schema_bound` / `OBJECTPROPERTY(id,'IsSchemaBound')`, gating `OBJECTPROPERTY(id,'IsDeterministic')` (see [`catalog-views.md`](catalog-views.md#isdeterministic)), and enrolling the body's references in the dependency gate — [Schema binding](#schema-binding-with-schemabinding).
  `ENCRYPTION` parse-and-discards.
- **Recursion cap: 32.**
  Tracked by `SimulatedDbConnection.NestingLevel`; exceeding → **Msg 217**.
  Shared with future stored procs / triggers / views.
- **Catalog surface**: `sys.objects` `type='FN'` / `type_desc='SQL_SCALAR_FUNCTION'`.
  `sys.parameters` emits one row per declared parameter (parameter_id 1+, `is_output=0`) plus a `parameter_id=0` return-type row (empty name, `is_output=1`).
  `max_length` is 0 across the board — pending wiring `GetSysColumnMetadata` to bare `SqlType`.
- **`OBJECT_ID(name, 'FN')`** routes to function resolution; the `'IF'` / `'TF'` filters resolve inline / multi-statement TVFs respectively; no-filter form tries function then table.
  Other codes (V / P / ...) route to the matching object kind.
- **DROP FUNCTION [IF EXISTS] schema.name[, ...]**: same shape as DROP TABLE; missing target → **Msg 3701** with "function" wording variant.
- **`ALTER FUNCTION` / `CREATE OR ALTER FUNCTION`** replace the definition in place across all three function kinds — see [Replacing a module](#replacing-a-module--alter--create-or-alter).
- **Msg 111 batch-first rule** is enforced: `CREATE FUNCTION` must open its batch, and since there is no `GO`, a batch is one `CommandText` — so `IF OBJECT_ID(…) IS NOT NULL DROP FUNCTION …; CREATE FUNCTION …` raises where two commands succeed (`ExecuteBatches` in the tests is the split).
  Real's state byte identifies the kind: 4 for CREATE FUNCTION, 5 for ALTER FUNCTION, 6 / 7 for CREATE / ALTER TRIGGER, 9 / 10 for CREATE / ALTER VIEW, 12 for CREATE RULE, 13 for CREATE DEFAULT, 14 for CREATE SCHEMA, and 1 for the merged `'CREATE/ALTER PROCEDURE'` label (probe-confirmed 2026-07-31; the simulator carries the ones it parses).

**Fidelity gaps**:
- **The body-shape rules stay unchecked** — Msg 455 (missing RETURN), Msg 443 (side-effects), Msg 444 (result-set SELECT in body).
  The body itself binds at CREATE ([CREATE-time body binding](#create-time-body-binding)), but these three want shape analysis rather than a parse, so a fall-through body returns typed NULL and side-effecting statements surface their own errors at invocation.
- **`@@ROWCOUNT` inside a UDF body** isn't isolated — body statements overwrite the caller's `LastStatementRowCount`.
  Real SQL Server preserves it across the call.

## Inline table-valued functions
`CREATE FUNCTION schema.name(@p type [= default], ...) RETURNS TABLE [WITH SCHEMABINDING | ENCRYPTION] AS RETURN [(] <SELECT> [)]`, called from a FROM clause.
Stored as `InlineTableValuedFunction` alongside `ScalarFunction` under the abstract `UserDefinedFunction` base in `Schema.Functions`.
Body re-parsed per call inside a child `BatchContext` with parameters seeded as variables, returned as a synthetic `Selection.ForInlineTvf` wrapped in a `FromSource.LateralPlan`.
Same 32-level recursion cap (Msg 217) as scalar UDFs.
Probed against SQL Server 2025.

- **Body grammar**: exactly one SELECT, optionally carrying a `WITH cte AS (…)` prefix ([`ctes.md`](ctes.md#where-a-prefix-may-appear)).
  Parens optional.
  Multi-statement inside parens → Msg 102.
  The body's stored span is measured by a token scan (`CaptureInlineTvfBody`) rather than by a parse, and the paren-less form's terminator — a statement keyword — counts only at the body's own nesting level, so a SELECT belonging to a derived table, a subquery or a CTE definition doesn't truncate the span.
  A body opening with `WITH` also spends one depth-0 statement keyword on the query the prefix scopes to.
- **WITH-clause options**: `SCHEMABINDING` records on `UserDefinedFunction.IsSchemaBound` (same surfaces and same dependency gate as scalar UDFs — [Schema binding](#schema-binding-with-schemabinding)); `ENCRYPTION` parse-and-discards.
  `RETURNS NULL ON NULL INPUT` → **Msg 487** (scalar-only).
- **CREATE-time validation**: body parses once with parameters seeded as typed variables; `OutputColumns` derives from the resulting projection.
  Unnamed column → **Msg 4514** (distinct from SELECT INTO's Msg 1038).
  Duplicate column name → **Msg 4506** (distinct from SELECT INTO's Msg 2705).
- **Argument parsing** shares `UserFunctionCall.ParseFunctionArguments` with scalar UDFs — same `DEFAULT` rule (omission → Msg 313), same Msg 313 / 8144 arity errors.
- **Kind-vs-position routing**: `ScalarFunction` in FROM → **Msg 208** (treated as missing-object, not kind-mismatch).
  `InlineTableValuedFunction` in expression position → **Msg 4121** through the existing factory.
- **CROSS APPLY / OUTER APPLY**: right side must be a parenthesized derived table OR an inline TVF.
  `ParseLateralFromSource` peeks; bare table after APPLY → syntax error (matching real SQL Server).
- **Catalog surface**: `sys.objects` `type='IF'` / `type_desc='SQL_INLINE_TABLE_VALUED_FUNCTION'`.
  `sys.columns` emits one row per output column (`is_identity=0`, `is_computed=0`).
  `sys.parameters` skips the `parameter_id=0` return-row (the TABLE shape lives in sys.columns).
  `OBJECT_ID(name, 'IF')` resolves TVFs only; `'FN'` filter doesn't match; no-filter form tries both kinds.

**Fidelity gaps**:
- **`is_nullable` always True** in `sys.columns` for TVF output.
  Real SQL Server propagates per-projection nullability via the same rules SELECT INTO uses (`Expression.ResultIsNullable`); wiring it through requires exposing projection expressions on `Selection` post-parse.
  Apps reading TVF rows through raw SQL aren't affected.
- **Forward refs aren't deferred** — an inline TVF binds its body in full (real does too: probe-confirmed that a body over a missing table reports Msg 208 at CREATE, unlike a procedure's or a scalar UDF's), so a self-recursive one fails at CREATE with Msg 208 where real reports a different error path.

## Multi-statement table-valued functions
`CREATE FUNCTION schema.name(@p type [= default], ...) RETURNS @r TABLE (column-list) [WITH SCHEMABINDING | ENCRYPTION] AS BEGIN ... END`, called from a FROM clause exactly like an inline TVF.
Stored as `MultiStatementTableValuedFunction` alongside `ScalarFunction` / `InlineTableValuedFunction` under the abstract `UserDefinedFunction` base in `Schema.Functions`.
The function class captures parsed `OutputColumns` + `KeyConstraints` + `CheckConstraints` once at CREATE time; the body re-tokenizes per call.
Probed against SQL Server 2025.

- **Body grammar**: `BEGIN ... END` block; nesting walked at token level (same code path as scalar UDF body capture).
  Body statements freely `INSERT INTO @r` / `UPDATE @r` / `DELETE @r`, may read other tables, may call other functions.
  Bare `RETURN;` exits the body and projects the accumulated `@r` rows to the caller; fall-through without `RETURN` also projects.
- **Return-table column features**: same as `DECLARE @t TABLE` (the column-list parsers share `TryParseTableVariableColumnsAndConstraints`) — typed columns, NULL / NOT NULL, IDENTITY, DEFAULT, computed columns (persisted / non-persisted), inline + table-level CHECK, PRIMARY KEY, UNIQUE.
  Named constraints (`CONSTRAINT pk PRIMARY KEY`) and FOREIGN KEY are rejected (Msg 102) at parse, inherited from the column-list parser's `isTableVariable: true` branch.
- **Per-call execution**: `Simulation.InvokeMultiStatementTvf` allocates a child `BatchContext` (no `UdfFrame` / no `ProcFrame`), pre-seeds parameters as variables and constructs a fresh `HeapTable` for `@r` registered in `TableVariables[returnVariableName]`.
  Constraint instances (`KeyConstraint[]` / `CheckConstraint[]`) are shared across calls — they're immutable, and the simulator runs single-threaded per `Simulation`.
  After body dispatch, the accumulated `@r` rows yield to the FROM-source driver.
- **RETURN handling**: bare `RETURN;` sets `BatchContext.ReturnSignaled` (the dispatch loop bails the same way procedure bodies do).
  Value-form `RETURN N` raises **Msg 178** at invoke time via the existing `ParseReturnStatement` check (both `UdfFrame` and `ProcFrame` are null).
  Real SQL Server enforces Msg 178 at CREATE time; the simulator defers — same convention scalar UDFs use for body validation.
- **WITH-clause options**: `SCHEMABINDING` records on `UserDefinedFunction.IsSchemaBound` and enrolls the body in the dependency gate ([Schema binding](#schema-binding-with-schemabinding)); `ENCRYPTION` parse-and-discards (shared with inline TVF).
- **CROSS APPLY / OUTER APPLY**: works through the same `ParseSingleFromSource` branch as inline TVF — both function kinds dispatch through `Selection.ForInlineTvf` / `Selection.ForMultiStatementTvf` returning a `FromSource.LateralPlan`.
  Arguments evaluate against the outer row scope per call.
- **Catalog surface**: `sys.objects` `type='TF'` / `type_desc='SQL_TABLE_VALUED_FUNCTION'` (distinct from inline TVF's `'IF'`).
  `OBJECT_ID(name, 'TF')` resolves multi-statement TVFs only.
- **EF Core integration**: `HasDbFunction` mapped to an `IQueryable<T>`-returning DbContext method emits `SELECT ... FROM dbo.fn(@p)` through the SqlServer provider; the simulator dispatches the body and yields rows back through the same FROM-source pipeline.
  LINQ composition (`Where` / `OrderBy` / `Select`) applies to the function's result rows post-dispatch — no pushdown into the body.

**Fidelity gaps**:
- **The body-shape rules stay unchecked**: Msg 455 (last statement must be RETURN) and Msg 443 (side-effecting external DML inside function).
  Real enforces both at CREATE; the simulator silently accepts them, so a body real would reject can run successfully here.
  **Msg 178** (value-form RETURN) *does* fire at CREATE — the body bind carries no frame, which is the same absence the invocation relies on ([CREATE-time body binding](#create-time-body-binding)).
- **Constraint enforcement is row-level strict**: PK / UNIQUE / CHECK violations in the body surface as runtime errors (Msg 2627 / Msg 547).
  Real SQL Server's probe-observed behavior is more forgiving in some cases — for shared-key collisions it returns an empty result set rather than raising.
  Stricter behavior is defensible since apps that hit it are buggy.
- **`is_nullable` always True** in `sys.columns` for return-table output (same gap as inline TVFs).

## Views
`CREATE VIEW schema.name [(col_list)] [WITH SCHEMABINDING | ENCRYPTION | VIEW_METADATA] AS <SELECT> [WITH CHECK OPTION]`, referenced from FROM as `FROM schema.view [alias]` (or unqualified `FROM view`).
Stored as `View` in `Schema.Views`.
Body re-parsed per call inside a child `BatchContext`, returned as `Selection.ForView` wrapped in a `FromSource.LateralPlan`.
Same 32-level recursion cap (Msg 217) as scalar UDFs / inline TVFs.
Probed against SQL Server 2025.

- **Body grammar**: a single SELECT, optionally carrying a `WITH cte AS (…)` prefix — recognized at the body-parse seam rather than by the statement dispatch loop, which a body never reaches → [`ctes.md`](ctes.md#where-a-prefix-may-appear).
  ORDER BY without TOP / OFFSET / FETCH → **Msg 1033** (same factory CTE bodies use).
- **Column-rename list**: `CREATE VIEW v(a, b) AS SELECT ...` renames the projection.
  Count mismatch → **Msg 8158** (too few listed) / **Msg 8159** (too many) — shared factories with CTE rename lists.
- **CREATE-time validation**: body parses once to derive `OutputColumns`.
  Unnamed projection → **Msg 4511** (distinct from inline TVF's Msg 4514 and SELECT INTO's Msg 1038 — different wording too: `"Create View or Function failed because no column name was specified for column N."`).
  Duplicate column name → **Msg 4506** (shared with inline TVFs).
- **WITH-clause options**: `SCHEMABINDING` is captured on `View.IsSchemaBound` (it gates `CREATE INDEX` on the view, surfaces through `sys.sql_modules.is_schema_bound` / `OBJECTPROPERTY(id,'IsSchemaBound')`, is the precondition `OBJECTPROPERTY(id,'IsDeterministic')` reads — see [`catalog-views.md`](catalog-views.md#isdeterministic) — and enrolls the body's references in the dependency gate, [Schema binding](#schema-binding-with-schemabinding)).
  `ENCRYPTION` / `VIEW_METADATA` parse-and-ignore.
  **`WITH CHECK OPTION`** (trailing the body) parses and records on `View.WithCheckOption`, enforced at DML time (Msg 550).
  A schema-bound view can carry a unique clustered index — an **indexed view** — see [`indexes.md`](indexes.md).
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
- **`ALTER VIEW` / `CREATE OR ALTER VIEW`** replace the body in place, keeping the object id, grants and `INSTEAD OF` triggers but dropping any indexes — see [Replacing a module](#replacing-a-module--alter--create-or-alter).
- **EF Core integration**: `ToView()` mapping works end-to-end.
  Keyless entities (`HasNoKey().ToView("name")`) project rows from CREATE VIEW-produced views; the simulator's per-call body re-parse handles correlated LINQ-emitted WHERE clauses against the view's projection.

**Fidelity gaps**:
- **`VIEW_DEFINITION` always surfaces body text** even for WITH ENCRYPTION views (real SQL Server returns NULL for ENCRYPTION views).
- **`is_nullable` always True** in `sys.columns` for view output — same gap as inline TVFs.

## Schema binding (`WITH SCHEMABINDING`)
`WITH SCHEMABINDING` on a view, scalar function, inline TVF or multi-statement TVF pins everything the body names: the referenced objects can't be dropped, altered, renamed or moved while the module stands.
`Schemas/SchemaBinding.cs` holds both halves — the reverse lookup the DDL gates consult, and the forward rules a schema-bound body's own references obey.
Every message below was probe-confirmed verbatim against SQL Server 2025 (2026-08-01).

**No stored dependency record.**
The reference set is recomputed from the module body on every gate check rather than recorded at CREATE.
A module's dependencies die with the module and travel with a replacement body for free that way, so there is no registry to invalidate on ALTER / DROP / `ALTER SCHEMA TRANSFER`.
The sweep is gated on `View.IsSchemaBound` / `UserDefinedFunction.IsSchemaBound`, so a database with no schema-bound modules pays only a dictionary walk, and DDL is the only caller.
The body walk re-tokenizes the stored source and lifts every dotted name chain out of the token stream — the same shape [`ModuleDeterminism`](catalog-views.md#isdeterministic) walks for its own question, kept separate because that one bails at the first nondeterministic built-in and needs neither column names nor FROM-clause positions.

**What the gate blocks** (the referent is schema-qualified, the blocking module surfaces as its bare leaf; real names **one** blocker and picks the oldest, which the simulator matches by ordering candidates by object id):

| Statement | Error |
| --- | --- |
| `DROP TABLE` / `DROP VIEW` / `DROP FUNCTION` of a referenced object | **Msg 3729** state 1 — `Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'v'.` |
| `ALTER` / `CREATE OR ALTER` of a referenced view or function | **Msg 3729** state 3 — `Cannot ALTER 'dbo.f' because it is being referenced by object 'v'.` (no object kind in the wording; the altered module is the error's Procedure attribution) |
| `ALTER TABLE DROP COLUMN` / `ALTER COLUMN` of a referenced column | **Msg 5074** — the module joins the constraint / index blocker list as `The object 'v' is dependent on column 'a'.`, ordered after DEFAULT and before indexes; see [`alter-table.md`](alter-table.md) |
| `sp_rename` of a referenced table or column | **Msg 15336** — `Object 'dbo.t' cannot be renamed because the object participates in enforced dependencies.` (echoes `@objname` as passed) |
| `ALTER SCHEMA … TRANSFER` of a referenced object | **Msg 15348** — `Cannot transfer a schemabound object.` |

Deliberately *not* blocked, each probe-confirmed: `ALTER TABLE … ADD` a new column; `TRUNCATE TABLE` on a referenced table; `ALTER SCHEMA … TRANSFER` of the schema-bound module itself; and every one of these against a **non**-schema-bound dependent, where real's late binding lets the DROP / ALTER through and the dependent breaks at its next call.
`DROP TABLE` runs the FK gate first: a table that is both an FK parent and a schema-bound view's base reports **Msg 3726**, not 3729.

**What a schema-bound body may reference.**
Both checks run at CREATE / ALTER of the schema-bound module, off the same extraction:

- A FROM-clause source named with anything other than a two-part name → **Msg 4512** state 3 (`Cannot schema bind view 'dbo.v' because name 't' is invalid for schema binding. Names must be in two-part format and an object cannot reference itself.`), for the one-part (`FROM t`) and three-part (`FROM other.dbo.t`) forms alike.
- A referenced view or function that isn't itself schema bound → **Msg 4513** state 2 (`Cannot schema bind view 'dbo.v'. 'dbo.plain' is not schema bound.`) — the rule that keeps the dependency graph closed under schema binding.

**Divergences**:
- **Column dependency is name-based.** Real tracks the exact columns a body binds; column references here resolve per row through a name-keyed resolver, so there is no parse-time (table, ordinal) binding to consult.
  A module counts as depending on column `C` of table `T` when it references `T` *and* its body mentions the identifier `C` anywhere.
  That is exact for the single-table bodies schema binding is used for — a column the body never names stays droppable, matching real — and over-restrictive only when a body joins two referenced tables that share a column name and touches just one of them.
- **The Msg 4512 one-part leg only fires for a name that resolves to an object** in the default schema.
  A derived-table alias is indistinguishable from a table in a token stream, so requiring the name to resolve is what keeps a legal body (probe-confirmed legal on real, as is a built-in TVF like `FROM STRING_SPLIT(…)`) out of the message; an alias that happens to collide with a real table is reported.
  A **CTE name is excluded outright**: the body's leading `WITH` prefix is walked for the names it declares, and a one-part reference to one of them is skipped even when the default schema holds a table of that name — real reads it as the CTE (probe-confirmed).
  The exclusion covers only the declared names; a real one-part table reference *inside* a CTE definition still raises 4512.
- **Msg 5074's blocker lines merge into one exception** rather than real's line-per-blocker Msg 5074 stream followed by a Msg 4922 — the pre-existing shape the constraint and index blockers already use.
- **Real's blocker ordering isn't reproduced**: real interleaves by its own dependency-graph order (probed CHECK → view → PK on one column), where the simulator keeps its fixed walker order.
  The view-before-index relationship *is* matched.
- **`sys.sql_expression_dependencies` isn't projected.** Real records dependency rows for every module, schema-bound or not, down to `referenced_minor_id` per column; this extraction is schema-bound-only and name-approximate for columns, so a faithful projection is its own build — see [`backlog.md`](backlog.md).

## Updatable views (DML through views)
INSERT / UPDATE / DELETE through a view route to the view's eventual base `HeapTable` with view-aware column-name translation, visibility filtering, and (optional) WITH CHECK OPTION enforcement.
Captured at CREATE VIEW time via `AnalyzeViewUpdatability` (in `Simulation.CreateView.Updatability.cs`) onto five `View` fields: `BaseTable` / `BaseColumnOrdinals` / `RejectionReason` / `VisibilityCheck` / `CheckOptionCheck`.
Probed against SQL Server 2025.

**Eligible shape** (each level in a view-on-view chain must satisfy all):
- Exactly one FROM source (a heap table OR another updatable view).
- No JOINs, no DISTINCT, no aggregates, no GROUP BY, no HAVING, no window functions, no set-op chain.
  TOP / OFFSET / FETCH / ORDER BY are allowed (they only affect reads).
- Every column referenced in any WHERE clause up the chain maps to a real base-table column (no WHERE that references an upstream derived projection).

Selection-side capture is `Selection.UpdatabilityProfile` (set in `BuildSqlProjection` when shape-eligible) + `Selection.UpdatabilityRejection` (drives Msg 4403 vs Msg 4405 vs Msg 4406 at the DML site).
`FromSource.BackingView` mirrors `FromSource.BackingTable` so the chained-view analyzer can recurse to find the eventual base.

**Per-output-column updatability**: `View.BaseColumnOrdinals[i]` is the base-table ordinal for column `i`, or `-1` when the projection is a derived expression (anything other than a `Reference` possibly wrapped in `NamedExpression` from `AS alias`).
The map composes through view-on-view chains so a renamed column at level 1 referenced through level 2 still resolves to its base ordinal.

**DML routing**:
- **INSERT**: `ProcessViewInsert` validates `BaseTable` is non-null (else Msg 4403 / Msg 4405 by `RejectionReason`), then routes to `ProcessHeapInsert(baseTable, context, destinationView)`.
  Column-name lookups in the explicit list translate through `BaseColumnOrdinals`; touching a `-1` ordinal raises **Msg 4406**.
  Implicit column list (no `(cols)` after view name) expands to the view's writable projection columns mapped to base ordinals — derived columns and computed/identity/rowversion base columns drop out, defaults fire normally for unlisted base columns.
- **UPDATE**: `ParseUpdate` resolves the leading identifier as a view, threads `leadingView` into `ResolveSetAssignments` (view-name → base-ordinal translation, same Msg 4406 path) and `ExecuteUpdateAgainstTable`.
  The heap scan gates `VisibilityCheck` before the user's WHERE — UPDATE through a filtered view only affects rows visible through the view.
  WHERE column references resolve against view's output columns first (so an UPDATE against a renamed view uses the rename, not the base name).
- **DELETE**: `ParseDelete` mirrors the same shape.
  Same visibility filter + column-name remap.

**WITH CHECK OPTION** (Msg 550): a per-row post-construction check that fires after row construction (INSERT) or post-update value computation (UPDATE) and before the heap write.
The closure on `View.CheckOptionCheck` AND's together every CHECK OPTION-bearing level's visibility (= that level's WHERE composed with all upstream WHEREs) up the chain — so a chained view-on-view with CHECK OPTION at one or both levels enforces both correctly.
Cascade behavior: a CHECK OPTION at any level "spans" the upstream views, matching SQL Server's `"or spans a view that specifies WITH CHECK OPTION"` wording.
DELETE never fires Msg 550 (a row leaving the view is fine).

**Errors** (all probe-confirmed verbatim against SQL Server 2025):
- **Msg 4403**: INSERT / UPDATE / DELETE through a view with aggregate / DISTINCT / GROUP BY.
  Body of the message names the view (`"Cannot update the view or function 'dbo.v' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator."`).
- **Msg 4405**: INSERT / UPDATE / DELETE through a JOIN view.
  **Simulator divergence**: real SQL Server allows single-base-table UPDATEs through JOIN views; the simulator rejects uniformly here (deferred to a follow-up bundle).
- **Msg 4406**: INSERT or UPDATE touched a derived projection column.
  Per-column gate — a view with mixed direct + derived columns accepts INSERT/UPDATE on the direct columns and DELETE through it works fine.
- **Msg 550**: WITH CHECK OPTION violation (covers chain spans).

**Catalog surface**: unchanged — `INFORMATION_SCHEMA.VIEWS.IS_UPDATABLE` stays hardcoded `'NO'` (probe-confirmed real SQL Server always reports `'NO'` here regardless of actual updatability, so the existing surface is correct).

**No-WITH-CHECK-OPTION quirk** (probe-confirmed): a filtered view without WITH CHECK OPTION accepts INSERTs that produce rows outside its WHERE, and UPDATEs that move rows out of view.
The row lands in the base; the view's WHERE only filters reads.
The simulator preserves this — `VisibilityCheck` gates UPDATE/DELETE *row selection* (which rows to mutate), not INSERT acceptance.

**Fidelity gaps**:
- **JOIN-view single-base UPDATE/DELETE** rejected with Msg 4405 — real SQL Server permits these when the modification affects exactly one base table.
  EF Core doesn't emit this shape; apps that hand-write it surface Msg 4405 prematurely.
- **OUTPUT through a view** raises `NotSupportedException` for INSERT / UPDATE / DELETE.
  Would need view-output-column rebinding for INSERTED.* / DELETED.* projection.
- **Multi-source UPDATE / DELETE** (alias-form `UPDATE alias SET ... FROM ...` where the alias resolves to a view) raises `NotSupportedException` — the alias-form FROM clause can't compose with the view's visibility predicate in the existing joined-update infrastructure.
- **WHERE referencing a derived upstream column** (a chained view's WHERE that references an expression-projected column from the level below) marks the view as not-updatable with `ViewUpdatabilityRejection.UnsupportedShape` → Msg 4403 at DML.
  Real SQL Server's behavior on this specific niche shape isn't probe-confirmed; the simulator errs on the side of rejection.
- **A CTE-bodied view is not updatable** — `INSERT` / `UPDATE` / `DELETE` through `CREATE VIEW v AS WITH c AS (SELECT … FROM t) SELECT … FROM c` reports Msg 4403 where real (probe-confirmed) passes all three through to the base table.
  The analysis reads the body's FROM source, which is the CTE rather than a base table; seeing through the CTE's own plan is what's missing.
  Reading such a view ships in full — see [`ctes.md`](ctes.md#where-a-prefix-may-appear).

## Stored procedures
`CREATE [OR ALTER] PROCEDURE schema.name [(@p type [= default] [OUTPUT], ...)] [WITH options] AS body` lives in `Schema.Procedures`.
Body source captured from `AS` to end-of-batch (BEGIN/END is optional — probe-confirmed), re-tokenized per call inside a child `BatchContext` with parameters seeded as variables and a `ProcFrame` carrying the return-code slot.
EXEC's result sets propagate to the outer caller (distinct from scalar UDFs, which discard).
Same 32-level recursion cap (Msg 217) shared with UDFs / views via `SimulatedDbConnection.NestingLevel`.
Probed against SQL Server 2025.

**Grammar**:
- **Body capture**: from the first token after `AS` to end-of-batch, with empty bodies legal (`CREATE PROC p AS` with nothing after `AS` succeeds — probe-confirmed; the per-call invocation short-circuits when `BodyText` is empty so the parser doesn't reject empty `CommandText`).
  Separately, the handlers also capture the *full* original statement text into `SchemaObject.DefinitionText` (verb normalized to `CREATE`) for `OBJECT_DEFINITION` / `sys.sql_modules` / `INFORMATION_SCHEMA.ROUTINES.ROUTINE_DEFINITION` — see [`catalog-views.md`](catalog-views.md).
- **Parens around parameter list optional**: `CREATE PROC p (@x int)` and `CREATE PROC p @x int` are equivalent.
- **WITH options** (`RECOMPILE`, `ENCRYPTION`, `EXECUTE AS CALLER|SELF|OWNER|'name'`, `FOR REPLICATION`) parse-and-ignore — the simulator doesn't model query-planner / security / replication semantics.
- **`CREATE OR ALTER`** is an upsert: creates when missing, replaces when present — see [Replacing a module](#replacing-a-module--alter--create-or-alter) for what the replacement preserves.
- **Bare `CREATE PROC` on existing name** raises **Msg 2714** (same factory as duplicate CREATE TABLE).
- **Bare `ALTER PROC` on missing name** raises **Msg 208** (NOT Msg 3701 — distinct from DROP); on a name another object kind holds, **Msg 2010**.
- **`DROP PROCEDURE [IF EXISTS] schema.name[, name...]`**: comma-list form; missing target → **Msg 3701** with `"Cannot drop the procedure 'X', …"` wording variant (`CannotDropProcedureDoesNotExist` factory).
- **Msg 111 batch-first rule** is enforced under the merged `'CREATE/ALTER PROCEDURE'` label, state 1 (same stance as UDFs / views / triggers / schemas).

**EXEC statement grammar** (`Simulation.Exec.cs`):
- **`EXEC [@rc =] target [args]`** where `target` is a procedure name (or `( <string-expr> )` for dynamic SQL).
  The optional `[@rc = ]` between EXEC and the proc name captures the return code into a caller variable — probe-confirmed position (NOT `@rc = EXEC ...` at statement start).
- **Argument forms**: positional / named (`@p = value`) / mixed positional-then-named.
  Once any named arg appears, every following arg must also be named (Msg 119).
  Each argument value must be a literal (numeric / string / NULL), a **bare identifier** (unquoted or bracketed — a legacy T-SQL form SQL Server treats as a string constant of the identifier's verbatim, case-preserved text; how Alembic / SSMS pass `sp_rename`'s new-name argument, `EXEC sp_rename 'books.title', headline, 'COLUMN'`), an `@variable` (with optional `OUTPUT`/`OUT` suffix), or the `DEFAULT` keyword — probe-confirmed: arithmetic expressions like `EXEC p @x - 1` raise Msg 102 at parse.
- **Unqualified names work**: `EXEC p1` resolves to `dbo.p1` (probe-confirmed; matches view-routing relaxation).
- **EXEC missing proc** → **Msg 2812** (`"Could not find stored procedure 'X'."`) — distinct error from Msg 208 / 3701; State 62 verbatim.
- **EXEC in expression position** (`SELECT EXEC p`) → Msg 156 via the standard non-statement-start path.

**Error matrix at EXEC**:
- **Msg 201** (`"Procedure or function 'X' expects parameter '@Y', which was not supplied."`) for an unknown named arg OR a missing required parameter (no default).
  State 4.
- **Msg 8143** (`"Parameter '@X' was supplied multiple times."`) for duplicate named args.
- **Msg 8144** (`"Procedure or function X has too many arguments specified."`) for too many positional args (same factory as scalar UDFs, but the proc name renders without single-quote wrapping in real SQL Server; the simulator's existing factory is close enough).
- **Msg 119** (mixing named-then-positional) — verbatim wording probe-confirmed.

**OUTPUT parameters**:
- Procedure parameters declared with `OUTPUT` / `OUT` get `ProcedureParameter.IsOutput = true`; the EXEC argument's optional `OUTPUT` keyword binds the caller's `@variable` slot (captured live, not by value) into `ProcArgument.OutputSlot`.
- At proc exit, OUTPUT-declared params whose call-site also passed OUTPUT copy the child batch's final variable value back to the caller's slot.
  **Probe-confirmed quirks**:
  - Caller that omits `OUTPUT` on an OUTPUT-declared parameter: writeback is suppressed (caller's variable retains its pre-EXEC value).
  - Output param when proc throws after writing: the partial write is preserved (caller sees the mid-proc value).
- For `CommandType.StoredProcedure` callers (`SimulatedDbCommand.CommandType = StoredProcedure`), parameters with `ParameterDirection.Output` / `InputOutput` writeback to `DbParameter.Value` at end-of-call; `ParameterDirection.ReturnValue` captures the proc's return code (default 0).

**RETURN semantics**:
- Bare `RETURN` exits the procedure early; subsequent statements in the body don't execute (propagates through `IF` / `BEGIN…END` / `WHILE` via `BatchContext.ReturnSignaled`, same plumbing as bare-batch RETURN).
- `RETURN <expr>` evaluates the expression, coerces to `int`, lands in `ProcFrame.ReturnCode`.
  **Probe-confirmed: `RETURN NULL` yields 0 in the caller's `@rc`** — NULL coerces to 0 in this slot specifically (NOT propagated as `DBNull`), distinct from how NULL flows through other expression contexts.
- `RETURN 'abc'` (non-coercible string) raises **Msg 245** at the proc body's RETURN statement.
- Default return code (no explicit RETURN) is **0**.
- Value-form RETURN is also legal inside scalar UDF bodies (existing); the parse-time check accepts either `BatchContext.UdfFrame` or `BatchContext.ProcFrame` being non-null.

**Multi-result-set forwarding**: a procedure body's `SELECT` statements yield result sets through the outer caller's iterator (`ExecuteReader().NextResult()` walks them).
Unlike UDF bodies, the proc invocation iterates `DispatchStatementsUntil` and yields each outcome.
Output parameter values populate AFTER reader close — probe-confirmed: real SQL Server holds OUTPUT param values until the response stream's done message, which `SimulatedDbDataReader` mirrors via the standard ADO.NET timing.

**Recursion**: each proc call increments `SimulatedDbConnection.NestingLevel`; entering a body at the cap raises Msg 217 (verbatim same wording as scalar UDFs / views).
`@@NESTLEVEL` reads the counter as int.

**`@@PROCID`**: returns the current procedure's / function's `object_id` as `int` when inside a `ProcFrame` / `UdfFrame`, else `0`.
Reads `BatchContext.ProcFrame?.Procedure.ObjectId ?? BatchContext.UdfFrame?.Function.ObjectId ?? 0`.
Used by tooling that introspects the calling proc from inside its own body (e.g. logging procs that record their own `OBJECT_NAME(@@procid)`).

**`CommandType.StoredProcedure` entrypoint**: `SimulatedDbCommand.CommandType` accepts `StoredProcedure`; on execute, `CreateResultSetsForCommand` short-circuits the parser path and routes directly to `InvokeProcedure` with arguments translated from `DbParameterCollection`.
Each `DbParameter` binds to a proc parameter by name (the `@` prefix is stripped if present); `ParameterDirection.Output` / `InputOutput` writeback paths and the optional `ParameterDirection.ReturnValue` capture mirror the EXEC-text behavior.

**Catalog surface**:
- `sys.objects` `type='P '` (char(2) trailing-space padded) / `type_desc='SQL_STORED_PROCEDURE'`.
- `sys.procedures` (load-bearing subset): `object_id`, `name`, `schema_id`, `type`, `type_desc`, `create_date`, `modify_date`, `is_ms_shipped`.
- `sys.parameters` emits one row per declared parameter (parameter_id 1+); no `parameter_id=0` row (distinct from scalar UDFs — proc has no return-type slot in this view).
  `is_output` reflects the `OUTPUT`/`OUT` declaration.
- `INFORMATION_SCHEMA.ROUTINES` (5-col subset): `ROUTINE_TYPE='PROCEDURE'`, `DATA_TYPE=NULL` for procs.
- `INFORMATION_SCHEMA.PARAMETERS` (8-col subset): `PARAMETER_MODE='IN'`/`'INOUT'` (no `'OUT'`-only — procedures always reflect OUTPUT as INOUT, probe-confirmed).
- `OBJECT_ID(name, 'P')` resolves procedures only; no-filter form tries function → view → procedure → table in order.

**Fidelity gaps**:
- **`sys.parameters`** ships the full documented 16-column shape — `object_id` / `name` / `parameter_id` / `system_type_id` / `user_type_id` / `max_length` / `precision` / `scale` / `is_output` / `is_cursor_ref` / `has_default_value` / `is_xml_document` / `default_value` (first-class **`sql_variant`** matching real SQL Server; always a NULL variant — parameter default values aren't tracked) / `xml_collection_id` / `is_readonly` / `is_nullable`.
  **`sys.all_parameters`** shares the same shape and row generator (`EnumerateParameters`) — user-object parity, like `sys.all_columns` / `sys.all_objects` (real SQL Server's `all_parameters` also surfaces system-object parameters; SMO filters by `object_id` so the identical user-object set suffices).
  SMO's UserDefinedFunction / StoredProcedure scripting reads the return / parameter metadata through `sys.all_parameters` (`LEFT JOIN … ret_param.object_id = udf.object_id AND ret_param.is_output = 1`), reading `max_length` / `precision` / `scale` / `is_xml_document` / `xml_collection_id`.
- **`sys.parameters.has_default_value`** is hardcoded `False` (matches probed real SQL Server behavior: the column reflects CLR-side DEFAULT_VALUE metadata, not the `= value` parameter default — even `@x int = 5` shows `has_default_value=False`).
- **`sys.numbered_procedures`** (3-col: `object_id` / `procedure_number` / `definition`) is always empty — numbered stored procedures are a removed legacy feature.
  SMO's StoredProcedure scripting `LEFT JOIN`s it; modeling it also cleared the sweep's proc-Script transport crash (the unresolved name was hitting the skip-mode deferred-name-resolution wire death, not a distinct fault).
- **`sys.procedures.modify_date`** tracks the last ALTER: an unaltered module reports `create_date`, and every `ALTER` / `CREATE OR ALTER` leg advances `modify_date` while `create_date` holds (probe-confirmed).
- **EXEC argument value-grammar limited to literals + `@var` + `DEFAULT`** — matches real SQL Server (Msg 102 on arithmetic), but the *type* of the literal is taken from the source token, not coerced through any inference like real SQL Server's procedure-call binding.
- **`@@ROWCOUNT` inside a proc body** isn't isolated from the caller — same gap documented for UDF bodies.
- **`OUTPUT` parameter timing**: the simulator's `SimulatedDbDataReader` populates output `DbParameter.Value` after the reader closes (via the synthesized `WriteBackOutputParameters` path), matching real SqlClient's general behavior; pre-close access reads the pre-EXEC value.
- `INSERT … EXEC` **ships** — the INSERT parser takes `EXEC` as a third row source alongside VALUES / SELECT, appending every result set the proc or dynamic batch yields.
  See [`dml.md`](dml.md#insert--exec).

## `EXECUTE … WITH RESULT SETS`

The `WITH` trailer on an `EXECUTE` statement, parsed by `Simulation.ResultSets.cs` and layered over the invoked module's outcomes as a projection.
Probed against SQL Server 2025 (2026-07-31).

**Grammar**: `WITH <option> [, …]`, where an option is `RECOMPILE` (accepted and discarded — the simulator has no plan-reuse decision to override) or one of the three `RESULT SETS` forms.
Order is free (`WITH RECOMPILE, RESULT SETS …` and the reverse both parse), a second `RESULT SETS` is Msg 102, and a stray token after the clause is Msg 102 naming it.
The `WITH` is claimed only when an execute option follows it, so a CTE behind an `EXEC` still dispatches as its own statement.

**The three forms**:
- `RESULT SETS UNDEFINED` — no-op; the module's own metadata stands.
- `RESULT SETS NONE` — declares zero sets. A module that sends one raises **Msg 11535**; a pure-DML module satisfies it (row counts aren't result sets).
- `RESULT SETS ( <definition> [, …] )` — one definition per set, each its own parenthesized `(column_name data_type [COLLATE …] [NULL | NOT NULL], …)` list.
  The doubled parentheses are load-bearing: a single set still writes `((…))`, and a bare `(…)` fails at the first column name.
  Omitted nullability means nullable.

**Where it applies**: the procedure form, `EXEC (@sql)`, and `sp_executesql`, including the `@rc =` return-code and implicit-`EXEC` shapes.
Not the system procedures (`sp_help`, `sp_tables`, …), whose arg parsers don't reach the option list.
`INSERT … EXEC` **rejects** the clause with Msg 102 — real does too, and reports the token one late (`'SETS'`, not `'WITH'`), which the simulator mirrors.

**The projection**: the declared names and types replace the module's, reaching the in-process reader's `GetName` / `GetDataTypeName` / `GetFieldType` and the wire's COLMETADATA (including the `NULL` / `NOT NULL` flag).
Values convert through the CAST value path, so the `varchar` asterisk fallback, silent narrowing truncation and rounding behave as they do in a CAST.

**The contract errors**:
- **Msg 11535** — more sets sent than declared.
- **Msg 11536** — fewer sets sent than declared.
- **Msg 11537** — a set's column count doesn't match its definition (note real's wording asymmetry: `result set number N` here, `result set #N` in 11538 / 11553).
- **Msg 11538** — the declared type isn't reachable from the run-time type by *implicit* conversion.
  This is a narrower gate than CAST: `xml` → `varchar` and `varchar` → `varbinary` both have a legal explicit CAST and are still refused.
  Both type names render bare, so a `decimal(5,2)` declaration reports `'decimal'`.
  The gate is a family matrix (`IsImplicitlyConvertible` / `ConversionFamilyOf`), differentially checked cell-by-cell against real over a 25 × 25 type grid: 601 of 625 cells agree, and the 24 that don't are all `hierarchyid`-as-source, which never reach the gate because `CAST(<string> AS hierarchyid)` isn't in `SqlValue.CoerceTo` yet.
- **Msg 11553** — a `NOT NULL` column received a NULL. Raised per row as the set streams, so preceding rows reach the client.
- **Msg 8114** — a value-level conversion failure, with both type names *decorated* (`Error converting data type varchar(5) to numeric(5,2).`).
  Real routes every conversion rule through this one number here, so the simulator remaps the CAST path's own failures (Msg 245 / 8115 / 8170 / …) onto it.

**Error attribution**: Msg 11535 / 11537 / 11538 / 11553 and the Msg 8114 failure name the module's producing statement, not the `EXECUTE` — `ERROR_PROCEDURE()` reads the innermost producing procedure and `ERROR_LINE()` its statement's line.
`SimulatedQueryResult.OriginLine` / `OriginProcedure`, stamped by the dispatch loop beside `ClientTextSize`, carry that; the innermost frame wins because an already-stamped result passes through untouched.
Msg 11536 is the exception — it belongs to the `EXECUTE` statement itself and leaves `ERROR_PROCEDURE()` NULL.
All of them are catchable by `TRY` / `CATCH`.

**Not modeled yet**:
- The `AS OBJECT <table>` / `AS TYPE <table_type>` / `AS FOR XML` result-set definition shorthands → `NotSupportedException`.
- `WITH RESULT SETS` on a system procedure.
- **`rowversion`** rides the binary family in the implicit-conversion matrix; real treats `timestamp` more narrowly than `varbinary` there (it declines `nvarchar` and `sql_variant`).
- A pair the gate **allows** but `SqlValue.CoerceTo` hasn't built raises that path's own error rather than converting — `decimal` / `money` / `float` → `varbinary`, `money` ↔ `float`, `<string>` → `image` / `hierarchyid`, `varbinary` → `datetime`.
  The same gaps show for a plain `CAST`, so they close there, not here.

**Divergence**: a set-level violation (11535 / 11537 / 11538) fails the whole `EXECUTE`, so sets that preceded it don't reach the client — real streams the matched sets first and then raises.
The dispatch loop materializes a statement's outcomes before yielding any of them, which is what hoists the error.
Row-level violations inside an accepted set still stream (11553 and the Msg 8114 failure surface mid-drain, after the earlier rows).

## Replacing a module — `ALTER` / `CREATE OR ALTER`

`ALTER {VIEW | FUNCTION | PROCEDURE | TRIGGER}` and `CREATE OR ALTER {VIEW | FUNCTION | PROCEDURE | TRIGGER}` all reuse the matching `CREATE` parser — the grammar is identical, and only the existence-check direction and the preserved identity differ.
`Simulation.Alter.cs` routes the ALTER verb; the `CREATE OR ALTER` arm lives in `Simulation.Create.cs`.
The shared rules live in `ResolveModuleAlterTarget` / `ResolveModuleSchema` (`Simulation.ModuleDefinition.cs`), so a new module kind gets them by calling one helper.
Probed against SQL Server 2025 (2026-07-31).

**What the replacement preserves**:
- `SchemaObject.ObjectId` and `SchemaObject.CreateDate`.
- Every permission granted on the module — object-scope permission rows key off `object_id`, so preserving the id preserves the grants with no extra work.
- A view's `INSTEAD OF` triggers, which reseat onto the replacement instance (the trigger-to-parent match is by reference, so `Trigger.Parent` is repointed explicitly).

**What it resets**:
- `SchemaObject.ModifyDate` advances to the statement's frozen `UtcNow`.
- A view's indexes and its schema-binding: `ALTER VIEW` on an indexed view drops the indexes along with the `WITH SCHEMABINDING` that allowed them, and the base tables stop re-validating the view's unique keys (`DetachIndexedViewDependencies` unwires `HeapTable.DependentIndexedViews`, which `DROP VIEW` also calls).

**Errors**:
- **Bare `ALTER` on a name nothing holds** → **Msg 208** state 6, including when the *schema* qualifier doesn't exist (`ALTER VIEW nosuch.v` reports `Invalid object name 'nosuch.v'`, not the Msg 2760 either `CREATE` form reports).
- **Replacing a view or function a schema-bound module references** → **Msg 3729** state 3, from the same choke point (`ResolveModuleAlterTarget`) — see [Schema binding](#schema-binding-with-schemabinding).
- **Either ALTER leg over a name another object kind holds** → **Msg 2010** (`"Cannot perform alter on 'X' because it is an incompatible object type."`), where the name echoes what the statement wrote — an unqualified reference stays unqualified, brackets are stripped.
  That covers `ALTER VIEW` over a table, `ALTER FUNCTION` over a procedure, `ALTER PROCEDURE` over a table, and `CREATE OR ALTER` landing on any of them.
- **`ALTER TRIGGER` takes the same Msg 2010 gate**, over a table, a view or a procedure name, on both ALTER legs.
  Two trigger-specific errors sit around it, in real's own order: a missing `ON` target reports **Msg 8197** first (`"The object 'dbo.nosuch' does not exist or is invalid for this operation."`), ahead of any check on the trigger name; and a trigger that exists but hangs off a *different* parent is **Msg 2110** (`"Cannot alter trigger 'dbo.tr' on 'dbo.tb' because this trigger does not belong to this object. …"`, Class 15), with both names echoing what the statement wrote.
  The ALTER is refused outright there — the trigger stays on its original parent rather than re-homing.
- **A function's kind is fixed at creation.**
  An `ALTER FUNCTION` body that writes a different kind (scalar ↔ inline TVF ↔ multi-statement TVF) is the same **Msg 2010**, and the stored function is left untouched.
  A T-SQL body over a CLR routine (or an `AS EXTERNAL NAME` body over a T-SQL one) takes the same branch by construction — the type codes differ, so the narrowing finds nothing of the declared kind.
- **Bare `CREATE` on an existing name** stays **Msg 2714**.
- **Msg 111 batch-first** applies with the ALTER label and its own state: `ALTER FUNCTION` 5, `ALTER TRIGGER` 7, `ALTER VIEW` 10.
  `CREATE OR ALTER` reports under the plain `CREATE` label and state, never the ALTER one — real names the statement by the verb it started with.
  `PROCEDURE` merges both verbs into the one `'CREATE/ALTER PROCEDURE'` label at state 1.

**A database-qualified name is rejected** on every one of these statements, `CREATE` / `ALTER` / `CREATE OR ALTER` alike: **Msg 166** `'CREATE/ALTER {VIEW | FUNCTION | PROCEDURE | TRIGGER}' does not allow specifying the database name as a prefix to the object name.`
Real always names the statement in that combined `CREATE/ALTER` form whichever verb was written, and rejects a prefix naming the *current* database as readily as any other (probe-confirmed).
A server prefix (four-part name) is **Msg 117** instead (`"contains more than the maximum number of prefixes. The maximum is 2."`).
Both live in `RejectQualifiedModuleName` (`Simulation.ModuleDefinition.cs`), called by each module parser right after it reads its name.

**Not modeled yet**:
- **The permission gate on the ALTER legs**: only a plain `CREATE` runs `PermissionEnforcement.CheckCreateModule`.
  Replacing an existing module is ungated, matching the procedure parser's pre-existing stance.

## Dynamic SQL (`EXEC (@sql)` / `sp_executesql`)
Two re-tokenizing paths in `Simulation.ExecDynamicSql.cs`.
Both run the dynamic batch inside its own child `BatchContext` (`ProcFrame` set for `RETURN` legality but the return code is discarded), share the outer connection's database / transaction state, and forward result sets to the outer caller.
**Outer `@`-variables are NOT visible** — probe-confirmed: a dynamic batch referencing an undeclared `@x` raises Msg 137.
**The dynamic batch inherits the enclosing module's `QUOTED_IDENTIFIER`**, not the session's — an `EXEC ('SELECT "x"')` inside a procedure created under `SET QUOTED_IDENTIFIER OFF` reads a string literal even when the caller's session is ON (probe-confirmed).
That falls out of how the capture is applied: invocation swaps the session flag for the body's duration, and the dynamic command seeds from the connection like any other ([`grammar.md`](grammar.md#per-object-creation-time-capture)).
A `SET QUOTED_IDENTIFIER` *inside* the dynamic batch still scopes to that batch alone.
**A `USE` inside the dynamic batch binds for that batch only** — probe-confirmed for both forms: the statements after it in the same dynamic string see the new database, and the caller resumes on the one it was on.
That scoping is what lets `sp_MSforeachdb`'s `'USE [?]; …'` idiom run each command against its own database without moving the session (see [`catalog-views.md`](catalog-views.md)).

**`EXEC (<string-expr>)`**:
- Operand evaluates in the outer batch's context (so `EXEC ('SELECT ' + @col + ' FROM t')` works), then the resulting string is dispatched as a fresh batch.
- NULL string operand → silent no-op (matches real SQL Server's permissive handling).
- The dynamic-SQL form doesn't expose a meaningful return code; `@rc = EXEC ('...')` writes 0 unconditionally.

**`EXEC sp_executesql N'sql', N'@p1 type [OUTPUT], ...', @p1 = value, @p2 = @callervar OUTPUT, ...`**:
- First argument is the SQL text; second (optional) is a parameter-declaration string parsed by `ParseSpExecuteSqlParamDefinitions` (mini-parser: `@name type [OUTPUT]` entries, comma-separated).
- Remaining arguments bind values to declared params (positional or named); `OUTPUT` keyword on an `@variable`-valued arg writes the dynamic batch's final variable value back to the caller's slot at exit.
- The pre-declared `@`-variables exist as the dynamic batch's own `Variables` dict — they don't leak into the outer scope.
- Probe-confirmed: `sp_executesql` works with no parameters (`EXEC sp_executesql N'SELECT 42'`).
