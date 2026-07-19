# Batch grammar: statement separators

Statements are separated by an optional `;`.
Real SQL Server's relaxed grammar lets most statement pairs sit adjacent (`declare @v int = 7 select @v`, `set @v = 1 set @w = 2`, `insert t values (1) select * from t`, `begin tran ... commit`); the simulator follows.
Two enforced exceptions match SQL Server's specific rules:
- A CTE (`WITH`) directly following another statement raises **Msg 319 St 1** (verbatim wording: `Incorrect syntax near the keyword 'with'. If this statement is a common table expression, an xmlnamespaces clause or a change tracking context clause, the previous statement must be terminated with a semicolon.`).
  A `WITH` at batch start (or right after a `;`) is fine.
  The check fires both at `Simulation.CreateResultSetsForCommand`'s top-level dispatch (`requireSemicolonBeforeCte` flag) and inside `Selection.Parse`'s projection-element switches — the latter is where `select 0 with cte ...` surfaces it before the SELECT can complete.
- A `MERGE` not terminated by `;` raises **Msg 10713 St 1** (`A MERGE statement must be terminated by a semi-colon (;).`) regardless of whether another statement follows or the batch ends.
  The check sits at the dispatch site immediately after `ParseMerge` returns, before any cursor normalization.

The dispatch loop drains optional `;`s at the top of each iteration and trusts each parser to leave `Token` at its first un-consumed token (the `ParserContext` lookahead-position contract).
Parsers that historically ended on the last token they consumed (DBCC's closing `)`, SET-session-state's `ON`/`OFF`) get a one-token advance via `IsStatementBoundary` after dispatch — Token already at `;`, end-of-batch, or a recognized statement-starting keyword is left alone.

`Simulation.IsStatementBoundary(Token?)` is the **single source of truth** for "does this token begin a new top-level statement (or a hard boundary — `null` / `;` / the contextual `THROW`)?"
It answers `true` for the full statement-keyword set: SELECT / INSERT / UPDATE / DELETE / MERGE / BEGIN / COMMIT / ROLLBACK / SAVE / CREATE / DROP / ALTER / DBCC / SET / DECLARE / WITH / IF / ELSE / END / WHILE / BREAK / CONTINUE / RETURN / PRINT / RAISERROR / WAITFOR / TRUNCATE / USE / GRANT / REVOKE / DENY / OPEN / FETCH / CLOSE / DEALLOCATE / EXEC / EXECUTE.
Four consumers route through it so a new statement keyword is added in exactly one place:

