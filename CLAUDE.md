# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. It's an **ADO.NET stand-in for `Microsoft.Data.SqlClient`** — consumers create a `Simulation`, get a `SimulatedDbConnection` via `CreateDbConnection()`, and use it with (for example) `Microsoft.EntityFrameworkCore.SqlServer` instead of going through SqlClient over the wire. The full ADO.NET concrete-pipeline chain (`SimulatedDb{Connection,Command,Parameter,ParameterCollection,DataReader,Transaction}` + `SimulatedSqlException` + the info-message family) is public with `new`-shadowed strongly-typed returns, mirroring `Microsoft.Data.SqlClient`'s shape so consumers can downcast and reach concrete properties identically. Public surface beyond that chain is intentionally minimal so internals stay free to refactor; `QualityTests.PublicApiWhitelist` is the authoritative list and fails the build on any unintended expansion — resist adding to it.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter just registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter` (since the simulator's connection isn't a `SqlConnection`).

**Packaging:** only `SqlServerSimulator` publishes; `SqlServerSimulator.EFCore` and `Example` are `IsPackable=false` — the adapter stays in-repo-but-unpublished as a deliberate demand signal, so don't pitch publishing it without a user request.

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server's behavior is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. Fidelity has caught real upstream bugs (EF Core 10's `Math.Sign(decimal)` int/decimal mismatch) — when a probe feels wrong, verify against the reference before relaxing the simulator; matching a real cross-stack quirk is the feature, not a bug to paper over. EF Core trusts the simulator end-to-end (`*.Tests.EFCore` is the regression oracle and must stay green). Beyond that floor, priority is broad SQL Server coverage weighted by popularity (user wins) and ease (thoroughness wins). The living [`docs/claude/backlog.md`](docs/claude/backlog.md) — missing features, fidelity gaps, design choices to revisit, deliberate exclusions — is ordered by that weighting but non-authoritatively; read it before picking up new feature work or pitching a built-in.

## Feature-bundle workflow

1. **Probe.** Behavior questions get answered against the real SQL Server 2025 reference instance (connection details in user memory). Probe scaffolds live in `/tmp/<probe-name>/`; deleted after the bundle. Only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`. EF Core probes against the reference clean up with `DROP TABLE IF EXISTS`, **never** `Database.EnsureDeleted()` (the login can drop but not recreate the reference DB). A probe reveals *server* behavior, not *doc* content: don't write "Microsoft's docs claim X but…" without actually reading the MSDN page.
2. **Surface decisions.** Before writing code, surface 2–3 concrete design choices and recommend one each. Before pitching a built-in as net-new, grep `Parser/Expression.cs:ResolveBuiltIn` + `Parser/Expressions/` — the What's-modeled catalog lags the code, and variants ride a flag on the existing class (`tryMode` on Convert, `isBig` on DateDiff, `kind` discriminators), not a new one.
3. **Implement + test.** `*.Tests` exercises public API; `*.Tests.EFCore` validates the oracle. `*.Tests.Internal` only for things genuinely unreachable from public SQL. `*.Tests.EFCore` must drive EF Core's LINQ→SQL emission (and C#-side surface like `HasTrigger` / `UseHiLo` / `UseTpcMappingStrategy` that shifts the emit shape across EF versions), **not** hand-written SQL through `FromSql*` / `SqlQuery` — that's parser testing in disguise (covered once by `EFCoreFromSql.cs`).
4. **Update CLAUDE.md and `docs/claude/`.** Move bullets between What's-modeled / Not-modeled as scope changes. Deep-dive catalogs — *and feature-specific quirks/divergences* — live under `docs/claude/`; CLAUDE.md's Quirks section is reserved for cross-cutting divergences with no single feature-doc home (keep it short).
5. **Single-sentence commit.** Squashes capture end state. Focus the message on *what changed* and *why*, plus probe-confirmed facts (Msg numbers, wording, semantic decisions) future-me would otherwise re-derive from the diff; omit CI-visible status (test counts, build / `dotnet format` state) — GitHub surfaces that and it goes stale. Don't run `git commit` — the user holds signing credentials.

Behavioral claims below were probed against a live SQL Server 2025 reference instance unless flagged otherwise.

## Build / test

```
dotnet build
dotnet test
```

Every `.csproj` sets `EnforceCodeStyleInBuild=true`, so `dotnet build` runs the IDE / SSS / MSTEST analyzer rules and fails on violations. No separate `dotnet format` pass is needed — running it costs seconds and catches nothing build doesn't already. CI matrix: Debug + Release. If `obj/` permission errors appear, the user's been building outside the dev container; `rm -rf obj/ bin/` clears them.

Full suite runs in ~3s; single-test filter (`--filter "FullyQualifiedName~Foo"`) under 100ms. Treat `dotnet test` as a verifier between micro-edits, not a checkpoint between major ones.

## Architecture — load-bearing patterns

Layout: `Storage/` (pages, types, row encoder/decoder, heap, constraints, lock manager + DMVs), `Parser/` (tokenizer, expressions, query planning + execution), `Simulation/` (per-statement-kind partials), `Schemas/` (`SchemaObject` hierarchy + alias/catalog-view/full-text/spatial/xml-schema-collection types), `Errors/` (exception factory partials), root (`Simulated*` ADO.NET front-door + `Simulation` / `Database` / `Schema` / supporting types).

### Storage
8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating; single-column reads through an array-typed schema take the `RowLayout` fast path (per-schema geometry cached by array identity via `ConditionalWeakTable`, making `RowDecoder.DecodeColumn` O(1) instead of two O(columns) walks — the per-row execution resolvers' path). Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) flow through a parallel chain of 8KB LOB pages. Bounded `varchar(N)` / `nvarchar(N)` / `varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits within 8060 bytes. Allocation tracking is a flat page list (no IAM/PFS).

