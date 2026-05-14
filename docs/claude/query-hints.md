# Query hints — table & OPTION clauses (parse-and-discard)

Parse-and-discard support for SQL Server's hint grammar. The simulator
doesn't model locking / isolation / planner choices / indexes, so all
recognized hint shapes are accepted and ignored. The value of shipping
this is grammar compatibility: applications with `WITH (NOLOCK)` hints
in their schema / raw SQL no longer trip `Msg 102` on parse.

Implementation lives in
[`SqlServerSimulator/Parser/Selection.Hints.cs`](../../SqlServerSimulator/Parser/Selection.Hints.cs).

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
| MERGE source          | not modeled            | n/a                  |

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

The legacy bare-paren `(hint)` form is **FROM / JOIN-RHS only** — INSERT
treats `(` as the column-list opener (probe-confirmed Msg 207 on the
would-be hint name), and UPDATE / DELETE / MERGE all raise Msg 102 on
the bare-paren form. The parser signals this with the
`allowLegacyParenForm` parameter on `ParseOptionalTableHints` (default
`true`; INSERT / UPDATE / DELETE / MERGE pass `false`).

**MERGE is the odd one out for hint-vs-alias placement** — hint comes
between the target name and the optional `[AS] alias`, not after.
Real SQL Server rejects alias-then-hint on MERGE target with Msg 156.

**Table-variable targets reject hints entirely** for INSERT / MERGE
(probe-confirmed Msg 156). The parser short-circuits via
`BatchContext.IsTableVariableName` before calling
`ParseOptionalTableHints`; `WITH` after `@t` falls through to the
default Msg 102 at the dispatch site.

**MERGE source hints aren't modeled** — the simulator only supports the
parenthesized `USING (SELECT/VALUES …)` form, and probe-confirmed that
real SQL Server rejects WITH-hints on a parenthesized source anyway
(Msg 156). Bare-table USING (`USING t AS s WITH (nolock)`) accepts hints
on real SQL Server but isn't parsed by the simulator yet.

Hint-argument shapes recognized:

| Shape           | Example                                       |
|-----------------|-----------------------------------------------|
| Bare name       | `NOLOCK`                                      |
| `name = value`  | `SPATIAL_WINDOW_MAX_CELLS = 1024`             |
| `name(args)`    | `INDEX(IX_foo)`, `FORCESEEK(IX_foo(c1, c2))`  |

Closed accept-list (case-insensitive, in `TableHintNames`):

`NOLOCK`, `READPAST`, `READUNCOMMITTED`, `READCOMMITTED`,
`READCOMMITTEDLOCK`, `REPEATABLEREAD`, `SERIALIZABLE`, `SNAPSHOT`,
`HOLDLOCK`, `UPDLOCK`, `XLOCK`, `TABLOCK`, `TABLOCKX`, `ROWLOCK`,
`PAGLOCK`, `NOWAIT`, `KEEPIDENTITY`, `KEEPDEFAULTS`, `NOEXPAND`,
`IGNORE_CONSTRAINTS`, `IGNORE_TRIGGERS`, `FORCESEEK`, `FORCESCAN`,
`INDEX`, `SPATIAL_WINDOW_MAX_CELLS`, `READONLY`, `REMOTE`.

Unknown hint name → **Msg 321** verbatim: `"<name>" is not a recognized
table hints option.` (probe-confirmed against SQL Server 2025).

### Legacy `(hint)` form disambiguation

The legacy `FROM t (nolock)` form omits `WITH` and is FROM / JOIN-RHS
only. After parsing a base-table name + optional alias,
`ParseOptionalTableHints` peeks one token past `(`:

- First inner token in `TableHintNames` → consume as hint clause.
- First inner token anything else → restore cursor; caller continues.

