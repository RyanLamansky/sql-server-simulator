# DML — UPDATE / DELETE / INSERT…SELECT / INSERT…EXEC / SELECT…INTO / MERGE / rowversion

## UPDATE / DELETE
- Bare `UPDATE table SET ... [WHERE]` and `DELETE [FROM] table [WHERE]`.
- **Target scan is seek-narrowed** when the single-table WHERE carries an indexable equality / IN / range (`Selection.SeekMutationTarget`), instead of walking the whole heap — the same per-`Heap` seek cache the SELECT path and FK enforcement use.
  The mutation loop re-runs the full WHERE per row (residual filter) and X-locks only the rows it commits, so it's a pure narrowing; positioned (`WHERE CURRENT OF`) mutations keep the scan.
  See [`indexes.md`](indexes.md#update--delete-target-seeking).
  The multi-table (joined) UPDATE / DELETE form isn't seek-narrowed; `MERGE` is, via loop inversion (see its section below).
- Multi-table syntax (`UPDATE alias SET ... FROM <sources> [WHERE]`, `DELETE FROM alias FROM <sources> [WHERE]`) — the EF7+ `ExecuteUpdate`/`ExecuteDelete` shape.
  Target identified by leading-identifier match against each source's `FromSource.Qualifier`; missing match → Msg 208.
- **Joined UPDATE/DELETE: each unique target row processed exactly once.**
  When the same target matches multiple join tuples, SQL Server uses the *first* matching tuple's RHS for SET.
  The simulator dedupes by `(page, slot)` via a side-channel byte[]→address map.
  LEFT JOIN with no right-side match still surfaces the target (RHS sees NULL).
- **OUTPUT** supported only when the leading identifier resolves to a real table name; OUTPUT + alias-form multi-source → `NotSupportedException` (EF doesn't combine those).
- **Multi-column SET evaluates RHS against pre-update snapshot** — `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` → `(a=100, b=11)`.
  Scalar subquery RHS sees pre-update state.
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
  All four route through one `OutputProjection` and one `TryParseOutputIntoTarget`; an INTO target consumes the rows, so the statement reports as a non-query rather than leaving an empty result set behind.
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

## `TOP (expr) [PERCENT]` on UPDATE / DELETE / INSERT

`TOP` caps the number of rows a DML statement affects — SSMS's "Edit Top 200 Rows" commits every cell edit as `UPDATE TOP (200) <t> SET … WHERE <22 concurrency predicates>`.
Parsed by `Selection.ParseDmlTopClause` (a leading-clause helper threaded into `ParseUpdate` / `ParseDelete` / `ParseInsert`) and applied post-collection by `Simulation.ApplyDmlTopCap`, which trims the affected/deleted/source-row list to the cap `Selection.ResolveDmlTopCap` computes.
Placement: after the verb, before the target — `UPDATE TOP (n) t …`, `DELETE TOP (n) [FROM] t …`, `INSERT TOP (n) [INTO] t …`.
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
- **Validation timing divergence.**
  Real SQL Server rejects a bad literal at compile time (before any scan); the simulator resolves + validates the value after collecting candidate rows but before any heap write, so the error still surfaces with zero rows changed — observably identical for a single statement.
  `ResolveDmlTopCap` is always called when a limit is present (even at zero candidates) so the value errors fire regardless of match count.
- **INSERT TOP** caps the inserted-row count across `VALUES` (multiple tuples), `SELECT`, and `EXEC` sources — applied to the buffered `sourceRows` list in `ProcessHeapInsert` (and the view / INSTEAD OF paths).

## `DEFAULT` as a `VALUES` element

`INSERT INTO t (a, b) VALUES (1, DEFAULT)` — the `DEFAULT` keyword in an individual value cell, distinct from the whole-row `DEFAULT VALUES` form below.
Legal only inside `INSERT … VALUES`, so `ParseValuesTuples` takes an `allowDefault` flag and only the INSERT-VALUES path passes `true`.

The VALUES source is parsed **before** identity diagnostics run (`Simulation.Insert.cs`), because the per-cell DEFAULT keywords have to be visible to them:

- an **identity** column receiving `DEFAULT` raises **Msg 339** ("DEFAULT or NULL are not allowed as explicit identity values."), and it fires *before* the `IDENTITY_INSERT` gate — probe-confirmed to raise with `IDENTITY_INSERT` both ON and OFF.
- a **non-identity** DEFAULT cell resolves to the column's default in the shared row-encode loop, taking the same path an omitted column would.

Django's `db_default` field option emits this shape, which is what motivated it.

## `INSERT INTO t DEFAULT VALUES`
Inserts a single row with every column defaulted.
`ProcessHeapInsert` clears the destination-column list and feeds one empty source tuple, so every column flows through the default / identity-allocation / implicit-NULL path — a NOT NULL column with no default hits the same constraint error an explicit all-defaults insert would (probe-confirmed).

## `rowversion` (legacy synonym `timestamp`)
8-byte big-endian database-scoped monotonic counter; advances on every INSERT into a rowversion-bearing table and every UPDATE affecting one.
Storage type name surfaces as `timestamp` in `information_schema` regardless of declaration.
Explicit insert → Msg 273; explicit update → Msg 272; second column on a table → Msg 2738.
Outbound CAST: `varbinary(N)`/`binary(N)` copy 8 bytes; `bigint` reads big-endian.
`Promote(RowVersion, Varbinary) → Varbinary` so EF's `WHERE [rv] = @originalRv` parameter works directly.
EF `[Timestamp]` SaveChanges round-trips end-to-end.

**`MIN_ACTIVE_ROWVERSION()`** returns the rowversion counter's current next-allocated value as `binary(8)` big-endian.
Real SQL Server returns the minimum *active transaction's* lowest rowversion, which over-approximates to "current next-to-allocate" when no transactions are open; the simulator returns the current next-to-allocate value unconditionally — semantically equivalent for the common consumer (incremental-sync watermarks) since rowversion writes within an open transaction wouldn't be visible to readers anyway.

**`@@DBTS`** returns the last-allocated rowversion value as `binary(8)` big-endian — the value `MIN_ACTIVE_ROWVERSION` reports as next-allocated, minus one.
Implementation calls `Database.AllocateRowVersion() - 1`, which bumps the counter as a side effect (rowversion values are advisory and monotonic, so the spurious bump is harmless to the contract but is a fidelity gap from real SQL Server's non-bumping read).
Used by tooling watermarking via the "current high water" pattern (sync delta from `WHERE rv > @lastDbts`).

## Identity helpers (`@@IDENTITY` / `SCOPE_IDENTITY` / `IDENT_CURRENT` / `IDENT_INCR` / `IDENT_SEED`)
Per-column identity allocation routes through `HeapTable.IdentityState`.
The session-state scalars (`@@IDENTITY`, `SCOPE_IDENTITY()`) read from `SimulatedDbConnection.LastIdentity`; `IDENT_CURRENT(name)` reads the named table's last-allocated value directly.
`IDENT_INCR(name)` / `IDENT_SEED(name)` (`Parser/Expressions/IdentSeedIncrement.cs`) return the declared step / start of the named table's identity column, or NULL when the table lacks one or the name doesn't resolve.
All three name-arg scalars accept a 1-/2-/3-part dotted runtime string via the same `TryParseObjectName` helper `OBJECT_ID` uses.
Result type is `numeric(38, 0)` matching real SQL Server's projection (covers tinyint/smallint/int/bigint columns uniformly).

`SET IDENTITY_INSERT <table> ON | OFF` (`Simulation.Set.cs`) sets / clears `SimulatedDbConnection.IdentityInsertTable`.
ON validates the target: a table with no identity column raises **Msg 8106** (`TableHasNoIdentityForSet`, "Table 't' does not have the identity property. Cannot perform SET operation."); a second table while one is already held raises **Msg 8107** (`IdentityInsertAlreadyOn`) — both probe-confirmed against SQL Server 2025.

## `@@ROWCOUNT` / `ROWCOUNT_BIG()`
Both expose the row count of the most-recently-completed statement on the session via `SimulatedDbConnection.LastStatementRowCount`.
`@@ROWCOUNT` projects as `int`; `ROWCOUNT_BIG()` (`Parser/Expressions/TransactionScalarFunctions.cs`) is its `bigint` sibling — same source, wider projection.
Same set + reset rules: every DML statement updates the count; control-flow statements (IF / WHILE / SET / DECLARE) leave it unchanged on the failure path but set it to the result on success; SELECT inside a `set @v = (select ...)` reports the inner-SELECT's affected row count.

## INSERT … SELECT
`INSERT [INTO] target [(cols)] SELECT …` accepts the full Selection grammar — WHERE/JOIN/GROUP BY/aggregates/ORDER BY/TOP/OFFSET-FETCH/UNION/INTERSECT/EXCEPT all work source-side.

Source-kind dispatch after the OUTPUT-clause parse: `Values` token → existing tuple-parsing path; `Select` token → `Selection.Parse(…).Execute()`.
Both funnel into one shared per-row encode loop (defaults / identity / rowversion / computed / constraints / OUTPUT).

**Full buffering**: source materializes to `List<SqlValue[]>` before any destination write — makes self-insert (`INSERT t SELECT … FROM t`) safe.

Projection-count mismatch fires at parse time: too few SELECT columns → Msg 120 St 1 Cls 15; too many → Msg 121.
Empty source → silent success, rows-affected 0.
Mid-source constraint violations trigger statement-level rollback.
EF doesn't emit `INSERT…SELECT` from SaveChanges; reachable from raw SQL and bulk-copy patterns.
CTE-prefix INSERTs not modeled.

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
  Validated during the drain, before any heap write.
- **Uncoercible values** surface the shared per-row coercion error (the simulator's Msg 245 conversion path — real SQL Server raises Msg 8114 for a dynamic value; the simulator's INSERT coercion path is Msg 245 for both INSERT…SELECT and INSERT…EXEC, a divergence).
- **Nested INSERT…EXEC** (the executed proc / dynamic batch itself contains an `INSERT … EXEC`) raises **Msg 8164 St 1** "An INSERT EXEC statement cannot be nested." Guarded by `SimulatedDbConnection.InsertExecActive`, set while the outer drain runs and checked at the inner INSERT…EXEC entry.
- **OUTPUT clause combined with INSERT…EXEC** raises **Msg 483 St 2** "The OUTPUT clause cannot be used in an INSERT...EXEC statement." — a structural check that fires regardless of skip state, before the source dispatch.

Skip-mode (un-taken IF branch) parses the EXEC clause for cursor advance but `ParseExec` self-suppresses the invocation, so the drain sees no result sets and no rows land.

## `SELECT … INTO target`
Creates a destination table from the projection's inferred schema, then copies rows in.
Target routes by `#`-prefix: `#foo` lands in the per-connection `TempTables` dict (same as `CREATE TABLE #foo`); regular names land in the current database's `HeapTables`.
Probe-confirmed schema-inference rules:

- **Nullability**: direct column refs preserve source nullability.
  Integer arithmetic, `CAST`, `COALESCE`, aggregates (incl. `COUNT`), and bare `NULL` literal all project as **nullable**.
  `ISNULL(x, y)` is **non-null when either arg is non-null** (asymmetric with COALESCE).
  `CASE` is non-null when every `THEN` branch is non-null AND the `ELSE` branch is non-null (no-`ELSE` = implicit `ELSE NULL` = nullable).
  Non-NULL literals are non-null.
  String `+` should also project non-null when both operands non-null, but the simulator's runtime-dispatch design (Add can be arithmetic or concat depending on operand types) makes static analysis impractical — projects as nullable (minor fidelity gap; staging tables rarely depend on this).
- **Identity propagation**: only when the projection is a *direct column ref* (a `Reference`, possibly wrapped in `NamedExpression` for `AS alias`) AND the FROM clause is exactly one source with a `BackingTable` (a real heap, not a derived table / CTE / OPENJSON) AND no joins.
  WHERE/TOP/ORDER BY preserve.
  Any join, set-op, expression wrapping, or CTE drops it.
  Destination's `IdentityState` starts fresh with the source's seed+increment and tracks the copied values via `ObserveExplicit`.
- **Implementation**: `Selection.IntoTarget` + `Selection.DestColumnSchema` (a `HeapColumn[]`) are captured at parse time inside `ParseInner` and propagated through `CombineSetOps` / `ApplyTopLevelOrderBy`.
  `Simulation.SelectInto.cs:ExecuteSelectInto` creates the heap table, runs the Selection, encodes each row through `RowEncoder.EncodeRow`, appends to the dest's heap, and tracks the active transaction's undo log so a `ROLLBACK` unwinds both the table creation (for temp tables) and the row writes.
- **Schema rules + validation** live in `Selection.SelectInto.cs:ComputeIntoDestSchema`.
  Nullability uses `Expression.ResultIsNullable` (a new virtual override on `Value` / `Reference` / `NamedExpression` / `IsNullExpression` / `CaseExpression`; default `true` for everything else).
  Identity uses `UnwrapDirectRef` to drill through `NamedExpression` layers.
- **Errors**: unnamed projection → **Msg 1038 Cl 15 St 5** (`SelectIntoMissingColumnName`); duplicate column name in projection → **Msg 2705 Cl 16 St 3** (`DuplicateColumnInSelectInto`, names the target table); target already exists → **Msg 2714** (reused factory); `##` global target → `NotSupportedException`.
- **INTO + UNION**: real SQL Server allows `SELECT … INTO #t FROM a UNION ALL SELECT … FROM b` (INTO on first branch).
  The simulator parses this, propagates `IntoTarget` from the left branch through `CombineSetOps`, and strips identity on the combined dest schema.
  A right branch carrying its own INTO → Msg 156 (`Incorrect syntax near the keyword 'into'.`).
- **INTO without FROM** works (`SELECT 1 AS x INTO #t`) — synthesized-row path threads `IntoTarget` through.
- **Quirk**: CTE-wrapped single-heap source drops identity and nullability — the simulator's CTE bindings synthesize `HeapColumn` entries with `nullable: true` and no identity, so the analyzer can't peer through.
  Real SQL Server propagates both.
  Fix would require propagating column metadata through CTE bindings; future bundle.

## MERGE

`MERGE [INTO] target [WITH (hints)] [AS alias] USING <source> [AS alias] [(cols)] [WITH (hints)] ON predicate <when-clause>+ [OUTPUT …];` where `<source>` is one of:

- `(VALUES …)` — parenthesized literal tuples; alias is required.
- `(SELECT …)` or a set-op chain — parenthesized query; alias is required.
- bare-table / view / `#temp` / `@tablevar` / `schema.table` reference — alias is optional and defaults to the source's leaf name.
  Optional `WITH (hint [, …])` table hints sit alias-then-hint (same placement as FROM source); the trailing column-rename `(c1, c2)` list isn't legal here and parses as a hint clause (probe-confirmed Msg 321 on the first column name).

`<when-clause>` is one of:

- `WHEN MATCHED [AND <cond>] THEN UPDATE SET col = expr [, …]` / `DELETE`
- `WHEN NOT MATCHED [BY TARGET] [AND <cond>] THEN INSERT (cols) VALUES (exprs)`
- `WHEN NOT MATCHED BY SOURCE [AND <cond>] THEN UPDATE SET col = expr [, …]` / `DELETE`

### Grammar enforcement

| Probed Msg | When raised |
|---|---|
| **5324** | A WHEN MATCHED or WHEN NOT MATCHED BY SOURCE clause with `AND` appeared after the unconditional clause in the same family. |
| **8672** | A target row matched more than one source row, and the WHEN MATCHED clause that fired chose UPDATE. DELETE is forgiving (multiple matches collapse to one delete — probe-confirmed). |
| **10710** | WHEN NOT MATCHED [BY TARGET] clause specified UPDATE or DELETE (only INSERT is legal). |
| **10711** | WHEN MATCHED or WHEN NOT MATCHED BY SOURCE clause specified INSERT (only UPDATE / DELETE are legal). |
| **10713** | MERGE statement missing the required trailing `;`. The dispatch loop accepts either `;` or end-of-batch; anything else here raises. |
| **10714** | More than one WHEN NOT MATCHED [BY TARGET] clause (real SQL Server admits at most one INSERT branch — different from MATCHED / NOT MATCHED BY SOURCE which allow multiple AND-conditioned clauses). |

### Execution

`Simulation.Merge.cs:ExecuteMerge` is a single-pass walk:

1. **Materialize source** once into `List<SqlValue[]>` via the parse-time `Func<BatchContext, List<SqlValue[]>>` materializer.
   `VALUES`-form evaluates the tuple expressions; `SELECT`-form runs `Selection.Execute` and decodes via `RowDecoder`; the bare-table / view form iterates the underlying heap or view selection respectively, then runs `EvaluateComputedColumns` per row so source-side computed columns are observable from the ON predicate / SET / INSERT projections.
2. **Phase A — target × source**: for each target heap row, enumerate source rows; ON evaluates with a combined resolver wired to both target alias and source alias.
   Multiple-match collection feeds the Msg 8672 guard.
   For each target with ≥ 1 match, walk WHEN MATCHED clauses; first clause whose `AND` is satisfied (or absent) wins.
   For each target with 0 matches, walk WHEN NOT MATCHED BY SOURCE clauses the same way.
   Action gets queued (`pendingInserts` / `pendingUpdates` / `pendingDeletes`) along with the `(page, slot)` address + pre-update and post-update row snapshots.
   **Seek-accelerated when applicable**: when the ON carries a seekable target equality and the target isn't a view, the match phase inverts — it seeks matching targets per source row (`Selection.TryPrepareMergeTargetSeek`), re-running the full ON per candidate (residual filter), first-source-wins and heap order preserved.
   With no WHEN NOT MATCHED BY SOURCE clause it then visits only matched targets; with one it walks the heap once applying the precomputed matches (BY-SOURCE for the rest) — dropping the inner source loop either way.
   ~9× faster on a large target; see [`indexes.md`](indexes.md#merge-target-seeking-loop-inversion).
   Declines to the full scan for a view target or a non-seekable ON.
3. **Phase B — unmatched sources**: for each source row that didn't match any target, the single WHEN NOT MATCHED BY TARGET clause's AND condition is evaluated; if true, queue an INSERT.
4. **Phase C — commit**: PK / UNIQUE validation runs on the union of pending inserts + updates via `EnforceKeyConstraintsForUpdate` (inserts use sentinel `(-1, i)` addresses).
   If a violation surfaces, every queued mutation is abandoned and the statement-atomic undo log already captures the no-heap-writes state.
   Then deletes tombstone, updates rewrite, inserts append, in that order.
5. **Phase D — OUTPUT**: walk queued INSERT rows → UPDATE rows → DELETE rows; the `MergeOutputProjection` resolves `INSERTED.col` / `DELETED.col` / source-alias / `$action`.
   For each row, the unmatched side projects all-NULL.
6. **Phase E — triggers**: INSERT triggers fire once with the combined inserted set, then UPDATE triggers once with both inserted + deleted, then DELETE triggers once with the deleted set.
   Order is probe-confirmed (INSERT → UPDATE → DELETE); each kind fires once total per MERGE, regardless of how many WHEN clauses contributed to that kind.

### `$action` pseudo-column

Recognized in OUTPUT only.
Tokenizer special-cases `$action` (case-insensitive, word-boundary terminated) into a single `UnquotedString` token rather than the default `$`-as-money-literal + `action`-as-name split.
The OUTPUT parser detects it by string compare and synthesizes a private `MergeActionReference` expression whose runtime value is the action verb (`INSERT` / `UPDATE` / `DELETE` uppercase nvarchar).
Surfaces through any wrapping `AS alias` thanks to `IsMergeActionRef` drilling past `NamedExpression`.
Default column name is `$action`.

### Triggers + identity

Each MERGE invocation fires its triggers AFTER all queued mutations apply (matching real SQL Server's "statement-after" semantic).
Identity counter advances per insert as expected; `SCOPE_IDENTITY` (and `@@IDENTITY` collapsed onto the same slot) holds the last inserted row's identity at MERGE completion.
Trigger bodies see the post-MERGE state in INSERTED/DELETED.

### EF Core reach

EF Core 7+ emits MERGE for SaveChanges batch INSERT (the OUTPUT-INSERTED-id shape).
Multi-action MERGE through EF requires raw SQL (`FromSqlInterpolated` for the body) — EF's LINQ surface doesn't reach the multi-branch form.
EF Core's `ExecuteUpdate` / `ExecuteDelete` for batched single-statement DML emits regular `UPDATE FROM` / `DELETE FROM` (the simulator's existing joined-source UPDATE/DELETE paths handle those), not MERGE.

### Not modeled

- `WHEN NOT MATCHED BY SOURCE` with `THEN INSERT` — Msg 10711 (parsing rejects).
- MERGE into a view (real SQL Server allows updatable views as MERGE targets) — only base tables and table variables ship.
- Source as a CTE-prefixed SELECT (`USING (WITH cte AS … SELECT …)`) — Selection.Parse doesn't reach CTEs from a subquery slot.
  Wrap the CTE inside a non-CTE SELECT instead.
- `OUTPUT … INTO @t` with `$action` — the existing `OUTPUT INTO @t` path uses `MutationOutputProjection`, which doesn't carry the `$action` slot.
  INTO-less OUTPUT works fully.
- Multi-statement WHEN-clause bodies (real SQL Server only allows the one DML action per WHEN — same restriction here).
