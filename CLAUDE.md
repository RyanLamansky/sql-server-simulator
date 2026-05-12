# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. It's an **ADO.NET stand-in for `Microsoft.Data.SqlClient`** — consumers create a `Simulation`, get a `DbConnection` via `CreateDbConnection()`, and use it with (for example) `Microsoft.EntityFrameworkCore.SqlServer` instead of going through SqlClient over the wire. Public surface is `Simulation` + `CreateDbConnection()`; `QualityTests.PublicApiWhitelist` fails the build if anything else leaks public — resist expanding.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter just registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter` (since the simulator's connection isn't a `SqlConnection`).

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server's behavior is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. Current fidelity bar: EF Core trusts the simulator end-to-end. `*.Tests.EFCore` is the regression oracle. Priority is opportunistic — pick the lowest-effort path that unlocks the most application compatibility next.

## Feature-bundle workflow

1. **Probe.** Behavior questions get answered against the real SQL Server 2025 reference instance (connection details in user memory). Probe scaffolds live in `/tmp/<probe-name>/`; deleted after the bundle. Only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`.
2. **Surface decisions.** Before writing code, surface 2–3 concrete design choices and recommend one each.
3. **Implement + test.** `*.Tests` exercises public API; `*.Tests.EFCore` validates the oracle. `*.Tests.Internal` only for things genuinely unreachable from public SQL.
4. **Update CLAUDE.md and `docs/claude/`.** Move bullets between What's-modeled / Not-modeled / Quirks as scope changes; deep-dive feature catalogs live under `docs/claude/`.
5. **Single-sentence commit.** Squashes capture end state. Don't run `git commit` — the user holds signing credentials.

Behavioral claims below were probed against a live SQL Server 2025 reference instance unless flagged otherwise.

## Build / test / format

```
dotnet build
dotnet test
dotnet format --verify-no-changes
```

`dotnet format whitespace` (IDE0055 + textual rules) lives outside the analyzer host — CI runs it separately. CI matrix: Debug + Release. If `obj/` permission errors appear, the user's been building outside the dev container; `rm -rf obj/ bin/` clears them.

## Architecture — load-bearing patterns

Layout: `Storage/` (pages, types, row encoder/decoder, heap, constraints), `Parser/` (tokenizer, expressions, query planning + execution), `Simulation/` (per-statement-kind partials), `Errors/` (exception factory partials), root (`Simulated*` ADO.NET types).

### Storage
8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating. Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) flow through a parallel chain of 8KB LOB pages. Bounded `varchar(N)` / `nvarchar(N)` / `varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits within 8060 bytes. Allocation tracking is a flat page list (no IAM/PFS).

### Type system
`SqlType` / `SqlValue` is the storage-layer pair. Three coercion paths: `SqlValue.Coerce` (runtime values), `SqlType.Promote` (static unification for CASE / set ops / COALESCE), `SqlType.PromoteForArithmetic(a, b, op)` (per-operator decimal/integer/money/float result type — the single source of truth for both `TwoSidedExpression.GetSqlType` and `DecimalArithmetic`; static/runtime parity required because the row encoder rejects type mismatches).

### Selection
`Selection.cs` + `Selection.Execution.cs` are a partial-class pair. `Parse → Selection`, `Execute → SimulatedSqlResultSet`. Correlated subqueries re-run the same plan per outer row via `outerResolver: Func<MultiPartName, SqlValue>?` (execute) and `outerTypeResolver: Func<MultiPartName, SqlType>?` (parse). Both walk arbitrary nesting depth via `ParserContext.OuterTypeResolver` + the runtime arg. **Derived tables in FROM are always deferred** (`FromSource.LateralPlan` is re-executed per outer row), matching SQL Server's "any FROM derived table can correlate" rule — required because outer references in WHERE/ON resolve through `Run`, not `GetSqlType`.

### Multi-source rows
`FromSource[]`; rows during enumeration are `byte[]?[]`, one slot per source, null = unmatched LEFT JOIN right side. Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple`; ambiguous unqualified name → Msg 209.

### `MultiPartName`
Readonly struct, up to 4 inline slots (SQL Server's grammar limit). API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Default.Equals(name.ImmediateQualifier, "INSERTED")`, the equality folds null into `false`), `Count`, `ToString()`. 5th segment → Msg 4104.

### Exception factories
`SimulatedSqlException` constructor is private; each error case is an `internal static` factory in a topical partial (`TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`). The number lands in `Data["HelpLink.EvtID"]`. **Grep for an existing factory before adding a new one.**

