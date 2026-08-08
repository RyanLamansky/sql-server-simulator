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
Authenticity over desirability — when SQL Server is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it.
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
8KB heap pages.
Rows encoded as bytes, navigated column-by-column without rehydrating; single-column reads via an array-typed schema take the `RowLayout` fast path (per-schema geometry cached by array identity in a `ConditionalWeakTable`, making `RowDecoder.DecodeColumn` O(1) vs two O(columns) walks — the per-row resolvers' path).
Type-only `SqlType[]` schemas reach that same fast path through `RowDecoder.ColumnsFor`, which caches the `HeapColumn[]` conversion by schema-array identity — **never convert per call**: a fresh array defeats the layout cache's identity key and re-lays-out the geometry every read (measured at a third of result-drain CPU; the reader-cursor and subquery decode sites are the precedent consumers).
The **write** side has the same rule and the same seam: `RowEncoder.EncodeRow` has an array-schema overload that routes through `ColumnsFor`, which overload resolution picks for every caller holding a `SqlType[]`; the span form builds the array *and* a column object per column, which on a row-at-a-time producer measures as the largest single allocation of a `SELECT … INTO`.
`EncodeRow`'s own per-row scratch is `stackalloc` up to 64 columns and heap-allocated past it.
A caller that hands the encoded bytes to a heap and drops them takes `EncodeRowInto`, which writes into a reused caller-owned buffer; one that **retains** them keeps the allocating `EncodeRow`, since a reused buffer outlives its row and is longer than it — see [`heap-storage.md`](docs/claude/heap-storage.md).
Every non-NULL variable-length column carries a 1-byte inline/pointer marker.
LOB-eligible types (`varchar/nvarchar/varbinary(MAX)`, `text`/`ntext`/`image`) flow through a parallel 8KB-LOB-page chain.
Bounded `varchar/nvarchar/varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits 8060 bytes.
Allocation is a flat page list (no IAM/PFS).
`Heap.EnumerateRowsWithAddress` is the path **every** table scan runs, so it walks slots inline (one `HeapPage.TryReadLiveSlot` per row rather than a nested per-page enumerator plus four re-reads of the same 2-byte directory entry) and probes the forward-target set only when it holds something — its key is a tuple, and a `Count` test keeps that hash probe off the common scan; **don't re-add a layer here** (a nested per-page enumerator measures 71 ms against 11 ms inline on a 228k-row `COUNT(*)`, the floor under every scan-bound query) — see [`heap-storage.md`](docs/claude/heap-storage.md).

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
`FromSource[]`; enumeration rows are `byte[]?[]`, one slot per source, null = NULL-filled outer-join side (LEFT/RIGHT/FULL/OUTER APPLY).
Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple`; ambiguous unqualified name → Msg 209.
Per-row resolution goes through a per-enumeration `SourceColumnMemo` (name → (source, column), keyed by string reference identity — execution-scoped per the plan-cache shared-plan contract); un-memoized re-resolution was the largest CPU cost of scan-bound joins/aggregates.
**Per-row resolver loops use the hoisted-scaffolding pattern**: one mutable-capture tuple slot + one cached _self-referencing lambda_ (never a local function passed as its own `selfRecursive` argument — that allocates a delegate per resolution per row; 41% of profile bytes) + one `RuntimeContext` per loop; follow it in executor loops.
The same rule reaches the _arrays_ a per-row loop fills: a grouping key, a hash-join bucket key and a group's projection under a bounded `TOP (n)` are all written into reused scratch and copied out only where something retains them (a group's first row, a heap admission), which is one allocation per group rather than per row.
The aggregate executor also accumulates straight off the enumeration for a single grouping set instead of buffering the rows first — real pipelines its Filter into its aggregate the same way, which is observable in _which_ error a row raises — see [`query.md`](docs/claude/query.md#streaming-accumulation-and-where-an-error-surfaces).

### `MultiPartName`
Readonly struct, up to 4 inline slots.
API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Baseline.Equals(name.ImmediateQualifier, "INSERTED")`, which folds null into `false`), `Count`, `ToString()`.

### Matching a name against a fixed vocabulary
Three matchers, in cost order: a `switch` over string constants or `string.Equals(…, Ordinal[IgnoreCase])` (~1 ns, and a miss against a differently-sized literal is only a length check), `BuiltInToken` (spec-defined tokens — an ASCII-alphanumeric shortcut over an invariant `CompareInfo`, see [`collations.md`](docs/claude/collations.md#fixed-tokens-builtintoken)), and a `Collation` (the database's own semantics, mandatory for user identifiers).
**Pick by the semantics the site needs, then keep the shape simple** — the measured traps run the other way from intuition:

- A short chain of ordinal compares **beats** a `Frozen*` lookup, so don't convert one.
  Hashing pays off at `ResolveBuiltIn`'s scale, not an accept-list's.
- Uppercasing into a `stackalloc` span to reach a span `switch` (the SSS003 / SSS007 shape) may cost more than the chain it replaces at accept-list size.
  It earns its keep across `ResolveBuiltIn`'s ~300 entries.
- What does cost: **materializing a string to feed a lookup**, when the token already exposes `Source` as a span (`Frozen*.GetAlternateLookup<ReadOnlySpan<char>>` is the fix), and **repeating a compare per row** that a parse-time discriminator settles once (`XmlMethodCall`'s `XmlMethod`, `ObjectId.ClassifyTypeFilter`).

Because these compares sit behind the memo layers (`SourceColumnMemo`, the plan cache), none of it moves a realistic query measurably; treat it as allocation and clarity work, not throughput work.

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
  Holds `SystemHeapTables`, the `Databases` dict, and `ServerCollationName` (`init`-only; defaults `SQL_Latin1_General_CP1_CI_AS`; mirrors `model.collation` — install-time seed for every new `Database`, both the lazy `"simulated"` seed and collation-less bacpac imports; `init` reflects real immutability).
  Public surface = `Simulation` ctor + `CreateDbConnection()` + `ImportBacpac()` + `AddRemoteSimulation()` + `ServerCollationName` + `EnableClr` + `ListenLocalAsync()` / `ListenNetworkAsync()` (int-port or `SimulatedNetworkListenerOptions` overloads) → `SimulatedNetworkListener`.
- **`Database`** (internal) = one database.
  Holds `Schemas`, `CompatibilityLevel`, `CollationName`, the rowversion counter (`@@DBTS`), the MVCC version store, and the principal/permission/extended-property/full-text/DDL-trigger surfaces.
  `Databases` is seeded at construction with all four system databases — `master` / `tempdb` / `model` / `msdb` (so `USE <systemdb>` / `master.sys.*` / `master.dbo.<proc>` / SSMS's `msdb.dbo.syspolicy_system_health_state` all resolve without an import); the first `CreateDbConnection()` lazily seeds `DefaultDatabaseName` (`"simulated"`) when no *user* database is present, and all four system databases are excluded from the initial-database fallback (`Simulation.SystemDatabaseNames`) so a fresh connection still lands on `simulated`.
  Database ids: the four system databases carry fixed ids (master = 1, tempdb = 2, model = 3, msdb = 4, from `Simulation.SystemDatabaseIds`); every user database carries a **stored** `Database.Id` assigned at registration (`Simulation.RegisterUserDatabase` — the smallest free id ≥ 5, so a dropped id is reused, matching real). `DbId.DatabasesWithIds` projects `(db, db.Id)` ordered by id — single source of truth consumed by `DB_ID`/`DB_NAME`, `sys.databases`, `OBJECT_NAME`, and `DBCC SHRINKDATABASE`. `CREATE DATABASE` (via `RegisterUserDatabase`) and `Simulation.ImportBacpac` and the lazy `simulated` seed all allocate through the same path.
  `has_dbaccess` is accessibility-aware: 1 for master/tempdb/msdb/user dbs, 0 for `model` (restricted template), NULL for unknown.
  `#temp` still routes through the connection's `TempTables`, not the seeded `tempdb`.
  `USE <db>` switches session (Msg 911 on miss); 3-part names route both reads and writes cross-DB (`INSERT other.dbo.t …`, synonyms and `db..t` included), with the per-database state — rowversion counter, version store, trigger dispatch, object-id allocation — following the *target* via `HeapTable.OwningDatabase` / `BatchContext.DatabaseFor`, while `@@ROWCOUNT` / `SCOPE_IDENTITY` / the transaction stay the session's; a 4-part write still raises `NotSupportedException` via `BatchContext.RejectCrossServerMutation` — see [`schemas.md`](docs/claude/schemas.md#cross-database-writes).
- **`Schema`** (internal) = one namespace in a database.
  Holds the object dicts (`HeapTables` / `Functions` / `Views` / `Procedures` / `Sequences` / `Triggers` — DML triggers share the object namespace) + the type namespace (`TableTypes` / `AliasTypes` / `XmlSchemaCollections`).
  Schema-qualified refs route through `Database.Schemas[<schema>]`; unqualified falls back to `DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session.
  Holds `@@`-state (`LastIdentity` = `SCOPE_IDENTITY`/`@@IDENTITY`, `LastStatementRowCount` = `@@ROWCOUNT`, `LastErrorNumber` = `@@ERROR`), `CurrentDatabase` / `CurrentTransaction`, per-session `TempTables` (`#foo`, cleared on Dispose), `NestingLevel` (cap 32), `Spid` (≥51), `SessionIsolationLevel`, `LockTimeoutMillis`, `Session` (the `SessionToken` every shared structure names it by — see [`locking.md`](docs/claude/locking.md#abandoned-session-reclamation)), `CurrentExecutingThreadId` (same-thread-deadlock detection, carried on the token), and `Security` (`SessionSecurityContext`: original login + base database principal + impersonation stack; default = dbo everywhere; read by the identity scalars, mutated by `EXECUTE AS`/`REVERT` and module `WITH EXECUTE AS`, stamped by connection-string / TDS auth; `EffectiveIsDbo` is the same-database permission-enforcement bypass, while a cross-database reference resolves the login's user in the *target* database and asks the boundary-aware bypass, which a database-scoped `dbo` frame (a module's `WITH EXECUTE AS OWNER` / `SELF`) fails — see [`permissions.md`](docs/claude/permissions.md)).
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

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields.
  Expose the field directly: `public readonly T Foo = expr;` (`static readonly` for static-singleton).
  Overrides, abstracts, interface impls exempt.
  A **positional record**'s parameter list reaches the same auto-properties indirectly and is reported too — once on the record's identifier, since the fix rewrites the whole list rather than any one parameter.
  A derived record forwarding every parameter to a base (`record D(int A) : B(A)`) declares none of its own and is exempt.
- **SSS002**: a `readonly` field in a non-public type whose declared type is a strict supertype of its initializer should use the concrete type.
  Public types, value-typed initializers (boxing), const, and uninitialized fields exempt.
- **SSS003**: `string.ToUpperInvariant()`/`ToLowerInvariant()` as a `switch`'s *governing expression* allocates a temp string.
  Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on that.
- **SSS004**: 2+ `if`/`else if` branches of shape `<sameScrutinee> is <SameType> { <SameProperty>: … }` should be a single `switch` (fuses isinst + ldfld; the if-chain repeats both per arm).
- **SSS005**: a `switch` (expr or statement) whose arms are all single string/numeric constants must be sorted — strings ordinal, numbers numerically (`_`/`null` excluded).
  Exempts a guard, or/relational/recursive/`var` pattern, or **enum/`char`/`bool`** constant (those order by meaning).
  A switch deliberately ordered by meaning (time-unit magnitude, host level): `#pragma warning disable SSS005` + one-line rationale rather than sort.
- **SSS006**: 2+ consecutive self-returning `StringBuilder` calls (`Append`/`Insert`/`Replace`/…) on the same builder, result discarded (bare or `_ =`), should be one fluent chain.
  An already-chained statement peels to its base receiver, so `sb.Append(a).Append(b)` beside `sb.Append(c)` merges.
  Only when the base is side-effect-free (identifier / `this.field` / dotted); call-valued roots exempt.
  Comments between statements don't exempt — slot them into the chain.
- **SSS007**: a `switch` **expression** over `Span<char>`/`ReadOnlySpan<char>` whose arm is a discard guard `_ when <governing>.SequenceEqual("literal")` should be the constant pattern `"literal"` — a span-of-char switch matches string constants directly since C# 11.
  Only the pure single-invocation guard whose receiver is the switch's governing expression is flagged (negated / `&&`-combined / different-span conditions are left alone).
  Enforcement companion to SSS003 (which creates the `stackalloc Span<char>` scrutinee); `ResolveBuiltIn` in `Parser/Expression.cs` is the reference shape.
- **SSS008**: a `static readonly` field typed as a general-purpose collection from `System.Collections{,.Generic,.Immutable}` must be an array or a `Frozen` type — a static's contents are fixed for the process, so they should be laid out once for reading.
  Throughput, not immutability, is the motive: arrays are permitted, `Immutable*` dictionary / set / list are flagged too (a per-lookup tree walk buys nothing here), `ImmutableArray<T>` is exempt.
  `Lazy<T>` unwraps first; `Concurrent*` and `PriorityQueue` are exempt; anything genuinely mutated after init takes `#pragma warning disable SSS008` + rationale.
- **SSS009**: a non-public type may not be declared as a `record`.
  The compiler emits `Equals` / `GetHashCode` / the equality operators / `ToString` / `PrintMembers` / a copy constructor / `Deconstruct` whether or not anything calls them, so on a type with no API surface each uncalled member is shipped metadata and an uncovered member in every coverage report.
  Declare a plain class or struct with readonly fields; the primary-constructor + field-initializer shape is what the repo uses (`internal sealed class Foo(int a) { public readonly int A = a; }` — `FrameSpec`, `PlanCacheEntry` are the precedent).
  **Value equality alone is not a justification**: a dictionary key or hash-set member implements `IEquatable<T>` directly, which emits the two members the lookup calls and keeps a struct off `ValueType.Equals`'s boxing-and-reflection fallback — `Simulation.PlanCacheKey` is the reference shape.
  A record kept for a genuinely-used `with`, deconstruction or printed `ToString` takes `#pragma warning disable SSS009` + rationale; `with` needs init properties, so such a record takes an SSS001 suppression alongside it.
  Public types are exempt — there the synthesized members are deliberate API surface.
- **SSS010**: a non-public method / constructor / local function parameter declared as a general-purpose collection **interface** (the `System.Collections{,.Generic,.Immutable}` `IEnumerable` / `ICollection` / `IList` / `ISet` / `IDictionary` / `IReadOnly*` / `IImmutable*` family) should declare the concrete type when **every call site in the compilation** passes that one type — the parameter half of SSS002's field rule.
  The interface buys flexibility nobody uses: each read through it is a dispatch the concrete type resolves statically, and the concrete type's own members are hidden from the body.
  A **struct** collection is the highest-value hit, since passing it as an interface boxes on every call — the value-type case runs the *opposite* way from SSS002, which exempts a value-typed initializer precisely to keep a field's boxing boundary.
  Because the verdict is call-site evidence, the rule needs a compilation-end pass and **converges by iteration**: an argument whose own compile-time type is the interface (it arrived through another interface-typed parameter or field) settles the callee as genuinely interface-fed, so fixing the upstream declaration is what exposes the next hit — rebuild until the build is clean.
  Exempt: public / protected members of public types, overrides, abstract / virtual members, interface implementations, partial methods, `params` and by-reference parameters, lambda / delegate parameter positions, a parameter whose type mentions a type parameter, and a method whose group is converted to a delegate.
  A `null` / `default` / omitted argument contributes no type rather than disqualifying — but it blocks a *struct* replacement, which couldn't restate the null.
  Two call sites with different concrete types is genuine polymorphism and goes unreported; polymorphism visible only outside the compilation (a call from a test assembly into an `internal` member) takes `#pragma warning disable SSS010` + rationale.
  Hits on `private` members overlap CA1859, which is already on; SSS010 is what extends the same fix to `internal` members, constructors and local functions.
- **SSS011**: the same judgement in return position — a non-public method / local function whose **return type** is one of those collection interfaces should declare the concrete type when every `return` in its body produces it.
  **Iterators are exempt by construction** — a `yield` body can only be declared as `IEnumerable<T>` / `IEnumerator<T>`, and deferred execution is the flexibility an interface return genuinely buys; the rest of the exemption list is SSS010's, plus `ref` returns.
  Evidence is the body's own returns (an expression body counts as one), read past nested lambdas and local functions; a `null` / `default` return contributes no type but blocks a *struct* replacement, a return whose own type is an interface settles the member as interface-returning, two different concrete types is polymorphism, and a throw-only body has nothing to judge.
  A separate id from SSS010 because the evidence is local rather than compilation-wide — and so one member carrying both can suppress them independently — but the two cascade into each other (a narrowed return makes a caller's argument concrete, and vice versa), so iterate them together.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`.
  Pattern: `public TestContext TestContext { get; set; } = null!;`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; typed asserts over generic.

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
  `PromoteForArithmetic` is the single source of truth for both `GetSqlType` and the runtime; **they must agree**, because the row encoder rejects a type mismatch → [`arithmetic.md`](docs/claude/arithmetic.md).
- **`Cast` / coercion** — CAST / CONVERT / TRY_* / PARSE, the per-style string→date input grammar, and the `float` / `real` → string style split.
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
- **Variables, control flow, TRY/CATCH + THROW + ERROR_\*, `@@ERROR` / `@@TRANCOUNT` / `XACT_STATE`, WAITFOR, PRINT, GOTO** → [`control-flow.md`](docs/claude/control-flow.md).
- **Error diagnostics** — line-number rules per context, `Server` / `Procedure` population, `ERROR_LINE` / `ERROR_PROCEDURE` parity → [`errors.md`](docs/claude/errors.md).
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
Everything here is unbuilt for cost reasons and is fair game to pick up — [`backlog.md`](docs/claude/backlog.md) carries the weighting and the prospective view of the same ground.
Entries that raise a *real* SQL Server error deliberately are **not** here; they're coverage, and live in their feature's deep-dive.

- **Key-range coverage past a sargable predicate on a leading key prefix** — a SERIALIZABLE / HOLDLOCK read fences a key range when its predicate bounds a **leading prefix** of some key or index; a whole-table scan, a predicate on an unindexed or non-leading column, an `ORDER BY`-eliminated ordered scan and a view / multi-source read all keep the whole-table S, which is what real degenerates to for the unindexed cases.
  A SERIALIZABLE **writer** takes no fence of its own (real converts its key locks to `RangeX-X`), and two readers' ranges meet only on an *identical* interval, since ranges intern per interval and containment is tested on the write path — see [`locking.md`](docs/claude/locking.md#key-range-locks).
- **An aggregate reading only an enclosing query's columns** (`(SELECT MAX(t.col) FROM u)` inside a query over `t`) → `NotSupportedException` where the scope offers nowhere to move it to; real binds it to the outer query, collapsing that query to one row.
  Correlation itself ships, as does the rehoming where a collector exists, and a name that resolves in *no* scope is real's own Msg 207 — see [`query.md`](docs/claude/query.md#outer-scope-correlation-in-the-select-list).
- **Cross-server DML** (`INSERT`/`UPDATE`/`DELETE`/`MERGE` through a 4-part linked-server name) → `NotSupportedException` via `BatchContext.RejectCrossServerMutation`; open a connection on the target `Simulation` instead.
  Four-part *reads* (SELECT/JOIN) ship; catalog-view reads via four-part names (`srv.db.sys.tables`) fall to Msg 208.
  Cross-*database* DML inside one `Simulation` ships, permission resolution, catalog-view metadata visibility and snapshot stamps included; the surface still reading the session's database is the `OBJECT_*` scalars' visibility gate.
  See [`schemas.md`](docs/claude/schemas.md#cross-database-writes), [`linked-servers.md`](docs/claude/linked-servers.md).
- **`SET <option>` accept-list** (`Simulation.Set.cs`): the ANSI/session-state toggles, `STATISTICS {IO|TIME|XML|PROFILE}`, `DATEFORMAT` / `DEADLOCK_PRIORITY` / `QUERY_GOVERNOR_COST_LIMIT` and the rest of the value-taking family — parse-and-discard.
  Unknown SET → Msg 195.
  `SET @v`, `IDENTITY_INSERT`, `NOCOUNT`, `LOCK_TIMEOUT`, `TEXTSIZE` (client-boundary LOB truncation — see [`scalars.md`](docs/claude/scalars.md)), `TRANSACTION ISOLATION LEVEL`, `QUOTED_IDENTIFIER` (and `ANSI_DEFAULTS`'s QI component), `XACT_ABORT`, `ROWCOUNT`, `DATEFIRST` and `LANGUAGE` carry semantic effect.
  `SET DATEFORMAT` not following `SET LANGUAGE` is the one coupling those leave open — see [`scalars.md`](docs/claude/scalars.md#set-language-and-the-datefirst-it-moves).
- **`ALTER DATABASE … SET` / `COLLATE`** — see [`database-options.md`](docs/claude/database-options.md).
  Most options parse-and-discard; `COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`, `RECURSIVE_TRIGGERS`, `TRUSTWORTHY`, `DB_CHAINING`, `READ_ONLY` / `READ_WRITE`, `QUERY_STORE` are load-bearing, and every option lands on the **named** database (`CURRENT` for the session's; an unhosted name is Msg 5011).
- **`MERGE … OUTPUT` through a view** → `NotSupportedException` (view-column projection through `INSERTED`/`DELETED` deferred); the other MERGE shapes ship — see [`dml.md`](docs/claude/dml.md).
- Heap allocation tracking (flat page list, no IAM/PFS).
- **`ALTER AUTHORIZATION`** in every form — there is no parser for the statement, so a schema's owner is settled once by `CREATE SCHEMA … AUTHORIZATION` and an object's follows its schema.
  See [`schemas.md`](docs/claude/schemas.md#create-schemas-owner-and-its-element-list).
- **Programmable-object top-level gaps**: CLR procedures / TVFs / aggregates / UDTs (CLR *scalar functions* ship — see [`clr-assemblies.md`](docs/claude/clr-assemblies.md)), logon triggers, INSTEAD OF UPDATE/DELETE on non-updatable views, DML through a JOIN view whose own source is another JOIN view, and MERGE into a JOIN view (the single-base INSERT / UPDATE, and a chain of single-source views over such a body, ship — see [`programmable.md`](docs/claude/programmable.md#dml-through-a-join-view)), OUTPUT through views, multi-source alias-form UPDATE/DELETE through views (Msg 4405).
  Natively-compiled + CLR procedures ship at parser-fidelity only (ATOMIC boundary → session isolation; CLR proc bodies parse but `EXEC` no-ops).
  `WITH RESULT SETS`'s `AS OBJECT` / `AS TYPE` / `AS FOR XML` definition shorthands raise `NotSupportedException`; its three main forms ship, on a system procedure as well as a user one.
  A module body binds at CREATE and its shape is checked there, but a statement contributes at most one binder error to the report (real names every bad column reference of one statement), and a bind abandoned at a deferral reports only what it gathered and leaves the last-statement rule unrun.
  See [`programmable.md`](docs/claude/programmable.md).
- **`ALTER TABLE` shapes not built**: `SWITCH PARTITION`, and the `ALTER COLUMN … ADD | DROP {PERSISTED | MASKED}` sub-clauses (the `ROWGUIDCOL` / `SPARSE` pair ships, as do `ADD` / `DROP PERIOD FOR SYSTEM_TIME`).
  Modeled shapes in [`alter-table.md`](docs/claude/alter-table.md).
- **`FORCESEEK`'s plan-infeasibility refusal** (Msg 8622 level 16 state 1, compile-time and so uncatchable) — real raises it whenever the planner can't honour the directive; the simulator validates the hint's index and seek columns (Msg 308 / 362 / 365) and then reads normally, having no plan to declare infeasible.
  Probed 2026-08-08: real refuses a `FORCESEEK` with **no predicate at all**, one whose only predicate is on an **unindexed** column, and one whose **named** index no predicate touches the keys of; it accepts an equality, a range, an `IN`, a `<>`, an `OR` mixing an indexed with an unindexed column, and a join `ON` equality.
  Closing it wants the seek planner's sargability analysis lifted from execution to compile time — the accepting cases are what make a cheaper rule over-raise.
  Surface in [`query-hints.md`](docs/claude/query-hints.md).

## Quirks (modeled, not byte-identical to SQL Server)

Cross-cutting divergences with no single feature-doc home; feature-specific quirks live in their `docs/claude/` deep-dive's divergence section (via [Feature reference](#feature-reference)).

- `decimal` / `numeric`: carried in `Storage/Decimal38`, a purpose-built `UInt128` magnitude + sign + scale covering `numeric`'s whole 38-digit domain, so every value real represents computes, stores and renders here — including the declared scale's own trailing zeros (`CAST(1 AS numeric(38, 30))` carries all thirty).
  The one narrowing is the **client** edge, and it is real SqlClient's: `GetDecimal` / `GetValue` shed trailing fractional zeros to fit a .NET `decimal` and raise `OverflowException("Conversion overflows.")` when nothing can be shed.
  SqlClient answers a minority of non-fitting values with `SqlTypeException("Invalid numeric precision/scale.")` instead, off a value-dependent internal path; the simulator raises `OverflowException` uniformly — see [`arithmetic.md`](docs/claude/arithmetic.md#the-backing-type) and [`data-reader.md`](docs/claude/data-reader.md).
- Auto-generated constraint names: PK/UNIQUE shape `PK__<table8>__<16hex>` / `UQ__<table8>__<16hex>` (16-hex 64-bit FNV-1a); CK/FK/DF shape `CK__<table8>__[<col8>__]<8hex>` (8-hex 32-bit FNV-1a).
  Deterministic across runs, distinct from SQL Server's object-id-derived hex (won't byte-match).
- Client-surface streaming and heap-page divergences live in [`data-reader.md`](docs/claude/data-reader.md) and [`heap-storage.md`](docs/claude/heap-storage.md).
