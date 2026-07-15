# Batch grammar: statement separators

Statements are separated by an optional `;`. Real SQL Server's relaxed grammar lets most statement pairs sit adjacent (`declare @v int = 7 select @v`, `set @v = 1 set @w = 2`, `insert t values (1) select * from t`, `begin tran ... commit`); the simulator follows. Two enforced exceptions match SQL Server's specific rules:
- A CTE (`WITH`) directly following another statement raises **Msg 319 St 1** (verbatim wording: `Incorrect syntax near the keyword 'with'. If this statement is a common table expression, an xmlnamespaces clause or a change tracking context clause, the previous statement must be terminated with a semicolon.`). A `WITH` at batch start (or right after a `;`) is fine. The check fires both at `Simulation.CreateResultSetsForCommand`'s top-level dispatch (`requireSemicolonBeforeCte` flag) and inside `Selection.Parse`'s projection-element switches — the latter is where `select 0 with cte ...` surfaces it before the SELECT can complete.
- A `MERGE` not terminated by `;` raises **Msg 10713 St 1** (`A MERGE statement must be terminated by a semi-colon (;).`) regardless of whether another statement follows or the batch ends. The check sits at the dispatch site immediately after `ParseMerge` returns, before any cursor normalization.

The dispatch loop drains optional `;`s at the top of each iteration and trusts each parser to leave `Token` at its first un-consumed token (the `ParserContext` lookahead-position contract). Parsers that historically ended on the last token they consumed (DBCC's closing `)`, SET-session-state's `ON`/`OFF`) get a one-token advance via `IsStatementBoundary` after dispatch — Token already at `;`, end-of-batch, or a recognized statement-starting keyword is left alone.

`Simulation.IsStatementBoundary(Token?)` is the **single source of truth** for "does this token begin a new top-level statement (or a hard boundary — `null` / `;` / the contextual `THROW`)?" It answers `true` for the full statement-keyword set: SELECT / INSERT / UPDATE / DELETE / MERGE / BEGIN / COMMIT / ROLLBACK / SAVE / CREATE / DROP / ALTER / DBCC / SET / DECLARE / WITH / IF / ELSE / END / WHILE / BREAK / CONTINUE / RETURN / PRINT / RAISERROR / WAITFOR / TRUNCATE / USE / GRANT / REVOKE / DENY / OPEN / FETCH / CLOSE / DEALLOCATE / EXEC / EXECUTE. Four consumers route through it so a new statement keyword is added in exactly one place:

