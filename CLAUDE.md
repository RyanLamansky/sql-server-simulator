# Claude Working Notes

Canonical orientation for working in this repo. Claude Code reads this file automatically; `README.md` is user-facing and not auto-loaded.

## What this is

SqlServerSimulator is a .NET library that emulates a SQL Server instance in-process. Code written against `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` can use a `Simulation` instance instead of a real database — useful for fast deterministic tests, for reproducing pathological SQL Server behaviors that are hard to set up on a real server, and for any scenario where embedding a database avoids the operational weight of a real one.

## Long-term direction

High-fidelity SQL Server simulation, eventually including transactions, locks, MVCC, real allocation tracking (IAM/PFS), and overflow pages — the things that make SQL Server *SQL Server*, not just a SQL parser with a hash table behind it. The fidelity bar is set by the EF Core regression oracle in the `*.Tests.EFCore` project: if EF Core trusts the simulator end-to-end, the simulator is earning its keep. That project must stay green.

When SQL Server's actual behavior is quirky or lossy (CP1252's silent `?` replacement for out-of-codepage characters, ANSI trailing-space padding for `=`, `LEN` excluding trailing spaces), mirror it rather than "fixing" it — authenticity over desirability is what makes the simulator a faithful stand-in.

The overall plan is incremental and non-specific: pick the lowest-effort path that unlocks the most useful application compatibility next. Transactions / locks / MVCC, the `varchar(MAX)` family, the EF Core adapter — all of these are eventual targets, but the order is opportunistic, driven by what's blocking the user's actual application stand-ins. Over time, this accretion gets the simulator to "most apps just work"; near term, work flows to the smallest unlock for the biggest cohort of pain points.

## Public API surface

`Simulation` and `CreateDbConnection()`. That's it. `QualityTests.PublicApiWhitelist` fails the build if anything else becomes public. Resist expanding the surface.

## Architecture shape

One backend: page-format storage. User tables hold a heap of 8KB pages; rows are encoded as bytes via a row encoder/decoder and navigated column-by-column without rehydrating whole rows. The type system has its own `SqlType`/`SqlValue` pair. Expression evaluation has separate paths for runtime evaluation and static type-of resolution (for projection planning).

Top-level subsystems (in `SqlServerSimulator/`):
- `Storage/` — pages, row encoder/decoder, types, values
- `Parser/` — tokenizer, expression tree, query planner
- root — `Simulated*` implementations of `DbCommand`/`DbConnection`/`DbDataReader`/etc., plus `Simulation` itself

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
`SimulatedSqlException` mirrors a real SQL Server error — its number, class, state, and message format. Use it when the simulator's behavior matches SQL Server's: invalid SQL, type mismatches, constraint violations, oversize column declarations, truncation. `NotSupportedException` is for valid SQL Server features the simulator hasn't built yet (row-overflow pages, `varchar(MAX)`, etc.); name the unmodeled feature in the message. The distinction matters to users debugging tests against the simulator: "this is invalid SQL" should look different from "this works against real SQL Server but not yet here."

### Commit messages
Single sentence with period. Concrete artifacts named where useful. Squashes capture the intended end state, not the working steps.

## Test organization

The main test project is split topically — one file per feature (SELECT, WHERE, TOP, INSERT, etc.). When adding tests, prefer extending an existing topical file over creating a new one with overlapping scope. Storage internals are tested separately because they require `internal` access.

Prefer public-API tests when the behavior is reachable from SQL — they exercise the full parse/evaluate path and don't pin internal shapes that may refactor. Reserve `Tests.Internal` for things genuinely unreachable from public SQL: raw storage byte layouts, encoder/decoder invariants, and similar contracts that have no observable public surface.

## Live limitations

Heavy-hitters someone might assume work but don't. Source and `git log` are the truth for what's done; this list is for what isn't.

- Transactions / locks / MVCC.
- Row-overflow / LOB pages and the `varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` types they enable.
- `decimal` / `numeric` values are backed by .NET `decimal`, so values requiring more than 28 significant digits aren't modeled (the type declarations up through `decimal(38, *)` are accepted so storage byte-width still matches SQL Server). `float` text formatting uses .NET's `G15`/`G7` conventions rather than SQL Server's exact `1e+015`-style scientific layout. `money` / `smallmoney` are wired in raw SQL but unreachable through EF Core for the same `SqlParameter`-downcast reason as the date-only / time-only mappings — adding a money column to an EF entity breaks that entity's whole save path.
- `NEWSEQUENTIALID()` (deferred until `DEFAULT`-clause support exists in `CREATE TABLE`; the function is only valid in that context).
- `CONVERT` / `TRY_CONVERT` style-code coverage. Only styles `0`, `120`, and `121` are wired up for date-like sources targeting a character string (the EF Core code-generation defaults). Other style numbers raise Msg 281; money / float / binary style codes and `CONVERT(date, str, 103)`-style date-parsing styles aren't modeled.
- `LIKE` `COLLATE` override. The default collation (case-insensitive, Latin1_General-shaped) is what every `LIKE` runs under; explicit `COLLATE` clauses on the predicate aren't parsed yet.
- Cross-category `Promote` for integer ↔ string. Only CAST works for that pair.
- EF Core compatibility: the SqlServer provider downcasts `DbParameter` to `SqlParameter` for some mappings — `DateTime → date`, `DateTime → smalldatetime`, `DateOnly`, `TimeOnly`, `TimeSpan`, and `decimal → money` / `decimal → smallmoney` (via `SqlServerDecimalTypeMapping`) all break at SaveChanges. See `SimulatedDbParameter` for the matrix; a `SqlServerSimulator.EFCore` adapter package is planned to close the gap.
- `OUTPUT` and `MERGE` are scoped to the EF Core SaveChanges shape: `INSERT ... OUTPUT INSERTED.<col>` (single-row) and `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch). `OUTPUT INTO @table_var`, `OUTPUT DELETED.*`, OUTPUT on UPDATE/DELETE, `INSERTED.*` star expansion, MERGE source subqueries, MERGE target with column refs in `ON`, the `WHEN MATCHED` UPDATE/DELETE branches, and `$action` aren't supported. The `WHEN MATCHED` branch parses syntactically but throws `NotSupportedException` if the per-row predicate ever evaluates true.
- Session-scoped state. `SCOPE_IDENTITY()` / `@@IDENTITY`, `SET IDENTITY_INSERT`'s active table, and `DBCC TRACEON(N)` flags all live on `Simulation` rather than per-connection. The simulator collapses session vs global scope until a real connection-scoped state model exists; revisit when transactions/locks/MVCC bring the per-session shape with them.
- CAST to a smaller `varchar`/`nvarchar` than the value renders: SQL Server silently truncates; the simulator returns the full string.
- Heap allocation tracking (IAM/PFS): the page list is flat.
