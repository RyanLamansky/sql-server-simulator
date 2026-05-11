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
`Expression.Run(RuntimeContext runtime)` (runtime) and `Expression.GetSqlType(...)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema. `RuntimeContext` bundles `ResolveColumn` (per-row column lookup) and `Batch` (the executing `BatchContext`); expressions that need batch / session / database state read `runtime.Batch.*` directly. `BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes UNKNOWN. Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

### Context layering
Five scopes, one home each. **Add new state to whichever class matches its true scope** — when in doubt, the rule of thumb is: who outlives whom?

- **`Simulation`** = server / instance. Process-shared system tables (`SystemHeapTables`), `NEWSEQUENTIALID` anchor + counter, the `Databases` dictionary. Public surface (`Simulation` ctor + `CreateDbConnection()`) stays on this class.
- **`Database`** (internal) = one database hosted by the server instance. `Schemas` (named-schema dict, pre-seeded with `dbo`), `CompatibilityLevel`, `VerboseTruncationWarnings`, `rowVersionCounter` (per-DB `@@DBTS`). Every `Simulation` ships with one entry named `Simulation.DefaultDatabaseName` (`"simulated"`); a future `USE <db>` adds entries to the dictionary.
- **`Schema`** (internal) = one namespace inside a database. `HeapTables` (per-schema table dict). Future views / procs / sequences land here too. Schema-qualified references (`SELECT * FROM audit.t`) route through `Database.Schemas["audit"].HeapTables`; unqualified references fall back to `Database.DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session. `CurrentDatabase` pointer, `CurrentTransaction`, `LastIdentity` (`SCOPE_IDENTITY()` / `@@IDENTITY`), `LastStatementRowCount` (`@@ROWCOUNT`), `IdentityInsertTable`, `TraceFlags`, `IsVerboseTruncationActive()`, `TempTables` (per-session `#foo` dictionary, cleared on `Dispose`).
- **`BatchContext`** (internal, in `Parser/`) = one command execution. Owns the `ParserContext` (parse-time-only scratch — `Token`, `AggregateCollector`, `WindowCollector`, `OuterTypeResolver`, `CteBindings`, `InDefaultClause`, `AllowsWindowExpressions`) and holds batch-lifetime runtime state: `Variables`, `CurrentUndoLog`, plus the per-statement frame `CurrentStatement`. Exposes `TryResolveTable(MultiPartName)` — the routing rule that dispatches `#foo` leaves to `Connection.TempTables` regardless of qualifier; everything else routes through the named schema (or `dbo` for an unqualified reference), with `SystemHeapTables` reachable only as a flat 1-part fallback. `TryResolveSchema(MultiPartName)` exposes the dict-bearing schema for CREATE / DROP / TRUNCATE / SELECT INTO. `ParseObjectName(ParserContext)` parses the 1–4-segment dotted form, leaves cursor on the last name segment (standard parser contract), and compresses empty middle segments (so `tempdb..#foo` returns a 2-part name). Threaded explicitly into every `Expression.Run(RuntimeContext runtime)` call via `runtime.Batch`.
- **`StatementContext`** (internal, in `Parser/`) = the dispatch loop's per-statement frame. Allocated once per batch and overwritten in place at the top of each iteration; holds `UtcNow` (the per-statement-freeze the time scalars read). Stored-proc EXEC / TRY-CATCH frames slot in here when added.

**Don't stack misfit state into these buckets unthinkingly**: when adding fields, ask which scope it actually belongs to. If none fits, that's the signal to introduce the missing scope rather than squat on a neighbor. Multi-database is structurally supported but exercised only by the default entry; `USE <db>` is the trigger to populate the dictionary properly.

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
- CASE: searched + simple. UNKNOWN excludes (matches WHERE); simple-form `CASE NULL WHEN NULL` falls through. Result type from `SqlType.Promote` over THEN/ELSE. **Msg 8133** fires at parse when every result expression (every THEN body + the explicit ELSE if present; an absent ELSE counts as implicit bare NULL) is a bare `NULL` literal — `Expression.IsBareNullLiteral` unwraps `Parenthesized` so `(NULL)` still trips. A single typed branch (e.g. `CAST(NULL AS int)`) satisfies the rule. `IIF` enforces the same check on its two value arms (real SQL Server desugars IIF to CASE).
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

**Trig family** (`SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN`/`ATN2`/`COT`/`PI`/`SQUARE`) always returns `float`. Domain errors → Msg 3623 (including `ATN2(0, 0)`, which diverges from .NET's `Math.Atan2(0, 0) = 0`). Wrong arg count → Msg 174 (`"The {lower-name} function requires {N} argument(s)."`) — `pi(1)` raises Msg 174 not Msg 102.

**`DEGREES`/`RADIANS`** are type-preserving with one tweak: `decimal(p, s)` widens to `decimal(38, max(s, 18))` rather than preserving. Integer arm truncates toward zero; out-of-range integer results raise Msg 8115 with the family name. Decimal arm uses a 28-digit `DecimalPi` constant in evaluation order `(input * 180m) / DecimalPi` for trailing-digit fidelity. .NET decimal's 28-digit precision cap means scale > 28 results land at scale 28.

### Date scalar functions: `DATEPART` / `DATEADD` / `DATEDIFF` / `DATEDIFF_BIG`
All take a bare datepart keyword. Result types: `DATEPART` → int; `DATEADD` preserves input type; `DATEDIFF` → int; `DATEDIFF_BIG` → bigint.

`DATEPART`/`DATEADD` enforce per-type keyword compatibility: `date` accepts only date parts; `time(N)` only time parts; `datetime`/`smalldatetime`/`datetime2(N)` accept both; `datetimeoffset(N)` adds `tzoffset`. Wrong combination → Msg 9810. `DATEADD` overflow → Msg 517. `DATEPART(weekday)` uses default `DATEFIRST 7` (Sunday=1); changing `DATEFIRST` not modeled.

`DATEDIFF`/`DATEDIFF_BIG` count `datepart`-unit *boundaries crossed*, not elapsed time — `datediff(year, '2023-12-31', '2024-01-01')` = 1. More permissive than DATEPART/DATEADD: every datepart works against every date/time-family combo. Only `tzoffset` and `iso_week` are rejected unconditionally → Msg 9806. String literals implicitly cast to `datetime2(7)`. `datetimeoffset` operands compare via UTC instant. Result-width overflow → Msg 535.

Unknown keyword → Msg 155 with the calling function's lowercase name embedded. NULL on any operand → typed NULL.

### Current-time scalars
Result types: `GETDATE`/`GETUTCDATE`/`CURRENT_TIMESTAMP` → `datetime`; `SYSDATETIME`/`SYSUTCDATETIME` → `datetime2(7)`; `SYSDATETIMEOFFSET` → `datetimeoffset(7)`. EF emits these from `DateTime.UtcNow`/`Now`/`DateTimeOffset.UtcNow` and `HasDefaultValueSql("getutcdate()")`.

**Per-statement freeze**: two `SYSDATETIME()` calls in one SELECT return identical values; an UPDATE that stamps every row writes the same value; successive SELECTs in one batch DO advance. Captured once per statement-loop iteration into `BatchContext.CurrentStatement.UtcNow`.

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
Six builders (`DATE`/`DATETIME`/`DATETIME2`/`DATETIMEOFFSET`/`SMALLDATETIME`/`TIME` + `FROMPARTS`). Shared shape: NULL on any non-precision arg propagates; non-int operands coerce through CAST; out-of-range → Msg 289 with type-specific State (1=date, 2=time, 3=datetime, 5=datetime2, 6=datetimeoffset). Variable-precision builders (`datetime2`/`datetimeoffset`/`time`) take the precision as a constant-foldable expression — column refs → Msg 10760; out-of-`[0, 7]` → Msg 1002.

Per-builder quirks: `DATETIMEFROMPARTS` ms 999 + h23:m59:s59 rolls to next day (1/300s tick rounding); `DATETIMEOFFSETFROMPARTS` enforces sign-consistency between hour/minute_offset (mixed → Msg 289 St 6) and |offset| ≤ 14:00. `EOMONTH(start_date [, month_offset])` always returns `date` and silently treats NULL `month_offset` as zero (NULL `start_date` propagates normally).

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
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate. Inline column-level CHECK predicates may only reference their owning column — peer references raise **Msg 8141** at CREATE TABLE (probe-confirmed verbatim wording). The walker is structural via `Expression.VisitColumnReferences` + `BooleanExpression.VisitOperandExpressions`; coverage is currently limited to common container subclasses (`Reference`, `Parenthesized`, `TwoSidedExpression`, `Cast`, `Length`) — peer refs buried in less-common containers (`DATEPART`, `SUBSTRING`, nested `CASE`, etc.) silently escape the CREATE-TABLE check and surface at INSERT instead. Table-level CHECK has no peer restriction.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree.

### Transactions
Three entry points share one per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).

