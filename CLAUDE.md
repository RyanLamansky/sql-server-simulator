# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. Consumers point `Microsoft.Data.SqlClient` or `Microsoft.EntityFrameworkCore.SqlServer` at a `Simulation` instead of a real database. Public surface is `Simulation` + `CreateDbConnection()`; `QualityTests.PublicApiWhitelist` fails the build if anything else leaks public — resist expanding.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. It registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter`.

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server's behavior is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. Final fidelity bar: EF Core trusts the simulator end-to-end. `*.Tests.EFCore` is the regression oracle; must stay green.

Priority is opportunistic: each bundle picks the lowest-effort path that unlocks the most application compatibility next. Near-term order is driven by what's actually blocking real EF Core / SqlClient code.

## Feature-bundle workflow

Standing pattern for non-trivial SQL feature work:

1. **Probe.** Behavior questions get answered against a real SQL Server 2025 instance, not from memory or docs. Connection details for the user's reference instance live in user memory under "Real SQL Server reference instance." Probe scaffolds — both raw SqlClient probes and EF Core emission probes — live in `/tmp/<probe-name>/` console projects and get deleted after the bundle. The git workspace stays free of probe scratch; only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`.
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

### Multi-part identifiers: `MultiPartName`

`Parser/MultiPartName.cs` is the readonly struct carried by every `Reference` expression and passed to runtime / parse-time column resolvers (`Func<MultiPartName, SqlValue>` / `Func<MultiPartName, SqlType>`). Up to 4 inline string slots (matching SQL Server's grammar limit), with `Count` reporting how many are populated. The API is intentionally minimal — three accessors plus the build helper:
- `Leaf` — the rightmost segment (the column / object name itself).
- `ImmediateQualifier` — the segment to the left of `Leaf`, or `null` when unqualified. Pair with `Collation.Default.Equals(name.ImmediateQualifier, "INSERTED")`: the equality folds null-or-unqualified into `false` without a separate guard.
- `Count` — populated-segment count (single SaveChanges-style early reject site uses it).
- `ToString()` — dotted form (`db.schema.table.col`) for error-message interpolation; no `string.Join` at the call site.

`Reference` accumulates parts via `WithAddedPart` (struct reassignment) during parsing. `WithAddedPart` raises **Msg 4104** (`"The multi-part identifier 'X' could not be bound."`) with the full attempted dotted name when a 5th segment would be added — matching the user-visible wire effect of real SQL Server, which parses arbitrary-many parts and rejects them at resolution time.

### SimulatedSqlException factories

Constructor is private. Each error case is an `internal static` factory named per behavior, carrying the SQL Server `(message, number, class, state)` tuple:

```csharp
internal static SimulatedSqlException ArithmeticOverflow(string targetType) =>
    new($"Arithmetic overflow error converting expression to data type {targetType}.", 8115, 16, 8);
```

The number lands in `Data["HelpLink.EvtID"]` for tests to assert. When adding error coverage: add a factory in the partial that matches the error's theme — never construct directly.

`SimulatedSqlException.cs` holds the type definition (fields, constructor, the `[SuppressMessage]` for CA1032). Factories are split across topical partials in the same directory:
- `SimulatedSqlException.TypeErrors.cs` — type lookup, size, CAST / CONVERT, conversion, arithmetic. Largest at ~30 factories.
- `SimulatedSqlException.SchemaErrors.cs` — DDL rules (identity, rowversion, computed columns, table-level invariants, compatibility level).
- `SimulatedSqlException.ConstraintErrors.cs` — per-row write violations (NOT NULL, CHECK, PK / UNIQUE, truncation, row size).
- `SimulatedSqlException.ResolutionErrors.cs` — column / object / identifier resolution.
- `SimulatedSqlException.QueryErrors.cs` — set ops, ORDER BY, aggregates, subqueries, pagination, function lookup.
- `SimulatedSqlException.SyntaxErrors.cs` — generic parse-time errors.

### Aggregators

`Parser/Aggregator.cs` is the abstract base (`Add(SqlValue)` / `Result()`). Implementations live in `Parser/Aggregators/`. To add a new aggregate: subclass `Aggregator`, register in `AggregateExpression`'s dispatch.

### Expression evaluation

`Expression.Run(columnResolver)` is the runtime path; `Expression.GetSqlType(...)` is the static-type-of path used for projection schema. Both must agree on result type — drift breaks union/CASE/coalesce schema. Expressions live in `Parser/Expressions/`.

`BooleanExpression.Run` returns `bool?` (three-valued). WHERE / MERGE-ON exclude UNKNOWN; CHECK passes UNKNOWN.

## Conventions that fail builds

- **SSS001** (custom analyzer): non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;`. Overrides, abstracts, statics, and interface implementations (both explicit and implicit — a property whose name and signature satisfy a member of an implemented interface) are exempt; the interface contract dictates the property shape. Lives in `SqlServerSimulator.Analyzers/`.
- **SSS002** (custom analyzer): a `readonly` field in a non-public-API type whose declared type is a strict supertype of its immediately-assigned initializer should be declared as the concrete type. Same-assembly callers gain no API-stability benefit from the abstraction; the concrete declaration exposes more members directly and avoids virtual dispatch. Public types are exempt; value-typed initializers (boxing) are exempt; const fields and fields without initializers don't apply. After applying the rule, switch / conditional expressions that previously inferred the (now-shed) base type may need an explicit base-type annotation — extract to a helper method with an explicit return type (`SqlType.ResolveSimpleKeyword`), declare the destination variable explicitly (`SqlType resultType = ... ? ...`), or cast a `null` arm.
- **SSS003** (custom analyzer): `string.ToUpperInvariant()` / `string.ToLowerInvariant()` whose result is the *governing expression* of a `switch` statement or `switch` expression allocates a temporary string only to throw it away after dispatch. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on the resulting count (which lets the parser dispatch by length first; matches the established `Parser/Expression.cs:ResolveBuiltIn` and `Storage/SqlType.cs:GetByName` pattern). The rule is intentionally narrow: only the switch-governing case is flagged. Allocating uses where the upper/lower string itself is the function's returned value (e.g. SQL `UPPER`, GUID-to-string casts) don't trip — their result isn't a switch governing expression.
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
`INNER JOIN ... ON`, bare `JOIN` (= INNER), `LEFT [OUTER] JOIN ... ON`, `CROSS JOIN`, `CROSS APPLY (...) [AS alias]`, `OUTER APPLY (...) [AS alias]`. Multi-table chains compose left-to-right. Self-joins via alias work. ON-predicate UNKNOWN excludes. Aliases parse with or without `AS`.

APPLY is the lateral form: the right side is a derived-table SELECT re-executed per outer row, with the outer tuple's columns visible inside its WHERE / projection. CROSS APPLY drops outer rows whose plan yields zero rows (INNER-style); OUTER APPLY null-fills the right side (LEFT-style). No `ON` clause — correlation lives inside the inner WHERE. The lateral plan stays deferred (`FromSource.LateralPlan`) and re-executes via `Selection.Execute(currentTupleResolver)` per outer tuple in `JoinDriver`. EF Core 10 emits `CROSS APPLY` for `SelectMany(a => a.Books.Where(...))` over a collection navigation.

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

### Window functions
`ROW_NUMBER() OVER([PARTITION BY <expr-list>] ORDER BY <expr-list-with-direction>)` only — the single shape EF Core 10 emits for top-N / Skip+Take per group. `RANK` / `DENSE_RANK` / analytic family (`LAG` / `LEAD` / `FIRST_VALUE`) and frame specs (`ROWS BETWEEN`, `RANGE BETWEEN`) aren't modeled — EF Core 10 doesn't emit them from idiomatic LINQ. Result type is `bigint`. ORDER BY is required inside OVER (without it, parse fails — SQL Server raises Msg 4112).

`WindowExpression` (`Parser/Expressions/WindowExpression.cs`) registers itself in `ParserContext.WindowCollector` like aggregates do with `AggregateCollector`. The executor's `ProjectWindowedRows` buffers post-WHERE tuples, partitions by each window's PARTITION BY keys, sorts each partition by ORDER BY keys, assigns row numbers per partition, then walks the buffer in original order binding per-tuple results before projecting. Combining window functions with GROUP BY / HAVING / aggregates raises `NotSupportedException`.

EF Core 10 always wraps ROW_NUMBER in a derived-table subquery: `SELECT ... FROM (SELECT cols, ROW_NUMBER() OVER(...) AS row FROM T) AS sub WHERE sub.row <= N` (Take) or `WHERE 1 < sub.row AND sub.row <= K` (Skip+Take). The simulator's plain-derived-table-doesn't-see-outer-scope limitation doesn't bite here because the ROW_NUMBER subquery has no outer correlation — its OVER refers only to the inner FROM.

### Date scalar functions: `DATEPART` / `DATEADD`
Both take a bare datepart keyword as the first argument (parse-time `Name` token, not an expression). Canonical keywords + common aliases: `year`/`yy`/`yyyy`, `quarter`/`qq`/`q`, `month`/`mm`/`m`, `dayofyear`/`dy`/`y`, `day`/`dd`/`d`, `week`/`wk`/`ww`, `iso_week`/`isowk`/`isoww`, `weekday`/`dw`, `hour`/`hh`, `minute`/`mi`/`n`, `second`/`ss`/`s`, `millisecond`/`ms`, `microsecond`/`mcs`, `nanosecond`/`ns`, `tzoffset`/`tz`. `DATEPART` always returns `int`; `DATEADD` preserves the input's SQL type (`date` stays `date`, `time(N)` stays `time(N)`, etc.).

Per-type keyword compatibility mirrors SQL Server (probed against real SQL Server 2025, 2026-05-07): `date` accepts only date parts; `time(N)` accepts only time parts; `datetime` / `smalldatetime` / `datetime2(N)` accept date and time parts; `datetimeoffset(N)` adds `tzoffset`. Wrong combinations raise **Msg 9810** `"The datepart {part} is not supported by date function {function} for data type {type}."`. Unknown keyword → **Msg 155** `"'{X}' is not a recognized datepart option."`. `DATEADD` overflow (e.g. `dateadd(year, 100000, dateCol)`) → **Msg 517** `"Adding a value to a '{type}' column caused an overflow."`. NULL-value input → typed-NULL output (DATEPART returns NULL int). DATEPART(weekday) uses default `DATEFIRST 7` (Sunday=1, Saturday=7); changing `DATEFIRST` isn't modeled. The week / iso_week algorithm pins the default us_english behavior — week 1 is the week containing January 1, rolling on Sundays.

`DatePartKind` (`Parser/Expressions/DatePartKind.cs`) is the shared enum + helpers; both `DatePart.cs` and `DateAdd.cs` route through it for keyword resolution, type-compatibility validation, extraction, and addition.

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree backing.

### Transactions
Three entry points all backed by the same per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()` / `Commit()` / `Rollback()`), and SQL-text (`BEGIN` / `COMMIT` / `ROLLBACK` / `SAVE TRANSACTION`). Probe-confirmed against SQL Server 2025 (2026-05-08).

