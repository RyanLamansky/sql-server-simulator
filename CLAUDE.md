# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. It's an **ADO.NET stand-in for `Microsoft.Data.SqlClient`** — consumers create a `Simulation`, get a `DbConnection` via `CreateDbConnection()`, and use it with (for example) `Microsoft.EntityFrameworkCore.SqlServer` instead of going through SqlClient over the wire. Public surface is `Simulation` + `CreateDbConnection()`; `QualityTests.PublicApiWhitelist` fails the build if anything else leaks public — resist expanding.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter just registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter` (since the simulator's connection isn't a `SqlConnection`).

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server's behavior is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. Current fidelity bar: EF Core trusts the simulator end-to-end. `*.Tests.EFCore` is the regression oracle. Priority is opportunistic — pick the lowest-effort path that unlocks the most application compatibility next.

## Feature-bundle workflow

1. **Probe.** Behavior questions get answered against the real SQL Server 2025 reference instance (connection details in user memory). Probe scaffolds live in `/tmp/<probe-name>/`; deleted after the bundle. Only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`.
2. **Surface decisions.** Before writing code, surface 2–3 concrete design choices and recommend one each.
3. **Implement + test.** `*.Tests` exercises public API; `*.Tests.EFCore` validates the oracle. `*.Tests.Internal` only for things genuinely unreachable from public SQL.
4. **Update CLAUDE.md.** Move bullets between What's-modeled / Not-modeled / Quirks as scope changes.
5. **Single-sentence commit.** Squashes capture end state. Don't run `git commit` — the user holds signing credentials.

Behavioral claims below were probed against a live SQL Server 2025 reference instance unless flagged otherwise.

## Build / test / format

```
dotnet build
dotnet test
dotnet format --verify-no-changes
```

`dotnet format whitespace` (IDE0055 + textual rules) lives outside the analyzer host — CI runs it separately. CI matrix: Debug + Release. If `obj/` permission errors appear, the user's been building outside the dev container; `rm -rf obj/ bin/` clears them.

## Architecture — load-bearing patterns

Layout: `Storage/` (pages, types, row encoder/decoder, heap, constraints), `Parser/` (tokenizer, expressions, query planning + execution), `Simulation/` (per-statement-kind partials), `Errors/` (exception factory partials), root (`Simulated*` ADO.NET types).

### Storage
8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating. Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) flow through a parallel chain of 8KB LOB pages. Bounded `varchar(N)` / `nvarchar(N)` / `varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits within 8060 bytes. Allocation tracking is a flat page list (no IAM/PFS).

### Type system
`SqlType` / `SqlValue` is the storage-layer pair. Three coercion paths: `SqlValue.Coerce` (runtime values), `SqlType.Promote` (static unification for CASE / set ops / COALESCE), `SqlType.PromoteForArithmetic(a, b, op)` (per-operator decimal/integer/money/float result type — the single source of truth for both `TwoSidedExpression.GetSqlType` and `DecimalArithmetic`; static/runtime parity required because the row encoder rejects type mismatches).

### Selection
`Selection.cs` + `Selection.Execution.cs` are a partial-class pair. `Parse → Selection`, `Execute → SimulatedSqlResultSet`. Correlated subqueries re-run the same plan per outer row via `outerResolver: Func<MultiPartName, SqlValue>?` (execute) and `outerTypeResolver: Func<MultiPartName, SqlType>?` (parse). Both walk arbitrary nesting depth via `ParserContext.OuterTypeResolver` + the runtime arg. **Derived tables in FROM are always deferred** (`FromSource.LateralPlan` is re-executed per outer row), matching SQL Server's "any FROM derived table can correlate" rule — required because outer references in WHERE/ON resolve through `Run`, not `GetSqlType`.

### Multi-source rows
`FromSource[]`; rows during enumeration are `byte[]?[]`, one slot per source, null = unmatched LEFT JOIN right side. Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple`; ambiguous unqualified name → Msg 209.

### `MultiPartName`
Readonly struct, up to 4 inline slots (SQL Server's grammar limit). API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Default.Equals(name.ImmediateQualifier, "INSERTED")`, the equality folds null into `false`), `Count`, `ToString()`. 5th segment → Msg 4104.

### Exception factories
`SimulatedSqlException` constructor is private; each error case is an `internal static` factory in a topical partial (`TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`). The number lands in `Data["HelpLink.EvtID"]`. **Grep for an existing factory before adding a new one.**

### Expression evaluation
`Expression.Run(columnResolver)` (runtime) and `Expression.GetSqlType(...)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema. `BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes UNKNOWN. Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

