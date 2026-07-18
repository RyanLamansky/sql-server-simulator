# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. An **ADO.NET stand-in for `Microsoft.Data.SqlClient`** — consumers create a `Simulation`, get a `SimulatedDbConnection` via `CreateDbConnection()`, and use it with (e.g.) `Microsoft.EntityFrameworkCore.SqlServer` instead of SqlClient over the wire. The full ADO.NET concrete-pipeline chain (`SimulatedDb{Connection,Command,Parameter,ParameterCollection,DataReader,Transaction}` + `SimulatedSqlException` + the info-message family) is public with `new`-shadowed strongly-typed returns, mirroring SqlClient's shape so consumers downcast and reach concrete properties identically. Public surface beyond that chain is intentionally minimal so internals stay free to refactor; `QualityTests.PublicApiWhitelist` is authoritative and fails on unintended expansion — resist adding to it.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter` (the simulator's connection isn't a `SqlConnection`).

**Packaging:** only `SqlServerSimulator` publishes; `SqlServerSimulator.EFCore` and `Example` are `IsPackable=false` — the adapter stays in-repo-but-unpublished as a deliberate demand signal, so don't pitch publishing it without a user request.

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. Fidelity has caught real upstream bugs (EF Core 10's `Math.Sign(decimal)` mismatch) — when a probe feels wrong, verify against the reference before relaxing the simulator; matching a real cross-stack quirk is the feature, not a bug. EF Core trusts the simulator end-to-end (`*.Tests.EFCore` is the regression oracle, must stay green). Beyond that floor, priority is broad coverage weighted by popularity (user wins) × ease (thoroughness wins). The living [`docs/claude/backlog.md`](docs/claude/backlog.md) — missing features, fidelity gaps, design choices, exclusions — is ordered by that weighting non-authoritatively; read it before new feature work or pitching a built-in.

## Feature-bundle workflow

1. **Probe.** Behavior questions get answered against the real SQL Server 2025 reference (connection in user memory). Probe scaffolds live in `/tmp/<probe-name>/`, deleted after. Only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`. EF Core probes clean up with `DROP TABLE IF EXISTS`, **never** `Database.EnsureDeleted()` (the login can drop but not recreate the reference DB). A probe reveals *server* behavior, not *doc* content: don't write "Microsoft's docs claim X but…" without reading the MSDN page.
2. **Surface decisions.** Before writing code, surface 2–3 concrete design choices, recommend one each. Before pitching a built-in as net-new, grep `Parser/Expression.cs:ResolveBuiltIn` + `Parser/Expressions/` — the catalog lags the code, and variants ride a flag on the existing class (`tryMode` on Convert, `isBig` on DateDiff, `kind` discriminators), not a new one.
3. **Implement + test.** `*.Tests` exercises public API; `*.Tests.EFCore` validates the oracle. `*.Tests.Internal` only for things genuinely unreachable from public SQL. `*.Tests.EFCore` must drive EF Core's LINQ→SQL emission (and C#-side surface like `HasTrigger` / `UseHiLo` / `UseTpcMappingStrategy` that shifts the emit shape across EF versions), **not** hand-written SQL through `FromSql*` / `SqlQuery` — that's parser testing in disguise (covered once by `EFCoreFromSql.cs`).
4. **Update CLAUDE.md and `docs/claude/`.** Move bullets between What's-modeled / Not-modeled as scope changes. Deep-dives — *and feature-specific quirks/divergences* — live under `docs/claude/`; the Quirks section here is only for cross-cutting divergences with no feature-doc home.
5. **Single-sentence commit.** Squashes capture end state. Message = *what changed* + *why* + probe-confirmed facts (Msg numbers, wording, semantic decisions) future-me would re-derive from the diff; omit CI-visible status (test counts, build state) — GitHub surfaces it and it goes stale. Don't run `git commit` — the user holds signing credentials.

Behavioral claims below were probed against a live SQL Server 2025 reference instance unless flagged otherwise.

## Build / test

```
dotnet build
dotnet test
```

Every `.csproj` sets `EnforceCodeStyleInBuild=true`, so `dotnet build` runs the IDE / SSS / MSTEST analyzers and fails on violations. No separate `dotnet format` pass — it catches nothing build doesn't. CI matrix: Debug + Release. `obj/` permission errors mean building outside the dev container; `rm -rf obj/ bin/` clears them.

A full build + test cycle runs 20–30s; single-test filter (`--filter "FullyQualifiedName~Foo"`) stays fast. Still cheap enough to treat `dotnet test` as a verifier between micro-edits, not a checkpoint.

**No large binary files in the repo** (bacpacs included — the WWI/AW `.bacpac` fixtures live gitignored under `.vs/`, local-only). Tests get their data by scripting the key shapes in-code (`CREATE TABLE` + inserts, `BacpacBuilder` for import tests), never by committing a fixture blob.

## Architecture — load-bearing patterns

Layout: `Storage/` (pages, types, row encoder/decoder, heap, constraints, lock manager + DMVs), `Parser/` (tokenizer, expressions, query planning + execution), `Simulation/` (per-statement-kind partials), `Schemas/` (`SchemaObject` hierarchy + alias/catalog-view/full-text/spatial/xml-schema-collection types), `Errors/` (exception factory partials), root (`Simulated*` ADO.NET front-door + `Simulation` / `Database` / `Schema` / supporting types).

### Storage
8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating; single-column reads via an array-typed schema take the `RowLayout` fast path (per-schema geometry cached by array identity in a `ConditionalWeakTable`, making `RowDecoder.DecodeColumn` O(1) vs two O(columns) walks — the per-row resolvers' path). Type-only `SqlType[]` schemas reach that same fast path through `RowDecoder.ColumnsFor`, which caches the `HeapColumn[]` conversion by schema-array identity — **never convert per call**: a fresh array defeats the layout cache's identity key and re-lays-out the geometry every read (measured at a third of result-drain CPU; the reader-cursor and subquery decode sites are the precedent consumers). Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar/nvarchar/varbinary(MAX)`, `text`/`ntext`/`image`) flow through a parallel 8KB-LOB-page chain. Bounded `varchar/nvarchar/varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits 8060 bytes. Allocation is a flat page list (no IAM/PFS).

### Type system
`SqlType` / `SqlValue` is the storage-layer pair. Three coercion paths: `SqlValue.Coerce` (runtime values), `SqlType.Promote` (static unification for CASE / set ops / COALESCE), `SqlType.PromoteForArithmetic(a, b, op)` (per-operator decimal/integer/money/float result type — single source of truth for `TwoSidedExpression.GetSqlType` + `DecimalArithmetic`; static/runtime parity required since the row encoder rejects type mismatches).

### Selection
`Selection.cs` + `Selection.Execution.cs` are a partial-class pair. `Parse → Selection`, `Execute → SimulatedSqlResultSet`. Correlated subqueries re-run the plan per outer row via `outerResolver` (execute) / `outerTypeResolver` (parse), both walking arbitrary depth via `ParserContext.OuterTypeResolver` + the runtime arg. **Derived tables in FROM are always deferred** (`FromSource.LateralPlan` re-executed per outer row), matching SQL Server's "any FROM derived table can correlate" — needed because outer refs in WHERE/ON resolve through `Run`, not `GetSqlType`.

### Multi-source rows
`FromSource[]`; enumeration rows are `byte[]?[]`, one slot per source, null = NULL-filled outer-join side (LEFT/RIGHT/FULL/OUTER APPLY). Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple`; ambiguous unqualified name → Msg 209. Per-row resolution goes through a per-enumeration `SourceColumnMemo` (name → (source, column), keyed by string reference identity — execution-scoped per the plan-cache shared-plan contract); un-memoized re-resolution was the largest CPU cost of scan-bound joins/aggregates. **Per-row resolver loops use the hoisted-scaffolding pattern**: one mutable-capture tuple slot + one cached *self-referencing lambda* (never a local function passed as its own `selfRecursive` argument — that allocates a delegate per resolution per row; 41% of profile bytes) + one `RuntimeContext` per loop; follow it in executor loops.