**Statement-level atomicity** (auto-commit): a single INSERT / UPDATE / DELETE / MERGE that throws mid-execution rolls back its partial writes. A multi-row INSERT whose third row violates a constraint leaves zero rows behind, not two. Each statement captures a marker at entry; only entries appended this statement are unwound on failure, so a failed statement inside an explicit transaction leaves the surrounding tx alive.

**Explicit transactions** span multiple statements. `DbConnection.BeginTransaction()` returns a `SimulatedDbTransaction`; `Commit()` drops the log, `Rollback()` walks it backwards, dispose-without-explicit-resolution auto-rolls-back. Parallel `BeginTransaction` raises `InvalidOperationException` ("SqlConnection does not support parallel transactions.") matching SqlClient. SQL-text `BEGIN TRAN[SACTION] [name] [WITH MARK]` opens a tx when none active or increments `TranCount` when one is; `COMMIT [TRAN[SACTION]] [name] | WORK` decrements (only outermost actually commits); `ROLLBACK [TRAN[SACTION]] | WORK` zeroes `TranCount` and walks the entire log regardless of depth. `SAVE TRAN[SACTION] <name>` records a marker; `ROLLBACK TRAN[SACTION] <name>` rolls back to it (the path EF Core 10 emits per SaveChanges call inside an explicit tx). `COMMIT` / `ROLLBACK` with no active transaction raise **Msg 3902** / **Msg 3903** verbatim. SqlClient API and SQL-text share the same `SimulatedDbTransaction`, so interleaving works.