## Conventions that fail builds

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;`. Overrides, abstracts, statics, and interface implementations exempt.
- **SSS002**: a `readonly` field in a non-public-API type whose declared type is a strict supertype of its initializer should be declared as the concrete type. Public types, value-typed initializers (boxing), const fields, and uninitialized fields exempt.
- **SSS003**: `string.ToUpperInvariant()` / `ToLowerInvariant()` whose result is the *governing expression* of a `switch` allocates a temporary string. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on the resulting count.
- **SSS004**: two or more `if`/`else if` branches with conditions of the shape `<sameScrutinee> is <SameType> { <SameProperty>: ... }` should be a single `switch`. The `switch` form fuses isinst + ldfld; the if-chain repeats both per arm.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; typed asserts over generic.

## Style notes

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly.
- **No comments inside expression chains** — IDE0055 fails on comments in ternary chains or between `=>` and body. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (SSS001 generalized).
- **AssemblyHooks**: each test project has `AssemblyHooks.cs` with a `static [TestClass] [AssemblyInitialize]` to warm shared initialization paths once before the parallel test run. Without it, the first batch of tests races to initialize hot shared state and serializes on contention. The analyzer-tests' Roslyn-cache warm-up is the most extreme case observed (~3x slowdown), but the pattern generalizes to any expensive first-touch shared resource.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature.

## What's modeled

The `*.Tests` and `*.Tests.EFCore` suites are the authoritative behavior contract. Notes below cover only probe-confirmed quirks, deviations from SQL Server, and non-obvious implementation rules.

### Batch grammar: statement separators
Statements are separated by an optional `;`. Real SQL Server's relaxed grammar lets most statement pairs sit adjacent (`declare @v int = 7 select @v`, `set @v = 1 set @w = 2`, `insert t values (1) select * from t`, `begin tran ... commit`); the simulator follows. Two enforced exceptions match SQL Server's specific rules:
- A CTE (`WITH`) directly following another statement raises **Msg 319 St 1** (verbatim wording: `Incorrect syntax near the keyword 'with'. If this statement is a common table expression, an xmlnamespaces clause or a change tracking context clause, the previous statement must be terminated with a semicolon.`). A `WITH` at batch start (or right after a `;`) is fine. The check fires both at `Simulation.CreateResultSetsForCommand`'s top-level dispatch (`requireSemicolonBeforeCte` flag) and inside `Selection.Parse`'s projection-element switches — the latter is where `select 0 with cte ...` surfaces it before the SELECT can complete.
- A `MERGE` not terminated by `;` raises **Msg 10713 St 1** (`A MERGE statement must be terminated by a semi-colon (;).`) regardless of whether another statement follows or the batch ends. The check sits at the dispatch site immediately after `ParseMerge` returns, before any cursor normalization.

The dispatch loop drains optional `;`s at the top of each iteration and trusts each parser to leave `Token` at its first un-consumed token (the `ParserContext` lookahead-position contract). Parsers that historically ended on the last token they consumed (DBCC's closing `)`, SET-session-state's `ON`/`OFF`) get a one-token advance via `IsStatementBoundary` after dispatch — Token already at `;`, end-of-batch, or a recognized statement-starting keyword is left alone.

### Boolean / set ops / projection / CASE
- Boolean combinators (WHERE / MERGE-ON / CHECK): `AND` / `OR` / `NOT`, parens, `IS [NOT] NULL`, `[NOT] IN (literal,...)`. Tri-valued.
- Set ops (UNION / UNION ALL / INTERSECT / EXCEPT): standard precedence (INTERSECT > UNION/EXCEPT). **NULLs are equal during set-op dedup/matching** (opposite of `=`'s tri-state). Per-branch ORDER BY in non-final branch → Msg 156. Top-level ORDER BY references first-branch column names only.
- `SELECT *`: bare and qualified `<source>.*`. Multi-source `*` keeps duplicate names. Unbound `<qualifier>.*` → Msg 4104.
- CASE: searched + simple. UNKNOWN excludes (matches WHERE); simple-form `CASE NULL WHEN NULL` falls through. Result type from `SqlType.Promote` over THEN/ELSE.
- `ISNULL` truncates fallback to first arg's type. `IIF` = sugar for searched CASE. `NULLIF(a, b)` = `CASE WHEN a = b THEN NULL ELSE a END`. EF emits `ISNULL` only for `??` with a CAST; bare `??` emits `COALESCE`. Neither IIF nor NULLIF is EF-emitted (LINQ ternary → CASE) — load-bearing for `FromSqlInterpolated`.

### JOINs / APPLY
INNER / bare JOIN / LEFT [OUTER] / CROSS / CROSS APPLY / OUTER APPLY. Multi-table chains compose left-to-right. ON-predicate UNKNOWN excludes. APPLY = lateral form (right side re-executed per outer row, no ON clause).

### Subqueries
`EXISTS` / `NOT EXISTS` (multi-column inner allowed); `expr [NOT] IN (SELECT ...)` (single inner column, Msg 116); scalar `(SELECT col FROM ...)` (single column, single-row Msg 512 per outer row, empty → typed NULL). All forms work correlated and non-correlated, arbitrary nesting depth.

### Pagination (`OFFSET ... FETCH`)
- OFFSET requires ORDER BY (else Msg 102).
- FETCH alone (no preceding OFFSET) → **Msg 153**.
- Negative offset → **Msg 10742** (`"...a OFFSET clause may not be negative."` — verbatim "a OFFSET").
- Fetch ≤ 0 → **Msg 10744** (verbatim typo "greater then zero").
- TOP + OFFSET → **Msg 10741**.
- Counts resolve at parse time (constants, parameters, arithmetic).

### Aggregates
`COUNT(*)` / `COUNT(expr)` / `COUNT(DISTINCT)` / `COUNT_BIG`, `SUM` / `AVG`, `MAX` / `MIN`, statistical (`STDEV` / `STDEVP` / `VAR` / `VARP`), `STRING_AGG`, `CHECKSUM_AGG`, `APPROX_COUNT_DISTINCT`. `AVG(int)` truncates; `AVG(decimal(p,s))` widens to `decimal(38, max(s,6))`.

`STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY ...)` reorders concatenation per group (EF emits this from `GroupBy(...).Select(g => string.Join(sep, g.OrderBy(...)))`). NULL operand rows skip both ORDER BY input and output. Non-`STRING_AGG` aggregate with `WITHIN GROUP` → **Msg 10757**; ORDER BY ordinal in this context → **Msg 5308** (distinct from projection-level ORDER BY which accepts ordinals); `WITHIN` is contextual (not reserved). Cross-aggregate Msg 8711 isn't modeled (EF doesn't emit).

### Window functions
- `ROW_NUMBER() OVER([PARTITION BY ...] ORDER BY ...)` — bigint, ORDER BY required (else Msg 4112). EF wraps in derived-table subquery.
- Aggregate windows: `SUM`/`AVG`/`COUNT`/`COUNT_BIG`/`MIN`/`MAX`/`STDEV*`/`VAR*`/`CHECKSUM_AGG`/`APPROX_COUNT_DISTINCT(expr) OVER ([PARTITION BY ...])`. **Implicit-frame whole-partition only** (no ORDER BY in OVER for aggregates).
- `RANK`/`DENSE_RANK`/analytic family/explicit frames/aggregate-window ORDER BY → `NotSupportedException` (not silent Msg 102) so users get a diagnostic.
- Errors: `STRING_AGG OVER` → Msg 4113; `COUNT(DISTINCT) OVER` / `SUM(DISTINCT) OVER` → Msg 10759; windowed function in WHERE/HAVING/GROUP BY/ON → Msg 4108. Window + GROUP BY/HAVING in same SELECT → `NotSupportedException`.

### Integer ↔ string promotion
Cross-category `int ↔ string` lands the integer's specific subtype (`tinyint + '3'` stays tinyint; `bigint + '3'` stays bigint). String parses through the integer's CAST path: empty/whitespace → 0, `+`/`-` accepted, leading/trailing whitespace trimmed. **Decimal-shaped strings (`'5.5'`) raise Msg 245** rather than routing through decimal. Hex (`'0x05'`) likewise rejected.

`bit ↔ string` asymmetry: comparison works (`'true'`/`'false'`/empty → true/false/false; non-zero digit string → True regardless of magnitude); `bit + str` rejected — `+`/`-`/`%` → Msg 402, `*`/`/` → Msg 8117 with LEFT operand's type only.

WHERE on a varchar column compared against int halts on the first unparseable row (not isolated as per-row UNKNOWN). SQL Server's lazy-IN quirk (unparseable IN-list value suppressed when another matches) isn't modeled.

`BuildSynthesizedSqlRow` (FROM-less SELECT) runs each expression first (surfacing runtime-only errors with operator-name wording), then `GetSqlType` for schema, then bridges any mismatch via `CoerceTo` — required for mixed-type CASE/Coalesce without a FROM clause.

### Decimal arithmetic precision / scale
Per-operator decimal scale rules differ from the joint-envelope rule used for non-arithmetic uses (comparison / COALESCE / set ops):
- `+` / `-`: `p = max(p1-s1, p2-s2) + max(s1, s2) + 1`, `s = max(s1, s2)`
- `*`: `p = p1 + p2 + 1`, `s = s1 + s2`
- `/`: `s = max(6, s1 + p2 + 1)`, `p = p1 - s1 + s2 + s`
- `%`: `p = min(p1-s1, p2-s2) + max(s1, s2)`, `s = max(s1, s2)`

When precision exceeds 38, scale reduces by the excess down to a floor of `min(originalScale, 6)`; precision clips to 38. The 6-floor stabilizes division (`s ≥ 6` always); for `+ - * %` it binds only when original scale was already ≤ 6.

Integer/money operands canonicalize before formulas apply (bit→(1,0) … bigint→(19,0); money→(19,4); smallmoney→(10,4)). Pure integer-pair, pure money-pair, and float-involving arithmetic skip the decimal path (joint-envelope `Promote` instead).

`SqlType.Promote` (joint-envelope, `scale = max(s1, s2); precision = min(38, max(p1-s1, p2-s2) + scale)`) stays the right rule for non-arithmetic uses.

### Math scalar functions
`ABS`, `ROUND` (2-/3-arg, half-away-from-zero + truncate mode), `FLOOR`, `CEILING`, `POWER`, `SQRT`, `SIGN`, `LOG` (1-/2-arg), `EXP`, `LOG10`, trig family (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`), `PI`, `DEGREES`/`RADIANS`, `SQUARE`. EF emits all from `Math.X` LINQ; `Math.Truncate(x)` → `ROUND(x, 0, 1)`; `Math.Atan2` → `ATN2`.

