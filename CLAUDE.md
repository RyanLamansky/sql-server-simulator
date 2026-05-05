# Claude Working Notes

Canonical orientation for working in this repo. Claude Code reads this file automatically; `README.md` is user-facing and not auto-loaded.

## What this is

SqlServerSimulator is a .NET library that emulates a SQL Server instance in-process. Code written against `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` can use a `Simulation` instance instead of a real database — useful for fast deterministic tests, for reproducing pathological SQL Server behaviors that are hard to set up on a real server, and for any scenario where embedding a database avoids the operational weight of a real one.

## Long-term direction

High-fidelity SQL Server simulation, eventually including transactions, locks, MVCC, real allocation tracking (IAM/PFS), and overflow pages — the things that make SQL Server *SQL Server*, not just a SQL parser with a hash table behind it. The fidelity bar is set by the EF Core regression oracle in the `*.Tests.EFCore` project: if EF Core trusts the simulator end-to-end, the simulator is earning its keep. That project must stay green.

When SQL Server's actual behavior is quirky or lossy (CP1252's silent `?` replacement for out-of-codepage characters, ANSI trailing-space padding for `=`, `LEN` excluding trailing spaces), mirror it rather than "fixing" it — authenticity over desirability is what makes the simulator a faithful stand-in.

A long arc replacing in-memory boxed-object row storage with page-format-aligned encoding completed before this file was written; the simulator now has a single backend with real 8KB pages. Transactions / locks / MVCC are the next phase.

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
- Decimal / numeric / float / real / money types. (Also blocks fractional-day `cast(0.5 as datetime)`.)
- Fixed-length `char(N)` / `nchar(N)` / `binary(N)`, and identity columns.
- `NEWSEQUENTIALID()` (deferred until `DEFAULT`-clause support exists in `CREATE TABLE`; the function is only valid in that context).
- Pattern matching (`LIKE`) and `CONVERT`.
- Cross-category `Promote` for `varchar` ↔ `nvarchar` and integer ↔ string. Only CAST works for those pairs.
- EF Core compatibility: the SqlServer provider downcasts `DbParameter` to `SqlParameter` for some mappings — `DateTime → date`, `DateTime → smalldatetime`, `DateOnly`, `TimeOnly`, `TimeSpan` all break at SaveChanges. See `SimulatedDbParameter` for the matrix; a `SqlServerSimulator.EFCore` adapter package is planned to close the gap.
- CAST to a smaller `varchar`/`nvarchar` than the value renders: SQL Server silently truncates; the simulator returns the full string.
- Heap allocation tracking (IAM/PFS): the page list is flat.
