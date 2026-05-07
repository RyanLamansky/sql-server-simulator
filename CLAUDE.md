# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. Consumers point `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` at a `Simulation` instead of a real database. Public surface is `Simulation` + `CreateDbConnection()`; `QualityTests.PublicApiWhitelist` fails the build if anything else leaks public — resist expanding.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. It registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter`.

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server's behavior is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. Final fidelity bar: EF Core trusts the simulator end-to-end. `*.Tests.EFCore` is the regression oracle; must stay green.

Priority is opportunistic: each bundle picks the lowest-effort path that unlocks the most application compatibility next. Transactions / locks / MVCC are the eventual obvious target; near-term order is driven by what's actually blocking real EF Core / SqlClient code.

## Feature-bundle workflow

Standing pattern for non-trivial SQL feature work:

1. **Probe.** Behavior questions get answered against a real SQL Server 2025 instance, not from memory or docs. Connection details for the user's reference instance live in user memory under "Real SQL Server reference instance."
2. **Surface decisions.** Before writing code, surface 2–3 concrete design choices and recommend one each. The user is decisive at choice points.
3. **Implement + test.** Tests in `*.Tests` exercise the public API path; `*.Tests.EFCore` validates the oracle. Use `*.Tests.Internal` only for things genuinely unreachable from public SQL.
4. **Update CLAUDE.md.** Move bullets between "What's modeled" / "Not modeled" / "Quirks" as scope changes.
5. **Single-sentence commit.** Squashes capture the end state, not working steps. Don't run `git commit` — the user holds signing credentials.

## Build / test / format

```
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Each `.csproj` sets `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, so `dotnet build` runs IDE-prefixed analyzers as build errors. `dotnet format whitespace` is the only thing build doesn't cover — IDE0055 and the textual rules (`csharp_space_*`, `csharp_indent_*`, `csharp_new_line_*`, trailing whitespace, final-newline-at-EOF) live in Roslyn's formatter, not its analyzer host. The `style` and `analyzers` sub-pipelines overlap the build, so CI runs only `dotnet format whitespace` separately. Run the full `dotnet format` locally — that's still useful for picking up any drift.

CI matrix runs Debug + Release builds (conditional compilation can differ). Test parallelism is method-level on every project (`[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`); follow that on new test projects.

If `obj/` permission errors appear (e.g. `Up2Date` access denied), the user has been building outside the dev container; `rm -rf obj/ bin/` clears them.

## Architecture — load-bearing patterns

Top-level layout (`SqlServerSimulator/`):
- `Storage/` — pages, types, row encoder/decoder, heap, constraints
- `Parser/` — tokenizer, expressions, query planning + execution
- root — `Simulated*` ADO.NET implementations, `Simulation`

### Storage

8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating. Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) flow through a parallel chain of 8KB LOB pages on the same heap. Bounded `varchar(N)` / `nvarchar(N)` / `varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits within 8060 bytes. Allocation tracking is a flat page list (no IAM/PFS).

`SqlType` and `SqlValue` are the storage-layer type pair. Two coercion paths exist intentionally:
- `SqlValue.Coerce` — runtime value coercion
- `SqlType.Promote` — static type unification, used by CASE / set ops / COALESCE

### Selection: deferred plan, partial-class split

`Selection.cs` and `Selection.Execution.cs` are a partial-class pair (parser side / executor side). Don't grep just one.

```
Selection.Parse(parserContext, depth, outerTypeResolver?) → Selection
Selection.Execute(outerResolver?) → SimulatedSqlResultSet
```

`Parse` returns a deferred plan; `Execute` runs it. Correlated subqueries re-run the same plan once per outer row by passing different `outerResolver` values:
- `outerTypeResolver: Func<List<string>, SqlType>?` — outer column types at parse time (projection planning)
- `outerResolver: Func<List<string>, SqlValue>?` — outer column values at execute time

`List<string>` is the multipart name (`["t1", "id"]` or `["id"]`). Outer scope chains via `ParserContext.OuterTypeResolver` (parse) and the runtime `outerResolver` argument (execute); both walk arbitrary nesting depth.

### Multi-source rows: FromSource[]

`Parser/FromSource.cs` is the per-source bundle (qualifier, columns, storage handle, row enumeration). Multi-source FROM (joins, set-op branches that join internally) uses `FromSource[]`; rows during enumeration are `byte[]?[]` — one slot per source, null = unmatched LEFT JOIN right side.

Column resolution is qualifier-aware: `alias.col` / `tableName.col` restricts to the matching source; an unqualified name that resolves in more than one source raises **Msg 209**. `FindSourceColumn` / `ResolveAcrossTuple` are the lookup helpers.

### SimulatedSqlException factories

Constructor is private. Each error case is an `internal static` factory named per behavior, carrying the SQL Server `(message, number, class, state)` tuple:

```csharp
internal static SimulatedSqlException ArithmeticOverflow(string targetType) =>
    new($"Arithmetic overflow error converting expression to data type {targetType}.", 8115, 16, 8);
