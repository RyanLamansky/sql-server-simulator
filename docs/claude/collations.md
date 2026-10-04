# Collations — per-column declaration, coercibility, Msg 468 / 457 / 456 / 451 / 446 / 4191

Every string-categorized `SqlType` instance carries a `(Collation, Coercibility)` pair.
CREATE TABLE / ALTER COLUMN pin the declared collation at `Implicit` rank onto the column's `SqlType`; values decoded from the column inherit that type instance through row decode, so `SqlValue.CompareTo` / `Equals` / `GetHashCode` honor the declared rules.
Cross-collation operand pairs that can't be resolved by coercibility either report where they arise — Msg 468 (comparison / set ops / LIKE, and any pair of explicit `COLLATE` postfixes) or Msg 457 (a `varchar`-family `+` / `||` / CASE / UNION ALL result) — or travel outward as SQL Server's *No collation* label until something demands a definite collation: Msg 451 at an output column, Msg 4191 at an operation that needs one, Msg 446 at DISTINCT / CONVERT / COLLATE, Msg 456 at an assignment target, Msg 5335 at a deduping set operator.

## Type-side wiring

`VarcharSqlType` / `NVarcharSqlType` / `CharSqlType` / `NCharSqlType` intern per `(length, Collation, Coercibility)` trio via a 3-tuple `ConcurrentDictionary`.
The existing length-only `Get(N)` overloads return the `(N, Collation.Baseline, CoercibleDefault)` variant — matching the literal / parameter / CAST-of-literal contexts that historically didn't pin collation.
New `Get(length, collation, coercibility)` overloads return the column-pinned variant.
The `varchar` / `char` pair intern under `Collation.ForVarcharStorage()`, so a type built from a database or column collation always carries the collation's non-Unicode sort body — a `CHAR(n)` result once compared through the default collation's Unicode weights, ignoring `CHAR(0)` where real's varchar sort doesn't.

`SystemNameSqlType` / `TextSqlType` / `NTextSqlType` are single shared instances reporting `Collation.Baseline` at `Implicit` rank — sysname/text/ntext don't accept per-column COLLATE in the simulator (sysname rejects per grammar; text/ntext deferred as deprecated).

`SqlType.WithCollation(Collation, Coercibility)` is the virtual that rewraps a string type with new metadata; non-string types and the sysname/text/ntext singletons return `this`.
Used by `CollateExpression` for the postfix override and by CREATE TABLE / ALTER COLUMN for the column declaration.

## Coercibility precedence

`Coercibility` enum: `CoercibleDefault` (0) &lt; `Implicit` (1) &lt; `Explicit` (2).
Maps to SQL Server's collation-precedence ranks:

| Rank | Source | Wins over |
|---|---|---|
| `CoercibleDefault` | Literal, parameter, CAST of a coercible-default source, system-function result | nothing |
| `Implicit` | Column reference, CAST of a column, computed-column expression | `CoercibleDefault` |
| `Explicit` | `COLLATE` postfix on an expression | both lower ranks |

`Collation.Resolve(SqlType, SqlType)` returns `(Collation, Coercibility)?`: the winning pair when one rank is higher, the shared collation when both are the same rank, `null` when both are the same rank but the collations differ (caller raises Msg 468 / 457) — **except at `CoercibleDefault` rank, where two operands never conflict**.
SQL Server's rules make two coercible-default operands always resolve to the current database's default collation; the simulator doesn't thread the active database collation into this static resolver, so it picks the operand carrying a concrete (non-`Baseline`) collation over the `Baseline` fallback that unpinned system-function results use — equality-neutral for the ASCII identifiers these comparisons overwhelmingly involve.
Without this, a baseline-collated system-function result (e.g. `DATABASEPROPERTYEX(...)`) compared with a database-collation literal under a non-baseline database collation raised a spurious Msg 468 (surfaced by SMO's "Script Table as → CREATE To" against WWI's `Latin1_General_100_CI_AS`).

## Value-side compare path

`SqlValue.CompareTo` / `Equals` / `GetHashCode` route through `this.Type.Collation ?? Collation.Baseline`.
Same-type pairs (which after the interning split are also same-collation pairs) take the fast path.
Cross-type cases flow through `CompareValuesPromoted` (in `BooleanExpression.cs`), which:

