# Claude Working Notes

Canonical orientation for working in this repo. Claude Code reads this file automatically; `README.md` is user-facing and not auto-loaded.

## What this is

SqlServerSimulator is a .NET library that emulates a SQL Server instance in-process. Code written against `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` can use a `Simulation` instance instead of a real database — useful for fast deterministic tests, for reproducing pathological SQL Server behaviors that are hard to set up on a real server, and for any scenario where embedding a database avoids the operational weight of a real one.

## Long-term direction

High-fidelity SQL Server simulation, eventually including transactions, locks, MVCC, real allocation tracking (IAM/PFS), and overflow pages — the things that make SQL Server *SQL Server*, not just a SQL parser with a hash table behind it. The fidelity bar is set by the EF Core regression oracle in the `*.Tests.EFCore` project: if EF Core trusts the simulator end-to-end, the simulator is earning its keep. That project must stay green.

When SQL Server's actual behavior is quirky or lossy (CP1252's silent `?` replacement for out-of-codepage characters, ANSI trailing-space padding for `=`, `LEN` excluding trailing spaces), mirror it rather than "fixing" it — authenticity over desirability is what makes the simulator a faithful stand-in.

The overall plan is incremental and non-specific: pick the lowest-effort path that unlocks the most useful application compatibility next. Transactions / locks / MVCC are the next obvious eventual target; order is opportunistic, driven by what's blocking the user's actual application stand-ins. Over time, this accretion gets the simulator to "most apps just work"; near term, work flows to the smallest unlock for the biggest cohort of pain points.

## Public API surface

`Simulation` and `CreateDbConnection()`. That's it. `QualityTests.PublicApiWhitelist` fails the build if anything else becomes public. Resist expanding the surface.

## Architecture shape

One backend: page-format storage. User tables hold a heap of 8KB pages; rows are encoded as bytes via a row encoder/decoder and navigated column-by-column without rehydrating whole rows. Every non-NULL variable-length column carries a 1-byte inline/pointer marker ahead of its bytes. Oversize values for the LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) always flow through a parallel chain of 8KB LOB pages on the same heap; bounded var columns (`varchar(N)` / `nvarchar(N)` / `varbinary(N)`) start inline but the encoder pushes the largest one off-row greedily — repeating until the row fits — when the encoded row would exceed 8060 bytes. The type system has its own `SqlType`/`SqlValue` pair. Expression evaluation has separate paths for runtime evaluation and static type-of resolution (for projection planning).

Top-level subsystems (in `SqlServerSimulator/`):
- `Storage/` — pages, row encoder/decoder, types, values
- `Parser/` — tokenizer, expression tree, query planner
- root — `Simulated*` implementations of `DbCommand`/`DbConnection`/`DbDataReader`/etc., plus `Simulation` itself

`SqlServerSimulator.EFCore/` is a sibling package whose only public surface is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. It registers an `IRelationalTypeMappingSourcePlugin` that substitutes provider-agnostic mappings for the (CLR type, store type) pairs whose default `SqlServer`-provider mappings downcast `DbParameter` to `SqlParameter`: `DateOnly` / `DateTime → date`, `DateTime → smalldatetime`, `TimeOnly` / `TimeSpan → time(N)`, and `decimal → money` / `smallmoney`.

Test projects: main feature tests, internal-access tests for storage/parser internals, EF Core integration (the oracle), and analyzer tests.

## Build, test, format

```
dotnet build
dotnet test
dotnet format --verify-no-changes
```

`dotnet build` runs most analyzers via `EnforceCodeStyleInBuild=true`, but a few formatting rules (notably IDE0055) only fire under `dotnet format` or in Visual Studio. CI runs both — keep both green locally before pushing.

## Project-specific rules

### SSS001 (custom analyzer)
A repo-local analyzer flags any property in a non-public type that's an auto-property or a trivial wrapper over a same-type field. Rationale: non-public types gain no API-stability benefit from property metadata; the wrapper just adds JIT/IDE overhead. Expose the field directly (`public readonly T Foo = expr;`). Overrides, abstracts, statics, and explicit interface implementations are exempt. The analyzer lives in its own project with its own test project.

### MSTEST0049 (error severity)
Configured in `.editorconfig` to fail the build. Async tests must thread `TestContext.CancellationToken` into framework calls. Pattern: `public TestContext TestContext { get; set; } = null!;` on the test class plus a helper that uses `this.TestContext.CancellationToken`.

### AssemblyHooks
Each test project has an `AssemblyHooks.cs` with a `static [TestClass]` hosting `[AssemblyInitialize]`. Use this for assembly-scoped warm-up or sanity checks rather than embedding the hook in a regular test class. The analyzer-tests warm-up specifically prevents Roslyn cache contention under parallel test execution (~3x slowdown without it).