- the dispatch loop's post-statement cursor normalization + error-recovery scans;
- `Selection.Parse`'s two projection-list terminator switches (the `WITH` case is checked *before* the shared predicate so its more-specific Msg 319 wins; the switch matches only `ReservedKeyword`, so a following statement's keyword ends the projection while column-name-like contextual keywords are unaffected);
- `ParseExecArguments` — an EXEC argument list stops at any statement start (reserved statement keywords can't be bare argument values, so this never truncates a legitimate literal / `@var` / DEFAULT / OUTPUT / NULL / `@@`-niladic arg);
- `ConsumeToStatementBoundary` — the principal-DDL parse-and-discard tail (`FROM LOGIN` / `WITH PASSWORD` / `DEFAULT_SCHEMA`).

This is why semicolon-less statement sequences work for the full set, e.g. `select 1\nexec xp_msver`, `declare @x int\nuse master`, `if 1=1 select 1\nfetch c`. Before unification these predicates had drifted (EXEC/EXECUTE missing from several), so SSMS's semicolon-less AlwaysOn probe died with Msg 102.

# Reserved keywords as identifiers

The tokenizer classifies a bare word as a `ReservedKeyword` iff it matches a `Parser/Keyword.cs` enum member (`UnquotedString.CheckReserved`), and reserved words can't stand in as identifiers — `SELECT 1 AS from` / `c.user` raise **Msg 156**. So the enum is the sole gate on which words are usable as unquoted identifiers, and `Tests.Internal/ReservedKeywordsTests` pins it to Microsoft's [canonical reserved-keyword list](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/reserved-keywords-transact-sql) in both directions.

Two canonical entries are **deliberately omitted** from the enum because real SQL Server doesn't actually enforce them as reserved (`ReservedKeywordsTests.DocumentedOmissions`):

- **`WITHIN GROUP`** — a two-word entry whose component words aren't independently reserved (`WITHIN` is contextual; `GROUP` is covered).
- **`PRECISION`** — on the list only because it forms the `DOUBLE PRECISION` type name, but probe-confirmed (SQL Server 2025, 2026-07-15) fully usable as an identifier in **every** position: dotted member (`clmns.precision`), bare projection (`SELECT precision FROM …`), alias (`SELECT 1 AS precision`), table alias, and `ORDER BY` all succeed. SMO's SSMS column-node query reads `CAST(clmns.precision AS int)` off `sys.all_columns`, so reserving it blocked a real client. Server behavior is authoritative over the doc list, so `Precision` is not a `Keyword`.

# Double-quoted identifiers / `SET QUOTED_IDENTIFIER`

`"…"` is dual-natured, switched by the session `QUOTED_IDENTIFIER` option (**default ON**). The tokenizer's `NextToken(…, bool quotedIdentifiers)` reads the parser flag threaded from `ParserContext.QuotedIdentifiers` (seeded from `SimulatedDbConnection.QuotedIdentifiers`):

- **ON** — `"foo"` tokenizes as a `DelimitedIdentifier` (`Parser/Tokens/DelimitedIdentifier.cs`, the renamed `BracketDelimitedString`), identical to `[foo]`. `""` is the embedded-quote escape; `[`, `]`, `'` are ordinary characters inside. So `"a""b"` → identifier `a"b`, and `"a]b'c [d"` is that exact name. Reserved words (`"select"`) and spaces-only (`"   "`) are legal identifiers. An unresolvable quoted column raises Msg 207 (`Invalid column name '…'`).
- **OFF** — `"foo"` tokenizes as a varchar `Literal`, typed exactly like `'foo'` (same collation/coercibility). `""` is the escape, so `"a""b"` → string `a"b`, `"it's"` → `it's`. Empty `""` is a valid **empty string**, not an error. Concatenation and string-literal aliasing work (`"a" + 'b' + "c"` → `abc`; `SELECT 1 AS "X Y"` names the column via string-literal-alias). Brackets `[…]` stay identifiers regardless.

`N` is **not** a Unicode prefix for double quotes (unlike `N'…'`): `N"foo"` tokenizes as identifier `N` followed by `"foo"`, so in a select list it reads as column `N` aliased `foo` → Msg 207 for `N`.

Empty `""` (ON) and properly-closed empty `[]` raise **Msg 1038** (class 15, **state 4**, `EmptyColumnAlias()`) at the *tokenizer* level — so at every identifier position (`SELECT 1 AS ""`, `SELECT [] FROM t`, `CREATE TABLE ""(c int)`), not only select-list aliases. An unclosed `"` (either mode) raises **Msg 105** via `UnclosedStringLiteral(body)`, whose message echoes the scanned body: `Unclosed quotation mark after the character string '<body>'.` (the `'…'` / `N'…'` / `"…"` scanners share `ParseQuotedBody`, hence one Msg-105 shape).

`@@OPTIONS` (`Parser/Expressions/Value.cs` `FromAtAtOptions`) returns 5432 with **bit 256** tracking the parse-position QI setting — `@@OPTIONS & 256` is 256 under ON, 0 under OFF. The **plan cache** key (`PlanCacheKey`, `Simulation/Simulation.cs`) includes the `QuotedIdentifiers` bool, so the identical text `SELECT "abc"` caches separately under ON vs OFF and never replays the wrong reading.

## Scoping — parse-time, textual order

`SET QUOTED_IDENTIFIER ON|OFF` and `SET ANSI_DEFAULTS ON|OFF` (whose bundle includes QI) apply through `ApplyQuotedIdentifierOption` (`Simulation/Simulation.Set.cs`), including the comma multi-option form (`SET QUOTED_IDENTIFIER, ANSI_NULLS OFF`). The application is **parse-time and NOT gated on skip-mode**, matching SQL Server:

- **Textual order, not control flow** — a `SET` inside a never-taken branch still applies to everything textually after it: `IF 1 = 0 SET QUOTED_IDENTIFIER OFF; SELECT "deadlit"` returns the string `deadlit`. And at the top level the change also **persists to the session**, so a subsequent separate command reads `"…"` the same way.
- **Forward-only** — a statement *before* the `SET` parses under the prior setting: with the session ON, `SELECT "a1"; SET QUOTED_IDENTIFIER OFF; SELECT "a2"` raises Msg 207 for `a1` (parsed as identifier before the flip).
- **Top level → flips both** the in-flight tokenizer flag and the session setting (`SimulatedDbConnection.QuotedIdentifiers`), so it crosses command boundaries on the same connection.
- **Dynamic SQL** (`EXEC('…')` / `sp_executesql`, marked by `ProcFrame.IsDynamicSql`) flips **only its own batch** — the session is unaffected (`@@OPTIONS & 256` unchanged afterward).
- **Procedure / function / trigger bodies ignore the `SET` entirely** (SQL Server's "ignored in a stored procedure" rule) — neither the body's own reading nor the session changes.

## Divergences

- **Per-object creation-time QI capture is NOT modeled.** Real SQL Server stamps procedures / views / triggers / tables with the `QUOTED_IDENTIFIER` in effect at CREATE time (`sys.sql_modules.uses_quoted_identifier`, `OBJECTPROPERTY(id, 'IsQuotedIdentOn')`) and executes their bodies under that captured setting regardless of the caller's session. The simulator re-parses bodies under the **executing session's** current setting instead. Rare legacy pattern (creating an object under a non-default QI and relying on the stamp); most code runs everything under the default ON.
- **Multi-statement-TVF bodies treat a `SET QUOTED_IDENTIFIER` as top-level** rather than rejecting it (real SQL Server disallows `SET QUOTED_IDENTIFIER` inside a function body).

## Expression nesting limits (Msg 8631 / Msg 191)

Expression parsing recurses per binary operator and grouping level (`TwoSidedExpression`'s parsing ctor re-enters `Expression.Parse` for its right side), and a .NET stack overflow is uncatchable and process-fatal — unacceptable for an in-process library handed a pathological query. Two graceful limits ship instead, both probed against SQL Server 2025 (2026-07-15):

- **Msg 8631, Class 17** (`ServerStackLimitReached()`): `Internal error: Server stack limit has been reached. Please look for potentially deep nesting in your query, and try to simplify it.` Raised from a stack probe (`RuntimeHelpers.EnsureSufficientExecutionStack()`) at the top of `Expression.Parse` — the faithful mechanism, since real's Msg 8631 is likewise a genuine stack probe with a stack-dependent threshold (reference: a 3000-term `1 + 1 + …` chain succeeds, 6000 fails). Every recursive parse path (boolean chains, CASE, function arguments, subquery projections) passes through `Expression.Parse` at least once per nesting level, so the single guard site bounds them all, and it composes across nested proc/dynamic-SQL batches because it measures the real stack.
- **Msg 191, Class 15** (`StatementNestedTooDeeply()`): `Some part of your SQL statement is nested too deeply. Rewrite the query or break it up into smaller queries.` Structural counter on grouped-expression parens (`ParserContext.GroupingDepth`, limit 512 in `Expression.MaxGroupingDepth`).

**Divergences**: the simulator's tolerated operator-chain depth is well below real's because its parse frames are fat — measured on a default 1 MB thread, Msg 8631 fires near 750 levels (Release) / 600 (Debug), where the reference server handles 3000+ terms. The Msg-191 paren limit (512) is likewise below real's (1000 parens succeed there, 2000 fail) so the structural error deterministically beats the stack probe on 1 MB threads. Lifting the chain-depth ceiling toward real's requires the iterative/precedence-climbing parse restructure tracked in the backlog.