### Expression evaluation
`Expression.Run(RuntimeContext runtime)` (runtime) and `Expression.GetSqlType(...)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema. `RuntimeContext` bundles `ResolveColumn` (per-row column lookup) and `Batch` (the executing `BatchContext`); expressions that need batch / session / database state read `runtime.Batch.*` directly. `BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes UNKNOWN. Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

### Context layering
Five scopes, one home each. **Add new state to whichever class matches its true scope** — when in doubt, the rule of thumb is: who outlives whom?

- **`Simulation`** = server / instance. Process-shared system tables (`SystemHeapTables`), `NEWSEQUENTIALID` anchor + counter, the `Databases` dictionary. Public surface (`Simulation` ctor + `CreateDbConnection()`) stays on this class.
- **`Database`** (internal) = one database hosted by the server instance. `Schemas` (named-schema dict, pre-seeded with `dbo`), `CompatibilityLevel`, `VerboseTruncationWarnings`, `rowVersionCounter` (per-DB `@@DBTS`). Every `Simulation` ships with one entry named `Simulation.DefaultDatabaseName` (`"simulated"`); a future `USE <db>` adds entries to the dictionary.
- **`Schema`** (internal) = one namespace inside a database. `HeapTables` (per-schema table dict), `Functions` (UDFs — abstract `UserDefinedFunction` keyed by name, runtime-typed as either `ScalarFunction` or `InlineTableValuedFunction`), `Views` (per-schema view dict), `Procedures` (per-schema stored-procedure dict). Future sequences / triggers land here too. Schema-qualified references (`SELECT * FROM audit.t`, `SELECT audit.fn(x)`, `FROM audit.tvf(x)`, `FROM audit.view1`, `EXEC audit.proc1`) route through `Database.Schemas["audit"]`; unqualified references fall back to `Database.DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session. `CurrentDatabase` pointer, `CurrentTransaction`, `LastIdentity` (`SCOPE_IDENTITY()` / `@@IDENTITY`), `LastStatementRowCount` (`@@ROWCOUNT`), `LastErrorNumber` (`@@ERROR`), `NestingLevel` (UDF / proc / trigger / view recursion depth, capped at 32), `IdentityInsertTable`, `TraceFlags`, `IsVerboseTruncationActive()`, `TempTables` (per-session `#foo` dictionary, cleared on `Dispose`).
- **`BatchContext`** (internal, in `Parser/`) = one command execution. Owns the `ParserContext` (parse-time-only scratch — `Token`, `AggregateCollector`, `WindowCollector`, `OuterTypeResolver`, `CteBindings`, `InDefaultClause`, `AllowsWindowExpressions`) and holds batch-lifetime runtime state: `Variables`, `CurrentUndoLog`, `UdfFrame` (non-null when this batch is a scalar-UDF body being dispatched — gates value-form `RETURN` and lands the return value for the caller), `ProcFrame` (non-null when this batch is a stored-procedure body — same gate for value-form `RETURN`, plus a return-code slot the caller reads), plus the per-statement frame `CurrentStatement`. Exposes `TryResolveTable(MultiPartName)` — the routing rule that dispatches `#foo` leaves to `Connection.TempTables` regardless of qualifier; everything else routes through the named schema (or `dbo` for an unqualified reference), with `SystemHeapTables` reachable only as a flat 1-part fallback. `TryResolveFunction(MultiPartName)` resolves 2-/3-part dotted names against the named schema's `Functions` dict; 1-part names return false (real SQL Server rejects bare UDF calls with Msg 195). `TryResolveProcedure(MultiPartName)` resolves through the same schema-lookup path but accepts 1-part names (probe-confirmed: `EXEC p1` finds `dbo.p1`). `TryResolveSchema(MultiPartName)` exposes the dict-bearing schema for CREATE / DROP / TRUNCATE / SELECT INTO. `ParseObjectName(ParserContext)` parses the 1–4-segment dotted form, leaves cursor on the last name segment (standard parser contract), and compresses empty middle segments (so `tempdb..#foo` returns a 2-part name). Threaded explicitly into every `Expression.Run(RuntimeContext runtime)` call via `runtime.Batch`. Scalar-UDF and procedure invocation each allocate a child `BatchContext` via the corresponding body constructor: parameters pre-seed `Variables`, the matching frame is set, the body source text (captured at CREATE FUNCTION / CREATE PROCEDURE time) is re-tokenized through a synthesized `SimulatedDbCommand`, and the same dispatch loop runs the body. UDF bodies discard yielded result sets at the call site (Msg 444 territory); procedure bodies forward them through to the outer caller's iterator.
- **`StatementContext`** (internal, in `Parser/`) = the dispatch loop's per-statement frame. Allocated once per batch and overwritten in place at the top of each iteration; holds `UtcNow` (the per-statement-freeze the time scalars read). Stored-proc EXEC / TRY-CATCH frames slot in here when added.