**`@@TRANCOUNT`** reads the connection's current depth as `int` (0 when none active).

`Storage/UndoLog.cs` is a row-level LIFO list (`(Heap, UndoKind, pageIdx, slotIdx)` tuples) with a `Position` / `RollbackTo(position)` marker pattern. `Heap.Insert` / `Heap.DeleteAt` take an optional log; on success they append (UPDATE = delete-old + insert-new, two log entries unwound LIFO). `Simulation.RunMutation` wraps each mutation statement: route to the connection's active-transaction log when one exists, else create a fresh per-statement log; capture marker; on exception `RollbackTo(marker)` before re-raising.

Identity counters and the database-scoped rowversion counter bypass the log — both keep advancing even when their consuming inserts are rolled back (probe-confirmed). LOB chains allocated for rolled-back inserts also bypass the log; they leak the same way committed deletes leak. The simulator has no isolation: uncommitted writes are immediately visible to any reader on any connection (single-Simulation, single-thread-at-a-time assumption).

### UPDATE / DELETE
- `UPDATE table SET col = expr [, col = expr]* [WHERE pred]` and `DELETE [FROM] table [WHERE pred]`.
- Multi-table-syntax form (`UPDATE alias SET alias.col = expr FROM table AS alias [WHERE pred]`, `DELETE FROM alias FROM table AS alias [WHERE pred]`) is the EF7+ `ExecuteUpdate` / `ExecuteDelete` shape. Single-source-only — additional sources or joins on the FROM clause raise `NotSupportedException`. Two-pass parsing: collect raw `(columnName, expr)` pairs without resolving ordinals, then bind to the FROM-clause table once known. SET LHS supports both bare `col = expr` and alias-qualified `[a].[col] = expr`; the alias prefix is accepted verbatim and not cross-checked against the FROM-clause's alias since the simulator's row resolvers use `name.Leaf` (the alias is moot for single-source). OUTPUT is only supported on the single-table form (EF doesn't combine OUTPUT with multi-table-syntax) — see `Simulation.Update.cs` / `Simulation.Delete.cs` for the deferred-table-binding pattern.
- **Multi-column SET evaluates RHS against the pre-update row snapshot** — verified: `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` produces `(a=100, b=11)` (b read pre-update a). Scalar subquery RHS sees the pre-update table state.
- Identity-column update → **Msg 8102** `"Cannot update identity column 'X'."`. Computed-column update → Msg 271 (existing factory). Rowversion update → **Msg 272** `"Cannot update a timestamp column."`.
- Per-row constraint re-validation: NOT NULL → **Msg 515** with `"UPDATE fails."` verb; CHECK → **Msg 547** with `"UPDATE statement"` verb. PK / UNIQUE → Msg 2627 (same wording as INSERT — verbatim SQL Server quirk: "Cannot insert duplicate key" wording even on UPDATE).
- Two-phase execution: phase 1 picks affected rows + computes new values + per-row validation; phase 2 validates PK / UNIQUE against the post-update virtual state (other affected rows' new keys + non-affected heap rows' existing keys); phase 3 mutates (tombstone old, insert new).
- `OUTPUT` clause on UPDATE / DELETE supports `INSERTED.<col>` (post-update / new value), `DELETED.<col>` (pre-update / old value), and literal / parameter expressions. UPDATE allows both qualifiers; DELETE rejects `INSERTED.<col>` at parse time → **Msg 4104** (verbatim probed). Bare column refs → Msg 207. Star expansion (`INSERTED.*` / `DELETED.*`) and table-alias qualifiers aren't modeled (parse error). Storage uses page-slot tombstones (high bit on slot directory entry; row payload bytes not reclaimed) and orphaned LOB chains stay in `Heap.LobPages` (see Quirks below).

### `rowversion` (legacy synonym `timestamp`)
8-byte big-endian auto-generated counter, implicitly NOT NULL, at most one per table. Database-scoped monotonic counter (`Simulation.AllocateRowVersion`) — every INSERT into a rowversion-bearing table and every UPDATE that affects a row in one allocates the next value. Storage type name surfaces as `timestamp` in `information_schema` and SqlClient's `DataTypeName` regardless of which keyword the column was declared with.

- Explicit value in INSERT column list → **Msg 273** `"Cannot insert an explicit value into a timestamp column. ..."`.
- Explicit value in UPDATE SET → **Msg 272** `"Cannot update a timestamp column."`.
- Second rowversion column on a table → **Msg 2738** `"A table can only have one timestamp column. ..."`.
- Outbound CAST: `varbinary(N)` and `binary(N)` copy the 8 bytes; `bigint` reads them big-endian (matches the `@@DBTS`-style integer view real SQL Server exposes). No reverse-direction CAST — rowversion values can only be auto-generated.
- `Promote(RowVersion, Varbinary)` → `Varbinary` so the EF Core optimistic-concurrency `WHERE [rv] = @originalRv` pattern (where the parameter binds as `varbinary`) works directly.
- `EF Core [Timestamp]` round-trip works end-to-end: SaveChanges of a modified entity emits `UPDATE ... OUTPUT INSERTED.[RowVersion] WHERE [Id] = @p AND [RowVersion] = @originalRv` and the simulator returns the new rowversion through OUTPUT for EF's change tracker.

### MERGE / OUTPUT (EF Core SaveChanges shape only)
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch).
- `WHEN MATCHED` parses but throws `NotSupportedException` if its predicate ever evaluates true.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers the seven SqlParameter-downcast pairs: `DateOnly → date`, `DateTime → date`, `DateTime → smalldatetime`, `TimeOnly → time(N)`, `TimeSpan → time(N)`, `decimal → money`, `decimal → smallmoney`. Without the adapter, those mappings throw at SaveChanges. The MAX-string family (default `string → nvarchar(max)`, `[Column(TypeName="varchar(max)|varbinary(max)")]`) flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY / DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) are enforced.

