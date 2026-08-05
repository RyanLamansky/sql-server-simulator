# Batch grammar: statement separators

Statements are separated by an optional `;`.
Real SQL Server's relaxed grammar lets most statement pairs sit adjacent (`declare @v int = 7 select @v`, `set @v = 1 set @w = 2`, `insert t values (1) select * from t`, `begin tran ... commit`); the simulator follows.
Two enforced exceptions match SQL Server's specific rules:
- A CTE (`WITH`) directly following another statement raises **Msg 319 St 1** (verbatim wording: `Incorrect syntax near the keyword 'with'. If this statement is a common table expression, an xmlnamespaces clause or a change tracking context clause, the previous statement must be terminated with a semicolon.`).
  A `WITH` at batch start (or right after a `;`) is fine.
  The check fires both at `Simulation.CreateResultSetsForCommand`'s top-level dispatch (`requireSemicolonBeforeCte` flag) and inside `Selection.Parse`'s projection-element switches — the latter is where `select 0 with cte ...` surfaces it before the SELECT can complete.
- A `MERGE` not terminated by `;` raises **Msg 10713 St 1** (`A MERGE statement must be terminated by a semi-colon (;).`) regardless of whether another statement follows or the batch ends.
  The check sits at the dispatch site immediately after `ParseMerge` returns, before any cursor normalization.

