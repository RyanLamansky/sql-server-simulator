# Query hints — table & OPTION clauses

SQL Server's hint grammar, with the refusals real raises over it.
The locking hints drive the lock manager ([`locking.md`](locking.md#hint-surface)); the access-path hints (`INDEX`, `FORCESEEK`, `FORCESCAN`) choose no access path here but are checked for the plans real refuses — see [Enforced rejections](#enforced-rejections); the rest parse and are discarded.

Implementation lives in [`src/SqlServerSimulator/Parser/Selection.Hints.cs`](../../src/SqlServerSimulator/Parser/Selection.Hints.cs) (table hints) and [`Selection.StatementHints.cs`](../../src/SqlServerSimulator/Parser/Selection.StatementHints.cs) (the OPTION clause and the checks a statement's hints make once it has parsed).

## Inline join-algorithm hints

`MERGE` / `HASH` / `LOOP` / `REMOTE` between the join type and `JOIN` — `INNER MERGE JOIN`, `LEFT OUTER HASH JOIN`, `FULL LOOP JOIN`.
The hint names the physical operator real should use; the simulator picks its own strategy, so it can never change an answer (probe-confirmed — hinted and unhinted forms return identical rows).
The parser records it on the join (`JoinSpec.Algorithm` / `Remote`) for the checks below.
A hint does fix the join order, and real says so with the informational Msg 8625 ("Warning: The join order has been enforced because a local join hint is used.") as the statement compiles — once per statement however many joins carry one, and not at all under `OPTION (FORCE ORDER)`.
That is when a batch compiles, ahead of everything it runs — each hinted statement's on its own line, an untaken branch's and a `SET NOEXEC ON` batch's included — and only then: a loop's statement warns once, a repeated batch text or dynamic SQL text runs on its cached plan and sends none, and a procedure warns at the compile its first call makes, never at `CREATE`, as a view never does (probed 2026-10-06 against SQL Server 2025).
What compiles as it runs warns as it runs: a statement the batch's compile deferred, and every run of one carrying `OPTION (RECOMPILE)` or reading a table variable.
A view's hint warns on the line of the statement referencing it.
`Selection.SendJoinOrderEnforced` holds the rules, the batch's compile recording what it sent in `BatchContext.JoinOrderWarnedStatements`.

Real accepts all four hints against every join type, including combinations that look implausible (`FULL LOOP JOIN`, `RIGHT LOOP JOIN`).
It does require the type keyword, and refuses these shapes (all probe-confirmed):

- `CROSS <hint> JOIN` → **Msg 156** naming the hint as a keyword.
- Two hints (`INNER MERGE HASH JOIN`) → **Msg 102** on the second.
- A word that isn't a hint (`INNER NONSENSE JOIN`) → **Msg 155** `'nonsense' is not a recognized join option.` — this position's own error, not the generic syntax one.
- `REMOTE` on an outer join → **Msg 1072**.
- An inline hint the statement's `OPTION` join hints don't include → **Msg 1042** `Conflicting JOIN optimizer hints specified.` (class 16); an inline `REMOTE` beside any `OPTION` join hint → **Msg 1071**.
- A join its allowed algorithms can't build → **Msg 8622**, settled with the other optimizer refusals (`ValidateJoinAlgorithms`).
  The allowed set is the inline hint's, else the `OPTION` clause's; a set holding `LOOP` builds anything.
  `HASH` needs an equality between an expression over the left side alone and one over the right side alone (`h.k = t.id + 1` and `isnull(h.k, 0) = t.id` qualify, `h.k = 1` and an `OR` don't), found among the join's `ON` conjuncts or the query's `WHERE` — the latter is what lets a comma join or an outer join a `WHERE` equality null-rejects pass; `MERGE` needs the same except on a `FULL` join; neither seeks a `FORCESEEK` source on the inner side.


## Table hints

Position varies by site (probe-confirmed against SQL Server 2025):

| Site                  | Placement              | Legacy `(hint)` form |
|-----------------------|------------------------|----------------------|
| FROM source           | alias-then-hint        | accepted             |
| JOIN-RHS              | alias-then-hint        | accepted             |
| UPDATE target         | target-then-hint       | rejected (Msg 102)   |
| DELETE target         | target-then-hint       | rejected (Msg 102)   |
| INSERT target         | target-then-hint, before column list | always parses as column list (Msg 207 on the would-be hint name) |
| MERGE target          | hint-then-alias        | rejected (Msg 102)   |
| MERGE source (bare-table) | alias-then-hint    | accepted (commits)   |
| MERGE source (parenthesized) | not accepted    | n/a                  |

Examples:

```sql
select * from t with (nolock, holdlock)
select * from t (nolock)            -- legacy, FROM-source only
select a.id from t a inner join t b with (nolock) on a.id = b.id
update t with (rowlock) set v = v + 1
delete from t with (tablock) where id = 5
insert into t with (tablock) (a, b) values (1, 2)
merge into t with (tablock) as x using s on s.id = x.id …
```

The legacy bare-paren `(hint)` form is **FROM / JOIN-RHS only** — INSERT treats `(` as the column-list opener (probe-confirmed Msg 207 on the would-be hint name), and UPDATE / DELETE / MERGE all raise Msg 102 on the bare-paren form.
The parser signals this with the `allowLegacyParenForm` parameter on `ParseOptionalTableHints` (default `true`; INSERT / UPDATE / DELETE / MERGE pass `false`).

**MERGE is the odd one out for hint-vs-alias placement** — hint comes between the target name and the optional `[AS] alias`, not after.
Real SQL Server rejects alias-then-hint on MERGE target with Msg 156.

**Table-variable targets reject hints entirely** for INSERT / MERGE (probe-confirmed Msg 156).
The parser short-circuits via `BatchContext.IsTableVariableName` before calling `ParseOptionalTableHints`; `WITH` after `@t` falls through to the default Msg 102 at the dispatch site.

**MERGE source supports two shapes**: the parenthesized form `USING (VALUES … / SELECT …) AS alias` and the bare-table form `USING tbl [AS alias]`.
Hints are accepted only on the bare-table form (alias-then-hint placement), matching real SQL Server: probe-confirmed that `USING (SELECT …) AS s WITH (NOLOCK)` raises Msg 156 (the parser treats WITH after the parenthesized source as a CTE prefix, not a hint clause).
The bare-table form is alias-then-hint with the same commit-on-paren semantic real SQL Server applies — a trailing `(x, y)` with unknown names raises Msg 321 rather than falling through to Msg 102.

Hint-argument shapes recognized:

| Shape           | Example                                       |
|-----------------|-----------------------------------------------|
| Bare name       | `NOLOCK`                                      |
| `name = value`  | `SPATIAL_WINDOW_MAX_CELLS = 1024`             |
| `name(args)`    | `INDEX(IX_foo)`, `FORCESEEK(IX_foo(c1, c2))`  |

Closed accept-list (case-insensitive, in `TableHintNames`), which doubles as the modifier table — each name maps to the `TableHintInfo` field it sets, or to `Discard` where the simulator models no effect, so membership and dispatch stay one table and one lookup.
A hint name arrives as a slice of the command text and is looked up through `TableHintLookup`, the set's `AlternateLookup<ReadOnlySpan<char>>`, so no string is materialized per hint:

`NOLOCK`, `READPAST`, `READUNCOMMITTED`, `READCOMMITTED`, `READCOMMITTEDLOCK`, `REPEATABLEREAD`, `SERIALIZABLE`, `SNAPSHOT`, `HOLDLOCK`, `UPDLOCK`, `XLOCK`, `TABLOCK`, `TABLOCKX`, `ROWLOCK`, `PAGLOCK`, `NOWAIT`, `KEEPIDENTITY`, `KEEPDEFAULTS`, `NOEXPAND`, `IGNORE_CONSTRAINTS`, `IGNORE_TRIGGERS`, `FORCESEEK`, `FORCESCAN`, `INDEX`, `SPATIAL_WINDOW_MAX_CELLS`, `REMOTE`.

Unknown hint name → **Msg 321** verbatim: `"<name>" is not a recognized table hints option.` (probe-confirmed against SQL Server 2025) — `READONLY`, a table-valued parameter's keyword, among them.
Real takes a hint written straight after another without the comma (`WITH (INDEX(ix) NOLOCK)`), and a CTE reference takes a `WITH (…)` list and discards it save a `FORCESEEK`, which reaches the body's tables (probed 2026-10-05 and 2026-10-07).
An `INDEX` hint on a catalog view is ignored with the view warning **Msg 4430**, as on a user view read without `NOEXPAND`.

`NOWAIT` zeroes the lock timeout for the table it names, so a conflicting acquisition raises **Msg 1222** rather than waiting — real documents it as "equivalent to specifying `SET LOCK_TIMEOUT 0` for a specific table", and the scoping is per table, not per statement.
See [`locking.md`](locking.md#hint-surface).

`NOEXPAND` (`FROM <indexed_view> WITH (NOEXPAND)` — forces the optimizer to use the view's own index instead of expanding it) has no execution effect: the simulator always expands an indexed view, so results are identical.
It is the one otherwise-discarded hint the parser tracks (`TableHintInfo.NoExpand`), because reading an indexed view through it is one of the operations real's SET-option gate covers — **Msg 1934** under the enclosing statement's verb, where a plain reference to the same view is never gated; see [`grammar.md`](grammar.md#set-option-gates--msg-1934--msg-1935).
See [`indexes.md`](indexes.md) for indexed views.

### Legacy `(hint)` form disambiguation

The legacy `FROM t (nolock)` form omits `WITH` and is FROM / JOIN-RHS only.
After parsing a base-table name + optional alias, `ParseOptionalTableHints` peeks one token past `(`:

- First inner token in `TableHintNames` → consume as hint clause.
- First inner token anything else → restore cursor; caller continues.

This is safe because a base-table FROM source has no column-alias list (only derived tables do, and those don't pass through this code path).
The disambiguation is structural: peek + restore, not pattern matching.

DML callers (INSERT / UPDATE / DELETE / MERGE) pass `allowLegacyParenForm: false`, which skips the entire peek-and-restore branch — a `(` after the target is always either a column list (INSERT) or a syntax error (UPDATE / DELETE / MERGE).

### Skip-balanced-parens for arguments

A `FORCESEEK(IX_foo(c1, c2))` payload and an unmodeled hint's parenthesized argument carry nested parens.
`SkipBalancedParens` walks tokens until depth returns to 0; contents are discarded once the captures above have read them.

## OPTION clause

Statement-level hints in `OPTION (hint [, …])`.
Position: after the trailing ORDER BY / OFFSET / FETCH on the outermost SELECT, after an UPDATE's or DELETE's WHERE, an `INSERT … VALUES` row list, or a MERGE's last `WHEN` clause.
Only the statement's own query takes one: a subquery's, a derived table's, a CTE's or an `EXISTS` test's is Msg 156 at the keyword, and so is a set operator or a second `OPTION` after it, while a `FOR XML` / `FOR JSON` clause may follow it (probed 2026-10-05 against SQL Server 2025).

```sql
select 1 option (recompile)
select 1 option (maxdop 4, fast 100)
select * from t order by id option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))
```

Every hint's grammar and arguments are checked as real checks them (`ConsumeOneOptionHint`, the whole set probed 2026-10-05 against SQL Server 2025):

- A word real doesn't know, or one missing its second word (`LOOP` alone, `KEEP` alone), is **Msg 102** on the hint word; `PARAMETERIZATION`, a plan-guide-only hint, is among them, and `FORCE` / `DISABLE EXTERNALPUSHDOWN` are refused at the second word.
- A whole hint followed by a word is read on and the token after it named (`maxdop 1 recompile)` is Msg 102 near `)`); a keyword there is Msg 156.
- An integer argument (`MAXDOP`, `FAST`, `MAXRECURSION`, `QUERYTRACEON`) that is a literal of another kind — a string, a decimal, an integer past `int` — is Msg 102 on the hint word; a sign, a variable or a binary literal is Msg 102 on itself.
  `MAXDOP` over 32767 is **Msg 304**, `MAXRECURSION` over 32767 **Msg 310**.
- A hint given twice with different values is **Msg 1042** (`Conflicting maxdop optimizer hints specified.`, likewise `fast` and `maxrecursion`); the same value twice is fine.
- `MIN_GRANT_PERCENT` / `MAX_GRANT_PERCENT = n` take an integer or decimal literal from 0 to 100, Msg 1042 outside it; `LABEL = 'x'` takes a string, and a second `LABEL` is **Msg 10768**.
- `OPTIMIZE FOR (@v {UNKNOWN | = literal} [, …])` binds each variable: undeclared is **Msg 137**, named twice across the clauses **Msg 4131**, given a non-literal **Msg 320**, a value of a type it can't convert from **Msg 206**, a value that fails to convert **Msg 4132**.
- `REMOTE JOIN` is **Msg 155** (`'remote' is not a recognized join option.`).
- `USE PLAN N''` is **Msg 8695**, and a plan document whose root isn't `ShowPlanXML` fails the showplan schema with **Msg 6913**.
- `TABLE HINT (object [, hint …])` names a source as the query exposes it — its alias when it has one, even an alias spelled as the table's own name — or it is **Msg 8723**; a second clause for one object is **Msg 8720**; a semantic hint (a lock, isolation or granularity hint, `NOEXPAND`) the source's own `WITH` clause carries none of is **Msg 8722**; its index hints are checked against the table (Msg 308 naming the object as the clause wrote it) and its `FORCESEEK` against the query's predicates (Msg 8622).

The join hints, `FORCE ORDER` and the `TABLE HINT` clauses come back on an `OptionClause` for `SettleStatementHints`, which runs once the statement's outermost query has parsed and checks them against every query specification the statement parsed (`ParserContext.JoinHintSites`).

### `USE HINT('name' [, 'name'] …)` — the one name-validated OPTION hint

Every DacFx reverse-engineering query (`sqlpackage /Action:Export`) ends with `OPTION (USE HINT('FORCE_LEGACY_CARDINALITY_ESTIMATION'))`.
Unlike the rest of the OPTION grammar (parse-and-discard, no argument check), `USE HINT` is the one hint whose string argument SQL Server validates by name, so the simulator does too (`ConsumeUseHint` in `Selection.Hints.cs`):

- Each argument must be a **non-null string literal** (`'…'` or `N'…'`).
  A non-string argument or empty parens raises the generic **Msg 102** (probe-confirmed: `USE HINT()` → `Incorrect syntax near ')'`, `USE HINT(123)` → `near '123'`).
- Each name is matched **case-insensitively** against `ValidUseHintNames` — the contents of `sys.dm_exec_valid_use_hints` on SQL Server 2025 (35 names, probed).
  An unknown name raises **Msg 10715** (`'<name>' is not a valid hint.`, class 15) — distinct from the generic OPTION-clause Msg 102.
  Real accepts a lowercase argument.
- Combines with other OPTION hints in either order (`OPTION (MAXDOP 1, USE HINT('…'))` and the reverse both parse).
- `USE PLAN N'…'` shares the `USE` first-word but is **not** `USE HINT` — the parser peeks the second word.

The valid-hints list is version-specific and grows across releases — an app targeting a hint added after SQL Server 2025 would need a refresh in `ValidUseHintNames`, the same trust-region trade-off the table-hint accept-list carries.
Tests: `QueryHintTests.Option_UseHint_*`.

Probe-confirmed surprise: SQL Server's OPTION clause has no dedicated unknown-hint code — `BANANA` inside `OPTION (...)` raises the same generic syntax error as any other parse failure, unlike table hints' dedicated Msg 321.

### `MAXRECURSION` retains runtime effect

`MAXRECURSION N` is applied to every in-scope `CteBinding.MaxRecursion`; the recursive-CTE executor reads the per-binding cap, and `MAXRECURSION 0` disables it.
Beside it, `RECOMPILE` and `USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING')` set their statement flags; every other hint is checked and has no execution effect.

## Enforced rejections

- **`EXEC (…) WITH RECOMPILE`** — a character string's `EXECUTE` takes `RESULT SETS` alone: `RECOMPILE` is **Msg 102** on the word and a second option Msg 102 on its comma, where a procedure call and `sp_executesql` take both (probed 2026-10-07 against SQL Server 2025).

- **Conflicting lock hints** — the whole pair matrix probed 2026-10-05 against SQL Server 2025, raised at parse inside `ValidateHintCombinations`:
  **Msg 1047** ("Conflicting locking hints specified.", fixed wording) for two isolation levels (`NOLOCK` / `READUNCOMMITTED`, `READCOMMITTED`, `READCOMMITTEDLOCK`, `REPEATABLEREAD`, `SERIALIZABLE` / `HOLDLOCK`, `SNAPSHOT`), two granularities (`ROWLOCK`, `PAGLOCK`, `TABLOCK`, `TABLOCKX` — `TABLOCK` beside `TABLOCKX` included), `UPDLOCK` beside `XLOCK`, and a dirty read beside `UPDLOCK`, `XLOCK` or any granularity;
  **Msg 650** for `READPAST` beside a dirty read or `SERIALIZABLE`, and — once the table resolves — `READPAST` in a READ UNCOMMITTED, SERIALIZABLE or SNAPSHOT session without a hint naming a level it takes (ahead of the snapshot refusal Msg 3952).
  `SNAPSHOT` beside a non-isolation hint on a disk table reaches Msg 367 instead.
- **`PAGLOCK` where the heap or clustered index disallows page locks** — **Msg 651**.
- **Bulk-load hints** — `IGNORE_CONSTRAINTS` / `IGNORE_TRIGGERS` on a read, and any of the four on a write's target, are **Msg 8171** state 1 naming the hint and the object as written; `KEEPIDENTITY` / `KEEPDEFAULTS` on a read are discarded.
- **DML-target rejections** (`ValidateDmlTargetHints`, at every INSERT / UPDATE / DELETE / MERGE target site) — **Msg 1065** for `NOLOCK` / `READUNCOMMITTED`; on an INSERT, UPDATE or DELETE target **Msg 10724** for `FORCESEEK`, **Msg 10745** for `FORCESCAN` (both reported at line 15, as real does) and **Msg 1069** for `INDEX(…)`, which therefore never reaches Msg 308; **Msg 4102** for `READPAST` on an INSERT target.
  A MERGE target takes index hints, checked against its table like a FROM source's.
- **Per-table index existence** (`ValidateIndexHintArguments`) — an id is checked against the table's `sys.indexes` ids: `0` is always valid, a heap's `1` is **Msg 307** while its first nonclustered index's `2` is valid, and a disabled index's id is **Msg 316**.
  A name matches the PRIMARY KEY / UNIQUE constraints and the indexes case-insensitively: a disabled one is **Msg 315**, an XML index's **Msg 309**, anything else **Msg 308**; a temporary table is named alone (`'#x'`), anything else as `'<schema>.<table>'`.
  The first failing argument raises.
  A module body's hints are checked when it first runs, not at its `CREATE`.
  `FORCESEEK`'s nested form names its index by name or by id — `FORCESEEK(2(a))`, the messages then giving the id as the name — through the same checks, and `FORCESEEK(0(…))` is **Msg 10749**.
- **`INDEX(0)` beside another index** — **Msg 8622** in state 2 (`INDEX(0, 0)` and `INDEX(1, 1)` are fine).
- **FORCESEEK's seek columns** — the nested form's column list has to be a **leading prefix of the named index's own key columns, in order**, and `ValidateForceSeekColumns` measures it once the table has resolved:
  more names than the index has key columns is **Msg 365** (checked first, so a list that is both too long and misspelled reports the count), and the first name that isn't the key column at its position is **Msg 362** naming it.
  An `INCLUDE`d column, a key column out of order and an unknown name all land on Msg 362 alike; the match is collation-driven, and both messages name the **base table** rather than the alias the query wrote (probe-confirmed).
  A key constraint is a legal target too, its `StorageOrdinals` mapped back to the declared column names.
- **`FORCESEEK` a plan can't honor** — **Msg 8622**, raised by `ValidateForcedSeeks` once the query's predicates have parsed, with real's refusal as its model (probed 2026-09-28 against SQL Server 2025).
  A hinted source needs some predicate an index seek can answer (`BooleanExpression.OffersSeek`): a top-level conjunct of the WHERE, the HAVING or any join's ON that compares a key-leading column — through a conversion that stays in the column's type family — against a side reading nothing of the source, an `IN`, a `BETWEEN` (a column on either bound counts), `IS [NOT] NULL`, a `LIKE` whose constant pattern doesn't lead with `%` or `_`, an `OR` every branch of which seeks, a `NOT` of any of those; or, for a one-column subquery, its select-list column, which an enclosing `IN` seeks on.
  A predicate settled false while compiling needs no seek.
  Refused: no predicate at all, an unindexed or non-leading column, a column in a function or arithmetic, a cast to a string, a `varchar` column under a SQL collation against a Unicode value, a column compared with its own table's, a join or `CROSS JOIN` whose inner side offers nothing, a heap or columnstore-only table.
  An index named beside the hint (`FORCESEEK(ix(…))`, `INDEX(ix)`, `INDEX(n)` by its id) narrows the keys to that one and `INDEX(0)` to none, a disabled index seeks nothing, and a seek-column list needs a predicate on every column it names.
  `FORCESCAN` beside an `INDEX` naming only nonclustered indexes is refused too when the query reads a column those indexes don't carry, the lookup being a seek.
  It is a compile error: it ends the batch before any statement runs, and a `TRY` in the batch doesn't catch it; a statement over a table the batch creates meets it when it runs, and a procedure body at its execution rather than at `CREATE`; a binder error earlier in the batch keeps it from being reported, and a batch reports only the first.
  `ForceSeekPlanTests` holds the probed shapes both ways.
- **`FORCESEEK` on a view or CTE reference** — real carries it to every table the body reads, at any depth of views, CTEs, derived tables and `UNION` branches, so each needs a seek of its own (probed 2026-10-07 against SQL Server 2025), and **Msg 8622** is raised as for a table (`BodySeeksAreFeasible`).
  A body table seeks on a predicate of its own block, on a join's only once the table on the other side seeks, or on the reading query's predicate over a column the body passes the key through as — not past a `TOP`, an `OFFSET` or a window function the column isn't a partition key of; a heap or an unindexed column is refused, as is a view read with no predicate.
  `FORCESEEK(ix(…))` naming an index on a view without `NOEXPAND` is **Msg 364**.
- **An `INDEX` hint naming one filtered index the query doesn't confine itself to** — **Msg 8622** (probed 2026-10-06 against SQL Server 2025), settled with `FORCESEEK`'s (`HintsUnimpliedFilteredIndex`): the query's top-level conjuncts over literals have to prove the filter, so `b >= 6` and `b = 6` imply `b > 5` over an integer column, any comparison implies `IS NOT NULL`, and an `IN` list implies a filter listing its values, while a variable, an `OR` or a conjunct on another table's column proves nothing; naming several indexes is planned whatever their filters.
- **Hint combinations real's optimizer refuses outright** (class 15, raised with the hint list): **Msg 10746** for `FORCESEEK` beside `FORCESCAN`, **Msg 10747** for a nested `FORCESEEK(ix(…))` beside an `INDEX` hint, **Msg 10750** for `FORCESCAN` beside more than one index, and a bare `FORCESEEK(ix)` without its column list is **Msg 102** on the closing parenthesis (probed 2026-09-28).
- **The legacy no-`WITH` parenthesized form** splits on the alias, matching real.
  With an alias written, the parens are unambiguously a hint list and an unknown name is Msg 321.
  Without one they are an **argument list**: real binds them first, so each name inside reports its own **Msg 207** — the source is not in scope for its own arguments, so even a name the table carries is unresolvable — and the run closes with **Msg 215** (`Parameters supplied for object 't' which is not a function. If the parameters are intended as a table hint, a WITH keyword is required.`), all of it arriving as one multi-error exception the way a client sees it.
  A scalar argument (a literal, a variable) reports Msg 215 alone.
  `INDEX` is the one name real refuses outright in that form, wherever in the list it stands and whether or not an alias preceded the parens: **Msg 1018**, naming the hint as written and carrying real's own inconsistent capitalization (`… A WITH keyword and parenthesis are now required.`).
  A table variable takes no hint at all: a `WITH` after it ends the statement (Msg 319), and the parenthesized form is Msg 1018 for any hint name, Msg 102 for anything else (probed 2026-09-26).
  A rowset function's call takes none either (probed 2026-10-05, 2026-10-07 and 2026-10-08 against SQL Server 2025).
  Real reads a `WITH` right after the call, no alias between, as the start of a column schema like `OPENJSON`'s, so a hint is refused as a schema that doesn't parse: a reserved hint word where a column name belongs — `INDEX`, `HOLDLOCK` — is **Msg 1018** naming it, any other hint a column missing its type, **Msg 102** at what follows it (`WITH (NOLOCK)` near `)`, `WITH (NOLOCK, INDEX(ix))` near `,`), another reserved word Msg 156, and a schema that does parse (`WITH (a int)`) **Msg 319** at state 2, at line 12 wherever the statement stands (`Selection.RejectRowsetFunctionWith`, for user functions, `STRING_SPLIT`, `GENERATE_SERIES` and the other built-in rowsets, `OPENJSON`'s own schema taking the Msg 1018 too).
  After an alias a `WITH` ends the statement (Msg 319), and a user function's parenthesized list is a column alias list, **Msg 317**, alias or none, save `INDEX` / `HOLDLOCK`'s Msg 1018.

## Not enforced

- **`FORCESEEK` through an inline table-valued function** a view or CTE reads, and through a set operation other than `UNION`: real carries the hint into it, the simulator leaves the body unjudged and runs the read.
- **Two tables `FORCESEEK` joins outside a view, each seeking only on the join** (`a WITH (FORCESEEK) JOIN b WITH (FORCESEEK) ON a.k = b.k`) are accepted, where through a view the join seeks one side only once the other side seeks.
- **Msg 364's companion warning**: real follows it with the view's Msg 4430, which the simulator, raising Msg 364 as the batch compiles, doesn't send.
- **The order an index-hinted scan returns rows in** — real scans the hinted index and returns its key order (`WITH (INDEX(ix_c))` over `c DESC` comes back by `c` descending, a heap's `INDEX(ix)` by `ix`'s key, re-probed 2026-10-06); the simulator's scan keeps its own order.
  So does an unhinted query real answers from a narrower covering index, and an `IN` list seeks its values in written order where real sorts them.
  Not chased: the order is the physical one of the index real's plan reads.
- **Join-hint feasibility through `APPLY` and semi-joins** — real refuses `OPTION (HASH JOIN)` over a `CROSS APPLY` or an `EXISTS` whose correlation isn't an equality once decorrelated, and some `RIGHT LOOP JOIN`s, as its decorrelation and join reordering decide; those are left alone here.
  Not chased: the shapes follow real's rewrites, not the query as written.
- **Msg 8625 from dynamic SQL run by `sp_executesql`** after `EXEC (…)` ran the same text: real's cached plan sends none, where the simulator's separate compile sends it once more.
- **A table function's legacy `(NOLOCK)` form** — `STRING_SPLIT(…) (NOLOCK)` is Msg 102 near `nolock` here and real's Msg 195 state 15 (`'string_split' is not a recognized function name.`, probed 2026-10-08 against SQL Server 2025); a user function's is modeled (above).
- **`INDEX = (value-list)` equals-form** — probe-confirmed that real SQL Server raises `Msg 102` on the equals-with-multiple-values form anyway (the docs notwithstanding), so the simulator's "= takes one literal" rule matches by parsing as well.

`FROM t NOLOCK` without parens is *not* a deprecated hint shape — it parses as the bare-alias form (`FROM t <alias>`) on both real SQL Server and the simulator.
`nolock` / `readpast` / etc. aren't reserved keywords, so they're valid bare aliases via the standard `ConsumeOptionalAlias` `Name`-token path.
The hint-vs-alias question here has only one answer (alias), no divergence.

## Probe artifacts

Captured against SQL Server 2025 from `/tmp/hint-probe/` and `/tmp/insert-hints/` (both deleted after use).
Notable findings:

- `Msg 321` for unknown table hint, with surrounding double-quotes on the offending name.
- `Msg 102` for unknown OPTION hint — no dedicated code.
- `Msg 1047` for conflicting locking hints — fixed wording ("Conflicting locking hints specified.") regardless of which pair conflicted; the full pair matrix is under [Enforced rejections](#enforced-rejections).
- `Msg 1065` for `NOLOCK` / `READUNCOMMITTED` on any DML target.
- `Msg 1069` for `INDEX(…)` on an INSERT / UPDATE / DELETE target — fires *before* per-index validation, so unknown-name there surfaces as 1069 not 308.
- `Msg 307` for out-of-range `INDEX(N)` id — the suffix `(specified in the FROM clause)` is hard-coded in the wording even though the hint can appear on JOIN-RHS too.
- `Msg 308` for unknown `INDEX(name)` / `INDEX = name`, including PRIMARY KEY and UNIQUE constraint names which both qualify as valid arguments.
  Case-insensitive lookup.
  Schema-qualified table reference surfaces in the message as `'<schema>.<leaf>'`.
- `INDEX(0)` is always valid alone — accepted on heap-only tables and on PK-tables alike, even though sys.indexes only synthesizes a HEAP row (index_id=0) for heap tables.
- `INDEX(-1)` raises generic Msg 102 — negative integer literal isn't in the hint-argument grammar.
- Legacy `(hint)` form without `WITH` works on **FROM / JOIN-RHS only** — rejected on every DML target.
- MERGE target uses **hint-then-alias** placement; alias-then-hint raises Msg 156 there.
  Every other site is alias-then-hint.
- `INSERT t (TABLOCK) …` raises Msg 207 (the paren is always a column list); the legacy form is structurally unreachable on INSERT.
- INSERT / MERGE on a `@t` target rejects `WITH` outright (Msg 156).
- The "bare `FROM t NOLOCK` without parens" shape isn't a hint at all — hint-naming identifiers aren't reserved, so it parses as bare-alias.
