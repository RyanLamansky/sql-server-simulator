# Query hints — table & OPTION clauses (parse-and-discard)

Parse-and-discard support for SQL Server's hint grammar.
The simulator doesn't model locking / isolation / planner choices / indexes, so all recognized hint shapes are accepted and ignored.
The value of shipping this is grammar compatibility: applications with `WITH (NOLOCK)` hints in their schema / raw SQL don't trip `Msg 102` on parse.

Implementation lives in [`src/SqlServerSimulator/Parser/Selection.Hints.cs`](../../src/SqlServerSimulator/Parser/Selection.Hints.cs).

## Inline join-algorithm hints

`MERGE` / `HASH` / `LOOP` / `REMOTE` between the join type and `JOIN` — `INNER MERGE JOIN`, `LEFT OUTER HASH JOIN`, `FULL LOOP JOIN`.
Accept-and-discard: the hint names the physical operator real should use, and the simulator picks its own strategy, so it can never change an answer (probe-confirmed — hinted and unhinted forms return identical rows).
Distinct from the statement-level `OPTION (MERGE JOIN)` spelling, which is parsed separately.

Real accepts all four hints against **every** join type, including combinations that look implausible: `FULL LOOP JOIN` and `RIGHT LOOP JOIN` are both legal, so there is no pairing to refuse.
It does require the type keyword, and refuses three shapes (all probe-confirmed):

- `CROSS <hint> JOIN` → **Msg 156** naming the hint as a keyword.
- Two hints (`INNER MERGE HASH JOIN`) → **Msg 102** on the second.
- A word that isn't a hint (`INNER NONSENSE JOIN`) → **Msg 155** `'nonsense' is not a recognized join option.` — this position's own error, not the generic syntax one.


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

`NOLOCK`, `READPAST`, `READUNCOMMITTED`, `READCOMMITTED`, `READCOMMITTEDLOCK`, `REPEATABLEREAD`, `SERIALIZABLE`, `SNAPSHOT`, `HOLDLOCK`, `UPDLOCK`, `XLOCK`, `TABLOCK`, `TABLOCKX`, `ROWLOCK`, `PAGLOCK`, `NOWAIT`, `KEEPIDENTITY`, `KEEPDEFAULTS`, `NOEXPAND`, `IGNORE_CONSTRAINTS`, `IGNORE_TRIGGERS`, `FORCESEEK`, `FORCESCAN`, `INDEX`, `SPATIAL_WINDOW_MAX_CELLS`, `READONLY`, `REMOTE`.

Unknown hint name → **Msg 321** verbatim: `"<name>" is not a recognized table hints option.` (probe-confirmed against SQL Server 2025).

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

`INDEX(IX_foo(c1, c2))` and `OPTIMIZE FOR (@p UNKNOWN)` carry nested parens.
`SkipBalancedParens` walks tokens until depth returns to 0; contents are discarded.

## OPTION clause

Statement-level hints in `OPTION (hint [, …])`.
Position: after the trailing ORDER BY / OFFSET / FETCH on the outermost SELECT.

```sql
select 1 option (recompile)
select 1 option (maxdop 4, fast 100)
select * from t order by id option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))
```

First-word accept-list (case-insensitive, in `OptionHintFirstWords`):

`RECOMPILE`, `MAXRECURSION`, `MAXDOP`, `FAST`, `LOOP`, `HASH`, `MERGE`, `FORCE`, `KEEPFIXED`, `KEEP`, `ROBUST`, `OPTIMIZE`, `USE`, `EXPAND`, `IGNORE_NONCLUSTERED_COLUMNSTORE_INDEX`, `NO_PERFORMANCE_SPOOL`, `QUERYTRACEON`, `TABLE`, `PARAMETERIZATION`, `ORDER`, `CONCAT`.

Multi-word hints (`LOOP JOIN`, `FORCE ORDER`, `KEEPFIXED PLAN`, `OPTIMIZE FOR UNKNOWN`, `HASH GROUP`, `CONCAT UNION`, etc.) are accepted via first-word match + skip-tokens-to-comma-or-paren.
The trailing words / numeric arguments / parenthesized payloads aren't validated beyond bracket balancing.

### `USE HINT('name' [, 'name'] …)` — the one name-validated OPTION hint

Every DacFx reverse-engineering query (`sqlpackage /Action:Export`) ends with `OPTION (USE HINT('FORCE_LEGACY_CARDINALITY_ESTIMATION'))`.
Unlike the rest of the OPTION grammar (parse-and-discard, no argument check), `USE HINT` is the one hint whose string argument SQL Server validates by name, so the simulator does too (`ConsumeUseHint` in `Selection.Hints.cs`):