### Test parallelism
Every test project uses `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`. New test projects should follow.

### Exception types
`SimulatedSqlException` mirrors a real SQL Server error — its number, class, state, and message format. Use it when the simulator's behavior matches SQL Server's: invalid SQL, type mismatches, constraint violations, oversize column declarations, truncation. `NotSupportedException` is for valid SQL Server features the simulator hasn't built yet; name the unmodeled feature in the message. The distinction matters to users debugging tests against the simulator: "this is invalid SQL" should look different from "this works against real SQL Server but not yet here."

### Commit messages
Single sentence with period. Concrete artifacts named where useful. Squashes capture the intended end state, not the working steps.

## Test organization

The main test project is split topically — one file per feature (SELECT, WHERE, TOP, INSERT, etc.). When adding tests, prefer extending an existing topical file over creating a new one with overlapping scope. Storage internals are tested separately because they require `internal` access.

Prefer public-API tests when the behavior is reachable from SQL — they exercise the full parse/evaluate path and don't pin internal shapes that may refactor. Reserve `Tests.Internal` for things genuinely unreachable from public SQL: raw storage byte layouts, encoder/decoder invariants, and similar contracts that have no observable public surface.

## Live limitations

Heavy-hitters someone might assume work but don't. Source and `git log` are the truth for what's done; this list is for what isn't.