A third exception belongs to the module statements rather than to the separator grammar: `CREATE` / `ALTER` of a `VIEW`, `FUNCTION`, `TRIGGER`, `PROCEDURE` or `SCHEMA` must *open* its batch (**Msg 111**), and a `VIEW` or `FUNCTION` must additionally be its batch's *only* statement, since its body runs to the end of the batch (**Msg 156 / 102** at whatever follows).
Procedures and triggers instead swallow a trailing statement into the body.
See [Where a module statement may sit in its batch](programmable.md#where-a-module-statement-may-sit-in-its-batch).

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

# Implicit `EXECUTE` (bare procedure call)

A statement that is a bare object name — optionally followed by an argument list — is an implicit `EXECUTE`, matching real SQL Server: `sp_datatype_info_100 0, 3` runs identically to `EXEC sp_datatype_info_100 0, 3`.
This is the form mssql-jdbc's `getTypeInfo` sends (no `EXEC` keyword).
The restriction is strict — probe-confirmed against SQL Server 2025: the bare form is accepted **only as the literal first statement of a batch**.
Anywhere else it is Msg 102: after a prior statement (`SELECT 1; sp_who`), and even after a leading empty statement (`; sp_who`).
The dispatch loop carries an `atBatchStart` flag (`DispatchStatementsUntil` → `DispatchOneStatement` → `DispatchOneStatementCore`) that starts true for a top-level batch (`endKeyword is null` — never inside a `BEGIN…END` block) and clears on the first `;` or dispatched statement.
When it is still set and the leading token is a bare `Name` (not a reserved statement keyword — those match their own switch arms first), the statement routes through `ParseExec(batch, implicitExec: true)`, which skips the EXEC-keyword consume, the `EXECUTE AS` / `@rc =` capture, and the dynamic-SQL `(…)` branches and starts directly at the proc-name parse — so RPC and text execution stay identical.
Positional args (`a, b`), named args (`@p = v`), and no-arg (`sp_who`) all work; an unknown bare name raises the normal proc-not-found (Msg 2812), not Msg 102.

# Unquoted identifier body characters

`Tokenizer.IsIdentifierBodyChar` governs what may follow an unquoted identifier's first character: letters and digits, `_`, **`$`, `#`, `@`**, and Unicode non-spacing marks (so a decomposed spelling — `zzcafe` + U+0301 — tokenizes and resolves to a table created as composed `zzcafé`).

`$` / `#` / `@` are body-only.
A *leading* `@` or `#` dispatches separately, as a variable or a temp-table name, and a leading `$` (outside `$action`) is a currency literal — so these three characters only extend an identifier mid-token.
ORMs emit exactly this shape: Django's annotations tests generate crafted aliases like `crafted_alia$`, which is what motivated the rule.

# Reserved keywords as identifiers

The tokenizer classifies a bare word as a `ReservedKeyword` iff it matches a `Parser/Keyword.cs` enum member (`UnquotedString.CheckReserved`), and reserved words can't stand in as identifiers — `SELECT 1 AS from` / `c.user` raise **Msg 156**.
So the enum is the sole gate on which words are usable as unquoted identifiers, and `Tests.Internal/ReservedKeywordsTests` pins it to Microsoft's [canonical reserved-keyword list](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/reserved-keywords-transact-sql) in both directions.

Two canonical entries are **deliberately omitted** from the enum because real SQL Server doesn't actually enforce them as reserved (`ReservedKeywordsTests.DocumentedOmissions`):

- **`WITHIN GROUP`** — a two-word entry whose component words aren't independently reserved (`WITHIN` is contextual; `GROUP` is covered).
- **`PRECISION`** — on the list only because it forms the `DOUBLE PRECISION` type name, but probe-confirmed (SQL Server 2025) fully usable as an identifier in **every** position: dotted member (`clmns.precision`), bare projection (`SELECT precision FROM …`), alias (`SELECT 1 AS precision`), table alias, and `ORDER BY` all succeed.
  SMO's SSMS column-node query reads `CAST(clmns.precision AS int)` off `sys.all_columns`, so reserving it blocked a real client.
  Server behavior is authoritative over the doc list, so `Precision` is not a `Keyword`.

The Msg 156 rejection reaches two sites the generic Msg 102 path used to cover, both probe-confirmed against real: a select-list alias (`Selection.ReadAliasName` — `SELECT 1 AS user`) and a dotted name segment (`Expression`'s postfix `.` arm — `dbo.user('a')`, `t.user`).

## Compatibility-gated reservation: `REGEXP_LIKE`

One word is reserved only from a given compatibility level.
`REGEXP_LIKE` is reserved at **170** — where SQL Server 2025's native predicate ships — and usable as an identifier at 160 and below.
Probe-confirmed: at 170, `SELECT 1 AS REGEXP_LIKE`, `CREATE TABLE REGEXP_LIKE (a int)` and the unbracketed `dbo.REGEXP_LIKE(...)` spelling all raise **Msg 156**; at 160 all three succeed (the last resolving as an ordinary two-part function name, so a miss is Msg 4121).

That last case is the one applications hit: mssql-django installs its regex support as a **CLR scalar function** named `dbo.REGEXP_LIKE`, and the generated SQL calls it unbracketed — which stops parsing the moment the database moves to 170.
Bracketing or double-quoting the name (`dbo.[REGEXP_LIKE]`) is the escape hatch, and it works at every level.
The reservation covers only `REGEXP_LIKE`; the other six `REGEXP_*` names are ordinary identifiers.

Mechanically, the tokenizer is the gate.
`Tokenizer.NextToken` takes the active database's `CompatibilityLevel` alongside the collation and `QUOTED_IDENTIFIER` flag it already threaded, and `UnquotedString.CheckReserved` returns an `UnquotedString` rather than a `ReservedKeyword` for `Keyword.Regexp_Like` below 170.
`ReservedKeywordsTests.DocumentedAdditions` records the enum entry as a deliberate departure from the canonical list, which hasn't caught up.
No plan-cache key change is needed: `ALTER DATABASE … SET COMPATIBILITY_LEVEL` runs through the Alter dispatch arm, which bumps `SchemaVersion` and so re-parses every cached plan.

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

The **128-character identifier limit** (**Msg 103**, class 15 state 4) measures the identifier's own characters, not its source text: the delimiters don't count, so `[` + 128 characters + `]` is legal, and an escaped `]]` / `""` counts once (both probe-confirmed).
The message quotes the first 128 characters of that undelimited body.
Django's schema editor emits exactly-128-character table names for a long model's implicit m2m table, which is what surfaced the delimiter-counting version of the rule.

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
  A body's reading comes from the module's creation-time capture instead — see below.

## Per-object creation-time capture

Every programmable module stamps the `QUOTED_IDENTIFIER` in effect at its `CREATE` onto `SchemaObject.UsesQuotedIdentifier`, and **its body parses under that capture rather than the invoking session's setting**.
So a procedure created under OFF keeps reading `"…"` as a string literal no matter who `EXEC`s it, and one created under ON keeps reading identifiers even from an OFF session.
`ALTER` and the ALTER leg of `CREATE OR ALTER` re-stamp; the unified per-kind construction sites (`Simulation.Create{Procedure,View,Trigger,Function}.cs`) carry one assignment each, so both legs are covered at once.
Probe-confirmed across all six kinds — procedure, view, DML trigger, scalar UDF, inline TVF, multi-statement TVF (DDL triggers ride the same path).

Mechanically, invocation **swaps `SimulatedDbConnection.QuotedIdentifiers` to the capture for the body's duration** and restores it in the existing `finally` (the precedent is the `savedTextSize` restore beside it in `Simulation.InvokeProcedure.cs`).
Seeding the child `ParserContext` directly would fix only the tokenizer; the session swap is what carries the setting to everything else that reads the connection:

- **`@@OPTIONS & 256` and `SESSIONPROPERTY('QUOTED_IDENTIFIER')` report the capture inside the body** — 0 in an OFF-created module even when the caller is ON (probe-confirmed).
- **Dynamic SQL inherits it** — `EXEC('SELECT "x"')` inside an OFF-created procedure reads a literal.
- **The plan-cache key stays honest** — `PlanCacheKey` includes the bool, so two identically-worded bodies captured under different settings never share a plan.
- **The Msg 1934 gates below read it**, which is how an OFF-created procedure trips them from an ON session.

Each module in a call chain runs under its own capture; nesting is naturally handled by the save/restore pair, and the session is unchanged once the outermost body returns.
The swap is held across `yield return` at the two lazily-enumerated sites (`InvokeViewCore`, `InvokeInlineTvfCore`) — the module is still producing rows there, and every nested re-parse swaps to its own capture, so the only window is a `SESSIONPROPERTY('QUOTED_IDENTIFIER')` evaluated by the *outer* statement between two rows of a view body.

**Tables don't capture `QUOTED_IDENTIFIER`.**
`OBJECTPROPERTY(<table>, 'IsQuotedIdentOn')` answers 1 for any table regardless of the creating session (probe-confirmed), which the `UsesQuotedIdentifier = true` default reproduces — a table's computed-column and constraint expressions are parsed once at CREATE and stored normalized (`([a]+'x')` for a `"x"` written under OFF), never re-read.

**Nor do constraints, in the other direction.**
A CHECK or DEFAULT constraint answers a constant **0** — probe-confirmed both ways, including for one created with `QUOTED_IDENTIFIER` ON, and uniformly 0 across msdb's 229 shipped constraints — while a PRIMARY KEY / UNIQUE / FOREIGN KEY constraint answers NULL, and `IsAnsiNullsOn` is NULL for all five.
Constraint object ids resolve through `ObjectProperty.TryFindConstraint` (they aren't `SchemaObject`s, so the object walk can't reach them) and answer through `EvaluateConstraintProperty`: every object-kind discriminator plus `IsEncrypted` / `IsMSShipped` / `IsSystemTable` is 0, and the module- and table-scoped names are NULL.
`OBJECTPROPERTYEX` gives the same answers.
`OBJECT_ID('<constraint name>')` doesn't resolve a constraint, so the id comes from `sys.objects` (or `sys.default_constraints` for a DEFAULT, which `sys.objects` has no row for).

**`ANSI_NULLS` captures alongside it, tables included, but only as metadata.**
`SchemaObject.UsesAnsiNulls` records the session's `SET ANSI_NULLS` at every `CREATE` the same way — modules and tables both, since real answers 0 for a table created under OFF.
Nothing behavioral rides on it: real freezes a module's `= NULL` comparison semantics to the capture, while the simulator doesn't model `SET ANSI_NULLS OFF` comparison semantics at all, so every comparison stays ANSI whatever the capture says.
Catalog projection for both captures is in [`catalog-views.md`](catalog-views.md#creation-time-set-option-capture).

## `SET`-option gates — Msg 1934 / Msg 1935

Real refuses to touch a stored expression from a session whose SET options would read that expression differently.
Message shape (class 16, state 1), the verb echoing the statement:

```
<VERB> failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'.
Verify that SET options are correct for use with indexed views and/or indexes on computed columns
and/or filtered indexes and/or query notifications and/or XML data type methods and/or spatial
index operations.
```

### The option list

Six options are checked, and every offending one is named comma-separated in the single message: `QUOTED_IDENTIFIER` / `ANSI_NULLS` / `CONCAT_NULL_YIELDS_NULL` / `ANSI_WARNINGS` / `ANSI_PADDING` ON and `NUMERIC_ROUNDABORT` OFF.
The order is fixed and is neither the order the session set them nor alphabetical — probe-confirmed with three and with five wrong at once, the five-wrong case reading `'ANSI_NULLS, CONCAT_NULL_YIELDS_NULL, ANSI_WARNINGS, ANSI_PADDING, NUMERIC_ROUNDABORT'`.
`QUOTED_IDENTIFIER` is reported **alone** when it is off, whatever the other five say (probe-confirmed with all six wrong at once), which is why it reads as the only component in the common case.

`ARITHABORT` is documented as part of the required set but never appears: real accepts a session whose `ARITHABORT` bit is 0 as long as `ANSI_WARNINGS` is on, probe-confirmed by reading `@@OPTIONS & 64` inside the batch real accepted.
The ANSI_WARNINGS-implies-ARITHABORT rule stands in for it, so the gate has six components rather than seven.

`Simulation.IncorrectSetOptionNames` is the single source of the list; every site below calls it, and the `QUOTED_IDENTIFIER` component alone reads the **parse-position** setting so a module body answers from its own creation-time capture rather than the caller's session.

### The probed matrix

Each row raises for any of the six, shown here under `QUOTED_IDENTIFIER OFF`:

| Operation | Msg 1934? | Verb |
| --- | --- | --- |
| `INSERT` / `UPDATE` / `DELETE` / `MERGE` on a table with a **PERSISTED** computed column | yes | the DML verb |
| …with an **enabled** index keying or including a computed column (persisted or not) | yes | the DML verb |
| …with an **enabled filtered** index | yes | the DML verb |
| …with an **XML** or **spatial** index | yes | the DML verb |
| …that an **indexed view** is built on | yes | the DML verb |
| …with a non-persisted computed column and **no index over it** | no | — |
| …with a **disabled** filtered index | no | — |
| `SELECT` from any of the above | no | — |
| `SELECT … FROM <indexed view> WITH (NOEXPAND)` | yes | the enclosing statement's verb |
| …the same view without the hint, or `NOEXPAND` on an unindexed view | no | — |
| An XML data-type method — `.value()` / `.exist()` / `.query()` / `.modify()` — even on an `xml` **variable** | yes | enclosing statement |
| `.nodes()` alone | no | — |
| `CREATE TABLE` / `ALTER TABLE ADD` declaring a **PERSISTED** computed column | yes | `CREATE TABLE` / `ALTER TABLE` |
| …declaring a non-persisted one | no | — |
| `CREATE INDEX` that is filtered, over a computed column, or on a view | yes | `CREATE INDEX` |
| `CREATE INDEX` over plain columns | no | — |
| `CREATE SPATIAL INDEX` | yes | `CREATE INDEX`, **spatial-only verify clause** |
| `CREATE PRIMARY XML INDEX` / `CREATE XML INDEX` | yes | that exact statement name |
| `TRUNCATE TABLE`, `ALTER TABLE ADD <plain column>`, `UPDATE STATISTICS`, `SELECT … INTO` | no | — |

Two wording notes.
`CREATE SPATIAL INDEX` alone narrows the verify clause to `Verify that SET options are correct for use with spatial index operations.` while keeping the bare `CREATE INDEX` verb.
The XML-method verb follows the enclosing statement — `INSERT @t SELECT @x.value(…)` reports `INSERT` — while a bare `SET @i = @x.value(…)` reports `SELECT`; `StatementContext.StatementVerb`, stamped from the leading token beside `LeadingKeywordReturnsRows`, supplies it.

**Msg 1935** is the object-side companion: indexing a view whose *own* capture is OFF fails even from a session with QI ON, because no session setting repairs it — the view has to be recreated.
Real checks the view first, so an OFF-created view raises 1935 and only an ON-created one falls through to the session's 1934.

```
Cannot create index. Object 'v' was created with the following SET options off: 'QUOTED_IDENTIFIER'.
```

Timing: the gate is a **batch-level compile check, not a runtime one** — a never-taken `IF 1 = 0 INSERT …` still raises — but **create-time module-body binding is exempt**: real accepts `CREATE PROCEDURE … AS INSERT …` under OFF and raises only when the body runs, so the write gate and the XML-method gate both skip when `BatchContext.CreateTimeBinding` is set.

### Not modeled yet

- **Msg 1935's own option list** — the object-side companion reads only the view's `QUOTED_IDENTIFIER` capture, which is the only SET option a view records beyond `ANSI_NULLS`.
- **The LOGIN7 option flags** — `Network/Login7Request.cs` reads name / credential / database fields and ignores `OptionFlags2`, whose `fODBC` bit is what a client uses to request the `ANSI_DEFAULTS` bundle (`QUOTED_IDENTIFIER` included) at connect time.
  The session default is ON either way, which is what SqlClient asks for, so the omission shows only for a client that deliberately connects with QI OFF.

## Trailing-token tightening

The per-statement dispatch normalizer advances one token past a parser that stopped on its last-consumed token (many statement parsers rely on this), so a lone *unexpected* trailing token after a statement was silently swallowed — `SELECT id FROM t LIMIT 2` parsed `LIMIT` as the source's alias and the normalizer dropped the dangling `2`.
A general "any unconsumed trailing token → Msg 102" rule proved too invasive (dozens of statement parsers legitimately end on a last-consumed token; the parenthesized-join FROM form leaves its alias dangling).
The narrow fix: a completed top-level SELECT that left the cursor on a **value literal** (`Numeric` / `Literal`) — which a well-formed SELECT never does — raises Msg 102, matching real for `SELECT … LIMIT n` and `SELECT … OFFSET n` (both Msg 102 without an ORDER BY on real).
An identifier or other token still routes through the normalizer; the alias-swallow case it used to leave open (`SELECT 1 xyz 2` parsing as two columns) is now caught inside the projection loop instead — see [Select-list element positions](#select-list-element-positions).

**A binding error the statement owes waits for that check.**
Real parses a batch before binding any of it, so a syntax error past a clause outranks the clause's own binding error: `GROUP BY 'a' 'b'` is Msg 102 at `'b'` where `GROUP BY 'a'` alone is Msg 164, and the same holds for the Msg 144 shape `GROUP BY (SELECT …) 'b'` (all three probe-confirmed).
`ParserContext.PendingGroupByBindError` holds the clause's message until the statement's outermost query expression has parsed, and the flush yields to a trailing value literal — the same token class this rule rejects — so the syntax error wins.
The first offending item still supplies the message, which is what an immediate throw produced.

`ALTER TABLE … ADD COLUMN c TYPE` is rejected with **Msg 156** near COLUMN (unlike `DROP COLUMN` / `ALTER COLUMN`, the ADD form names the column directly) — a prior "COLUMN is optional here" note was based on a mistaken probe; the live reference rejects it.

## Select-list element positions

The projection loop tracks one bit of state — `elementExpected`, true at the start and after a comma, false once an element and any alias it took are complete.
Both of the grammar's position rules fall out of it, and both were over-permissive before it existed.

**A keyword where an element belongs → Msg 156**, naming that keyword.
The loop treats a statement keyword as a boundary so back-to-back statements need no separating semicolon (`SELECT 1 UPDATE t SET …` is two statements), which is right once an element exists and wrong while one is still owed.
Without the check, `SELECT FROM t` parsed to a zero-column SELECT — an `ArgumentException` when a row materialized, and no error at all over an empty table — and `SELECT 1, FROM t` silently returned a single column.
Probe-confirmed for `UPDATE` / `DELETE` / `INSERT` / `FROM` / `WHERE` / `ORDER`, in first and later positions alike; real echoes the keyword in the **source's own casing**, so `select update(c1)` reports `'update'`.
`Selection.CanBeginProjectionElement` carries the exceptions — the function-call heads (`LEFT` / `RIGHT` / `CONVERT` / `TRY_CONVERT` / `COALESCE` / `NULLIF`), `CASE`, `NULL`, the parens-less niladic constants, and the `DISTINCT` / `ALL` / `TOP` prefixes.

**A value where only a separator belongs → Msg 102**, at that token.
Each element takes at most one postfix alias, so the token after it must be a comma, a clause keyword, or the end: `SELECT 1 xyz 2` is an error at the `2`, not a second column.
The loop re-enters the element switch after an alias arm, which is exactly where `elementExpected` is false, so the check costs nothing extra.
A string literal is a legal alias, and the error names the *next* token (`SELECT 'p' y 'q'` → `near 'q'`) under the rendering rule below.

Two end-of-input cases stay on Msg 102 and are distinguished by what preceded them: a bare `SELECT` reports near `'select'`, while a comma that promised an element the input never supplied reports near `','`.

## How Msg 102 names the offending token

The `near '…'` slot does not always echo the token as written, so `Token.ErrorText` — not `Token.ToString()` — feeds the Msg 102 factory.
`ToString()` stays source-exact because the parser matches token text against it (table-hint and query-hint name lookup).
Probe-confirmed against SQL Server 2025 (2026-07-31), each spelling placed in the same trailing-token position:

| Spelling | Reported as | Rule |
| --- | --- | --- |
| `'b'` / `N'b'` / `"b"` | `b` | delimiters and the `N` prefix drop |
| `'it''s'` | `it's` | the doubling that escaped a delimiter collapses |
| `''` | *(empty)* | |
| `[y]` / `[a]]b]` | `y` / `a]b` | delimited identifiers unwrap the same way |
| `0xABC` | `0x0abc` | re-rendered from the parsed bytes: lowercase, odd digit count regains its leading zero |
| `$00005` | `$00005` | a currency literal keeps its spelling, not the `money` value |
| `12345` / `@v` | `12345` / `@v` | everything else is source text |

A character body is named **as written, not as the collation stores it**: under a CP1252 database `'日本'` stores as `??` yet real still reports `near '日本'`, so the rendering reads the source rather than the parsed `SqlValue`.
The binary spelling is the one exception that reads the value, which is why an odd-digit literal comes back padded.

The slot also clips, at limits consistent with one shared 258-byte buffer: a character body at **129 UTF-16 code units** (counted after escapes collapse, and splitting a surrogate pair rather than rounding down to a whole character), a binary value at **258 bytes**, and any source-spelled token at **128 characters**.
Real reaches that last clip through a 200-digit numeric literal — which the simulator's `decimal` backing can't represent — and precedes it with a Msg 103 for the over-long token that the simulator doesn't raise for non-identifiers.

## What a syntax error names at end of batch

Real names the **last token it consumed**, never an empty slot — probed against SQL Server 2025 (2026-08-05) over the whole family, Msg 102 and Msg 4145 alike:

| Batch | Reported |
| --- | --- |
| `SELECT abs(-1` | Msg 102 `near '1'` |
| `SELECT (1` | Msg 102 `near '1'` |
| `SELECT 1 WHERE 1 IN (1` | Msg 102 `near '1'` |
| `SELECT * FROM (SELECT 1 AS a` | Msg 102 `near 'a'` |
| `SELECT 1 FROM` / `SELECT 1 ORDER BY` | Msg 102 `near 'FROM'` / `near 'BY'` |
| `IF 'abc'` | Msg 4145 `near 'abc'` |

`ParserContext.LastToken` carries it: `MoveNext` stashes the token it is leaving behind whenever the input runs out, and the Msg 102 / Msg 4145 factories fall back to it once `Token` is null.

An **argument list or parenthesized expression the batch never closed** is refused rather than treated as closed.
The check sits at the postfix loop's call arm and in the grouped-expression parser: both promise to leave the cursor on the construct's `)`, and anything else there would otherwise be swallowed by the loop's next advance — the swallow that admits `SELECT abs(-1` and `SELECT abs(-1 x` without it.
A window function is the one exception the check names: the bare `OVER w` named-window reference ends on the window's name.

**Msg 4145's own near-token is the token following the whole non-boolean expression**, parentheses included — `IF ((1)) PRINT 'x'` names `'PRINT'`, while `SELECT 1 WHERE (1)` names `')'` because nothing follows it.
The simulator's predicate grammar consumes a boolean group's parens on the way in, so the factory steps back over one closer per still-open group (against a checkpoint, leaving the failing parse's cursor where it was) before reading the name.

## Divergences

- **Multi-statement-TVF bodies treat a `SET QUOTED_IDENTIFIER` as top-level** rather than rejecting it (real SQL Server disallows `SET QUOTED_IDENTIFIER` inside a function body).

## Expression depth limits (Msg 8631 / Msg 191 / Msg 125)

A .NET stack overflow is uncatchable and process-fatal — unacceptable for an in-process library handed a pathological query.
So no recursive spine that user SQL can drive arbitrarily deep is allowed to overflow: flat operator chains parse **and** evaluate iteratively (no per-term recursion), genuinely-nested shapes are bounded by SQL Server's own graceful structural errors, and a runtime stack probe backstops whatever remains.
Thresholds probed against SQL Server 2025.

### Flat left-associative chains — no cap (iterative)

`Expression.Parse` parses binary-operator chains (`+ - * / % & | ^`) by **iterative precedence-climbing** (`ParseBinaryContinuation`) rather than constructor recursion, so `1 + 1 + … + 1` of any length builds a left-leaning tree with parse recursion bounded by the number of precedence levels (2), not the term count.
Evaluation is iterative too: `TwoSidedExpression.Run` / `GetSqlType` walk the left spine in a loop (recursing only into right operands, whose depth is paren-bounded), and boolean `AND` / `OR` chains collapse to **n-ary** `AndExpression` / `OrExpression` nodes evaluated in a loop.
A run of `NOT` prefixes collapses at parse (three-valued `NOT` is an involution).
So flat `+` / concat / `AND` / `OR` chains of tens of thousands of terms parse and run without recursion.

**Divergence (acceptable direction):** real SQL Server caps flat chains at a genuine stack limit — a 3030-term `1 + 1 + …` or `'a' + 'a' + …` chain succeeds, 3031 raises **Msg 8631**; an `AND` chain succeeds to 8871, 8872 raises Msg 8631.
The simulator has **no** artificial cap on flat chains — being more permissive than real on Msg 8631 (a physical limit, not a semantic one) is fine, so none is added.
`IN`-list length is likewise uncapped on both.

### Nested shapes — shared weighted budget, Msg 191

Parens, scalar subqueries, and function-argument lists share **one** nesting budget (`ParserContext.NestingDepth`, cap `Expression.MaxNestingDepth` = 500), each construct charging its cost on entry and refunding it in a `finally`; crossing the cap raises **Msg 191, Class 15** (`StatementNestedTooDeeply()`, `Some part of your SQL statement is nested too deeply. …`).
Probe-confirmed that real pools these into a single budget (1000 nested parens + 14 nested funcs fails; 150 subqueries + 116 parens fails) where a subquery level costs ≈ 6 paren levels — the simulator mirrors the ratio: `ParenNestingCost` = `FunctionCallNestingCost` = 1, `SubqueryNestingCost` = 6.

**Divergence:** real's absolute limits are higher and stack-dependent (1015 nested parens / 1013 nested funcs / 168 nested subqueries all succeed; +1 each raises Msg 191).
The simulator's parse frames are fatter — a 1 MB Debug thread parses only ~990 nested parens before the stack probe would claim the shape as Msg 8631 — so the cap is set to 500 (paren/func limit 500, subquery limit ⌊500/6⌋ = 83), giving ~2× headroom on the tightest test config (1 MB Debug) while preserving the probed subquery ≈ 6× paren ratio.
Deep **function** nesting alone has fatter frames still (~40 levels on a 512 KB thread, ~80–100 on 1 MB): on an adequate stack it reaches the 500-unit budget and raises Msg 191, but on a tight thread the runtime probe pre-empts with Msg 8631 (real always raises Msg 191 here).
Both outcomes are graceful.

### Nested CASE / IIF — Msg 125, cap 10

`CASE` / `IIF` lexical nesting is capped at ten levels (`ParserContext.CaseDepth`, `MaxCaseNestingDepth`), an eleventh raising **Msg 125, Class 15** (`Case expressions may only be nested to level 10.`) — an **exact** match to real.
The **state** identifies the construct entered at the eleventh level: **State 4** for a searched/simple `CASE`, **State 2** for `IIF` (which desugars to a searched CASE).
Probe-confirmed: nesting in a `WHEN` condition counts identically to a `THEN` / `ELSE` result, the count is **not** reset by a scalar-subquery boundary, and a mixed CASE/IIF stack shares one counter (the innermost-entered construct sets the state).

### Msg 8631 backstop

**Msg 8631, Class 17** (`ServerStackLimitReached()`, `Internal error: Server stack limit has been reached. …`) is raised from a runtime stack probe (`RuntimeHelpers.EnsureSufficientExecutionStack()`) at the top of `Expression.Parse`, `Expression.ParseSignedOperand` and `BooleanExpression.ParseNot` — the faithful mechanism, since real's Msg 8631 is likewise a genuine stack probe whose threshold scales with the thread stack.
The sign-operand site is its own probe because unary `+` / `-` prefixes are *genuinely* nested: each sign parses the following multiplicative chain as its operand (see [`arithmetic.md`](arithmetic.md#the-unary-signs-bind-at-the-additive-level)), so both a stack of signs (`- - - … 1`) and a chain of signed factors (`-1 * -1 * …`) recurse once per sign without passing through `Expression.Parse`.
Real bounds both with **Msg 191** — probe-confirmed past ~350 signed factors and ~1000 stacked signs, where an *unsigned* `1 * 1 * …` chain of 3000 is flat and fine — while the simulator tolerates far more and then raises Msg 8631, the same more-permissive-then-graceful direction taken for flat chains.
It composes across nested proc / dynamic-SQL batches (it measures the real stack) and catches any deep recursive shape whose frames outrun a deterministic cap (deep function nesting on a tight thread; deeply parenthesized boolean predicates).