### Type system
`SqlType` / `SqlValue` is the storage-layer pair. Three coercion paths: `SqlValue.Coerce` (runtime values), `SqlType.Promote` (static unification for CASE / set ops / COALESCE), `SqlType.PromoteForArithmetic(a, b, op)` (per-operator decimal/integer/money/float result type — the single source of truth for both `TwoSidedExpression.GetSqlType` and `DecimalArithmetic`; static/runtime parity required because the row encoder rejects type mismatches).

### Selection
`Selection.cs` + `Selection.Execution.cs` are a partial-class pair. `Parse → Selection`, `Execute → SimulatedSqlResultSet`. Correlated subqueries re-run the same plan per outer row via `outerResolver: Func<MultiPartName, SqlValue>?` (execute) and `outerTypeResolver: Func<MultiPartName, SqlType>?` (parse). Both walk arbitrary nesting depth via `ParserContext.OuterTypeResolver` + the runtime arg. **Derived tables in FROM are always deferred** (`FromSource.LateralPlan` is re-executed per outer row), matching SQL Server's "any FROM derived table can correlate" rule — required because outer references in WHERE/ON resolve through `Run`, not `GetSqlType`.

### Multi-source rows
`FromSource[]`; rows during enumeration are `byte[]?[]`, one slot per source, null = NULL-filled outer-join side (LEFT/RIGHT/FULL/OUTER APPLY). Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple`; ambiguous unqualified name → Msg 209. Per-row resolution goes through a per-enumeration `SourceColumnMemo` (name → (source, column), keyed by the name's string reference identity — execution-scoped per the plan-cache shared-plan contract); un-memoized re-resolution was the single largest CPU cost of scan-bound joins/aggregates. **Per-row resolver loops use the hoisted-scaffolding pattern**: one mutable-capture tuple slot + one cached *self-referencing lambda* (never a local function passed as its own `selfRecursive` argument — that allocates a delegate per column resolution per row, 41% of all bytes in the allocation profile) + one `RuntimeContext` per loop; follow it when adding executor loops.

### `MultiPartName`
Readonly struct, up to 4 inline slots (SQL Server's grammar limit). API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Baseline.Equals(name.ImmediateQualifier, "INSERTED")`, the equality folds null into `false`), `Count`, `ToString()`. 5th segment → Msg 4104.

### Exception factories
`SimulatedSqlException` constructor is private; each error case is an `internal static` factory in a topical partial (`TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`). The number lands in `Data["HelpLink.EvtID"]`. **Grep for an existing factory before adding a new one.**

### Expression evaluation
`Expression.Run(RuntimeContext runtime)` (runtime) and `Expression.GetSqlType(BatchContext batch, ...)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema. Both take a `BatchContext` so result types that depend on the active database (notably the collation of literal / CAST / function-result string types) stay in parity between the parse-time schema and the runtime value. `RuntimeContext` bundles `ResolveColumn` (per-row column lookup) and `Batch` (the executing `BatchContext`); expressions that need batch / session / database state read `runtime.Batch.*` directly. `BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes UNKNOWN. Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

### Context layering
Six scopes, one home each. **Add new state to whichever class matches its true scope** — when in doubt, ask who outlives whom. The field roster on each class lives in the source XML docs; this section captures only the identity + load-bearing contracts.