**Don't stack misfit state into these buckets unthinkingly**: when adding fields, ask which scope it actually belongs to. If none fits, that's the signal to introduce the missing scope rather than squat on a neighbor. Multi-database is structurally supported but exercised only by the default entry; `USE <db>` is the trigger to populate the dictionary properly.

## Conventions that fail builds

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;`. Overrides, abstracts, statics, and interface implementations exempt.
- **SSS002**: a `readonly` field in a non-public-API type whose declared type is a strict supertype of its initializer should be declared as the concrete type. Public types, value-typed initializers (boxing), const fields, and uninitialized fields exempt.
- **SSS003**: `string.ToUpperInvariant()` / `ToLowerInvariant()` whose result is the *governing expression* of a `switch` allocates a temporary string. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on the resulting count.
- **SSS004**: two or more `if`/`else if` branches with conditions of the shape `<sameScrutinee> is <SameType> { <SameProperty>: ... }` should be a single `switch`. The `switch` form fuses isinst + ldfld; the if-chain repeats both per arm.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; typed asserts over generic.

## Style notes

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly.
- **No comments inside expression chains** — IDE0055 fails on comments in ternary chains or between `=>` and body. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (SSS001 generalized).
- **AssemblyHooks**: each test project has `AssemblyHooks.cs` with a `static [TestClass] [AssemblyInitialize]` to warm shared initialization paths once before the parallel test run. Without it, the first batch of tests races to initialize hot shared state and serializes on contention. The analyzer-tests' Roslyn-cache warm-up is the most extreme case observed (~3x slowdown), but the pattern generalizes to any expensive first-touch shared resource.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature.

## What's modeled

The `*.Tests` and `*.Tests.EFCore` suites are the authoritative behavior contract. Notes below cover only probe-confirmed quirks, deviations from SQL Server, and non-obvious implementation rules. Per-feature deep-dives live under `docs/claude/` (see [Feature reference](#feature-reference) for the trigger-phrased index); short cross-cutting sections stay inline below.

### JOINs / APPLY
INNER / bare JOIN / LEFT [OUTER] / CROSS / CROSS APPLY / OUTER APPLY. Multi-table chains compose left-to-right. ON-predicate UNKNOWN excludes. APPLY = lateral form (right side re-executed per outer row, no ON clause).

### Subqueries
`EXISTS` / `NOT EXISTS` (multi-column inner allowed); `expr [NOT] IN (SELECT ...)` (single inner column, Msg 116); scalar `(SELECT col FROM ...)` (single column, single-row Msg 512 per outer row, empty → typed NULL). All forms work correlated and non-correlated, arbitrary nesting depth.

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate. Inline column-level CHECK predicates may only reference their owning column — peer references raise **Msg 8141** at CREATE TABLE (probe-confirmed verbatim wording). The walker is structural via `Expression.VisitColumnReferences` + `BooleanExpression.VisitOperandExpressions`; coverage is currently limited to common container subclasses (`Reference`, `Parenthesized`, `TwoSidedExpression`, `Cast`, `Length`) — peer refs buried in less-common containers (`DATEPART`, `SUBSTRING`, nested `CASE`, etc.) silently escape the CREATE-TABLE check and surface at INSERT instead. Table-level CHECK has no peer restriction.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree.

### Transactions
Three entry points share one per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).

- **Statement-level atomicity**: a single mutation throwing mid-execution rolls back its partial writes. Multi-row INSERT failing on row 3 leaves zero rows.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` actually commits; `ROLLBACK` zeroes `TranCount` and walks the entire log. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx. Parallel `BeginTransaction` → `InvalidOperationException`. `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both keep advancing through rollback. Orphaned LOB chains for rolled-back inserts also leak.
- **Temp-table CREATE/DROP participates in the log** via `TempTableCreation` / `TempTableRemoval` `UndoEntry` subtypes (rollback removes from / restores into the connection's `TempTables` dict). Regular CREATE/DROP TABLE is NOT logged — see the corresponding quirk.
- No isolation: uncommitted writes immediately visible to all readers (single-Simulation, single-thread-at-a-time).

### MERGE / OUTPUT (EF SaveChanges shape only)
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch).
- `WHEN MATCHED` parses but throws `NotSupportedException` if its predicate ever evaluates true.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers seven SqlParameter-downcast pairs: `DateOnly→date`, `DateTime→date`, `DateTime→smalldatetime`, `TimeOnly→time(N)`, `TimeSpan→time(N)`, `decimal→money`, `decimal→smallmoney`. Without the adapter those mappings throw at SaveChanges. MAX-string family flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY/DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) all enforced.

### `SimulatedDbDataReader`
Full `DbDataReader` contract. Typed accessors read `SqlValue` directly via the cursor's indexer and unwrap via `As*` (no boxing); NULL on a typed accessor → `SqlNullValueException` matching SqlClient. `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date surfaces at midnight, `Kind=Unspecified`); `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` and `TimeOnly`-over-`Time`. `GetOrdinal(name)` is two-pass linear scan (case-sensitive then case-insensitive — SqlClient's documented precedence). `HasRows` is a sticky bit. `GetChar(int)` always raises `InvalidCastException` (matches SqlClient).

### Feature reference

Per-feature deep-dives live under `docs/claude/`. Each entry below is a trigger: read the linked file on demand when working in the matching area.

- **Adding or modifying a built-in scalar** (math, date, current-time, `*FROMPARTS`/EOMONTH, AT TIME ZONE, CONCAT/CONCAT_WS, string `+`) → [`docs/claude/scalars.md`](docs/claude/scalars.md).
- **Touching `SqlType.Promote` / `PromoteForArithmetic` / decimal precision-scale formulas / int↔string promotion** → [`docs/claude/arithmetic.md`](docs/claude/arithmetic.md).
- **Touching `Cast` or coercion error paths** (CAST/CONVERT narrow targets, TRY_CAST/TRY_CONVERT swallow set) → [`docs/claude/casting.md`](docs/claude/casting.md).
- **Changing `Selection`, aggregates, window functions, set ops, CASE, OFFSET/FETCH** → [`docs/claude/query.md`](docs/claude/query.md).
- **Changing UPDATE / DELETE / INSERT…SELECT / SELECT…INTO / rowversion paths** → [`docs/claude/dml.md`](docs/claude/dml.md).
- **Touching variables (DECLARE/SET/SELECT-assign), control flow (IF/BEGIN/WHILE/BREAK/CONTINUE/RETURN), TRY/CATCH+THROW+ERROR_*, PRINT, WAITFOR DELAY** → [`docs/claude/control-flow.md`](docs/claude/control-flow.md).
- **Extending CTE shapes or recursive-CTE error handling** → [`docs/claude/ctes.md`](docs/claude/ctes.md).
- **Touching JSON_VALUE / JSON_MODIFY / OPENJSON** → [`docs/claude/json.md`](docs/claude/json.md).
- **Changing name resolution, schema lookup, CREATE SCHEMA, or OBJECT_ID** → [`docs/claude/schemas.md`](docs/claude/schemas.md).
- **Adding or changing system metadata surfaces** (sys.* / INFORMATION_SCHEMA.*) → [`docs/claude/catalog-views.md`](docs/claude/catalog-views.md).
- **Extending scalar UDFs, inline TVFs, views, updatable-view DML routing, stored procedures (CREATE/ALTER/DROP/EXEC), or dynamic SQL (`EXEC (@sql)` / `sp_executesql`)** → [`docs/claude/programmable.md`](docs/claude/programmable.md).
- **Touching `#foo` routing, DROP TABLE, TRUNCATE TABLE** → [`docs/claude/temp-tables.md`](docs/claude/temp-tables.md).
- **Adding a new top-level statement parser or changing the dispatch loop's statement-separator rules** → [`docs/claude/grammar.md`](docs/claude/grammar.md) + [`docs/claude/control-flow.md`](docs/claude/control-flow.md).

## Not modeled

- Locks / MVCC / isolation levels (single-Simulation, single-thread-at-a-time). `BEGIN DISTRIBUTED TRANSACTION`, `BEGIN TRANSACTION ... WITH MARK`, `XACT_ABORT`, `SET TRANSACTION ISOLATION LEVEL` not parsed.
- `RIGHT JOIN` (rewrite as LEFT with sources swapped) and `FULL OUTER JOIN` — both raise `NotSupportedException` at parse.
- Comma-separated FROM (legacy ANSI-89 join syntax).
- `ANY` / `SOME` / `ALL` quantifiers.
- `UNION` / `UNION ALL` inside a subquery body.
- Row-constructor `IN ((1,2), (3,4))`.
- Window functions other than `ROW_NUMBER` and the aggregate-OVER family.
- Recursive-part feature restrictions (Msg 460 DISTINCT / 461 TOP / 462 OUTER JOIN / 467 aggregate-or-GROUP-BY / 465 ref-in-subquery) — silently accepted with possibly-incorrect semantics rather than raising. Apps that exercise these in real SQL Server hit rejection there too.
- `LIKE` with `COLLATE` override (default collation only).
- `CONVERT` / `TRY_CONVERT` style codes other than `0` / `120` / `121`.
- `LEN(ntext)` raising Msg 8116; legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- `OUTPUT INTO @table_var`, `OUTPUT DELETED.*` / `INSERTED.*` star expansion.
- MERGE source subqueries; MERGE target column refs in `ON`; `WHEN MATCHED` UPDATE/DELETE branches; `$action`.
- `PRIMARY KEY` / `UNIQUE` on a computed column (`NotSupportedException`).
- Heap allocation tracking (flat page list, no IAM/PFS).
- Compound assignment (`SET @v += expr` / `-=` / `*=` etc.) — rewrite as `SET @v = @v + expr`. The arithmetic-operator runtime is locked behind `protected` instance methods on `TwoSidedExpression`; exposing them as static helpers is the prerequisite refactor.
- Table variables (`DECLARE @t TABLE (...)`) — separate feature with its own storage / scope / lifecycle.
- **Global temp tables (`##foo`)** — `NotSupportedException` at parse. Local `#foo` works; the lifecycle for global temps (drops when creator session closes, visible across sessions) is the deferred scope.
- **`ALTER TABLE #foo`**, **`OBJECT_ID('tempdb..#foo')`** — none modeled (none of those exist for regular tables either yet). The common `IF OBJECT_ID(...) IS NOT NULL DROP TABLE` cleanup pattern works via `DROP TABLE IF EXISTS #foo` instead.
- **`DROP SCHEMA`**, **`ALTER SCHEMA … TRANSFER`** — deferred. CREATE SCHEMA + schema-qualified resolution ships; lifecycle doesn't yet (catalog views — `sys.schemas`, `INFORMATION_SCHEMA.SCHEMATA` — do ship as of the catalog-view-expansion bundle).
- **`CREATE SCHEMA AUTHORIZATION <owner>`** — `NotSupportedException` (simulator has no user / principal model).
- **CREATE SCHEMA's `<schema_element>` greedy form** — real SQL Server consumes trailing CREATE TABLE / VIEW / GRANT as part of the same CREATE SCHEMA statement (and requires CREATE SCHEMA to be the first statement in the batch as a result). The simulator instead dispatches the trailing tokens as their own statements — same end state for the common idiom, but mismatched-grammar trailers (e.g. anything that isn't a recognized statement start) raise `NotSupportedException`.
- **`CREATE SCHEMA sys` / `INFORMATION_SCHEMA`** — raises Msg 2760 (matching real SQL Server). The schemas themselves exist as catalog-view hosts (`select * from sys.tables` / `select * from INFORMATION_SCHEMA.COLUMNS` work); legacy bare 1-part system-table access (`select * from systypes`) also still works.
- T-SQL `GOTO` / labels — `IF` / `BEGIN…END` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN` (bare + UDF-body and stored-procedure-body value form) ship; unconditional jumps don't.
- `RAISERROR`, multi-statement table-valued functions (`RETURNS @t TABLE (...) AS BEGIN ... END`), CLR functions, triggers, sequences. Scalar UDFs, inline TVFs, views, DML-through-views (single-source updatable shape), and stored procedures (including CREATE / ALTER / DROP, EXEC with input/output/default params, `@rc = EXEC` return-code capture, `CommandType.StoredProcedure`, `EXEC (@sql)` and `sp_executesql` dynamic SQL) ship (see `docs/claude/programmable.md`). JOIN-view single-base-table UPDATE/DELETE, OUTPUT through views, and multi-source alias-form UPDATE/DELETE through views are deferred (Msg 4405 or `NotSupportedException` at the DML site). `BEGIN ATOMIC` / `BEGIN DISTRIBUTED TRANSACTION` raise `NotSupportedException` at dispatch. Value-form `RETURN N` is legal inside a scalar-UDF body and a stored-procedure body; bare batch / dynamic-SQL scope raises Msg 178. TRY/CATCH + THROW + live `@@ERROR` + `ERROR_*()` functions ship (see `docs/claude/control-flow.md`).
- **`PRINT` message capture** — the statement parses + evaluates the operand (so operand-side errors like Msg 245 surface), but the message is discarded. `DbConnection` has no `InfoMessage` event (that's a `SqlConnection` extension), so adding a public observability surface would mean a new event on `SimulatedDbConnection`. Defer until an application needs it.
- `hierarchyid`, `geography`, `geometry`.

## Quirks (modeled, not byte-identical to SQL Server)

- `CHECKSUM_AGG`: order-independent XOR fold; semantic guarantee matches (same multiset → same checksum), bit pattern won't.
- `APPROX_COUNT_DISTINCT`: implemented as exact `COUNT(DISTINCT)`.
- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific.
- Auto-generated PK / UNIQUE / CHECK constraint names: `PK__<table>__<hex>` / `UQ__...` / `CK__<table>__[col__]<hex>`; the 16-hex suffix is a deterministic FNV-1a hash, not SQL Server's object-id-derived hex (stable across runs but won't byte-match).
- **DELETE / UPDATE leak page space**: deleted (or relocated) row payload bytes stay in their original page until process exit; only the slot is tombstoned. Slot directory entries also never reused.
- **DELETE / UPDATE leak LOB chains**: orphaned LOB chains stay in `Heap.LobPages`. Other rows reference LOB pages by stable index, so list compaction would corrupt them.
- **Mass-shift UPDATE on a unique key**: `UPDATE t SET k = k + 1` where k is PK/UNIQUE may spuriously raise Msg 2627 — the two-phase validator compares each affected row's new key against other affected rows' new keys, so post-shift values overlapping pre-shift values trigger a false positive.
- **`GetBytes`/`GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Behavior matches per-call observation; the streaming-memory guarantee doesn't.
- **`SELECT INTO` string `+` reads as nullable**: real SQL Server projects `cs + 'x'` (both NOT NULL) as NOT NULL; the simulator can't statically distinguish string-concat from integer-add at projection-schema time (the dispatch happens runtime on operand types), so all `Add` results read as nullable. Conservative; no test reliance on string-`+`-non-null.
- **`SELECT INTO` from a CTE drops identity + nullability**: CTE bindings synthesize their wrapper `HeapColumn` entries with `nullable: true` and no identity, so the analyzer treats CTE sources as derived plans. Real SQL Server preserves both through simple single-source CTEs. Fix requires propagating column metadata through CTE bindings.
- **Temp-table DDL is transactional, regular-table DDL isn't**: `CREATE TABLE #foo` / `DROP TABLE #foo` inside `BEGIN TRAN` participate in the undo log (matching real SQL Server); the same statements on a regular table commit immediately regardless of an active transaction. Asymmetric, but no real workload depends on transactional regular-DDL (EF doesn't do schema changes through SaveChanges, and migrations run outside transactions on real SQL Server too).
- **Un-taken IF branches resolve names eagerly**: real SQL Server defers name resolution for un-taken branches, so `IF 1=0 SELECT bad_col FROM bad_table` runs silently. The simulator's parsers do name resolution inline with parsing, so un-taken branches that reference non-existent tables/columns still raise `Msg 208` / `Msg 207`. The common idioms (`IF NOT EXISTS (…) CREATE TABLE foo (…)`, `IF OBJECT_ID('foo','U') IS NOT NULL DROP TABLE foo`, `IF cond INSERT t VALUES (…)` against pre-existing `t`) work end-to-end because referenced names exist when the branch is skipped. State mutations inside the un-taken branch are correctly suppressed (skip-mode gate); the gap is name resolution only.
- **`IF` cond divide-by-zero**: real SQL Server surfaces `IF 1/0 = 0 …` as Msg 8134; the simulator surfaces the raw `DivideByZeroException` from .NET decimal arithmetic. Same pre-existing gap as documented for `TRY_CAST(1/0 AS INT)`.
- **`IF (1) select` paren-wrapped non-boolean cond — slight positional gap**: simulator raises Msg 4145 near `')'`; real SQL Server reports `'select'` (the post-paren token). Wording is correct (Msg 4145, non-boolean type), only the "near 'X'" suffix differs. Same gap applies to any `IF (value-expr) …` shape.