**Type-widening rule** (shared across `ABS`/`FLOOR`/`CEILING`/`ROUND`/`SIGN`/`POWER`'s first arg): `tinyint`/`smallint` → `int`; `smallmoney` → `money`; `real`/`bit` → `float` (sic — bit widens to float, not int); everything else preserves. `POWER` returns the post-widen type of the *first* arg regardless of exponent — `POWER(int, float) → int` with truncation toward zero. `SQRT`/`LOG`/`EXP`/`LOG10` always return float.

Errors: `SQRT(neg)` / `LOG(<= 0)` / `LOG10(<= 0)` / `LOG(x, 1)` / `POWER(neg, frac)` → Msg 3623. `POWER(0, neg)` → Msg 8134. `EXP` / `SQUARE` overflow → Msg 8115 float. `ABS(int.MinValue)` / `ABS(bigint.MinValue)` → Msg 8115 with the result type's family. `POWER` int-result overflow → Msg 232.

**Trig family** (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`/`PI`/`SQUARE`) always returns `float`. Domain errors → Msg 3623: `ASIN`/`ACOS` outside `[-1, 1]`, `COT(0)`, `ATN2(0, 0)` (the last diverges from .NET's `Math.Atan2(0, 0) = 0`). Wrong arg count → Msg 174 (`"The {lower-name} function requires {N} argument(s)."`). `pi(1)` raises Msg 174 not Msg 102.

**`DEGREES`/`RADIANS`** are type-preserving with one tweak: `decimal(p, s)` widens to `decimal(38, max(s, 18))` rather than preserving. Other categories follow the shared rule. Integer arm truncates toward zero — `DEGREES(360)` → `20626` from `20626.48...`; out-of-range integer results raise Msg 8115 with the family name. Decimal arm uses a 28-digit `DecimalPi` constant in evaluation order `(input * 180m) / DecimalPi` for trailing-digit fidelity. .NET decimal's 28-digit precision cap means scale > 28 results land at scale 28 (pre-existing quirk).

**`Math.Sign(decimal)` doesn't work end-to-end** against either real SQL Server or the simulator: LINQ's CLR signature returns `int` but EF emits `SIGN([col])` which returns decimal for decimal inputs, so the reader-side cast throws. Same failure mode in both — not a fidelity bug. `Math.Sign` over int columns works.

### Date scalar functions: `DATEPART` / `DATEADD` / `DATEDIFF` / `DATEDIFF_BIG`
All take a bare datepart keyword. Result types: `DATEPART` → int; `DATEADD` preserves input type; `DATEDIFF` → int; `DATEDIFF_BIG` → bigint.

`DATEPART`/`DATEADD` enforce per-type keyword compatibility: `date` accepts only date parts; `time(N)` only time parts; `datetime`/`smalldatetime`/`datetime2(N)` accept both; `datetimeoffset(N)` adds `tzoffset`. Wrong combination → Msg 9810. `DATEADD` overflow → Msg 517. `DATEPART(weekday)` uses default `DATEFIRST 7` (Sunday=1); changing `DATEFIRST` not modeled.

`DATEDIFF`/`DATEDIFF_BIG` count `datepart`-unit *boundaries crossed*, not elapsed time — `datediff(year, '2023-12-31', '2024-01-01')` = 1. More permissive than DATEPART/DATEADD: every datepart works against every date/time-family combo. Only `tzoffset` and `iso_week` are rejected unconditionally → Msg 9806. String literals implicitly cast to `datetime2(7)`. `datetimeoffset` operands compare via UTC instant. Result-width overflow → Msg 535.

Unknown keyword → Msg 155 with the calling function's lowercase name embedded. NULL on any operand → typed NULL.

### Current-time scalars
Result types: `GETDATE`/`GETUTCDATE`/`CURRENT_TIMESTAMP` → `datetime`; `SYSDATETIME`/`SYSUTCDATETIME` → `datetime2(7)`; `SYSDATETIMEOFFSET` → `datetimeoffset(7)`. EF emits these from `DateTime.UtcNow`/`Now`/`DateTimeOffset.UtcNow` and `HasDefaultValueSql("getutcdate()")`.

**Per-statement freeze**: two `SYSDATETIME()` calls in one SELECT return identical values; an UPDATE that stamps every row writes the same value; successive SELECTs in one batch DO advance. Captured once per statement-loop iteration into `Simulation.CurrentStatementUtcNow`.

**UTC == Local** (Azure SQL Database default): no local-time conversion; all six functions return the same UTC instant (rounded per type — datetime variants quantize to 1/300s tick). `SYSDATETIMEOFFSET` reports `+00:00`. Apps depending on `GETDATE` ≠ `GETUTCDATE` differing by zone won't behave like on-prem; matches cloud default.

**`CURRENT_TIMESTAMP` is parens-less** — only zero-arg function in the grammar without `()`. Surfaces as `ReservedKeyword { Keyword: Keyword.Current_Timestamp }`, dispatched directly from `Expression.Parse`'s expression-start switch (NOT via `ResolveBuiltIn`). `CURRENT_TIMESTAMP()` with parens → Msg 102.

### Variadic string concat: `CONCAT` / `CONCAT_WS`
Both stringify each arg via CAST-to-varchar/nvarchar, **skip NULL args** (don't propagate), and **never return NULL** — all-NULL input → `''`. Result is `nvarchar` if any arg has a national-string type, else `varchar`. Arg-count rules → Msg 189: `CONCAT` requires 2-254 args; `CONCAT_WS` requires 3-254 (separator + ≥2 values).

`CONCAT_WS` quirks: NULL separator silently degrades to empty string (NOT NULL propagation despite docs); NULL values skipped entirely (no double separators); `concat_ws(sep, single_value)` → Msg 189 (refuses no-op stringify).

**EF doesn't emit `CONCAT` from `string.Concat`** — that translates to `[a] + N'-' + [b]` (the `+` operator, NULL-propagating). CONCAT/CONCAT_WS are reachable from raw SQL (`FromSqlInterpolated` / direct command).

### String `+` operator (concatenation)
**NULL-propagating** (matches default `CONCAT_NULL_YIELDS_NULL ON`; OFF setting not modeled). Result is `nvarchar` when either operand is national-string, else `varchar`. EF's dominant string-concat path. `text` / `ntext` / `image` / `varbinary` operands → Msg 402.

**Bare-NULL divergence**: simulator's untyped `NULL` literal carries `SqlType.Int32`, so `'a' + NULL` and `'a' + cast(NULL as int)` are indistinguishable at runtime. Both treated as string concat (returning NULL of the result string type); matches real SQL Server on bare NULL but diverges from `cast(NULL as int) + 'a'` (real raises Msg 245). Bare NULL dominates in practice; typed-null-int is a rare hand-written shape EF never emits.

**Result-type fidelity**: `char(N) + char(M)` → `char(N+M)` (capped at 8000); `nchar` analogous; mixed `char + nchar` → `nchar`. Variable-length pairs and mixed fixed/variable → length-bearing `varchar(N+M)` / `nvarchar(N+M)` (capped at 8000/4000). LOB and unspecified-length operands fall back to the unspecified form. `Subtract`/`Multiply`/etc. on string operands → `NotSupportedException` (real SQL Server: Msg 402 / Msg 8117).

### Date-construction scalars: `*FROMPARTS` family + `EOMONTH`
Six builders: `DATEFROMPARTS`, `DATETIMEFROMPARTS`, `DATETIME2FROMPARTS`, `DATETIMEOFFSETFROMPARTS`, `SMALLDATETIMEFROMPARTS`, `TIMEFROMPARTS`. Shared shape: NULL on any non-precision arg propagates; non-int operands coerce through CAST (decimal/string/bigint accepted); out-of-range → Msg 289 with type-specific State (1=date, 2=time, 3=datetime, 5=datetime2, 6=datetimeoffset).

Variable-precision builders (`datetime2`/`datetimeoffset`/`time`) extract precision at parse time by evaluating the parsed sub-expression with a NULL-returning resolver — literal `1+2` folds to `3`, but a column ref degrades to NULL → Msg 10760. Out-of-`[0, 7]` precision → Msg 1002. Result type carries the captured precision: `DATETIME2FROMPARTS(..., 3)` → `datetime2(3)`.

`DATETIMEFROMPARTS` ms 999 with hour 23/min 59/sec 59 rolls to next day via legacy datetime's 1/300s tick rounding. `DATETIMEOFFSETFROMPARTS` enforces sign-consistency between hour_offset and minute_offset (mixed signs → Msg 289 St 6) and a |offset| ≤ 14:00 cap.

`EOMONTH(start_date [, month_offset])` always returns `date` regardless of input type. **Quirk**: NULL `month_offset` is silently treated as zero, unlike NULL `start_date` which propagates.

### `AT TIME ZONE`
Postfix operator; LHS-type-discriminated semantics:
- `datetime2`/`datetime`/`smalldatetime AT TIME ZONE 'X'`: treats LHS wall-clock as already in zone X, attaches X's offset. Skipped (spring-forward) wall-clocks shift forward by DST delta with post-transition offset; ambiguous (fall-back) picks daylight (pre-fall-back).
- `datetimeoffset AT TIME ZONE 'X'`: preserves UTC instant; both offset and wall-clock change.

Result is always `datetimeoffset` with LHS fractional precision preserved (`datetime2(N)`/`datetimeoffset(N)` → `datetimeoffset(N)`; legacy `datetime`/`smalldatetime` → `datetimeoffset(3)`). `date`/`time` LHS → Msg 8116. Unrecognized zone → Msg 9820. NULL on either side propagates.

Zone-name resolution via `TimeZoneInfo.FindSystemTimeZoneById` (accepts both Windows-style and IANA names cross-platform via ICU); cached in a process-static `ConcurrentDictionary`.

**Precedence**: `AT TIME ZONE` binds tighter than `+`. The zone-name slot parses as a primary expression only — literals, `@variables`, single-segment column refs, or parenthesized full expressions. Multi-part dotted refs and binary chains in the zone slot aren't modeled; wrap in parens. `AT`/`TIME`/`ZONE` are contextual keywords (still valid identifiers).

### CAST/CONVERT to narrow `varchar` / `nvarchar` / `varbinary`
Per-source-category rule applied after `SqlValue.CoerceTo`:
- String / varbinary / date-time-family source → silent truncation. `CAST('hello world' AS varchar(5))` → `'hello'`.
- `tinyint`/`smallint`/`int` source → `varchar` too narrow → asterisk fallback (`'*'`). Quirk specific to `varchar`; `nvarchar` raises Msg 8115. `bigint` doesn't get fallback either.
- `decimal`/`numeric` source → Msg 8115 with "numeric" wording (distinct from int/bigint's "expression" wording).
- `money`/`smallmoney` → Msg 234 (`"There is insufficient result space to convert a money value to <target>."` — "money" regardless of source variant).
- `float`/`real` → Msg 232 with formatted source value (F6).
- `uniqueidentifier`: pre-CoerceTo branch (Msg 8170 char/varchar, Msg 8115 nchar/nvarchar).
- `datetimeoffset → varchar` too narrow: real SQL Server raises Msg 241; simulator silently truncates (niche).

**CAST/CONVERT context defaults missing length to 30** for `varchar`/`nvarchar`/`varbinary` (column-context default is 1).

`VarcharSqlType`/`NVarcharSqlType`/`VarbinarySqlType` are per-length singletons via `Get(N)` (parallel to `CharSqlType`); `Unspecified` (length 0) is the runtime sentinel; `MaxForm` (length -1) is the LOB form. **Equality**: `value.Type == SqlType.Varchar` is true only for the unspecified form; "is any varchar" needs `is VarcharSqlType`. The encoder accepts any same-family pair regardless of length (write-time truncation enforced upstream).

### `TRY_CAST` / `TRY_CONVERT`
Wrap regular CAST/CONVERT in try/catch that swallows documented "conversion failed" error numbers (returning typed NULL) while letting structural errors propagate.

Swallow set (`Cast.IsConversionFailure`): **241** (datetime-from-string parse), **242** (datetime out-of-range), **244** (tinyint/smallint INT1/INT2 overflow), **245** (string→numeric parse), **248** (int overflow), **295** (smalldatetime parse), **8114** (decimal conversion), **8115** (generic arithmetic overflow), **8169** (uniqueidentifier-from-string), **8170** (uniqueidentifier→too-narrow-string).

NOT swallowed: Msg 529 (explicit-cast disallowed pair like `int → date`), Msg 243 (unknown target type), and any source-evaluation error that fires before the cast itself runs. `TRY_CAST(1/0 AS INT)` raises Msg 8134 in real SQL Server; the simulator surfaces a raw `DivideByZeroException` (pre-existing fidelity gap orthogonal to TRY_CAST).

String-source truncation isn't a "conversion failure" path either way — `TRY_CAST('hello' AS varchar(3))` → `'hel'`. EF doesn't emit TRY_CAST/TRY_CONVERT from idiomatic LINQ (raw SQL only).

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree.

### Transactions
Three entry points share one per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).

- **Statement-level atomicity**: a single mutation throwing mid-execution rolls back its partial writes. Multi-row INSERT failing on row 3 leaves zero rows.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` actually commits; `ROLLBACK` zeroes `TranCount` and walks the entire log. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx. Parallel `BeginTransaction` → `InvalidOperationException`. `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both keep advancing through rollback. Orphaned LOB chains for rolled-back inserts also leak.
- No isolation: uncommitted writes immediately visible to all readers (single-Simulation, single-thread-at-a-time).

### UPDATE / DELETE
- Bare `UPDATE table SET ... [WHERE]` and `DELETE [FROM] table [WHERE]`.
- Multi-table syntax (`UPDATE alias SET ... FROM <sources> [WHERE]`, `DELETE FROM alias FROM <sources> [WHERE]`) — the EF7+ `ExecuteUpdate`/`ExecuteDelete` shape. Target identified by leading-identifier match against each source's `FromSource.Qualifier`; missing match → Msg 208.
- **Joined UPDATE/DELETE: each unique target row processed exactly once.** When the same target matches multiple join tuples, SQL Server uses the *first* matching tuple's RHS for SET. The simulator dedupes by `(page, slot)` via a side-channel byte[]→address map. LEFT JOIN with no right-side match still surfaces the target (RHS sees NULL).
- **OUTPUT** supported only when the leading identifier resolves to a real table name; OUTPUT + alias-form multi-source → `NotSupportedException` (EF doesn't combine those).
- **Multi-column SET evaluates RHS against pre-update snapshot** — `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` → `(a=100, b=11)`. Scalar subquery RHS sees pre-update state.
- Identity update → Msg 8102. Computed update → Msg 271. Rowversion update → Msg 272. Per-row constraint re-validation: NOT NULL → Msg 515 ("UPDATE fails."); CHECK → Msg 547 ("UPDATE statement"); PK/UNIQUE → Msg 2627 (verbatim "Cannot insert duplicate key" wording even on UPDATE — SQL Server quirk).
- Two-phase: phase 1 picks affected rows + computes new values + per-row validation; phase 2 validates PK/UNIQUE against the post-update virtual state; phase 3 mutates (tombstone old, insert new).
- `OUTPUT INSERTED.<col>` (post-update) / `DELETED.<col>` (pre-update). UPDATE allows both qualifiers; DELETE rejects `INSERTED.<col>` at parse → Msg 4104. Star expansion (`INSERTED.*`/`DELETED.*`) and `OUTPUT INTO @table_var` not modeled.

### `rowversion` (legacy synonym `timestamp`)
8-byte big-endian database-scoped monotonic counter; advances on every INSERT into a rowversion-bearing table and every UPDATE affecting one. Storage type name surfaces as `timestamp` in `information_schema` regardless of declaration. Explicit insert → Msg 273; explicit update → Msg 272; second column on a table → Msg 2738. Outbound CAST: `varbinary(N)`/`binary(N)` copy 8 bytes; `bigint` reads big-endian. `Promote(RowVersion, Varbinary) → Varbinary` so EF's `WHERE [rv] = @originalRv` parameter works directly. EF `[Timestamp]` SaveChanges round-trips end-to-end.

### Variables: `DECLARE` / `SET` / `SELECT @v = expr`
Per-batch scalar variables. `DECLARE @v TYPE [= expr] [, @w TYPE [= expr] ...]` registers slots; `SET @v = expr` and `SELECT @v = expr [, @w = expr2 ...]` mutate them. SqlClient parameters seed the same store as if pre-DECLAREd, so a parameter and a DECLARE can't share a name (Msg 134).

Variable references resolve at runtime via a captured `VariableSlot` — required because mutations between statements have to be visible to subsequent reads. Assignment coercion routes through `Cast.ApplyCoercion` so the slot's declared type is honored: `SET @v(varchar(3)) = 'hello'` truncates to `'hel'`; `SET @v(int) = 'abc'` raises Msg 245.

**SELECT-assign quirks**:
- All-or-nothing: `SELECT @v = 1, 2` (mixing assign and projection) → Msg 141.
- Empty result-set keeps prior value (no rows iterate, slot unchanged); `SET @v = (SELECT no rows)` differs — assigns NULL.
- Multi-row last-row-wins post-ORDER-BY (per-row evaluation, last write wins).
- The dispatch drains rows for side-effects and yields a `SimulatedNonQuery` rather than a result set (matches SQL Server's no-result-set-envelope behavior for SELECT-assign).

**Errors**: Msg 137 use-before-declare (existing factory); Msg 134 duplicate DECLARE (also fires for parameter+DECLARE collision); Msg 141 mixed assignment + retrieval; standard CAST errors propagate from coercion (Msg 245, Msg 8115, etc.). `DECLARE @v INT NOT NULL` and `DECLARE @v INT = DEFAULT` raise Msg 102 / 156 respectively (DECLARE doesn't accept column-style constraints — falls out of grammar mismatch).

**Output-parameter write-back**: at end of batch, the dispatch walks the parameter list and copies each `InputOutput` / `Output` direction parameter's final slot value back to `DbParameter.Value`. Mirrors SqlClient's round-trip behavior for hand-rolled scripts that mutate parameters.

**`@@ROWCOUNT`**: tracks the most-recently-completed statement's row count via `Simulation.LastStatementRowCount`. SELECT row counts populate after the dispatch materializes rows up-front (so the next statement in the batch sees the final count); DML mutations write their affected count; `SET` / `DECLARE @v = init` write 1; bare `DECLARE @v` (no initializer) preserves the prior count; transaction / DDL statements reset to 0.

**Compound assignment** (`SET @v += expr` etc.) and **table variables** (`DECLARE @t TABLE (...)`) aren't modeled — rewrite as `SET @v = @v + expr` for the former; the latter is a separate bundle.

### Common table expressions
`WITH name [(col, …)] AS (SELECT …) [, …] {SELECT|INSERT|UPDATE|DELETE|MERGE} …`. WITH prefix scopes to exactly one immediately-following statement. Both non-recursive and recursive forms modeled.

Bindings registered at statement-loop top in `ParserContext.CteBindings` (a `Dictionary<string, CteBinding>`); cleared on next iteration. CTE name shadows a real table for the prefixed statement. Multiple comma-separated CTEs cascade — later ones see earlier ones.

**Non-recursive**: branches folded via `CombineSetOps` (type-promotion matches regular set-op chain). FromSource uses `lateralPlan: binding.Plan` so each FROM-side reference re-runs the inner Selection.

**Recursive**: body parser splits branches into anchor (no self-ref) and recursive (one self-ref each); `Selection.FromRecursiveCte` runs anchors once into a seed rowset, then iterates each recursive branch against the previous-iteration rowset until empty (or `MaxRecursion` trips Msg 530 with the literal limit). Default 100; `OPTION (MAXRECURSION N)` overrides; `0` disables.

Self-reference resolution: during the recursive-part parse, `binding.IsRecursivePartParse` is set after anchor completes (which captures Schema + ColumnNames). Subsequent branches route self-refs through a FromSource backed by `binding.CurrentIterationRows`. Per-branch `SelfReferenceCountInCurrentBranch` classifies the branch as anchor (count 0) or recursive (count 1) and enforces one-self-ref-per-branch (Msg 253).

Errors:
- **Msg 240**: anchor and recursive parts produce different per-column types (strict type-equality, no Promote-style widening — must explicitly cast).
- **Msg 247**: anchor branch appears after a recursive branch.
- **Msg 252**: self-reference but no top-level UNION ALL splitting it from an anchor; also fires when UNION-without-ALL is used between branches.
- **Msg 253**: one recursive branch references the CTE more than once.
- **Msg 530**: MAXRECURSION exceeded.
- **Msg 239** duplicate CTE name; **Msg 8158**/**8159** rename-list count mismatch; **Msg 1033** ORDER BY in CTE body without TOP/OFFSET/FETCH.

`OPTION (MAXRECURSION N)` parses inside `Selection.ParseQueryExpression` after ORDER BY/OFFSET/FETCH and writes to every binding in scope. Other hints (`OPTIMIZE FOR`, `RECOMPILE`, etc.) → `NotSupportedException`. EF emits non-recursive CTEs in some shapes (TPC inheritance, certain Distinct/OrderBy/Skip patterns); recursive CTEs only via raw SQL.

### INSERT … SELECT
`INSERT [INTO] target [(cols)] SELECT …` accepts the full Selection grammar — WHERE/JOIN/GROUP BY/aggregates/ORDER BY/TOP/OFFSET-FETCH/UNION/INTERSECT/EXCEPT all work source-side.

Source-kind dispatch after the OUTPUT-clause parse: `Values` token → existing tuple-parsing path; `Select` token → `Selection.Parse(…).Execute()`. Both funnel into one shared per-row encode loop (defaults / identity / rowversion / computed / constraints / OUTPUT).

**Full buffering**: source materializes to `List<SqlValue[]>` before any destination write — makes self-insert (`INSERT t SELECT … FROM t`) safe.

Projection-count mismatch fires at parse time: too few SELECT columns → Msg 120 St 1 Cls 15; too many → Msg 121. Empty source → silent success, rows-affected 0. Mid-source constraint violations trigger statement-level rollback. EF doesn't emit `INSERT…SELECT` from SaveChanges; reachable from raw SQL and bulk-copy patterns. CTE-prefix INSERTs not modeled.

### JSON: `JSON_VALUE` / `JSON_MODIFY` / `OPENJSON`
Unlocks EF's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection emissions. JSON columns are plain `nvarchar(max)`.

`JSON_VALUE(json, path)` returns `nvarchar`. Lax mode (default and EF's only emitted form): missing path / non-scalar match → SQL NULL. `strict $.foo` raises Msg 13608 on miss. NULL `json` or NULL path → NULL. JSON booleans render as lowercase `'true'`/`'false'`; numbers as raw text via `JsonElement.GetRawText`. Object/array matches → NULL in lax.

`JSON_MODIFY(json, path, newValue)` returns `nvarchar`. EF emits `'strict $.City'`-shape paths from owned-as-JSON partial updates (missing leaf → Msg 13608). Bare `'$'` replaces the entire document. Lax existing-key + NULL value removes the key; lax missing key + non-NULL value adds it. Numeric/boolean `newValue` stays JSON-typed (`{"n":42}` not `{"n":"42"}`).

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning, structurally a new FromSource kind. Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null/1=string/2=number/3=bool/4=array/5=object. With WITH: each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use `'$'`. `AS JSON` modifier → `NotSupportedException`. NULL/invalid JSON → zero rows under lax.

OPENJSON WITH-clause types: `int`/`bigint`/`decimal(p,s)`/`float`/`bit`/`nvarchar(N|max)`/`varchar(N)`/`date`/`datetime2(N)`/`datetimeoffset(N)`/`uniqueidentifier`. Coercion via `SqlValue.CoerceTo`. Backed by `System.Text.Json` (no NuGet dep added).

EF emissions covered: `Where(c => c.Address.City == "X")`; `c.Tags.Contains("x")` → `OPENJSON ... WITH ([value] nvarchar(max) '$')` inside `IN(SELECT)`; `c.Scores.Count` / `Any(...)`; owned-as-JSON partial UPDATE → `JSON_MODIFY([Address], 'strict $.City', JSON_VALUE(@p0, '$.""'))`. Quoted-property escape `""` → literal `"`.

Not emitted by EF / not modeled: `JSON_QUERY`, `ISJSON`, `FOR JSON PATH`/`AUTO`. Reachable only via raw SQL.

### MERGE / OUTPUT (EF SaveChanges shape only)
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch).
- `WHEN MATCHED` parses but throws `NotSupportedException` if its predicate ever evaluates true.

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers seven SqlParameter-downcast pairs: `DateOnly→date`, `DateTime→date`, `DateTime→smalldatetime`, `TimeOnly→time(N)`, `TimeSpan→time(N)`, `decimal→money`, `decimal→smallmoney`. Without the adapter those mappings throw at SaveChanges. MAX-string family flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY/DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) all enforced.