```

When adding error coverage: add a factory; never construct directly. The number lands in `Data["HelpLink.EvtID"]` for tests to assert.

### Aggregators

`Parser/Aggregator.cs` is the abstract base (`Add(SqlValue)` / `Result()`). Implementations live in `Parser/Aggregators/`. To add a new aggregate: subclass `Aggregator`, register in `AggregateExpression`'s dispatch.

### Expression evaluation

`Expression.Run(columnResolver)` is the runtime path; `Expression.GetSqlType(...)` is the static-type-of path used for projection schema. Both must agree on result type — drift breaks union/CASE/coalesce schema. Expressions live in `Parser/Expressions/`.

`BooleanExpression.Run` returns `bool?` (three-valued). WHERE / MERGE-ON exclude UNKNOWN; CHECK passes UNKNOWN.

## Conventions that fail builds

- **SSS001** (custom analyzer): non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;`. Overrides, abstracts, statics, explicit-interface members are exempt. Lives in `SqlServerSimulator.Analyzers/`.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;` plus a helper that uses `this.TestContext.CancellationToken`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; use typed asserts over generic `Assert.AreEqual` on known types.
- **AssemblyHooks**: each test project has `AssemblyHooks.cs` with `static [TestClass]` hosting `[AssemblyInitialize]`. The analyzer-tests warm-up specifically prevents Roslyn cache contention under parallel execution (~3x slowdown without it).

## Style notes

User-corrected drift to avoid:

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly. Describe what is, not when.
- **No comments inside expression chains** — VS IDE0055 fails on comments in ternary chains or between `=>` and body, even when `dotnet format` accepts them. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (the SSS001 rationale, generalized).
- **Squashes capture end state**, not working steps.
- **Parallel architectures over bridges** — when replacing a subsystem, build the new one parallel to the old, no shims.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message format.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature in the message.

The distinction matters to users debugging tests against the simulator: "this is invalid" vs "this works upstream but not here yet."

---

## What's modeled (with subtleties)

### Boolean combinators (WHERE / MERGE-ON / CHECK)
`AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `[NOT] IN (literal, ...)`. Standard precedence (AND > OR; NOT highest). Tri-valued via `BooleanExpression.Run → bool?`. `IS NULL` definitively resolves UNKNOWN to true/false. Quirk: `where (arith_expr) cmp rhs` (parens around arithmetic as the LHS of a comparison) doesn't parse here; SQL Server accepts it.