1. Rejects a pair the comparison grid refuses — a LOB operand even beside a NULL, any other pair once both values are non-NULL (see [`arithmetic.md`](arithmetic.md#type-pair-legality); the compile-time check has normally raised it already).
2. Runs `Collation.Resolve` for string-string pairs with different types; raises **Msg 468 State 9** on conflict (probe-confirmed wording: `Cannot resolve the collation conflict between "X" and "Y" in the <op> operation.`).
   The check fires before NULL short-circuits, matching real SQL Server (`NULL = NULL` across cross-collation columns also raises).
3. Falls through to `SqlType.Promote` + per-side `CoerceTo` for the value coercion.

The fast path uses `SqlValue.WithType` to re-tag a value with a different `SqlType` instance — used by `CollateExpression.Run` to apply the explicit collation rewrap without re-allocating the underlying string reference.

## Decode preserves column type instance

`VarcharSqlType.Decode` and `NVarcharSqlType.Decode` thread `this` (the actual interned instance) into `SqlValue.FromVarchar(VarcharSqlType, string)` / `FromNVarchar(NVarcharSqlType, string)` rather than the singleton `FromVarchar(string)` overload, so the column's collation/coercibility survives the decode.
The parallel `SqlValue.FromString(type, value)` similarly preserves the target type during cross-string coercion.

`CharSqlType.Decode` / `NCharSqlType.Decode` pass `this` (via `FromChar` / `FromNChar`), so the decoded value carries the column's collation.

## Operator-site enforcement

- **Comparison (`=` / `&lt;&gt;` / `&lt;` / `&gt;` / `&lt;=` / `&gt;=` / `IN` / `BETWEEN` / `ALL` / `ANY`)** — all funnel through `CompareValuesPromoted`.
  Msg 468 with the per-op name (`"equal to"`, `"not equal to"`, `"less than"`, …).
- **`LIKE`** — `LikeExpression.Run` calls `Collation.Resolve(l.Type, r.Type)` on the runtime values' types.
  Replaces the old parse-time `PeelExplicitCollation` walk, which only caught explicit COLLATE postfixes.
  Conflict raises Msg 468 with operator name `"like"`.
  The resolved collation then decides the match itself, every half of it — see [LIKE / PATINDEX matching](#like--patindex-matching) below.
  Compiling the pattern is memoized per node (`LikeMatcher.Cache`, shared with `PATINDEX`) — see [the pattern memo](#the-pattern-compilation-is-memoized-per-node) below.
- **String concat (`+`)** — `Add.StringConcatenation` hands the operand pair to `UnresolvedCollation.Settle`, which either settles it, marks the result unresolved, or raises (see [the propagation rules](#an-unresolved-collation-propagates--coercibilitynocollation)); the `varchar`-family raise is **Msg 457 State 1**, and real calls string `+` *add*.
  `TwoSidedExpression.GetSqlType` runs the same body so the projection schema's result type matches the runtime value's type — RowEncoder rejects mismatched instances, so the GetSqlType / Run paths must stay aligned.
- **ANSI concat (`||`)** — `Concatenate.ResolveResultType` (shared by `Run` and `GetSqlType`) settles the same way naming the **`concat`** operator where `+` names `add` (probe-confirmed both ways).
- **Value-arm unification (`CASE` / `COALESCE` / `IIF`)** — `Expression.PromoteValueArms` folds the arms pairwise through the same seam, naming the **`CASE`** operator, which is what real says for `COALESCE` too (it desugars to a CASE).
  `ISNULL` is the exception that never conflicts: it takes its first argument's collation outright rather than unifying (probe-confirmed — `ISNULL(<CI col>, <CS col>)` returns the rowset), so an already-unresolved argument rides through it.
- **`CONCAT` / `CONCAT_WS`** — `StringConcat.CollationAccumulator` left-folds the string arguments (`CONCAT_WS`'s separator participates like any other; non-string arguments stringify into the accumulated collation and contribute nothing), so the result carries a column's collation instead of the database default.
  An unresolvable fold marks the result rather than raising, for **both** string families — see [Msg 451](#msg-451--the-output-column-message) for where that lands.

## LIKE / PATINDEX matching

`LikeMatcher` compiles a pattern into a segment list — literal runs, `%`, `_`, character classes — and matches it against the subject under the **resolved collation's whole comparison semantics**.
Both front doors share it: `LIKE` / `NOT LIKE` compile anchored at both ends, `PATINDEX` consumes a leading / trailing `%` into the anchoring decision instead (so `PATINDEX('%abc', 'xabc')` reports 2, where the content starts) and reports the match position in UTF-16 units, codepoints under `_SC_`.
The whole model below is probe-confirmed against SQL Server 2025; the matrix lives in `LikeCollationTests`, each case naming its probe row.

**The subject is a sequence of characters, not of UTF-16 units.**
A base character carries its combining marks — `N'e' + NCHAR(0x0301)` matches `N'_'` and not `N'__'`, and so does a halfwidth kana plus its voiced sound mark — while a bare mark, a CR and an LF each stand alone.
Every boundary the match lands on is a character boundary, which is what makes `PATINDEX(N'%e%', N'Xe' + NCHAR(0x0301) + N'Y')` answer 0 under an accent-sensitive collation: the base `e` alone isn't a character there.
A **surrogate pair** reads three ways, and the collation's vintage picks (`Collation.SurrogateMatching`, derived in the name parser):

| Collation | `emoji LIKE N'_'` | `emoji LIKE N'__'` |
|---|---|---|
| unversioned — the `SQL_*` family and the pre-100 Windows names | no | no |
| versioned (`_90_`, `_100_`) and every binary collation | no | **yes** |
| supplementary-character-aware (`_SC`, v140+) | **yes** | no |

A literal still matches the pair under all three, and so does `%`.

**A literal run compares linguistically**, through `CompareInfo.IsPrefix`'s own match length — which is how much of the subject the run consumed, five UTF-16 units for a four-unit run when an accent-insensitive `cafe` eats a following combining mark.
So `N'café' LIKE N'cafe%'` matches under `_CI_AI`, a decomposed subject matches a composed pattern, and `_KS_` / `_WS_` stop the kana and width folding exactly as they stop it for `=`.
Turkish is why the ordinal fast path below has to be proven rather than assumed: `N'I' COLLATE Turkish_CI_AS LIKE N'i'` answers no.

**A character class is ordered by the collation.**
A range tests `Collation.Compare` and a single member tests `Collation.Equals`, so `[a-c]` under `Latin1_General_CS_AS` holds `A` and `B` but not `C` (the collation interleaves the cases), `á` falls inside `[a-c]` under an accent-*sensitive* collation, and a fullwidth `５` falls inside `[0-9]` even under `_WS`.
A reversed range (`[c-a]`) is unsatisfiable rather than fatal — `[c-a1]` still matches `1`.
Members are characters, so `[` + `e` + U+0301 + `]` is one member.
An empty `[]` and an unterminated `[` never match, real's silent failure.

**Wildcards and the escape character are matched by code point**, never through the collation: a fullwidth `％` is a literal, and `ESCAPE N'e'` makes neither the pattern's `E` nor its `é` an escape.
What an escape protects is still an ordinary literal that compares under the collation.
The escape itself converts to `varchar(1)` whatever its type — `ESCAPE 1` is `'1'`, a date escapes with its first character, and `ESCAPE 1.5` or a `money` raises that conversion's own overflow (Msg 8115 / 234) — and a NULL escape escapes nothing; a `sql_variant` or `xml` in any of the three slots is Msg 8116 against the "like function" (probed 2026-09-25 against SQL Server 2025).

**Trailing-space slack is the non-Unicode family's.**
A subject may carry trailing U+0020 the pattern didn't consume — `'x  ' LIKE 'x'` — but a single `nvarchar` / `nchar` operand makes the comparison Unicode and the slack disappears: `N'x  ' LIKE N'x'`, `N'x  ' LIKE 'x'` and `'x  ' LIKE N'x'` all answer no.
The rule is the operand types', not storage's, so a `char(10)` holding `'x'` matches `'x'` and an `nchar(10)` doesn't, and a `char(10)` subject against an `nvarchar` pattern doesn't either.
The pattern's own trailing spaces are significant in every case, and `PATINDEX` takes the same rule wherever its match has to reach the subject's end.

### Cost, and the ordinal fast path

Matching linguistically is more expensive per character than an ordinal compare, so the matcher proves its way onto an ordinal path instead of assuming it.
`Collation.PrintableAsciiFoldsCaseOnly` decides eligibility: across U+0020..U+007E, the collation's equality has to be exactly ordinal equality plus — for a case-insensitive collation — the 26 ASCII letter-case pairs.
It is computed once per collation instance by bucketing the 95 characters through `Collation.GetHashCode` (equal strings hash equally, so only same-bucket pairs can be equal) and checking every collision, plus the 26 pairs directly; `Turkish_CI_AS` fails it, which is the point.
With that established, a literal run whose own window — the run's length plus one character, so a combining mark right after it sends the match back to the linguistic path — is printable ASCII compares with an ordinal `StartsWith`.
Only the `%`-then-literal shape classifies the *whole* subject, because that is what licenses skipping ahead with an ordinal `IndexOf` instead of walking character by character.

Measured on the WWI battery: `UPPER(Description) LIKE 'USB%'` over 228k rows runs **36.7 ms** against the regex engine's 93 ms, and `StockItemName LIKE '%shark%'` 0.14 ms against ~1 ms.

## The character-matching string scalars search under the collation too

`CHARINDEX`, `REPLACE`, `TRANSLATE`, `STRING_SPLIT` (its separator) and the `TRIM` / `LTRIM` / `RTRIM` family (their character set) all match through one primitive, `Collation.IndexOf` in `Collation.Matching.cs`, so the collation decides them the way it decides `=` and `LIKE`.
Which collation is the arguments' own resolution (`StringScalars.CollationFor`), so an explicit `COLLATE` on **any** argument decides the whole call — including `REPLACE`'s replacement and `TRANSLATE`'s translation list, neither of which is ever compared.
The whole model below is probe-confirmed against SQL Server 2025; the matrix lives in `StringScalarCollationTests`, each case naming its probe row.

**Every half the name declares folds.**
`CHARINDEX(N'e', N'café')` is 4 under `_AI` and 0 under `_AS`; `TRANSLATE(N'cafe', N'E', N'Z')` is `cafZ` under `_CI` and unchanged under `_CS`; a fullwidth `ａ` finds a halfwidth `a` unless the name carries `_WS`, and hiragana finds katakana unless it carries `_KS`.
A binary collation folds nothing.
Before this, the five scalars folded **case only** (`CHARINDEX` / `REPLACE`) or nothing at all (`TRANSLATE` / `STRING_SPLIT` / `TRIM`), so `PATINDEX(N'%e%', s)` found `café` where `CHARINDEX(N'e', s)` didn't.

**How much of the subject a match consumes is not the needle's length.**
An accent-insensitive `e` matches a decomposed `e` + U+0301 and eats both code units, so `REPLACE` over a decomposed `café` comes back as a four-character `cafX`.
Each caller applies its own rule to that length, and they genuinely differ:

| scalar | resumes at |
|---|---|
| `REPLACE` | past the whole match — `REPLACE(N'assb', NCHAR(0x00DF), N'Q')` on real is `aQb` |
| `STRING_SPLIT` | one separator character past the match's **start** — the same pair splits into `a` and `sb` |
| `CHARINDEX` | reports the match's start; the position counts UTF-16 units, codepoints under `_SC_` |

**`TRANSLATE` and the `TRIM` family ask a different question**: they walk their input one code unit at a time and ask whether the *character set* holds that character, which is `Collation.IndexOfElement` — a search whose subject is the set.
So a combining mark is its own candidate (a decomposed `café` keeps its mark and only the base letter is substituted or stripped), and `TRANSLATE` substitutes by the **position** the search reports rather than by a member index.
Under an `_SC` collation `TRANSLATE` walks code points instead, in all three arguments: a surrogate pair counts once toward the Msg 9828 length check and maps whole, and a lone half matches only a lone half in the list, never half of a pair (probed 2026-09-25 against SQL Server 2025).
`STRING_SPLIT` likewise takes a surrogate pair as its one-character separator exactly when the resolved collation is `_SC`, whichever argument carries it.

**A weightless needle is not found.**
An empty string, and a bare combining mark under an accent-insensitive collation, match at every position with zero length as far as `CompareInfo` is concerned; real reports not-found for both, so the match length is what the miss is keyed on rather than a special case per caller (`CHARINDEX(N'', N'abc')` and `CHARINDEX(NCHAR(0x0301), N'abc')` are each 0).

**The one-argument `TRIM` / `LTRIM` / `RTRIM` forms are not collation-driven.**
Real strips U+0020 there and nothing else, so an ideographic space survives a bare `TRIM` under the very collation whose two-argument `N' '` set removes it — the one place the two forms disagree, and the reason `StringScalars.TrimSpaces` sits beside `TrimUnderCollation`.

`Collation.IsPrefix` is the same primitive's anchored form, and `LikeMatcher`'s literal runs go through it — which is what makes `CompareInfo`'s one lossy corner a single fix rather than two.
`IndexOf` and `IsPrefix` silently drop the case level once `CompareOptions.IgnoreNonSpace` is set, where `Compare` keeps it, so under a `_CS_AI` collation they report `N'E'` as matching `N'é'`; a hit is re-read through `Compare` when the collation is case-sensitive, and the search resumes one position on if the re-read rejects it.
The [ordinal fast path](#cost-and-the-ordinal-fast-path) carries over unchanged: an all-printable-ASCII needle against an all-printable-ASCII subject takes a vectorized ordinal `IndexOf` under the same `PrintableAsciiFoldsCaseOnly` proof, which is every call under an ASCII workload.

## The pattern compilation is memoized per node

`LIKE` / `NOT LIKE` / `PATINDEX` compile a SQL pattern into a `LikeMatcher`: the pattern is walked, the segment list built, and each character class's printable-ASCII bitmap filled with 95 collation comparisons.
Compiling the pattern per row instead costs roughly a microsecond and a few kilobytes each time.
`LikeMatcher.Cache` — one instance on each `LikeExpression` / `PatIndex` node — holds the last compilation and its whole input: the **pattern text**, the **escape character**, and the **resolved collation**.
A literal or a parameter hands out the same pattern for every row of a statement, so one entry is enough; a per-row pattern column misses every time and recompiles exactly as it did before, one reference compare later.

On a 228k-row `WHERE Description LIKE 'USB%'`, per-row compilation measures 285 ms and 782 MB against the memoized 35 ms and 68 MB — and the same scan reading the column and doing nothing else costs 23 ms and 68 MB, so the residual is the matching itself.

The memo is read and written concurrently, because the plan cache shares a node across sessions: the entry is immutable and published through `Volatile`, and two threads racing to fill it compile equivalent matchers.
A `LikeMatcher` is immutable, so matching is thread-safe.

Two of the three key components are per-node stable in every shape reachable through SQL today — the escape character can vary per row (`… LIKE 'A_B' ESCAPE <expression>`), but the resolved collation can't, since every route to a second collation for one node (`COLLATE` postfix, a `CASE` over two differently-collated columns, a set operation) is settled statically or is a Msg 451 / 468 conflict.
The collation stays in the key anyway: the memo's contract is the whole input, not the parts that happen to vary.

## Compile-time binding

Every one of those sites binds at **parse**, so an **empty** rowset raises exactly what a populated one does — real compiles the rule rather than evaluating it, probe-confirmed across SQL Server 2025 on empty tables.

`BooleanExpression.Bind(batch, resolveColumnType)` is the predicate-side counterpart to `Expression.GetSqlType`: it types both operands of each comparison shape and runs `BooleanExpression.RequireResolvableCollation` over the pair — the same body `CompareValuesPromoted` calls per value, so the two phases can't drift.
The comparison subclasses each declare their `OperatorName`, which both phases weave into the message.
Drive sites, all handing over the resolver that scope already had:

| Clause | Where the bind runs | Resolver |
|---|---|---|
| `WHERE` / `HAVING` / `GROUP BY` term / a JOIN's `ON` | `Selection.BuildSqlProjection`, after the projection types | `ResolveColumnTypeAcrossSources` over the FROM sources |
| single-table `UPDATE` / `DELETE` `WHERE` + the `SET` values | `Simulation.Update.cs` / `Simulation.Delete.cs` | `Selection.TargetColumnTypeResolver` (target's columns, or the view's projection) |
| joined `UPDATE` / `DELETE` `WHERE` + the `SET` values | same files | `Selection.ColumnTypeResolverFor` over the parsed sources |
| `MERGE`'s `ON` and each `WHEN … AND` | `Simulation.Merge.cs` | the existing two-sided `ResolveMergeColumnType` |
| a `CASE`'s `WHEN` predicates / compare values | `CaseExpression.GetSqlType` | whatever resolver the enclosing expression got |

A DML predicate parses with its resolver installed as `ParserContext.OuterTypeResolver` (`Selection.ParseAndBindPredicate`), and a JOIN's `ON` with the sources parsed so far (`ParseOnPredicateWithScope`) — the same chaining `ConsumeWhereOrderByWithOuterScope` gives a SELECT's WHERE, and what lets a subquery *inside* one of those clauses resolve the enclosing columns while typing its own projection.
SMO's index-scripting query needs exactly that: it nests `(select min(index_id) from sys.indexes where object_id = tbl.object_id)` inside an `ON`.

The check runs at parse, which a plan-cache hit skips — so a cached plan has to be invalidated when a column's collation changes, and it is: every successful `ALTER` bumps the `Simulation.SchemaVersion` the cache entry is stamped with, so the re-parse picks up the new conflict.

## `COLLATE` postfix

`CollateExpression.Run` rewraps the value's type via `WithCollation(this.ResolvedCollation, Coercibility.Explicit)`.
`GetSqlType` propagates the same override through projection.
A non-string operand — xml and a bare `NULL` included — raises Msg 447 while compiling, and it outranks an unknown collation name's Msg 448, which real raises only over a string (probed 2026-09-26 against SQL Server 2025).

A `varchar` / `char` moved to a collation of another code page converts there and then — best fit, else `?` — so what reads it next sees the converted text: `LOWER(CAST(CHAR(192) AS varchar(1)) COLLATE SQL_Latin1_General_CP437_CI_AS)` is `a`, the `À` having become `A` at the `COLLATE` (probed 2026-09-28 against SQL Server 2025).

Chained `expr COLLATE A COLLATE B` rejects with Msg 156 at parse time (probe-confirmed).
Unknown collation name raises Msg 448 at parse time.

### The postfix on a parenthesized group, in a predicate

`WHERE (a + b) COLLATE X LIKE 'ab%'` reaches the boolean grammar's one ambiguous position: a leading `(` at the predicate-atom level is either a grouped sub-predicate (`WHERE (col = 5) AND …`) or a parens-wrapped *value* on a comparison's left (`WHERE (a + b) = 5`), and `BooleanExpression.LookaheadValueLhs` decides by peeking the single token past the matching `)`.
`COLLATE` belongs in that token set: only a character **value** takes the postfix, and the comparison it belongs to (`= 'x'`, `LIKE 'x%'`, `IN (…)`, `IS NULL`, `BETWEEN`) sits past the collation name, out of the one-token peek's reach — so without it the whole shape read as a boolean group and reported Msg 4145 at the `)`.

Real accepts it in every predicate position — WHERE, HAVING, a JOIN `ON`, a CHECK constraint — over any value expression the parens can hold, a `CASE` and a scalar subquery included, and against every comparison form (probe-confirmed 2026-08-05, including `NOT LIKE` / `NOT IN` / `NOT BETWEEN` and a leading `NOT`).
It refuses the same postfix on a parenthesized *boolean* — **Msg 156** near `COLLATE`, the way it refuses `(a = 'a') LIKE 'x'` (Msg 156) and `(a = 'a') + 1` (Msg 102) — so routing that spelling down the value path costs nothing real accepted.
The simulator reports Msg 4145 rather than Msg 156 / 102 for all three of those, which is a message divergence inside a shape both engines reject.

The disambiguation's other guards are untouched: a top-level `,` inside the parens still routes to the boolean-group path so a row constructor reports its own Msg 4145 at the comma, and a second `COLLATE` is the Msg 156 above.

The pseudo-collations **`catalog_default`** and **`database_default`** resolve before the name lookup: `catalog_default` → `Collation.Catalog` (the fixed metadata collation), `database_default` → `context.Batch.CurrentDatabase.Collation` (resolved at parse time to the active database).
SMO's system-configuration query uses `name COLLATE catalog_default` to normalize catalog string columns.

The **column-definition** COLLATE sites — CREATE TABLE / ALTER COLUMN / `DECLARE @t TABLE` / CREATE TYPE AS TABLE / `#temp` — resolve the same two keywords through the shared `CollateExpression.ResolvePseudoCollationName(name, batch)` seam, which expands `database_default` → the active database's collation *name* and `catalog_default` → the catalog collation name, storing the concrete name as the column's collation (so `sys.columns.collation_name` reports the resolved name, matching real).
Any other name passes through unchanged to the per-site `Collation.IsRecognized` gate, where a name it doesn't know is real's Msg 448 at state 2, as it is for a postfix `COLLATE` at state 1 (probed 2026-10-01 against SQL Server 2025).
The keyword match is case-insensitive (`COLLATE DATABASE_DEFAULT` works).
Resolution at bind time is plan-cache-safe: column DDL isn't plan-cached, and the cache keys per database regardless.
The SSMS Disk Usage report declares `nvarchar(…) COLLATE database_default` table variables; before this seam those were rejected.

**Probed `database_default` semantics** (SQL Server 2025, distinctive-collation scratch database): `database_default` resolves to the **session** database's collation in *every* context, including `#temp` columns — real does **not** use tempdb's collation there (`SQL_VARIANT_PROPERTY(cast(#t.c as sql_variant), 'Collation')` reports the session DB's collation, not tempdb's).
This matches the simulator's `CurrentDatabase.Collation` resolution exactly.
`catalog_default` in a non-contained database resolves to the database collation on real (containment isn't modeled); the simulator keeps it pinned to `Collation.Catalog` for parity with the expression-level SMO catalog-comparison use — a documented, low-impact divergence for the astronomically-rare `catalog_default`-on-a-user-column case.

## Database default and `#temp` inheritance

`Simulation.Create.cs`'s column wiring (shared by CREATE TABLE / ALTER TABLE ADD / DECLARE @t / CREATE TYPE AS TABLE / temp-table paths) resolves the column's pinned collation as: explicit `COLLATE` clause first, else the active database's `Database.CollationName`, else `Collation.Baseline`.
So `#temp` tables created while a BACPAC-loaded non-default-collation database is active inherit that database's collation — avoiding the EF temp-join footgun (real SQL Server's tempdb is independent, but the common shape — tempdb matches server default which matches user DB — collapses to the same behavior).

## Server-level seed: `Simulation.ServerCollationName`

`Simulation.ServerCollationName` (string-typed `init`-only property, defaults to `SQL_Latin1_General_CP1_CI_AS`) is the seed for every freshly-created `Database`: both the lazy `"simulated"` seed picked up on first `CreateDbConnection` and bacpac imports that don't carry their own collation declaration.
Mirrors SQL Server's `model.collation` role; `init`-only reflects real SQL Server's install-time immutability (the only way to change it on a real instance is the `sqlservr -m -q` rebuild-master dance, blocked outright on Azure SQL).
Setter validates against `Collation.TryGet` and raises `ArgumentException` on an unrecognized name.

Important fidelity edge — the seed knob closes a documented identifier-dict-comparer gap.
Per-database dict comparers (`Database.Schemas`, `Schema.HeapTables`, etc.) are built at `Database` construction time from the seeded collation; `ALTER DATABASE COLLATE` updates `Database.Collation` for future identifier compares but doesn't rebuild the existing dict comparers.
Setting `ServerCollationName` at construction means the dict comparer is right from the start: e.g., `CREATE SCHEMA DBO` on a CS-seeded DB succeeds (both `dbo` and `DBO` coexist as distinct schemas, probe-confirmed verbatim on real SQL Server 2025), whereas the post-hoc `ALTER DATABASE simulated COLLATE SQL_Latin1_General_CP1_CS_AS` path raises Msg 2714 because the stale CI dict comparer still treats `DBO == dbo`.
Coverage: the `ServerCollationName_*` region in `NameComparisonRegimeTests.cs`.

## Result-type collation routing through the active database

`Expression.GetSqlType` takes a `BatchContext batch` parameter (paired with `Expression.Run`'s `RuntimeContext.Batch`), so result types that depend on the active database — notably string-typed scalar function returns and the MERGE `$action` pseudo-column — pin the per-DB collation at both parse-time schema computation and runtime value construction.
The parity contract is preserved: both sides resolve from the same `batch.CurrentDatabase.Collation`.

Sites routed through the active DB collation:
- `CHAR(N)` / `NCHAR(N)` result type (`CharFromCode` / `NCharFromCode`).
- `hierarchyid.ToString()` and spatial / XML method calls that return string-typed values.
- `MERGE … OUTPUT $action` (`Simulation.Merge.MergeActionReference`).
- `sys.fn_listextendedproperty` `value` projection column (`Selection.ListExtendedProperty.cs`).
- `SqlType.PromoteForArithmetic`'s string-concat path derives the result collation via `Collation.Resolve(a, b)` from the operands' coercibility ranks rather than defaulting to `Collation.Baseline`.
- **`CAST` / `CONVERT` to a character type** (`Cast.ResultStringType`, shared by `Cast` and `ConvertExpression` at both `GetSqlType` and `Run`): a **character source** carries its collation and coercibility through; a **non-character source** yields the database default collation with `CoercibleDefault` coercibility.
  So `CAST(int AS varchar)` concatenates and compares cleanly with literals and other database-collation values (was `Collation.Baseline`, which raised Msg 457/468 under a non-baseline database — surfaced by SMO's `'extended_index_' + CAST(i.object_id AS varchar)` and `CONVERT(nvarchar(128), DATABASEPROPERTYEX(...))` patterns).
  Probe-confirmed against SQL Server 2025: `CAST(<char COLLATE X> AS varchar)` keeps `X`; `CAST(<int> AS varchar)` gets the database default.

Probe-confirmed fidelity (real SQL Server CS database): `SELECT IIF(CHAR(65) = CHAR(97), 'eq', 'neq')` returns `'neq'` (literals don't case-fold under CS).
The simulator matches; the `CsDatabase_*CharFunctionResultUsesActiveCollation` tests in `NameComparisonRegimeTests.cs` lock the behavior in.

Sites that intentionally stay on `Collation.Baseline`:
- `SqlType.Varchar` / `NVarchar` pseudo-singletons and `SqlType.GetChar` / `GetNChar` static bridges — type-identity placeholders.
- `text` / `ntext` / `sysname` `Collation` overrides — server-default-only types.
  (Real SQL Server pins these per-database for text/ntext and per-server for sysname; a per-Simulation routing model is deferred.)
- Error-message type placeholders, dynamic-SQL string extraction, PRINT formatting — collation irrelevant to the surfaced value.
- User-supplied catalog content columns: `sys.extended_properties.value`, `sys.indexes.filter_definition` — real SQL Server tags these with the database collation; the process-wide catalog view declaration can't reach per-`Database` state, so these stay at Baseline until the per-Simulation catalog model lands.
- `Simulation.ServerCollation` initializer — the deliberate anchor for "what does the simulator's hardcoded baseline resolve to."

## Catalog views pin `_desc` / enum columns to `Collation.Catalog`

`Collation.Catalog` resolves to `Latin1_General_CI_AS_KS_WS`, the collation SQL Server 2025 reports on 425 of its catalog columns — the `_desc` enums, the type codes, `permission_name` — and the one its Msg 451 names when such a column meets a database-collation one (`sys.all_columns.collation_name` and `SQL_VARIANT_PROPERTY(…, 'Collation')`, probed 2026-09-25).
The rest of real's collated catalog columns carry the server collation (`Collation.Baseline`) or a fixed one of their own — `Latin1_General_BIN` for the Service Broker and CLR-assembly names, `Latin1_General_CI_AI` for `sys.spatial_reference_systems`' text — and each declaration names whichever real reports, found by sweeping every view's `sp_describe_first_result_set` against real (2026-09-25); so not every `_desc` column is catalog-collated (the availability-group ones are the server's).
The simulator once pinned `Latin1_General_100_CI_AS_KS_WS_SC` instead — the contained-database catalog collation Microsoft's "Contained Database Collations" doc names — but a non-contained instance never reports it, and the two sort some characters differently, so real's value is the one that matches both the metadata and the behavior.

The catalog-view registrations (across the `BuiltInResources.<Topic>.cs` partials) share `nvarchar60Catalog` / `nvarchar128Catalog` / `charTwo` / `charOne` as `private static readonly` fields in root `BuiltInResources.cs`, all at `Collation.Catalog` + `Coercibility.Implicit`.
Sites that pin to catalog:

- Most `_desc` columns (`type_desc`, `class_desc`, `state_desc`, `temporal_type_desc`, `delete_referential_action_desc`, etc.).
- `sys.database_permissions.permission_name`.
- The `type` / `state` char(1)/char(2) enum-code columns and matching cell-value sites (`fkType` 'F ', `ckType` 'C ', `dfType` 'D ', `pkType` 'PK', `uqType` 'UQ', etc.).
- The null-`_desc` placeholder in `sys.spatial_indexes` (cell carries the catalog tag for visual consistency; row encode/decode routes through the column type anyway).

`Coercibility.Implicit` matches real SQL Server's behavior for explicit-`COLLATE`-pinned columns: catalog-column-vs-literal comparisons resolve under the catalog collation rather than rank-ambiguously.
`RowEncoder.IsCompatibleColumnType` accepts `CharSqlType` / `NCharSqlType` pairs with matching length but differing collation/coercibility (mirroring the existing var-family rule), so cells from `SqlType.GetChar(N)` bridges still flow through the catalog-pinned column types without false rejections.

## String literals carry the active DB collation

`Tokenizer.NextToken` takes a `Collation activeCollation` parameter; `ParserContext.MoveNext` threads `context.CurrentDatabase.Collation` in.
The two string-literal entry points (`ParseStringLiteral` for `'foo'`, `ParseNPrefixedStringLiteral` for `N'foo'`) construct `VarcharSqlType.Get(0, activeCollation, Coercibility.CoercibleDefault)` / `NVarcharSqlType` and tag the resulting `SqlValue` with it.
Other literal kinds (varbinary `0xHEX`, currency `$1.23`) don't carry collation and ignore the parameter.
A `'foo'` body is also read in that collation's code page (`Tokenizer.InCodePage`), so a character the page lacks is its best fit or `?` before the literal reaches anything: `UNICODE('水')` is 63 under Latin1 and 27700 under `Japanese_CI_AS`, and `'水'` stores `?` even into an `nvarchar` column — real's missing-`N` data loss (probed 2026-09-25).

Effect: `SELECT IIF('A' = 'a', 'eq', 'neq')` on a CS database returns `'neq'` (case-sensitive), matching real SQL Server.
The `CsDatabase_TwoVarcharLiteralsCompareCaseSensitively` / `CsDatabase_TwoNVarcharLiteralsCompareCaseSensitively` tests in `NameComparisonRegimeTests.cs` lock the behavior in.

`ALTER COLUMN` without an explicit `COLLATE` clause preserves the existing column's collation (probe-aligned).
With an explicit `COLLATE`, the new collation pins at `Implicit` rank.

## Parser-driven catalog

`Collation.TryGet(name)` decodes the grammatical shape of a name and constructs the matching instance on demand; results are interned so the same name always resolves to the same reference.
The complete `sys.fn_helpcollations()` catalog ships — 5540 names total (77 SQL_* + 5463 non-SQL_*), probed against SQL Server 2025 and validated against the per-prefix tail-set tables in `Collation.Catalog.cs`.
Names outside the catalog (whether outright misspellings or grammar-valid but never-shipped combinations like `Pashto_CI_AS` or `Latin1_General_140_BIN`) raise `NotSupportedException` in direct SQL and surface on `BacpacImportResult.Warnings` for BACPAC loads.

### Architecture

Three files carry the work:

- **`Collation.Catalog.cs`** — data.
  124 prefix entries (`KnownPrefixes`: prefix → BCP-47 culture + human-readable description prefix); 77 SQL_* per-name entries (`SqlServerSortOrders`: full name → sort order number + human prefix); 9 distinct tail-set patterns (`Pattern0Tails`..`Pattern8Tails`) covering every (prefix, version, flag) combination real SQL Server ships across the non-SQL_* family; the 89 non-SQL_* prefixes share these 9 patterns via `PrefixToPattern`.
- **`Collation.Parser.cs`** — `TryParse(name)` tokenizes the suffix from the right, extracts version / code-page / flag bitmask, then validates: SQL_* names against `SqlServerSortOrders`, non-SQL_* names against the prefix's tail-set pattern.
  Description column is generated from the flag bitmask + prefix metadata + (for SQL_*) the baked sort order.
- **`Collation.cs`** — abstract base + four concrete bodies (`CultureCollation` for the generic comparer, `BinaryCollationBody` for `_BIN`/`_BIN2` with the pre-Bin2 position-0-quirk dispatch, `Cp1252BinaryCollation` and `Utf8CodepointBinaryCollation` for the varchar-storage substitutes).

### Bespoke per-name body

`CreateInstance` (in `Collation.Parser.cs`) special-cases a name when the generic `CultureCollation` / `BinaryCollationBody` construction doesn't capture its real behavior: it builds the generic body, then wraps or replaces it.
The Latin1-General names are special-cased — `SortsByLatin1GeneralTable` picks the unversioned and `_100_` `Latin1_General` names and the `SQL_Latin1_General` names over code pages 1, 850 and 437, `_KS` / `_WS` excepted — and the freshly-built `CultureCollation` is handed to `new Latin1GeneralTableCollation(cultureBody, version)` (see [byte-exact sort](#the-latin1-general-names--byte-exact-sort)); that wrapper keeps the culture body for metadata and the fallback past its tables, and overrides `Compare` / `Equals` / `GetHashCode` and the matching seam.
The wrapped instance is interned like any other, so `TryGet` and `Baseline` return it.
Every other modeled name routes through `CultureCollation` or `BinaryCollationBody` with the appropriate flag-driven options.

### Behavioral notes by family

- **SQL_\* family**: the `SQL_Latin1_General` names over code pages 1, 850 and 437 take the [byte-exact tables](#the-latin1-general-names--byte-exact-sort); the rest route through invariant `CompareInfo` (unless the human-prefix description maps to a locale-specific culture, e.g., `SQL_Croatian_CP1250_CI_AS` → `hr-HR`) with the [two-pass minimal-punctuation treatment](#symbol-sort-weighting-other-sql_--windows--locale-families).
  Description carries the per-name SQL Server Sort Order number + Code Page (extracted from the `CP*` token).
- **Windows-style Latin1_General**: the unversioned and `_100_` names take the byte-exact tables; `_90` and `_140` keep invariant `CompareInfo` with the two-pass minimal-punctuation sort.
  `_BIN` engages the pre-2005 position-0-codeunit / position-1+-codepoint quirk; `_BIN2` is pure UTF-16 code-unit ordinal.
- **`_UTF8` collations**: storage encoding flips from CP1252 to UTF-8 for varchar/char columns.
  `_BIN2_UTF8` substitutes `Utf8CodepointBinaryCollation` (codepoint-order = UTF-8 byte order) on varchar storage.
- **`_SC_` collations** (and v140+ implicitly): set `IsSupplementaryCharacterAware` on the constructed instance, driving codepoint-aware LEN/SUBSTRING/etc. dispatch (see [`_SC_` function-semantics dispatch](#_sc_-function-semantics-dispatch)).
- **`_AI` flag**: sets `CompareOptions.IgnoreNonSpace`, so a diacritic folds for every comparison the collation drives — `=`, DISTINCT, ORDER BY, an index seek's range and LIKE alike.
  A name carrying neither `_AI` nor `_AS` is accent-sensitive, the grammar's own default.
  Note what that means for a key: an accent-only variant of an existing value violates a PRIMARY KEY under an `_AI` collation (Msg 2627, probe-confirmed).
- **`_KS_` / `_WS_` flags**: flip `CompareOptions.IgnoreKanaType` / `IgnoreWidth` off (default = both on).
- **Locale prefixes** (Japanese, Chinese, Turkish, Korean, etc.): map to the closest .NET culture via `KnownPrefixes`; fall back to invariant when no clean .NET equivalent exists (Tamazight, Traditional_Spanish, Indic_General).
  Sort-parity caveat in [Locale-comparer sort-parity gap](#locale-comparer-sort-parity-gap) applies — equality / CI/CS / KS / WS folding align, secondary sort tiebreakers within equivalence classes may diverge.

## The Latin1-General names — byte-exact sort

`Latin1GeneralTableCollation` (`Collation.Latin1GeneralSort.cs`, its Unicode records in `Collation.Latin1GeneralWeights.cs`) reproduces real's ordering, equality and matching from probe-extracted weight tables, because neither table real uses matches .NET's `CompareInfo`.
Each Unicode table was read as dense ranks of `NCHAR(n) + N'a'` under a name's `_CI_AI`, `_CI_AS` and `_CS_AI` forms over U+0000–U+024F, the code page 1252 repertoire, Thai, Latin Extended Additional and General Punctuation; each `varchar` sort order as the same ranks of every byte (probed 2026-10-02 against SQL Server 2025).
Validated by a fuzz harness diffing random string pairs over those repertoires against the live server under every routed name, in both storage types, at zero divergence apart from the characters listed under [Known gaps](#known-gaps); the real-world divergence that first motivated the default collation's tables — base64 `MIN(PasswordHash)` on AdventureWorks `Person.Password` (`varchar`, `+`/`/` order) — stays closed.

**Which table.**
The unversioned Windows names and every `SQL_Latin1_General` name share one Unicode table; the `_100_` names have their own, which adds characters the older one weighs nothing (the `ȸ` / `ȹ` digraphs expand, U+202F weighs as a space) and drops `¼ ½ ¾` from its repertoire.
A Windows name's `varchar` data compares as its Unicode data does.
A `SQL_` name's `varchar` data takes its SQL sort order instead, in `varcharBody`: 52 for `CP1_CI_AS`, 51 for `CP1_CS_AS`, 54 for `CP1_CI_AI`, 53 for `Pref_CP1_CI_AS`, and the code page 850 and 437 orders for those names.

**The Unicode tables** compare in Windows' own level order, each level over the whole string before the next:

- **Letters** — the accent-folded rank, so `'à' < 'Ao'`.
- **Accents** — `'cafe' < 'café'`, `'az' < 'àz'`; skipped under `_AI`.
  A Thai tone mark is an accent on the letter before it.
- **Case** — lowercase first, a superscript after its digit; skipped under `_CI`.
- **Minimal-weight characters** — apostrophe, hyphen, the dashes, soft hyphen and the controls weigh only at this last level: `'coop' < 'co-op'`, `'cant' < "can't"`, `'A' < "'A"`.
- Characters the version doesn't know weigh nothing at all (see [Characters real gives no weight](#characters-real-gives-no-weight-by-collation-version)).
- The Latin ligatures expand to their letters at every level, so they are **equal** to their expansion: `'æ' = 'ae'`, `'ß' = 'ss'`, `'ǅ' = 'Dž'`, `'ﬁ' = 'fi'`; one carrying a mark (`ǽ`, `ǣ`) weighs it on its last letter.
- A shorter string compares as if space-padded.

**The SQL sort orders** are per character, with no minimal-weight characters (`'coop' > 'co-op'`) and `CHAR(0)` weighted below everything:

- They share sort order 52's letters (the accent-folded rank, with the cedilla folded onto `c`, probe-confirmed `'Çm' < 'cn'`) and differ in what follows: 52 weighs the accents; 51 the accents and then the case, **uppercase first** (`ORDER BY` over `a, A, b, B` is `A a B b`, so `MIN` / `MAX` over a pair report `A` / `a`); 54 only the cedilla.
  The code page 850 and 437 names follow the same three shapes over their own byte ranks.
- Only `æ Æ ß` expand (plus the 437 page's own ligature bytes), and a ligature's last letter weighs above every accent, so `'ae' < 'æ'` and `'ss' < 'ß'`; `œ Œ þ Þ` are single letters.
- `CHAR(0)` and the controls weigh below the space, and a shorter string compares as if space-padded, so a control sorts a string *before* its own prefix: `'a' + CHAR(0) + 'b' < 'ab'`, `'a' + CHAR(9) < 'a'`, `CHAR(0) <> ''` (probed 2026-09-26).
  Every other non-binary `SQL_` collation's `varchar` data weights `CHAR(0)` the same way, and so does the matching seam under all of them — `LIKE 'ab'` doesn't match `'a' + CHAR(0) + 'b'`, `CHARINDEX` / `REPLACE` find the NUL (probed 2026-09-28).
  The names outside the tables compare through `CompareInfo`, which ignores it, so their `varchar` sibling (`ForVarcharStorage`, `Collation.WeightsNul`) compares the text between NULs piece by piece, and the matcher searches a NUL-bearing needle by code units and refuses a hit that crosses a NUL the needle lacks.
  A `LIKE` pattern's literal run that ends in NULs takes exactly that many of the subject's, whatever wildcard follows: `'a' + CHAR(0) + 'b' LIKE 'a' + CHAR(0) + '_'` and `CHAR(0) LIKE CHAR(0) + '%'` are true, and a lone `CHAR(0)` is a character (`LIKE '_'` true, `LIKE ''` false; probed 2026-09-30).
  Unicode data and the Windows collations ignore `CHAR(0)` on real as well.
- The nine `Pref` names (`SQL_Latin1_General_Pref_CP1` / `CP437` / `CP850`, `Danish`, `Icelandic`, `Scandinavian`, `SwedishPhone`, `SwedishStd`, `AltDiction`) compare equal across a case pair in every operator, yet their `varchar` data sorts the pair uppercase first, at the first character the spellings differ in (`AB`, `Ab`, `aB`, `ab`), while their `nvarchar` data doesn't (probed 2026-09-29 against SQL Server 2025 across all nine).
  The preference is only a **final tiebreak**: it applies once every `ORDER BY` key ties, so a later key outranks it (`ORDER BY v, id` keeps `id` order across a pair), it reverses under `DESC`, and it decides a `TOP … WITH TIES` boundary, where `RANK` / `DENSE_RANK` peers and equality still see one value.
  `Collation.PreferenceCompare` carries it, and `SortOrderKeys` (the sort-site sibling of `CompareOrderKeys`), `STRING_AGG` / `JSON_ARRAYAGG`'s `WITHIN GROUP` sort and the top-N heaps apply it; `MIN` / `MAX` keep the first-seen spelling, as real does.
  A `GROUP BY` / `DISTINCT` / `UNION` / `INTERSECT` reports the first-seen spelling here; real reports its uppercase-preferred one while its plan sorts and the first-seen one once it hashes — over a heap of `'a'`, n × `'b'`, `'A'`, both `DISTINCT` and `GROUP BY` answer `A` up to about 760 rows and `a` from about 770 (probed 2026-10-04 against SQL Server 2025).
  **Settled — don't re-pitch:** real itself doesn't guarantee the spelling — its cost model's sort-or-hash choice decides it.
  The other case-sensitive legacy orders (the Scandinavian, EBCDIC and alternate-dictionary families) are not modeled yet.

**Matching.**
`LIKE` literal runs, `PATINDEX` and the [character-matching scalars](#the-character-matching-string-scalars-search-under-the-collation-too) match through the same table (`MatchesByTable` / `TableMatch`): a run matches where its collation elements equal the subject's at the name's strengths and end on a character boundary, so a ligature matches its letters and half of one matches nothing — `N'ß' LIKE N'ss'` and `CHARINDEX(N'ss', N'aßb')` find it, `N'ß' LIKE N's%'` and `N'ß' LIKE N'[s]'` don't (probed 2026-10-02).

**Past the repertoire.**
A string with a character outside the table — after composing a decomposed accent, since real weighs `'s'` + U+0307 as `'ṡ'` — falls back to the inner `CultureCollation`'s `CompareInfo` path for the whole pair, which is close for arbitrary Unicode but not exact (see [Known gaps](#known-gaps)).
`Equals` is `Compare == 0` for in-repertoire pairs; a pair with any out-of-repertoire character uses the inner `CultureCollation`'s **plain** equality rather than its two-pass ordering (see [Equality and hash across the repertoire boundary](#equality-and-hash-across-the-repertoire-boundary)).
`GetHashCode` hashes the primary run and, under an `_AS` name, the accent run, leaving out a trailing run of elements weighing as a space, after a hash canonicalization pass, so DISTINCT / GROUP BY stay consistent with equality.
Equality keeps every symbol significant (only trailing spaces fold), so `'co-op' = 'coop'` is false, and apostrophe ≠ hyphen even off-repertoire (probe-confirmed: `N'ab''cＸ' = N'ab-cＸ'` is false and the two group separately).

**Which spelling `MIN` / `MAX` / a group key reports.**
Among values the collation calls equal — trailing spaces, case under `_CI_`, `'ก'` beside `'ก '` — real reports the **first one its operator reads**, for `MIN`, `MAX`, a `GROUP BY` key, `DISTINCT` and a window's `MAX … OVER`, serial stream or hash aggregate alike; the simulator's aggregators keep the first too (probed 2026-09-29 against SQL Server 2025 over heaps, clustered and indexed tables in four collations and four string types).
So the spelling is decided by the order rows reach the operator, and wherever real's plan sorts first, by its **Sort operator's order among ties**, which isn't stable: `ORDER BY g` over a heap whose `g` values run `1, 0, 1, 0, 0, 1, 1, 1, 1` returns the first `1` row last.
So a grouped `MAX` over tied spellings can report another one than real's; a parallel plan's choice is nondeterministic on real itself.
**Settled — don't re-pitch:** real doesn't guarantee which tied spelling `MIN` / `MAX` reports — it is its sort's unstable tie order and its plan's arrival order (the tie order is settled the same way, see [`backlog.md`](backlog.md#unbuilt-feature-areas)).

Implementation: this is the engine's hottest string path (it backs `Baseline`), so `Compare` / `GetHashCode` stream each operand's weights through a `WeightCursor` `ref struct` — one element per `MoveNext`, weightless characters skipped and ligatures expanded inline — rather than materializing weight lists, and each table holds U+0000–U+024F in a flat array ahead of a frozen dictionary for the rest.
A comparison walks the cursors at the primary level, dropping to a further walk only on a tie; the common case resolves in one pass with **zero allocation**.
Keep the `Covers` repertoire gate ahead of the streaming path: the "any out-of-repertoire char ⇒ `CompareInfo` fallback for the whole pair" contract requires scanning both operands before choosing the table path, which a bail-mid-walk detector would break.

## Equality and hash across the repertoire boundary

The hybrid body's `IEqualityComparer<string>` contract (`Equals(x, y)` ⇒ equal hashes) has to hold across two different equality sources: weight comparison for in-repertoire pairs and the inner `CultureCollation` for pairs with any out-of-repertoire character.
Three pieces make it hold (`Collation.Latin1GeneralSort.cs`):

- **Cross-boundary `Equals` is the inner's *plain* equality**, not `inner.Compare == 0`.
  The inner's two-pass minimal-punctuation logic is an *ordering* device whose tie-break checks only minimal-vs-real per position and would equate apostrophe with hyphen; plain `CompareInfo` equality keeps them distinct marks — matching the live server (probed) and matching `CultureCollation`'s own `Equals`/`Compare` split.
  Consequence: cross-boundary sort-equal-but-not-equal pairs exist, as they do on `CultureCollation` itself.
- **`GetHashCode` has a fast path and a canonicalized path.**
  A string whose every character is in the per-body *hash-clean* set (repertoire minus `hashFolds` keys — the overwhelmingly common case) hashes straight off its weight runs.
  Anything else is canonicalized — NFC (composes `e`+U+0301 → `é`), then per-rune folds — and the canonical form takes the weight-run hash if it lands in-repertoire, else the inner hash (consistent with the inner equality that governs such pairs by construction).
- **Every fold substitutes inner-equal content**, so canonicalization preserves the inner equality relation; that plus "weight-equal in-repertoire pairs already hash equal" is the whole consistency argument.
  In-repertoire folds live in the hard-coded `hashFolds` table: ICU-ignorable controls + soft hyphen → empty, NBSP → space, `ª º ¹ ² ³` → base, Thai digits → ASCII digits, vulgar fractions → their FRACTION SLASH decompositions (deliberately out-of-repertoire targets: both spellings then take the inner hash together), the CP1252 case pairs whose legacy *varchar* weights are asymmetric (`Œ Š Ÿ Ž` → lowercase), and Thai SARA AM → NIKHAHIT + SARA AA.
  Out-of-repertoire runes resolve lazily (`ComputeRuneFold`, cached per body): NFKC+lowercase candidate accepted only when the inner collation confirms equality (so `ſ` → `s`, which ICU rejects, never lands), with a one-time repertoire scan fallback for wrong-direction decompositions (Greek `μ` → CP1252 `µ`, other scripts' decimal digits).

Why folds of *in-repertoire* characters exist at all: an out-of-repertoire spelling can be `Equals`-equal to two in-repertoire strings that are unequal to each other (fullwidth `２` equals both `2` and `²` through the inner collation), so those in-repertoire strings must share a hash — a legal collision of unequal strings.
`CollationHashConsistencyTests` (Tests.Internal) guards the contract: repertoire-wide ICU-class sweep, Unicode-block normalization-variant sweep, seeded substitution fuzz, and the named triangles.

Downstream effect (the bug this closed): every hash container keyed by the collation folds alternate spellings — fullwidth / decomposed / homoglyph references to user tables, schemas, and procedures resolve (`Database.Schemas`, `Schema.HeapTables` / `Procedures`), EXEC duplicate-named-argument detection folds (`@a` + fullwidth `@ａ` → Msg 8143 echoing the first-seen spelling), and GROUP BY / DISTINCT buckets fold data-level variants (`N's'`, `N'ｓ'`, `N'S'` → one group — probe-confirmed).
Coverage: the `Regime1_*` fullwidth/decomposed tests in `NameComparisonRegimeTests.cs`.

## Name regimes outside the database collation

Two identifier surfaces do **not** follow the database collation (both probe-confirmed on a real `SQL_Latin1_General_CP1_CS_AS` database):

- **Variable / table-variable names** fold case, width, and kana type unconditionally — `declare @vx int; set @VX = 5` succeeds on a CS database, as does fullwidth `@ｖx` ≡ `@vx`.
  Comparer: `BatchContext.VariableNameComparer` (invariant `CompareInfo`, `IgnoreCase | IgnoreKanaType | IgnoreWidth`), keying `Variables` and `TableVariables` everywhere they're constructed.
  Note the contrast: *named-argument-to-parameter matching* (`exec p @A=1` against declared `@a`) **does** follow the database collation — on the CS database the case-flipped name doesn't bind (Msg 8144 too-many-arguments for sp_executesql).
- **Temp-table names** stay case-insensitive on a CS database (`#zzc` ≡ `#ZZC`), consistent with tempdb's server-collation inheritance.

Related tokenizer rule: non-spacing combining marks are identifier *continuation* characters (`Tokenizer.IsIdentifierBodyChar`) — a decomposed spelling (`zzcafe` + U+0301) both tokenizes and resolves against a composed `zzcafé` table on the live server (probed); resolution comes free from the NFC step in hash canonicalization plus the inner equality.

### Fixed tokens: `BuiltInToken`

Spec-defined strings that no `ALTER DATABASE COLLATE` reaches — the `INSERTED` / `DELETED` pseudo-tables, `OBJECT_ID`'s type-filter codes, `sp_addextendedproperty`'s argument names and level-type values — match through `BuiltInToken` rather than through any `Collation`.
Its comparison is invariant `CompareInfo` under `IgnoreCase | IgnoreKanaType | IgnoreWidth`, so those sites keep matching a wrong-case or fullwidth spelling (`'u'`, `ｉnserted`, `'ｓchema'`) even on a case-sensitive database — probe-confirmed, and covered by the regime-1 tests in `NameComparisonRegimeTests`.

Most calls answer without reaching that comparison.
When **both** operands consist only of ASCII alphanumerics, an ordinal-ignore-case compare gives the same answer and stands in; anything else falls back to the linguistic path.
The equivalence holds over that range because each ASCII alphanumeric carries its own primary collation weight, case is the only difference the options erase, and none of them participate in a contraction, an expansion, or a zero weight.
Both characters that break it are outside the range and both are load-bearing here, which is why the guard is alphanumerics rather than "is ASCII": a **fullwidth** `Ｓ` matches an ASCII `S` under `IgnoreWidth`, and a **control character** carries no weight at all, so `a` + U+0001 and `a` + U+0002 compare equal linguistically and unequal ordinally.
`BuiltInTokenComparisonTests` pins the equivalence exhaustively over one- and two-character words and pins both fallback shapes.

`GetHashCode` stays linguistic for every input, because equality reaches *across* the shortcut boundary — an in-range `SCHEMA` equals an out-of-range `ｓchema` — so only a width- and case-folding hash keeps the pair in one bucket.
That is also why a `Frozen*` collection keyed by `BuiltInToken.Comparer` is the wrong shape for a small accept-list: its per-lookup linguistic hash costs more than walking the whole list of candidates now does (measured at roughly 62 ns against 36 ns for a twelve-entry walk).
`ObjectId.ClassifyTypeFilter` is the pattern to copy where a value is matched against many codes — classify once into a discriminator, then dispatch on that.

**Choosing a matcher.**
The three matchers, in cost order: a `switch` over string constants or `string.Equals(…, Ordinal[IgnoreCase])` (~1 ns, and a miss against a differently-sized literal is only a length check), `BuiltInToken`, and a `Collation` (the database's own semantics, mandatory for user identifiers).
Pick by the semantics the site needs, then keep the shape simple, because the measured traps run the other way from intuition:

- A short chain of ordinal compares **beats** a `Frozen*` lookup; hashing pays off at `ResolveBuiltIn`'s scale (~300 entries), not an accept-list's.
- Uppercasing into a `stackalloc` span to reach a span `switch` (the SSS003 / SSS007 shape) may cost more than the chain it replaces at accept-list size; it too earns its keep across `ResolveBuiltIn`.
- What does cost is **materializing a string to feed a lookup** when the token already exposes `Source` as a span (`Frozen*.GetAlternateLookup<ReadOnlySpan<char>>` is the fix), and **repeating a compare per row** that a parse-time discriminator settles once (`XmlMethodCall`'s `XmlMethod`, `ObjectId.ClassifyTypeFilter`).

These compares sit behind the memo layers (`SourceColumnMemo`, the plan cache), so none of it moves a realistic query measurably; treat it as allocation and clarity work, not throughput work.

## Characters real gives no weight, by collation version

A code point real's weight table has nothing for is **ignorable**: `N'a' + NCHAR(0x0378) + N'b' = N'ab'` under every non-binary collation.
Which code points those are depends on the collation's **version alone** — the unversioned names (the `SQL_*` family included), `_90`, `_100` and `_140` — and not on language, case, kana or width; an accent-insensitive name adds the marks it folds (probed 2026-09-29 against SQL Server 2025 by testing all 65,536 BMP code units under 31 names).
The sets shrink as the tables grow: 21,229 code units under the unversioned names, 13,414 under `_90`, 5,838 under `_100` and 3,373 under `_140`, so `N'Ә'` (U+04D8) is ignorable before `_90` and `N'Ͱ'` (U+0370) before `_140`.
Surrogates split the same way: an unversioned name ignores every surrogate code unit, a supplementary character included, while `_90` and later weigh every surrogate **pair** — unassigned planes and tag characters too — and ignore only a lone high surrogate in U+D880..U+DB7F.

`WeightlessCharacters` (`Collation.Weightless.cs`) carries the sets as ranges (6.2 KB) and reconciles them with `CompareInfo` in both directions:
- **What real ignores and `CompareInfo` weighs** is removed before comparing and hashing, and replaced by a word joiner where a search needs its positions, so `CHARINDEX` of such a needle is 0 and `REPLACE` / `STRING_SPLIT` see through it.
- **What `CompareInfo` ignores and real weighs** — the C0 and C1 controls, U+200B, U+00AD before `_100`, the Hebrew accents — joins hyphen and apostrophe in the minimal treatment: no primary weight, a final-level weight that keeps `N'x' + NCHAR(1) = N'x'` false and sorts the copy carrying it after the copy without (`'a' < 'a' + CHAR(1) < 'ab' < 'a' + CHAR(1) + 'b'`), and a private-use stand-in wherever a search runs, so `CHARINDEX(NCHAR(1), N'a' + NCHAR(1))` is 2.

`LIKE`'s `_` doesn't count an ignorable character as one of its own: it rides with the character before it, or at the start with the one after, so `N'a' + NCHAR(0x0378) + N'b' LIKE N'a_b'` is false and `LIKE N'ab'` true, `NCHAR(0x0378) + N'b' LIKE N'_b'` is false, and `N'a' + NCHAR(0x0378) LIKE N'a'` true — the same for U+200D and for a NUL in Unicode data, while a minimal-weight `NCHAR(1)` is a character (`LIKE N'a_b'` true).
With no character to ride with, a subject made only of ignorables reads as empty: `NCHAR(0) LIKE N''` and `NCHAR(0) + NCHAR(0x200D) LIKE N''` are true, `NCHAR(0) LIKE N'_'` and `LIKE N'[^a]'` false (probed 2026-09-30 against SQL Server 2025).
The same holds for `varchar` data under the Windows collations, whose `CHAR(0)` is ignorable too (`('a' + CHAR(0) + 'b') COLLATE Latin1_General_CI_AS LIKE 'a_b'` is false).
A grid of U+0000, U+0001, U+00AD, U+200B, U+200D and U+0378 in and around subjects and patterns, under unversioned, `_100`, `_140`, `SQL_`, UTF-8 and binary names, matches real in both types.
Not modeled yet: U+FFFF, which rides like an ignorable under `_` on real yet makes `LIKE N'%' + NCHAR(65535) + N'%'` false even over a subject holding it, and keeps `N'a' + NCHAR(65535) + N'b' LIKE N'a[^x]b'` false, where both are true here.

Measured by the probe itself: the simulator's ignorable set disagreed with real's on 21,096 of the 65,536 code units under `Latin1_General_CI_AS` and 5,841 under `Latin1_General_100_CI_AS`, and matches it exactly under all 31 names.

## Symbol sort weighting (other SQL_\* / Windows / locale families)

`CultureCollation.Compare` (the `CompareInfo`-routed comparer behind every collation **other than the default**) gives hyphen (`-`) and apostrophe (`'`) the **minimal-weight** treatment SQL Server applies, while every other symbol keeps a real primary weight:

- **Non-minimal symbols (`#`, `+`, `,`, `!`, `~`, `_`, …) sort first** — ahead of digits and letters.
  .NET's `CompareOptions.IgnoreSymbols` would *strip* these (mis-ranking `'#500-75'` among the digits as `50075`); plain `CompareInfo` without it keeps them, which is what the comparer uses.
- **Hyphen and apostrophe drop out of the primary key** but carry a secondary weight, so the copy bearing the mark sorts *after*: `'coop' < 'co-op'`, `'cant' < "can't"`, `'A' < "'A"`.

Implementation: a fast path (`compareInfo.Compare(x, y, equalityOptions)`) when neither operand contains a minimal mark; otherwise a primary pass over hyphen/apostrophe-stripped copies, then `MinimalPunctuationTiebreak` (a two-pointer scan where a minimal mark sorts after a real character).
This is structurally faithful but not byte-exact for symbol-internal order or accent multi-level — only the [Latin1-General names](#the-latin1-general-names--byte-exact-sort) get exact tables.
The same approach could extend to other heavily-used names if a divergence surfaces.

## Locale-comparer sort-parity gap

The East Asian families order their scripts and ideographs by tables `CompareInfo` doesn't reproduce: ICU's `zh-CN` ranks Han before Latin where real ranks it after (`az` < `a中`, `Zebra` < `安徽`), reads polyphonic ideographs its own way (real reads 重 as *zhòng* and 长 as *cháng*), and each family interleaves Han with kana, Hangul and the compatibility blocks in an order of its own (`Korean_Wansung_CI_AS` puts Hangul before Latin).
The rank is interleaved rather than layered — a string-level script class, script-run segmentation and a class-vector prefix were each tried and fail on `az` against `a中` — so the fix is a per-character primary table.

`CjkPrimaryOrder` (`Collation.CjkPrimary.cs`) carries real's dense primary rank of every BMP code unit for six families — `Chinese_PRC`, `Chinese_PRC_Stroke`, `Chinese_Taiwan_Stroke`, `Japanese`, `Japanese_Unicode` and `Korean_Wansung` — read as `DENSE_RANK() OVER (ORDER BY NCHAR(n) + N'a' COLLATE <family>_CI_AI)` (the suffix keeps a space from reading as trailing), with the family's ignorable set at rank 0 (probed 2026-09-29 against SQL Server 2025).
Each table is stored as the code units' ascending runs in sort order, deflated, in `Collation.CjkPrimary.txt` (245 KB of base64 for the six).
A comparison that reaches past U+2E80 and that `CompareInfo` doesn't call equal walks the two strings' primary ranks, skipping the minimal-weight characters; a primary tie falls back to `CompareInfo` for the accent, case, kana and width levels, and equality and hashing stay `CompareInfo`'s, so `Compare == 0` exactly when `Equals`.
A versioned family borrows the unversioned table its ideograph order descends from (`Chinese_PRC_90` and `Chinese_Simplified_Pinyin_100` read `Chinese_PRC`'s, `Japanese_XJIS_140` reads `Japanese`'s, the bopomofo and Hong Kong families read `Chinese_Taiwan_Stroke`'s), which places the scripts right and most ideographs.

Measured as pairwise order agreement with real over 1,500 random strings mixing Latin, kana, common and random ideographs, Hangul, fullwidth forms and bopomofo (1.1 million pairs per collation):

| Collation | Before | After |
|---|---|---|
| `Chinese_PRC_CI_AS` / `_CS_AS` | 56.0% | 99.999% |
| `Chinese_PRC_Stroke_CI_AS` | 51.2% | 99.999% |
| `Chinese_Taiwan_Stroke_CI_AS` | 51.3% | 99.998% |
| `Japanese_CI_AS` / `_CS_AS` | 86.8% | 99.999% |
| `Korean_Wansung_CI_AS` | 99.2% | 99.998% |
| `Chinese_PRC_90_CI_AS` | 53.4% | 97.4% |
| `Chinese_Simplified_Pinyin_100_CI_AS` | 61.4% | 93.9% |
| `Chinese_Taiwan_Bopomofo_CI_AS` | 47.9% | 84.6% |
| `Chinese_Hong_Kong_Stroke_90_CI_AS` | 47.2% | 95.9% |
| `Japanese_XJIS_140_CI_AS` | 86.2% | 99.2% |
| `Korean_90_CI_AS` / `Korean_100_CI_AS` | 82.8% / 50.8% | 83.6% / 51.3% |

What's left in a shipped family is kana a `CompareInfo` tie equates where real doesn't (`け` / `ヶ`) and the prolonged sound mark `ー`, whose weight real takes from the kana before it.
A table of each family's own costs 25–80 KB deflated apiece; the versioned Korean families and the bopomofo ones are the ones borrowing worst.
**Equality, CI/CS / KS / WS folding, grouping and LIKE** keep `CompareInfo`'s answers.

## Binary collation storage-aware dispatch

`Latin1_General_BIN`, `Latin1_General_BIN2`, and `Latin1_General_100_BIN2_UTF8` each carry two comparer bodies and dispatch on the column's storage type via `Collation.ForVarcharStorage()`.
The virtual returns `this` by default; binary collations override to point at a storage-flavored sibling.
`VarcharSqlType.WithCollation` and `CharSqlType.WithCollation` call it at column-pin time; `NVarcharSqlType` / `NCharSqlType` don't substitute (UTF-16 storage matches the UTF-16 code-unit-order body).
Substituted siblings share the same `Name` so catalog views report one collation name and `Collation.Resolve` treats them as the same collation for cross-operand coercibility.

| Outer collation | nvarchar / nchar body | varchar / char body |
|---|---|---|
| `Latin1_General_BIN` | `BinaryCollation` via `StringComparer.Ordinal` at position 0; codepoint-combining at position 1+ (see "pre-2005 _BIN" note below) | `Cp1252BinaryCollation` — CP1252 byte sequence compare |
| `Latin1_General_BIN2` | `BinaryCollation` via `StringComparer.Ordinal` (UTF-16 code-unit ordinal throughout) | `Cp1252BinaryCollation` |
| `Latin1_General_100_BIN2_UTF8` | `BinaryCollation` (UTF-16 code-unit ordinal — `_UTF8` is a no-op on nvarchar storage) | `Utf8CodepointBinaryCollation` — codepoint-order compare (≡ UTF-8 byte order, ≡ surrogate-pair-combined scalar order) |

The three varchar bodies pairwise diverge on the same 0x80-0x9F window: codepoints whose CP1252 representation lands in that range scatter across the BMP — `€` U+20AC → CP1252 0x80, `ƒ` U+0192 → 0x83, `Ÿ` U+0178 → 0x9F, `‚` U+201A → 0x82.
CP1252 byte order, UTF-8 byte order, and UTF-16 code-unit order give three different rankings for any set spanning that window.
Probe-confirmed against SQL Server 2025: `varchar BIN2` of `{Z, €, ƒ, NBSP}` sorts Z, €, ƒ, NBSP (CP1252 byte order); same data on `varchar BIN2_UTF8` sorts Z, NBSP, ƒ, € (codepoint = UTF-8 byte order).

**Guard — nvarchar `_BIN2` is UTF-16 *code-unit*, not code-point; don't "fix" it.**
Microsoft's `_BIN2` documentation describes the ordering in code-point terms, which invites a well-meaning correction toward 32-bit scalar comparison.
Empirically (probed on SQL Server 2025) the box compares UTF-16 code units — i.e. surrogate pairs are compared as their two 16-bit halves, not as the combined scalar.
So `(nchar(0xD83D)+nchar(0xDE00)) < nchar(0xE000)` is **true** under `Latin1_General_BIN2` (emoji U+1F600 sorts before U+E000 because the high surrogate 0xD83D < 0xE000), even though the scalar 0x1F600 > 0xE000.
`StringComparer.Ordinal` is UTF-16 code-unit comparison, so `BinaryCollation` over `Ordinal` is byte-exact for nvarchar BIN2 *including* emoji / supplementary chars — adding code-point logic would break currently-correct behavior.
(The varchar `_BIN2_UTF8` substitute *is* codepoint-ordered, because UTF-8 byte order equals surrogate-pair-combined scalar order — that's the one place the two coincide.)

## Storage code page

`varchar` / `char` store through the collation's **own ANSI code page**, not CP1252 for everything.
`Collation.AnsiCodePage` carries it (what `COLLATIONPROPERTY(name, 'CodePage')` reports), and `Collation.StorageEncoding` is the matching encoder, interned per code page by `Collation.AnsiEncoding` with an `EncoderReplacementFallback("?")` so an unrepresentable character narrows to `?` the way real does rather than throwing.
Resolution lives in one place — `Collation.Parser.cs`'s `ResolveAnsiCodePage`, shared by `CreateInstance` and `TryGetMetrics` so storage and the catalog can't disagree: `_UTF8` → 65001, else a `CPnnn` name token (the SQL_\* family carries its page there — `CP1` = 1252, `CP850` = 850), else the per-prefix registry in `Collation.LcidCodePage.cs`.

Verified exhaustively: all **5540** names from `sys.fn_helpcollations()` report the same code page as the reference server, and storage bytes + `DATALENGTH` + `LEN` match byte-for-byte across one representative collation per code page (1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258, 874, 932, 936, 949, 950, 850, 437).

- **The byte budget is the code page's.**
  `varchar(N)` counts N **bytes**, so under a DBCS page fewer characters fit: five CP932 kana need ten bytes, and `varchar(5)` holds two.
  `Simulation.Coerce.EnforceMaxLength` measures with `GetByteCount`, and `Collation.ClipToByteBudget` does the clipping wherever a value is cut down — CAST/CONVERT to a narrower target, Msg 2628's `Truncated value:` prefix, a write under `SET ANSI_WARNINGS OFF` (which truncates where it would raise Msg 2628), and the `ALTER COLUMN` narrowing scan.
  It never splits a multi-byte character (or a surrogate pair), matching real: `CAST(<five kana> AS varchar(5))` yields two kana / four bytes, and the Msg 2628 text reports `'こん'`.
- **String functions stay character-based.**
  `LEN`, `LEFT`, `RIGHT`, `SUBSTRING` count characters while `DATALENGTH` and the declared width count bytes — so `LEFT(v, 2)` over CP932 kana is two characters and four bytes.
- **`ASCII` reads the argument's code page**, first byte only: `Ğ` under Turkish is 208 (CP1254), `こ` under Japanese is 130 (the CP932 lead byte).
  `UNICODE` is code-point based and so is code-page independent.
- **`CAST(varchar AS varbinary)` renders the stored bytes**, so it agrees with `DATALENGTH` over the same expression.
  This path used `Encoding.Latin1` before, which is ISO-8859-1 rather than CP1252 — it differs across 0x80-0x9F *and* best-fit-folds (`€` → `?`, `Š` → `S`, `—` → `-`), so the default collation returned wrong bytes for its own repertoire.
- **Binary collations byte-compare in their code page** (`AnsiBinaryCollation`), so `Japanese_BIN2` on varchar orders by CP932 bytes.
  Its buffers size by `GetByteCount`, not character count, which a DBCS page would overflow.

### Unicode-only collations — Msg 459

Twelve Windows prefixes have no ANSI code page at all (Assamese, Bengali, Divehi, Indic_General, Khmer, Lao, Maltese, Maori, Nepali, Pashto, Syriac, Tibetan); real reports their `COLLATIONPROPERTY` code page as **0** and rejects them on a char family type with **Msg 459** (Class 16 State 2, batch-aborting — `TRY`/`CATCH` doesn't intercept it):

> Collation 'Assamese_100_CI_AS' is supported on Unicode data types only and cannot be applied to char, varchar or text data types.

`Collation.RejectIfUnicodeOnly` fires from the `VarcharSqlType` / `CharSqlType` constructors — the one chokepoint every char-family pairing interns through, so the check runs once per triple and never on the cache-hit path, and `StorageEncoding` downstream is always code-page-bearing.
`text` needs its own gate at the CREATE TABLE / ALTER COLUMN column sites because it reports the shared `Collation.Baseline` instance rather than interning a per-column type (see [Type-side wiring](#type-side-wiring)).
`nvarchar` under these collations is accepted, as on real.

### UTF-8 storage encoding

Three modeled collations carry UTF-8 as their `StorageEncoding`: `Latin1_General_100_CI_AS_SC_UTF8`, `_CS_AS_SC_UTF8`, `_BIN2_UTF8`.
The encoding is read by `VarcharSqlType.Encode` / `Decode` / `GetVariableByteCount` (and the same trio on `CharSqlType`) at row-encode time, and by `Simulation.Coerce.EnforceMaxLength` for the per-row byte-budget check.
Net effects:

- **`DATALENGTH`** returns UTF-8 byte counts (`café` → 5, NBSP → 2, 😀 → 4) on varchar / char columns.
- **`varchar(N)`** budgets N **bytes**, not N characters: `é` (2 UTF-8 bytes) fits in `varchar(2)` exactly; appending one ASCII byte overflows to Msg 2628 / 8152.
- **`char(N)`** pads to N **bytes**: `é` in `char(5)` stores as the 2 UTF-8 bytes + 3 ASCII space bytes (= 5 bytes).
  The padding count is computed against the column's storage encoding via `NormalizeFixedLengthStringToByteCount` in `SqlValue`.
  Truncation walks runes to avoid splitting a UTF-8 sequence mid-codepoint.
- **Sort** behavior is varchar-storage-specific: `BIN2_UTF8` substitutes the `Utf8CodepointBinaryCollation` body (codepoint order); the two `*_SC_UTF8` siblings keep their `CompareInfo`-routed bodies (operate on UTF-16 strings; storage encoding doesn't affect them).
- **`nvarchar` / `nchar` with `*_UTF8` collation** is a partial no-op: storage stays UTF-16 (UTF-8 byte width never materializes), sort body stays the UTF-16-friendly one.
  The `_SC_` flag (on `_CI_AS_SC_UTF8` / `_CS_AS_SC_UTF8`) still affects LEN / SUBSTRING semantics on nvarchar — modeled separately under the `_SC_` gap.

### Microsoft-docs-vs-real-behavior gap: BIN2 is code *unit*, not code point

Microsoft's [Collation and Unicode Support](https://learn.microsoft.com/en-us/sql/relational-databases/collations/collation-and-unicode-support) page states "In a `BIN2` collation all characters are sorted according to their code points." This is **inaccurate for supplementary characters on nvarchar**.
Empirical behavior on SQL Server 2025 (probed, three routes — `NCHAR`-synthesized, parameter-passed .NET string, raw SQL literal — all agree): BIN2 nvarchar compares UTF-16 16-bit code units, which differs from code-point order when surrogate pairs are involved.

Demo: `(NCHAR(0xD83D) + NCHAR(0xDE00))` (the surrogate pair for 😀 U+1F600) sorts BEFORE `NCHAR(0xE000)` under BIN2, because the high surrogate D83D (0xD83D) < E000 (0xE000) as 16-bit values.
Under code-point order, U+1F600 (0x1F600) > U+E000 would put the emoji last.
Real SQL Server returns the code-unit answer; the simulator's `StringComparer.Ordinal` (which is also code-unit) matches.

Community sources documenting the same gap:
- [Solomon Rutzky — Differences Between the Various Binary Collations (Sql Quantum Leap, 2019)](https://sqlquantumleap.com/2019/03/13/differences-between-the-various-binary-collations-cultures-versions-and-bin-vs-bin2/): "the BIN2 collations, when dealing with NVARCHAR data, sort by code *unit*, not by code *point*."
- [SQLServerCentral mirror of the same analysis](https://www.sqlservercentral.com/blogs/differences-between-the-various-binary-collations-cultures-versions-and-bin-vs-bin2).

This aligns with the [Unicode specification](https://www.unicode.org/versions/latest/) — UTF-16 binary order is not codepoint order when supplementary characters are present.
SQL Server matches the Unicode spec; only its own product docs are out of step.
Don't "fix" the simulator by adding code-point logic — that would introduce a divergence where none exists.

The pre-2005 `_BIN` (not `_BIN2`) variant has a different, real quirk: at position 0 it's code-unit (same as BIN2), but at position 1+ it switches to code-point.
Probe-confirmed via `'Z'+emoji > 'Z'+nchar(0xE000)` returning TRUE under BIN and FALSE under BIN2.
**The simulator models this**: `Latin1_General_BIN.Compare` (the nvarchar body) overrides `BinaryCollation.Compare` to walk the strings with the asymmetric rule — first 16-bit unit raw, then surrogate-pair-combining scalar compare.
`Equals` / `GetHashCode` stay on `Ordinal` because equality of code-unit sequences implies equality of scalar sequences regardless of which rule walked them.

## `_SC_` function-semantics dispatch

`Collation.IsSupplementaryCharacterAware` (virtual, default `false`; overridden `true` on `Latin1_General_100_CI_AS_SC_UTF8` and `Latin1_General_100_CS_AS_SC_UTF8`) drives eight scalar functions to switch between UTF-16 code-unit semantics (non-`_SC_`) and Unicode-codepoint semantics (`_SC_`).
Each function reads the dispatch flag off its input value's `SqlType.Collation`, so a postfix `COLLATE …_SC_UTF8` flips the semantic per-call.
Probe-confirmed against SQL Server 2025.

| Function | Non-`_SC_` (code units) | `_SC_` (codepoints) |
|---|---|---|
| `LEN(N'😀')` | 2 | 1 |
| `SUBSTRING(N'😀X', 1, 1)` | lone high surrogate (`0xD83D`) | full emoji (`0xD83D 0xDE00`) |
| `LEFT(N'😀X', 1)` | lone high surrogate | full emoji |
| `RIGHT(N'X😀', 1)` | lone low surrogate (`0xDE00`) | full emoji |
| `CHARINDEX(N'X', N'😀X')` | 3 | 2 |
| `PATINDEX(N'%X%', N'😀X')` | 3 | 2 |
| `REVERSE(N'😀X')` | `X` + low surrogate + high surrogate (split pair) | `X` + full emoji (intact pair) |
| `UNICODE(N'😀')` | 55357 (high surrogate value) | 128512 (U+1F600 codepoint) |
| `STUFF(N'😀X', 1, 1, N'Y')` | `Y` + lone low surrogate + `X` (replaces 1 code unit) | `Y` + `X` (replaces full codepoint) |

`SupplementaryCharacters` (in `Parser/Expressions/`) holds the rune-walking helpers (`CodepointCount`, `CodepointToCodeUnit`, `CodeUnitToCodepoint`, `LeftByCodepoints`, `RightByCodepoints`, `ReverseByCodepoints`, `ReverseByCodeUnits`, `LeadingCodepoint`).
The non-`_SC_` path stays on .NET's native code-unit operations (`string.Length`, `Substring`, `IndexOf`, etc.), which already match real SQL Server's non-`_SC_` semantics.

**Lone-surrogate preservation:** the nvarchar / nchar / sysname / ntext row encoders byte-copy UTF-16 LE directly (`SystemNameSqlType.Utf16LeEncode` / `Utf16LeDecode` via `MemoryMarshal.AsBytes`) instead of routing through `Encoding.Unicode.GetBytes`, which silently rewrites lone surrogates to `U+FFFD` via its `EncoderReplacementFallback`.
Every other place Unicode text meets bytes takes the same helpers — `CAST` / `CONVERT` in both directions, `HASHBYTES`, `COMPRESS`, the `sql_variant` image, the TDS reader and writer and the BCP reader — since real keeps the raw unit through all of them (`CONVERT(varbinary, N'x' + NCHAR(55357))` is `0x78003DD8`, probed 2026-09-25).
Real SQL Server preserves lone surrogates end-to-end (probe-confirmed: `SUBSTRING(N'😀X', 1, 1)` on a non-`_SC_` column round-trips through `sys.columns` storage with the lone high surrogate intact); the byte-copy path keeps the simulator's fidelity bar.

## KS / WS suffix dispatch

`Latin1_General_CI_AS_KS_WS` is currently the only `_KS_WS`-marked collation in the recognized catalog.
Real SQL Server's `_KS_` (kanatype-sensitive) and `_WS_` (width-sensitive) suffixes flip the corresponding `IgnoreKanaType` / `IgnoreWidth` flags OFF.
Without them (e.g. plain `_CI_AS`), the trio { full-width katakana ア U+30A2, hiragana あ U+3042, half-width katakana ｱ U+FF71 } folds together under equality and DISTINCT.
With `_KS_WS` they distinguish.

`CultureCollation` takes optional `kanaTypeSensitive` / `widthSensitive` parameters (default `false`); the `Latin1_General_CI_AS_KS_WS` instance passes `true` for both.
Probe-confirmed against SQL Server 2025: `nchar(0x30A2) = nchar(0x3042)` is FALSE under `_KS_WS` and TRUE under plain `_CI_AS`.

## An unresolved collation propagates — `Coercibility.NoCollation`

A conflict a producing operator can't settle doesn't always report there.
SQL Server's collation-precedence rules define a fourth coercibility label, **No collation**, and an expression carrying it travels until something demands a definite collation — which is then what reports, in its own words.
The simulator models the label as `Coercibility.NoCollation` plus an `UnresolvedCollation` (a `Collation` remembering the conflicting pair and the operator that produced it, interned per triple, every member delegating to the left operand's collation so a marker that escapes the modeled consumers degrades rather than crashes).
`Collation.Resolve` returns the marker for any pairing that touches one, so propagation is the default and each consumer opts into reporting.

### Which producers travel and which report

`UnresolvedCollation.Settle` holds the whole rule for `+` / `||` / `CASE`-arm unification / `UNION ALL`'s per-column unification, all probe-confirmed against SQL Server 2025:

| Operand pair | `varchar` / `char` result | `nvarchar` / `nchar` result |
|---|---|---|
| two `Explicit` postfixes | **Msg 468** naming the operator | **Msg 468** naming the operator |
| an operand already carrying a conflict | **Msg 456** naming the *producing* operator | travels |
| otherwise unresolvable | **Msg 457** naming this operator | travels |

The split is the **result family**, not the operator: a `varchar` carries a code page and can't be materialized without knowing which, so the conversion fails where it stands; UTF-16 needs none, so the conflict rides along.
A mixed `varchar` + `nvarchar` pair promotes to `nvarchar` and travels.
`CONCAT` / `CONCAT_WS` travel for **both** families — they mark the result rather than materializing one, and a `varchar` `CONCAT` reports only when something downstream converts it.
Real spells string `+` as `add`, `||` as `concat`, and upper-cases the set operator.

### Msg 451 — the output-column message

An output column has to name one collation, so a conflict that reaches a projection slot reports **Msg 451 State 1**:

```
Cannot resolve collation conflict between "R" and "L" in concat operator occurring in SELECT statement column 1.
```

Note there's no leading *the* (Msg 468's wording has one), the collation names follow the same right-then-left order every other site uses, the operator named is the one that **produced** the conflict however far upstream, and the tail names the **clause and the 1-based ordinal of the slot being settled**.
`Selection.BuildSqlProjection` checks each bound term via `RequireSettledOutputCollation`:

| Clause | Ordinal (probe-confirmed) |
|---|---|
| `SELECT` | the projection's 1-based position |
| `ORDER BY` | the ORDER BY item's 1-based position, independent of the select list |
| `GROUP BY` | the grouping term's position **plus one** — the grouped projection real builds carries one column ahead of the keys |

**The select list settles last.**
A `WHERE` predicate's Msg 4191, a `GROUP BY` term's Msg 451 and an `ORDER BY` term's all report ahead of it, and an `ORDER BY` naming the conflicted projection column — by ordinal *or* by alias — reports as `ORDER BY statement column <n>` rather than the select list's slot (all probe-confirmed).
So the select-list slot is recorded during the projection loop and raised only once every other clause has bound.

Only a **statement's own** projection names a collation, and two of those don't: an `INSERT … SELECT` source and a `SELECT @v = …` list, where the assignment target supplies one (see Msg 456 below).
A **nested** query — a derived table, CTE, subquery, `EXISTS` body, or a view or inline function inlined into its caller — never settles its own output collation: the conflict travels on in its column's type, and whatever reads that column reports it as its own (probed 2026-09-23).
So `SELECT COUNT(*) FROM (SELECT concat(a, b) x …) d` answers, `SELECT x FROM (…) d` is Msg 451 at the outer select list, `WHERE d.x = 'q'` is Msg 4191, and an `INSERT` or `SET @v = (SELECT …)` reading it is Msg 456.
`QueryScope.NamesOutputCollation` carries the rule.
`SELECT … INTO` is not an assignment in that sense: it has to materialize a column of its own, so it raises.
Like every other site the check binds at compile time, so an empty rowset and a `CREATE PROCEDURE` whose body carries the conflict both raise, the latter attributed to the module.

### Msg 4191 — the consuming operation reports

An operation that needs a definite collation to do its work reports **Msg 4191 State 9**, naming only itself — not the conflicting pair, and not the operator that produced the conflict:

```
Cannot resolve collation conflict for len operation.
```

The demanding set (probe-confirmed, each naming itself lower-cased): `LEN`, `UPPER`, `LOWER`, `LTRIM`, `RTRIM`, `TRIM` — real's own odd one out, which reports `Trim` capitalized — `SUBSTRING`, `CHARINDEX` and `PATINDEX` (from either operand), `REPLACE`, `REVERSE`, `STUFF`, `LEFT`, `RIGHT`, `SOUNDEX`, `DIFFERENCE`, `TRANSLATE`, `UNICODE`, `STRING_AGG`, the `MAX` / `MIN` aggregates, `LIKE`, and every comparison — which uses the spelled-out vocabulary Msg 468 uses (`equal to`, `not equal to`, `less than`, `greater than or equal to`), with `IN` and `BETWEEN` reporting through the comparison they desugar to.
The gate is `UnresolvedCollation.Require`, reached from `StringScalars.BindArgument` / `BindCoercedArgument` (so the compile-time and per-value paths can't drift, exactly as the Msg 8116 legacy-LOB gate beside it doesn't) and from `BooleanExpression.RequireResolvableCollation`, which both `Bind` and the per-value `CompareValuesPromoted` run.

The complement travels instead: `REPLICATE`, `STRING_ESCAPE`, `QUOTENAME`, `SPACE`, `ISNULL`, `IIF`, a `CAST` to a Unicode target, and a `+` whose other operand is settled all hand the conflict onward — those sites pass `propagatesUnresolvedCollation: true` where they share the bind seam.
`DATALENGTH`, `ASCII`, `COUNT`, `HASHBYTES` and `FORMAT` never look at collation and answer normally.

### Msg 446 — DISTINCT / CONVERT / COLLATE

One message, one State per operation, naming the producing operator and the consuming one together:

| Operation | State | Applies to |
|---|---|---|
| `DISTINCT` | **11** | both families, the projection-level `SELECT DISTINCT` and an aggregate's own `COUNT(DISTINCT …)` |
| `CONVERT` | **20** | a `CAST` / `CONVERT` to a `varchar`-family target (spelled `CONVERT` for `CAST` too); a Unicode target propagates instead |
| `COLLATE` | **6** | a postfix on a `varchar`-family value; on the Unicode family the postfix settles the conflict outright and the statement runs |

A conversion never *resolves* a conflict — it inherits the source's collation, marker included.

### Msg 456 — an assignment target that can't settle it

Which family raises at an implicit conversion is the **source**'s, not the destination's (probe-confirmed): an unresolved `nvarchar` assigns into a `varchar` column silently, where an unresolved `varchar` is refused even assigning into `nvarchar`.

```
Implicit conversion of varchar value to varchar cannot be performed because the resulting collation is unresolved due to collation conflict between "R" and "L" in concat operator.
```

Note the wording differs from Msg 457's in two places — *the **resulting** collation is unresolved due to collation conflict* where 457 says *the collation of the value is unresolved due to **a** collation conflict* — and the operator named is the one that produced the conflict, not the assignment consuming it.
`UnresolvedCollation.RequireAssignable` runs it at the `INSERT … SELECT` source, the `SELECT @v = …` item (through `AssignmentExpression.GetSqlType`, which binds its source for exactly this), and both of `UPDATE`'s `SET`-value bind loops.

## Set-operation collation resolution

Cross-collation branches of a set operation must resolve to a single output collation, and the check binds at **compile time** — probe-confirmed against SQL Server 2025 that it fires on empty tables.
`Selection.Execution.SetOps.cs`'s per-column unification loop runs at parse, so the check lands in the right phase naturally.

| Operation | Error | Wording |
|---|---|---|
| `UNION` / `INTERSECT` / `EXCEPT`, branches freshly conflicting | **Msg 468, State 9** | `Cannot resolve the collation conflict between "R" and "L" in the UNION\|INTERSECT\|EXCEPT operation.` |
| `UNION` / `INTERSECT` / `EXCEPT`, a branch already unresolved | **Msg 5335, State 1** | `The data type nvarchar cannot be used as an operand to the UNION, INTERSECT or EXCEPT operators because it is not comparable.` |
| `UNION ALL` | per [`UnresolvedCollation.Settle`](#which-producers-travel-and-which-report) — **Msg 457** for a `varchar` result, the marker for `nvarchar`, then **Msg 451** at the combined output column |

The value-comparing operators have to dedup, and a value with no collation has no comparison to dedup by; `UNION ALL` only concatenates, so it settles like the string operators do.
The combined column *is* an output column when the set operation is a statement's own query, so its ordinal counts by output position like the select list's; in a nested query or an assignment source it travels on like any other nested column.
Note real upper-cases the set operator where it lower-cases the comparison / `add` names, and says *operation* for 468 versus *operator* for 457.
Collation names follow the same right-then-left order the comparison sites use.

Resolution follows `Collation.Resolve`'s precedence, so these bind cleanly: an explicit `COLLATE` on one branch (Explicit outranks Implicit), a literal branch (coercible-default yields), matching collations, and non-string columns.
A `CAST` does **not** resolve a conflict — the cast result inherits the source column's collation, so `CAST(x AS nvarchar(10))` on both branches still raises (probe-confirmed).

## Known gaps

- **A ligature doesn't expand outside the Latin1-General tables.**
  Real treats a ligature as equal to its expansion at the primary level under every non-binary collation probed, `Japanese_CI_AS` and `French_CI_AS` included (`N'æ' = N'ae'` and `N'ß' LIKE N'ss'` are true; probed 2026-10-02), and the [byte-exact tables](#the-latin1-general-names--byte-exact-sort) expand it; every other name runs through `CompareInfo`, which holds the two apart.
  The probed set: `Æ`→`AE`, `æ`→`ae`, `Þ`→`TH`, `þ`→`th`, `ß`→`ss`, `Ĳ`→`IJ`, `ĳ`→`ij`, `Œ`→`OE`, `œ`→`oe`, `Ǉ`→`LJ`, `ǈ`→`Lj`, `Ǌ`→`NJ`, `ǋ`→`Nj`, `Ǳ`→`DZ`, `ǲ`→`Dz`, `ﬀ`→`ff`, `ﬁ`→`fi`, `ﬂ`→`fl`, `ﬃ`→`ffi`, `ﬄ`→`ffl`, `ﬆ`→`st`.
  Nearby characters that look like members and are **not**: `ẞ` (U+1E9E) doesn't fold to `SS`, `ﬅ` (U+FB05) doesn't fold to `st`, and `№` / `™` / `½` / `ﬓ` don't fold at all.
  Closing it for another family wants that family's own table, since the Latin1-General one's letter order isn't a locale's tailoring.
- **Characters outside the Latin1-General tables compare through `CompareInfo`.**
  A pair holding one falls back for the whole comparison, so its order against the in-table characters is ICU's: `N'µ' = N'μ'` (micro sign against Greek mu) is true here and false on real, and `ŉ` (U+0149), `ẚ` (U+1E9A), `⁄` (U+2044) and, under the `_100_` names, `¼ ½ ¾` sort elsewhere than real puts them — each expands to a sequence holding a character the probe's ranks couldn't place (probed 2026-10-02 against SQL Server 2025).
  A Thai tone mark leading a string, with no letter to ride on, is the one in-table shape that diverges.
- **A space variant is a space to the culture comparers' `=`, and the weightless-mark order is approximate.**
  A search holds the no-break space and the U+2000..U+200A spaces apart from U+0020 as real does — only U+0020 and the ideographic space U+3000 match a space in `CHARINDEX`, `REPLACE`, `PATINDEX`, `TRANSLATE`, `TRIM`, `STRING_SPLIT` and `LIKE` (probed 2026-09-29 against SQL Server 2025 over every BMP code unit; `WeightlessCharacters.ForSearch` gives each a private-use stand-in) — but `N'x' + NCHAR(160) = N'x ' COLLATE French_CI_AS` is still true here and false on real, since `=` under the culture comparers reads them through `CompareInfo` (the Latin1-General tables get it right).
  The characters `CompareInfo` ignores and real weighs all take a final-level weight, where real gives U+200B and U+200C a low primary one and the Hebrew accents a diacritic one: under `French_CI_AS` real orders each followed by `a` as `space < U+3000 < U+00A0 < tab < U+200A < U+200B < U+200C < U+2028 < !`, where the simulator orders tab and U+2028 first and puts U+200B and U+200C after the Hebrew accents, so their order against other characters differs while equality matches (probed 2026-10-02 against SQL Server 2025).
  The Latin1-General tables place all but U+3000, which lies outside them.
  Two further classes surfaced by the same sweep and aren't modeled yet: `CompareInfo` equates `ª`, `ℬ` and the circled letters `Ⓐ` `Ⓑ` `ⓐ` `ⓑ` with the Latin letter they decorate where real's search does not, and real equates `ʙ` (U+0299) with `b` where `CompareInfo` does not.
- **A standalone combining mark matches any other standalone mark on real.**
  `TRANSLATE(NCHAR(0x0308) + …, NCHAR(0x0301), N'd')` substitutes on real and doesn't here, and the same equivalence shows up in `TRIM`'s character set, in `REPLACE`'s pattern and in a `STRING_SPLIT` separator — real appears to compare the marks at a weight level `CompareInfo` doesn't expose, since a mark attached to a base letter stays distinct in both engines.
  A differential fuzz of the five scalars against live (3,000 random cases per seed over an alphabet holding three bare marks) puts the whole class at ~1% of cases; with the marks removed from the alphabet the same fuzz is 0.2%, and every remaining case is the ligature or zero-weight entry above.
- **An unresolved collation reaching a consumer the marker model doesn't cover.**
  The catalog under [Msg 4191](#msg-4191--the-consuming-operation-reports) is what probing established; a value with no collation that reaches full-text, spatial, XML or the JSON builders isn't gated, and a conflict that survives to execution falls back to the left operand's collation rather than raising.
- **An operator that converts an unresolved string doesn't refuse it.**
  Real raises Msg 456 when arithmetic or a set operator's type unification implicitly converts a `varchar` whose collation is unresolved (`concat(a, b) + 1`, `… UNION ALL SELECT 1`, `… UNION ALL SELECT N'q'`, each naming the destination type), and Msg 457 when `+` meets one (`SET @v += (SELECT concat(a, b) …)`); the simulator converts the value (a runtime Msg 245 for a non-number) or, at a statement's own set operation, reports the output column's Msg 451 (probed 2026-09-23).
  A conversion to `datetime` is the odd one out on real too: it raises the ordinary runtime Msg 241.
- **Msg 456 names the source type as its destination too.**
  The seam that raises it carries the value's type, not the target's, so a *cross-family* assignment (`insert <nvarchar col> select concat(<varchar pair>)`) reads `varchar value to varchar` where real reads `varchar value to nvarchar`.
  Number, State, the collation pair and the producing operator all match; the same-family assignment — much the more common one — is verbatim.
- **A bind error in a statement over a table the batch creates is catchable here and isn't on real.**
  Both engines compile the batch first, so a collation conflict against existing tables is uncatchable in both; a statement whose table didn't exist yet binds only when it runs, where real's error is still uncatchable and the simulator's is an ordinary one.
  Shared with every other bind error rather than specific to collation; see [`errors.md`](errors.md#bind-errors-in-a-deferred-statement-are-catchable-here-and-arent-on-real).
- **`text` / `ntext` columns can't be declared with an explicit COLLATE in the simulator.**
  Real SQL Server allows it; the simulator's single-instance modeling collapses all text/ntext to the default, so a `text` column stores CP1252 regardless of the clause.
  The clause is still *validated* — a Unicode-only collation on `text` raises Msg 459 from the column-declaration site (see [Unicode-only collations](#unicode-only-collations--msg-459)) — it just isn't pinned.
  Low impact (text/ntext deprecated since SQL Server 2005).
- **Sysname's collation is always `Collation.Baseline`** at `Implicit` rank — real SQL Server's sysname inherits the server's catalog collation which can differ from the user database's collation; the simulator's single-instance modeling collapses them.
- **Pre-v100 collation sort divergence on supplementary chars at position 1+, outside the Latin1-General tables.**
  Probe-confirmed against SQL Server 2025: the pre-v100 names sort `Z+emoji` BEFORE `Z+U+E000`, while the v100 family sorts the other way.
  The Latin1-General tables reproduce both (the unversioned table weighs no surrogate); every other pre-v100 name routes through `CompareInfo`, which compares by code point and so behaves like v100.
- **Non-`_SC` `TRANSLATE` over surrogate halves.**
  Here each half maps by its own position; real reads the halves as weightless riders on one character, by a rule not yet explained: `TRANSLATE(N'x' + <pair>, <pair>, N'ZQ')` is `xZZ`, `TRANSLATE(N'a😀', N'😀', N'xy')` is `axx` (`axy` here), `TRANSLATE(N'😀😁', N'😁', N'xy')` is `xxxx`, a lone low surrogate looked up in a list holding only the high one translates (`TRANSLATE(N'a' + NCHAR(56832), NCHAR(55357), N'x')` is `ax`), yet `TRANSLATE(N'😀', N'x😀', N'abc')` leaves the input unchanged (probed 2026-09-29 and 2026-10-01, re-checked 2026-10-03 against SQL Server 2025).
- **A string operand moved into a `_UTF8` collation by `LIKE` or `CHARINDEX` keeps its characters**, where real converts it within its declared byte budget as a comparison already does here: `CHARINDEX('ä', CAST('ä' AS varchar(10)) COLLATE Latin1_General_100_CI_AI_SC_UTF8)`, whose `varchar(1)` needle can't hold the two-byte `ä`, is 0 on real and 1 here (probed 2026-10-03 against SQL Server 2025).
- **`STRING_AGG(a, b)` over two columns of conflicting collations** is Msg 468 on real, which settles the collation before the separator check, and Msg 8733 here (probed 2026-10-03 against SQL Server 2025).
- **`TERTIARY_WEIGHTS`** is Msg 195 here; real accepts a `varchar` argument under a SQL collation and refuses an `nvarchar` one with Msg 8116 (probed 2026-10-02 against SQL Server 2025).

## Cross-references

- Database-level `ALTER DATABASE COLLATE` and the parser-driven recognition gate → [`database-options.md`](database-options.md).
- BACPAC import collation handling (loader warns on names the parser rejects and continues) → [`bacpac-loader.md`](bacpac-loader.md).