### `SimulatedDbDataReader`
Full `DbDataReader` contract. Typed accessors read `SqlValue` directly via the cursor's indexer and unwrap via `As*` (no boxing); NULL on a typed accessor → `SqlNullValueException` matching SqlClient. `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date surfaces at midnight, `Kind=Unspecified`); `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` and `TimeOnly`-over-`Time`. `GetOrdinal(name)` is two-pass linear scan (case-sensitive then case-insensitive — SqlClient's documented precedence). `HasRows` is a sticky bit. `GetChar(int)` always raises `InvalidCastException` (matches SqlClient).

## Not modeled

- Locks / MVCC / isolation levels (single-Simulation, single-thread-at-a-time). `BEGIN DISTRIBUTED TRANSACTION`, `BEGIN TRANSACTION ... WITH MARK`, `XACT_ABORT`, `SET TRANSACTION ISOLATION LEVEL` not parsed.
- `RIGHT JOIN` (rewrite as LEFT with sources swapped) and `FULL OUTER JOIN` — both raise `NotSupportedException` at parse.
- Comma-separated FROM (legacy ANSI-89 join syntax).
- `ANY` / `SOME` / `ALL` quantifiers.
- `UNION` / `UNION ALL` inside a subquery body.
- Row-constructor `IN ((1,2), (3,4))`.
- Window functions other than `ROW_NUMBER` and the aggregate-OVER family.
- Recursive-part feature restrictions (Msg 460 DISTINCT / 461 TOP / 462 OUTER JOIN / 467 aggregate-or-GROUP-BY / 465 ref-in-subquery) — silently accepted with possibly-incorrect semantics rather than raising. Apps that exercise these in real SQL Server hit rejection there too.
- `LIKE` with `COLLATE` override (default collation only).
- `CONVERT` / `TRY_CONVERT` style codes other than `0` / `120` / `121`.
- `LEN(ntext)` raising Msg 8116; legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- `OUTPUT INTO @table_var`, `OUTPUT DELETED.*` / `INSERTED.*` star expansion.
- MERGE source subqueries; MERGE target column refs in `ON`; `WHEN MATCHED` UPDATE/DELETE branches; `$action`.
- Msg 8141 (inline CHECK referencing a peer column — SQL Server rejects at CREATE TABLE; simulator allows).
- Msg 8133 (CASE where every branch is bare `NULL`; simulator returns NULL of `int`).
- `PRIMARY KEY` / `UNIQUE` on a computed column (`NotSupportedException`).
- Heap allocation tracking (flat page list, no IAM/PFS).
- Per-connection session state for `SCOPE_IDENTITY()` / `@@IDENTITY`, `SET IDENTITY_INSERT`'s active table, `DBCC TRACEON(N)` flags — all live on `Simulation` rather than connection. (Tx state is already per-connection.)
- Compound assignment (`SET @v += expr` / `-=` / `*=` etc.) — rewrite as `SET @v = @v + expr`. The arithmetic-operator runtime is locked behind `protected` instance methods on `TwoSidedExpression`; exposing them as static helpers is the prerequisite refactor.
- Table variables (`DECLARE @t TABLE (...)`) — separate feature with its own storage / scope / lifecycle.
- T-SQL control flow (`IF` / `WHILE` / `BEGIN ... END` / `BREAK` / `CONTINUE`) — Bundle 2 of scripting.
- `TRY ... CATCH`, `THROW`, `RAISERROR`, `@@ERROR`, `RETURN`, `PRINT`, stored procs / UDFs.
- `hierarchyid`, `geography`, `geometry`.

