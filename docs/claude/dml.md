# DML — UPDATE / DELETE / INSERT…SELECT / INSERT…EXEC / SELECT…INTO / MERGE / rowversion

## The DML target

`INSERT`, `UPDATE`, `DELETE` and `MERGE` read their target through one layer (`Simulation.DmlTarget.cs`): `ParseDmlTarget` resolves the name once into a `DmlTarget` — a linked server's table (four-part, `OPENQUERY`, `OPENROWSET`), a view or a CTE the statement writes through, a table, or nothing — and each verb routes on what it found.
A view routes through `RouteViewWrite`: the action's `INSTEAD OF` trigger, its one base table, a join view's written table, or real's refusal.
The layer holds what the verbs do alike — the missing-target error, the checks and lock ahead of a table write, the `UPDATE` / `DELETE` `OUTPUT` binding with its Msg 334 naming, the remote replay shape, a view's broken ownership chain, and the plan-cache shape gate — so a new target kind or route lands in one place.

What stays per verb is where the verbs differ:

- **Permissions.**
  `INSERT` checks object-grain `INSERT`, `UPDATE` column-grain SELECT then UPDATE, `DELETE` column-grain SELECT then object-grain DELETE, and `MERGE` object-grain SELECT plus each action's permission, each probe-pinned.
- **Order.**
  `INSERT` records its function-body write by the written name ahead of resolving it, parses its hints before resolving, and locks its table before the disabled-index and SET-option checks the other verbs make first; `UPDATE` and `DELETE` resolve before their hints, and refuse a view real can't write through there.
- **`MERGE` and views.**
  A remote target is Msg 5315 before anything a remote write checks of the name, and a view with no base table or carrying any `INSTEAD OF` trigger is matched as its own rows, its triggers taking all of the actions or none (Msg 5316) — so `MERGE` doesn't take `RouteViewWrite`.
- **`DELETE` through a join view** is Msg 4405 whatever it names, where `INSERT` and `UPDATE` write the one base table their columns land in.