This is safe because a base-table FROM source has no column-alias list
(only derived tables do, and those don't pass through this code path).
The disambiguation is structural: peek + restore, not pattern matching.

DML callers (INSERT / UPDATE / DELETE / MERGE) pass
`allowLegacyParenForm: false`, which skips the entire peek-and-restore
branch — a `(` after the target is always either a column list (INSERT)
or a syntax error (UPDATE / DELETE / MERGE).

### Skip-balanced-parens for arguments

`INDEX(IX_foo(c1, c2))` and `OPTIMIZE FOR (@p UNKNOWN)` carry nested
parens. `SkipBalancedParens` walks tokens until depth returns to 0;
contents are discarded.

## OPTION clause

Statement-level hints in `OPTION (hint [, …])`. Position: after the
trailing ORDER BY / OFFSET / FETCH on the outermost SELECT.

```sql
select 1 option (recompile)
select 1 option (maxdop 4, fast 100)
select * from t order by id option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))
```

First-word accept-list (case-insensitive, in `OptionHintFirstWords`):

`RECOMPILE`, `MAXRECURSION`, `MAXDOP`, `FAST`, `LOOP`, `HASH`, `MERGE`,
`FORCE`, `KEEPFIXED`, `KEEP`, `ROBUST`, `OPTIMIZE`, `USE`, `EXPAND`,
`IGNORE_NONCLUSTERED_COLUMNSTORE_INDEX`, `NO_PERFORMANCE_SPOOL`,
`QUERYTRACEON`, `TABLE`, `PARAMETERIZATION`, `ORDER`, `CONCAT`.

Multi-word hints (`LOOP JOIN`, `FORCE ORDER`, `KEEPFIXED PLAN`,
`OPTIMIZE FOR UNKNOWN`, `HASH GROUP`, `CONCAT UNION`, etc.) are accepted
via first-word match + skip-tokens-to-comma-or-paren. The trailing
words / numeric arguments / parenthesized payloads aren't validated
beyond bracket balancing.

Unknown first-word → **Msg 102** generic syntax error
(`Incorrect syntax near '<name>'`). Probe-confirmed surprise: SQL
Server's OPTION clause has no dedicated unknown-hint code — `BANANA`
inside `OPTION (...)` raises the same generic syntax error as any other
parse failure, unlike table hints' dedicated Msg 321.

### `MAXRECURSION` retains runtime effect

The only OPTION hint with observable simulator behavior is
`MAXRECURSION N`. Its argument is strict-parsed (integer literal in
0–32767) and applied to every in-scope `CteBinding.MaxRecursion`. The
recursive-CTE executor reads the per-binding cap; `MAXRECURSION 0`
disables the cap. Every other recognized OPTION hint is a pure no-op.

## Not enforced

- **Conflict detection** (`Msg 1047` for `NOLOCK + XLOCK` etc.) — no
  lock state to conflict over. Apps that exercise this in real SQL
  Server hit rejection there; the simulator silently parses both.
- **DML-target-specific hint rejections** (`Msg 1065` for `NOLOCK` /
  `READUNCOMMITTED` on INSERT / UPDATE / DELETE / MERGE targets,
  `Msg 1069` for `INDEX(…)` on the same) — these are real SQL Server
  diagnostics with no lock / index state to back them. Probe-confirmed
  (2026-05-14) that both fire on real SQL Server; the simulator parses
  every hint name uniformly via the closed accept-list.
- **INDEX-name validity** (`Msg 308`) — the simulator doesn't model
  index usage, so `INDEX(IX_does_not_exist)` parses successfully.
- **FORCESEEK plan rejection** (`Msg 8622`) — same posture.
- **`INDEX = (value-list)` equals-form** — probe-confirmed that real
  SQL Server raises `Msg 102` on the equals-form anyway (the docs
  notwithstanding), so the simulator's rejection happens to match by
  accident.

`FROM t NOLOCK` without parens is *not* a deprecated hint shape — it
parses as the bare-alias form (`FROM t <alias>`) on both real SQL Server
and the simulator. `nolock` / `readpast` / etc. aren't reserved
keywords, so they're valid bare aliases via the standard
`ConsumeOptionalAlias` `Name`-token path. The hint-vs-alias question
here has only one answer (alias), no divergence.

## Probe artifacts

Captured 2026-05-14 against SQL Server 2025 from `/tmp/hint-probe/` and
`/tmp/insert-hints/` (both deleted after their bundles landed). Notable
findings:

- `Msg 321` for unknown table hint, with surrounding double-quotes on
  the offending name.
- `Msg 102` for unknown OPTION hint — no dedicated code.
- `Msg 1047` for conflicting locking hints (out of scope here).
- `Msg 1065` for `NOLOCK` / `READUNCOMMITTED` on any DML target.
- `Msg 1069` for `INDEX(…)` on any DML target.
- Legacy `(hint)` form without `WITH` works on **FROM / JOIN-RHS only** —
  rejected on every DML target.
- MERGE target uses **hint-then-alias** placement; alias-then-hint
  raises Msg 156 there. Every other site is alias-then-hint.
- `INSERT t (TABLOCK) …` raises Msg 207 (the paren is always a column
  list); the legacy form is structurally unreachable on INSERT.
- INSERT / MERGE on a `@t` target rejects `WITH` outright (Msg 156).
- The "bare `FROM t NOLOCK` without parens" shape isn't a hint at all —
  hint-naming identifiers aren't reserved, so it parses as bare-alias.