- **Statement-level atomicity**: a single mutation throwing mid-execution rolls back its partial writes. Multi-row INSERT failing on row 3 leaves zero rows.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` actually commits; `ROLLBACK` zeroes `TranCount` and walks the entire log. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx. Parallel `BeginTransaction` → `InvalidOperationException`. `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both keep advancing through rollback. Orphaned LOB chains for rolled-back inserts also leak.
- **Temp-table CREATE/DROP participates in the log** via `TempTableCreation` / `TempTableRemoval` `UndoEntry` subtypes (rollback removes from / restores into the connection's `TempTables` dict). Regular CREATE/DROP TABLE is NOT logged — see the corresponding quirk.
- No isolation: uncommitted writes immediately visible to all readers (single-Simulation, single-thread-at-a-time).

### UPDATE / DELETE
- Bare `UPDATE table SET ... [WHERE]` and `DELETE [FROM] table [WHERE]`.
- Multi-table syntax (`UPDATE alias SET ... FROM <sources> [WHERE]`, `DELETE FROM alias FROM <sources> [WHERE]`) — the EF7+ `ExecuteUpdate`/`ExecuteDelete` shape. Target identified by leading-identifier match against each source's `FromSource.Qualifier`; missing match → Msg 208.
- **Joined UPDATE/DELETE: each unique target row processed exactly once.** When the same target matches multiple join tuples, SQL Server uses the *first* matching tuple's RHS for SET. The simulator dedupes by `(page, slot)` via a side-channel byte[]→address map. LEFT JOIN with no right-side match still surfaces the target (RHS sees NULL).
- **OUTPUT** supported only when the leading identifier resolves to a real table name; OUTPUT + alias-form multi-source → `NotSupportedException` (EF doesn't combine those).
- **Multi-column SET evaluates RHS against pre-update snapshot** — `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` → `(a=100, b=11)`. Scalar subquery RHS sees pre-update state.
- Identity update → Msg 8102. Computed update → Msg 271. Rowversion update → Msg 272. Per-row constraint re-validation: NOT NULL → Msg 515 ("UPDATE fails."); CHECK → Msg 547 ("UPDATE statement"); PK/UNIQUE → Msg 2627 (verbatim "Cannot insert duplicate key" wording even on UPDATE — SQL Server quirk). PK/UNIQUE validation runs against the post-update virtual state, so mass-shift on a unique key can false-positive (see Quirks).
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

**`@@ROWCOUNT`**: tracks the most-recently-completed statement's row count via `SimulatedDbConnection.LastStatementRowCount`. SELECT row counts populate after the dispatch materializes rows up-front (so the next statement in the batch sees the final count); DML mutations write their affected count; `SET` / `DECLARE @v = init` write 1; bare `DECLARE @v` (no initializer) preserves the prior count; transaction / DDL statements reset to 0.

**`@@ERROR`**: error number of the most-recently-completed statement; `int`. **Always 0 in the simulator** because TRY/CATCH isn't modeled — every `SimulatedSqlException` propagates out of the dispatch loop and terminates the batch, so only successful statements ever complete. Straight-line scripts that read `@@ERROR` after a known-good statement get correct behavior; scripts that wrap a statement-terminating-only error in `TRY ... CATCH` and expect to observe the number won't until TRY/CATCH lands — at which point `LastErrorExpression` becomes the natural home for live tracking on `BatchContext`.

**Compound assignment** (`SET @v += expr` etc.) and **table variables** (`DECLARE @t TABLE (...)`) aren't modeled — rewrite as `SET @v = @v + expr` for the former; the latter is a separate bundle.

### Common table expressions
`WITH name [(col, …)] AS (SELECT …) [, …] {SELECT|INSERT|UPDATE|DELETE|MERGE} …`. WITH prefix scopes to exactly one immediately-following statement. Both non-recursive and recursive forms modeled.

CTE name shadows a real table for the prefixed statement. Multiple comma-separated CTEs cascade — later ones see earlier ones.

**Recursive form**: anchor branches (no self-ref) run once into a seed rowset; recursive branches (one self-ref each) iterate against the previous rowset until empty or `MaxRecursion` trips Msg 530. Default 100; `OPTION (MAXRECURSION N)` overrides; `0` disables.

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

OPENJSON WITH-clause types: `int`/`bigint`/`decimal(p,s)`/`float`/`bit`/`nvarchar(N|max)`/`varchar(N)`/`date`/`datetime2(N)`/`datetimeoffset(N)`/`uniqueidentifier`. Coercion via `SqlValue.CoerceTo`. Backed by `System.Text.Json`. JSON-path quoted-property escape `""` → literal `"`.

Not emitted by EF / not modeled: `JSON_QUERY`, `ISJSON`, `FOR JSON PATH`/`AUTO`. Reachable only via raw SQL.

### MERGE / OUTPUT (EF SaveChanges shape only)
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING (VALUES ...) AS alias (cols) ON predicate WHEN NOT MATCHED THEN INSERT ... [OUTPUT ...]` (multi-row batch).
- `WHEN MATCHED` parses but throws `NotSupportedException` if its predicate ever evaluates true.

### `SELECT … INTO target`
Creates a destination table from the projection's inferred schema, then copies rows in. Target routes by `#`-prefix: `#foo` lands in the per-connection `TempTables` dict (same as `CREATE TABLE #foo`); regular names land in the current database's `HeapTables`. Probe-confirmed schema-inference rules (2026-05-11):

- **Nullability**: direct column refs preserve source nullability. Integer arithmetic, `CAST`, `COALESCE`, aggregates (incl. `COUNT`), and bare `NULL` literal all project as **nullable**. `ISNULL(x, y)` is **non-null when either arg is non-null** (asymmetric with COALESCE). `CASE` is non-null when every `THEN` branch is non-null AND the `ELSE` branch is non-null (no-`ELSE` = implicit `ELSE NULL` = nullable). Non-NULL literals are non-null. String `+` should also project non-null when both operands non-null, but the simulator's runtime-dispatch design (Add can be arithmetic or concat depending on operand types) makes static analysis impractical — projects as nullable (minor fidelity gap; staging tables rarely depend on this).
- **Identity propagation**: only when the projection is a *direct column ref* (a `Reference`, possibly wrapped in `NamedExpression` for `AS alias`) AND the FROM clause is exactly one source with a `BackingTable` (a real heap, not a derived table / CTE / OPENJSON) AND no joins. WHERE/TOP/ORDER BY preserve. Any join, set-op, expression wrapping, or CTE drops it. Destination's `IdentityState` starts fresh with the source's seed+increment and tracks the copied values via `ObserveExplicit`.
- **Implementation**: `Selection.IntoTarget` + `Selection.DestColumnSchema` (a `HeapColumn[]`) are captured at parse time inside `ParseInner` and propagated through `CombineSetOps` / `ApplyTopLevelOrderBy`. `Simulation.SelectInto.cs:ExecuteSelectInto` creates the heap table, runs the Selection, encodes each row through `RowEncoder.EncodeRow`, appends to the dest's heap, and tracks the active transaction's undo log so a `ROLLBACK` unwinds both the table creation (for temp tables) and the row writes.
- **Schema rules + validation** live in `Selection.SelectInto.cs:ComputeIntoDestSchema`. Nullability uses `Expression.ResultIsNullable` (a new virtual override on `Value` / `Reference` / `NamedExpression` / `IsNullExpression` / `CaseExpression`; default `true` for everything else). Identity uses `UnwrapDirectRef` to drill through `NamedExpression` layers.
- **Errors**: unnamed projection → **Msg 1038 Cl 15 St 5** (`SelectIntoMissingColumnName`); duplicate column name in projection → **Msg 2705 Cl 16 St 3** (`DuplicateColumnInSelectInto`, names the target table); target already exists → **Msg 2714** (reused factory); `##` global target → `NotSupportedException`.
- **INTO + UNION**: real SQL Server allows `SELECT … INTO #t FROM a UNION ALL SELECT … FROM b` (INTO on first branch). The simulator parses this, propagates `IntoTarget` from the left branch through `CombineSetOps`, and strips identity on the combined dest schema. A right branch carrying its own INTO → Msg 156 (`Incorrect syntax near the keyword 'into'.`).
- **INTO without FROM** works (`SELECT 1 AS x INTO #t`) — synthesized-row path threads `IntoTarget` through.
- **Quirk**: CTE-wrapped single-heap source drops identity and nullability — the simulator's CTE bindings synthesize `HeapColumn` entries with `nullable: true` and no identity, so the analyzer can't peer through. Real SQL Server propagates both. Fix would require propagating column metadata through CTE bindings; future bundle.

### T-SQL control flow: `IF` / `BEGIN…END` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN`
`IF <boolean-expr> <stmt> [ELSE <stmt>]`, `BEGIN <stmt>+ END` compound blocks, `WHILE <boolean-expr> <stmt>` loops with `BREAK` / `CONTINUE`, bare `RETURN` for batch-level early-exit. GOTO/labels, TRY/CATCH, and the value-form `RETURN N` (stored-proc / function scope) still aren't modeled. Probed against SQL Server 2025 (2026-05-11).

- **Body grammar**: exactly one statement. The famous T-SQL footgun `IF cond SELECT 'a' SELECT 'b'` runs *both* SELECTs — only the first is the IF body; the second escapes the IF as a subsequent batch-level statement. Replicated.
- **Dangling-else binds to the inner IF** (standard rule). `IF 1=0 IF 1=1 stmt ELSE stmt` → outer skips the entire inner-IF including its ELSE; no output.
- **Cond must be a Boolean predicate** (`BooleanExpression`). Bare values raise **Msg 4145** (`"An expression of non-boolean type specified in a context where a condition is expected, near 'X'"`): `IF 1`, `IF NULL`, `IF 'abc'`, `IF (cast(null as bit))` — bit is *not* boolean in SQL Server's static type check. Implemented by changing `BooleanExpression.ParseComparison`'s default (atom-without-comparison-op) case from Msg 102 to Msg 4145 (cross-cuts WHERE / HAVING / ON / CHECK too; probe-confirmed they share the wording). Slight positional gap on paren-wrapped value cases (`IF (1) select` — simulator reports "near ')'" where SQL Server reports "near 'select'") — wording correct, near-token off by one.
- **Three-valued cond**: only an explicit `true` takes THEN; both `false` and UNKNOWN go to ELSE (`IF 1 = null …` → ELSE).
- **`BEGIN` disambiguation**: peek the token after `BEGIN`. `TRAN`/`TRANSACTION` → existing `TryParseBeginTransaction`. `DISTRIBUTED` → `NotSupportedException` (no DTC). `TRY`/`ATOMIC` → `NotSupportedException` (TRY/CATCH and natively-compiled SP atomic blocks). Everything else → compound block. Implemented via `ParserContext.SaveCheckpoint`/`RestoreCheckpoint` so the transaction-start case re-parses through the unchanged `TryParseBeginTransaction` path.
- **Empty `BEGIN END`** (and `BEGIN ; END` with only separators) → **Msg 102** near `'end'`. Variables declared inside a block are batch-scoped, not block-scoped (visible after `END`) — matches existing batch-scope model on `BatchContext.Variables`.
- **`@@ROWCOUNT`**: an IF that ran no branch (cond false, no ELSE) resets `@@ROWCOUNT` to 0. An IF whose body ran lets the body's last statement set `@@ROWCOUNT` normally. Probe-confirmed.

**WHILE specifics**: `BooleanExpression.Parse` for cond (Msg 4145 on non-boolean, same path as IF). `ParserContext.SaveCheckpoint` captures the body-start; `RestoreCheckpoint` before each iteration so the body re-parses from scratch (variable references hold live `VariableSlot` references, so cond / body mutations between iterations are visible). After every exit path (cond initially false, cond goes false mid-loop, BREAK) `@@ROWCOUNT` resets to 0 — probe-confirmed, independent of what the body's last statement produced. Empty `BEGIN END` body raises Msg 102 (same rule as IF). One-statement-body footgun: `WHILE @i<2 set @i=@i+1 select @i` — the `SELECT` is *not* part of the body; it runs once after the loop exits.

**BREAK / CONTINUE — flag-based, not exception-based.** `BatchContext.LoopControl` enum (`None` / `Break` / `Continue`). The BREAK parser sets it to `Break`; the CONTINUE parser sets it to `Continue`. The innermost WHILE consumes and clears the flag. The `IsSkipping` property OR's the flag into the skip predicate, so subsequent statements in the body block naturally no-op (`set @sum = @sum + 100;` after a `BREAK` doesn't run — probe-confirmed). Nested loops work because each WHILE clears its own flag before returning to its caller, so the outer never sees the inner's break/continue. This composes cleanly with iterator-based dispatch in a way exception-based signaling doesn't — see `feedback_no_exceptions_for_control_flow.md`.

**BREAK / CONTINUE outside a loop** raises **Msg 135** / **Msg 136** verbatim: `"Cannot use a BREAK statement outside the scope of a WHILE statement."` / `"Cannot use a CONTINUE statement outside the scope of a WHILE statement."`. The check on `BatchContext.LoopDepth == 0` fires *unconditionally* — real SQL Server applies the loop-scope check at compile time, so the simulator does too. **This is distinct from the Q15 un-taken-branch fidelity gap**: `IF 1=0 BREAK` at batch top level fires Msg 135 even though the branch is un-taken. Inside a real WHILE, `LoopDepth > 0` lets BREAK in an un-taken IF body just no-op (because the `!IsSkipping` gate on the flag *write* prevents the actual control transfer).

**Iteration cap** — simulator-only safety net at `BatchContext.LoopIterationLimit = 100_000` total iterations per batch. Real SQL Server has no such cap (timeouts handle runaway loops). The simulator throws `InvalidOperationException` so a buggy test doesn't hang CI.

**`LoopDepth` is bumped unconditionally** (even when the WHILE itself is in skip mode) so BREAK / CONTINUE inside the body — including inside un-taken IF branches — never see Msg 135 / 136 fire incorrectly. The flag-write gate (`!IsSkipping`) handles the runtime "BREAK in skipped-IF inside WHILE" case.

**`RETURN` — bare-form only, batch-exit propagation.** Set `BatchContext.ReturnSignaled = true` (gated on `!IsSkipping`); `IsSkipping` OR's it in, and the dispatch loop's `DispatchStatementsUntil` checks the flag at the top of every iteration to `yield break`. The WHILE iteration loop checks after every body dispatch (RETURN propagates *through* WHILE — only `BREAK` / `CONTINUE` are caught by the innermost loop). `ParseBeginBlock` short-circuits its "expect END" check when the flag is set, since RETURN may fire mid-block before the cursor reaches END. End result: bare RETURN exits the entire batch — through any nesting of IF / BEGIN…END / WHILE — and any code after it (including `SELECT 'after'` follow-ups or unreached `END` terminators) never executes.

**`RETURN <value>` raises Msg 178** verbatim (`"A RETURN statement with a return value cannot be used in this context."`) at parse time, regardless of skip mode — the value form is reserved for stored procedures and scalar functions, neither of which is modeled yet. Compile-time check (same pattern as BREAK Msg 135): `IF 1=0 RETURN 5` raises Msg 178 even though the branch is un-taken. The simulator detects "value follows" via `IsStatementBoundary(context.Token)` — any non-boundary token after RETURN (operators, variables, literals, parens, non-statement-start keywords) triggers Msg 178; boundary tokens (`;`, EOB, statement-start keywords like SELECT/INSERT/IF/etc.) leave RETURN bare.

**Un-taken-branch skip mode** (`BatchContext.SkipModeFlag` + `IsSkipping` computed property). The IF parser sets `SkipModeFlag` around dispatch of the un-taken branch (THEN if cond false, ELSE if cond true), then restores in a `finally`. `IsSkipping = SkipModeFlag || LoopControl != None || ReturnSignaled` is the combined predicate every statement parser reads. Skip-mode propagates through nested IF/BEGIN/WHILE automatically — a nested IF inside a skipped block reads `IsSkipping=true`, short-circuits cond eval entirely (so a divide-by-zero inside un-evaluated cond doesn't fire), and dispatches both its branches in skip mode. A WHILE in skip mode never iterates; it skip-dispatches its body once to advance the cursor and exits.

Each statement parser still runs its full parse — the cursor advances normally, names resolve, expressions parse — but gates its state mutation on `!batch.IsSkipping`. Touchpoints: SELECT `Execute` call in the dispatch, `ProcessHeapInsert`'s heap insert + `LastIdentity` update, `CommitUpdate`'s heap delete+insert, `CommitDelete`'s heap delete, MERGE's INSERT branch, `TryParseCreate`'s dict add + Msg 2714 existence check, `DropOneTable`'s lookup + Msg 3701 + dict remove, `ExecuteSelectInto`'s create + bulk-insert, `TryParseSetVariable`'s `slot.Value =` (plus its RHS evaluation), `TryParseSetIdentityInsert`'s state change, `TryParseDeclare`'s dict add + Msg 134 duplicate check + initializer evaluation, `TryParseBeginTransaction` / `TryParseCommit` / `TryParseRollbackTransaction` / `TryParseSavepoint`'s state changes (including their no-active-tx error checks), `TryParseAlter`'s database property write, `TryParseDbcc`'s trace-flag mutation. The dispatch loop also suppresses `yield return` for SELECT results and the `LastStatementRowCount` update on skipped statements.

**Dispatch refactor**: extracted `DispatchOneStatement(batch, requireSemicolonBeforeCte)` and `DispatchStatementsUntil(batch, endKeyword)` from `CreateResultSetsForCommand`. The top-level loop calls `DispatchStatementsUntil(null)`; `ParseBeginBlock` calls `DispatchStatementsUntil(Keyword.End)`; `ParseIfStatement` and `ParseWhileStatement` call `DispatchOneStatement` directly (the body of each is exactly one statement). `IsStatementBoundary` includes `If` / `Else` / `End` / `While` / `Break` / `Continue` / `Return` so the cursor-normalization at the end of each dispatch correctly recognizes nested-control terminators. `Selection.ParseInner`'s projection-list terminator switch lists the same set plus `Drop` so `SELECT ... ELSE` / `SELECT ... END` / `SELECT ... BREAK` / etc. correctly stop at the keyword instead of throwing Msg 102.

**Fidelity gap** (Q15): real SQL Server defers name resolution for un-taken branches — `IF 1=0 SELECT bad_col FROM bad_table` runs silently. The simulator's parsers do name resolution inline with parsing, so un-taken branches with non-existent table/column refs still raise Msg 208 / Msg 207. Common idioms (safe-CREATE / safe-DROP / safe-INSERT against pre-existing tables) work end-to-end because referenced names exist when the branch is skipped.

### `PRINT`
`PRINT <expression>` parses + evaluates the operand and **discards the result** — no `InfoMessage` event on `SimulatedDbConnection` (DbConnection doesn't define one, and no application has needed PRINT output observation yet). The evaluation isn't a no-op: operand-side errors still surface — `PRINT 'val=' + 5` raises Msg 245 because the `+` operator's int-side promotion tries to parse `'val='` as int (probe-confirmed against SQL Server 2025).

Probe-confirmed semantics (2026-05-11) the simulator handles correctly because evaluation runs unchanged:
- `PRINT NULL` and `PRINT ''` are silent no-ops (no message body, no error).
- `PRINT` resets `@@ROWCOUNT` to 0 — applied by the dispatcher after the parser returns.
- Skip-mode (un-taken IF, after BREAK / CONTINUE / RETURN) suppresses operand evaluation entirely, so an error-bearing operand in a skipped branch doesn't fire. Standard pattern: parse the expression unconditionally to advance the cursor, then gate `expression.Run` on `!batch.IsSkipping`.
- Rollback doesn't undo a PRINT (real SQL Server's InfoMessage stream is non-transactional too); orthogonal to the simulator's discard-everything design.

**Fidelity gaps** (modeled deviations from probed behavior):
- Real SQL Server's PRINT truncates messages at 8000 chars (varchar) / 4000 chars (nvarchar). Simulator: no truncation modeled (output is discarded).
- Real SQL Server raises **Msg 1046** ("Subqueries are not allowed in this context. Only scalar expressions are allowed.") for `PRINT (SELECT 'inner')`. The simulator silently evaluates the scalar subquery — Msg 1046 isn't modeled.

### `WAITFOR DELAY`
`WAITFOR DELAY '<time>'` and `WAITFOR DELAY @variable` block the calling thread via `Thread.Sleep(TimeSpan)`, matching real SQL Server's "blocks the connection" semantics. Operand grammar is strict (matches probe of SQL Server 2025, 2026-05-11): only a varchar/nvarchar string literal or an `@-variable` reference. `cast(...)`, integer literal, bare `NULL` literal all fail at parse (Msg 102/156); `time`-typed variable raises **Msg 9815** (`"Waitfor delay and waitfor time cannot be of type time."` — note SQL Server reserves the operand slot for *string-typed* values, not its own `time` type). Empty string and NULL-valued variable both silently succeed as zero delay. Bad-format string raises **Msg 148** with the offending value embedded. `@@ROWCOUNT` resets to 0. Skip-mode suppresses the sleep entirely (an `IF 1=0 WAITFOR DELAY '00:00:10'` returns instantly). **`WAITFOR TIME`** (absolute-time wait) raises `NotSupportedException` — scheduling-style primitive not yet needed. **Cancellation gap**: an `ExecuteReaderAsync` caller's `CancellationToken` isn't threaded into the sleep; the thread blocks until the duration elapses regardless.

### `TRUNCATE TABLE`
`TRUNCATE TABLE <name>` empties the heap (clears `Pages` / `LobPages`) and resets every identity column's high-water mark to its declared seed — probe-confirmed against SQL Server 2025 (2026-05-11) that a subsequent INSERT receives the seed, not the next-after-prior-max. Routing reuses the same `#`-prefix dispatch as DROP TABLE (`#foo` → connection's `TempTables`; else the named schema's heap-table dict via `Database.Schemas`). Missing target raises **Msg 4701** (`"Cannot find the object \"X\" because it does not exist or you do not have permissions."` — note this is distinct from DROP's Msg 3701 and from generic INSERT/UPDATE/DELETE's Msg 208; TRUNCATE has its own error path, and its wording carries **only the leaf** of a multi-part name — probe-confirmed asymmetric with 208 / 3701 which embed the full qualifier). `@@ROWCOUNT` resets to 0. Skip-mode suppresses the entire action including name resolution. `WHERE` clause fails at parse (Msg 102 — TRUNCATE doesn't accept predicates).

**Transactional rollback is supported** via a single `HeapTruncation` undo entry that snapshots the pre-truncate `Pages` / `LobPages` lists AND each identity column's pre-truncate high-water mark. `BEGIN TRAN; TRUNCATE; ROLLBACK` restores both — including the identity counter. **This diverges from the simulator's general "identity counters bypass the undo log" rule, which is INSERT-only**: INSERT-advanced counters stay advanced through rollback (probe-confirmed for real SQL Server too); TRUNCATE's explicit reset action gets undone on rollback. Outside an explicit transaction, TRUNCATE commits immediately (no log entry — same pattern as DROP TABLE on regular tables).

### Schemas (`CREATE SCHEMA` + schema-qualified resolution)
`CREATE SCHEMA <name>` adds an entry to `Database.Schemas`; subsequent two-part references (`SELECT * FROM audit.t`, `INSERT audit.t VALUES (…)`, every DML / DDL targeting a table) route through it. Unqualified references fall back to `Database.DefaultSchemaName` (`"dbo"`), which every `Database` ships pre-populated with. The 9 table-lookup sites (Selection FROM, Insert/Update/Delete/Merge targets, CREATE / DROP / TRUNCATE, SET IDENTITY_INSERT, IDENT_CURRENT, SELECT INTO) all share one parser (`BatchContext.ParseObjectName`) and one resolver pair (`BatchContext.TryResolveTable` for lookup, `BatchContext.TryResolveSchema` for CREATE-shape callsites that need the dict). Every `Database` ships with three pre-populated schemas at conventional ids: `dbo=1`, `INFORMATION_SCHEMA=3`, `sys=4`. User schemas allocate ids starting at 5 from `Database.AllocateSchemaId()` (a counter seeded so the next-allocated value is 5). Probed against SQL Server 2025 (2026-05-11).

- **Duplicate `CREATE SCHEMA`** (case-insensitive) → **Msg 2714** (`"There is already an object named '<n>' in the database."` — same factory as duplicate CREATE TABLE; SQL Server shares the namespace).
- **Reserved schema names** (`dbo`, `sys`, `INFORMATION_SCHEMA`) → **Msg 2760** (`"The specified schema name \"<n>\" either does not exist or you do not have permission to use it."`). Wording is quirky for a CREATE (says "does not exist"), but probe-confirmed verbatim — real SQL Server resolves the principal first and these schemas tie to system principals.
- **Three-part `db.schema.t`** validates the db segment against `CurrentDatabase.Name` (case-insensitive); mismatch → Msg 208. **Four-part `server.db.schema.t`** always returns false from `TryResolveSchema` (linked-server names aren't modeled — surfaces as Msg 208 / Msg 3701 / NULL from OBJECT_ID per callsite). Empty middle segment (`tempdb..#foo`) is silently compressed by `ParseObjectName`, so a 2-part name pops out — preserves the existing DROP TABLE behavior for temp-table qualifiers.
- **`CREATE TABLE schema.t`** where `schema` doesn't exist → **Msg 2760** (target schema for the create must already exist). Distinct from FROM / INSERT / UPDATE / DELETE / MERGE / DROP / TRUNCATE access which use 208 / 3701 / 4701 respectively.
- **`AUTHORIZATION owner`** and the embedded `<schema_element>` list (CREATE TABLE / VIEW / GRANT nested inside CREATE SCHEMA) aren't modeled — `AUTHORIZATION` raises `NotSupportedException`; trailing statement-starting tokens (CREATE / SELECT / INSERT / etc.) parse as their own statements in the same batch (deviates from real SQL Server's strict greedy-consume but reaches the same end state for the common idiom).
- **No "first in batch" enforcement** — real SQL Server raises Msg 111 if CREATE SCHEMA isn't the first statement, tied to its greedy schema_element grammar. The simulator's dispatch already treats each statement as independent, so CREATE SCHEMA in any position works.
- **`sys` and `INFORMATION_SCHEMA` host catalog views** (sys.schemas / sys.tables / sys.objects / sys.columns / INFORMATION_SCHEMA.TABLES / .COLUMNS / .SCHEMATA — see the dedicated section below). Adding a user table via `CREATE TABLE sys.foo (…)` raises `NotSupportedException` ("Cannot CREATE TABLE in the built-in 'sys' schema"); same rejection for `INFORMATION_SCHEMA`. Both `Schema` entries exist in `Database.Schemas` to carry their conventional ids and to be reachable from `sys.schemas`, but their `HeapTables` dicts stay empty — catalog views live in a separate `Simulation.CatalogViews` registry.
- **Error wording**: Msg 208 wraps the qualified name in single quotes (`Invalid object name 'badschema.t'.`); Msg 3701 (DROP) does the same; Msg 4701 (TRUNCATE) carries only the leaf (probe-confirmed asymmetric — distinct error path).
- **DROP SCHEMA, ALTER SCHEMA TRANSFER**: not modeled.

### Object identifiers + `OBJECT_ID()`
Every `HeapTable` carries a stable per-database `int ObjectId` assigned at CREATE time from `Database.AllocateObjectId()` (a `Database`-scoped `Interlocked.Increment` counter seeded at 100). DROP-then-recreate yields a fresh id, matching real SQL Server (probe-confirmed 2026-05-11 — counter never reuses values). The counter bypasses transaction rollback: a rolled-back CREATE TABLE still consumed an id, matching the identity-counter rule. System tables (`SystemHeapTables`) carry a sentinel `ObjectId = -1` — they're process-shared, sit outside per-DB id space, and aren't reachable through `OBJECT_ID()` anyway. Backs `OBJECT_ID()` plus `sys.tables` / `sys.objects` / `sys.columns.object_id`.

**`OBJECT_ID(name [, type])`** scalar (`Parser/Expressions/ObjectId.cs`): returns the `int` ObjectId of the named object, or NULL when not found / wrong type / malformed name. The name is a runtime string parsed as a 1–3-part dotted identifier with bracket-quoting (`'[dbo].[foo]'`, `'dbo.foo'`, `'simulated.dbo.foo'` all resolve identically); 4-segment names return NULL (linked-server form unmodeled). The type filter is case-insensitive but whitespace-sensitive — `'U'` and `'u'` match user tables; `' U '`, `'XX'`, `''` all → NULL; other documented codes (`V`/`P`/`F`/`FN`/...) → NULL until those features land. A NULL on any argument propagates NULL.

- **Runtime-evaluated arguments**: `DECLARE @n nvarchar(100) = 'foo'; SELECT OBJECT_ID(@n)` works — both args are full `Expression`s.
- **Temp-table divergence**: `OBJECT_ID('#foo')` resolves the session's `#foo` directly because `BatchContext.TryResolveTable` routes `#` leaves to the connection's temp dict regardless of qualifier. Real SQL Server requires the explicit `tempdb..#foo` three-part form (since unqualified resolution targets the current DB, not tempdb). The simulator's existing temp-routing simplification carries through; `OBJECT_ID('tempdb..#foo')` also works (probe-confirmed real behavior).
- **Bracket-handling fidelity gap**: the runtime-string name parser strips bracket pairs at segment level (`'[dbo].[foo]'` → `dbo`+`foo`) and decodes `]]` → `]` inside brackets — but bracketed segments containing a literal `.` (`'[a.b].[c]'`, the literal-dot case) don't parse correctly (split on `.` happens before bracket-aware tokenization). Rare in practice; revisit if a real app hits it.
- **Arity**: too-few-args (`OBJECT_ID()`) currently surfaces as Msg 102 (the inner Parse failure path) rather than Msg 174 — same pattern as other built-ins; the simulator doesn't enforce min-args. Too-many-args raises Msg 174 verbatim.

### `sys.*` and `INFORMATION_SCHEMA.*` catalog views
`Simulation.CatalogViews` is a process-static dict of virtual catalog-view projections keyed by fully-qualified name (`"sys.tables"`, `"INFORMATION_SCHEMA.COLUMNS"`, etc.) so one resolver serves both namespaces without per-schema dispatch. Each `CatalogView` carries a fixed `HeapColumn[]` schema and a `Func<BatchContext, IEnumerable<SqlValue[]>>` row generator that runs against live `Database` / `Schema` / `HeapTable` metadata; rows aren't cached, so CREATE / DROP / TRUNCATE changes made earlier in the same batch are visible on the next read. The FROM-source parser detects catalog views via `BatchContext.TryResolveCatalogView` (case-insensitive on the qualifier, 2-part or `<currentDb>.qualifier.<view>` 3-part), wraps the view in `Selection.ForCatalogView`, and threads it as the `FromSource.LateralPlan` — so each Execute re-runs the generator. The `RowEncoder.EncodeRow(HeapColumn[], SqlValue[])` overload bridges the SqlValue-array generator output into the byte stream the FromSource consumes.

Shipped views:
- **`sys.schemas`** projects `name sysname`, `schema_id int`, `principal_id int NULL` (always NULL — no principal model). Always lists dbo / INFORMATION_SCHEMA / sys plus every user CREATE SCHEMA addition.
- **`sys.tables`** projects user heap tables only: `object_id`, `name sysname`, `schema_id`, `type char(2)` (always `'U '` — trailing-space padded, probe-confirmed), `type_desc nvarchar(60)` (`USER_TABLE`), `create_date datetime`, `modify_date datetime`, `is_ms_shipped bit` (always 0).
- **`sys.objects`** is the superset: one row per `HeapTable` plus one row per `KeyConstraint` (type `PK` / `UQ`) and `CheckConstraint` (type `C `) with `parent_object_id` linking to the owning table. Constraint object_ids allocate from the same `Database.AllocateObjectId` counter as tables, so PK / UQ / CHECK constraints get globally-unique ids that `sys.objects.object_id` surfaces.
- **`sys.columns`** projects per-column metadata: `object_id`, `name sysname`, `column_id` (1-based), `system_type_id tinyint`, `user_type_id int`, `max_length smallint` (byte-length — `nvarchar(50)→100`, `char(5)→5`, `-1` for the MAX form, `16` for text/ntext/image LOB pointers, `256` for sysname), `precision` / `scale tinyint` (decimal/numeric carry their declared (p,s); date/time fractional types follow `(time(N): 8+N, N)` / `(datetime2(N): 19+N, N)` / `(datetimeoffset(N): 26+N, N)`; 0 for everything else), `is_nullable` / `is_identity` / `is_computed bit`, `collation_name sysname` (set only for string types). Backed by `SqlType.SystemTypeId` (byte-typed switch on `this` matching real SQL Server's `sys.types.system_type_id`) and `SqlType.UserTypeId` (== `SystemTypeId` except `sysname=256`). `system_type_id` covers the 22 base types modeled.
- **`INFORMATION_SCHEMA.TABLES`** (4 cols): TABLE_CATALOG / TABLE_SCHEMA / TABLE_NAME / TABLE_TYPE. TABLE_TYPE is `'BASE TABLE'` for every user table (views not yet modeled).
- **`INFORMATION_SCHEMA.COLUMNS`** (full 23-col ISO shape): the always-NULL columns (DOMAIN_*, CHARACTER_SET_SCHEMA, COLLATION_CATALOG, etc.) ship anyway since tooling does `SELECT *`. IS_NULLABLE is `varchar(3)` `'YES'`/`'NO'` (not bit). CHARACTER_MAXIMUM_LENGTH is declared **chars** (`nvarchar(50)→50`); CHARACTER_OCTET_LENGTH is **bytes** (`nvarchar(50)→100`). Text-family sentinels: text/image = `2147483647`; ntext = `1073741823` chars / `2147483646` bytes. NUMERIC_PRECISION_RADIX is 10 for integer/decimal/money, 2 for float/real; NUMERIC_SCALE is NULL for float/real, otherwise the actual scale. DATETIME_PRECISION carries the fractional-seconds digit count (0 for date/smalldatetime, 3 for datetime, N for datetime2/time/datetimeoffset). CHARACTER_SET_NAME: `'UNICODE'` for nvarchar/nchar/ntext/sysname; `'iso_1'` for varchar/char/text; NULL for binary/varbinary/image.
- **`INFORMATION_SCHEMA.SCHEMATA`** (6 cols): only the schemas the simulator models (no role-principal padding — real SQL Server lists 13 schemas because of `db_owner`/`db_datareader`/etc., and we have no principal model). SCHEMA_OWNER mirrors SCHEMA_NAME. DEFAULT_CHARACTER_SET_NAME is `'iso_1'`.
- **`SCHEMA_ID([name])`** scalar: no-arg returns `Database.DboSchemaId` (=1) — the simulator's "caller default schema" (no user model means dbo is universal). With an arg, returns the schema's id or NULL.

Cross-cutting notes:
- **Column subset (sys.* only)**: real SQL Server's `sys.tables` / `sys.objects` / `sys.columns` have 30+ columns each; the simulator ships the load-bearing subset that EF / migration tooling and the probe queried. `SELECT *` returns fewer columns than real SQL Server — apps that depend on a specific full-column shape will surface gaps, address those as needed. INFORMATION_SCHEMA views ship the full ISO column set.
- **Temp tables not in `sys.tables` / `INFORMATION_SCHEMA.TABLES`**: the per-connection `TempTables` dict isn't walked by the row generators (real SQL Server lists temp tables in `tempdb.sys.tables`, which the simulator's single-database model doesn't separate). Catalog views show user tables in `dbo` + any user schema only.
- **No write paths**: `INSERT sys.tables …` / `UPDATE sys.tables …` / `DROP TABLE INFORMATION_SCHEMA.COLUMNS` etc. all raise Msg 208 — catalog views aren't in `Schema.HeapTables`, so the regular table-lookup miss path fires.
- **Constraint object_ids**: `KeyConstraint.ObjectId` and `CheckConstraint.ObjectId` are now allocated at CREATE TABLE alongside the table's own id (via `Database.AllocateObjectId()`). The order is: schema resolution → allocate constraint ids (inside `ResolveKeyConstraints` / `ResolveCheckConstraints`) → allocate table id → construct `HeapTable`. The constraint resolvers take a `Database` parameter to thread the allocation.
- **`COLUMN_DEFAULT` always NULL** (fidelity gap): real SQL Server renders default expressions as parenthesized text (`(sysdatetime())`). Serializing arbitrary `Expression`s back to SQL is a separate (sizable) bundle; the column ships as NULL until that lands.
- **`precision` is a reserved keyword in the simulator's parser**: `select precision from sys.columns` raises Msg 102; bracket it (`[precision]`) or alias it. Real SQL Server accepts the bare name. Minor fidelity gap — fix would loosen `Keyword.Precision` to a contextual-keyword classification.

### Local temp tables (`#foo`)
Per-connection `Dictionary<string, HeapTable> TempTables` on `SimulatedDbConnection`; routed by `BatchContext.TryResolveTable` (`#`-prefix leaf → connection dict, ignoring any qualifier; else the named schema's heap-table dict + flat `SystemHeapTables`). Auto-cleared on `Dispose`, matching real SQL Server's session-close drop. Lifecycle, cross-conn isolation, and Msg 208 from other sessions all probe-confirmed against SQL Server 2025.

- **`CREATE TABLE #foo`** reuses the full CREATE TABLE grammar (constraints, identity, computed, defaults). **`##foo`** raises `NotSupportedException` at parse — not modeled. Tokenizer: `#` is now a leading-identifier char (`Parser/Tokenizer.cs:ParseHashPrefixedName`), so `#foo` / `##foo` / bare `#` all lex as a `Name` (CheckReserved short-circuits because no keyword begins with `#`); semantic rejection of `##` happens at CREATE.
- **`DROP TABLE [IF EXISTS] name[, name...]`** — new statement (`Simulation.Drop.cs`). Routes by `#`-prefix; comma-list form supported. Missing → **Msg 3701 St 5 Class 11** verbatim; `IF EXISTS` suppresses. Also covers regular (non-`#`) tables — first user-visible DROP TABLE support in the simulator.
- **Multi-part qualifiers on `#`-prefixed names** (`tempdb..#foo`, `tempdb.dbo.#foo`, `claude..#foo`) all resolve to the session's `#foo` regardless of qualifier (DB and schema segments are cosmetic on `#` leaves — probe-confirmed). With the schemas bundle this rule now applies uniformly across FROM / INSERT / UPDATE / DELETE / MERGE / SET IDENTITY_INSERT / DROP / TRUNCATE — the routing happens in `BatchContext.TryResolveTable` based on the leaf alone.
- **Transactional CREATE / DROP**: probe-confirmed that `BEGIN TRAN; CREATE TABLE #foo; ROLLBACK` undoes the table, and DROP TABLE inside a tran is similarly reversible. `UndoLog` (re-shaped to a polymorphic `UndoEntry` hierarchy) records `TempTableCreation` and `TempTableRemoval` entries alongside the existing slot-mutation kinds.
- **Persists across batches** in the same connection; **invisible to other connections** (Msg 208). Two sessions can independently hold a `#foo` of the same name — real SQL Server mangles internally; the simulator achieves the same effect by giving each connection its own dict (user-visible names stay un-mangled).
- **Bare `#`** is a valid temp-table name (one-char `#`). Identity / SCOPE_IDENTITY work identically to regular tables. CTE prefix, JOINs across multiple `#`-tables, all queries against `#foo` flow through the same Selection / Insert / Update / Delete / Merge machinery via `TryResolveTable`.

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
- `PRIMARY KEY` / `UNIQUE` on a computed column (`NotSupportedException`).
- Heap allocation tracking (flat page list, no IAM/PFS).
- Compound assignment (`SET @v += expr` / `-=` / `*=` etc.) — rewrite as `SET @v = @v + expr`. The arithmetic-operator runtime is locked behind `protected` instance methods on `TwoSidedExpression`; exposing them as static helpers is the prerequisite refactor.
- Table variables (`DECLARE @t TABLE (...)`) — separate feature with its own storage / scope / lifecycle.
- **Global temp tables (`##foo`)** — `NotSupportedException` at parse. Local `#foo` works; the lifecycle for global temps (drops when creator session closes, visible across sessions) is the deferred scope.
- **`ALTER TABLE #foo`**, **`OBJECT_ID('tempdb..#foo')`** — none modeled (none of those exist for regular tables either yet). The common `IF OBJECT_ID(...) IS NOT NULL DROP TABLE` cleanup pattern works via `DROP TABLE IF EXISTS #foo` instead.
- **`DROP SCHEMA`**, **`ALTER SCHEMA … TRANSFER`** — deferred. CREATE SCHEMA + schema-qualified resolution ships; lifecycle doesn't yet (catalog views — `sys.schemas`, `INFORMATION_SCHEMA.SCHEMATA` — do ship as of the catalog-view-expansion bundle).
- **`CREATE SCHEMA AUTHORIZATION <owner>`** — `NotSupportedException` (simulator has no user / principal model).
- **CREATE SCHEMA's `<schema_element>` greedy form** — real SQL Server consumes trailing CREATE TABLE / VIEW / GRANT as part of the same CREATE SCHEMA statement (and requires CREATE SCHEMA to be the first statement in the batch as a result). The simulator instead dispatches the trailing tokens as their own statements — same end state for the common idiom, but mismatched-grammar trailers (e.g. anything that isn't a recognized statement start) raise `NotSupportedException`.
- **`CREATE SCHEMA sys` / `INFORMATION_SCHEMA`** — raises Msg 2760 (matching real SQL Server). The schemas themselves exist as catalog-view hosts (`select * from sys.tables` / `select * from INFORMATION_SCHEMA.COLUMNS` work); legacy bare 1-part system-table access (`select * from systypes`) also still works.
- T-SQL `GOTO` / labels — `IF` / `BEGIN…END` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN` (bare) ship; unconditional jumps don't.
- `TRY ... CATCH`, `THROW`, `RAISERROR`, stored procs / UDFs. `BEGIN TRY` / `BEGIN ATOMIC` / `BEGIN DISTRIBUTED TRANSACTION` raise `NotSupportedException` at dispatch (peeked after `BEGIN`). Value-form `RETURN N` raises Msg 178 (reserved for the stored-proc / function scope, neither modeled yet). `@@ERROR` parses + returns 0 (see batch-state section); live tracking lands with TRY/CATCH.
- **`PRINT` message capture** — the statement parses + evaluates the operand (so operand-side errors like Msg 245 surface), but the message is discarded. `DbConnection` has no `InfoMessage` event (that's a `SqlConnection` extension), so adding a public observability surface would mean a new event on `SimulatedDbConnection`. Defer until an application needs it.
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
- **`SELECT INTO` string `+` reads as nullable**: real SQL Server projects `cs + 'x'` (both NOT NULL) as NOT NULL; the simulator can't statically distinguish string-concat from integer-add at projection-schema time (the dispatch happens runtime on operand types), so all `Add` results read as nullable. Conservative; no test reliance on string-`+`-non-null.
- **`SELECT INTO` from a CTE drops identity + nullability**: CTE bindings synthesize their wrapper `HeapColumn` entries with `nullable: true` and no identity, so the analyzer treats CTE sources as derived plans. Real SQL Server preserves both through simple single-source CTEs. Fix requires propagating column metadata through CTE bindings.
- **Temp-table DDL is transactional, regular-table DDL isn't**: `CREATE TABLE #foo` / `DROP TABLE #foo` inside `BEGIN TRAN` participate in the undo log (matching real SQL Server); the same statements on a regular table commit immediately regardless of an active transaction. Asymmetric, but no real workload depends on transactional regular-DDL (EF doesn't do schema changes through SaveChanges, and migrations run outside transactions on real SQL Server too).
- **Un-taken IF branches resolve names eagerly**: real SQL Server defers name resolution for un-taken branches, so `IF 1=0 SELECT bad_col FROM bad_table` runs silently. The simulator's parsers do name resolution inline with parsing, so un-taken branches that reference non-existent tables/columns still raise `Msg 208` / `Msg 207`. The common idioms (`IF NOT EXISTS (…) CREATE TABLE foo (…)`, `IF OBJECT_ID('foo','U') IS NOT NULL DROP TABLE foo`, `IF cond INSERT t VALUES (…)` against pre-existing `t`) work end-to-end because referenced names exist when the branch is skipped. State mutations inside the un-taken branch are correctly suppressed (skip-mode gate); the gap is name resolution only.
- **`IF` cond divide-by-zero**: real SQL Server surfaces `IF 1/0 = 0 …` as Msg 8134; the simulator surfaces the raw `DivideByZeroException` from .NET decimal arithmetic. Same pre-existing gap as documented for `TRY_CAST(1/0 AS INT)`.
- **`IF (1) select` paren-wrapped non-boolean cond — slight positional gap**: simulator raises Msg 4145 near `')'`; real SQL Server reports `'select'` (the post-paren token). Wording is correct (Msg 4145, non-boolean type), only the "near 'X'" suffix differs. Same gap applies to any `IF (value-expr) …` shape.