## UPDATE / DELETE
- Bare `UPDATE table SET ... [WHERE]` and `DELETE [FROM] table [WHERE]`.
- **Target scan is seek-narrowed** when the single-table WHERE carries an indexable equality / IN / cross-column OR / range (`Selection.SeekMutationTarget`), instead of walking the whole heap — the same per-`Heap` seek cache the SELECT path and FK enforcement use.
  The mutation loop re-runs the full WHERE per row (residual filter) and X-locks only the rows it commits, so it's a pure narrowing; positioned (`WHERE CURRENT OF`) mutations keep the scan.
  See [`indexes.md`](indexes.md#update--delete-target-seeking).
  The multi-table (joined) form's **target** isn't seek-narrowed either — its *other* sources are, and the target's row stream is prefiltered, see [Joined row sources](#joined-row-sources) below; `MERGE` narrows its target via loop inversion (see its section below).
- Multi-table syntax (`UPDATE alias SET ... FROM <sources> [WHERE]`, `DELETE FROM alias FROM <sources> [WHERE]`) — the EF7+ `ExecuteUpdate`/`ExecuteDelete` shape.
  Target identified by leading-identifier match against each source's `FromSource.Qualifier`; missing match → Msg 208, and a table the clause reads twice under aliases neither of which the target names → Msg 8154 (probed 2026-10-01 against SQL Server 2025).
- **Joined UPDATE/DELETE: each unique target row processed exactly once.**
  When the same target matches multiple join tuples, SQL Server uses the *first* matching tuple's RHS for SET.
  The simulator dedupes by `(page, slot)` via a side-channel byte[]→address map.
  LEFT JOIN with no right-side match still surfaces the target (RHS sees NULL).
  The same dedupe carries **UPDATE through a view over a join** — real accepts one whose SET list lands entirely in a single base table, and the join-multiplied target updates once there too — see [`programmable.md`](programmable.md#dml-through-a-join-view).
  A **target that is a view, CTE or derived table** in the FROM clause writes through it to its base table, reading its rows as the view yields them → [`programmable.md`](programmable.md#a-joined-write-through-a-view).
- **A SET assignment target admits one qualifier: the write target as written.**
  The leading name, its alias when it has one, and any schema / database prefix in front of it (`UPDATE t SET dbo.t.v = 5` binds; `UPDATE a SET t.v = 5 FROM t a` does not, the alias having hidden the table name).
  Everything else is **Msg 4104** naming the whole dotted form, ahead of the leaf lookup and whether or not the leaf names a real column; a matching qualifier with an unknown leaf is the ordinary Msg 207.
  A joined UPDATE writes only its target, so a join source's column is Msg 4104 there too, and a MERGE's own alias hides its table name the same way.
  A fifth segment is Msg 4104 with the whole name, from `MultiPartName` itself.
  Probed against SQL Server 2025 (2026-08-05); oracle `DmlTargetQualifierTests`.
- **A `VALUES` cell has no column scope at all**, so every qualified reference in one is Msg 4104 — the insert target's own name included (`INSERT INTO t (id, v) VALUES (t.id, 1)`) — while an unqualified unknown name stays Msg 207.
- **OUTPUT** binds against the target table; the alias form's clause, written ahead of the FROM clause that names the table, binds once the statement has read that clause (`Selection.PreParseMutationFrom`), and a trigger's Msg 334 names the alias (probed 2026-10-01 against SQL Server 2025).
  A joined write's clause also reads the FROM clause's **other sources** through their qualifiers (`OUTPUT u.w, inserted.v`, `u.*`), each written row's value coming from the partner it was written with — the first one the join meets, as the SET values read — and a NULL-extended partner reading NULL; the target's own name or alias is Msg 4104 there, an unqualified name Msg 207 (probed 2026-10-01 against SQL Server 2025, for table, view, join-view, CTE and derived-table targets).
  The leading-table form's clause is written ahead of the FROM clause too, which is read ahead for it the same way; the walk notes each written row's partner tuple only when the clause reads one (`OutputPartnerRows`).
- **Multi-column SET evaluates RHS against pre-update snapshot** — `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` → `(a=100, b=11)`.
  Scalar subquery RHS sees pre-update state.
- **Variables in the SET list** (`@x = expr`, `@x += expr`, `@x = col = expr`) are assigned first for each row, in written order and against the pre-update row, and only then are the columns evaluated, reading the variables as just assigned — so `SET v = @x, @x = @x + 1` writes the incremented value, and `SET @x = v = v + @x` is the running total.
  The compound form gives the column the variable's value; the variable must be declared the column's own type (Msg 425 otherwise), and neither `@x += col = …` nor `@x = @y = …` parses (probed 2026-09-24 against SQL Server 2025).
  It must hold every value the column can, too: a shorter string or binary is Msg 426, naming both lengths in bytes (a MAX column's as 8100), and a `decimal` with fewer digits either side of the point Msg 4187; all three bind as the statement compiles, ending the batch (probed 2026-10-01).
  A table variable qualifying a target (`SET @t.a = …`) is the syntax error at its dot.
- **`SET col.WRITE(expression, @Offset, @Length)`** rewrites part of a `varchar(max)` / `nvarchar(max)` / `varbinary(max)` value (`Parser/Expressions/WriteMutator.cs`, probed 2026-10-01 against SQL Server 2025): the zero-based offset and the length count characters or bytes, a NULL offset appends, a NULL length or one past the end replaces to the end, and a NULL expression cuts the value at the offset.
  A NULL column is Msg 5302, an offset past the end Msg 582, a negative offset or length Msg 583, and a column of any other type Msg 258.
  Real logs only the bytes it replaces; the simulator writes the whole new value.
- **A target the batch hasn't created yet defers the statement's bind**, its SET list's Msg 157 / 4108 included, so the statements before it run (probed 2026-10-01).
  A variable-only entry carries no column name, and `ComputeUpdatedRow` runs the two passes.
- Identity update → Msg 8102.
  Computed update → Msg 271.
  Rowversion update → Msg 272.
  Per-row constraint re-validation: NOT NULL → Msg 515 ("UPDATE fails."); CHECK → Msg 547 ("UPDATE statement"); PK/UNIQUE → Msg 2627 (verbatim "Cannot insert duplicate key" wording even on UPDATE — SQL Server quirk).
  PK/UNIQUE validation runs against the post-update virtual state, so mass-shift on a unique key can false-positive (see Quirks).
- `OUTPUT INSERTED.<col>` (post-update) / `DELETED.<col>` (pre-update).
  UPDATE allows both qualifiers; DELETE rejects `INSERTED.<col>` at parse → Msg 4104.
  **Star expansion** (`INSERTED.*` / `DELETED.*`, plus the MERGE source-alias `<src>.*`) ships via parse-time expansion in `Simulation.Output.cs`: `TryDetectStarReference` peeks for the `<qualifier>.*` token sequence and `AppendStarExpansion` synthesizes one `Reference("qualifier", col)` per column of the target (or per column of the MERGE source alias).
  Expanded names take the underlying column's leaf (probe-confirmed — not the qualified form).
  The MERGE form keeps the per-action NULL-fill semantic for `INSERTED.* / DELETED.*` (DELETED columns are NULL on a WHEN NOT MATCHED INSERT row; INSERTED columns are NULL on a WHEN MATCHED DELETE row).
  Unbound qualifier on `.*` raises Msg 4104, same as `<qualifier>.<col>`.
  The expansion runs before `Expression.Parse`, so the alias-suffix shape (`INSERTED.* AS x`) inherits real SQL Server's Msg 102 rejection naturally — the cursor advances past `*` to either `,` or the end-of-OUTPUT terminator.
  `OUTPUT … INTO @t [(cols)]` ships for INSERT / UPDATE / DELETE / MERGE — see [Table variables](table-variables.md).
  A client `OUTPUT` may follow an `OUTPUT … INTO` in all four verbs (`UPDATE @t SET a = 1 OUTPUT inserted.a INTO @u OUTPUT inserted.a`): the statement writes the target and returns the second clause's rows, `@@ROWCOUNT` counting the rows written, and `$action` and a MERGE's source columns read in both (probed 2026-10-01 against SQL Server 2025).
  Only that order parses: a second `OUTPUT` after a client one reads as its column alias, so the reverse order is Msg 102 at the token after it, a second `INTO` is Msg 156 at the keyword and a third clause Msg 102.
  The client clause is what a triggered target refuses with Msg 334, and a function body refuses the statement with its usual Msg 443.
  The client projection holds the `INTO` one it follows (`OutputProjection`'s `logged`), so every row writes the target before it projects.
  Not modeled yet: real streams the client `OUTPUT` rows a multi-row write produced before the row that raised — `INSERT t OUTPUT inserted.a VALUES (2), (1)` over a key `1` sends the `2` row, then the Msg 2627 — where here the statement's result set is empty, with or without an `OUTPUT … INTO` beside it (probed 2026-10-01 against SQL Server 2025).
  All four route through one `OutputProjection` and one `TryParseOutputIntoTarget`; an INTO target with no client clause after it consumes the rows, so the statement reports as a non-query rather than leaving an empty result set behind.
  For MERGE this is also the only legal way to emit OUTPUT against a table carrying an enabled trigger, since [Msg 334](triggers.md#output-on-a-triggered-target--msg-334) forbids the client-returning form.
- **One projection type backs all four statements.** `Simulation.Output.cs`'s `OutputProjection` resolves `INSERTED` / `DELETED` and — for MERGE — the source alias and `$action`, with a reference to a side the statement doesn't have reading as a typed NULL rather than throwing.
  It previously existed in three near-copies (`OutputProjection` for INSERT, `MutationOutputProjection` for UPDATE / DELETE, and a `MergeOutputProjection` inside `Simulation.Merge.cs`), and the MERGE fork was the one that never grew `outputTarget` — which is why `MERGE … OUTPUT … INTO` didn't work, why the docs claimed it did, and why the Msg 334 gate needed a MERGE-specific branch. All three are gone.
- **`OUTPUT … INTO` against a target with an IDENTITY column** follows real's rules exactly (probe-confirmed matrix).
  The positional (no column list) form fills the target's **non-identity** columns, so the projection is measured against that narrower count: equal succeeds and the identity column generates its own value; fewer is **Msg 213**; more would have to write the identity column and is **Msg 8101**, whose message names the OUTPUT target *schema-qualified* (`'dbo.dest'`, or the bare `'#tmp'` form for a temp table).
  An explicit column list naming the identity column is **Msg 544** — and `SET IDENTITY_INSERT <target> ON` does **not** unlock it.
  Msg 544's slot names the **DML statement's own target table**, not the OUTPUT target that owns the identity column; that is real's behavior and is mirrored verbatim, so the two messages disagree about which table they name.
  A column list that omits the identity column is the accepted spelling.
- **A subquery in a SET expression sees the update target's columns.**
  `UPDATE t SET alias = (SELECT MAX(v) FROM (VALUES (t.name), (t.goes_by)) x(v))` — the shape ORMs emit for GREATEST / LEAST — binds `t`'s columns at parse time via a target-scoped resolver installed around the SET list.
  Runtime already threaded the per-row resolver through `RuntimeContext`, so only the parse-time type resolution was missing.
  The multi-table alias form has no resolved target at that point and keeps the enclosing scope.
- **`OUTPUT … INTO` coerces each value to its destination column's type.**
  The projection's type comes from the source table and need not match the target's — an ORM building a returning buffer with `SELECT TOP 0 CAST(id AS bigint) … INTO #tmp` then `OUTPUT INSERTED.id INTO #tmp` hands an int to a bigint column.
  Storing it raw reached the row encoder's type check as a bare `ArgumentException`; over the TDS wire that aborts the response mid-stream, so the client reports `HY000 "A severe error occurred"` and the connection dies rather than getting any usable error.
  Uncovered columns already coerced their DEFAULT the same way — this closes the covered-column half.
  Each projected type must also be *assignable* to its target column as the statement compiles — an `int` into a `date` is Msg 206 — and the row then meets the target's length rules, its computed columns, a rowversion it carries and its NOT NULL columns as an INSERT's would.
- **`OUTPUT … INTO` refuses some targets outright**, as the statement compiles (probed 2026-10-01 against SQL Server 2025): a view or CTE is **Msg 330**, a table with an enabled trigger **331** (a trigger the same batch disables still counts — it was enabled when the batch compiled), one on either side of an enabled foreign key **332**, and one with an enabled CHECK or a bound rule **333**, the last two naming the constraint or rule found (`RejectRestrictedOutputIntoTarget`).
  A disabled CHECK or foreign key passes.
  A computed target column takes no value: the positional list skips it, and naming it is Msg 271.
- **An `OUTPUT` item** admits no subquery (Msg 10705) and no aggregate (Msg 158), in all four verbs.
- **An `OUTPUT` reference to a computed column** whose definition calls a function real assumes reads data — one that isn't schema-bound, or a schema-bound one that reads a table — is **Msg 4186**, naming `inserted.c` / `deleted.c` in lowercase however written (probed 2026-10-02 against SQL Server 2025); EF Core recognizes it as its computed-column-with-function failure.
- **A client `OUTPUT`'s column nullability** is inferred as a SELECT's is (`InferOutputNullability`, probed 2026-10-01 against SQL Server 2025): `INSERTED.c` / `DELETED.c` take column `c`'s declaration — an identity, a rowversion and an `ISNULL` computed column are NOT NULL — a literal is NOT NULL and arithmetic nullable.
  A MERGE's `INSERTED` reads nullable when a clause deletes, its `DELETED` when one inserts, its source columns when one acts `BY SOURCE` (else as the source projects them), and `$action` never; a joined write's other sources read nullable.
  Nothing a trigger produced reaches it: a target with an enabled trigger refuses a client-bound clause (Msg 334).

### A target the FROM clause never names

The FROM clause needn't introduce a source for the target at all: real binds one it doesn't as an **additional, implicitly cross-joined source**, so `UPDATE u SET id = d.n FROM (SELECT 1 AS n) d` is `u CROSS JOIN d`, and `u`'s own columns stay in scope for the SET list, the WHERE, the OUTPUT clause and any correlated subquery.
Probe-confirmed against SQL Server 2025 (2026-08-05) for a parenthesized derived table alone and joined to a table, two derived tables, `d LEFT JOIN t`, `d CROSS APPLY (…)`, a plain unparenthesized table (`UPDATE u SET id = u2.id FROM u2`), a schema-qualified target, a `#temp` target and both `DELETE t FROM …` / `DELETE FROM t FROM …` spellings.

The cross join multiplies rows **examined**, not rows affected: two target rows against a one-row FROM report two affected, each written once, exactly as a written `FROM u JOIN …` would.

`FindOrAppendMutationTarget` (`Simulation.Update.cs`, shared with the DELETE path) is the seam.
On a miss it appends the target as a `FromSource` built the way an explicitly-written heap source is — same read plan, same row-conflict wrapping — behind a `JoinKind.Cross` `JoinSpec`, and joins it at the **tail** so the written FROM keeps its own leftmost source and join tree.
A leading identifier that named no table at all stays **Msg 208**: an alias the FROM never defined is real's error too.
A FROM source *aliased* as the target's name still wins the match, so `UPDATE u … FROM (SELECT 1 AS n) u` keeps binding to the derived table (real then reports its own missing column; the simulator's derived-table-target rejection is unchanged).

### Joined row sources

`ExecuteJoinedUpdate` / `ExecuteJoinedDelete` enumerate their join tuples through the same `Selection.EnumerateJoinedRows` a SELECT does, and reach it through `Selection.PrepareMutationJoinSources` — the read path's own two row-source passes, minus the reorder:

- **Deferred sources materialize once per enumeration**, exactly as they do for a SELECT and under the same gates (non-APPLY, non-leftmost, query-body-only, and the `NEWID()` volatility rule) — see [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration).
  A derived table / CTE reference / view joined to the target stops re-executing per target row and keys into the O(L + R) hash path instead.
  Measured on WWI's `UPDATE #t … FROM #t t JOIN (<grouped aggregate over Invoices ⋈ InvoiceLines>) d ON …` over **30** target rows: **6,297 ms → 164 ms** (live 64 ms), which is the derived body's own once-through cost plus the 30 writes.
- **A joined `GROUP BY` body is reduced to the join's key set** (`ReduceGroupedBodiesByJoinKeys`, run before the materialization so what materializes is the reduced body) — see [`joins.md`](joins.md#join-key-reduction-of-a-grouped-body) for the legality and the gates.
  The pass reaches the mutation path unchanged: it only ever rewrites a *deferred body's* slot, which the target — a base table the write pipeline addresses row by row — can never be, and the partner side it reads for keys may well be the target, where the pre-statement rows are what the enumeration reads however many times it runs (the Halloween reasoning below).
  The WWI `UPDATE #t … FROM #t t JOIN (<grouped aggregate>) d ON …` over 30 target rows measures **10 ms** (live 63 ms) with the aggregate covering 30 customers; a body the reduction declines pays the full 663-customer aggregate (~160 ms on the same shape).
- **The WHERE narrows every source but the target** (`NarrowMutationJoinSources`), so a joined source carrying an indexable equality seeks before the join runs.
  It is the same pure narrowing it is in a SELECT because the statement re-runs its whole WHERE per join tuple, so a matched conjunct is still the filter it was.
  Every source is gated by the read path's `IsSeekNarrowingTarget` — the leftmost included, unlike the read path's unconditional leftmost attempt, since extending that to a mutation would change which key ranges a SERIALIZABLE reader locks around a write.
- **No reorder.** The written join order stands; the target is identified by slot index.

**The target source enumerates every row, and a prefilter drops the ones the WHERE rejects on the target alone.**
The write pipeline reaches each affected row through an address side-channel keyed by the `byte[]` instances that enumerator yields, and settles its lock / undo bookkeeping per row it touches, so a seek on the target would be a change to the write path rather than to a read.
The scan prefilter ([`indexes.md`](indexes.md#the-scan-prefilter-a-join-source-no-key-can-seek)) isn't: `PrefilterMutationTarget` wraps the stream *after* the address wrapper, so it passes on the instances the wrapper recorded, in the order it read them, and the write path's waits, locks, `TOP` cap and rejudging all happen for qualifying rows only, as before.
What a rejected row stops costing is its drive through the join — an `APPLY` body's execution included — before the residual WHERE turned it away; the measured EF Core shape is on `PrefilterMutationTarget`.
It also stops raising what only its partners raise, which is real's behavior: an `APPLY` body dividing by a rejected row's zero is no Msg 8134 on SQL Server 2025 (probed 2026-10-02), where the unfiltered walk raised it.

**Halloween** needs no separate protection here: the statement collects its whole affected-row set before it writes anything, so a source reading the target table reads the pre-statement rows however many times it runs — which is what makes running it once identical to running it per target row, and is what real does anyway.
Probe-confirmed against SQL Server 2025: `UPDATE t SET v = d.m FROM #t t JOIN (SELECT MAX(v) AS m FROM #t) d ON 1 = 1` over `(10, 20, 30)` leaves every row at `30`, the CTE spelling and the grouped per-key spelling agree, `UPDATE t SET v = u.v FROM #t t JOIN #t u ON u.id = t.id + 1` reads the pre-update partner, and a `NEWID()` inside the joined source draws **once per target row** (five distinct values over five rows), which the per-row re-draw of a merged body's drawn columns is what preserves (see [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration)).

Both passes decline in **skip mode** (an un-taken `IF` / `WHILE` branch): nothing commits there, so they are pure cost — and the materializing execution would run a deferred body on behalf of a statement that never runs, raising where the per-outer-row execution never reached one (an empty target drives no rows at all).
The materialization's one divergence, inherited from the read path, is in [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration): a body that raises is evaluated even when the target is empty and real would never have driven a row into it.

**Not wired yet: DML through a join view** (`Simulation.Update.JoinView.cs` / `Simulation.JoinViewDml.cs`).
Its WHERE names the *view's* output columns and resolves through the per-level chain resolvers, not against the base `FromSource[]` — so the seek's name resolution against a base source could bind a view column name to a same-named base column, which is a correctness question rather than a perf one.
The materialization half is sound there, but the chain's `WITH CHECK OPTION` probe re-enumerates the same sources per affected row, so the seam is two call sites rather than one.

## `TOP (expr) [PERCENT]` on UPDATE / DELETE / INSERT / MERGE

`TOP` caps the number of rows a DML statement affects — SSMS's "Edit Top 200 Rows" commits every cell edit as `UPDATE TOP (200) <t> SET … WHERE <22 concurrency predicates>`.
Parsed by `Selection.ParseDmlTopClause` (a leading-clause helper threaded into `ParseUpdate` / `ParseDelete` / `ParseInsert`) and applied post-collection by `Simulation.ApplyDmlTopCap`, which trims the affected/deleted/source-row list to the cap `Selection.ResolveDmlTopCap` computes.
Placement: after the verb, before the target — `UPDATE TOP (n) t …`, `DELETE TOP (n) [FROM] t …`, `INSERT TOP (n) [INTO] t …`, `MERGE TOP (n) [INTO] t …`.
Which rows the cap keeps is arbitrary scan order (tests assert only the COUNT).

Probe-confirmed semantics (SQL Server 2025):

- **Parentheses mandatory.**
  The legacy bare form (`UPDATE TOP 2 …`) is SELECT-only; on DML it raises **Msg 102** near the value.
  `ParseDmlTopClause` requires `(` after `TOP` and raises `SyntaxErrorNear` (Msg 102) otherwise.
- **Value shapes.**
  Integer literal, arithmetic expression, `@variable`, and a parenthesized scalar subquery all resolve (the whole parenthesized expression funnels through `Expression.Parse`).
  Bigint-range values are accepted and clamp to the row count.
- **Non-PERCENT validation.**
  The value must be a non-negative integer.
  Non-integer / decimal / `NULL` → **Msg 1060** ("… must be an integer.", reused `TopFetchRequiresInteger`); negative → **Msg 127** ("A TOP N or FETCH rowcount value may not be negative.", `TopRowCountMustNotBeNegative`).
  `TOP (0)` affects zero rows.
- **PERCENT.**
  The value is numeric (int / decimal / float) and must fall in `[0, 100]` — out of range → **Msg 1031** ("Percent values must be between 0 and 100.", `TopPercentOutOfRange`); `NULL` → **Msg 1014** ("A TOP or FETCH clause contains an invalid value.", `TopClauseInvalidValue`, distinct from the non-percent NULL's Msg 1060).
  The cap is `ceil(candidateCount * pct / 100)` — probe-confirmed ceiling: `50 PERCENT` of 3 rows → 2, `33.3 PERCENT` of 10 → 4, `2 PERCENT` of 10 → 1, `0 PERCENT` → 0, `100 PERCENT` → all.
- **Interactions.**
  `OUTPUT` emits exactly the capped set; `@@ROWCOUNT` reflects the cap.
  `UPDATE TOP … ORDER BY` is Msg 156 on the reference (ORDER BY isn't part of the DML grammar) — the simulator raises Msg 102 at the trailing `ORDER` for the same effect (no ORDER BY acceptance on DML).
- **Validation timing.**
  A written constant is judged while compiling, so `TOP (-1)`, `TOP (1.5)` or `TOP (101) PERCENT` reports alone, with no Msg 3621 (probed 2026-09-28 against SQL Server 2025); `ParseDmlTopClause` checks it.
  A variable or subquery is read as the statement runs, and its error reports at the statement's first line with a Msg 3621 after it — real attributes that Msg 3621 to line 1, which the simulator doesn't reproduce.
  `ResolveDmlTopCap` is always called when a limit is present (even at zero candidates) so the value errors fire regardless of match count.
- **`TOP (1) WITH TIES`** is Msg 156 on both engines; real follows it with Msg 319, the simulator doesn't.
- **INSERT TOP** caps the inserted-row count across `VALUES` (multiple tuples), `SELECT`, and `EXEC` sources — applied to the buffered `sourceRows` list in `ProcessHeapInsert` (and the view / INSTEAD OF paths).
- **MERGE TOP** caps the actions taken, not the joined rows: a row every WHEN clause declines costs nothing, and PERCENT is a share of the actions the statement would take.
  Which actions come first is the plan's choice on real — over the same four-row target a `TOP (1)` deleted an unmatched target row where `TOP (2)` inserted and updated instead (probed 2026-09-28 against SQL Server 2025) — so the simulator takes them in the order its OUTPUT lists them (see [Execution](#execution)), the one real's source-driven plan follows; for two small heaps real drove from the smaller side and took the target's first row instead.
  Nothing past the cap is evaluated, so a later row's Msg 8672 or conversion error never raises, as on real.
- **`SET ROWCOUNT n` caps a DML statement the same way**, and the two compose as a minimum — `ApplyDmlTopCap` reads the session value beside the written `TOP`, so `INSERT` / `UPDATE` / `DELETE` all take it at one seam and `MERGE` at its own.
  See [`query.md`](query.md#set-rowcount-n).

## INSERT value counts

### The positional column list

An INSERT that writes no column list of its own is measured against a **positional list**: every column of the target *except* its identity and computed ones.
Identity drops out whatever `IDENTITY_INSERT` says, which is why the ON case can never succeed without a column list — a matching count leaves the identity unsupplied (**Msg 545**) and one value more reports **Msg 8101**.

`rowversion` and `GENERATED ALWAYS AS ROW START | END` columns **stay in** the list.
They hold a position that has to be filled with the `DEFAULT` keyword, so against `t(a int, ts rowversion, b int)` the intuitive `INSERT INTO t VALUES (1, 2)` is Msg 213 rather than a two-column write, and `VALUES (1, DEFAULT, 2)` is the accepted form.
A column carrying a `DEFAULT` *clause* likewise keeps its position — a default makes a column omissible from a column list, never from the positional one.

A **view** target is measured against its own projection the same way, its base table's identity and computed columns dropping out and a projected `rowversion` keeping its slot.
A view projecting a **derived** column (`x + 1 AS d`) has no position to fill, so the column-list-less form is **Msg 4406** whatever the value count.

### Which diagnostic a mismatch reports

| shape | too many values | too few values |
| --- | --- | --- |
| no column list, `VALUES` / `SELECT` | **Msg 8101** with an identity column, else **Msg 213** | **Msg 213** |
| explicit column list, `VALUES` | **Msg 110** | **Msg 109** |
| explicit column list, `SELECT` | **Msg 121** | **Msg 120** |
| any shape, `EXEC` | **Msg 213** State 7 | **Msg 213** State 7 |

Msg 213 is class 16 State 1; Msg 109 / 110 / 120 / 121 are class **15**.
The no-column-list surplus reports the identity diagnostic for *any* excess, not merely one value's worth — real reads the extra value as an attempt to supply the identity, and names the table bare (`'ident'`) where the `OUTPUT … INTO` form of Msg 8101 names it schema-qualified.

Multi-row `VALUES` whose tuples disagree with **each other** is **Msg 10709**, and it outranks the table's own arity rules: ragged tuples carry no single width to measure, so `(a) VALUES (1), (2, 3)` reports 10709 rather than 110.
Tuples that agree with each other but not with the destination take the ordinary rules — `VALUES (1, 2), (3, 4)` into a one-column list is Msg 110.

### Check order

Arity settles first, then the auto-generated-column gate, then the identity gates — probe-confirmed at each step:

1. **Arity** (the table above), which is why `(id, a) VALUES (1, 2, 3)` is Msg 110 and not the Msg 544 its identity reference would otherwise earn.
2. **Msg 273** (`rowversion`) / **Msg 13536** (`GENERATED ALWAYS`) for a position holding anything but `DEFAULT`.
   Naming either column in a column list is legal — the escape hatch each message's own text points at — so the check is per cell, and one tuple of a multi-row constructor supplying a real value is enough to raise.
   A `SELECT` / `EXEC` source carries no `DEFAULT` keyword, so merely reaching such a column refuses.
3. **Msg 339** (`DEFAULT` / NULL as an identity value), then **Msg 544** / **Msg 545** (the `IDENTITY_INSERT` gates).

A computed column is refused for merely being named, whatever its cell holds — **Msg 271** even for `DEFAULT` — so it never reaches the per-cell scan.

### Timing

The whole family is settled while the statement **compiles**, not when it runs: it fires from an untaken `IF` branch, and it aborts a `CREATE PROCEDURE` whose body carries it, leaving the module uncreated (see [CREATE-time body binding](programmable.md)).
`RejectValuesArityMismatch` therefore reads the *parsed* tuples rather than the evaluated rows and runs regardless of skip state.

Both engines compile a whole batch before running any of it, so a bad-arity statement stops its predecessors from running too ([batch compilation](control-flow.md#batch-compilation)).

**Divergence:** real reports a statement offending several rules at once as a multi-error response (an unknown column name *and* a bad count come back as Msg 207 then Msg 110; a ragged constructor into a rowversion table as Msg 273 then Msg 10709) — the simulator raises the leading error alone.

## Writing a value into a column

Every write — INSERT, UPDATE, MERGE, `OUTPUT … INTO` and the `ALTER COLUMN` rewrite — meets the column through `Simulation.Coerce.EnforceMaxLength` then the type coercion (probed 2026-10-01 against SQL Server 2025):

- A value whose excess beyond the declared length is only **trailing spaces**, or trailing zero bytes of a binary, is cut to fit without an error: `'abc   '` into `varchar(3)` stores `abc`, `0x010000` into `varbinary(2)` stores `0x0100`.
- A **number, date or `uniqueidentifier`** written to a string column converts as a `CAST` to the column's declared length would: an `int` too wide for a `varchar(2)` stores `*`, a `date` into `varchar(5)` is cut to `2020-`, and a `bigint`, `decimal`, `money`, `float` or `uniqueidentifier` raises the cast's own error (Msg 8115, 234, 232, 8170).
- Msg 2628 reports a binary's truncated value **empty** (`Truncated value: ''`), a parameter's included, as it does every value an `ALTER COLUMN` cuts.
- The out-of-range conversions of a written string (Msg 242, 244, 248) and a date overflow (Msg 517) end the statement with Msg 3621 after them.
- A row whose value fails to convert has **drawn its identity value** already, so the next row's skips it — the same gap a row a constraint refuses leaves.

## A multi-row `VALUES` list's column types

A multi-row `INSERT … VALUES` list is a table constructor, so each column takes the type its rows unify to — the `UNION ALL` rule — before converting to the target (probed 2026-09-25 against SQL Server 2025).
`VALUES (1), ('a')` into a `varchar` column is Msg 245 converting `'a'` to `int`, `VALUES (1.50), ('2')` stores `2.00`, and a `sql_variant` target stores `int` for both rows of `VALUES (1), ('2')`; a single row converts straight to the target, and a `DEFAULT` cell or a bare `NULL` takes no part (`Simulation.UnifyValueRows`).

## `DEFAULT` as a `VALUES` element

`INSERT INTO t (a, b) VALUES (1, DEFAULT)` — the `DEFAULT` keyword in an individual value cell, distinct from the whole-row `DEFAULT VALUES` form below.
Legal only inside `INSERT … VALUES`, so `ParseValuesTuples` takes an `allowDefault` flag and only the INSERT-VALUES path passes `true`.

The VALUES source is parsed **before** identity diagnostics run (`Simulation.Insert.cs`), because the per-cell DEFAULT keywords have to be visible to them:

- an **identity** column receiving `DEFAULT` raises **Msg 339** ("DEFAULT or NULL are not allowed as explicit identity values."), and it fires *before* the `IDENTITY_INSERT` gate — probe-confirmed to raise with `IDENTITY_INSERT` both ON and OFF.
  So does a written constant that folds to NULL — `NULL`, `CAST(NULL AS int)` — in a `VALUES` cell, a `SELECT` source's projection or a MERGE insert, while a NULL only a run produces (a variable) is Msg 515 (probed 2026-10-01 against SQL Server 2025).
- a **non-identity** DEFAULT cell resolves to the column's default in the shared row-encode loop, taking the same path an omitted column would.

Django's `db_default` field option emits this shape, which is what motivated it.

`UPDATE … SET col = DEFAULT` and a `MERGE` update action's `SET col = DEFAULT` take the same default, or NULL where there is none, one evaluation per row (`ColumnDefaultValue`, probed 2026-09-26 against SQL Server 2025).
The keyword stands alone: `DEFAULT + 1` and `(DEFAULT)` are syntax errors, a variable target is Msg 156, and a compound operator is Msg 10708; the column's own refusals (identity, computed, rowversion) and the write's (NULL into NOT NULL, truncation) apply as to any value.

## `INSERT INTO t DEFAULT VALUES`
Inserts a single row with every column defaulted.
`ProcessHeapInsert` clears the destination-column list and feeds one empty source tuple, so every column flows through the default / identity-allocation / implicit-NULL path — a NOT NULL column with no default hits the same constraint error an explicit all-defaults insert would (probe-confirmed).

## `rowversion` (legacy synonym `timestamp`)
8-byte big-endian database-scoped monotonic counter; advances on every INSERT into a rowversion-bearing table and every UPDATE affecting one.
Storage type name surfaces as `timestamp` in `information_schema` regardless of declaration.
Explicit insert → Msg 273; explicit update → Msg 272; second column on a table → Msg 2738.
A written constant NULL in a `VALUES` cell reads as `DEFAULT` and draws a value, where a NULL variable is Msg 273 (probed 2026-10-01 against SQL Server 2025).
A column written as just `timestamp` (bracketed or not) is one, named timestamp — in a table, a table variable, a table type and a function's return table (probed 2026-09-30 against SQL Server 2025).
A variable takes a value from an integer, an exact number or a binary; a string or `datetime` is Msg 257 and every other class Msg 206 (`SqlType.PairRules.cs`'s assign grid).
The column keeps its position in an INSERT's positional column list and accepts the `DEFAULT` keyword there (and in a column list naming it) — see [INSERT value counts](#insert-value-counts).
Outbound CAST: `varbinary(N)`/`binary(N)` copy 8 bytes; `bigint` reads big-endian.
`Promote(RowVersion, Varbinary) → Varbinary` so EF's `WHERE [rv] = @originalRv` parameter works directly.
EF `[Timestamp]` SaveChanges round-trips end-to-end.

**`MIN_ACTIVE_ROWVERSION()`** returns the rowversion counter's current next-allocated value as `binary(8)` big-endian.
Real SQL Server returns the minimum *active transaction's* lowest rowversion, which over-approximates to "current next-to-allocate" when no transactions are open; the simulator returns the current next-to-allocate value unconditionally — semantically equivalent for the common consumer (incremental-sync watermarks) since rowversion writes within an open transaction wouldn't be visible to readers anyway.

**`@@DBTS`** returns the last-allocated rowversion value as `varbinary(8)` big-endian (the type real describes it with, probed 2026-10-02) — the value `MIN_ACTIVE_ROWVERSION` reports as next-allocated, minus one — and neither read advances the counter.
A new database's counter stands at 2000, so `@@DBTS` reads `0x00000000000007D0` and the first rowversion is 2001 (probed 2026-09-24 against SQL Server 2025).
Used by tooling watermarking via the "current high water" pattern (sync delta from `WHERE rv > @lastDbts`).

## Identity helpers (`@@IDENTITY` / `SCOPE_IDENTITY` / `IDENT_CURRENT` / `IDENT_INCR` / `IDENT_SEED`)
Per-column identity allocation routes through `HeapTable.IdentityState`.
`@@IDENTITY` reads the session's `SimulatedDbConnection.LastIdentity` and `SCOPE_IDENTITY()` the current scope's `ScopeIdentity`, both set by every INSERT — to its last identity value, or NULL when it produced none — and `IDENT_CURRENT(name)` reads the named table's last-allocated value directly.
A procedure, trigger, function or dynamic-SQL body is a scope of its own (`IdentityScope`): it starts at NULL and its caller reads its own `SCOPE_IDENTITY()` again afterwards, while `@@IDENTITY` reads the body's — save that a trigger producing no identity value leaves its firing statement's in place, and a function's INSERT touches neither (probed 2026-09-28 against SQL Server 2025).
`IDENT_INCR(name)` / `IDENT_SEED(name)` (`Parser/Expressions/IdentSeedIncrement.cs`) return the declared step / start of the named table's identity column, or NULL when the table lacks one or the name doesn't resolve.
All three name-arg scalars accept a 1-/2-/3-part dotted runtime string via the same `TryParseObjectName` helper `OBJECT_ID` uses.
Result type is `numeric(38, 0)` matching real SQL Server's projection (covers tinyint/smallint/int/bigint columns uniformly), named `numeric` by `SQL_VARIANT_PROPERTY` (probed 2026-10-01).
The counter is an `Int128`, since a `decimal(38, 0)` identity's seed, increment and values run past `bigint` — real generates, reseeds and reports them across the whole 38-digit range (probed 2026-09-28 against SQL Server 2025).

`SET IDENTITY_INSERT <table> ON | OFF` (`Simulation.Set.cs`) sets / clears `SimulatedDbConnection.IdentityInsertTable`.
ON validates the target: a table with no identity column raises **Msg 8106** (`TableHasNoIdentityForSet`, "Table 't' does not have the identity property. Cannot perform SET operation."); a second table while one is already held raises **Msg 8107** (`IdentityInsertAlreadyOn`), naming the held one three-part — both probe-confirmed against SQL Server 2025.
A view is Msg 8105 and a missing object Msg 1088, each named as written (probed 2026-10-01).

`DBCC CHECKIDENT` (`Simulation.CheckIdent.cs`) reports and reseeds, and a reseed rolls back with its transaction though a generated value never does.
The one rule that isn't a plain assignment: a table that hasn't generated a value since it was created or truncated takes the reseed value *itself* on its next insert, where one that has takes the value after it; the pending value shows through `IDENT_CURRENT` but not through CHECKIDENT's own report, which still says `NULL` (probed 2026-09-24).

**The identity values a failing `INSERT` uses up** follow where real's plan draws them: per row, as the row reaches the insert, ahead of computing the row's own select-list values, its defaults and its conversions into the columns — and a drawn value is never given back (probed 2026-10-04 against SQL Server 2025).
So a failing statement uses up one value for each row its source produced before the failure, plus one for the failing row when its error came from that row's own values, and none for it when the error came from an operator real runs ahead of every draw: a filter, a sort key, `DISTINCT`, an aggregate, a multi-row `VALUES` (a constant scan, computed whole before its first row goes out) or a set operation over constants alone.
`VALUES (1/0)` uses up one where `VALUES (1), (1/0)` uses none; `SELECT 10 / (k - 3) FROM src` over `k` = 1..5 uses up three, through a derived table, a CTE, an `APPLY`, a join, a `CASE`, a scalar subquery, an `ORDER BY k` or an `OUTPUT` alike; `WHERE 10 / (3 - k) > 0` uses up the two rows that passed; `DEFAULT VALUES` over a failing default uses one; an `INSTEAD OF` trigger draws none.
The source is still read whole before the first write here, so the simulator counts what real would have drawn: the rows read before the failure, and the failing row when `SimulatedSqlException.RaisedInRowProjection` says its error came from a pipelined select list — set where a streaming, sorted or FROM-less projection raises and a single-row `VALUES` row, and cleared by the operators above — through `UseUpIdentityValues` (`Simulation.Insert.cs`).

**Not modeled yet**:

- Under `IDENTITY_INSERT ON`, a failing `INSERT` still moves `IDENT_CURRENT` to the explicit values of the rows up to and including the failing one, whatever the failing cell's position — `VALUES (10, 1), (11, 1/0)` leaves it at 11, `(v, id) VALUES (1/0, 10)` at 10, and `SELECT k + 10, 10 / (k - 3) FROM src` at 13 — where the simulator leaves it unmoved (probed 2026-10-04 against SQL Server 2025).
- A failing `MERGE … WHEN NOT MATCHED THEN INSERT` uses up an identity value on real (`USING (SELECT 1/0 AS x) s ON 1 = 0` leaves the next row at 2) and none here; so does a failing select list over a window function (`ROW_NUMBER() OVER (ORDER BY k) * 10 / (k - 3)` uses up three), which the simulator computes ahead of the rows it yields.
- `INSERT TOP (n) … SELECT` reads only the first n rows of its source on real, so a later row's error never raises; the simulator reads the source whole first (probed 2026-10-04 against SQL Server 2025).
- A FROM-less source's constant converts into its column as real's plan starts here, so `INSERT t (c) SELECT 'abcdef' WHERE 1 = 0` into a `varchar(2)` is Msg 2628 where real inserts nothing and raises nothing (probed 2026-10-04 against SQL Server 2025).
- **`$IDENTITY`**, the identity-column pseudo-reference (`SELECT $identity FROM t`, NOT NULL on real), is Msg 156 here (probed 2026-10-01 against SQL Server 2025).

## `@@ROWCOUNT` / `ROWCOUNT_BIG()`
Both expose the row count of the most-recently-completed statement on the session via `SimulatedDbConnection.LastStatementRowCount`.
`@@ROWCOUNT` projects as `int`; `ROWCOUNT_BIG()` (`Parser/Expressions/TransactionScalarFunctions.cs`) is its `bigint` sibling — same source, wider projection.
Same set + reset rules: every DML statement updates the count; control-flow statements (IF / WHILE / SET / DECLARE) leave it unchanged on the failure path but set it to the result on success; SELECT inside a `set @v = (select ...)` reports the inner-SELECT's affected row count.
An `IF`'s condition resets it to 0 for the branch it chooses, whatever the condition queried (probed 2026-10-01 against SQL Server 2025).

## The parse / execute split
`INSERT … VALUES` and the single-table `UPDATE` and `DELETE` build a plan (`InsertPlan` / `UpdatePlan` / `DeletePlan`) at the point their last token is consumed, and run it through an execution half (`RunInsertValues` / `RunUpdate` / `RunDelete`) that reads no tokens — on every execution, so the plan cache's replay of one ([`plan-cache.md`](plan-cache.md#dml-statement-plans)) runs the same code a fresh parse does.
The split sits where the interleaved statement already stopped reading: `UPDATE` and `DELETE` start their execution with the permission checks and the SERIALIZABLE fence, and `INSERT` with evaluating the tuples, ahead of the checks real makes once the source is read (auto-generated columns, identity, ragged tuples), so no error changes place.
Two session reads moved across it — `IDENTITY_INSERT` and whether an `INSTEAD OF INSERT` trigger is enabled are read as the `INSERT` runs rather than as it parses — and neither raises.
An `INSERT` reading a `SELECT`, `EXEC` or `DEFAULT VALUES` source reads it as it parses and then writes through the same `InsertRows`.

## INSERT … SELECT
`INSERT [INTO] target [(cols)] SELECT …` accepts the full Selection grammar — WHERE/JOIN/GROUP BY/aggregates/ORDER BY/TOP/OFFSET-FETCH/UNION/INTERSECT/EXCEPT all work source-side.

Source-kind dispatch after the OUTPUT-clause parse: `Values` token → existing tuple-parsing path; `Select` token → `Selection.Parse(…).Execute()`; `(` → the same SELECT path, parenthesized.
Both funnel into one shared per-row encode loop (defaults / identity / rowversion / computed / constraints / OUTPUT).

**A parenthesized source** — `INSERT INTO t (cols) (SELECT …)`, nesting to any depth — is accepted, and the query inside is parsed at the open-paren count so the closing `)` reads as its terminator rather than a stray token.
Only the parentheses wrapping the whole source count: in `INSERT INTO t (a) (SELECT 1) UNION ALL (SELECT 2)` they open the first branch of an ordinary source (probed 2026-10-01 against SQL Server 2025).
Those parens are only reachable once an explicit column list has been consumed: with no column list the leading `(` is read as the start of one, and the `SELECT` inside it is Msg 102 — which is what real reports for `INSERT INTO t (SELECT …)` too, from the same ambiguity.

**The source query may not carry an `ORDER BY`** — real refuses it as **Msg 156** on the keyword, and refuses it even with the `TOP` that licenses one in a derived table, so this is stricter than the [Msg 1033 rule](ctes.md) the nested constructs take.
A set-operator source is refused the same way.
The restriction belongs to that one query: a derived table or a subquery written *inside* the source keeps the ordinary rules and may order with its own `TOP`.
A `FOR XML` / `FOR JSON` clause on it is the same Msg 156, on the `FOR` (probed 2026-09-23), where the unparenthesized source reports the write-statement refusal instead.
Both follow from the query's `QueryPosition.ParenthesizedInsertSource`, which a nested query inside it doesn't inherit.

**Full buffering**: source materializes to `List<SqlValue[]>` before any destination write — makes self-insert (`INSERT t SELECT … FROM t`) safe.

Projection-count mismatch fires at parse time, and which error depends on whether the statement wrote a column list: with one, too few SELECT columns → Msg 120 St 1 Cls 15 and too many → Msg 121; without one, either direction → Msg 213 (or Msg 8101 for a surplus against a table carrying an identity).
Full rules in [INSERT value counts](#insert-value-counts).
Empty source → silent success, rows-affected 0.
Mid-source constraint violations trigger statement-level rollback.
EF doesn't emit `INSERT…SELECT` from SaveChanges; reachable from raw SQL and bulk-copy patterns.

## INSERT … EXEC
`INSERT [INTO] target [(cols)] EXEC[UTE] <proc | (dynamic-sql)> [args]` appends the result sets the executed code yields into the target — the third source-kind arm alongside `VALUES` / `SELECT` (`Simulation.Insert.cs:ExecuteExecSource`).
SSMS's server-properties query relies on it (`insert #SVer exec master.dbo.xp_msver`).
The EXEC clause runs through the shared EXEC machinery (`ParseExec` — stored-proc call or `EXEC('…')` dynamic batch), so proc-arg binding, dynamic-SQL variable isolation, and system-proc dispatch all behave identically to a standalone EXEC.
Table-variable and updatable-view targets work (both share `ProcessHeapInsert`).

Every yielded result set is decoded row-by-row (`RowDecoder.DecodeRow`) and buffered into the same `List<SqlValue[]>` the VALUES / SELECT arms produce, then funnels into the shared per-row encode loop — so defaults / identity / rowversion / computed columns / constraints / triggers all behave exactly as they would for `INSERT … SELECT` of the same rows.
Probe-confirmed semantics (SQL Server 2025):

- **Multiple result sets append all rows** (`exec('select 5; select 6')` lands both), and `@@ROWCOUNT` is the **total** rows inserted across every result set.
- **A procedure yielding no result set** (pure-DML body) inserts 0 rows and succeeds — non-tabular outcomes (`SimulatedNonQuery`) are skipped during the drain.
- **Per-result-set column count** must match the target's column list — a mismatch (either direction) raises **Msg 213 St 7** (`InsertExecColumnCountMismatch`) — distinct from the SELECT arm's Msg 120/121, and distinct from OUTPUT INTO's Msg 213 St 1.
  Validated during the drain, before any heap write; real attributes it to the executed procedure (`Procedure` = its name), which the simulator leaves blank (probed 2026-10-01).
- **Uncoercible values** surface the shared per-row coercion error (the simulator's Msg 245 conversion path — real SQL Server raises Msg 8114 for a dynamic value; the simulator's INSERT coercion path is Msg 245 for both INSERT…SELECT and INSERT…EXEC, a divergence).
- **Each `SELECT` the executed body runs meets the target columns' one-way assignment rule** (the `Assign` grid, [`arithmetic.md`](arithmetic.md#type-pair-legality)) as it compiles — over no rows too, at the `SELECT`'s own line and procedure (Msg 257 state 3, Msg 206 state 2) — save a column that is a constant `NULL`, typed or not; the error ends the INSERT, rows of earlier result sets included, but not the caller's batch (probed 2026-09-28).
  The INSERT doesn't count as failed, though: its DONE carries no count, `@@ERROR` reads 0 after it, and every batch between it and the refusal ends with it (`SimulatedSqlException.EndsInsertExec`, probed 2026-10-01).
  `SimulatedDbConnection.InsertExecTargetTypes` carries the target types into the body's dispatch loop (`Simulation.RequireInsertExecAssignable`).
- **The executed body runs on past an error that ends only its statement**, as any called batch does ([`control-flow.md`](control-flow.md#procedure-and-dynamic-sql-bodies)): the error and the body's later messages go out ahead of the INSERT's outcome, every later result set — a nested procedure's included — is inserted, and the INSERT succeeds, its count and `@@ROWCOUNT` taking every row and `@@ERROR` reading 0 (probed 2026-10-01).
  An error that ends the executed batch — a missing object it names — still leaves the INSERT writing the rows returned before it, in a transaction too, after which the INSERT's DONE carries no count and `@@ERROR` reads the error.
  An open `TRY` around the statement catches the body's first error instead, and nothing is inserted.
- **Nested INSERT…EXEC** (the executed proc / dynamic batch itself contains an `INSERT … EXEC`) raises **Msg 8164 St 1** "An INSERT EXEC statement cannot be nested." as the inner statement runs, ending only it: the body goes on and its later result sets are inserted (probed 2026-10-01 against SQL Server 2025).
  Guarded by `SimulatedDbConnection.InsertExecActive`, set while the outer drain runs, handed back as it was found (so a body's compile walk doesn't clear it) and checked at the inner INSERT…EXEC entry, which reads its EXEC clause through unrun before raising.
- **OUTPUT clause combined with INSERT…EXEC** raises **Msg 483 St 2** "The OUTPUT clause cannot be used in an INSERT...EXEC statement." — a structural check that fires regardless of skip state, before the source dispatch, and ends the batch.
- **Not modeled yet**: an error a row raises as the executed body's rows land — a truncation, which real reports as the legacy Msg 8152 at state 14 attributed to the procedure — and a body's `ROLLBACK`, real's Msg 3915, come out here as the INSERT's own Msg 2628 and Msg 266 (probed 2026-10-01 against SQL Server 2025).
- **`WITH RESULT SETS` on the EXEC source** raises **Msg 102** — real refuses the clause here, and reports the token one late (`'SETS'`, not `'WITH'`), which the simulator mirrors by consuming both words before raising.
  `WITH RECOMPILE` stays accepted.
  The clause elsewhere is in [`programmable.md`](programmable.md#execute--with-result-sets).

Skip-mode (un-taken IF branch) parses the EXEC clause for cursor advance but `ParseExec` self-suppresses the invocation, so the drain sees no result sets and no rows land.

## `SELECT … INTO target`
Creates a destination table from the projection's inferred schema, then copies rows in.
Target routes by `#`-prefix: `#foo` lands in the per-connection `TempTables` dict (same as `CREATE TABLE #foo`); regular names land in the current database's `HeapTables`.
Probe-confirmed schema-inference rules:

- **Nullability**: the destination column's declaration comes from the same `Expression.ResultIsNullable` inference that drives the wire's COLMETADATA `fNullable` flag — real derives both identically, so the two surfaces agree cell for cell.
  The rule set (structural rules, the idiosyncratic per-built-in table, the operator split between arithmetic and concatenation, and the CASE family's constant fold) is documented on `Expression.ResultIsNullable` and summarized in [`tds-endpoint.md`](tds-endpoint.md); `ResultNullabilityTests` is the exhaustive matrix.
  Highlights for a staging table: a direct ref preserves the source column's nullability, `ISNULL(x, y)` is non-null when *either* argument is (asymmetric with `COALESCE`, which needs all of them), integer arithmetic / `CAST` / aggregates including `COUNT` project nullable, and a `varchar` column concatenated with another non-null one projects NOT NULL where the same shape over two `int`s does not.
- **Identity propagation**: only when the projection is a *direct column ref* (a `Reference`, possibly wrapped in `NamedExpression` for `AS alias`) AND the FROM clause is exactly one source with a `BackingTable` (a real heap, not a derived table / CTE / OPENJSON) AND no joins.
  WHERE/TOP/ORDER BY preserve.
  Any join, set-op, expression wrapping, or CTE drops it.
  Destination's `IdentityState` starts fresh with the source's seed+increment and tracks the copied values via `ObserveExplicit`.
- **Implementation**: `Selection.IntoTarget` + `Selection.DestColumnSchema` (a `HeapColumn[]`) are captured at parse time inside `ParseInner` and propagated through `CombineSetOps` / `ApplyTopLevelOrderBy`.
  `Simulation.SelectInto.cs:ExecuteSelectInto` creates the heap table, runs the Selection, encodes each row through `RowEncoder.EncodeRow`, appends to the dest's heap, and tracks the active transaction's undo log so a `ROLLBACK` unwinds both the table creation (for temp tables) and the row writes.
- **The copy reads the result set's values, not its bytes.**
  A FROM-bearing `SELECT` projects `SqlValue[]` rows; the copy reads `SimulatedSqlResultSet.RowValues`, taking those projected rows directly, and decodes only for the producers that genuinely emit encoded rows (a set operation, a view body, `OPENJSON`).
  Routing through `RowBytes` would encode each projected row into a page image only to decode it straight back for values the projection had already computed.
  The two are answer-for-answer identical (mutation-checked: putting the round trip back passes every test), and it is worth about a third of the statement — on a 73k-row joined copy, 164 MB down to 91 MB allocated.
  The reader path reaches the same rows the same way, through the cursor rather than through `RowValues` — see [`data-reader.md`](data-reader.md#the-row-form-the-reader-reads), which is also where the storage-form narrowing the encode round trip carries is described.
- **Schema rules + validation** live in `Selection.SelectInto.cs:ComputeIntoDestSchema`.
  Nullability uses `Expression.ResultIsNullable` (a virtual whose default is `true`, overridden per expression kind), threaded a `NullabilityContext` carrying the parsing batch plus the column nullability and column type resolvers — the type resolver is what tells string `+` from arithmetic `+`, and the batch is what evaluates the CASE family's folded conditions.
  Identity uses `UnwrapDirectRef` to drill through `NamedExpression` layers.
- **Errors**: unnamed projection → **Msg 1038 Cl 15 St 5** (`SelectIntoMissingColumnName`); duplicate column name in projection → **Msg 2705 Cl 16 St 3** (`DuplicateColumnInSelectInto`, names the target table); target already exists → **Msg 2714** (reused factory); `##` global target → `NotSupportedException`.
- **INTO + UNION**: real SQL Server allows `SELECT … INTO #t FROM a UNION ALL SELECT … FROM b` (INTO on first branch).
  The simulator parses this, propagates `IntoTarget` from the left branch through `CombineSetOps`, and strips identity on the combined dest schema.
  A right branch carrying its own INTO → Msg 156 (`Incorrect syntax near the keyword 'into'.`).
- **INTO without FROM** works (`SELECT 1 AS x INTO #t`) — synthesized-row path threads `IntoTarget` through.
  **Not modeled yet**: a projection error there (`SELECT 1 / 0 AS a INTO #t` in a `TRY`) sends a DONE counting 0 on real, as a failed write does; here the FROM-less projection fails while it parses, before the write begins, so no count goes out (probed 2026-10-02 against SQL Server 2025).
- **The `IDENTITY(type [, seed, increment])` function** makes the destination's identity column (`Parser/Expressions/IdentityFunction.cs`).
  Real takes it only as a whole select-list item of a statement-level query, with an alias (`AS i`, a bare alias or `i = IDENTITY(…)`); anywhere else the keyword is a syntax error, and a query without `INTO` is Msg 177 — an `INSERT … SELECT` source included.
  The projection carries a typed NULL, which the copy replaces with the column's next value in the rows' order, so `ORDER BY` numbers them.
  Seed and increment follow a column declaration's rules (`Parser/IdentitySpec.cs`), the type error is Msg 2749 at state 1 naming the type, a second call is Msg 8109, a column inheriting identity beside it Msg 8108, and a set operator Msg 1057 (probed 2026-09-24 against SQL Server 2025).
- **Quirk**: CTE-wrapped single-heap source drops identity and nullability — the simulator's CTE bindings synthesize `HeapColumn` entries with `nullable: true` and no identity, so the analyzer can't peer through.
  Real SQL Server propagates both.
  Fix would require propagating column metadata through CTE bindings; future bundle.

## MERGE

`MERGE [INTO] target [WITH (hints)] [AS alias] USING <source> [AS alias] [(cols)] [WITH (hints)] ON predicate <when-clause>+ [OUTPUT …];` where `<source>` is one of:

- `(VALUES …)` — parenthesized literal tuples; alias is required.
- `(SELECT …)` or a set-op chain — parenthesized query; alias is required.
  A CTE prefix inside the parens is **Msg 156** (`Incorrect syntax near the keyword 'with'.`), matching real — the prefix belongs ahead of the MERGE (`WITH c AS (…) MERGE … USING c`), which ships.
- bare-table / view / `#temp` / `@tablevar` / `schema.table` reference — alias is optional and defaults to the source's leaf name.
  Optional `WITH (hint [, …])` table hints sit alias-then-hint (same placement as FROM source); the trailing column-rename `(c1, c2)` list isn't legal here and parses as a hint clause (probe-confirmed Msg 321 on the first column name).

`<when-clause>` is one of:

- `WHEN MATCHED [AND <cond>] THEN UPDATE SET col = expr [, …]` / `DELETE`
- `WHEN NOT MATCHED [BY TARGET] [AND <cond>] THEN INSERT [(cols)] VALUES (exprs)` / `INSERT DEFAULT VALUES`
- `WHEN NOT MATCHED BY SOURCE [AND <cond>] THEN UPDATE SET col = expr [, …]` / `DELETE`

### Grammar enforcement

| Probed Msg | When raised |
|---|---|
| **5316** | The target has an INSTEAD OF trigger for some of the actions the WHEN clauses perform but not all — see [`triggers.md`](triggers.md#instead-of-on-views). |
| **5324** | A WHEN MATCHED or WHEN NOT MATCHED BY SOURCE clause with `AND` appeared after the unconditional clause in the same family. Real's parser raises it, so it comes back alone and ends the batch (probed 2026-10-01). |
| **8672** | A target row matched more than one source row, and the WHEN MATCHED clause that fired chose UPDATE. DELETE is forgiving (multiple matches collapse to one delete — probe-confirmed). It ends the batch and rolls the transaction back as under `XACT_ABORT` (probed 2026-09-28). |
| **10710** | WHEN NOT MATCHED [BY TARGET] clause specified UPDATE or DELETE (only INSERT is legal). |
| **10711** | WHEN MATCHED or WHEN NOT MATCHED BY SOURCE clause specified INSERT (only UPDATE / DELETE are legal). |
| **10713** | MERGE statement missing the required trailing `;` — at the end of the batch or before the next statement's keyword; a stray word in its place is Msg 102 at the word (probed 2026-09-30). |
| **10714** | More than one WHEN NOT MATCHED [BY TARGET] clause, or a MATCHED / NOT MATCHED BY SOURCE family repeating an action — two UPDATEs or two DELETEs — the message naming the family and the action (probed 2026-10-01). |
| **209** | An unqualified name in the `ON` predicate or a matched clause that both the target and the source carry (probed 2026-10-01). |
| **207** / **4104** | A name in the `ON` predicate or a `WHEN … AND` that neither side answers to — an aliased target's own name included (`MERGE t AS x … ON t.id = …`, probed 2026-10-01). Which side failed decides the message, matching real: a name whose qualifier is the target or source alias — or that carries no qualifier at all — is a bad *column* (Msg 207), while a qualifier neither side answers to is an unbindable identifier (Msg 4104). Both bind while compiling, so they fire over an empty rowset and at CREATE of a module carrying the MERGE. |
| **5333** / **5334** | A `WHEN NOT MATCHED [BY TARGET]` condition naming anything but a source column, or a `WHEN NOT MATCHED BY SOURCE` one naming anything but a target column — a name bound nowhere and one in a subquery included; state 2 for a qualified name, 1 for a bare one, while a miss qualified by the clause's own side stays Msg 207. The clause's action sees only that side too, missing the other with the ordinary Msg 4104 / 207 (probed 2026-09-28). |
| **1015** / **157** / **5310** / **5319** | An aggregate in the `ON`, an action's `SET` list, an insert action's `VALUES` or a `WHEN … AND`, written there or moved there from a subquery reading only the statement's columns ([`query.md`](query.md#aggregate-ownership-across-scopes)). |

A view target is written through its base table when it has one; a view with none is matched as its own rows, which its INSTEAD OF triggers take ([`triggers.md`](triggers.md#instead-of-on-views)) or, for a join view, are carried back to the one base table the actions name ([`programmable.md`](programmable.md#dml-through-a-join-view)).

### Execution

`Simulation.Merge.cs:ExecuteMerge` is a single-pass walk:

1. **Materialize source** once into `List<SqlValue[]>` via the parse-time `Func<BatchContext, List<SqlValue[]>>` materializer.
   `VALUES`-form evaluates the tuple expressions; `SELECT`-form runs `Selection.Execute` and decodes via `RowDecoder`; the bare-table / view form reads the table in scan order (clustered-key order when it has one) or runs the view selection respectively, then runs `EvaluateComputedColumns` per row so source-side computed columns are observable from the ON predicate / SET / INSERT projections.
2. **Phase A — matching**: per target row, the source rows it matches; ON operands resolve through a combined resolver wired to both the target alias (and the target's own name) and the source alias.
   Multiple-match collection feeds the Msg 8672 guard.
   For each target with ≥ 1 match, walk WHEN MATCHED clauses; first clause whose `AND` is satisfied (or absent) wins.
   For each target with 0 matches, walk WHEN NOT MATCHED BY SOURCE clauses the same way.
   Action gets queued (`pendingInserts` / `pendingUpdates` / `pendingDeletes`) along with the `(page, slot)` address + pre-update and post-update row snapshots.
   Under a `TOP` or `SET ROWCOUNT` cap each candidate row becomes a `MergeStep` instead, keyed as OUTPUT orders them, and the steps run in key order after phase B until the cap is met.
   Three strategies settle this phase — see [Match strategies](#match-strategies).
3. **Phase B — unmatched sources**: for each source row that didn't match any target, the single WHEN NOT MATCHED BY TARGET clause's AND condition is evaluated; if true, queue an INSERT.
4. **Phase C — commit**: PK / UNIQUE validation runs on the union of pending inserts + updates via `EnforceKeyConstraintsForUpdate` (inserts use sentinel `(-1, i)` addresses).
   If a violation surfaces, every queued mutation is abandoned and the statement-atomic undo log already captures the no-heap-writes state.
   Then deletes tombstone, updates rewrite, inserts append, in that order.
5. **Phase D — OUTPUT**: walk the queued actions in source-row order, each NOT MATCHED BY SOURCE delete after them all; the `MergeOutputProjection` resolves `INSERTED.col` / `DELETED.col` / source-alias / `$action`.
   That is the order real's usual plan for an upsert produces, driving from the source side (probed 2026-09-26 against SQL Server 2025).
   Where real chooses a merge join instead — a keyed target under a NOT MATCHED BY SOURCE clause, or a table source it sorts — it lists them in key order, which the simulator, having no plan choice to mirror, doesn't reproduce.
   For each row, the unmatched side projects all-NULL.
6. **Phase E — triggers**: INSERT triggers fire once with the combined inserted set, then UPDATE triggers once with both inserted + deleted, then DELETE triggers once with the deleted set.
   Order is probe-confirmed (INSERT → UPDATE → DELETE); each kind fires once total per MERGE, regardless of how many WHEN clauses contributed to that kind.

### Match strategies

Phase A settles on one of three, all producing the same matched-source list per target row — ascending source index, so first-source-wins, the Msg 8672 multi-match guard and heap-order application read identically whichever ran — or skips the target altogether.
The opt-in `JoinDiagnostics` trace records the choice (`Merge:NoTargetRead` / `Merge:TargetSeek` / `Merge:HashMatch(keys=N,residual=M)` / `Merge:Scan`), which is what `MergeMatchStrategyTests` guards.

- **No target read** — the ON is settled non-TRUE while compiling (`ON 1=0`, every EF Core multi-row insert; `ON 0=1 AND …`; `ON NULL = 1`) and no WHEN NOT MATCHED BY SOURCE clause needs the unmatched targets, so nothing can match and every source row goes straight to WHEN NOT MATCHED BY TARGET.
  Real's plan doesn't read the target either: `STATISTICS IO` lists it at scan count 0 with only the inserts' reads, and no worktable (probed 2026-10-01 against SQL Server 2025), and the simulator now reports the same.
  Walking it instead cost a decode per target row and an ON per target × source pair, growing with the table ([measured](plan-cache.md#ef-cores-multi-row-insert)).

- **Target seek** — the ON carries a seekable target equality and the target isn't a view (whose column names don't map to the base heap).
  The loop inverts: seek the matching targets per source row (`Selection.TryPrepareMergeTargetSeek`), re-running the full ON per candidate as a residual filter.
  With no WHEN NOT MATCHED BY SOURCE clause it then visits only matched targets; with one it walks the heap once applying the precomputed matches (BY-SOURCE for the rest).
  ~9× faster on a large target; see [`indexes.md`](indexes.md#merge-target-seeking-loop-inversion).
- **Source hash** — no seekable target equality (an unindexed target, a `#temp` built by `SELECT … INTO`, a view target), but the ON has at least one `<target column> = <source column>` conjunct.
  Those conjuncts become the hash keys: the source is hashed once into a bucket per distinct key value, and each target row probes with its own key values, so matching is O(target + source) rather than O(target × source).
  A bucket is a forward-linked chain over source indexes, which is what keeps a probe's candidates in source order.
  Everything the ON says beyond the keys stays a residual re-checked per probed pair.
  Only a bare column reference on each side qualifies as a key, and the two sides are told apart by the resolver's own rules (target alias or the target's own name → target, source alias → source, unqualified → target first) so a hashed pair can never swap sides; a pair whose types wouldn't promote the way the runtime `=` promotes them (a LOB operand, a collation conflict) declines to the residual, and key values are coerced to that promotion type before hashing, so a bucket hit means exactly what evaluating the equality would have said.
  A NULL in any key component equi-matches nothing (`NULL = NULL` is UNKNOWN) on either side — such a target row falls to WHEN NOT MATCHED BY SOURCE and such a source row to WHEN NOT MATCHED BY TARGET.
  The hash builds at the first target row that probes it, never ahead of one, so an empty target (or one a view hides entirely) evaluates nothing and can raise nothing — the scan's own behavior.
- **Target × source scan** — no equality splits across the two sides at all (`ON t.id < s.id`, `ON t.a = t.b`, an ON whose operands are computed).
  The whole ON runs per pair.

**A target's non-persisted computed column is evaluated where a row is read through it**, so one that fails for some row (`c AS 10 / (price - 1)` at `price = 1`) is Msg 8134 exactly when the statement reads it for that row (probed 2026-10-01 against SQL Server 2025).
The ON raises for every target row it pairs with a source row, a hash key on the column for every row probed; a seek or an equality key reads only the rows it finds, and a WHEN condition or an action only its own rows, so `ON p.id = s.x AND p.c = 1` raises only when `s.x` finds the failing row.
Nothing raises when nothing pairs: an empty source, or an ON settled non-TRUE with no WHEN NOT MATCHED BY SOURCE clause, whose target isn't read at all.
Real filters by every other ON conjunct first, wherever written — `ON p.c = 1 AND s.x = 0` and `ON p.c = 1 AND p.price = 5` raise nothing for a row the other conjunct turns away — so the match tries the conjuncts reading such a column last, while an `OR` evaluates in written order (`p.c = 1 OR p.id = s.x` raises, `p.id = s.x OR p.c = 1` doesn't for a row the first operand matches).
The statement ends before any write, trigger or `OUTPUT` row.
A persisted column can't hold a failing row: the insert that would store it raises.
The row decode leaves a failing column NULL, and a read of a NULL there evaluates it again (`ExecuteMerge`'s `ReadTargetColumn`).

Measured on WideWorldImporters (`.vs/workload`, `merge.*` in `complex4-wwi.sql`), MERGE into a `SELECT … INTO`-built `#temp` — the shape with no index to seek:

| target × source | scan | source hash | live |
|---|---|---|---|
| 1k × 2k | 557 ms | 8.8 ms | 11.6 ms |
| 4k × 8k | 8,703 ms | 31.0 ms | 41.8 ms |
| 36k × 73k | > 300,000 ms (timeout) | 122.3 ms | 506.5 ms |

### `$action` pseudo-column

Recognized in OUTPUT only.
Tokenizer special-cases `$action` (case-insensitive, word-boundary terminated) into a single `UnquotedString` token rather than the default `$`-as-money-literal + `action`-as-name split.
The OUTPUT parser detects it by string compare and synthesizes a private `MergeActionReference` expression whose runtime value is the action verb (`INSERT` / `UPDATE` / `DELETE` uppercase nvarchar).
Surfaces through any wrapping `AS alias` thanks to `IsMergeActionRef` drilling past `NamedExpression`.
Default column name is `$action`.

### Constraint messages

A MERGE's constraint refusals name the **MERGE** statement — `The MERGE statement conflicted with the CHECK constraint …`, for its inserts, updates and deletes alike, a referencing row its delete strands included — while its insert's NULL into a NOT NULL column says `UPDATE fails.`, as its update's does (probed 2026-10-01 against SQL Server 2025).

### Triggers + identity

Each MERGE invocation fires its triggers AFTER all queued mutations apply (matching real SQL Server's "statement-after" semantic).
Identity counter advances per insert as expected; `SCOPE_IDENTITY` and `@@IDENTITY` hold the last inserted row's identity at MERGE completion.
Trigger bodies see the post-MERGE state in INSERTED/DELETED.

### EF Core reach

EF Core 7+ emits MERGE for SaveChanges batch INSERT (the OUTPUT-INSERTED-id shape).
Multi-action MERGE through EF requires raw SQL (`FromSqlInterpolated` for the body) — EF's LINQ surface doesn't reach the multi-branch form.
EF Core's `ExecuteUpdate` / `ExecuteDelete` for batched single-statement DML emits regular `UPDATE FROM` / `DELETE FROM` (the simulator's existing joined-source UPDATE/DELETE paths handle those), not MERGE.

### Not modeled

- `WHEN NOT MATCHED BY SOURCE` with `THEN INSERT` — Msg 10711 (parsing rejects).
- MERGE into a view ships for a single-base updatable view (`MergeViewTests`), its `OUTPUT` reading the view's columns, for any view whose INSTEAD OF triggers take its actions, and for a join view whose actions each land in one base table — see [`programmable.md`](programmable.md#dml-through-a-join-view).
- Multi-statement WHEN-clause bodies (real SQL Server only allows the one DML action per WHEN — same restriction here).
- **An `OUTPUT` naming a source column in a MERGE whose only clauses are `WHEN NOT MATCHED BY SOURCE`** is Msg 4104 on real, which binds no source there; here it reads NULL (probed 2026-09-28).
- **Composable DML** — a nested `INSERT` / `UPDATE` / `DELETE` / `MERGE` with `OUTPUT` as an `INSERT … SELECT`'s source (`INSERT a SELECT … FROM (MERGE … OUTPUT …) x`), with real's Msg 355 for a triggered target and Msg 8156 for a duplicate column name — is Msg 156 here (probed 2026-10-01 against SQL Server 2025).
