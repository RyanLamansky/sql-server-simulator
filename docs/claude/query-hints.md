# Query hints — table & OPTION clauses (parse-and-discard)

Parse-and-discard support for SQL Server's hint grammar. The simulator
doesn't model locking / isolation / planner choices / indexes, so all
recognized hint shapes are accepted and ignored. The value of shipping
this is grammar compatibility: applications with `WITH (NOLOCK)` hints
in their schema / raw SQL no longer trip `Msg 102` on parse.

Implementation lives in
[`SqlServerSimulator/Parser/Selection.Hints.cs`](../../SqlServerSimulator/Parser/Selection.Hints.cs).

## Table hints

Position: after a FROM source (or JOIN-RHS table), or after an UPDATE /
DELETE target. Both spellings are accepted:

```sql
select * from t with (nolock, holdlock)
select * from t (nolock)            -- legacy, no WITH
select a.id from t a inner join t b with (nolock) on a.id = b.id
update t with (rowlock) set v = v + 1
delete from t with (tablock) where id = 5
```

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

The legacy `FROM t (nolock)` form omits `WITH`. After parsing a base-table
name + optional alias, `ParseOptionalTableHints` peeks one token past `(`:

- First inner token in `TableHintNames` → consume as hint clause.
- First inner token anything else → restore cursor; caller continues.

This is safe because a base-table FROM source has no column-alias list
(only derived tables do, and those don't pass through this code path).
The disambiguation is structural: peek + restore, not pattern matching.

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

Captured 2026-05-14 against SQL Server 2025 from `/tmp/hint-probe/`
(deleted after this bundle landed). Notable findings:

- `Msg 321` for unknown table hint, with surrounding double-quotes on
  the offending name.
- `Msg 102` for unknown OPTION hint — no dedicated code.
- `Msg 1047` for conflicting locking hints (out of scope here).
- Legacy `(hint)` form without `WITH` works on base tables.
- The "bare `FROM t NOLOCK` without parens" shape isn't a hint at all —
  hint-naming identifiers aren't reserved, so it parses as bare-alias.