- Each argument must be a **non-null string literal** (`'…'` or `N'…'`).
  A non-string argument or empty parens raises the generic **Msg 102** (probe-confirmed: `USE HINT()` → `Incorrect syntax near ')'`, `USE HINT(123)` → `near '123'`).
- Each name is matched **case-insensitively** against `ValidUseHintNames` — the contents of `sys.dm_exec_valid_use_hints` on SQL Server 2025 (35 names, probed).
  An unknown name raises **Msg 10715** (`'<name>' is not a valid hint.`, class 15) — distinct from the generic OPTION-clause Msg 102.
  Real accepts a lowercase argument.
- Combines with other OPTION hints in either order (`OPTION (MAXDOP 1, USE HINT('…'))` and the reverse both parse).
- `USE PLAN N'…'` shares the `USE` first-word but is **not** `USE HINT` — the parser peeks the second word and only intercepts `HINT`, leaving `USE PLAN` (and any other `USE`-prefixed hint) on the generic parse-and-discard skip.

The valid-hints list is version-specific and grows across releases — an app targeting a hint added after SQL Server 2025 would need a refresh in `ValidUseHintNames`, the same trust-region trade-off the table-hint accept-list carries.
Tests: `QueryHintTests.Option_UseHint_*`.

Unknown first-word → **Msg 102** generic syntax error (`Incorrect syntax near '<name>'`).
Probe-confirmed surprise: SQL Server's OPTION clause has no dedicated unknown-hint code — `BANANA` inside `OPTION (...)` raises the same generic syntax error as any other parse failure, unlike table hints' dedicated Msg 321.

### `MAXRECURSION` retains runtime effect

The only OPTION hint with observable simulator behavior is `MAXRECURSION N`.
Its argument is strict-parsed (integer literal in 0–32767) and applied to every in-scope `CteBinding.MaxRecursion`.
The recursive-CTE executor reads the per-binding cap; `MAXRECURSION 0` disables the cap.
Every other recognized OPTION hint is a pure no-op.

## Enforced rejections

- **Conflict detection** — `Msg 1047` ("Conflicting locking hints specified.") fires when `NOLOCK` / `READUNCOMMITTED` appears in the same hint list as any of `XLOCK` / `UPDLOCK` / `HOLDLOCK` / `SERIALIZABLE` / `REPEATABLEREAD` / `TABLOCKX`.
  The wording is fixed regardless of which pair conflicted (probe-confirmed).
  Raised at parse-time inside `ValidateHintCombinations`.
- **DML-target rejections** — `Msg 1065` ("The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.") for `NOLOCK` / `READUNCOMMITTED` on any DML target; `Msg 1069` ("Index hints are only allowed in a FROM or OPTION clause.") for `INDEX(…)` / `FORCESEEK` / `FORCESCAN` on the same.
  Both probe-confirmed verbatim.
  Raised inside `ValidateDmlTargetHints` at every INSERT / UPDATE / DELETE / MERGE target site.
  **Order matters**: Msg 1069 fires before any per-index validation, so `UPDATE t WITH (INDEX(name))` always raises 1069 — never reaches Msg 308.
- **Per-table index existence** — `Msg 307` ("Index ID N on table '<schema>.<table>' (specified in the FROM clause) does not exist.") for an out-of-range `INDEX(N)` id; `Msg 308` ("Index '<name>' on table '<schema>.<table>' …") for an unknown `INDEX(name)` / `INDEX = name`.
  Validation rule for the integer form: `N == 0` is always valid (the "heap scan" reference, accepted even on clustered tables); `N >= 1` is valid iff `N <= KeyConstraints.Count + Indexes.Count`.
  Name form matches case-insensitively against `HeapTable.KeyConstraints[].Name` (PRIMARY KEY / UNIQUE) plus `HeapTable.Indexes[].Name` (CREATE INDEX).
  Wired only into the FROM-source / JOIN-RHS heap-table path (`ValidateIndexHintArguments` in `Selection.Hints.cs`); arguments are captured at parse time into `TableHintInfo.IndexArguments` via the dedicated `ConsumeIndexHintArguments` walker (handles both `INDEX(arg [, …])` and `INDEX = arg` forms; negative integer arg raises Msg 102 at parse, matching probe).
  Multi-arg `INDEX(bad, good)` raises Msg 308 on the first failing argument and skips the rest.
  `FORCESEEK`'s nested form carries an index name too — `FORCESEEK(IX_foo(c1, c2))` — and the parser peeks that leading name into the same `IndexArguments` list (rewinding so the balanced-paren skip stays the single consumer of the payload), so it validates through the identical path and raises the identical Msg 308.
  The bare `FORCESEEK` / `FORCESCAN` forms carry no name and are unaffected.