### `MultiPartName`
Readonly struct, up to 4 inline slots (SQL Server's grammar limit). API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Baseline.Equals(name.ImmediateQualifier, "INSERTED")`, which folds null into `false`), `Count`, `ToString()`. 5th segment → Msg 4104.

### Exception factories
`SimulatedSqlException` ctor is private; each error case is an `internal static` factory in a topical partial (`TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`). The number lands in `Data["HelpLink.EvtID"]`. **Grep for an existing factory before adding one.**

### Expression evaluation
`Expression.Run(RuntimeContext)` (runtime) and `Expression.GetSqlType(BatchContext, …)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema. Both take a `BatchContext` so database-dependent result types (notably the collation of literal / CAST / function-result strings) stay in parse/runtime parity. `RuntimeContext` bundles `ResolveColumn` (per-row lookup) + `Batch`; expressions needing batch/session/database state read `runtime.Batch.*`. `BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes it. Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

### Context layering
Six scopes, one home each. **Add new state to whichever class matches its true scope** — when in doubt, ask who outlives whom. Field rosters live in the source XML docs; this captures only identity + load-bearing contracts.

- **`Simulation`** = server / instance. Holds `SystemHeapTables`, the `Databases` dict, and `ServerCollationName` (`init`-only; defaults `SQL_Latin1_General_CP1_CI_AS`; mirrors `model.collation` — install-time seed for every new `Database`, both the lazy `"simulated"` seed and collation-less bacpac imports; `init` reflects real immutability). Public surface = `Simulation` ctor + `CreateDbConnection()` + `ImportBacpac()` + `AddRemoteSimulation()` + `ServerCollationName` + `ListenAsync()` → `SimulatedNetworkListener`.
- **`Database`** (internal) = one database. Holds `Schemas`, `CompatibilityLevel`, `CollationName`, the rowversion counter (`@@DBTS`), the MVCC version store, and the principal/permission/extended-property/full-text/DDL-trigger surfaces. `Databases` is seeded at construction with all four system databases — `master` / `tempdb` / `model` / `msdb` (so `USE <systemdb>` / `master.sys.*` / `master.dbo.<proc>` / SSMS's `msdb.dbo.syspolicy_system_health_state` all resolve without an import); the first `CreateDbConnection()` lazily seeds `DefaultDatabaseName` (`"simulated"`) when no *user* database is present, and all four system databases are excluded from the initial-database fallback (`Simulation.SystemDatabaseNames`) so a fresh connection still lands on `simulated`. Database ids are a fixed map — master = 1, tempdb = 2, model = 3, msdb = 4; user databases from 5 in name order — single source of truth is `Simulation.SystemDatabaseIds` + `DbId.DatabasesWithIds`, consumed by `DB_ID`/`DB_NAME`, `sys.databases`, `OBJECT_NAME`, and `DBCC SHRINKDATABASE`. `has_dbaccess` is accessibility-aware: 1 for master/tempdb/msdb/user dbs, 0 for `model` (restricted template), NULL for unknown. `#temp` still routes through the connection's `TempTables`, not the seeded `tempdb`. `USE <db>` switches session (Msg 911 on miss); 3-part names route cross-DB reads (`SELECT * FROM other.dbo.t`), but cross-DB writes raise `NotSupportedException` via `BatchContext.RejectCrossDatabaseMutation` — `USE` first.
- **`Schema`** (internal) = one namespace in a database. Holds the object dicts (`HeapTables` / `Functions` / `Views` / `Procedures` / `Sequences` / `Triggers` — DML triggers share the object namespace) + the type namespace (`TableTypes` / `AliasTypes` / `XmlSchemaCollections`). Schema-qualified refs route through `Database.Schemas[<schema>]`; unqualified falls back to `DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session. Holds `@@`-state (`LastIdentity` = `SCOPE_IDENTITY`/`@@IDENTITY`, `LastStatementRowCount` = `@@ROWCOUNT`, `LastErrorNumber` = `@@ERROR`), `CurrentDatabase` / `CurrentTransaction`, per-session `TempTables` (`#foo`, cleared on Dispose), `NestingLevel` (cap 32), `Spid` (≥51), `SessionIsolationLevel`, `LockTimeoutMillis`, `CurrentExecutingThreadId` (same-thread-deadlock detection). Full roster in the source XML docs.
- **`BatchContext`** (internal, `Parser/`) = one command execution. Owns the `ParserContext` (parse-time scratch) + batch-lifetime runtime state: `Variables`, `TableVariables` (`@t`), `CurrentUndoLog`, `CurrentTableVarUndoLog` (statement-only, disjoint from the tx-scoped log so `ROLLBACK TRAN` skips `@t`), `UdfFrame` / `ProcFrame` (non-null in a UDF/proc body — gates value-form `RETURN`). Exposes the **resolver contract** the parser depends on:
  - `TryResolveTable` — `#foo` → `Connection.TempTables` (any qualifier); `@t` → `TableVariables` (1-part only); else named schema (`dbo` unqualified); `SystemHeapTables` only as flat 1-part fallback.
  - `TryResolveFunction` — 2-/3-part only (Msg 195 on bare 1-part). `TryResolveProcedure` accepts 1-part. `TryResolveTableType` accepts 1-part + `dbo` fallback.
  - `ParseObjectName(context, acceptTableVariable=false)` — 1-4-segment dotted form, compresses empty middle segments. `acceptTableVariable` routes `@t` to a 1-part leaf at DML/FROM sites, rejects it elsewhere (Msg 102 at ALTER/DROP TABLE / TRUNCATE / CREATE / SELECT INTO).
  - Threaded into every `Expression.Run(RuntimeContext)` via `runtime.Batch`.
  - UDF/proc invocation allocates a child `BatchContext` via the body ctor: parameters pre-seed `Variables`, the frame is set, the body text re-tokenizes through a synthesized `SimulatedDbCommand`. UDF bodies discard yielded result sets; proc bodies forward them.
- **`StatementContext`** (internal, `Parser/`) = the dispatch loop's per-statement frame. Allocated once per batch and overwritten at the top of each iteration; holds `UtcNow` (the per-statement freeze the time scalars read).

**Don't stack misfit state into these buckets unthinkingly**: if no scope fits, introduce the missing one, don't squat on a neighbor.

## Conventions that fail builds

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;` (`static readonly` for static-singleton). Overrides, abstracts, interface impls exempt.
- **SSS002**: a `readonly` field in a non-public type whose declared type is a strict supertype of its initializer should use the concrete type. Public types, value-typed initializers (boxing), const, and uninitialized fields exempt.
- **SSS003**: `string.ToUpperInvariant()`/`ToLowerInvariant()` as a `switch`'s *governing expression* allocates a temp string. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on that.
- **SSS004**: 2+ `if`/`else if` branches of shape `<sameScrutinee> is <SameType> { <SameProperty>: … }` should be a single `switch` (fuses isinst + ldfld; the if-chain repeats both per arm).
- **SSS005**: a `switch` (expr or statement) whose arms are all single string/numeric constants must be sorted — strings ordinal, numbers numerically (`_`/`null` excluded). Exempts a guard, or/relational/recursive/`var` pattern, or **enum/`char`/`bool`** constant (those order by meaning). A switch deliberately ordered by meaning (time-unit magnitude, host level): `#pragma warning disable SSS005` + one-line rationale rather than sort.
- **SSS006**: 2+ consecutive self-returning `StringBuilder` calls (`Append`/`Insert`/`Replace`/…) on the same builder, result discarded (bare or `_ =`), should be one fluent chain. An already-chained statement peels to its base receiver, so `sb.Append(a).Append(b)` beside `sb.Append(c)` merges. Only when the base is side-effect-free (identifier / `this.field` / dotted); call-valued roots exempt. Comments between statements don't exempt — slot them into the chain.
- **SSS007**: a `switch` **expression** over `Span<char>`/`ReadOnlySpan<char>` whose arm is a discard guard `_ when <governing>.SequenceEqual("literal")` should be the constant pattern `"literal"` — a span-of-char switch matches string constants directly since C# 11. Only the pure single-invocation guard whose receiver is the switch's governing expression is flagged (negated / `&&`-combined / different-span conditions are left alone). Enforcement companion to SSS003 (which creates the `stackalloc Span<char>` scrutinee); `ResolveBuiltIn` in `Parser/Expression.cs` is the reference shape.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; typed asserts over generic.

## Style notes

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly.
- **No comments inside expression chains** — IDE0055 fails on comments in ternary chains or between `=>` and body. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (SSS001 generalized).
- **No internal `<see cref>` in public-API XML docs** — a cref to an internal type dangles in consumer IntelliSense and implies stability for a name we're free to rename; state the contract in prose (`"an unrecognized collation name raises ArgumentException"`, not a cref to internal `Collation.IsRecognized`).
- **No conversation-scratch framing in code/docs/commits** — "Camp A/B", "this bundle", "Stage 1/2", "as we discussed" mean nothing to a future reader; describe behavior/motivation absolutely, cross-reference a sibling by the behavior it names, not the work-stage. Pre-existing repo terms (e.g. transactions' "Bundle 1/2") are load-bearing — leave them.
- **AssemblyHooks**: each test project's `AssemblyHooks.cs` has a `static [TestClass] [AssemblyInitialize]` warming shared init once before the parallel run. Without it, the first test batch races to init hot shared state and serializes on contention. The analyzer-tests' Roslyn-cache warm-up is the worst case (~3x slowdown); the pattern generalizes to any expensive first-touch shared resource.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature.
- **Control flow signals via flags, not exceptions.** BREAK / CONTINUE / RETURN / THROW set a typed `BatchContext` flag (`LoopControl` + skip predicate), never a signal exception — `yield return` inside try/catch composes badly with iterator dispatch, and the flag reuses the skip-mode plumbing that no-ops un-taken IF branches. Parse-time structural checks (BREAK outside WHILE → Msg 135) fire regardless of skip state. Exceptions stay for true errors.

## What's modeled

The `*.Tests` and `*.Tests.EFCore` suites are the authoritative behavior contract. The [Feature reference](#feature-reference) index maps every modeled area to its deep-dive — **presence there means it's modeled**; read the linked doc on demand when working in that area. The two small subsections below are inline-only notes with no deep-dive.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers seven SqlParameter-downcast pairs: `DateOnly→date`, `DateTime→date`, `DateTime→smalldatetime`, `TimeOnly→time(N)`, `TimeSpan→time(N)`, `decimal→money`, `decimal→smallmoney`. Without the adapter these throw at SaveChanges. MAX-string family flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY/DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) all enforced.

### Feature reference

Per-feature deep-dives live under `docs/claude/`. Each entry below is a trigger: read the linked file on demand when working in the matching area.

- **Built-in scalars** — math; date (DATETRUNC / DATE_BUCKET / SWITCHOFFSET / TODATETIMEOFFSET / `*FROMPARTS` / AT TIME ZONE / current-time); string (CONCAT / SOUNDEX / TRANSLATE / STRING_ESCAPE / DIFFERENCE); CHOOSE / IIF; bit (BIT_COUNT / GET_BIT / SET_BIT / shifts); CHECKSUM / BINARY_CHECKSUM; FORMAT / FORMATMESSAGE; RAND; STRING_SPLIT / GENERATE_SERIES; COMPRESS / DECOMPRESS; PWDENCRYPT / PWDCOMPARE / LOGINPROPERTY; sys.fn_varbintohexsubstring / sys.fn_varbintohexstr (varbinary→hex, sys-qualified system functions); `@@`-constants + HOST_NAME / APP_NAME / GETANSINULL / ORIGINAL_DB_NAME; session-state (SESSION_CONTEXT / sp_set_session_context / CONTEXT_INFO / CONNECTIONPROPERTY / SESSIONPROPERTY); SQL_VARIANT_PROPERTY → [`scalars.md`](docs/claude/scalars.md).
- **`SqlType.Promote` / `PromoteForArithmetic` / decimal precision-scale / int↔string promotion / string+binary literal value-width typing + width algebra (concat sum-cap, CASE/COALESCE/set-op max, per-function widths)** → [`arithmetic.md`](docs/claude/arithmetic.md).
- **`Cast` / coercion error paths** (CAST/CONVERT narrow targets, TRY_CAST/TRY_CONVERT swallow set, PARSE/TRY_PARSE culture-aware parsing) → [`casting.md`](docs/claude/casting.md).
- **`SimulatedDbDataReader` client surface** (typed accessors, `datetime` client-millisecond rounding, `GetOrdinal` precedence, GetBytes/GetChars materialization divergence) → [`data-reader.md`](docs/claude/data-reader.md).
- **`Selection`, aggregates, window functions, set ops, CASE, OFFSET/FETCH** → [`query.md`](docs/claude/query.md).
- **Subqueries** (EXISTS / IN(SELECT) / scalar / quantified ANY-SOME-ALL, three-valued rules, arbitrary-depth correlation, set ops in subquery contexts) → [`subqueries.md`](docs/claude/subqueries.md).
- **JOIN / APPLY** — INNER/LEFT/RIGHT/FULL/CROSS + CROSS/OUTER APPLY, ANSI-89 comma-FROM, EF `LeftJoin`/`RightJoin` routing (no LINQ `FullJoin`, so FULL is raw-SQL-only); `JoinDriver` equi-join hash fast path + `SqlValueKey` keying, nested-loop fallback, comma/`CROSS JOIN`+WHERE → equi-`INNER` rewrite, RIGHT/FULL materialization + derived-table-right, `JoinDiagnostics` guard → [`joins.md`](docs/claude/joins.md).
- **`PIVOT` / `UNPIVOT`** — PIVOT desugars to grouped conditional aggregation (implicit grouping = inner columns minus FOR + aggregate-arg), UNPIVOT is a NULL-skipping unfold; both attach as a postfix FROM-source wrapper on the derived-table `LateralPlan` seam → [`pivot.md`](docs/claude/pivot.md).
- **UPDATE / DELETE / INSERT…SELECT / SELECT…INTO / rowversion** (`@@DBTS` / `MIN_ACTIVE_ROWVERSION`) **/ identity helpers** (`@@IDENTITY` / `SCOPE_IDENTITY` / `IDENT_CURRENT`/`_INCR`/`_SEED`) **/ `@@ROWCOUNT` / `ROWCOUNT_BIG` / OUTPUT / MERGE** → [`dml.md`](docs/claude/dml.md).
- **Variables, control flow (IF/WHILE/BREAK/CONTINUE/RETURN), TRY/CATCH+THROW+ERROR_*, `@@ERROR` / `@@TRANCOUNT` / `XACT_STATE`, PRINT, WAITFOR** → [`control-flow.md`](docs/claude/control-flow.md).
- **Cursors** (`DECLARE … CURSOR` / `OPEN` / `FETCH` / `CLOSE` / `DEALLOCATE`, STATIC / KEYSET / DYNAMIC sensitivity, scroll fetches, `@@FETCH_STATUS` / `@@CURSOR_ROWS` / `CURSOR_STATUS`, `WHERE CURRENT OF`; GLOBAL / LOCAL scope + scope-aware resolution, cursor variables (`DECLARE @c CURSOR` / `SET @c = CURSOR …` refcounted, cursor OUTPUT proc params), `FOR UPDATE OF` gating (Msg 16932), SCROLL_LOCKS cursor-scoped U locks + OPTIMISTIC conflict detection (Msg 16947/16934), TYPE_WARNING (Msg 16956)) → [`cursors.md`](docs/claude/cursors.md).
- **CTE shapes / recursive-CTE error handling** → [`ctes.md`](docs/claude/ctes.md).
- **JSON** (JSON_VALUE / JSON_QUERY / JSON_MODIFY / JSON_OBJECT / JSON_ARRAY / JSON_OBJECTAGG / JSON_ARRAYAGG / JSON_PATH_EXISTS / ISJSON / OPENJSON) → [`json.md`](docs/claude/json.md).
- **Name resolution, schema lookup, CREATE / DROP / ALTER SCHEMA TRANSFER, `OBJECT_ID`/`_NAME`/`_SCHEMA_NAME` / `SCHEMA_ID`/`_NAME` / `DB_ID`/`_NAME`, cross-DB reads** → [`schemas.md`](docs/claude/schemas.md).
- **System metadata surfaces** (sys.* / INFORMATION_SCHEMA.*; `OBJECT_DEFINITION` / `OBJECTPROPERTY(EX)` / `COLUMNPROPERTY` / `INDEXPROPERTY` / `STATS_DATE` / `TYPEPROPERTY` / `SERVERPROPERTY` / `COL_NAME` / `TYPE_NAME` / `PARSENAME`) → [`catalog-views.md`](docs/claude/catalog-views.md).
- **Scalar UDFs / TVFs / views / stored procs / dynamic SQL / `@@NESTLEVEL` / `@@PROCID`** → [`programmable.md`](docs/claude/programmable.md).
- **`#foo` / `##foo` routing, DROP TABLE, TRUNCATE TABLE** → [`temp-tables.md`](docs/claude/temp-tables.md).
- **`DECLARE @t TABLE`, table-variable DML, `OUTPUT … INTO`** → [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE TYPE … AS TABLE`, TVP params + `READONLY`, ADO.NET TVP** → [`table-valued-parameters.md`](docs/claude/table-valued-parameters.md).
- **`CREATE TYPE … FROM <builtin>` (scalar alias types / UDDTs), multi-part type-references** → [`alias-types.md`](docs/claude/alias-types.md).
- **`sp_addextendedproperty` / `fn_listextendedproperty` / `sys.extended_properties`** → [`extended-properties.md`](docs/claude/extended-properties.md).
- **`CREATE/ALTER/DROP SEQUENCE`, `NEXT VALUE FOR`, `sys.sequences`** → [`sequences.md`](docs/claude/sequences.md).
- **DML + DDL triggers** (`CREATE TRIGGER` incl. `ON DATABASE`, `INSERTED`/`DELETED`, `TRIGGER_NESTLEVEL`, `sys.triggers`) → [`triggers.md`](docs/claude/triggers.md).
- **CHECK / PRIMARY KEY / UNIQUE enforcement** (Msg 547 / 8141 peer-reference gate, Msg 2627 vs 2601, NULLs-equal UNIQUE) → [`constraints.md`](docs/claude/constraints.md).
- **`FOREIGN KEY` + referential actions, `sys.foreign_keys`** → [`foreign-keys.md`](docs/claude/foreign-keys.md).
- **`PERIOD FOR SYSTEM_TIME`, `FOR SYSTEM_TIME ALL/AS OF`, history sibling, `temporal_type`** → [`temporal-tables.md`](docs/claude/temporal-tables.md).
- **`ALTER TABLE` ADD/DROP/ALTER COLUMN + CONSTRAINT (incl. trust toggling)** → [`alter-table.md`](docs/claude/alter-table.md).
- **`CREATE INDEX` (UNIQUE / CLUSTERED / INCLUDE / WHERE filter), indexed views (`CREATE INDEX ON <schema-bound view>`, Msg 1939/1940/1941 gates, live Msg 2601 uniqueness enforcement, NOEXPAND), `sys.indexes`, `DBCC SHOW_STATISTICS … WITH HISTOGRAM`** → [`indexes.md`](docs/claude/indexes.md).
- **Table hints (`WITH (NOLOCK …)`) + statement `OPTION (…)` hints** → [`query-hints.md`](docs/claude/query-hints.md).
- **Heap page lifecycle** (reclamation/reuse, tail-only shrink, `DBCC SHRINKDATABASE`/`SHRINKFILE`) → [`heap-storage.md`](docs/claude/heap-storage.md).
- **Per-`Simulation` plan cache** (single-SELECT `Selection` reuse keyed by text + db + param-type sig + QUOTED_IDENTIFIER setting, `SchemaVersion`-stamped invalidation, inline-in-SELECT-arm promotion since the iterator's post-yield code is unreachable on a non-draining `ExecuteReader`; `VariableReference` Run-time slot lookup is the co-fix) → [`plan-cache.md`](docs/claude/plan-cache.md).
- **Transactions** (statement atomicity, undo log, BEGIN/COMMIT/ROLLBACK/SAVE, `@@TRANCOUNT`, identity/rowversion log bypass, temp-table logging asymmetry) → [`transactions.md`](docs/claude/transactions.md).
- **Locking, MVCC, SNAPSHOT/RCSI, deadlock/timeout, lock-related DMVs** → [`locking.md`](docs/claude/locking.md).
- **Application locks** (`sp_getapplock` / `sp_releaseapplock` / `APPLOCK_MODE` / `APPLOCK_TEST`, return-code-vs-raised-error asymmetry, EF 9/10 `Database.Migrate()`'s `__EFMigrationsLock`) → [`app-locks.md`](docs/claude/app-locks.md).
- **`hierarchyid` type** (OrdPath storage, byte-identical CAST/wire/DATALENGTH, tier table + opaque passthrough) → [`hierarchyid.md`](docs/claude/hierarchyid.md).
- **`GRANT` / `REVOKE` / `DENY`, principal DDL (incl. server logins: `CREATE/ALTER/DROP LOGIN`), fixed-principal seed, principal scalars** (`USER_NAME` / `SUSER_SNAME` / `DATABASE_PRINCIPAL_ID` / `CURRENT_USER` / `SESSION_USER` / `ORIGINAL_LOGIN` / `HAS_PERMS_BY_NAME` / `IS_MEMBER`) → [`permissions.md`](docs/claude/permissions.md).
- **`CREATE FULLTEXT CATALOG`/`INDEX`, `CONTAINS`/`FREETEXT` rejection** → [`full-text.md`](docs/claude/full-text.md).
- **`xml` type, XML schema collections, XML methods (`.value()` / `.nodes()` / `.query()` / `.exist()` via an XQuery-subset evaluator; `.modify()` XML-DML skip-with-diagnostic), XML indexes** → [`xml.md`](docs/claude/xml.md).
- **`geography` / `geometry` types, spatial methods, spatial indexes** → [`spatial.md`](docs/claude/spatial.md).
- **`ALTER DATABASE SET <option>` accept-list + database-level `COLLATE` clause** → [`database-options.md`](docs/claude/database-options.md).
- **Per-column / per-expression collation, coercibility precedence, Msg 468/457 cross-collation enforcement, recognized catalog, `#temp` inheritance, name regimes (fullwidth/decomposed fold, hash canonicalization, variable names)** → [`collations.md`](docs/claude/collations.md).
- **New top-level statement parser or dispatch-loop separator rules, double-quoted identifiers / QUOTED_IDENTIFIER** → [`grammar.md`](docs/claude/grammar.md) + [`control-flow.md`](docs/claude/control-flow.md).
- **BACPAC import** (`Simulation.ImportBacpac` — multi-database via repeated calls, `BacpacImportOptions`, `ModelXmlReader` dispatcher, BCP wire format, `BacpacBuilder` test harness) → [`bacpac-loader.md`](docs/claude/bacpac-loader.md).
- **Linked servers** (`Simulation.AddRemoteSimulation`, `sp_addlinkedserver` / `sp_dropserver`, four-part FROM routing through the remote's ADO.NET pipeline, `OPENQUERY(server,'query')` ad-hoc pass-through + compile-time schema discovery, `sys.servers`) → [`linked-servers.md`](docs/claude/linked-servers.md).
- **TDS network endpoint** (`Simulation.ListenAsync` → `SimulatedNetworkListener`; real SqlClient over loopback TCP+TLS; SQLBatch/RPC/TM, no bulk; `sp_cursor*` API-server-cursor RPC family; credential enforcement via the `CREATE LOGIN` registry, Msg 18456 on mismatch; EF via plain `UseSqlServer`; oracles = `*.Tests.SqlClient` + `*.Tests.Smo`, the real-SMO consumer oracle) → [`tds-endpoint.md`](docs/claude/tds-endpoint.md).

## Not modeled

- **Key-range locks** — sole remaining phase 4+ deferral. See [`locking.md`](docs/claude/locking.md) for what does ship (full 8-mode matrix, SNAPSHOT/RCSI, DMVs, Msg 1205/1222/3952/3960).
- `BEGIN DISTRIBUTED TRANSACTION` → `NotSupportedException` at dispatch. `BEGIN TRANSACTION <name> WITH MARK 'm'` → **Msg 319** at parse (`WITH` not accepted here); bare named transactions (`BEGIN TRAN t1`) ship.
- **`CREATE DATABASE`** / **`CREATE ASSEMBLY`** — Msg 102 at parse; databases arrive via `Simulation.ImportBacpac`, CLR isn't modeled.
- **Cross-database / cross-server DML** (`INSERT`/`UPDATE`/`DELETE`/`MERGE` through a 3-/4-part name) → `NotSupportedException` via `BatchContext.RejectCrossDatabaseMutation` — `USE <db>` first. Cross-DB / four-part *reads* (SELECT/JOIN) ship; catalog-view reads via four-part names (`srv.db.sys.tables`) fall to Msg 208. See [`schemas.md`](docs/claude/schemas.md), [`linked-servers.md`](docs/claude/linked-servers.md).
- **`SET <option>` accept-list** (`Simulation.Set.cs`): XACT_ABORT, all ANSI/session-state toggles, `STATISTICS {IO|TIME|XML|PROFILE}`, value-taking options (`TEXTSIZE`/`DATEFIRST`/etc.) — all parse-and-discard. Unknown SET → Msg 195. `SET @v`, `IDENTITY_INSERT`, `NOCOUNT`, `LOCK_TIMEOUT`, `TRANSACTION ISOLATION LEVEL`, `QUOTED_IDENTIFIER` (and `ANSI_DEFAULTS`'s QI component) carry semantic effect.
- **`ALTER DATABASE … SET` / `COLLATE`** — see [`database-options.md`](docs/claude/database-options.md). Most options parse-and-discard; `COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT` are load-bearing.
- `RANGE BETWEEN <N> PRECEDING/FOLLOWING` numeric-offset — Msg 4194 (real's licensed-feature rejection). `ROWS` numeric-offset ships. Default frame with ORDER BY = `RANGE UNBOUNDED PRECEDING TO CURRENT ROW`; without it, whole partition. LAST_VALUE matches real's default-frame semantic.
- Recursive-part feature restrictions (Msg 460 / 461 / 462 / 467 / 465) — silently accepted with possibly-wrong semantics. Apps exercising these hit rejection on real SQL Server too.
- `LEN(ntext)` raising Msg 8116; legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- **MERGE gaps**: CTE-precedes-MERGE bare-name form and `MERGE INTO <updatable view>` both ship (the latter at full parity with UPDATE/INSERT/DELETE-through-view). `MERGE … OUTPUT` through a view → `NotSupportedException` (view-column projection through `INSERTED`/`DELETED` deferred). See [`dml.md`](docs/claude/dml.md).
- `UNIQUE` on a *non-persisted* computed column (PK/UNIQUE on `PERSISTED` ships). Msg 4936 determinism gate for PERSISTED computed columns not enforced.
- Heap allocation tracking (flat page list, no IAM/PFS).
- **Table-variable named constraints / FKs** — Msg 102 (real's `DECLARE @t TABLE` grammar restriction). Multi-variable DECLARE with a table variable, mixed scalar+table DECLARE, `SET IDENTITY_INSERT @t ON` also reject. Column features (IDENTITY / UNIQUE / inline + table-level CHECK / computed / rowversion) all ship — see [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE SCHEMA AUTHORIZATION`** — `NotSupportedException` (no principal model on schemas). `DROP SCHEMA` + `ALTER SCHEMA TRANSFER` ship — see [`schemas.md`](docs/claude/schemas.md).
- **`CREATE SCHEMA <schema_element>` greedy form** — dispatches trailing CREATE/GRANT as their own statements, not part of the CREATE SCHEMA. Same end state for the common idiom; mismatched-grammar trailers raise.
- **`CREATE SCHEMA sys` / `INFORMATION_SCHEMA`** + **`CREATE TABLE sys.*` / `INFORMATION_SCHEMA.*`** — both raise Msg 2760 (real's permission framing). The schemas exist as catalog-view hosts.
- T-SQL `GOTO` / labels — `IF` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN` ship; unconditional jumps don't.
- **Programmable-object top-level gaps**: CLR functions, logon triggers, INSTEAD OF UPDATE/DELETE on non-updatable views, JOIN-view single-base UPDATE/DELETE, OUTPUT through views, multi-source alias-form UPDATE/DELETE through views (Msg 4405). Natively-compiled + CLR procedures ship at parser-fidelity only (ATOMIC boundary → session isolation; CLR bodies parse but `EXEC` no-ops). See [`programmable.md`](docs/claude/programmable.md).
- **PRINT semantic gaps** — Msg 1046 subquery-in-operand not raised; non-string formatting uses `CoerceTo(varchar(8000))` not PRINT style 0; 8000/4000-byte truncation not enforced. The `InfoMessage` surface ships (`SimulatedDbConnection.InfoMessage`).
- **`ALTER TABLE` out-of-scope**: DROP PERIOD FOR SYSTEM_TIME, REBUILD, SWITCH PARTITION, `ALTER COLUMN ADD/DROP {PERSISTED|MASKED|ROWGUIDCOL|SPARSE}`, multi-constraint ADD. (ALTER COLUMN of IDENTITY to non-integer → Msg 2749; of a period column → Msg 13599.) Modeled shapes in [`alter-table.md`](docs/claude/alter-table.md).
- **`hierarchyid` OrdPath tiers beyond ±~5000** — the wide 6-byte tiers (ordinals ≥ 5200 / ≤ -4169) aren't in the encoder/decoder table; `Parse`/`ToString` of one raises `NotSupportedException`, but storage/BACPAC round-trip such bytes opaquely. Everything else (OrdPath storage, byte-identical `CAST … AS varbinary` both directions, TDS UDT wire, `DATALENGTH`, memcmp ordering, dotted forms, negatives to -4168) ships. (`geography` / `geometry` byte-identical CAST + TDS UDT wire form also ship — DacFx bacpac export of WWI's spatial columns works.) See [`hierarchyid.md`](docs/claude/hierarchyid.md), [`spatial.md`](docs/claude/spatial.md).
- **Query hints gaps**: FROM-source `(unknown)` without alias falls to Msg 102 (real raises Msg 207/321); `FORCESEEK(name(cols))` nested-form name validation not run. Surface in [`query-hints.md`](docs/claude/query-hints.md).

## Quirks (modeled, not byte-identical to SQL Server)

Cross-cutting divergences with no single feature-doc home; feature-specific quirks live in their `docs/claude/` deep-dive's divergence section (via [Feature reference](#feature-reference)).

- `decimal` / `numeric`: backed by .NET `decimal`. Values needing more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15`/`G7`, not SQL Server's `1e+015`-style scientific.
- Auto-generated constraint names: PK/UNIQUE shape `PK__<table8>__<16hex>` / `UQ__<table8>__<16hex>` (16-hex 64-bit FNV-1a); CK/FK/DF shape `CK__<table8>__[<col8>__]<8hex>` (8-hex 32-bit FNV-1a). Deterministic across runs, distinct from SQL Server's object-id-derived hex (won't byte-match).
- Client-surface streaming and heap-page divergences live in [`data-reader.md`](docs/claude/data-reader.md) and [`heap-storage.md`](docs/claude/heap-storage.md).