### Set operations (UNION / UNION ALL / INTERSECT / EXCEPT)
UNION dedupes; UNION ALL preserves duplicates; INTERSECT and EXCEPT both dedupe their inputs. **NULLs are equal during set-op dedup/matching** (opposite of `=`'s tri-state). Result column names from first branch; types promoted via `SqlType.Promote`, branch values coerced to combined schema.

Precedence: INTERSECT > UNION/EXCEPT (which are co-equal, left-to-right). Mismatched column count → Msg 205.

ORDER BY: a non-set-op SELECT keeps branch-internal ORDER BY (which can reference non-projected source columns); when a set-op follows the first branch, per-branch ORDER BY → Msg 156. Top-level ORDER BY (after the chain) wraps via `ApplyTopLevelOrderBy` and references first-branch column names only — no source-column fallback. `Selection.HasOrderBy` is the parse-time signal that gates Msg 156 in `CombineSetOps`.

### JOINs
`INNER JOIN ... ON`, bare `JOIN` (= INNER), `LEFT [OUTER] JOIN ... ON`, `CROSS JOIN`. Multi-table chains compose left-to-right. Self-joins via alias work. ON-predicate UNKNOWN excludes. Aliases parse with or without `AS`.

### CASE
Searched (`CASE WHEN cond THEN ... [ELSE ...] END`) and simple (`CASE input WHEN val ...`). Branches evaluate in order; first true predicate wins. UNKNOWN excludes (matches WHERE); simple-form `CASE NULL WHEN NULL` falls through. Result type computed via `SqlType.Promote` across all THEN/ELSE, cached on first `GetSqlType`; `Run` coerces matched values to the common type. No-match-no-ELSE → typed NULL.

### Subqueries
- `EXISTS (SELECT ...)` / `NOT EXISTS` — boolean atom in WHERE/HAVING/CHECK. Multi-column inner allowed.
- `expr [NOT] IN (SELECT ...)` — boolean atom; exactly one inner column (Msg 116). NULL semantics mirror literal-list IN (NULL row → UNKNOWN unless a non-NULL match wins first).
- Scalar subquery `(SELECT col FROM ...)` — anywhere an expression is allowed. Exactly one inner column (Msg 116). Single-row cardinality enforced at runtime (Msg 512, fired per outer row for correlated). Empty result → typed NULL.

All forms work correlated and non-correlated, arbitrary nesting depth. Inner plan re-executes per outer row (no caching — fidelity over performance).

### Pagination (`OFFSET ... FETCH`)
`OFFSET n ROWS [FETCH NEXT|FIRST k ROW|ROWS ONLY]` attached to ORDER BY. `ROW`/`ROWS` and `NEXT`/`FIRST` are interchangeable. OFFSET requires ORDER BY (no ORDER BY → Msg 102 generic syntax error). FETCH alone (no preceding OFFSET) → **Msg 153** `"Invalid usage of the option next in the FETCH statement."` Counts resolve at parse time (constants, parameters, arithmetic like `1+1`); validated at parse: negative offset → **Msg 10742** `"The offset specified in a OFFSET clause may not be negative."` (verbatim "a OFFSET"); fetch &le; 0 → **Msg 10744** `"... must be greater then zero."` (verbatim typo "then").

TOP and OFFSET are mutually exclusive on the same SELECT — both present → **Msg 10741** `"A TOP can not be used in the same query or sub-query as a OFFSET."` Detected at parse time before any rows are read.

Top-level OFFSET/FETCH (post-set-op chain) attaches alongside the top-level ORDER BY in `ApplyTopLevelOrderBy` and operates on the combined result. Per-branch OFFSET/FETCH in a non-final set-op branch inherits the existing Msg 156 rejection (since OFFSET requires ORDER BY which is already rejected per-branch). Inside a derived table: works (the inner SELECT is its own scope).

### Aggregates
`COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT expr)` / `COUNT_BIG`, `SUM` / `AVG` (integer-truncating; `decimal(38, max(s, 6))` widening for AVG over decimals), `MAX` / `MIN`, statistical (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`. Standalone and inside `GROUP BY` / `HAVING`.

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree backing.

### MERGE / OUTPUT (EF Core SaveChanges shape only)
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch).
- `WHEN MATCHED` parses but throws `NotSupportedException` if its predicate ever evaluates true.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers the seven SqlParameter-downcast pairs: `DateOnly → date`, `DateTime → date`, `DateTime → smalldatetime`, `TimeOnly → time(N)`, `TimeSpan → time(N)`, `decimal → money`, `decimal → smallmoney`. Without the adapter, those mappings throw at SaveChanges. The MAX-string family (default `string → nvarchar(max)`, `[Column(TypeName="varchar(max)|varbinary(max)")]`) flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY / DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) are enforced.

---

## Not modeled

- Transactions / locks / MVCC.
- `RIGHT JOIN` (rewrite as LEFT with sources swapped); `FULL OUTER JOIN`. Both raise `NotSupportedException` at parse.
- Comma-separated FROM (legacy ANSI-89 join syntax).
- `CROSS APPLY` / `OUTER APPLY` (lateral). Derived tables in FROM don't see outer scope.
- `ANY` / `SOME` / `ALL` quantifiers.
- `UNION` / `UNION ALL` inside a subquery body.
- Row-constructor `IN ((1,2), (3,4))`.
- Window-function aggregate form (`OVER (...)`).
- `STRING_AGG`'s `WITHIN GROUP (ORDER BY ...)`.
- `LIKE` with `COLLATE` override (default collation only — case-insensitive Latin1_General-shaped).
- `CONVERT` / `TRY_CONVERT` style codes other than `0` / `120` / `121` for date-like → string. Other styles raise Msg 281; money / float / binary style codes and `CONVERT(date, str, 103)`-style date parsing not modeled.
- Cross-category `Promote` for integer ↔ string. Only CAST works that pair.
- `LEN(ntext)` raising Msg 8116 (function-level text/ntext/image restrictions); legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- `OUTPUT INTO @table_var`, `OUTPUT DELETED.*`, OUTPUT on UPDATE/DELETE, `INSERTED.*` star expansion.
- MERGE source subqueries; MERGE target column refs in `ON`; `WHEN MATCHED` UPDATE/DELETE branches; `$action`.
- Msg 8141 (inline CHECK referencing a peer column — SQL Server rejects at CREATE TABLE; simulator allows).
- Msg 8133 (CASE where every branch is bare `NULL`; simulator returns NULL of `int`).
- `PRIMARY KEY` / `UNIQUE` on a computed column (would need to evaluate the expression against every existing row at insert; `NotSupportedException`).
- Heap allocation tracking: flat page list, no IAM/PFS.
- Per-connection session state. `SCOPE_IDENTITY()` / `@@IDENTITY`, `SET IDENTITY_INSERT`'s active table, `DBCC TRACEON(N)` flags all live on `Simulation`. Revisit when transactions/locks/MVCC bring per-session shape.
- `hierarchyid`, `geography`, `geometry`, `rowversion`.

---

## Quirks (modeled, not byte-identical to SQL Server)

- `CHECKSUM_AGG`: order-independent XOR fold; semantic guarantee matches (same multiset → same checksum), exact bit pattern won't.
- `APPROX_COUNT_DISTINCT`: implemented as exact `COUNT(DISTINCT)` (memory optimization isn't a goal here).
- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15` / `G7` rather than SQL Server's `1e+015`-style scientific.
- CAST to a smaller `varchar` / `nvarchar` than the value renders: SQL Server silently truncates; simulator returns the full string.
- Auto-generated PK / UNIQUE / CHECK constraint names: structurally `PK__<table>__<hex>` / `UQ__...` / `CK__<table>__[col__]<hex>` matching SQL Server's shape; the 16-hex suffix is a deterministic FNV-1a hash, not SQL Server's object-id-derived hex (stable across runs but won't byte-match a real-server reproduction).
