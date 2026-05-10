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

Each `.csproj` sets `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, so `dotnet build` runs IDE-prefixed analyzers as build errors. `dotnet format whitespace` is the only thing build doesn't cover — IDE0055 and the textual rules live in Roslyn's formatter, not its analyzer host. CI runs `dotnet format whitespace` separately; run the full `dotnet format` locally for drift.

CI matrix runs Debug + Release (conditional compilation can differ). Test parallelism is method-level on every project (`[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`); follow that on new test projects.

If `obj/` permission errors appear (e.g. `Up2Date` access denied), the user has been building outside the dev container; `rm -rf obj/ bin/` clears them.

## Architecture — load-bearing patterns

Top-level layout (`SqlServerSimulator/`):
- `Storage/` — pages, types, row encoder/decoder, heap, constraints
- `Parser/` — tokenizer, expressions, query planning + execution
- `Simulation/` — `Simulation` partial-class root and per-statement-kind partials (`Simulation.Create.cs`, `Simulation.Insert.cs`, etc.)
- `Errors/` — `SimulatedSqlException` partial-class root and topical factory partials
- root — `Simulated*` ADO.NET implementations

### Storage

8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating. Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) flow through a parallel chain of 8KB LOB pages on the same heap. Bounded `varchar(N)` / `nvarchar(N)` / `varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits within 8060 bytes. Allocation tracking is a flat page list (no IAM/PFS).

`SqlType` and `SqlValue` are the storage-layer type pair. Two coercion paths exist intentionally:
- `SqlValue.Coerce` — runtime value coercion
- `SqlType.Promote` — static type unification, used by CASE / set ops / COALESCE
- `SqlType.PromoteForArithmetic(a, b, op)` — per-operator decimal/integer/money/float arithmetic result type. Single source of truth: both `TwoSidedExpression.GetSqlType` (static schema) and `DecimalArithmetic` (runtime) call it. Static / runtime parity is required because the row encoder rejects values whose runtime type doesn't match the schema's declared type.

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

**Derived tables in FROM are always deferred** — `ParseSingleFromSource` stores every `(SELECT ...) AS alias` source on `FromSource.LateralPlan` and the executor re-runs it through `Selection.Execute(outerResolver)` per surrounding row. This matches SQL Server's "any FROM derived table can correlate" rule (not just APPLY); required for shapes like `(SELECT COUNT(*) FROM (SELECT DISTINCT col FROM t WHERE t.k = outer.k) AS sub)`. Static parse-time correlation detection isn't sufficient because outer references in WHERE / ON predicates resolve through `Run`, not `GetSqlType`. Cost is one inner-plan execution per outer Execute call regardless. `JoinDriver`'s lateral branch handles both APPLY and ordinary derived tables in INNER / LEFT / CROSS join slots.

### Multi-source rows: FromSource[]

`Parser/FromSource.cs` is the per-source bundle (qualifier, columns, storage handle, row enumeration). Multi-source FROM uses `FromSource[]`; rows during enumeration are `byte[]?[]` — one slot per source, null = unmatched LEFT JOIN right side.

Column resolution is qualifier-aware: `alias.col` / `tableName.col` restricts to the matching source; an unqualified name that resolves in more than one source raises **Msg 209**. `FindSourceColumn` / `ResolveAcrossTuple` are the lookup helpers.

### Multi-part identifiers: `MultiPartName`

`Parser/MultiPartName.cs` is the readonly struct carried by every `Reference` expression and passed to runtime / parse-time column resolvers (`Func<MultiPartName, SqlValue>` / `Func<MultiPartName, SqlType>`). Up to 4 inline string slots (matching SQL Server's grammar limit). The API is intentionally minimal:
- `Leaf` — rightmost segment.
- `ImmediateQualifier` — segment to the left of `Leaf`, or `null` when unqualified. Pair with `Collation.Default.Equals(name.ImmediateQualifier, "INSERTED")` — the equality folds null-or-unqualified into `false`.
- `Count` — populated-segment count.
- `ToString()` — dotted form (`db.schema.table.col`) for error-message interpolation.

`Reference` accumulates parts via `WithAddedPart` during parsing. A 5th segment raises **Msg 4104** matching the user-visible wire effect of real SQL Server.

### SimulatedSqlException factories

Constructor is private. Each error case is an `internal static` factory named per behavior:

```csharp
internal static SimulatedSqlException ArithmeticOverflow(string targetType) =>
    new($"Arithmetic overflow error converting expression to data type {targetType}.", 8115, 16, 8);
```

The number lands in `Data["HelpLink.EvtID"]` for tests to assert. When adding error coverage, add a factory in the partial that matches the error's theme — never construct directly. Topical partials live in `Errors/`: `TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`. Grep for an existing factory before adding a new one.

### Expression evaluation

`Expression.Run(columnResolver)` is the runtime path; `Expression.GetSqlType(...)` is the static-type-of path used for projection schema. Both must agree on result type — drift breaks union/CASE/coalesce schema. Expressions live in `Parser/Expressions/`.

`BooleanExpression.Run` returns `bool?` (three-valued). WHERE / MERGE-ON exclude UNKNOWN; CHECK passes UNKNOWN.

`Parser/Aggregator.cs` is the abstract base (`Add(SqlValue)` / `Result()`). Implementations live in `Parser/Aggregators/`. To add a new aggregate: subclass, register in `AggregateExpression`'s dispatch.

## Conventions that fail builds

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;`. Overrides, abstracts, statics, and interface implementations (explicit + implicit) are exempt.
- **SSS002**: a `readonly` field in a non-public-API type whose declared type is a strict supertype of its initializer should be declared as the concrete type. Same-assembly callers gain no API-stability benefit; concrete declaration exposes more members and avoids virtual dispatch. Public types, value-typed initializers (boxing), const fields, and uninitialized fields are exempt. After applying, `null` arms / switch arms may need explicit base-type annotations — extract to a helper with explicit return type or annotate the variable.
- **SSS003**: `string.ToUpperInvariant()` / `ToLowerInvariant()` whose result is the *governing expression* of a `switch` allocates a temporary string. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on the resulting count (lets dispatch by length first; matches `Parser/Expression.cs:ResolveBuiltIn` and `Storage/SqlType.cs:GetByName`). Only the switch-governing case is flagged.
- **SSS004**: two or more `if` / `else if` branches (or consecutive `if (cond) { return/throw; }`) whose conditions all have the shape `<sameScrutinee> is <SameType> { <SameProperty>: ... }` should be a single `switch`. The `switch` form fuses the type test and property read into one `isinst` + one `ldfld` (verified via `ilspycmd -il`); the `if`-chain form repeats both per arm. Scrutinee must be syntactically simple (locals, parameters, `this`, dotted member access) to avoid changing semantics. Property-pattern designations and multiple subpatterns are skipped.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;` plus a helper that uses `this.TestContext.CancellationToken`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; use typed asserts over generic `Assert.AreEqual`.
- **AssemblyHooks**: each test project has `AssemblyHooks.cs` with `static [TestClass]` hosting `[AssemblyInitialize]`. The analyzer-tests warm-up specifically prevents Roslyn cache contention under parallel execution (~3x slowdown without it).

## Style notes

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly. Describe what is, not when.
- **No comments inside expression chains** — IDE0055 fails on comments in ternary chains or between `=>` and body, even when `dotnet format` accepts them. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (the SSS001 rationale, generalized).
- **Squashes capture end state**, not working steps.
- **Parallel architectures over bridges** — when replacing a subsystem, build the new one parallel to the old, no shims.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message format.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature in the message.

The distinction matters to users debugging: "this is invalid" vs "this works upstream but not here yet."

---

## What's modeled

The catalog below is intentionally terse — `*.Tests` and `*.Tests.EFCore` are the authoritative behavior contract. Subsections expand only where there's a probe-confirmed quirk, deviation from SQL Server, or non-obvious implementation rule that would otherwise burn future-me's time.

### Boolean / set-op / projection / CASE
- **Boolean combinators** (WHERE / MERGE-ON / CHECK): `AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `[NOT] IN (literal, ...)`. Tri-valued. `where (arith_expr) cmp rhs` (parens around arithmetic LHS) doesn't parse here; SQL Server accepts it.
- **Set ops** (UNION / UNION ALL / INTERSECT / EXCEPT): standard precedence (INTERSECT > UNION/EXCEPT). **NULLs are equal during set-op dedup/matching** (opposite of `=`'s tri-state). Per-branch ORDER BY in a non-final branch → Msg 156; top-level ORDER BY references first-branch column names only. `Selection.HasOrderBy` is the parse-time signal that gates Msg 156 in `CombineSetOps`.
- **`SELECT *`**: bare `*` and qualified `<source>.*` both work. Multi-source `*` keeps duplicate column names because expansion qualifies each ref by its source's alias / table name. Unbound `<qualifier>.*` raises Msg 4104. The multiplication `*` is unchanged: only intercepted as star at projection-element-start position.
- **CASE**: searched + simple. UNKNOWN excludes (matches WHERE); simple-form `CASE NULL WHEN NULL` falls through. Result type from `SqlType.Promote` over THEN/ELSE; cached on first `GetSqlType`.
- **`ISNULL` / `IIF` / `NULLIF`**: `ISNULL` result type is fixed to first arg (truncates fallback / can throw on incompatible fallback). `IIF` = sugar for searched CASE. `NULLIF(a, b)` = `CASE WHEN a = b THEN NULL ELSE a END`. EF Core 10 emits `ISNULL` only for `?? <fallback>` *with a CAST involved*; bare `??` emits `COALESCE`. `IIF` / `NULLIF` aren't EF-emitted (LINQ ternary → CASE) — they're load-bearing for `FromSqlInterpolated` / `FromSqlRaw`. The simulator inherits CASE's deviation: doesn't raise Msg 8133 / Msg 4127 for bare-NULL forms.

### JOINs / APPLY
INNER / bare JOIN / LEFT [OUTER] / CROSS / CROSS APPLY / OUTER APPLY. Multi-table chains compose left-to-right. ON-predicate UNKNOWN excludes. APPLY = lateral form (right side re-executed per outer row, no ON clause; EF Core 10 emits `CROSS APPLY` for `SelectMany(a => a.Books.Where(...))`). Always-defer rule for derived tables in FROM is in the Selection section above.

### Subqueries
`EXISTS` / `NOT EXISTS` (multi-column inner allowed); `expr [NOT] IN (SELECT ...)` (single inner column, Msg 116); scalar `(SELECT col FROM ...)` (single column, single-row Msg 512 per outer row, empty → typed NULL). All forms work correlated and non-correlated, arbitrary nesting depth. Inner plan re-executes per outer row (fidelity over performance).

### Pagination (`OFFSET ... FETCH`)
Standard syntax. Quirks worth knowing:
- OFFSET requires ORDER BY (no ORDER BY → Msg 102 generic).
- FETCH alone (no preceding OFFSET) → **Msg 153**.
- Negative offset → **Msg 10742** `"The offset specified in a OFFSET clause may not be negative."` (verbatim "a OFFSET").
- Fetch ≤ 0 → **Msg 10744** `"... must be greater then zero."` (verbatim typo "then").
- TOP + OFFSET on same SELECT → **Msg 10741**.
- Counts resolve at parse time (constants, parameters, arithmetic).

### Aggregates
`COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT)` / `COUNT_BIG`, `SUM` / `AVG`, `MAX` / `MIN`, statistical (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`. `AVG(int)` truncates; `AVG(decimal(p,s))` widens to `decimal(38, max(s,6))`.

`STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY ... [ASC|DESC] [, ...])` reorders concatenation per group. EF Core 10 emits this from `GroupBy(...).Select(g => string.Join(sep, g.OrderBy(x => key).Select(x => field)))`. The aggregator switches to a buffered path when `OrderBy` is set on the parsed `AggregateExpression`; rows stash their value plus the per-row evaluated ordering tuple, then `Result()` sorts via `SqlValue.CompareTo` (NULLs first under ASC, last under DESC) and concatenates. NULL operand rows skip both the ORDER BY input and the output (matching the no-WITHIN-GROUP path). Non-`STRING_AGG` aggregate with `WITHIN GROUP` → **Msg 10757** (`"The function '<lower>' may not have a WITHIN GROUP clause."`); ORDER BY ordinal in this context → **Msg 5308** (distinct from the projection-level ORDER BY which accepts ordinals); WITHIN is parsed as a contextual keyword (not reserved — `select within from t` still works). Cross-aggregate Msg 8711 (mutually-incompatible orderings) isn't modeled — EF Core 10 doesn't emit shapes that hit it.

### Window functions
- `ROW_NUMBER() OVER([PARTITION BY ...] ORDER BY ...)` — bigint, ORDER BY required inside OVER (else Msg 4112). EF Core 10 always wraps ROW_NUMBER in a derived-table subquery (`SELECT ... FROM (SELECT cols, ROW_NUMBER() OVER(...) AS row FROM T) AS sub WHERE sub.row <= N`); the OVER never references outer scope, so the always-defer rule above doesn't bite.
- Aggregate windows: `SUM`/`AVG`/`COUNT`/`COUNT_BIG`/`MIN`/`MAX`/`STDEV*`/`VAR*`/`CHECKSUM_AGG`/`APPROX_COUNT_DISTINCT(<expr>) OVER ([PARTITION BY ...])`. **Implicit-frame whole-partition default only** (no ORDER BY in OVER for aggregates). Result types and NULL semantics match plain aggregates.
- `RANK`/`DENSE_RANK`/analytic family/explicit frames/aggregate-window ORDER BY all parse → `NotSupportedException` (not silent Msg 102) so users get a diagnostic. EF Core 10 doesn't emit any of these from idiomatic LINQ.
- Error gating: `STRING_AGG OVER` → Msg 4113; `COUNT(DISTINCT) OVER` / `SUM(DISTINCT) OVER` → Msg 10759; windowed function in WHERE / HAVING / GROUP BY / ON → Msg 4108 (gated parse-time by `ParserContext.AllowsWindowExpressions`).
- `WindowExpression`'s `WrapAggregate` is invoked from `Expression.Parse`'s binary loop when an aggregate is followed by `OVER`; it pops the aggregate from `AggregateCollector` and registers the window in `WindowCollector`. Combining window + GROUP BY / HAVING in the same SELECT raises `NotSupportedException`.

### Integer ↔ string promotion
Cross-category integer ↔ string lands the integer's specific subtype (`tinyint + '3'` stays tinyint, `bigint + '3'` stays bigint — probe-confirmed 2026-05-08). String parses through the integer's existing CAST path: empty / whitespace → 0, `+`/`-` accepted, leading/trailing whitespace trimmed. **Decimal-shaped strings (`'5.5'`, `'5.0'`) raise Msg 245** rather than routing through decimal — SQL Server's int-target string parse rejects any non-integer literal. Hex (`'0x05'`) likewise rejected.

The bit asymmetry: `bit ↔ string` **comparison** works through string→bit CAST (`'true'` / `'false'` / empty all accepted), but `bit + str` is rejected — `+`/`-`/`%` raise **Msg 402**; `*`/`/` raise **Msg 8117** with the LEFT operand's type only. Mirrors SQL Server's bit-arithmetic-with-bit restriction.

WHERE on a varchar column compared against an int halts the whole query on the first unparseable row — not isolated as per-row UNKNOWN. SQL Server's lazy-IN quirk (unparseable IN-list value suppressed when another matches) isn't modeled; conversion errors propagate immediately.

`BuildSynthesizedSqlRow` (the FROM-less SELECT path) runs each expression first to surface runtime-only errors with operator-name wording, then calls `GetSqlType` to populate schema, then bridges any runtime/schema mismatch via `CoerceTo` — required for mixed-type CASE / Coalesce without a FROM clause.

### Decimal arithmetic precision / scale
SQL Server has per-operator scale rules for decimal that differ from the joint-envelope rule used for non-arithmetic comparison / COALESCE / set-op unification (probe-confirmed 2026-05-08):

- `+` / `-`: `p = max(p1-s1, p2-s2) + max(s1, s2) + 1`, `s = max(s1, s2)`
- `*`: `p = p1 + p2 + 1`, `s = s1 + s2`
- `/`: `s = max(6, s1 + p2 + 1)`, `p = p1 - s1 + s2 + s`
- `%`: `p = min(p1-s1, p2-s2) + max(s1, s2)`, `s = max(s1, s2)`

When precision exceeds 38, scale reduces by the excess down to a floor of `min(originalScale, 6)` and precision clips to 38. The 6-floor stabilizes division (always `s ≥ 6`); for `+ - * %` the floor binds only when original scale was already ≤ 6. `decimal(38,2) * decimal(38,2)` → `decimal(38,4)`; `decimal(38,30) / decimal(38,30)` → `decimal(38,6)`; EF's `decimal(10,2) * 100.0 / decimal(38,2)` → `decimal(38,24)`.

Integer / money operands canonicalize before formulas apply (bit→(1,0)…bigint→(19,0); money→(19,4); smallmoney→(10,4)). Pure integer-pair, pure money-pair, and float-involving arithmetic skip the decimal path (joint-envelope `Promote` instead).

`SqlType.Promote` (joint-envelope, `scale = max(s1, s2); precision = min(38, max(p1-s1, p2-s2) + scale)`) stays the right rule for non-arithmetic uses.

### Math scalar functions
`ABS`, `ROUND` (2- and 3-arg, half-away-from-zero plus truncate mode), `FLOOR`, `CEILING`, `POWER`, `SQRT`, `SIGN`, `LOG` (1- and 2-arg), `EXP`, `LOG10`. EF Core 10 emits all from natural `Math.X` LINQ; `Math.Truncate(x)` → `ROUND(x, 0, 1)`.

**Type-widening rule** (shared across `ABS` / `FLOOR` / `CEILING` / `ROUND` / `SIGN` / `POWER`'s first-arg type — probe-confirmed via `SELECT INTO #t` + `tempdb.information_schema.columns`):
- `tinyint` / `smallint` → `int`
- `smallmoney` → `money`
- `real` / `bit` → `float` (sic — `bit` widens to float, not int, despite being in the integer category)
- `int` / `bigint` / `decimal(p,s)` / `money` / `float` → preserve input

`POWER` returns the (post-widen) type of the *first* argument regardless of exponent type — `POWER(int, float) → int` with truncation toward zero. `SQRT` / `LOG` / `EXP` / `LOG10` always return `float`. `MathScalars` (sibling helper in `Parser/Expressions/`) centralizes the widening rule and the `AsLong` / `AsDouble` / `AsDecimalOrMoney` accessors plus matching writers; each function file dispatches on `resultType.Category`.

Errors: `SQRT(neg)` / `LOG(<= 0)` / `LOG10(<= 0)` / `LOG(x, 1)` / `POWER(neg, frac)` → Msg 3623. `POWER(0, neg)` → Msg 8134. `EXP` overflow → Msg 8115 float. `ABS(int.MinValue)` / `ABS(bigint.MinValue)` → Msg 8115 with the result type's family in the data-type slot (smallint overflow is absorbed by int widening). `POWER` int-result overflow → Msg 232.

**`Math.Sign(decimal)` doesn't work end-to-end against either real SQL Server or the simulator** (probe-confirmed 2026-05-09): the LINQ method's CLR signature returns `int` but EF emits `SELECT SIGN([col])` and SQL Server's `SIGN(decimal)` returns decimal, so the reader-side cast throws. Same failure mode in both; not a fidelity bug. Apps using `Math.Sign` over an int column work fine. (This is the milestone where the simulator started finding upstream bugs.)

### Date scalar functions: `DATEPART` / `DATEADD` / `DATEDIFF` / `DATEDIFF_BIG`
All four take a bare datepart keyword as the first argument. Result types: `DATEPART` → int; `DATEADD` preserves input's SQL type; `DATEDIFF` → int; `DATEDIFF_BIG` → bigint.

**`DATEPART` / `DATEADD`** enforce per-type keyword compatibility: `date` accepts only date parts; `time(N)` only time parts; `datetime` / `smalldatetime` / `datetime2(N)` accept both; `datetimeoffset(N)` adds `tzoffset`. Wrong combination → **Msg 9810**. `DATEADD` overflow → **Msg 517**. `DATEPART(weekday)` uses default `DATEFIRST 7` (Sunday=1); changing `DATEFIRST` isn't modeled. Week / iso_week algorithm pins the default us_english behavior.

**`DATEDIFF` / `DATEDIFF_BIG`** count `datepart`-unit *boundaries crossed*, not elapsed time — `datediff(year, '2023-12-31', '2024-01-01')` = 1. Type compatibility is more permissive than DATEPART/DATEADD: every datepart works against every date/time-family combo (including mixed `date`/`time` pairs anchored to midnight / 1900-01-01). Only `tzoffset` and `iso_week` are rejected unconditionally with **Msg 9806**. String literals implicitly cast to `datetime2(7)`. `datetimeoffset` operands compare via UTC instant. Result-width overflow → **Msg 535** (DATEDIFF hits this on millisecond ranges past ~25 days; DATEDIFF_BIG only on extreme nanosecond ranges).

Unknown keyword → **Msg 155** with the calling function's lowercase name embedded. NULL on any operand → typed NULL. `DatePartKind` (`Parser/Expressions/`) is the shared enum + helpers; `DateDiff` handles both BIG and non-BIG via a single `bool isBig` field.

### Current-time scalar functions: `GETDATE` / `GETUTCDATE` / `SYSDATETIME` / `SYSUTCDATETIME` / `SYSDATETIMEOFFSET` / `CURRENT_TIMESTAMP`
Result types (probe-confirmed 2026-05-09 via `SELECT INTO` + `tempdb.information_schema.columns`): `GETDATE` / `GETUTCDATE` / `CURRENT_TIMESTAMP` → `datetime`; `SYSDATETIME` / `SYSUTCDATETIME` → `datetime2(7)`; `SYSDATETIMEOFFSET` → `datetimeoffset(7)`. EF Core 10 emits these from `DateTime.UtcNow` / `DateTime.Now` / `DateTimeOffset.UtcNow` in server-side LINQ predicates and from `HasDefaultValueSql("getutcdate()")` column defaults.

**Per-statement freeze** (probe-confirmed 2026-05-09): two `SYSDATETIME()` calls in one SELECT return identical values to the 7th decimal digit; an UPDATE that stamps every row with `SYSDATETIME()` writes the same value into all rows; successive SELECTs in one batch DO advance (per-statement, not per-batch). The simulator captures `DateTime.UtcNow` once at the top of `Simulation.CreateResultSetsForCommand`'s loop body into `Simulation.CurrentStatementUtcNow`; every current-time function call within that statement reads the same snapshot. DEFAULT-clause integration falls out for free — each INSERT runs through a fresh statement-loop iteration, so each insert sees its own captured stamp.

**UTC == Local** (Azure SQL Database default behavior): the simulator does no local-time conversion. All six functions return the same UTC instant (rounded per type — datetime variants quantize to 1/300s tick); `SYSDATETIMEOFFSET` reports a `+00:00` offset. Apps that depend on `GETDATE` returning a different value than `GETUTCDATE` won't behave like a real on-prem SQL Server installed in a non-UTC zone, but match the cloud default.

**`CURRENT_TIMESTAMP` is parens-less**: the only zero-arg function in SQL Server's grammar without `()`. The token surfaces as `ReservedKeyword { Keyword: Keyword.Current_Timestamp }`, dispatched directly from `Expression.Parse`'s expression-start switch (NOT via `ResolveBuiltIn`, which assumes `()`). `CURRENT_TIMESTAMP()` with parens raises **Msg 102** in SQL Server (probe-confirmed); the simulator inherits Msg 102 from the surrounding parser catching the unexpected `(`, though the "near X" snippet differs. `Selection.cs`'s projection-element start switch lists `Current_Timestamp` alongside `LEFT` / `RIGHT` / `CASE` etc. as the reserved-keyword exemption set.

`CurrentTimeFunction` (`Parser/Expressions/`) is a single class with a `CurrentTimeKind` discriminator; result-type rules and the SqlValue construction live in one place. The class holds a `Simulation` reference like `LastIdentityExpression` does, and reads `CurrentStatementUtcNow` per `Run` call.

### Variadic string concatenation: `CONCAT` / `CONCAT_WS`
Both functions stringify each argument via the standard CAST-to-varchar/nvarchar path, **skip NULL arguments** (rather than propagating), and **never return NULL** — an all-NULL input returns `''`, NOT NULL. Result type is `nvarchar` if any argument has a national-string type (`nvarchar` / `nchar` / `ntext`); otherwise `varchar`. Probe-confirmed 2026-05-09.

Argument-count rules raise **Msg 189** with lowercase function name and per-function minimum: `CONCAT` requires 2-254 args (`"The concat function requires 2 to 254 arguments."`); `CONCAT_WS` requires 3-254 args — separator + at least two values (`"The concat_ws function requires 3 to 254 arguments."`).

`CONCAT_WS` quirks worth knowing:
- **NULL separator silently degrades to empty string** — `concat_ws(NULL, 'a', 'b')` returns `'ab'`, not NULL. Probe-confirmed; doesn't match common documentation that asserts NULL propagation.
- **NULL values are skipped entirely** — no double separator next to a missing value: `concat_ws(',', 'a', NULL, 'b')` → `'a,b'`, not `'a,,b'`.
- **Single-value form errors** — `concat_ws(',', 'a')` raises Msg 189; the function refuses to act as a no-op stringifier.

The result type is computed from runtime argument types in `Run` (not just from `GetSqlType`'s static cache) because the function is reachable when its outer wrapper's `GetSqlType` doesn't cascade — e.g. `select datalength(concat(N'a', N'b'))` runs `Concat.Run` without `Concat.GetSqlType` being called, and the inner function still needs to settle on nvarchar based on the actual operand types.

**EF Core 10 doesn't emit `CONCAT` from `string.Concat`** — `string.Concat(a, b, c)` translates to `[a] + N'-' + [b] + N'-' + [c]` (the `+` operator), which is NULL-propagating, distinct from CONCAT's NULL-skipping semantics. The simulator's CONCAT/CONCAT_WS support is reachable from raw SQL (`FromSqlInterpolated` / direct command text); the LINQ-side `string.Concat` path goes through string `+` (covered immediately below).

`StringConcat` (`Parser/Expressions/`) is a single class with a `StringConcatKind` discriminator (`Concat` / `ConcatWs`); both share the same per-arg stringify + NULL-skip + nvarchar-promotion path, with the only branch in `Run` being whether the first argument is consumed as a separator.

### String `+` operator (concatenation)
String concatenation via `+` is **NULL-propagating** (matches default `CONCAT_NULL_YIELDS_NULL ON`; the simulator doesn't model the OFF setting). Result type is `nvarchar` when either operand is a national-string type (`nvarchar` / `nchar` / `ntext`), otherwise `varchar`. EF Core 10 emits this from `string.Concat(a, b, c)` server-side, from `+`-chains in LINQ, and as the dominant string-concat path for application code.

`text` / `ntext` / `image` / `varbinary` operands raise **Msg 402** (`"The data types {a} and {b} are incompatible in the add operator."`) matching SQL Server's restriction on LOB-string and binary types in arithmetic operators. Trailing-space preservation falls out for free: fixed-length `char(N)` storage carries its padding, so `cast('a' as char(5)) + cast('b' as char(5))` → `'a    b    '`.

**Bare-NULL handling has a minor divergence**: simulator's untyped `NULL` literal carries `SqlType.Int32` (the simulator has no truly untyped NULL sentinel), so `'a' + NULL` and `'a' + cast(NULL as int)` are indistinguishable at runtime. The simulator treats both as string concatenation (returning NULL of the result string type), which matches real SQL Server's behavior on bare NULL but diverges from `cast(NULL as int) + 'a'` (real raises Msg 245 from a string-to-int parse). The bare-NULL case dominates in practice; the typed-null-int edge case is a rare hand-written-SQL shape EF Core never emits.

**Result-type fidelity**: pure char/nchar pairs preserve fixed-length-ness with combined lengths capped at the type's max — `char(5) + char(5)` → `char(10)`, `nchar(5) + nchar(5)` → `nchar(10)`, `char(5) + nchar(5)` → `nchar(10)`, `char(8000) + char(100)` → `char(8000)`. Variable-length pairs and mixed fixed/variable pairs land on a length-bearing `varchar(N+M)` / `nvarchar(N+M)` (capped at 8000 / 4000) — `varchar(10) + varchar(20)` → `varchar(30)`; `char(5) + varchar(10)` → `varchar(15)`; `nvarchar(3000) + nvarchar(2000)` → `nvarchar(4000)`. National-string family wins on any mixed pair. LOB-family operands (text / ntext) and unspecified-length operands fall back to the unspecified form of the result family (since the missing operand's length can't be summed). `PromoteForArithmetic`'s `StringConcatResult` helper centralizes the rule; `VarcharSqlType` / `NVarcharSqlType` / `VarbinarySqlType` are length-bearing singletons via `Get(N)` (see the CAST section below).

`Subtract` / `Multiply` / etc. on string operands still raise `NotSupportedException` (real SQL Server: Msg 402 for `-`, Msg 8117 for `*` / `/` / `%`). Not a fidelity priority — apps don't use those shapes intentionally.

### Date-construction scalar functions: the `*FROMPARTS` family + `EOMONTH`
All six builders — `DATEFROMPARTS`, `DATETIMEFROMPARTS`, `DATETIME2FROMPARTS`, `DATETIMEOFFSETFROMPARTS`, `SMALLDATETIMEFROMPARTS`, `TIMEFROMPARTS` — share one runtime path: NULL on any non-precision argument propagates to NULL; non-int operands coerce through the existing CAST machinery (decimal / string / bigint inputs all accepted, probe-confirmed against SQL Server 2025 on 2026-05-09); out-of-range values raise **Msg 289** with the type-specific State number (1=date, 2=time, 3=datetime, 5=datetime2, 6=datetimeoffset) and verbatim text `"Cannot construct data type {type}, some of the arguments have values which are not valid."`

The variable-precision builders (`datetime2` / `datetimeoffset` / `time`) accept the precision arg as the last position and require it to be an integer constant or constant expression. The simulator extracts it at parse time by evaluating the parsed sub-expression with a NULL-returning column resolver — so literal `1+2` folds to `3`, but a column reference degrades to NULL and surfaces as **Msg 10760** (`"Scale argument is not valid. Valid expressions for data type {type} scale argument are integer constants and integer constant expressions."`). Out-of-`[0, 7]` precision raises **Msg 1002** with the standard `"Specified scale {N} is invalid."` wording. Result type carries the captured precision: `DATETIME2FROMPARTS(..., 3)` → `datetime2(3)`.

`DATETIMEFROMPARTS` ms 999 with hour 23 / minute 59 / second 59 rolls to the next day via legacy `datetime`'s 1/300s tick rounding (probe-confirmed). `DATETIMEOFFSETFROMPARTS` enforces sign-consistency between `hour_offset` and `minute_offset` (mixed signs raise Msg 289 State 6) and a |offset| ≤ 14:00 cap.

`EOMONTH(start_date [, month_offset])` always returns `date` regardless of input type — `date` / `datetime` / `datetime2` / `datetimeoffset` / `smalldatetime` / string-literal inputs all surface as date in the output. **Quirk** worth knowing: a NULL `month_offset` is silently treated as zero (no shift), unlike NULL `start_date` which propagates. Probe-confirmed against SQL Server 2025.

`DatePartsBuilder` (`Parser/Expressions/`) is a single class with a `DatePartsBuilderKind` discriminator covering all six builders; `EOMonth` lives in its own file because its argument shape (date input + optional int offset) is structurally different from the parts-list pattern.

### `AT TIME ZONE`
Postfix operator that converts the LHS to `datetimeoffset` in the supplied zone. Two semantics, distinguished by LHS type (probe-confirmed against SQL Server 2025 on 2026-05-09):

- **`datetime2 / datetime / smalldatetime AT TIME ZONE 'X'`**: treats the LHS wall-clock as already in zone X and attaches X's offset for that wall-clock. Skipped (spring-forward) wall-clocks shift forward by the DST delta and stamp the post-transition daylight offset; ambiguous (fall-back) wall-clocks pick the daylight (pre-fall-back) offset.
- **`datetimeoffset AT TIME ZONE 'X'`**: preserves the UTC instant and re-expresses it in zone X — both offset and wall-clock change to match.

Result type is always `datetimeoffset` with the LHS's fractional precision preserved: `datetime2(N)` / `datetimeoffset(N)` → `datetimeoffset(N)`; legacy `datetime` / `smalldatetime` → `datetimeoffset(3)`. **`date` and `time` LHS raise Msg 8116** (`"Argument data type {type} is invalid for argument 1 of AT TIME ZONE function."`). Unrecognized zone names raise **Msg 9820** (`"The time zone parameter '{name}' provided to AT TIME ZONE clause is invalid."`). NULL on either side propagates to NULL of the result type.

Zone-name resolution routes through .NET 6+'s `TimeZoneInfo.FindSystemTimeZoneById`, which accepts both Windows-style identifiers (`'Pacific Standard Time'`) and IANA names (`'America/Los_Angeles'`) cross-platform via ICU. Lookups are cached in a process-static `ConcurrentDictionary` to keep per-row overhead at a hashtable lookup.

**Precedence**: `AT TIME ZONE` binds tighter than `+` (probe-confirmed: `expr AT TIME ZONE 'UT' + 'C'` raises Msg 402 because real SQL Server parses it as `(expr AT TIME ZONE 'UT') + 'C'`, not `expr AT TIME ZONE 'UTC'`). The simulator models this by parsing the zone-name slot as a primary expression only — literals, `@variables`, single-segment column refs, or parenthesized full expressions. Multi-part dotted column refs and binary-operator chains in the zone-name slot aren't modeled; wrap in parens (`AT TIME ZONE (a + b)`) for those.

`AT`, `TIME`, and `ZONE` are contextual keywords (added to `ContextualKeyword`) — they remain valid identifiers everywhere else, so existing `create table t (Time int, Zone int)` shapes still work.

### CAST/CONVERT to narrow `varchar` / `nvarchar` / `varbinary`
The simulator's variable-length string types are stateless singletons (no length on the SqlType — that lives separately on `HeapColumn.MaxLength` for storage and on the parsed `targetMaxLength` for CAST). `Cast.EnforceTargetMaxLength` runs after `SqlValue.CoerceTo` and applies the per-source-category rule (probe-confirmed against SQL Server 2025 on 2026-05-09):

- **String / varbinary / date-time-family source** → silent truncation. `CAST('hello world' AS varchar(5))` → `'hello'`; `CAST(date AS varchar(9))` → `'2026-05-0'`; `CAST(0x0102030405 AS varbinary(3))` → `0x010203`.
- **`tinyint` / `smallint` / `int` source → `varchar`** too narrow → asterisk fallback (`'*'`). Legacy SQL Server quirk specific to the `varchar` target; the `nvarchar` path raises Msg 8115 instead. `bigint` doesn't get the fallback either — also Msg 8115.
- **`decimal` / `numeric` source** → Msg 8115 with the "numeric" wording (`Cast.ArithmeticOverflowToTarget`), distinct from the integer/bigint variant's "expression" wording.
- **`money` / `smallmoney` source** → Msg 234 with its dedicated `"There is insufficient result space to convert a money value to <target>."` wording (the message says "money" regardless of source variant).
- **`float` / `real` source** → Msg 232 with the formatted source value embedded (`F6` formatting; matches the `POWER`-overflow path's existing factory).
- **`uniqueidentifier`** has its own pre-CoerceTo branch (Msg 8170 for char/varchar, Msg 8115 for nchar/nvarchar) — unchanged.

`datetimeoffset → varchar` too narrow raises **Msg 241** in real SQL Server but isn't modeled — the simulator silently truncates the rendered string. Niche enough that no app I know of relies on it.

CAST/CONVERT context **defaults missing length to 30** for `varchar` / `nvarchar` / `varbinary` (column-context default is 1) — same two-context rule already in place for `char(N)` / `nchar(N)` / `binary(N)`. So `CAST('hello' AS varchar)` returns the full 5 characters, not `'h'`.

Fixed-length char(N) / nchar(N) / binary(N) targets carry their length on the SqlType and normalize through `FromChar` / `FromNChar` / `FromBinary` (right-pad-or-truncate); their `targetMaxLength` arrives as `null` and short-circuits `EnforceTargetMaxLength`.

`VarcharSqlType` / `NVarcharSqlType` / `VarbinarySqlType` are now per-length singletons too (parallel to `CharSqlType`'s long-standing model). `Get(N)` returns a length-bearing instance; `Unspecified` (length 0) is the sentinel for paths that haven't pinned a length (e.g. runtime `SqlValue.FromVarchar(string)` results); `MaxForm` (length -1) is the LOB `varchar(MAX)` form. `SqlType.Varchar` / `SqlType.NVarchar` / `SqlType.Varbinary` static fields point at the `Unspecified` instances. **Equality semantics**: `value.Type == SqlType.Varchar` is true only for the unspecified form — code meaning "is any varchar" must use `is VarcharSqlType` instead. The `RowEncoder` accepts any same-family pair regardless of length (the schema's declared cap and the runtime value's unspecified-length form are intentionally redundant; write-time truncation is enforced upstream by `Simulation.EnforceMaxLength` and `Cast.EnforceTargetMaxLength`).

### `TRY_CAST` / `TRY_CONVERT`
Both wrap the regular CAST / CONVERT runtime path in a try/catch that swallows the documented "conversion failed" error numbers (returning `SqlValue.Null(targetType)`) while letting structural errors propagate. Probe-confirmed against SQL Server 2025 on 2026-05-09.

`Cast.IsConversionFailure` is the shared swallow-set (single source of truth, reused by `ConvertExpression` for the TRY_CONVERT path): **241** (datetime-from-string parse), **242** (datetime out-of-range), **244** (tinyint/smallint INT1/INT2 overflow), **245** (string→numeric parse), **248** (int overflow), **295** (smalldatetime parse), **8114** (decimal/etc. conversion), **8115** (generic arithmetic overflow), **8169** (uniqueidentifier-from-string parse), **8170** (uniqueidentifier→too-narrow-string).

Errors NOT swallowed (structural / programming errors): **Msg 529** (explicit-cast disallowed pair like `int → date`, `text → int`), **Msg 243** (unknown target type), and any source-evaluation error that fires before the cast itself runs (e.g. an inner `CAST('abc' AS INT)` that raises Msg 245 — the wrapping `TRY_CAST(... AS BIGINT)` does NOT swallow it because the failure isn't at the outer cast level). Probe-confirmed: `TRY_CAST(1/0 AS INT)` raises Msg 8134 in real SQL Server (the simulator surfaces a raw `DivideByZeroException` here — pre-existing fidelity gap orthogonal to TRY_CAST).

String-source truncation isn't a "conversion failure" path either way — `TRY_CAST('hello' AS varchar(3))` returns `'hel'`, mirroring CAST's silent truncation rule (see the section above).

EF Core 10 doesn't emit `TRY_CAST` / `TRY_CONVERT` from idiomatic LINQ — these are reachable from raw SQL only (`FromSqlInterpolated` / `FromSqlRaw` / direct command text), like `CONCAT` / `CONCAT_WS`.

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree.

### Transactions
Three entry points share one per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()` / `Commit()` / `Rollback()`), and SQL-text (`BEGIN` / `COMMIT` / `ROLLBACK` / `SAVE TRANSACTION`). Probe-confirmed 2026-05-08.

- **Statement-level atomicity**: a single mutation that throws mid-execution rolls back its partial writes. A multi-row INSERT whose third row violates a constraint leaves zero rows behind. A failed statement inside an explicit tx leaves the surrounding tx alive (per-statement marker, only this-statement entries unwound).
- **Explicit txs**: `BEGIN TRAN` increments `TranCount` when nested; only outermost `COMMIT` actually commits; `ROLLBACK` zeroes `TranCount` and walks the entire log regardless of depth. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the path EF Core 10 emits per SaveChanges call inside an explicit tx. Parallel `BeginTransaction` raises `InvalidOperationException` ("SqlConnection does not support parallel transactions.") matching SqlClient. `COMMIT` / `ROLLBACK` with no active tx → Msg 3902 / Msg 3903.
- `@@TRANCOUNT` reads the connection's depth as int.
- Identity counters and the database-scoped rowversion counter **bypass the log** — both keep advancing when their consuming inserts are rolled back. LOB chains for rolled-back inserts also bypass; they leak the same way committed deletes leak.
- No isolation: uncommitted writes are immediately visible to all readers (single-Simulation, single-thread-at-a-time assumption).
- `Storage/UndoLog.cs` is row-level LIFO with `Position` / `RollbackTo(position)` markers. `Simulation.RunMutation` wraps each mutation: route to active-tx log when one exists, else fresh per-statement log; capture marker; on exception `RollbackTo` before re-raising.

### UPDATE / DELETE
- Bare `UPDATE table SET ... [WHERE]` and `DELETE [FROM] table [WHERE]`.
- Multi-table-syntax (`UPDATE alias SET ... FROM <sources> [WHERE]`, `DELETE FROM alias FROM <sources> [WHERE]`) is the EF7+ `ExecuteUpdate` / `ExecuteDelete` shape. Joined-source forms route through the SELECT-side `ParseSourcesAndJoins` + `EnumerateJoinedRows`. Target source is identified by matching the leading-identifier (alias OR table name) against each source's `FromSource.Qualifier`; missing match → **Msg 208** (distinct from Msg 4104). SET RHS can reference any source's columns.
- **Joined UPDATE / DELETE: each unique target row processed exactly once.** When the same target matches multiple join tuples, SQL Server applies the SET / DELETE once per unique target — using the *first* matching tuple's RHS values for SET (heap-scan order, probe-confirmed). The simulator dedupes by `(page, slot)` of the target heap row, recovered via a side-channel byte[]→address map. A wrapper around the target source's `Rows` enumerator records each yielded byte[]'s address; for inner-side targets, the wrapper repopulates the map on each restart. LEFT JOIN with no right-side match still surfaces the target tuple; RHS sees NULL for unmatched-source columns.
- **OUTPUT** is supported only when the leading identifier resolves to a real table name up-front; OUTPUT alongside an alias-form multi-source UPDATE / DELETE raises `NotSupportedException` (EF Core 10 doesn't combine those).
- **Multi-column SET evaluates RHS against the pre-update row snapshot** — `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` → `(a=100, b=11)`. Scalar subquery RHS sees pre-update table state.
- Identity update → Msg 8102. Computed update → Msg 271. Rowversion update → Msg 272. Per-row constraint re-validation: NOT NULL → Msg 515 ("UPDATE fails."); CHECK → Msg 547 ("UPDATE statement"); PK / UNIQUE → Msg 2627 (verbatim "Cannot insert duplicate key" wording even on UPDATE — SQL Server quirk).
- Two-phase: phase 1 picks affected rows + computes new values + per-row validation; phase 2 validates PK / UNIQUE against the post-update virtual state; phase 3 mutates (tombstone old, insert new).
- `OUTPUT INSERTED.<col>` (post-update) / `DELETED.<col>` (pre-update) supported. UPDATE allows both qualifiers; DELETE rejects `INSERTED.<col>` at parse → Msg 4104. Star expansion (`INSERTED.*` / `DELETED.*`) and `OUTPUT INTO @table_var` aren't modeled.

### `rowversion` (legacy synonym `timestamp`)
8-byte big-endian database-scoped monotonic counter; `Simulation.AllocateRowVersion` advances on every INSERT into a rowversion-bearing table and every UPDATE that affects a row in one. Storage type name surfaces as `timestamp` in `information_schema` regardless of declaration keyword. Explicit insert → Msg 273; explicit update → Msg 272; second column on a table → Msg 2738. Outbound CAST: `varbinary(N)` / `binary(N)` copy the 8 bytes; `bigint` reads big-endian. `Promote(RowVersion, Varbinary)` → `Varbinary` so EF Core's `WHERE [rv] = @originalRv` optimistic-concurrency parameter works directly. `EF Core [Timestamp]` SaveChanges round-trips end-to-end via `UPDATE ... OUTPUT INSERTED.[RowVersion] WHERE [Id] = @p AND [RowVersion] = @originalRv`.

### Common table expressions
`WITH name [(col, …)] AS (SELECT …) [, …] {SELECT|INSERT|UPDATE|DELETE|MERGE} …`. The WITH prefix scopes to exactly one immediately-following statement; the binding is gone after that statement runs. Both non-recursive and recursive forms are modeled. Probe-confirmed against SQL Server 2025 on 2026-05-10 / 2026-05-11.

`Simulation.ParseCteBindings` runs at the statement-loop top before dispatch, populating `ParserContext.CteBindings` (a `Dictionary<string, CteBinding>`). Each body parses branch-by-branch via `Selection.ParseIntersectChain` (the higher-precedence set-op chain parser); the surrounding loop walks UNION / UNION ALL / EXCEPT operators and tracks per-branch self-reference. Bindings are cleared at the top of the next statement-loop iteration. `ParseSingleFromSource` consults the bindings before falling through to `Simulation.HeapTables` — a CTE name shadows a real table for the prefixed statement (probe-confirmed). Multiple comma-separated CTEs cascade: later ones see earlier ones because the prior bindings are already in the dictionary when the later body is parsed.

**Non-recursive form**: when the body has no self-references, branches are folded via the standard `Selection.CombineSetOps` (so type-promotion across UNION / UNION ALL / EXCEPT matches a regular set-op chain). Resolution builds a `FromSource` with `lateralPlan: binding.Plan`, so each FROM-side reference re-runs the inner Selection (parallel to derived tables in FROM).

**Recursive form**: when any branch self-references, the body parser splits branches into anchor (no self-ref) and recursive (one self-ref) lists, then builds a Selection via `Selection.FromRecursiveCte(anchors, recursives, binding)`. The recursive Selection runs anchors once into a seed rowset, then iterates: each iteration rebinds `binding.CurrentIterationRows` to the previous iteration's output and runs every recursive branch, accumulating all rows. Termination: an iteration produces zero rows, OR `binding.MaxRecursion` (default 100, override via `OPTION (MAXRECURSION N)` at the end of the outer SELECT) is exceeded → **Msg 530** with the literal limit value in the message. `OPTION (MAXRECURSION 0)` disables the cap.

Self-reference resolution: during the recursive-part parse, `binding.IsRecursivePartParse` is set after the anchor branch completes (which captures the binding's `Schema` / `ColumnNames` from the anchor's projection). Subsequent branch parses route self-references through a FromSource backed by `SelfReferenceRows(binding)` — a closure that reads `binding.CurrentIterationRows` at iterator-start time. Each branch's `binding.SelfReferenceCountInCurrentBranch` is reset before parse; the body parser inspects the count after parse to classify the branch as anchor (count = 0) or recursive (count = 1) and to enforce one-self-ref-per-branch (Msg 253).

**Recursive CTE errors modeled:**
- **Msg 240**: anchor and recursive parts produce different per-column types. Recursive CTEs require strict type-equality (no Promote-style widening, unlike regular UNION ALL); the user must explicitly cast.
- **Msg 247**: an anchor branch (no self-ref) appears after a recursive branch — anchors must precede recursives.
- **Msg 252**: the body has a self-reference but no top-level UNION ALL splitting it from an anchor (e.g. `WITH c AS (SELECT n+1 FROM c WHERE n < 5) …` — no anchor); also fires when UNION-without-ALL is used between branches.
- **Msg 253**: one recursive branch references the CTE more than once (e.g. `c CROSS JOIN c`).
- **Msg 530**: MAXRECURSION exceeded; the literal limit value appears in the message.

**Recursive part restrictions not enforced** (pre-existing CLAUDE.md gap, deferred to a follow-up bundle): DISTINCT / TOP / OFFSET / aggregate / GROUP BY / OUTER JOIN inside a recursive branch should raise Msg 460/461/467/462 respectively in real SQL Server. The simulator silently accepts those and produces possibly-incorrect semantics; users hitting these constructs in recursive parts are writing SQL that real SQL Server rejects anyway. Recursive references inside subqueries (Msg 465) likewise aren't enforced.

**Non-recursive errors modeled:** **Msg 239** duplicate CTE name; **Msg 8158** / **Msg 8159** rename-list count mismatch; **Msg 1033** ORDER BY in CTE body without TOP / OFFSET / FETCH (gated via `Selection.HasTopOrOffsetOrFetch` threaded through all `new Selection(...)` call sites — set-op combiner inherits from branches; top-level ORDER-BY wrapper inherits from the inner plan plus its own OFFSET / FETCH).

**Msg 319** (WITH after a non-terminated previous statement) isn't enforced — the simulator's statement loop consumes one statement per iteration and treats WITH at iteration top as a fresh prefix regardless of prior `;`; apps that idiomatically terminate statements won't notice.

`OPTION (MAXRECURSION N)` parses inside `Selection.ParseQueryExpression` after the optional ORDER BY / OFFSET / FETCH; the parser walks `context.CteBindings.Values` and writes `MaxRecursion` to each, so recursive Selections see the override at execute time. `MAXRECURSION` is the only OPTION hint modeled; other hints (`OPTIMIZE FOR`, `RECOMPILE`, `MERGE JOIN`, etc.) raise `NotSupportedException`.

EF Core 10 emits non-recursive CTEs in some shapes (TPC inheritance, certain `Distinct/OrderBy/Skip` patterns); recursive CTEs are reachable only via raw SQL (`FromSqlInterpolated` / direct command text) — EF Core's LINQ surface doesn't compile to recursive CTEs from idiomatic queries.

### INSERT … SELECT
`INSERT [INTO] target [(cols)] SELECT …` accepts the same Selection grammar as a top-level SELECT — WHERE / JOIN / GROUP BY / aggregates / ORDER BY / TOP / OFFSET-FETCH / UNION / INTERSECT / EXCEPT all work on the source side. Probe-confirmed against SQL Server 2025 on 2026-05-10.

Source-kind dispatch happens after the OUTPUT-clause parse: a `Values`-keyword token routes to the existing tuple-parsing path; a `Select`-keyword token routes to `Selection.Parse(…).Execute()`. Both paths funnel into one shared per-row encode loop that handles defaults / identity / rowversion / computed / constraints / OUTPUT — VALUES eagerly evaluates each cell expression to a `SqlValue` upstream, SELECT-source rows arrive pre-decoded via `RowDecoder` from the executed `SimulatedSqlResultSet`'s row bytes.

Buffering is full: `ExecuteSelectSource` materializes the entire source result-set into `List<SqlValue[]>` before any destination write. This makes self-insert (`INSERT t SELECT … FROM t`) safe — without it, scanning the source's heap while inserting into it would yield undefined behavior.

Projection-count vs insert-list mismatch fires at parse time via `selection.Schema.Length`: too few SELECT columns → **Msg 120 St 1 Cls 15** (`"The select list for the INSERT statement contains fewer items than the insert list. The number of SELECT values must match the number of INSERT columns."`); too many → **Msg 121 St 1 Cls 15** with `"more items"` wording. Empty source → silent success with rows-affected 0. CHECK / NOT NULL / PK / UNIQUE violations mid-source still trigger statement-level rollback (every row from the SELECT is unwound, matching the VALUES path's atomicity).

EF Core 10 doesn't emit `INSERT … SELECT` from SaveChanges (which uses INSERT…OUTPUT VALUES for single-row and MERGE for batched-multi-row); this is reachable from raw SQL (`FromSqlInterpolated` / direct command text) and from application-side bulk-copy patterns. CTE-prefix INSERTs (`WITH … INSERT t SELECT …`) aren't modeled — orthogonal to this bundle since the simulator has no CTE support.

### JSON support: `JSON_VALUE` / `JSON_MODIFY` / `OPENJSON`
Three pieces unlock EF Core 10's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection (`List<int>` / `List<string>` etc.) emissions. JSON columns are plain `nvarchar(max)` — no special storage type. Probe-confirmed against SQL Server 2025 on 2026-05-10.

`JSON_VALUE(json, path)` — scalar function returning `nvarchar`. Lax mode (default and EF Core's only emitted form): missing path / non-scalar match → SQL NULL. `strict $.foo` raises **Msg 13608** on miss. NULL `json` or NULL path → NULL. JSON booleans render as lowercase `'true'` / `'false'`; JSON numbers return raw text via `JsonElement.GetRawText` (e.g. `'42'`, `'1.5'`). Object / array matches return NULL in lax (the documented scalar-only restriction); strict raises but EF Core never depends on it.

`JSON_MODIFY(json, path, newValue)` — scalar function returning `nvarchar`. Replaces the value at `path` with `newValue`. EF Core 10 emits `'strict $.City'`-shape paths from owned-as-JSON partial updates; the strict prefix is honored (missing leaf → Msg 13608). Bare `'$'` replaces the entire document. Lax-mode existing-key + NULL value removes the key; lax-mode missing key + non-NULL value adds it. Numeric / boolean `newValue` arguments stay JSON-typed (`{"n":42}` not `{"n":"42"}`); System.Text.Json `JsonValue.Create` handles primitive→JSON-text dispatch.

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning function, structurally a new FromSource kind. Implemented via a `Selection.FromOpenJson(...)` factory (parallel to derived tables / CTEs / VALUES) so the existing alias / qualifier / lateral re-execution machinery transparently covers it. Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null / 1=string / 2=number / 3=true-or-false / 4=array / 5=object. With WITH: each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use the `'$'` self-reference shape. `AS JSON` modifier raises `NotSupportedException` (EF Core 10 doesn't emit it). NULL JSON / invalid JSON → zero rows under lax mode (matches EF's tolerance). For arrays: one row per element with `key` = decimal index. For objects: one row per property with `key` = property name.

Dispatch: `JSON_VALUE` / `JSON_MODIFY` route through `Expression.cs:ResolveBuiltIn`'s length-keyed switch (10 / 11). `OPENJSON` is dispatched at `ParseSingleFromSource`'s `case Name tableName:` head — case-insensitive name match on `"OPENJSON"` wins over CTE / table lookup, parallel to how SQL Server reserves the function name. The path-string parser (`Parser/JsonPath.cs`) is shared between the three: a tiny grammar (`['lax'|'strict']? '$' (segment)*`) backing both runtime walks (via `JsonElement` for read paths, `JsonNode` for modify paths) and parse-time per-column path resolution in OPENJSON's WITH clause. Quoted-property escape `""` → literal `"` matches SQL Server (this is what EF Core's `'{"":"X"}'` + `$.""` parameter-wrap shape relies on).

OPENJSON WITH-clause type subset: `int` / `bigint` / `decimal(p,s)` / `float` / `bit` / `nvarchar(N|max)` / `varchar(N)` / `date` / `datetime2(N)` / `datetimeoffset(N)` / `uniqueidentifier`. Coercion from JSON scalar text routes through `SqlValue.CoerceTo` (the existing CAST machinery). JSON `null` element → SQL NULL of the column's type. Backed by `System.Text.Json` (runtime-shipped, no NuGet dependency added).

EF Core 10 emissions covered:
- `Where(c => c.Address.City == "X")` → `JSON_VALUE([c].[Address], '$.City')`
- `Where(c => c.Tags.Contains("x"))` → `OPENJSON(...) WITH ([value] nvarchar(max) '$')` inside `IN(SELECT)`
- `c.Scores.Count` → `(SELECT COUNT(*) FROM OPENJSON(...))`
- `c.Scores.Any(s => s > 15)` → `EXISTS (SELECT 1 FROM OPENJSON(...) WITH ([value] int '$') WHERE …)`
- Owned-as-JSON partial UPDATE → `SET [Address] = JSON_MODIFY([Address], 'strict $.City', JSON_VALUE(@p0, '$.""'))`

Not emitted by EF Core 10 (and not modeled): `JSON_QUERY` (object/array extraction; whole-owned-object reads use raw column + client-side deserialization), `ISJSON`, `FOR JSON PATH`/`AUTO`. Reachable from raw SQL only via `FromSqlInterpolated` / direct command text.

### MERGE / OUTPUT (EF Core SaveChanges shape only)
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch).
- `WHEN MATCHED` parses but throws `NotSupportedException` if its predicate ever evaluates true.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers the seven SqlParameter-downcast pairs: `DateOnly → date`, `DateTime → date`, `DateTime → smalldatetime`, `TimeOnly → time(N)`, `TimeSpan → time(N)`, `decimal → money`, `decimal → smallmoney`. Without the adapter, those mappings throw at SaveChanges. The MAX-string family flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY / DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) are enforced.

### `SimulatedDbDataReader`
Full `DbDataReader` contract. Typed accessors read `SqlValue` directly via the cursor's indexer and unwrap via `As*` (no boxing); NULL on a typed accessor → `SqlNullValueException` matching SqlClient. `GetDateTime` covers `Date` / `DateTime` / `SmallDateTime` / `DateTime2` (Date surfaces at midnight, `Kind=Unspecified`); `GetDecimal` covers `Decimal` / `Numeric` / `Money` / `SmallMoney`; `GetFieldValue<T>` short-circuits EF Core's `DateOnly`-over-`Date` and `TimeOnly`-over-`Time`. `GetOrdinal(name)` is a two-pass linear scan (case-sensitive then case-insensitive — SqlClient's documented match precedence). `HasRows` is a sticky bit; `SqlValueCursor` peek-and-buffers since the source has no token discriminator. `GetChar(int)` always raises `InvalidCastException` (matches SqlClient).

---

## Not modeled

- Locks / MVCC / isolation levels — single-Simulation, single-thread-at-a-time assumption. `BEGIN DISTRIBUTED TRANSACTION`, `BEGIN TRANSACTION ... WITH MARK`, `XACT_ABORT`, `SET TRANSACTION ISOLATION LEVEL` not parsed.
- `RIGHT JOIN` (rewrite as LEFT with sources swapped); `FULL OUTER JOIN`. Both raise `NotSupportedException` at parse.
- Comma-separated FROM (legacy ANSI-89 join syntax).
- `ANY` / `SOME` / `ALL` quantifiers.
- `UNION` / `UNION ALL` inside a subquery body.
- Row-constructor `IN ((1,2), (3,4))`.
- Window functions other than `ROW_NUMBER` and the aggregate-OVER family (see "Window functions" above).
- Recursive-part feature restrictions (Msg 460 DISTINCT / Msg 461 TOP / Msg 462 OUTER JOIN / Msg 467 aggregate-or-GROUP-BY / Msg 465 ref-in-subquery) — the recursive iteration accepts these constructs and produces possibly-incorrect semantics rather than raising. Apps that exercise these in real SQL Server hit the rejection there too, so the simulator's behavior diverges from real-server errors but matches the broader "don't write that" guidance. Recursive CTEs themselves and the structural error paths (Msg 240 / 247 / 252 / 253 / 530) are modeled.
- `LIKE` with `COLLATE` override (default collation only — case-insensitive Latin1_General-shaped).
- `CONVERT` / `TRY_CONVERT` style codes other than `0` / `120` / `121` for date-like → string.
- `LEN(ntext)` raising Msg 8116 (function-level text/ntext/image restrictions); legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- `OUTPUT INTO @table_var`, `OUTPUT DELETED.*` / `INSERTED.*` star expansion.
- MERGE source subqueries; MERGE target column refs in `ON`; `WHEN MATCHED` UPDATE/DELETE branches; `$action`. EF Core's batched-update path emits semicolon-separated `UPDATE … OUTPUT …` instead.
- Msg 8141 (inline CHECK referencing a peer column — SQL Server rejects at CREATE TABLE; simulator allows).
- Msg 8133 (CASE where every branch is bare `NULL`; simulator returns NULL of `int`).
- `PRIMARY KEY` / `UNIQUE` on a computed column (`NotSupportedException`).
- Heap allocation tracking: flat page list, no IAM/PFS.
- Per-connection session state for some scopes: `SCOPE_IDENTITY()` / `@@IDENTITY`, `SET IDENTITY_INSERT`'s active table, `DBCC TRACEON(N)` flags all live on `Simulation` rather than the connection. (Transaction state is already per-connection.)
- `hierarchyid`, `geography`, `geometry`.

---

## Quirks (modeled, not byte-identical to SQL Server)

- `CHECKSUM_AGG`: order-independent XOR fold; semantic guarantee matches (same multiset → same checksum), exact bit pattern won't.
- `APPROX_COUNT_DISTINCT`: implemented as exact `COUNT(DISTINCT)`.
- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15` / `G7` rather than SQL Server's `1e+015`-style scientific.
- Auto-generated PK / UNIQUE / CHECK constraint names: structurally `PK__<table>__<hex>` / `UQ__...` / `CK__<table>__[col__]<hex>`; the 16-hex suffix is a deterministic FNV-1a hash, not SQL Server's object-id-derived hex (stable across runs but won't byte-match a real-server reproduction).
- **DELETE / UPDATE leak page space**: deleted (or UPDATE-relocated) row payload bytes stay in their original page until process exit; only the slot is tombstoned. Slot directory entries also never reused. SQL Server has ghost-cleanup background work; simulator doesn't.
- **DELETE / UPDATE leak LOB chains**: orphaned LOB chains stay in `Heap.LobPages`. Other rows reference LOB pages by stable index, so list compaction would corrupt them.
- **Mass-shift UPDATE on a unique key**: `UPDATE t SET k = k + 1` where `k` is PK / UNIQUE may spuriously raise Msg 2627 — the two-phase validator compares each affected row's new key against other affected rows' new keys, so post-shift values overlapping pre-shift values trigger a false positive. SQL Server uses a temp store that staging-applies all updates before validation. Real EF Core SaveChanges patterns don't hit this.
- **`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Behavior matches per-call observation; the streaming-memory guarantee doesn't.