### `SimulatedDbDataReader` accessor surface
The `DbDataReader` contract is fully implemented (no remaining `NotImplementedException` throws on supported paths). Typed accessors (`GetBoolean` / `GetInt16` / `GetInt32` / `GetInt64` / `GetByte` / `GetDouble` / `GetFloat` / `GetGuid` / `GetString` / `GetDateTime` / `GetDecimal`) read each column's `SqlValue` directly via the cursor's indexer (`cursor[ordinal]`) and unwrap via `As*` — no `object` boxing. NULL on a typed accessor raises `SqlNullValueException` (matches SqlClient). Polymorphic accessors keep their multi-type fan-in inline:
- `GetDateTime`: `Date` / `DateTime` / `SmallDateTime` / `DateTime2` all return `DateTime`; `Date` surfaces at midnight (`Kind=Unspecified`), matching SqlClient.
- `GetDecimal`: `Decimal` / `Numeric` / `Money` / `SmallMoney`.
- `GetFieldValue<T>`: covers EF Core's `DateOnly`-over-`Date` and `TimeOnly`-over-`Time` direct paths without going through `ToObject()`; other T fall through to `ToObject()` + `(T)` cast.

Type-metadata accessors (`GetDataTypeName` / `GetFieldType`) read from `SimulatedQueryResult.Schema[ordinal]`, which is the abstract metadata channel parallel to `ColumnNames`. `SqlType.SqlServerName` returns the bare catalog name (e.g. `"decimal"`, not `"decimal(18,2)"`); `SqlType.ClrType` returns the BCL type returned by the untyped path. `GetFieldType` carries the trim-aware `[DynamicallyAccessedMembers]` annotation and a single `[UnconditionalSuppressMessage]` covering the closed set of concrete `SqlType` subclasses (which are all well-known BCL types, never linker-pruned in practice), so concrete types stay annotation-free.