- **`Simulation`** = server / instance. Holds `SystemHeapTables`, the `Databases` dict, and `ServerCollationName` (string-typed `init`-only knob; defaults to `SQL_Latin1_General_CP1_CI_AS`; mirrors `model.collation` — install-time seed for every freshly-created `Database`, both the lazy `"simulated"` seed and bacpac imports without their own collation declaration; `init` reflects real SQL Server's immutability). Public surface (`Simulation` ctor + `CreateDbConnection()` + `ImportBacpac()` + `AddRemoteSimulation()` + `ServerCollationName`) is the entire external API.
- **`Database`** (internal) = one database in the instance. Holds `Schemas`, `CompatibilityLevel`, `CollationName`, the rowversion counter (`@@DBTS`), the MVCC version store, and the principal/permission/extended-property/full-text/DDL-trigger surfaces. `Simulation.Databases` starts empty; the first `CreateDbConnection()` lazily seeds `Simulation.DefaultDatabaseName` (`"simulated"`) when no `ImportBacpac` landed a database first. `USE <db>` switches session (Msg 911 on miss); 3-part names route reads across databases (`SELECT * FROM other.dbo.t`), but cross-DB writes raise `NotSupportedException` via `BatchContext.RejectCrossDatabaseMutation` — issue `USE` first.
- **`Schema`** (internal) = one namespace inside a database. Holds the object dicts (`HeapTables` / `Functions` / `Views` / `Procedures` / `Sequences` / `Triggers` — DML triggers share the object namespace) and the separate type namespace (`TableTypes` / `AliasTypes` / `XmlSchemaCollections`). Schema-qualified references route through `Database.Schemas[<schema>]`; unqualified falls back to `Database.DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session. Holds the `@@`-state backing store (`LastIdentity` = `SCOPE_IDENTITY`/`@@IDENTITY`, `LastStatementRowCount` = `@@ROWCOUNT`, `LastErrorNumber` = `@@ERROR`), `CurrentDatabase` / `CurrentTransaction`, per-session `TempTables` (`#foo`, cleared on Dispose), `NestingLevel` (capped 32), `Spid` (≥51), `SessionIsolationLevel`, `LockTimeoutMillis`, and `CurrentExecutingThreadId` (drives same-thread-deadlock detection). Full roster in the source XML docs.
- **`BatchContext`** (internal, `Parser/`) = one command execution. Owns the `ParserContext` (parse-time scratch) and batch-lifetime runtime state: `Variables`, `TableVariables` (`@t`), `CurrentUndoLog`, `CurrentTableVarUndoLog` (statement-only, disjoint from the tx-scoped log so `ROLLBACK TRAN` skips `@t`), `UdfFrame` / `ProcFrame` (non-null in a UDF / procedure body — gates value-form `RETURN`). Exposes the **resolver contract** the parser depends on:
  - `TryResolveTable(MultiPartName)` — `#foo` → `Connection.TempTables` regardless of qualifier; `@t` → `TableVariables` (1-part only); else → named schema (`dbo` for unqualified); `SystemHeapTables` only as flat 1-part fallback.
  - `TryResolveFunction` — 2-/3-part only (Msg 195 on bare 1-part). `TryResolveProcedure` accepts 1-part. `TryResolveTableType` accepts 1-part with `dbo` fallback.
  - `ParseObjectName(context, acceptTableVariable=false)` — parses 1-4-segment dotted form, compresses empty middle segments. The `acceptTableVariable` opt-in routes `@t` to a 1-part leaf at DML/FROM sites and rejects it everywhere else (Msg 102 at ALTER TABLE / DROP TABLE / TRUNCATE / CREATE / SELECT INTO).
  - Threaded explicitly into every `Expression.Run(RuntimeContext runtime)` call via `runtime.Batch`.
  - UDF / procedure invocation allocates a child `BatchContext` via the body constructor: parameters pre-seed `Variables`, the matching frame is set, the captured body text is re-tokenized through a synthesized `SimulatedDbCommand`. UDF bodies discard yielded result sets; procedure bodies forward them through.
- **`StatementContext`** (internal, `Parser/`) = the dispatch loop's per-statement frame. Allocated once per batch and overwritten at the top of each iteration; holds `UtcNow` (the per-statement freeze the time scalars read).

**Don't stack misfit state into these buckets unthinkingly**: if no scope fits, introduce the missing one rather than squatting on a neighbor.

## Conventions that fail builds

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;` (or `static readonly` for static-singleton state). Overrides, abstracts, and interface implementations exempt.
- **SSS002**: a `readonly` field in a non-public-API type whose declared type is a strict supertype of its initializer should be declared as the concrete type. Public types, value-typed initializers (boxing), const fields, and uninitialized fields exempt.
- **SSS003**: `string.ToUpperInvariant()` / `ToLowerInvariant()` whose result is the *governing expression* of a `switch` allocates a temporary string. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on the resulting count.
- **SSS004**: two or more `if`/`else if` branches with conditions of the shape `<sameScrutinee> is <SameType> { <SameProperty>: ... }` should be a single `switch`. The `switch` form fuses isinst + ldfld; the if-chain repeats both per arm.
- **SSS005**: a `switch` (expr or statement) whose arms are all single string/numeric compile-time constants must list them sorted — strings ordinal, numbers numerically (`_`/`null` excluded). Exempts any arm that's a guard, or/relational/recursive/`var` pattern, or an **enum/`char`/`bool`** constant (those order by meaning, not value). A switch deliberately ordered by meaning (time-unit magnitude, host level, …): wrap in `#pragma warning disable SSS005` + one-line rationale rather than sorting.
- **SSS006**: 2+ consecutive statements that each call a self-returning `StringBuilder` method (containing-type and return-type both `StringBuilder` — `Append`/`Insert`/`Replace`/… ) on the same builder and discard the result (bare or `_ =`) should be one fluent chain. An already-chained statement is peeled to its base receiver, so `sb.Append(a).Append(b)` beside `sb.Append(c)` merges. Only fires when that base is side-effect-free (identifier / `this.field` / dotted); call-valued roots exempt. Comments between the statements don't exempt — slot them between the chained calls.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; typed asserts over generic.

## Style notes

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly.
- **No comments inside expression chains** — IDE0055 fails on comments in ternary chains or between `=>` and body. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (SSS001 generalized).
- **No internal `<see cref>` in public-API XML docs** — a cref to an internal type dangles in consumer IntelliSense and implicitly promises stability for a name we're free to rename; state the contract in prose instead (`"an unrecognized collation name raises ArgumentException"`, not a cref to internal `Collation.IsRecognized`).
- **No conversation-scratch framing in code/docs/commits** — "Camp A/B", "this bundle", "Stage 1/2", "as we discussed" carry no meaning to a future reader; describe behavior/motivation absolutely, and cross-reference a sibling by the behavior it names, not the work-stage that produced it. Pre-existing repo terms (e.g. the transactions feature's "Bundle 1/2") are load-bearing, not transient — leave them.
- **AssemblyHooks**: each test project has `AssemblyHooks.cs` with a `static [TestClass] [AssemblyInitialize]` to warm shared initialization paths once before the parallel test run. Without it, the first batch of tests races to initialize hot shared state and serializes on contention. The analyzer-tests' Roslyn-cache warm-up is the most extreme case observed (~3x slowdown), but the pattern generalizes to any expensive first-touch shared resource.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature.
- **Control flow signals via flags, not exceptions.** BREAK / CONTINUE / RETURN / THROW set a typed flag on `BatchContext` (`LoopControl` + the skip predicate), never a signal exception — `yield return` inside try/catch composes badly with the iterator-based dispatch, and the flag reuses the same skip-mode plumbing that no-ops un-taken IF branches. Parse-time structural checks (BREAK outside WHILE → Msg 135) still fire regardless of skip state. Exceptions stay for true error conditions.

## What's modeled

The `*.Tests` and `*.Tests.EFCore` suites are the authoritative behavior contract. Two complementary maps of what's modeled: the inline subsections below document cross-cutting behaviors with **no dedicated deep-dive** (this section is their canonical home), and the [Feature reference](#feature-reference) index at the end lists everything that **has** one — **presence in that index means the feature is modeled**; read the linked file on demand. Inline notes cover only probe-confirmed quirks, deviations, and non-obvious rules.

### Subqueries
`EXISTS` / `NOT EXISTS` (multi-column inner allowed); `expr [NOT] IN (SELECT ...)` (single inner column, Msg 116); scalar `(SELECT col FROM ...)` (single column, single-row Msg 512 per outer row, empty → typed NULL); `expr <op> {ANY|SOME|ALL} (SELECT col FROM ...)` quantified comparison with all six operators plus T-SQL synonyms (`!=` `!<` `!>`), predicate-only (SELECT-list usage → Msg 102 at the operator); SOME aliases ANY. Three-valued semantics: empty inner → ALL vacuously true / ANY vacuously false (independent of LHS NULL); a NULL on either side of any per-row compare taints to UNKNOWN. All forms work correlated and non-correlated at arbitrary nesting depth. Set ops (`UNION` / `UNION ALL` / `INTERSECT` / `EXCEPT`) are legal in every subquery context because the parsers route through `Selection.Parse` → `ParseQueryExpression` — so EF Core 7+'s TPC emit shape (UNION ALL wrapped in a derived table) ships end-to-end.

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate. Inline column-level CHECK predicates may only reference their owning column — peer references raise **Msg 8141** at CREATE TABLE (probe-confirmed verbatim wording). The walker is structural via `Expression.VisitColumnReferences` + `BooleanExpression.VisitOperandExpressions`; coverage is currently limited to common container subclasses (`Reference`, `Parenthesized`, `TwoSidedExpression`, `Cast`, `Length`) — peer refs buried in less-common containers (`DATEPART`, `SUBSTRING`, nested `CASE`, etc.) silently escape the CREATE-TABLE check and surface at INSERT instead. Table-level CHECK has no peer restriction.
- `PRIMARY KEY` / `UNIQUE` / secondary `CREATE INDEX`: linear scan (O(N) per insert), no B-tree; reads and `UPDATE` / `DELETE` / `MERGE` target scans get **incrementally-maintained** per-`Heap` seek acceleration (equality / IN / leading-column range / equality-prefix+range continuation / ORDER BY elimination / keyset). Seek shapes, mutation/MERGE seeking, journal mechanics, decline rules, residual-WHERE invariant in [`indexes.md`](docs/claude/indexes.md).
- `FOREIGN KEY`: inline / table-level / named forms; all four referential actions on `ON DELETE` / `ON UPDATE`; enforced at INSERT / UPDATE / DELETE / MERGE; full `sys.foreign_keys` / `sys.foreign_key_columns`. Enforcement **seeks the shared `HeapSeekCache`** (live-byte verified, no residual WHERE). Referential-action, cascade-cycle, PK/UNIQUE-target, and NULL-skip rules + Msg numbers in [`foreign-keys.md`](docs/claude/foreign-keys.md).

### Transactions
Three entry points share one per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).

- **Statement-level atomicity**: a single mutation throwing mid-execution rolls back its partial writes. Multi-row INSERT failing on row 3 leaves zero rows.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` actually commits; `ROLLBACK` zeroes `TranCount` and walks the entire log. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx. Parallel `BeginTransaction` → `InvalidOperationException`. `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both keep advancing through rollback. (A rolled-back INSERT's off-row LOB chain and heap-page bytes are reclaimed — rollback is terminal, so an uncommitted insert is invisible to every snapshot.)
- **Temp-table CREATE/DROP participates in the log** via `TempTableCreation` / `TempTableRemoval` `UndoEntry` subtypes. Regular CREATE/DROP TABLE is NOT logged — the temp-vs-regular DDL asymmetry is detailed in [`temp-tables.md`](docs/claude/temp-tables.md).
- Locking + MVCC: full 8-mode matrix, row-X writers + row-mode readers per hints/iso, RR/SER/UPDLOCK/XLOCK/TABLOCK/HOLDLOCK/REPEATABLEREAD/NOLOCK/READPAST hints, escalation at 5000 row-locks, Msg 1205 deadlock detection, Msg 1222 timeouts, SNAPSHOT + RCSI with version chains + GC + DMVs. See [`docs/claude/locking.md`](docs/claude/locking.md).

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers seven SqlParameter-downcast pairs: `DateOnly→date`, `DateTime→date`, `DateTime→smalldatetime`, `TimeOnly→time(N)`, `TimeSpan→time(N)`, `decimal→money`, `decimal→smallmoney`. Without the adapter those mappings throw at SaveChanges. MAX-string family flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY/DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) all enforced.

### `SimulatedDbDataReader`
Full `DbDataReader` contract. Typed accessors read `SqlValue` directly via the cursor's indexer and unwrap via `As*` (no boxing); NULL on a typed accessor → `SqlNullValueException` matching SqlClient. `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date surfaces at midnight, `Kind=Unspecified`). A **`datetime` value rounds to whole milliseconds at the ADO.NET boundary** (`DateTimeSqlType.RoundToClientMilliseconds`, applied in `GetDateTime` and `SqlValue.ToObject` — the latter also covers `GetValue` / `GetFieldValue` / output-parameter writeback) to match SqlClient's `.000`/`.003`/`.007` materialization; the value stays at full 1/300-second resolution for engine-internal comparison / arithmetic / re-encode, so only the client-facing surface rounds. `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` and `TimeOnly`-over-`Time`. `GetOrdinal(name)` is two-pass linear scan (case-sensitive then case-insensitive — SqlClient's documented precedence). `HasRows` is a sticky bit. `GetChar(int)` always raises `InvalidCastException` (matches SqlClient).

### Feature reference

Per-feature deep-dives live under `docs/claude/`. Each entry below is a trigger: read the linked file on demand when working in the matching area.

- **Built-in scalars** (math, date incl. DATETRUNC / DATE_BUCKET / SWITCHOFFSET / TODATETIMEOFFSET, current-time, `*FROMPARTS`, AT TIME ZONE, CONCAT, char-code, SOUNDEX / STR / TRANSLATE / STRING_ESCAPE / DIFFERENCE, CHOOSE / IIF, bit manipulation (BIT_COUNT / GET_BIT / SET_BIT / LEFT_SHIFT / RIGHT_SHIFT), CHECKSUM / BINARY_CHECKSUM, FORMAT, RAND, STRING_SPLIT, GENERATE_SERIES, COMPRESS/DECOMPRESS, session/server `@@`-constants + HOST_NAME / APP_NAME / GETANSINULL / ORIGINAL_DB_NAME, session-state store (SESSION_CONTEXT / sp_set_session_context / CONTEXT_INFO / CONNECTIONPROPERTY / CURRENT_TRANSACTION_ID / CURRENT_REQUEST_ID)) → [`scalars.md`](docs/claude/scalars.md).
- **`SqlType.Promote` / `PromoteForArithmetic` / decimal precision-scale / int↔string promotion** → [`arithmetic.md`](docs/claude/arithmetic.md).
- **`Cast` / coercion error paths** (CAST/CONVERT narrow targets, TRY_CAST/TRY_CONVERT swallow set, PARSE/TRY_PARSE culture-aware parsing) → [`casting.md`](docs/claude/casting.md).
- **`Selection`, aggregates, window functions, set ops, CASE, OFFSET/FETCH** → [`query.md`](docs/claude/query.md).
- **JOIN / APPLY** — INNER / LEFT / RIGHT / FULL / CROSS + CROSS/OUTER APPLY, ANSI-89 comma-FROM, EF `LeftJoin`/`RightJoin` routing (no LINQ `FullJoin`, so FULL is raw-SQL-only); `JoinDriver` equi-join hash fast path + `SqlValueKey` keying, nested-loop fallback, parse-time comma/`CROSS JOIN`+WHERE → equi-`INNER` rewrite, RIGHT/FULL materialization + derived-table-right, `JoinDiagnostics` strategy guard → [`joins.md`](docs/claude/joins.md).
- **`PIVOT` / `UNPIVOT`** table operators — PIVOT desugars to grouped conditional aggregation (implicit grouping = all inner columns except FOR + aggregate-arg), UNPIVOT is a NULL-skipping unfold; both attach as a postfix FROM-source wrapper and ride the derived-table `LateralPlan` seam → [`pivot.md`](docs/claude/pivot.md).
- **UPDATE / DELETE / INSERT…SELECT / SELECT…INTO / rowversion (incl. `@@DBTS` / `MIN_ACTIVE_ROWVERSION`) / identity helpers (`@@IDENTITY` / `SCOPE_IDENTITY` / `IDENT_CURRENT` / `IDENT_INCR` / `IDENT_SEED`) / `@@ROWCOUNT` / `ROWCOUNT_BIG` / OUTPUT / MERGE** → [`dml.md`](docs/claude/dml.md).
- **Variables, control flow (IF/WHILE/BREAK/CONTINUE/RETURN), TRY/CATCH+THROW+ERROR_*, `@@ERROR` / `@@TRANCOUNT` / `XACT_STATE`, PRINT, WAITFOR** → [`control-flow.md`](docs/claude/control-flow.md).
- **Cursors (`DECLARE … CURSOR` / `OPEN` / `FETCH` / `CLOSE` / `DEALLOCATE`, STATIC / KEYSET / DYNAMIC sensitivity, scroll fetches, `@@FETCH_STATUS` / `@@CURSOR_ROWS` / `CURSOR_STATUS`, `WHERE CURRENT OF`)** → [`cursors.md`](docs/claude/cursors.md).
- **CTE shapes / recursive-CTE error handling** → [`ctes.md`](docs/claude/ctes.md).
- **JSON_VALUE / JSON_QUERY / JSON_MODIFY / JSON_OBJECT / JSON_ARRAY / JSON_OBJECTAGG / JSON_ARRAYAGG / JSON_PATH_EXISTS / ISJSON / OPENJSON** → [`json.md`](docs/claude/json.md).
- **Name resolution, schema lookup, CREATE / DROP / ALTER SCHEMA TRANSFER, `OBJECT_ID` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` / `SCHEMA_ID` / `SCHEMA_NAME` / `DB_ID` / `DB_NAME`, cross-DB read routing** → [`schemas.md`](docs/claude/schemas.md).
- **System metadata surfaces** (sys.* / INFORMATION_SCHEMA.*, function-form lookups: `OBJECT_DEFINITION` / `OBJECTPROPERTY` / `OBJECTPROPERTYEX` / `COLUMNPROPERTY` / `INDEXPROPERTY` / `INDEX_COL` / `INDEXKEY_PROPERTY` / `STATS_DATE` / `TYPEPROPERTY` / `SERVERPROPERTY` / `COL_LENGTH` / `COL_NAME` / `TYPE_NAME` / `TYPE_ID` / `PARSENAME`) → [`catalog-views.md`](docs/claude/catalog-views.md).
- **Scalar UDFs / TVFs / views / stored procs / dynamic SQL / `@@NESTLEVEL` / `@@PROCID`** → [`programmable.md`](docs/claude/programmable.md).
- **`#foo` / `##foo` routing, DROP TABLE, TRUNCATE TABLE** → [`temp-tables.md`](docs/claude/temp-tables.md).
- **`DECLARE @t TABLE`, table-variable DML, `OUTPUT … INTO`** → [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE TYPE … AS TABLE`, TVP params + `READONLY`, ADO.NET TVP** → [`table-valued-parameters.md`](docs/claude/table-valued-parameters.md).
- **`CREATE TYPE … FROM <builtin>` (scalar alias types / UDDTs), multi-part type-references** → [`alias-types.md`](docs/claude/alias-types.md).
- **`sp_addextendedproperty` / `fn_listextendedproperty` / `sys.extended_properties`** → [`extended-properties.md`](docs/claude/extended-properties.md).
- **`CREATE/ALTER/DROP SEQUENCE`, `NEXT VALUE FOR`, `sys.sequences`** → [`sequences.md`](docs/claude/sequences.md).
- **DML + DDL triggers** (`CREATE TRIGGER` incl. `ON DATABASE`, `INSERTED`/`DELETED`, `TRIGGER_NESTLEVEL`, `sys.triggers`) → [`triggers.md`](docs/claude/triggers.md).
- **`FOREIGN KEY` + referential actions, `sys.foreign_keys`** → [`foreign-keys.md`](docs/claude/foreign-keys.md).
- **`PERIOD FOR SYSTEM_TIME`, `FOR SYSTEM_TIME ALL/AS OF`, history sibling, `temporal_type`** → [`temporal-tables.md`](docs/claude/temporal-tables.md).
- **`ALTER TABLE` ADD/DROP/ALTER COLUMN + CONSTRAINT (incl. trust toggling)** → [`alter-table.md`](docs/claude/alter-table.md).
- **`CREATE INDEX` (UNIQUE / CLUSTERED / INCLUDE / WHERE filter), `sys.indexes`** → [`indexes.md`](docs/claude/indexes.md).
- **Table hints (`WITH (NOLOCK …)`) + statement `OPTION (…)` hints** → [`query-hints.md`](docs/claude/query-hints.md).
- **Per-`Simulation` plan cache** (single-SELECT `Selection` reuse keyed by text + db + parameter-type signature, `SchemaVersion`-stamped invalidation, inline-in-the-SELECT-arm promotion because the iterator's post-yield code is unreachable on a non-draining `ExecuteReader`; `VariableReference` Run-time slot lookup is the load-bearing co-fix) → [`plan-cache.md`](docs/claude/plan-cache.md).
- **Locking, MVCC, SNAPSHOT/RCSI, deadlock/timeout, lock-related DMVs** → [`locking.md`](docs/claude/locking.md).
- **Application locks** (`sp_getapplock` / `sp_releaseapplock` / `APPLOCK_MODE` / `APPLOCK_TEST`, return-code-vs-raised-error asymmetry, EF Core 9/10 `Database.Migrate()`'s `__EFMigrationsLock`) → [`app-locks.md`](docs/claude/app-locks.md).
- **`hierarchyid` data type** (incl. deferred byte-identical CAST research notes) → [`hierarchyid.md`](docs/claude/hierarchyid.md).
- **`GRANT` / `REVOKE` / `DENY`, principal DDL, fixed-principal seed, principal scalars (`USER_ID` / `SUSER_ID` / `DATABASE_PRINCIPAL_ID` / `USER_NAME` / `SUSER_NAME` / `SUSER_SNAME` / `CURRENT_USER` / `SESSION_USER` / `SYSTEM_USER` / `ORIGINAL_LOGIN` / `HAS_PERMS_BY_NAME` / `IS_MEMBER` / `IS_ROLEMEMBER` / `IS_SRVROLEMEMBER`)** → [`permissions.md`](docs/claude/permissions.md).
- **`CREATE FULLTEXT CATALOG`/`INDEX`, `CONTAINS`/`FREETEXT` rejection** → [`full-text.md`](docs/claude/full-text.md).
- **`xml` data type, XML schema collections, XML methods (`.value()` / `.nodes()` / `.query()` / `.exist()` execute via an XQuery-subset evaluator; `.modify()` XML-DML skip-with-diagnostic), XML indexes** → [`xml.md`](docs/claude/xml.md).
- **`geography` / `geometry` types, spatial methods, spatial indexes** → [`spatial.md`](docs/claude/spatial.md).
- **`ALTER DATABASE SET <option>` accept-list + database-level `COLLATE` clause** → [`database-options.md`](docs/claude/database-options.md).
- **Per-column / per-expression collation, coercibility precedence, Msg 468 / 457 cross-collation enforcement, recognized catalog, `#temp` collation inheritance** → [`collations.md`](docs/claude/collations.md).
- **New top-level statement parser or dispatch-loop separator rules** → [`grammar.md`](docs/claude/grammar.md) + [`control-flow.md`](docs/claude/control-flow.md).
- **BACPAC import** (`Simulation.ImportBacpac` instance method — multi-database via repeated calls, `BacpacImportOptions`, `ModelXmlReader` dispatcher, BCP wire format, `BacpacBuilder` test harness) → [`bacpac-loader.md`](docs/claude/bacpac-loader.md).
- **Linked servers** (`Simulation.AddRemoteSimulation`, `sp_addlinkedserver` / `sp_dropserver`, four-part-name FROM routing through the remote's ADO.NET pipeline, `sys.servers`) → [`linked-servers.md`](docs/claude/linked-servers.md).

## Not modeled

- **Key-range locks** — sole remaining phase 4+ deferral. See [`locking.md`](docs/claude/locking.md) for what does ship (full 8-mode matrix, SNAPSHOT/RCSI, DMVs, Msg 1205/1222/3952/3960).
- `BEGIN DISTRIBUTED TRANSACTION` raises `NotSupportedException` at dispatch. `BEGIN TRANSACTION <name> WITH MARK 'm'` raises **Msg 319** at parse (the parser doesn't accept `WITH` here); bare named transactions (`BEGIN TRAN t1`) ship.
- **`CREATE DATABASE`** / **`CREATE ASSEMBLY`** — Msg 102 at parse. Adding databases routes through `Simulation.ImportBacpac`; CLR assemblies aren't modeled at all.
- **Cross-database / cross-server DML** (`INSERT`/`UPDATE`/`DELETE`/`MERGE` through a 3-/4-part name to another database or linked server) raises `NotSupportedException` via `BatchContext.RejectCrossDatabaseMutation` — issue `USE <db>` first. Cross-database / four-part-name *reads* (SELECT / JOIN) ship; catalog-view reads through four-part names (`srv.db.sys.tables`) fall through to Msg 208. See [`schemas.md`](docs/claude/schemas.md), [`linked-servers.md`](docs/claude/linked-servers.md).
- **`SET <option>` accept-list** (`Simulation.Set.cs`) covers XACT_ABORT, all ANSI/session-state toggles, `STATISTICS {IO|TIME|XML|PROFILE}`, value-taking options (`TEXTSIZE`/`DATEFIRST`/etc.) — all parse-and-discard. Unknown SET → Msg 195. `SET @v`, `IDENTITY_INSERT`, `NOCOUNT`, `LOCK_TIMEOUT`, `TRANSACTION ISOLATION LEVEL` carry semantic effect.
- **`ALTER DATABASE … SET` / `COLLATE` surface** — see [`database-options.md`](docs/claude/database-options.md). Most options parse-and-discard; `COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT` are load-bearing.
- `RANGE BETWEEN <N> PRECEDING/FOLLOWING` numeric-offset — Msg 4194, matching real SQL Server's licensed-feature rejection. `ROWS` numeric-offset ships. Default frame with ORDER BY is `RANGE UNBOUNDED PRECEDING TO CURRENT ROW`; without it, whole partition. LAST_VALUE matches real SQL Server's default-frame semantic.
- Recursive-part feature restrictions (Msg 460 / 461 / 462 / 467 / 465) — silently accepted with possibly-incorrect semantics. Apps that exercise these hit rejection on real SQL Server too.
- `LEN(ntext)` raising Msg 8116; legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- **MERGE gaps**: CTE-precedes-MERGE bare-name form and `MERGE INTO <updatable view>` both ship (the latter at full parity with UPDATE / INSERT / DELETE-through-view). `MERGE … OUTPUT` through a view raises `NotSupportedException` (view-column projection through `INSERTED` / `DELETED` deferred). See [`dml.md`](docs/claude/dml.md).
- `UNIQUE` on a *non-persisted* computed column (PK/UNIQUE on `PERSISTED` ships). Msg 4936 determinism gate for PERSISTED computed columns also not enforced.
- Heap allocation tracking (flat page list, no IAM/PFS).
- **Table-variable named constraints / foreign keys** — Msg 102 (matches real SQL Server's grammar restriction inside `DECLARE @t TABLE`). Multi-variable DECLARE with a table variable, mixed scalar+table DECLARE, and `SET IDENTITY_INSERT @t ON` also reject. Column features (IDENTITY / UNIQUE / inline + table-level CHECK / computed / rowversion) all ship — see [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE SCHEMA AUTHORIZATION`** — `NotSupportedException` (no principal model on schemas). `DROP SCHEMA` + `ALTER SCHEMA TRANSFER` ship — see [`schemas.md`](docs/claude/schemas.md).
- **`CREATE SCHEMA <schema_element>` greedy form** — simulator dispatches trailing CREATE/GRANT as their own statements rather than as part of the same CREATE SCHEMA. Same end state for the common idiom; mismatched-grammar trailers raise.
- **`CREATE SCHEMA sys` / `INFORMATION_SCHEMA`** + **`CREATE TABLE sys.*` / `INFORMATION_SCHEMA.*`** — both raise Msg 2760, matching real SQL Server's permission-error framing. The schemas exist as catalog-view hosts.
- T-SQL `GOTO` / labels — `IF` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN` ship; unconditional jumps don't.
- **Programmable-object top-level gaps**: CLR functions, logon triggers, INSTEAD OF UPDATE/DELETE on non-updatable views, JOIN-view single-base UPDATE/DELETE, OUTPUT through views, multi-source alias-form UPDATE/DELETE through views (Msg 4405). Natively-compiled and CLR procedures ship at parser-fidelity tier only (ATOMIC boundary falls through to session isolation; CLR bodies parse but `EXEC` is a no-op). See [`programmable.md`](docs/claude/programmable.md).
- **PRINT semantic gaps** — Msg 1046 subquery-in-operand not raised; non-string formatting uses `CoerceTo(varchar(8000))` instead of PRINT-specific style 0; 8000/4000-byte truncation not enforced. `InfoMessage` event surface itself ships (see `SimulatedDbConnection.InfoMessage`); these are the residual fidelity gaps in what each entry carries.
- **`ALTER TABLE` out-of-scope**: DROP PERIOD FOR SYSTEM_TIME, REBUILD, SWITCH PARTITION, `ALTER COLUMN ADD/DROP {PERSISTED|MASKED|ROWGUIDCOL|SPARSE}`, multi-constraint ADD in one statement. (ALTER COLUMN of an IDENTITY column to non-integer → Msg 2749; of a period column → Msg 13599.) Modeled shapes in [`alter-table.md`](docs/claude/alter-table.md).
- **`hierarchyid` / `geography` / `geometry` byte-identical CAST encoding** — currently simulator-native; cross-engine byte transfer deferred. See [`hierarchyid.md`](docs/claude/hierarchyid.md), [`spatial.md`](docs/claude/spatial.md).
- **Query hints gaps**: FROM-source `(unknown)` without alias falls through to Msg 102 (real raises Msg 207/321); `FORCESEEK(name(cols))` nested-form name validation isn't run. Surface in [`query-hints.md`](docs/claude/query-hints.md).

## Quirks (modeled, not byte-identical to SQL Server)

Cross-cutting divergences with no single feature-doc home live here; feature-specific quirks live in their `docs/claude/` deep-dive's divergence section (reachable via [Feature reference](#feature-reference)).

- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific.
- Auto-generated constraint names: PK / UNIQUE shape `PK__<table8>__<16hex>` / `UQ__<table8>__<16hex>` (16-hex 64-bit FNV-1a); CK / FK / DF shape `CK__<table8>__[<col8>__]<8hex>` (8-hex 32-bit FNV-1a). Deterministic across runs, distinct from SQL Server's object-id-derived hex (so won't byte-match).
- **`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Per-call observation matches; the streaming-memory guarantee doesn't.
- **Reclaimed heap space is reused, but page lists shrink only from the tail**: superseded row bytes and off-row LOB chains are freed and reused (`HeapPage.Compact` / `Heap.FreeLobChain`), so memory tracks the *peak concurrent* working set. A fully-dead interior page is reused in place but never removed from `Heap.Pages`, and a reclaimed slot keeps a 2-byte zero-extent directory entry — mid-list removal would break the stable `(page, slot)` addresses cursors, version Rids, and forward pointers depend on. `DBCC SHRINKDATABASE` / `SHRINKFILE` trim only the *trailing* run of fully-dead data / freed-LOB pages (`Heap.TrimTrailingDeadPages` / `TrimTrailingFreeLobPages`, after a version-store GC pass); interior dead pages and version-/lock-pinned tail pages stay put. SHRINKDATABASE emits no result set; SHRINKFILE returns the documented per-file row with sizes synthesized from heap page totals (no physical file model). A versioning-on **autocommit** UPDATE/DELETE reclaims its superseded chains via a GC pass at statement end when no snapshot is open.