- the dispatch loop's post-statement cursor normalization + error-recovery scans;
- `Selection.Parse`'s two projection-list terminator switches (the `WITH` case is checked *before* the shared predicate so its more-specific Msg 319 wins; the switch matches only `ReservedKeyword`, so a following statement's keyword ends the projection while column-name-like contextual keywords are unaffected);
- `ParseExecArguments` — an EXEC argument list stops at any statement start (reserved statement keywords can't be bare argument values, so this never truncates a legitimate literal / `@var` / DEFAULT / OUTPUT / NULL / `@@`-niladic arg);
- `ConsumeToStatementBoundary` — the principal-DDL parse-and-discard tail (`FROM LOGIN` / `WITH PASSWORD` / `DEFAULT_SCHEMA`).

This is why semicolon-less statement sequences work for the full set, e.g. `select 1\nexec xp_msver`, `declare @x int\nuse master`, `if 1=1 select 1\nfetch c`.
Before unification these predicates had drifted (EXEC/EXECUTE missing from several), so SSMS's semicolon-less AlwaysOn probe died with Msg 102.

# Reserved keywords as identifiers

The tokenizer classifies a bare word as a `ReservedKeyword` iff it matches a `Parser/Keyword.cs` enum member (`UnquotedString.CheckReserved`), and reserved words can't stand in as identifiers — `SELECT 1 AS from` / `c.user` raise **Msg 156**.
So the enum is the sole gate on which words are usable as unquoted identifiers, and `Tests.Internal/ReservedKeywordsTests` pins it to Microsoft's [canonical reserved-keyword list](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/reserved-keywords-transact-sql) in both directions.

Two canonical entries are **deliberately omitted** from the enum because real SQL Server doesn't actually enforce them as reserved (`ReservedKeywordsTests.DocumentedOmissions`):

- **`WITHIN GROUP`** — a two-word entry whose component words aren't independently reserved (`WITHIN` is contextual; `GROUP` is covered).
- **`PRECISION`** — on the list only because it forms the `DOUBLE PRECISION` type name, but probe-confirmed (SQL Server 2025, 2026-07-15) fully usable as an identifier in **every** position: dotted member (`clmns.precision`), bare projection (`SELECT precision FROM …`), alias (`SELECT 1 AS precision`), table alias, and `ORDER BY` all succeed.
  SMO's SSMS column-node query reads `CAST(clmns.precision AS int)` off `sys.all_columns`, so reserving it blocked a real client.
  Server behavior is authoritative over the doc list, so `Precision` is not a `Keyword`.

# Double-quoted identifiers / `SET QUOTED_IDENTIFIER`

`"…"` is dual-natured, switched by the session `QUOTED_IDENTIFIER` option (**default ON**).
The tokenizer's `NextToken(…, bool quotedIdentifiers)` reads the parser flag threaded from `ParserContext.QuotedIdentifiers` (seeded from `SimulatedDbConnection.QuotedIdentifiers`):

- **ON** — `"foo"` tokenizes as a `DelimitedIdentifier` (`Parser/Tokens/DelimitedIdentifier.cs`, the renamed `BracketDelimitedString`), identical to `[foo]`.
  `""` is the embedded-quote escape; `[`, `]`, `'` are ordinary characters inside.
  So `"a""b"` → identifier `a"b`, and `"a]b'c [d"` is that exact name.
  Reserved words (`"select"`) and spaces-only (`"   "`) are legal identifiers.
  An unresolvable quoted column raises Msg 207 (`Invalid column name '…'`).
- **OFF** — `"foo"` tokenizes as a varchar `Literal`, typed exactly like `'foo'` (same collation/coercibility).
  `""` is the escape, so `"a""b"` → string `a"b`, `"it's"` → `it's`.
  Empty `""` is a valid **empty string**, not an error.
  Concatenation and string-literal aliasing work (`"a" + 'b' + "c"` → `abc`; `SELECT 1 AS "X Y"` names the column via string-literal-alias).
  Brackets `[…]` stay identifiers regardless.

`N` is **not** a Unicode prefix for double quotes (unlike `N'…'`): `N"foo"` tokenizes as identifier `N` followed by `"foo"`, so in a select list it reads as column `N` aliased `foo` → Msg 207 for `N`.

Empty `""` (ON) and properly-closed empty `[]` raise **Msg 1038** (class 15, **state 4**, `EmptyColumnAlias()`) at the *tokenizer* level — so at every identifier position (`SELECT 1 AS ""`, `SELECT [] FROM t`, `CREATE TABLE ""(c int)`), not only select-list aliases.
An unclosed `"` (either mode) raises **Msg 105** via `UnclosedStringLiteral(body)`, whose message echoes the scanned body: `Unclosed quotation mark after the character string '<body>'.` (the `'…'` / `N'…'` / `"…"` scanners share `ParseQuotedBody`, hence one Msg-105 shape).

`@@OPTIONS` (`Parser/Expressions/Value.cs` `FromAtAtOptions`) returns 5432 with **bit 256** tracking the parse-position QI setting — `@@OPTIONS & 256` is 256 under ON, 0 under OFF.
The **plan cache** key (`PlanCacheKey`, `Simulation/Simulation.cs`) includes the `QuotedIdentifiers` bool, so the identical text `SELECT "abc"` caches separately under ON vs OFF and never replays the wrong reading.

## Scoping — parse-time, textual order

`SET QUOTED_IDENTIFIER ON|OFF` and `SET ANSI_DEFAULTS ON|OFF` (whose bundle includes QI) apply through `ApplyQuotedIdentifierOption` (`Simulation/Simulation.Set.cs`), including the comma multi-option form (`SET QUOTED_IDENTIFIER, ANSI_NULLS OFF`).
The application is **parse-time and NOT gated on skip-mode**, matching SQL Server:

- **Textual order, not control flow** — a `SET` inside a never-taken branch still applies to everything textually after it: `IF 1 = 0 SET QUOTED_IDENTIFIER OFF; SELECT "deadlit"` returns the string `deadlit`.
  And at the top level the change also **persists to the session**, so a subsequent separate command reads `"…"` the same way.
- **Forward-only** — a statement *before* the `SET` parses under the prior setting: with the session ON, `SELECT "a1"; SET QUOTED_IDENTIFIER OFF; SELECT "a2"` raises Msg 207 for `a1` (parsed as identifier before the flip).
- **Top level → flips both** the in-flight tokenizer flag and the session setting (`SimulatedDbConnection.QuotedIdentifiers`), so it crosses command boundaries on the same connection.
- **Dynamic SQL** (`EXEC('…')` / `sp_executesql`, marked by `ProcFrame.IsDynamicSql`) flips **only its own batch** — the session is unaffected (`@@OPTIONS & 256` unchanged afterward).
- **Procedure / function / trigger bodies ignore the `SET` entirely** (SQL Server's "ignored in a stored procedure" rule) — neither the body's own reading nor the session changes.

## Divergences

- **Per-object creation-time QI capture is NOT modeled.**
  Real SQL Server stamps procedures / views / triggers / tables with the `QUOTED_IDENTIFIER` in effect at CREATE time (`sys.sql_modules.uses_quoted_identifier`, `OBJECTPROPERTY(id, 'IsQuotedIdentOn')`) and executes their bodies under that captured setting regardless of the caller's session.
  The simulator re-parses bodies under the **executing session's** current setting instead.
  Rare legacy pattern (creating an object under a non-default QI and relying on the stamp); most code runs everything under the default ON.
- **Multi-statement-TVF bodies treat a `SET QUOTED_IDENTIFIER` as top-level** rather than rejecting it (real SQL Server disallows `SET QUOTED_IDENTIFIER` inside a function body).

## Expression depth limits (Msg 8631 / Msg 191 / Msg 125)

A .NET stack overflow is uncatchable and process-fatal — unacceptable for an in-process library handed a pathological query.
So no recursive spine that user SQL can drive arbitrarily deep is allowed to overflow: flat operator chains parse **and** evaluate iteratively (no per-term recursion), genuinely-nested shapes are bounded by SQL Server's own graceful structural errors, and a runtime stack probe backstops whatever remains.
Thresholds probed against SQL Server 2025 (2026-07-18).

### Flat left-associative chains — no cap (iterative)

`Expression.Parse` parses binary-operator chains (`+ - * / % & | ^`) by **iterative precedence-climbing** (`ParseBinaryContinuation`), not the former ctor-recursion, so `1 + 1 + … + 1` of any length builds a left-leaning tree with parse recursion bounded by the number of precedence levels (2), not the term count.
Evaluation is iterative too: `TwoSidedExpression.Run` / `GetSqlType` walk the left spine in a loop (recursing only into right operands, whose depth is paren-bounded), and boolean `AND` / `OR` chains collapse to **n-ary** `AndExpression` / `OrExpression` nodes evaluated in a loop.
A run of `NOT` prefixes collapses at parse (three-valued `NOT` is an involution).
So flat `+` / concat / `AND` / `OR` chains of tens of thousands of terms parse and run without recursion.

**Divergence (acceptable direction):** real SQL Server caps flat chains at a genuine stack limit — a 3030-term `1 + 1 + …` or `'a' + 'a' + …` chain succeeds, 3031 raises **Msg 8631**; an `AND` chain succeeds to 8871, 8872 raises Msg 8631.
The simulator has **no** artificial cap on flat chains — being more permissive than real on Msg 8631 (a physical limit, not a semantic one) is fine, so none is added.
`IN`-list length is likewise uncapped on both.

### Nested shapes — shared weighted budget, Msg 191

Parens, scalar subqueries, and function-argument lists share **one** nesting budget (`ParserContext.NestingDepth`, cap `Expression.MaxNestingDepth` = 500), each construct charging its cost on entry and refunding it in a `finally`; crossing the cap raises **Msg 191, Class 15** (`StatementNestedTooDeeply()`, `Some part of your SQL statement is nested too deeply. …`).
Probe-confirmed 2026-07-18 that real pools these into a single budget (1000 nested parens + 14 nested funcs fails; 150 subqueries + 116 parens fails) where a subquery level costs ≈ 6 paren levels — the simulator mirrors the ratio: `ParenNestingCost` = `FunctionCallNestingCost` = 1, `SubqueryNestingCost` = 6.

**Divergence:** real's absolute limits are higher and stack-dependent (1015 nested parens / 1013 nested funcs / 168 nested subqueries all succeed; +1 each raises Msg 191).
The simulator's parse frames are fatter — a 1 MB Debug thread parses only ~990 nested parens before the stack probe would claim the shape as Msg 8631 — so the cap is set to 500 (paren/func limit 500, subquery limit ⌊500/6⌋ = 83), giving ~2× headroom on the tightest test config (1 MB Debug) while preserving the probed subquery ≈ 6× paren ratio.
Deep **function** nesting alone has fatter frames still (~40 levels on a 512 KB thread, ~80–100 on 1 MB): on an adequate stack it reaches the 500-unit budget and raises Msg 191, but on a tight thread the runtime probe pre-empts with Msg 8631 (real always raises Msg 191 here).
Both outcomes are graceful.

### Nested CASE / IIF — Msg 125, cap 10

`CASE` / `IIF` lexical nesting is capped at ten levels (`ParserContext.CaseDepth`, `MaxCaseNestingDepth`), an eleventh raising **Msg 125, Class 15** (`Case expressions may only be nested to level 10.`) — an **exact** match to real.
The **state** identifies the construct entered at the eleventh level: **State 4** for a searched/simple `CASE`, **State 2** for `IIF` (which desugars to a searched CASE).
Probe-confirmed 2026-07-18: nesting in a `WHEN` condition counts identically to a `THEN` / `ELSE` result, the count is **not** reset by a scalar-subquery boundary, and a mixed CASE/IIF stack shares one counter (the innermost-entered construct sets the state).

### Msg 8631 backstop

**Msg 8631, Class 17** (`ServerStackLimitReached()`, `Internal error: Server stack limit has been reached. …`) is raised from a runtime stack probe (`RuntimeHelpers.EnsureSufficientExecutionStack()`) at the top of `Expression.Parse` and `BooleanExpression.ParseNot` — the faithful mechanism, since real's Msg 8631 is likewise a genuine stack probe whose threshold scales with the thread stack.
It composes across nested proc / dynamic-SQL batches (it measures the real stack) and catches any deep recursive shape whose frames outrun a deterministic cap (deep function nesting on a tight thread; deeply parenthesized boolean predicates).