`GetOrdinal(name)` is a two-pass linear scan (case-sensitive then case-insensitive — SqlClient's documented match precedence). Typical column counts make this cheaper than building a per-result-set dictionary. `this[string]` routes through it.

`HasRows` is a sticky bit on `RowCursor`. `SqlValueCursor` owns a one-row lookahead — first read of `HasRows` peeks the source enumerator and buffers the row; subsequent `MoveNext()` serves the buffered row. The sticky `everHadRows` flag preserves SqlClient's "remains true after exhaustion" semantic. SqlClient gets this cheap via TDS token peek (one byte tells it whether a `ROW` token follows); the simulator's source has no token discriminator (each `byte[]` *is* a row), so peek-and-buffer is the natural analog.

`GetBytes` / `GetChars` materialize the column's value (the typed `As*` accessor already does this) and slice into the caller's buffer. They honor SqlClient's `buffer == null` length-only contract. Quirk: real SqlClient streams from off-row LOB pages; the simulator decodes the full value per call. Behavior matches per-call observation; the streaming-memory guarantee doesn't.

`GetChar(int)` always raises `InvalidCastException` — same as SqlClient, which only succeeds for `nchar(1)` and otherwise throws.

---

## Not modeled

- Locks / MVCC / isolation levels — the simulator has no isolation; uncommitted writes are immediately visible to all readers (single-Simulation, single-thread-at-a-time assumption). `BEGIN DISTRIBUTED TRANSACTION` and `BEGIN TRANSACTION ... WITH MARK '...'` aren't parsed. `XACT_ABORT` / `SET TRANSACTION ISOLATION LEVEL`.
- `RIGHT JOIN` (rewrite as LEFT with sources swapped); `FULL OUTER JOIN`. Both raise `NotSupportedException` at parse.
- Comma-separated FROM (legacy ANSI-89 join syntax).
- Plain derived tables in FROM (without APPLY) don't see outer scope — they execute eagerly at parse time. Lateral access requires `CROSS APPLY` / `OUTER APPLY`. SQL Server actually allows derived-table-sees-outer in any FROM subquery; the gap shows up in compound shapes like `(SELECT … FROM (SELECT TOP(N) … WHERE outer.col = …) AS t)` where the inner correlation isn't expressed via APPLY.
- `ANY` / `SOME` / `ALL` quantifiers.
- `UNION` / `UNION ALL` inside a subquery body.
- Row-constructor `IN ((1,2), (3,4))`.
- Window functions other than `ROW_NUMBER`: `RANK` / `DENSE_RANK`, analytic (`LAG` / `LEAD` / `FIRST_VALUE` / `LAST_VALUE`), aggregate-OVER form (`SUM(x) OVER(...)` / `COUNT(*) OVER(...)`), frame specs (`ROWS BETWEEN` / `RANGE BETWEEN`). EF Core 10 doesn't emit any of these from idiomatic LINQ.
- `STRING_AGG`'s `WITHIN GROUP (ORDER BY ...)`.
- `LIKE` with `COLLATE` override (default collation only — case-insensitive Latin1_General-shaped).
- `CONVERT` / `TRY_CONVERT` style codes other than `0` / `120` / `121` for date-like → string. Other styles raise Msg 281; money / float / binary style codes and `CONVERT(date, str, 103)`-style date parsing not modeled.
- Cross-category `Promote` for integer ↔ string. Only CAST works that pair.
- `LEN(ntext)` raising Msg 8116 (function-level text/ntext/image restrictions); legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- `OUTPUT INTO @table_var`, `OUTPUT DELETED.*` / `INSERTED.*` star expansion. Per-column `OUTPUT INSERTED.<col>` / `OUTPUT DELETED.<col>` *is* supported (UPDATE / DELETE both); only the star-expansion form is missing. `OUTPUT INTO` (sending the projection to a table variable rather than the result set) isn't.
- Joined-source UPDATE / DELETE FROM clauses (`UPDATE a SET ... FROM t AS a JOIN u AS b ON ...`). The single-source alias form (`UPDATE a SET ... FROM t AS a [WHERE ...]`, `DELETE FROM a FROM t AS a [WHERE ...]`) IS supported — that's what EF7+ `ExecuteUpdate` / `ExecuteDelete` emit, verified against real SQL Server 2025. Adding sources beyond the single aliased target raises `NotSupportedException` so the gap is visible.
- MERGE source subqueries; MERGE target column refs in `ON`; `WHEN MATCHED` UPDATE/DELETE branches; `$action`. EF Core's batched-update path emits semicolon-separated `UPDATE … OUTPUT …` statements rather than `MERGE WHEN MATCHED`, so SaveChanges fidelity doesn't require it.
- Msg 8141 (inline CHECK referencing a peer column — SQL Server rejects at CREATE TABLE; simulator allows).
- Msg 8133 (CASE where every branch is bare `NULL`; simulator returns NULL of `int`).
- `PRIMARY KEY` / `UNIQUE` on a computed column (would need to evaluate the expression against every existing row at insert; `NotSupportedException`).
- Heap allocation tracking: flat page list, no IAM/PFS.
- Per-connection session state for some scopes: `SCOPE_IDENTITY()` / `@@IDENTITY`, `SET IDENTITY_INSERT`'s active table, `DBCC TRACEON(N)` flags all live on `Simulation` rather than the connection. (Transaction state already moved to per-connection in `SimulatedDbConnection.CurrentTransaction`.)
- `hierarchyid`, `geography`, `geometry`, `rowversion`.

---

## Quirks (modeled, not byte-identical to SQL Server)

- `CHECKSUM_AGG`: order-independent XOR fold; semantic guarantee matches (same multiset → same checksum), exact bit pattern won't.
- `APPROX_COUNT_DISTINCT`: implemented as exact `COUNT(DISTINCT)` (memory optimization isn't a goal here).
- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15` / `G7` rather than SQL Server's `1e+015`-style scientific.
- CAST to a smaller `varchar` / `nvarchar` than the value renders: SQL Server silently truncates; simulator returns the full string.
- Auto-generated PK / UNIQUE / CHECK constraint names: structurally `PK__<table>__<hex>` / `UQ__...` / `CK__<table>__[col__]<hex>` matching SQL Server's shape; the 16-hex suffix is a deterministic FNV-1a hash, not SQL Server's object-id-derived hex (stable across runs but won't byte-match a real-server reproduction).
- **DELETE / UPDATE leak page space**: deleted (or UPDATE-relocated) row payload bytes stay in their original page until process exit; only the slot is tombstoned. Slot directory entries are also never reused. SQL Server has ghost-cleanup background work that the simulator doesn't model.
- **DELETE / UPDATE leak LOB chains**: when a row is removed or its LOB-pointed value replaced, the orphaned LOB chain stays in `Heap.LobPages`. Other rows reference LOB pages by stable index, so list compaction would corrupt them; full LOB lifecycle (free-list / tombstones) isn't modeled.
- **Mass-shift UPDATE on a unique key**: `UPDATE t SET k = k + 1` where `k` is PK / UNIQUE produces a per-row collision check that may spuriously raise Msg 2627 — the simulator's two-phase validator compares each affected row's new key against other affected rows' new keys, so a "shift" pattern where post-shift values overlap pre-shift values triggers a false positive. SQL Server uses a temp store that staging-applies all updates before validation. Real EF Core SaveChanges patterns don't hit this.
- **`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Real SqlClient streams from off-row LOB pages; the simulator's per-call observation matches but the streaming-memory guarantee doesn't.