## Quirks (modeled, not byte-identical to SQL Server)

- `CHECKSUM_AGG`: order-independent XOR fold; semantic guarantee matches (same multiset → same checksum), bit pattern won't.
- `APPROX_COUNT_DISTINCT`: implemented as exact `COUNT(DISTINCT)`.
- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific.
- Auto-generated PK / UNIQUE / CHECK constraint names: `PK__<table>__<hex>` / `UQ__...` / `CK__<table>__[col__]<hex>`; the 16-hex suffix is a deterministic FNV-1a hash, not SQL Server's object-id-derived hex (stable across runs but won't byte-match).
- **DELETE / UPDATE leak page space**: deleted (or relocated) row payload bytes stay in their original page until process exit; only the slot is tombstoned. Slot directory entries also never reused.
- **DELETE / UPDATE leak LOB chains**: orphaned LOB chains stay in `Heap.LobPages`. Other rows reference LOB pages by stable index, so list compaction would corrupt them.
- **Mass-shift UPDATE on a unique key**: `UPDATE t SET k = k + 1` where k is PK/UNIQUE may spuriously raise Msg 2627 — the two-phase validator compares each affected row's new key against other affected rows' new keys, so post-shift values overlapping pre-shift values trigger a false positive.
- **`GetBytes`/`GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Behavior matches per-call observation; the streaming-memory guarantee doesn't.
