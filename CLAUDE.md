# Claude Working Notes

Auto-loaded orientation.
`README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation.
An **ADO.NET stand-in for `Microsoft.Data.SqlClient`** — consumers create a `Simulation`, get a `SimulatedDbConnection` via `CreateDbConnection()`, and use it with (e.g.) `Microsoft.EntityFrameworkCore.SqlServer` instead of SqlClient over the wire.
The full ADO.NET concrete-pipeline chain (`SimulatedDb{Connection,Command,Parameter,ParameterCollection,DataReader,Transaction}` + `SimulatedSqlException` + the info-message family) is public with `new`-shadowed strongly-typed returns, mirroring SqlClient's shape so consumers downcast and reach concrete properties identically.
Public surface beyond that chain is intentionally minimal so internals stay free to refactor; `QualityTests.PublicApiWhitelist` is authoritative and fails on unintended expansion — resist adding to it.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`.
EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter` (the simulator's connection isn't a `SqlConnection`).

**Packaging:** only `SqlServerSimulator` publishes — the root `Directory.Build.props` defaults every project to `IsPackable=false` and the package project alone opts back in.
The adapter stays in-repo-but-unpublished as a deliberate demand signal, so don't pitch publishing it without a user request.

## Operating goal

High-fidelity emulation.
Authenticity over desirability — when SQL Server is quirky or lossy (CP1252 best-fit and `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it.
Fidelity has caught real upstream bugs (EF Core 10's `Math.Sign(decimal)` mismatch) — when a probe feels wrong, verify against the reference before relaxing the simulator; matching a real cross-stack quirk is the feature, not a bug.
EF Core trusts the simulator end-to-end (`*.Tests.EFCore` is the regression oracle, must stay green).
Beyond that floor, priority is broad coverage weighted by popularity (user wins) × ease (thoroughness wins).
The living [`docs/claude/backlog.md`](docs/claude/backlog.md) — missing features, fidelity gaps, design choices, exclusions — is ordered by that weighting non-authoritatively; read it before new feature work or pitching a built-in.

**Nothing is permanently out of scope.**
Every gap is a *not yet*: scope statements in this file and under `docs/claude/` are descriptive snapshots of what's built, never decisions to exclude.
The picking-of-battles is a cost ordering, not a boundary — read an unbuilt feature as queued, not closed.
The rare genuinely-settled call is marked **settled — don't re-pitch** and carries its reason; absent that marker, anything is fair game.

## Feature-bundle workflow

1. **Probe.**
   Behavior questions get answered against a real SQL Server 2025 reference (connection in user memory).
   Probe scaffolds live in `/tmp/<probe-name>/`, deleted after.
   Only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`.
   EF Core probes clean up with `DROP TABLE IF EXISTS`, **never** `Database.EnsureDeleted()`.
   A probe reveals *server* behavior, not *doc* content: don't write "Microsoft's docs claim X but…" without reading the MSDN page.
2. **Surface decisions.**
   Before writing code, if there are non-obvious design choices to be made, provide them to the user with a recommendation.
   Ambitious, high-effort options will always be chosen by the user and don't need to be surfaced.
3. **Implement + test.**
   `*.Tests` exercises public API; `*.Tests.EFCore` validates the oracle.
   `*.Tests.Internal` only for things genuinely unreachable from public SQL.
   `*.Tests.EFCore` must drive EF Core's LINQ→SQL emission (and C#-side surface like `HasTrigger` / `UseHiLo` / `UseTpcMappingStrategy` that shifts the emit shape across EF versions), **not** hand-written SQL through `FromSql*` / `SqlQuery` — that's parser testing in disguise (covered once by `EFCoreFromSql.cs`).
4. **Update CLAUDE.md and `docs/claude/`.**
   Move bullets between What's-modeled / Not-modeled as scope changes.
   Deep-dives — *and feature-specific quirks/divergences* — live under `docs/claude/`; the Quirks section here is only for cross-cutting divergences with no feature-doc home.
5. **Single-sentence commit.**
   Squashes capture end state.
   Message = *what changed* + *why*; omit CI-visible status (test counts, build state) — GitHub surfaces it and it goes stale.
   Don't run `git commit` — the user holds signing credentials.

## Build / test

```
dotnet build
dotnet test
```

Projects live under `src/` (`SqlServerSimulator`, `SqlServerSimulator.EFCore`, `SqlServerSimulator.Analyzers`, `Example`) and `tests/`; the sln and both props files stay at the repo root, so `dotnet build` / `dotnet test` run from the root unchanged.
`tools/` holds local tooling, in the sln under a `tools` folder so CI compiles it but outside the analyzer and test gates — [`tools/sqllogictest/`](tools/sqllogictest/README.md) is the differential sweep whose in-process replay (`./tools/sqllogictest/replay.sh`, about a minute) catches regressions across millions of generated queries the committed suites never write; run it before committing a change to parsing, binding or expression evaluation.
Shared build settings live in the root `Directory.Build.props` (TargetFramework, nullable, warnings-as-errors, `EnforceCodeStyleInBuild=true` — so `dotnet build` runs the IDE / SSS / MSTEST analyzers and fails on violations); package versions are centralized in `Directory.Packages.props` (NuGet CPM), with deliberate per-project divergences as visible `VersionOverride`s (Tests.Smo pins SqlClient 5.1.x, SMO's supported line, while Tests.SqlClient tests the current 7.x — the reason those two projects stay separate).
Csprojs carry only per-project content.
No separate `dotnet format` pass — it catches nothing build doesn't.
CI matrix: Debug + Release.
`obj/` permission errors mean building outside the dev container; `rm -rf obj/ bin/` clears them.
Building while another long-lived `dotnet` process runs can fail with tens of thousands of bogus `CS0246` "type or namespace not found" errors — MSBuild node reuse racing it, not a code or restore problem, and `rm -rf obj/ bin/` does not clear it.
The tell is that identical back-to-back invocations alternate between failing and clean; build with `MSBUILDDISABLENODEREUSE=1` (add `-m:1` if it persists), or don't build concurrently.
Line endings are LF everywhere: `.gitattributes` `eol=lf` forces an LF working tree on Windows and Linux alike, and `.editorconfig` `end_of_line = lf` makes IDE0055 enforce it — so a CRLF checkout fails the formatter.

MSBuild's share of a round trip is a modest fixed cost and the tests themselves are the bulk of it — `SqlServerSimulator.Tests` most of all — so `--filter` saves real time on the csproj path too.
That balance depends on generated directories staying out of MSBuild's default item globs (`DefaultItemExcludes` in the root `Directory.Build.props`): the globs walk a project folder on every evaluation, so a grown `TestResults` tree becomes the dominant cost of even a no-op build.
`dotnet msbuild <proj> /profileevaluation:<file>` names the offending glob if a round trip ever goes mysteriously slow.

**Inner loop: invoke the built test DLL directly**, which skips MSBuild entirely:

```
dotnet test tests/SqlServerSimulator.Tests/bin/Debug/net10.0/SqlServerSimulator.Tests.dll --filter "FullyQualifiedName~Foo"
```

That's somewhat faster than `dotnet test <csproj> --no-build --filter`, which pays MSBuild's fixed cost on top; either serves a tight loop.
Rebuild the one project (`dotnet build src/SqlServerSimulator/SqlServerSimulator.csproj`) when the DLL goes stale; full `dotnet build` + `dotnet test` is the pre-commit checkpoint.
All tests use method-level parallelism.

**No large binary files in the repo** (bacpacs included — the WWI/AW `.bacpac` fixtures live gitignored under `.vs/`, local-only).
Tests get their data by scripting the key shapes in-code (`CREATE TABLE` + inserts, `BacpacBuilder` for import tests), never by committing a fixture blob.

## Architecture

Layout: `Storage/` (pages, types, row encoder/decoder, heap, constraints, lock manager + DMVs), `Parser/` (tokenizer, expressions, query planning + execution), `Simulation/` (per-statement-kind partials), `Schemas/` (`SchemaObject` hierarchy + alias/catalog-view/full-text/spatial/xml-schema-collection types), `Errors/` (exception factory partials), root (`Simulated*` ADO.NET front-door + `Simulation` / `Database` / `Schema` / supporting types).

### Storage
8KB heap pages; rows are encoded bytes navigated column-by-column without rehydrating.
Every non-NULL variable-length column carries a 1-byte inline/pointer marker; LOB-eligible types flow through a parallel LOB-page chain, and bounded `varchar/nvarchar/varbinary(N)` start inline, with the largest pushed off-row until the row fits 8060 bytes.
Allocation is a flat page list (no IAM/PFS).
**The scan and encode/decode paths are measured hot spots**: `Heap.EnumerateRowsWithAddress` (every table scan), `RowDecoder.ColumnsFor` / `DecodeColumn`, and the `RowEncoder.EncodeRow` / `EncodeRowInto` overloads carry XML docs saying what not to undo and why.
Read them before touching those members → [`heap-storage.md`](docs/claude/heap-storage.md).

### Type system
`SqlType` / `SqlValue` is the storage-layer pair.
Three coercion paths: `SqlValue.Coerce` (runtime values), `SqlType.Promote` (static unification for CASE / set ops / COALESCE), `SqlType.PromoteForArithmetic(a, b, op)` (per-operator decimal/integer/money/float result type — single source of truth for `TwoSidedExpression.GetSqlType` + `DecimalArithmetic`; static/runtime parity required since the row encoder rejects type mismatches).

### Selection
`Selection.cs` + `Selection.Execution.cs` are a partial-class pair.
`Parse → Selection`, `Execute → SimulatedSqlResultSet`.
Correlated subqueries re-run the plan per outer row via `outerResolver` (execute) / `outerTypeResolver` (parse), both walking arbitrary depth via `ParserContext.OuterTypeResolver` + the runtime arg.
**Derived tables in FROM are always deferred** (`FromSource.LateralPlan`), matching SQL Server's "any FROM derived table can correlate" — needed because outer refs in WHERE/ON resolve through `Run`, not `GetSqlType`.
A deferred source whose rows can't change across one enumeration — any **non-leftmost, non-APPLY** `LateralPlan` source (derived table / CTE / view / generator), plus every catalog view — is executed once per enumeration by `MaterializeUncorrelatedDeferredSources` instead of once per left-side row, which also makes it hash-join-eligible; APPLY and a `NEWID()`-drawing plan keep per-row execution — see [`joins.md`](docs/claude/joins.md#deferred-sources-materialize-once-per-enumeration).

### Multi-source rows
`FromSource[]`; enumeration rows are `byte[]?[]`, one slot per source, null = NULL-filled outer-join side.
Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple` (ambiguous unqualified name → Msg 209), memoized per enumeration in `SourceColumnMemo`, which is execution-scoped per the plan-cache shared-plan contract.
**Per-row resolver loops use the hoisted-scaffolding pattern**: one mutable-capture tuple slot + one cached _self-referencing lambda_ + one `RuntimeContext` per loop.
Never pass a local function as its own `selfRecursive` argument; that allocates a delegate per resolution per row, which once measured as 41% of profile bytes.
The same rule reaches the _arrays_ a per-row loop fills (grouping keys, hash-join bucket keys, bounded-`TOP` projections): write into reused scratch and copy out only where something retains them.
The aggregate executor accumulates straight off the enumeration for a single grouping set, which is observable in _which_ error a row raises → [`query.md`](docs/claude/query.md#streaming-accumulation-and-where-an-error-surfaces).

### `MultiPartName`
Readonly struct, up to 4 inline slots.
API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Baseline.Equals(name.ImmediateQualifier, "INSERTED")`, which folds null into `false`), `Count`, `ToString()`.

### Matching a name against a fixed vocabulary
Three matchers: string constants / `Ordinal[IgnoreCase]` compares, `BuiltInToken` (spec-defined tokens), and a `Collation` (mandatory for user identifiers).
Pick by the semantics the site needs, then keep the shape simple: a short ordinal chain beats a `Frozen*` lookup at accept-list size → [`collations.md`](docs/claude/collations.md#fixed-tokens-builtintoken).

### Exception factories
`SimulatedSqlException` ctor is private; each error case is an `internal static` factory in a topical partial (`TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`).
The number lands in `Data["HelpLink.EvtID"]`.
**Grep for an existing factory before adding one.**

### Expression evaluation
`Expression.Run(RuntimeContext)` (runtime) and `Expression.GetSqlType(BatchContext, …)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema.
Both take a `BatchContext` so database-dependent result types (notably the collation of literal / CAST / function-result strings) stay in parse/runtime parity.
`RuntimeContext` bundles `ResolveColumn` (per-row lookup) + `Batch`; expressions needing batch/session/database state read `runtime.Batch.*`.
`BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes it.
Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

### Context layering
Six scopes, one home each.
**Add new state to whichever class matches its true scope** — when in doubt, ask who outlives whom.
Field rosters live in the source XML docs; this captures only identity + load-bearing contracts.

- **`Simulation`** = server / instance.
  Holds `SystemHeapTables`, the `Databases` dict, and the `init`-only `ServerCollationName`, which seeds every new `Database` ([`collations.md`](docs/claude/collations.md#server-level-seed-simulationservercollationname)).
  Its public surface is whatever `QualityTests.PublicApiWhitelist` lists.
- **`Database`** (internal) = one database.
  Holds `Schemas`, `CompatibilityLevel`, `CollationName`, the rowversion counter (`@@DBTS`), the MVCC version store, and the principal / permission / extended-property / full-text / DDL-trigger surfaces.
  All four system databases are seeded at construction, and a fresh connection lazily seeds and lands on `simulated`; ids, the id allocator and `has_dbaccess` are in [`schemas.md`](docs/claude/schemas.md).
  `#temp` routes through the connection's `TempTables`, not the seeded `tempdb`.
  A three-part name routes reads and writes cross-database, with per-database state following the *target* (`HeapTable.OwningDatabase` / `BatchContext.DatabaseFor`) while `@@ROWCOUNT` / `SCOPE_IDENTITY` / the transaction stay the session's → [`schemas.md`](docs/claude/schemas.md#cross-database-writes).
- **`Schema`** (internal) = one namespace in a database.
  Holds the object dicts (`HeapTables` / `Functions` / `Views` / `Procedures` / `Sequences` / `Triggers` — DML triggers share the object namespace) + the type namespace (`TableTypes` / `AliasTypes` / `XmlSchemaCollections`).
  Schema-qualified refs route through `Database.Schemas[<schema>]`; unqualified falls back to `DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session.
  Holds the `@@`-state (`SCOPE_IDENTITY` / `@@ROWCOUNT` / `@@ERROR`), `CurrentDatabase` / `CurrentTransaction`, per-session `TempTables`, `NestingLevel`, isolation and lock-timeout settings, and two contracts:
  - `Session`: the one-way `SessionToken` every shared structure names the session by, so an abandoned connection can be reclaimed → [`locking.md`](docs/claude/locking.md#abandoned-session-reclamation).
  - `Security`: the login, database principal and impersonation stack; the default is dbo everywhere, and dbo bypasses same-database checks → [`permissions.md`](docs/claude/permissions.md).
  Full roster in the source XML docs.
- **`BatchContext`** (internal, `Parser/`) = one command execution.
  Owns the `ParserContext` (parse-time scratch) + batch-lifetime runtime state: `Variables`, `TableVariables` (`@t`), `CurrentUndoLog`, `CurrentTableVarUndoLog` (statement-only, disjoint from the tx-scoped log so `ROLLBACK TRAN` skips `@t`), `UdfFrame` / `ProcFrame` (non-null in a UDF/proc body — gates value-form `RETURN`).
  Exposes the **resolver contract** the parser depends on:
  - `TryResolveTable` — `#foo` → `Connection.TempTables` (any qualifier); `@t` → `TableVariables` (1-part only); else named schema (`dbo` unqualified); `SystemHeapTables` only as flat 1-part fallback.
  - `TryResolveFunction` — 2-/3-part only.
    `TryResolveProcedure` accepts 1-part.
    `TryResolveTableType` accepts 1-part + `dbo` fallback.
  - `ParseObjectName(context, acceptTableVariable=false)` — 1-4-segment dotted form, compresses empty middle segments.
    `acceptTableVariable` routes `@t` to a 1-part leaf at DML/FROM sites, rejects it elsewhere.
  - Threaded into every `Expression.Run(RuntimeContext)` via `runtime.Batch`.
  - UDF/proc invocation allocates a child `BatchContext` via the body ctor: parameters pre-seed `Variables`, the frame is set, the body text re-tokenizes through a synthesized `SimulatedDbCommand`.
    UDF bodies discard yielded result sets; proc bodies forward them.
- **`StatementContext`** (internal, `Parser/`) = the dispatch loop's per-statement frame.
  Allocated once per batch and overwritten at the top of each iteration; holds `UtcNow` (the per-statement freeze the time scalars read).

**Don't stack misfit state into these buckets unthinkingly**: if no scope fits, introduce the missing one, don't squat on a neighbor.

## Conventions that fail builds

The repo's own analyzers (`src/SqlServerSimulator.Analyzers`) enforce these, and each diagnostic's message and description name the fix and the exemptions.
Write to them up front to save a build round trip:

- **SSS001**: no auto-properties or trivial wrappers on non-public types; expose the field (`public readonly T Foo = expr;`).
- **SSS002** / **SSS010** / **SSS011**: on non-public members, declare the concrete type rather than a supertype or collection interface, for fields, parameters and return types respectively.
  SSS010 and SSS011 judge from call-site and return evidence, so fixing one declaration exposes the next: **rebuild until the build is clean**.
- **SSS003** / **SSS007**: switch on a `stackalloc` `Span<char>` case-fold rather than `ToUpperInvariant()`, and match its arms with string constants rather than `SequenceEqual` guards; `ResolveBuiltIn` in `Parser/Expression.cs` is the reference shape.
- **SSS004**: a chain of `x is T { P: … }` tests on the same scrutinee is one `switch`.
- **SSS005**: constant-only switch arms are sorted (strings ordinal, numbers numerically).
- **SSS006**: consecutive discarded `StringBuilder` calls on one builder are one fluent chain.
- **SSS008**: a `static readonly` collection is an array or a `Frozen*` type.
- **SSS009**: a non-public type is not a `record`; use a plain class or struct with readonly fields, implementing `IEquatable<T>` if it is a key (`Simulation.PlanCacheKey`).
- A deliberate exception takes `#pragma warning disable SSSnnn` plus a one-line rationale.
- **MSTEST0049**: async tests thread `TestContext.CancellationToken` (`public TestContext TestContext { get; set; } = null!;`).
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` and typed asserts over generic ones.

## Style notes

- **A doc line earns its place only when the information isn't already in the repo, and belongs as close to the code as it can get.**
  Restating the repo is worse than writing nothing: the copy drifts, and the drifted copy is trusted.
  The observed failure is always the same shape — a doc describing a gap that had since been closed, or pointing at a member that had been renamed.
  - **Distance is decay.** Prefer, in order: an analyzer or test that fails the build; an XML doc on the declaration itself; a comment at the site; a feature doc under `docs/claude/`; `CLAUDE.md`.
    Each step out is a step further from the thing that would have to change with it, so put a fact at the innermost level that can hold it and stop there.
    A rule you can enforce should never be a sentence instead.
  - **`docs/claude/` earns what no single declaration owns**: probe results against real — *especially* the negative ones and the ones we deliberately don't match — measurements and the conclusion they license, roads not taken and what made the cheaper rule wrong, cost orderings, and invariants spanning files.
  - **`CLAUDE.md` earns only what you need before knowing which file to open.**
  - **Don't write** what a grep answers: message numbers and wordings (the error factories carry them in XML docs that can't drift from the code they sit on), which shapes are accepted or rejected (the test suites are the behavior contract), member / type / column rosters.
  - **Date an observation of the outside world, never a fact about this repo.**
    `probed 2026-08-06 against SQL Server 2025` is what makes a claim re-checkable rather than folklore, because the thing observed is versioned and remote.
    A date or count describing the repo itself — how many factories there are, when a sweep ran, how many gaps it closed — is a snapshot that rots on contact and that a grep answers better anyway.
- **Markdown: one sentence per line.**
  In `.md` files, end each sentence with a newline instead of flowing paragraphs — line diffs then localize to the changed sentence, and rendering is identical (single newlines don't break paragraphs).
  Sentences continuing a bullet item go on their own lines indented to the bullet's text column.
  Never split inside code fences, tables, headings, or link/inline-code spans; abbreviations (`e.g.`, `i.e.`, `vs.`) and dotted values (`17.0.4065.4`, `sys.tables`) are not sentence ends.
- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly.
- **Fields over auto-properties on non-public types** (SSS001 generalized).
- **The XML docs on the public surface are consumer-facing output, and `QualityTests` fails the build over two ways they rot.**
  A cref to an internal name (`PublicApiDocsAvoidInternalCrefs`) dangles in consumer IntelliSense and implies stability for a name we're free to rename; state the contract in prose (`"an unrecognized collation name raises ArgumentException"`, not a cref to internal `Collation.IsRecognized`).
  Watch the shadowing case the test also catches: inside `SimulatedDbConnection`, `cref="Simulation"` binds to that type's own internal field until it's written namespace-qualified.
  A `///` comment on a *partial* declaration of a public type (`PublicTypeDocsHaveOneSummary`) is concatenated into the one summary a consumer reads — a note about what a partial file holds is a `//` comment.
- **No conversation-scratch framing in code/docs/commits** — "Camp A/B", "this bundle", "Stage 1/2", "as we discussed" mean nothing to a future reader; describe behavior/motivation absolutely, cross-reference a sibling by the behavior it names, not the work-stage.
- **Gap vocabulary: a gap is "not built yet", never "out of scope".**
  `deliberate` / `intentional` may describe *how shipped behavior works* — an approximation, shortcut, or divergence chosen on purpose — but never *whether a gap closes*; attach those words to a shape, not to an absence.
  Skip the justifying clause too ("real rejects it anyway", "no consumer reads it"): it explains low priority but reads as closed.
  Only a call that genuinely shouldn't be revisited says **settled — don't re-pitch** with its reason.
  Heading vocabulary in `docs/claude/`: **Not modeled yet** for absences, **Divergences** for shipped-but-not-byte-identical, and real's-own-error rejections fold into the feature's modeled description rather than any gap list.
- **AssemblyHooks**: each test project's `AssemblyHooks.cs` has a `static [TestClass] [AssemblyInitialize]` warming shared init once before the parallel run.
  Without it, the first test batch races to init hot shared state and serializes on contention.
  The analyzer-tests' Roslyn-cache warm-up is the worst case (~3x slowdown); the pattern generalizes to any expensive first-touch shared resource.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server.
  Mirrors number/class/state/message.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built.
  Name the unmodeled feature.
- **Control flow signals via flags, not exceptions.**
  BREAK / CONTINUE / RETURN / THROW set a typed `BatchContext` flag (`LoopControl` + skip predicate), never a signal exception — `yield return` inside try/catch composes badly with iterator dispatch, and the flag reuses the skip-mode plumbing that no-ops un-taken IF branches.
  Parse-time structural checks (BREAK outside WHILE → Msg 135) fire regardless of skip state.
  Exceptions stay for true errors.

## What's modeled

The `*.Tests` and `*.Tests.EFCore` suites are the authoritative behavior contract.
The [Feature reference](#feature-reference) index maps every modeled area to its deep-dive — **presence there means it's modeled**; read the linked doc on demand when working in that area.

**Raising real SQL Server's own error is modeled behavior**, so a rejection lives in its feature's deep-dive alongside what the feature accepts, never in a gap list — accepting the statement instead would regress fidelity, and the simulator accepting what real rejects is the more dangerous divergence direction (see the over-permissive section of [`backlog.md`](docs/claude/backlog.md)).
Distinguish one from a *gap* that happens to raise: the `SEMANTIC*` rowsets raise `NotSupportedException` because semantic search is unbuilt, which is a not-yet.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers seven SqlParameter-downcast pairs: `DateOnly→date`, `DateTime→date`, `DateTime→smalldatetime`, `TimeOnly→time(N)`, `TimeSpan→time(N)`, `decimal→money`, `decimal→smallmoney`.
Without the adapter these throw at SaveChanges.
MAX-string family flows through plain `UseSqlServer`.

### Feature reference

Per-feature deep-dives live under `docs/claude/`.
Each entry below is a **trigger, not a summary**: it names the area well enough to match your task against, and the linked doc is the catalog.
So an entry's silence means nothing — **grep the linked file before concluding something isn't modeled**, and read it before working in that area.
Where an entry carries a second clause it is because that fact changes what you'd *write*, not merely what you'd find.

- **Built-in scalars** — the math / date / string / bit families, HASHBYTES / CHECKSUM, FORMAT, COMPRESS, the built-in TVFs, `@@`-constants, SESSION_CONTEXT / CONTEXT_INFO, `sys.fn_*`, ODBC escapes, and `SET DATEFIRST` / `SET LANGUAGE`.
  One shared seam narrows every integer argument, so a new id / position / count parameter gets its range error for free by routing through it → [`scalars.md`](docs/claude/scalars.md).
- **The native `REGEXP_*` family** (SQL Server 2025) — the four scalars, the `REGEXP_LIKE` predicate, the two rowset members, and the RE2 pattern dialect the simulator translates into .NET `Regex`.
  RE2 is not a `Regex` subset in either direction: some constructs are refused and others silently mean something else → [`scalars.md`](docs/claude/scalars.md#the-native-regexp_-family-sql-server-2025).
- **Legacy LOB** — where `text` / `ntext` / `image` can't go, binary `SUBSTRING`, `TEXTPTR` / `TEXTVALID`, and `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
  The non-comparable rejections split by *slot* rather than by type, so `xml` and the spatial pair share most of them → [`legacy-lob.md`](docs/claude/legacy-lob.md).
- **Type promotion and arithmetic** — `Promote` / `PromoteForArithmetic`, `Storage/Decimal38`, decimal precision-scale, integer overflow and literal typing.
  `PromoteForArithmetic` is the single source of truth for both `GetSqlType` and the runtime; **they must agree**, because the row encoder rejects a type mismatch.
  Whether two types may unify, compare or meet in an operator — and which error real raises when not — is a probed per-class grid (`SqlType.PairRules.cs`), checked while compiling → [`arithmetic.md`](docs/claude/arithmetic.md).
- **`Cast` / coercion** — CAST / CONVERT / TRY_* / PARSE, the string→date grammar with `SET DATEFORMAT` and the per-style CONVERT inputs, and the `float` / `real` → string style split.
  Conversion *legality* is settled from the two types while compiling, so a typed NULL and an empty rowset raise it too → [`casting.md`](docs/claude/casting.md).
- **`SimulatedDbDataReader` client surface** — typed accessors, `GetOrdinal` precedence, and the client-side rounding and materialization divergences → [`data-reader.md`](docs/claude/data-reader.md).
- **`Selection`, aggregates, window functions, set ops, CASE, OFFSET/FETCH, `TOP`, named windows, `TABLESAMPLE`, `SET ROWCOUNT`** — with the aggregate / GROUP BY binding rules and the frame-and-ordering gates.
  A grouping *expression* covers a matching projection sub-expression rather than the columns it names, and the parallel grouped accumulation ships **off by default** → [`query.md`](docs/claude/query.md).
- **Subqueries** — EXISTS / IN / scalar / quantified, three-valued rules, arbitrary-depth correlation, and the two decorrelation transforms.
  Whether an inner plan runs once per statement or once per outer row is decided by a **runtime probe**, not by parse-time inspection → [`subqueries.md`](docs/claude/subqueries.md).
- **Outer-scope correlation from the select list** — the FROM clause binds before the select list, which is SQL Server's binder order rather than the written one → [`query.md`](docs/claude/query.md#outer-scope-correlation-in-the-select-list).
- **JOIN / APPLY** — every join kind, comma-FROM, the hash-vs-nested-loop choice, WHERE pushdown and the narrowed-source-first reorder.
  A deferred FROM source that can't change across one enumeration is materialized once; APPLY and a `NEWID()`-drawing plan aren't → [`joins.md`](docs/claude/joins.md).
- **`PIVOT` / `UNPIVOT`** — both attach as a postfix wrapper on the derived-table `LateralPlan` seam → [`pivot.md`](docs/claude/pivot.md).
- **UPDATE / DELETE / INSERT…SELECT / SELECT…INTO / MERGE / OUTPUT**, plus rowversion, the identity helpers and `@@ROWCOUNT` → [`dml.md`](docs/claude/dml.md).
- **Variables, control flow, TRY/CATCH + THROW + ERROR_\*, `@@ERROR` / `@@TRANCOUNT` / `XACT_STATE`, WAITFOR, PRINT, GOTO, batch compilation, statement errors inside procedure and dynamic-SQL bodies**.
  Every batch walks once in skip mode before it runs, so a check that reads session state or live rows (a cursor, `IDENTITY_INSERT`, a variable's value, a lock) must wait for `!IsSkipping` or it fails batches real runs → [`control-flow.md`](docs/claude/control-flow.md).
- **Error diagnostics** — line-number rules per context, `Server` / `Procedure` population, `ERROR_LINE` / `ERROR_PROCEDURE` parity, and where informational messages (PRINT, Msg 3621 / 8153 …) land among a batch's results and errors → [`errors.md`](docs/claude/errors.md).
- **Cursors** — the full lifecycle, the sensitivity / scrollability / concurrency matrix, cursor variables, multi-source and deferred-source cursors, `WHERE CURRENT OF`.
  Several ordinary shapes silently **convert** a cursor's sensitivity, and a keyless table converts it to read-only → [`cursors.md`](docs/claude/cursors.md).
- **CTEs** — the shapes, the recursive member's restrictions, the declared column list's scoping, and where a `WITH` prefix may appear → [`ctes.md`](docs/claude/ctes.md).
- **JSON** — the JSON_\* scalars, ISJSON, OPENJSON, `FOR JSON`, and one shared path parser.
  Every one of them reads the document left to right and stops as soon as the path is settled, so the same document can raise for one path and answer for another → [`json.md`](docs/claude/json.md).
- **Name resolution, schemas, CREATE / DROP DATABASE, the `OBJECT_*` / `SCHEMA_*` / `DB_*` scalars, cross-database reads and writes, synonyms** — with the reserved-schema pin.
  An unresolved column splits by *what* failed: a bad qualifier is Msg 4104 on the whole name, everything else Msg 207 on the leaf → [`schemas.md`](docs/claude/schemas.md).
- **System metadata surfaces** — the `sys.*` / `INFORMATION_SCHEMA.*` views, `OBJECTPROPERTY`, the `sp_help` family, `sp_who`, `sp_configure`, and the expression-dependency surfaces.
  All six dependency surfaces project from one walk of stored definition **text**, which is what reproduces real's name-based refresh rules → [`catalog-views.md`](docs/claude/catalog-views.md).
- **Scalar UDFs / TVFs / views / stored procs / dynamic SQL, the `ALTER` / `CREATE OR ALTER` path, `WITH RESULT SETS`, `WITH SCHEMABINDING`, DML through views**.
  A module body **binds at CREATE** — every binder error at once, in source order — while a missing object still defers → [`programmable.md`](docs/claude/programmable.md).
- **CLR assemblies** — `CREATE` / `DROP ASSEMBLY`, external-name scalar routines, `Simulation.EnableClr`, static SAFE verification → [`clr-assemblies.md`](docs/claude/clr-assemblies.md).
- **`#foo` / `##foo` routing, DROP TABLE, TRUNCATE TABLE** → [`temp-tables.md`](docs/claude/temp-tables.md).
- **`DECLARE @t TABLE`, table-variable DML, `OUTPUT … INTO`** — the column features ship; real's own `DECLARE` grammar refuses named constraints and FKs → [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE TYPE … AS TABLE`, TVP params + `READONLY`, ADO.NET TVP** → [`table-valued-parameters.md`](docs/claude/table-valued-parameters.md).
- **`CREATE TYPE … FROM <builtin>` (scalar alias types / UDDTs), multi-part type references** → [`alias-types.md`](docs/claude/alias-types.md).
- **`sp_addextendedproperty` / `fn_listextendedproperty` / `sys.extended_properties`** → [`extended-properties.md`](docs/claude/extended-properties.md).
- **`CREATE/ALTER/DROP SEQUENCE`, `NEXT VALUE FOR`, `sys.sequences`** — a reference is refused in nine distinct contexts, all settled at parse, so the sequence never advances → [`sequences.md`](docs/claude/sequences.md).
- **DML + DDL triggers** — `CREATE TRIGGER`, `INSERTED` / `DELETED`, `EVENTDATA()`, `UPDATE(col)` / `COLUMNS_UPDATED()`, firing order, the two nesting options.
  A trigger body has **no atomic scope of its own**: it and the firing statement roll back as one unit → [`triggers.md`](docs/claude/triggers.md).
- **CHECK / PRIMARY KEY / UNIQUE enforcement, the computed-column rules, `IGNORE_DUP_KEY`** — enforcement shares the per-`Heap` seek cache with reads and FK checks → [`constraints.md`](docs/claude/constraints.md).
- **`FOREIGN KEY` + referential actions** — including the child triggers a cascade fires and its mutual exclusion with an `INSTEAD OF` trigger over the same verb → [`foreign-keys.md`](docs/claude/foreign-keys.md).
- **`PERIOD FOR SYSTEM_TIME`, the history sibling and its validation, `HISTORY_RETENTION_PERIOD`, all five `FOR SYSTEM_TIME` query forms** → [`temporal-tables.md`](docs/claude/temporal-tables.md).
- **`ALTER TABLE` ADD / DROP / ALTER COLUMN + CONSTRAINT, the period pair, `REBUILD`, trust toggling**.
  A DEFAULT expression has an **empty scope** — a name inside one is Msg 128 even when it *is* a column → [`alter-table.md`](docs/claude/alter-table.md).
- **`CREATE INDEX`, inline indexes, indexed views, `ALTER INDEX`, disabled indexes, `CREATE STATISTICS`, computed columns as keys** — plus the **access-path choices** a read makes over the per-`Heap` seek cache, which is where query throughput actually lives → [`indexes.md`](docs/claude/indexes.md).
- **Table hints (`WITH (NOLOCK …)`) and statement `OPTION (…)` hints**, including `FORCESEEK`'s nested form and the legacy no-`WITH` parenthesized form → [`query-hints.md`](docs/claude/query-hints.md).
- **Heap page lifecycle** — reclamation / reuse, tail-only shrink, `DBCC SHRINKDATABASE` / `SHRINKFILE`, `Heap.RowCount`, and which callers may take the reused encode buffer → [`heap-storage.md`](docs/claude/heap-storage.md).
- **Per-`Simulation` plan cache and token memo** — the two reuse layers over a repeated `CommandText`.
  Cached plans are **shared**, so per-execution state belongs on `StatementContext`, never on the plan → [`plan-cache.md`](docs/claude/plan-cache.md).
- **Transactions** — statement atomicity, the undo log, BEGIN / COMMIT / ROLLBACK / SAVE, `SET XACT_ABORT`, and the rare transaction-*aborting* error class that unwinds the whole stack → [`transactions.md`](docs/claude/transactions.md).
- **Locking, MVCC, SNAPSHOT / RCSI, deadlock and timeout, the lock DMVs, key-range locks, application-lock siblings**.
  Every shared structure names a session by its one-way `SessionToken`, which is what lets an abandoned connection be collected and torn down → [`locking.md`](docs/claude/locking.md).
- **Application locks** — `sp_getapplock` / `sp_releaseapplock` / `APPLOCK_MODE` / `APPLOCK_TEST`, and EF's `__EFMigrationsLock` → [`app-locks.md`](docs/claude/app-locks.md).
- **`hierarchyid`** — OrdPath storage, byte-identical CAST / wire / DATALENGTH, and the full sixteen-tier ordinal domain (wider than `int`, so labels are `long`) → [`hierarchyid.md`](docs/claude/hierarchyid.md).
- **`GRANT` / `REVOKE` / `DENY`** — securable resolution, the covering scope walk, role closure, ownership chaining, `EXECUTE AS`, application roles, login and server-scope DDL, and a gate on every modeled CREATE / ALTER / DROP.
  A cross-database reference resolves the login's user in the **target**, and dbo bypasses every check → [`permissions.md`](docs/claude/permissions.md).
- **Full-text search** — the catalogs and indexes, `CONTAINS` / `FREETEXT`, the two rowset functions, the whole `contains_search_condition` grammar, the word breaker, stoplist and stemmer.
  Searches read live rows rather than a crawled index, so a write is searchable immediately where real's lags → [`full-text.md`](docs/claude/full-text.md).
- **`xml` type and XML schema collections** — typed writes and canonical form, the XQuery-subset evaluator behind `.value()` / `.nodes()` / `.query()` / `.exist()`, `.modify()` XML-DML, XML indexes, `FOR XML` in all four modes, and `OPENXML`.
  `OPENXML`'s patterns are **XPath 1.0** through the DOM, not the XQuery translator → [`xml.md`](docs/claude/xml.md).
- **`geography` / `geometry`** — the parsed value model, WKT / WKB, the member surface, spatial indexes, the measures, the DE-9IM topological engines, validity and the derived points.
  Round-earth measures follow the **great elliptic arc**, which is not the geodesic → [`spatial.md`](docs/claude/spatial.md).
- **`ALTER DATABASE SET <option>` and the database-level `COLLATE` clause** — most options parse-and-discard; the load-bearing ones (compat level, the snapshot pair, `TRUSTWORTHY`, `DB_CHAINING`, `READ_ONLY`, `QUERY_STORE`) are listed in the doc.
  `QUERY_STORE` is the only one with a sub-grammar of its own, and its whole configuration is retained though nothing is ever captured → [`database-options.md`](docs/claude/database-options.md).
- **Per-column / per-expression collation, coercibility precedence, the cross-collation error family, the per-collation ANSI code page, and the `LIKE` / `PATINDEX` matcher**.
  A subject is read as **characters, not UTF-16 units**, and collation is bound at compile time so an empty rowset raises → [`collations.md`](docs/claude/collations.md).
- **Grammar-level rules** — new statement parsers, dispatch-loop separators, `QUOTED_IDENTIFIER` and its per-object capture, reserved-keyword gating, the trailing-token tightenings and the module batch-position pair → [`grammar.md`](docs/claude/grammar.md).
- **sqllogictest as a differential oracle** — why a live server is the oracle and the corpus's own expected results are not, and the methodology traps that fabricate findings in *any* differential harness → [`sqllogictest.md`](docs/claude/sqllogictest.md).
- **BACPAC import** — `Simulation.ImportBacpac`, `BacpacImportOptions`, the BCP wire format, `BacpacBuilder`, and the phase order a module body's own bind imposes → [`bacpac-loader.md`](docs/claude/bacpac-loader.md).
- **Linked servers** — `AddRemoteSimulation`, `sp_addlinkedserver`, four-part FROM routing, `OPENQUERY`, `sys.servers` → [`linked-servers.md`](docs/claude/linked-servers.md).
- **TDS network endpoint** — `ListenLocalAsync` / `ListenNetworkAsync`, the SQLBatch / RPC / TM / BulkLoad families, API server cursors, attention, MARS, TDS 8.0, and the projection-nullability inference behind COLMETADATA's `fNullable`.
  Oracles are `*.Tests.SqlClient` + `*.Tests.Smo` → [`tds-endpoint.md`](docs/claude/tds-endpoint.md).

## Not modeled yet

Status, not decision.
Everything here is unbuilt for cost reasons and is fair game to pick up.
This is a trigger list: [`backlog.md`](docs/claude/backlog.md) carries the weighting, and the linked deep-dive carries the detail and the probe results.
Entries that raise a *real* SQL Server error deliberately are **not** here; they're coverage, and live in their feature's deep-dive.
The feature docs' own **Not modeled yet** sections hold the smaller gaps.

- **Key-range locks past a sargable leading-key-prefix predicate** — other SERIALIZABLE reads take the whole-table S → [`locking.md`](docs/claude/locking.md#key-range-locks).
- **An aggregate reading only an enclosing query's columns** where no collector can rehome it → `NotSupportedException` → [`query.md`](docs/claude/query.md#outer-scope-correlation-in-the-select-list).
- **Cross-server DML** through a four-part name → `NotSupportedException` via `BatchContext.RejectCrossServerMutation` (cross-*database* DML ships) → [`linked-servers.md`](docs/claude/linked-servers.md), [`schemas.md`](docs/claude/schemas.md#cross-database-writes).
- **Most `SET <option>` toggles parse and are discarded** (`Simulation.Set.cs`); the ones with semantic effect are handled by name there.
  The same goes for most `ALTER DATABASE … SET` options → [`database-options.md`](docs/claude/database-options.md).
- **Heap allocation tracking** (a flat page list, no IAM/PFS) → [`heap-storage.md`](docs/claude/heap-storage.md).
- **`ALTER AUTHORIZATION`**, in every form → [`schemas.md`](docs/claude/schemas.md#create-schemas-owner-and-its-element-list).
- **Programmable-object gaps**: CLR procedures / TVFs / aggregates / UDTs, logon triggers, INSTEAD OF UPDATE/DELETE on non-updatable views, a join view over a join view, MERGE into or OUTPUT through views, `WITH RESULT SETS`' shorthand forms, and one binder error per statement → [`programmable.md`](docs/claude/programmable.md), [`clr-assemblies.md`](docs/claude/clr-assemblies.md), [`triggers.md`](docs/claude/triggers.md).
- **`ALTER TABLE … SWITCH PARTITION`** and `ALTER COLUMN … ADD | DROP {PERSISTED | MASKED}` → [`alter-table.md`](docs/claude/alter-table.md).
- **`FORCESEEK`'s plan-infeasibility refusal** (Msg 8622) → [`query-hints.md`](docs/claude/query-hints.md#not-enforced).

## Quirks (modeled, not byte-identical to SQL Server)

Cross-cutting divergences with no single feature-doc home; feature-specific quirks live in their `docs/claude/` deep-dive's divergence section (via [Feature reference](#feature-reference)).

- `decimal` / `numeric`: carried in `Storage/Decimal38`, a purpose-built `UInt128` magnitude + sign + scale covering `numeric`'s whole 38-digit domain, so every value real represents computes, stores and renders here — including the declared scale's own trailing zeros (`CAST(1 AS numeric(38, 30))` carries all thirty).
  The one narrowing is the **client** edge, and it is real SqlClient's: `GetDecimal` / `GetValue` shed trailing fractional zeros to fit a .NET `decimal` and raise `OverflowException("Conversion overflows.")` when nothing can be shed.
  SqlClient answers a minority of non-fitting values with `SqlTypeException("Invalid numeric precision/scale.")` instead, off a value-dependent internal path; the simulator raises `OverflowException` uniformly — see [`arithmetic.md`](docs/claude/arithmetic.md#the-backing-type) and [`data-reader.md`](docs/claude/data-reader.md).
- Auto-generated constraint names: PK/UNIQUE shape `PK__<table8>__<16hex>` / `UQ__<table8>__<16hex>` (16-hex 64-bit FNV-1a); CK/FK/DF shape `CK__<table8>__[<col8>__]<8hex>` (8-hex 32-bit FNV-1a).
  Deterministic across runs, distinct from SQL Server's object-id-derived hex (won't byte-match).
- Client-surface streaming and heap-page divergences live in [`data-reader.md`](docs/claude/data-reader.md) and [`heap-storage.md`](docs/claude/heap-storage.md).