- Transactions / locks / MVCC.
- `text` / `ntext` / `image` operation restrictions: comparison (Msg 402), ORDER BY / DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) are enforced; function-level restrictions (e.g. `LEN(ntext)` raising Msg 8116) and the legacy `READTEXT`/`WRITETEXT`/`UPDATETEXT` family aren't modeled.
- Aggregate functions: `COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT expr)` / `COUNT_BIG`, `SUM` / `AVG` (with the documented integer-truncating behavior and `decimal(38, max(s, 6))` widening for AVG over decimals), `MAX` / `MIN`, the statistical family (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, and `APPROX_COUNT_DISTINCT` all parse and execute, both standalone and inside `GROUP BY` / `HAVING`. Window-function form (`OVER (...)`) and `STRING_AGG`'s `WITHIN GROUP (ORDER BY ...)` ordering aren't parsed yet. `CHECKSUM_AGG` returns an order-independent XOR fold whose semantic guarantee matches SQL Server (same multiset → same checksum) but whose exact bit pattern won't byte-match a real-server reproduction. `APPROX_COUNT_DISTINCT` is implemented as exact `COUNT(DISTINCT)` since memory optimization isn't a goal here.
- `decimal` / `numeric` values are backed by .NET `decimal`, so values requiring more than 28 significant digits aren't modeled (the type declarations up through `decimal(38, *)` are accepted so storage byte-width still matches SQL Server). `float` text formatting uses .NET's `G15`/`G7` conventions rather than SQL Server's exact `1e+015`-style scientific layout.
- `CONVERT` / `TRY_CONVERT` style-code coverage. Only styles `0`, `120`, and `121` are wired up for date-like sources targeting a character string (the EF Core code-generation defaults). Other style numbers raise Msg 281; money / float / binary style codes and `CONVERT(date, str, 103)`-style date-parsing styles aren't modeled.
- Boolean combinators in WHERE / MERGE-ON / CHECK predicates: `AND` / `OR` / `NOT`, parenthesized groupings, `IS [NOT] NULL`, and `[NOT] IN (literal, literal, ...)` all parse with standard SQL precedence (AND > OR; NOT highest). The pattern `where (arith_expr) cmp rhs` (parens-around-arithmetic as the left side of a comparison) is the one shape the simulator's parser doesn't accept; SQL Server does. `BooleanExpression.Run` returns `bool?` (three-valued: true / false / UNKNOWN); WHERE / MERGE-ON treat UNKNOWN as exclude, CHECK treats UNKNOWN as pass — matching SQL Server. `IS NULL` definitively resolves UNKNOWN to true/false (it's the canonical way to test nullability without falling through tri-state).
- `CASE` expressions: both searched (`CASE WHEN cond THEN ... [ELSE ...] END`) and simple (`CASE input WHEN val THEN ... [ELSE ...] END`) forms parse anywhere an expression is allowed. Branches evaluate in source order; first true predicate wins. UNKNOWN is treated as exclude (matching WHERE), so the simple form's NULL-vs-NULL `WHEN` falls through (`CASE NULL WHEN NULL` → no match). Result type is computed via `SqlType.Promote` across all THEN / ELSE branches and cached on the first `GetSqlType` call; `Run` then coerces matched values to that common type so projection schema stays consistent. No-match-no-ELSE → typed NULL. **Not enforced**: Msg 8133 (real SQL Server raises this when every branch is a bare `NULL` literal — the simulator returns NULL of `int`).
- Subqueries: `EXISTS (SELECT ...)` / `[NOT] EXISTS` and `expr [NOT] IN (SELECT ...)` parse as boolean atoms in WHERE / HAVING / CHECK; scalar subqueries `(SELECT col FROM ...)` parse anywhere an expression is allowed (projection, WHERE comparison, arithmetic operand). All forms work both correlated and non-correlated, with arbitrary outer-scope nesting depth. EXISTS counts rows only (multi-column inner allowed); `IN (SELECT ...)` and scalar subqueries require exactly one inner column (Msg 116). Scalar subqueries also enforce single-row cardinality at runtime (Msg 512, fired per outer row for correlated cases); empty result → NULL of the inner's projected type. NULL semantics in `IN (SELECT ...)` mirror the literal-list IN (NULL row → UNKNOWN unless a non-NULL match wins first). Column resolution honors qualifiers (`alias.col` / `tableName.col`) so a correlated reference to an outer column with the same name as an inner column works correctly. The inner plan re-executes per outer row (no result caching yet — fidelity over performance for now). `Selection.Parse` returns a deferred plan; `Selection.Execute(outerResolver)` materializes results, so the same plan re-runs against different outer rows. **Not modeled**: `ANY` / `SOME` / `ALL` quantifiers, `UNION` / `UNION ALL` inside a subquery body, row-constructor `IN ((1,2), (3,4))`. Derived tables in FROM (already supported) don't see outer scope (no APPLY / lateral).
- `LIKE` `COLLATE` override. The default collation (case-insensitive, Latin1_General-shaped) is what every `LIKE` runs under; explicit `COLLATE` clauses on the predicate aren't parsed yet.
- Cross-category `Promote` for integer ↔ string. Only CAST works for that pair.
- EF Core compatibility: the `SqlServerSimulator.EFCore` adapter (`UseSqlServerSimulator(...)`) covers the seven `SqlParameter`-downcast pairs — `DateOnly → date`, `DateTime → date`, `DateTime → smalldatetime`, `TimeOnly → time(N)`, `TimeSpan → time(N)`, `decimal → money`, `decimal → smallmoney`. Without the adapter (plain `UseSqlServer`), those mappings still throw at SaveChanges as before. The MAX-string family (default-mapped `string` → `nvarchar(max)`, `[Column(TypeName = "varchar(max)")]`, `[Column(TypeName = "varbinary(max)")]`) flows through plain `UseSqlServer` without needing the adapter. Other type pairs the SqlServer provider supports but aren't modeled here (e.g. `hierarchyid`, `geography`, `geometry`, `rowversion`) are not bridged.
- `OUTPUT` and `MERGE` are scoped to the EF Core SaveChanges shape: `INSERT ... OUTPUT INSERTED.<col>` (single-row) and `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch). `OUTPUT INTO @table_var`, `OUTPUT DELETED.*`, OUTPUT on UPDATE/DELETE, `INSERTED.*` star expansion, MERGE source subqueries, MERGE target with column refs in `ON`, the `WHEN MATCHED` UPDATE/DELETE branches, and `$action` aren't supported. The `WHEN MATCHED` branch parses syntactically but throws `NotSupportedException` if the per-row predicate ever evaluates true.
- Session-scoped state. `SCOPE_IDENTITY()` / `@@IDENTITY`, `SET IDENTITY_INSERT`'s active table, and `DBCC TRACEON(N)` flags all live on `Simulation` rather than per-connection. The simulator collapses session vs global scope until a real connection-scoped state model exists; revisit when transactions/locks/MVCC bring the per-session shape with them.
- CAST to a smaller `varchar`/`nvarchar` than the value renders: SQL Server silently truncates; the simulator returns the full string.
- `PRIMARY KEY` / `UNIQUE` on a computed column. SQL Server allows it (silently persisting the values for the underlying index); the simulator throws `NotSupportedException` because the enforcement loop would need to evaluate the computed expression against every existing row at insert time. Auto-generated constraint names use a deterministic FNV-1a hash for the 16-hex suffix, not SQL Server's object-id-derived hex — same shape, stable across runs but won't byte-match a real-server reproduction. PK/UNIQUE enforcement is a linear scan over the heap (O(N) per insert); no B-tree backing yet.
- `CHECK` constraints: parsed and enforced for inline single-column and table-level forms; Msg 547 fires per row on a definitely-false predicate. Msg 8141 (an inline CHECK that references a peer column) isn't enforced — SQL Server rejects at CREATE TABLE; the simulator accepts and lets the CHECK reference any column. Auto-generated names use the same FNV-1a hash convention as PK/UNIQUE; structurally identical to SQL Server's `CK__<table>__[col__]<hex>` shape but won't byte-match a real-server reproduction.
- Heap allocation tracking (IAM/PFS): the page list is flat.