- **FORCESEEK's seek columns** — the nested form's column list has to be a **leading prefix of the named index's own key columns, in order**, and `ValidateForceSeekColumns` measures it once the table has resolved:
  more names than the index has key columns is **Msg 365** (checked first, so a list that is both too long and misspelled reports the count), and the first name that isn't the key column at its position is **Msg 362** naming it.
  An `INCLUDE`d column, a key column out of order and an unknown name all land on Msg 362 alike; the match is collation-driven, and both messages name the **base table** rather than the alias the query wrote (probe-confirmed).
  A key constraint is a legal target too, its `StorageOrdinals` mapped back to the declared column names.
- **The legacy no-`WITH` parenthesized form** splits on the alias, matching real.
  With an alias written, the parens are unambiguously a hint list and an unknown name is Msg 321.
  Without one they are an **argument list**: real binds them first, so each name inside reports its own **Msg 207** — the source is not in scope for its own arguments, so even a name the table carries is unresolvable — and the run closes with **Msg 215** (`Parameters supplied for object 't' which is not a function. If the parameters are intended as a table hint, a WITH keyword is required.`), all of it arriving as one multi-error exception the way a client sees it.
  A scalar argument (a literal, a variable) reports Msg 215 alone.
  `INDEX` is the one name real refuses outright in that form, wherever in the list it stands and whether or not an alias preceded the parens: **Msg 1018**, carrying real's own inconsistent capitalization (`… A WITH keyword and parenthesis are now required.`).

## Not enforced

- **FORCESEEK plan rejection** (`Msg 8622`) — fires on real SQL Server when the planner can't honor the directive, which it does for every `FORCESEEK` over a query with no sargable predicate; the simulator validates the hint's names and then reads normally, since it has no plan to declare infeasible.
- **`INDEX = (value-list)` equals-form** — probe-confirmed that real SQL Server raises `Msg 102` on the equals-with-multiple-values form anyway (the docs notwithstanding), so the simulator's "= takes one literal" rule matches by parsing as well.

`FROM t NOLOCK` without parens is *not* a deprecated hint shape — it parses as the bare-alias form (`FROM t <alias>`) on both real SQL Server and the simulator.
`nolock` / `readpast` / etc. aren't reserved keywords, so they're valid bare aliases via the standard `ConsumeOptionalAlias` `Name`-token path.
The hint-vs-alias question here has only one answer (alias), no divergence.

## Probe artifacts

Captured against SQL Server 2025 from `/tmp/hint-probe/` and `/tmp/insert-hints/` (both deleted after use).
Notable findings:

- `Msg 321` for unknown table hint, with surrounding double-quotes on the offending name.
- `Msg 102` for unknown OPTION hint — no dedicated code.
- `Msg 1047` for conflicting locking hints — fixed wording ("Conflicting locking hints specified.") regardless of which pair conflicted.
- `Msg 1065` for `NOLOCK` / `READUNCOMMITTED` on any DML target.
- `Msg 1069` for `INDEX(…)` / `FORCESEEK` / `FORCESCAN` on any DML target — fires *before* per-index validation, so unknown-name on a DML target surfaces as 1069 not 308.
- `Msg 307` for out-of-range `INDEX(N)` id — the suffix `(specified in the FROM clause)` is hard-coded in the wording even though the hint can appear on JOIN-RHS too.
- `Msg 308` for unknown `INDEX(name)` / `INDEX = name`, including PRIMARY KEY and UNIQUE constraint names which both qualify as valid arguments.
  Case-insensitive lookup.
  Schema-qualified table reference surfaces in the message as `'<schema>.<leaf>'`.
- `INDEX(0)` is always valid — accepted on heap-only tables and on PK-tables alike, even though sys.indexes only synthesizes a HEAP row (index_id=0) for heap tables.
- `INDEX(-1)` raises generic Msg 102 — negative integer literal isn't in the hint-argument grammar.
- Legacy `(hint)` form without `WITH` works on **FROM / JOIN-RHS only** — rejected on every DML target.
- MERGE target uses **hint-then-alias** placement; alias-then-hint raises Msg 156 there.
  Every other site is alias-then-hint.
- `INSERT t (TABLOCK) …` raises Msg 207 (the paren is always a column list); the legacy form is structurally unreachable on INSERT.
- INSERT / MERGE on a `@t` target rejects `WITH` outright (Msg 156).
- The "bare `FROM t NOLOCK` without parens" shape isn't a hint at all — hint-naming identifiers aren't reserved, so it parses as bare-alias.
