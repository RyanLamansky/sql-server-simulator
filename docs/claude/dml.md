# DML — UPDATE / DELETE / INSERT…SELECT / SELECT…INTO / MERGE / rowversion

## UPDATE / DELETE
- Bare `UPDATE table SET ... [WHERE]` and `DELETE [FROM] table [WHERE]`.
- Multi-table syntax (`UPDATE alias SET ... FROM <sources> [WHERE]`, `DELETE FROM alias FROM <sources> [WHERE]`) — the EF7+ `ExecuteUpdate`/`ExecuteDelete` shape. Target identified by leading-identifier match against each source's `FromSource.Qualifier`; missing match → Msg 208.
- **Joined UPDATE/DELETE: each unique target row processed exactly once.** When the same target matches multiple join tuples, SQL Server uses the *first* matching tuple's RHS for SET. The simulator dedupes by `(page, slot)` via a side-channel byte[]→address map. LEFT JOIN with no right-side match still surfaces the target (RHS sees NULL).
- **OUTPUT** supported only when the leading identifier resolves to a real table name; OUTPUT + alias-form multi-source → `NotSupportedException` (EF doesn't combine those).
- **Multi-column SET evaluates RHS against pre-update snapshot** — `UPDATE t SET a = 100, b = a + 1` over `(a=10, b=20)` → `(a=100, b=11)`. Scalar subquery RHS sees pre-update state.
- Identity update → Msg 8102. Computed update → Msg 271. Rowversion update → Msg 272. Per-row constraint re-validation: NOT NULL → Msg 515 ("UPDATE fails."); CHECK → Msg 547 ("UPDATE statement"); PK/UNIQUE → Msg 2627 (verbatim "Cannot insert duplicate key" wording even on UPDATE — SQL Server quirk). PK/UNIQUE validation runs against the post-update virtual state, so mass-shift on a unique key can false-positive (see Quirks).
- `OUTPUT INSERTED.<col>` (post-update) / `DELETED.<col>` (pre-update). UPDATE allows both qualifiers; DELETE rejects `INSERTED.<col>` at parse → Msg 4104. Star expansion (`INSERTED.*`/`DELETED.*`) not modeled. `OUTPUT … INTO @t [(cols)]` ships for INSERT/UPDATE/DELETE/MERGE — see [Table variables](table-variables.md).

## `rowversion` (legacy synonym `timestamp`)
8-byte big-endian database-scoped monotonic counter; advances on every INSERT into a rowversion-bearing table and every UPDATE affecting one. Storage type name surfaces as `timestamp` in `information_schema` regardless of declaration. Explicit insert → Msg 273; explicit update → Msg 272; second column on a table → Msg 2738. Outbound CAST: `varbinary(N)`/`binary(N)` copy 8 bytes; `bigint` reads big-endian. `Promote(RowVersion, Varbinary) → Varbinary` so EF's `WHERE [rv] = @originalRv` parameter works directly. EF `[Timestamp]` SaveChanges round-trips end-to-end.

## INSERT … SELECT
`INSERT [INTO] target [(cols)] SELECT …` accepts the full Selection grammar — WHERE/JOIN/GROUP BY/aggregates/ORDER BY/TOP/OFFSET-FETCH/UNION/INTERSECT/EXCEPT all work source-side.

Source-kind dispatch after the OUTPUT-clause parse: `Values` token → existing tuple-parsing path; `Select` token → `Selection.Parse(…).Execute()`. Both funnel into one shared per-row encode loop (defaults / identity / rowversion / computed / constraints / OUTPUT).

**Full buffering**: source materializes to `List<SqlValue[]>` before any destination write — makes self-insert (`INSERT t SELECT … FROM t`) safe.

Projection-count mismatch fires at parse time: too few SELECT columns → Msg 120 St 1 Cls 15; too many → Msg 121. Empty source → silent success, rows-affected 0. Mid-source constraint violations trigger statement-level rollback. EF doesn't emit `INSERT…SELECT` from SaveChanges; reachable from raw SQL and bulk-copy patterns. CTE-prefix INSERTs not modeled.

## `SELECT … INTO target`
Creates a destination table from the projection's inferred schema, then copies rows in. Target routes by `#`-prefix: `#foo` lands in the per-connection `TempTables` dict (same as `CREATE TABLE #foo`); regular names land in the current database's `HeapTables`. Probe-confirmed schema-inference rules (2026-05-11):

- **Nullability**: direct column refs preserve source nullability. Integer arithmetic, `CAST`, `COALESCE`, aggregates (incl. `COUNT`), and bare `NULL` literal all project as **nullable**. `ISNULL(x, y)` is **non-null when either arg is non-null** (asymmetric with COALESCE). `CASE` is non-null when every `THEN` branch is non-null AND the `ELSE` branch is non-null (no-`ELSE` = implicit `ELSE NULL` = nullable). Non-NULL literals are non-null. String `+` should also project non-null when both operands non-null, but the simulator's runtime-dispatch design (Add can be arithmetic or concat depending on operand types) makes static analysis impractical — projects as nullable (minor fidelity gap; staging tables rarely depend on this).
- **Identity propagation**: only when the projection is a *direct column ref* (a `Reference`, possibly wrapped in `NamedExpression` for `AS alias`) AND the FROM clause is exactly one source with a `BackingTable` (a real heap, not a derived table / CTE / OPENJSON) AND no joins. WHERE/TOP/ORDER BY preserve. Any join, set-op, expression wrapping, or CTE drops it. Destination's `IdentityState` starts fresh with the source's seed+increment and tracks the copied values via `ObserveExplicit`.
- **Implementation**: `Selection.IntoTarget` + `Selection.DestColumnSchema` (a `HeapColumn[]`) are captured at parse time inside `ParseInner` and propagated through `CombineSetOps` / `ApplyTopLevelOrderBy`. `Simulation.SelectInto.cs:ExecuteSelectInto` creates the heap table, runs the Selection, encodes each row through `RowEncoder.EncodeRow`, appends to the dest's heap, and tracks the active transaction's undo log so a `ROLLBACK` unwinds both the table creation (for temp tables) and the row writes.
- **Schema rules + validation** live in `Selection.SelectInto.cs:ComputeIntoDestSchema`. Nullability uses `Expression.ResultIsNullable` (a new virtual override on `Value` / `Reference` / `NamedExpression` / `IsNullExpression` / `CaseExpression`; default `true` for everything else). Identity uses `UnwrapDirectRef` to drill through `NamedExpression` layers.
- **Errors**: unnamed projection → **Msg 1038 Cl 15 St 5** (`SelectIntoMissingColumnName`); duplicate column name in projection → **Msg 2705 Cl 16 St 3** (`DuplicateColumnInSelectInto`, names the target table); target already exists → **Msg 2714** (reused factory); `##` global target → `NotSupportedException`.
- **INTO + UNION**: real SQL Server allows `SELECT … INTO #t FROM a UNION ALL SELECT … FROM b` (INTO on first branch). The simulator parses this, propagates `IntoTarget` from the left branch through `CombineSetOps`, and strips identity on the combined dest schema. A right branch carrying its own INTO → Msg 156 (`Incorrect syntax near the keyword 'into'.`).
- **INTO without FROM** works (`SELECT 1 AS x INTO #t`) — synthesized-row path threads `IntoTarget` through.
- **Quirk**: CTE-wrapped single-heap source drops identity and nullability — the simulator's CTE bindings synthesize `HeapColumn` entries with `nullable: true` and no identity, so the analyzer can't peer through. Real SQL Server propagates both. Fix would require propagating column metadata through CTE bindings; future bundle.

## MERGE

`MERGE [INTO] target [AS alias] USING (<source>) [AS] alias [(cols)] ON predicate <when-clause>+ [OUTPUT …];` where `<source>` is `VALUES`, `SELECT`, or a set-op chain, and `<when-clause>` is one of:

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

1. **Materialize source** once into `List<SqlValue[]>` via the parse-time `Func<BatchContext, List<SqlValue[]>>` materializer (`VALUES`-form evaluates the tuple expressions; `SELECT`-form runs `Selection.Execute` and decodes via `RowDecoder`).
2. **Phase A — target × source**: for each target heap row, enumerate source rows; ON evaluates with a combined resolver wired to both target alias and source alias. Multiple-match collection feeds the Msg 8672 guard. For each target with ≥ 1 match, walk WHEN MATCHED clauses; first clause whose `AND` is satisfied (or absent) wins. For each target with 0 matches, walk WHEN NOT MATCHED BY SOURCE clauses the same way. Action gets queued (`pendingInserts` / `pendingUpdates` / `pendingDeletes`) along with the `(page, slot)` address + pre-update and post-update row snapshots.
3. **Phase B — unmatched sources**: for each source row that didn't match any target, the single WHEN NOT MATCHED BY TARGET clause's AND condition is evaluated; if true, queue an INSERT.
4. **Phase C — commit**: PK / UNIQUE validation runs on the union of pending inserts + updates via `EnforceKeyConstraintsForUpdate` (inserts use sentinel `(-1, i)` addresses). If a violation surfaces, every queued mutation is abandoned and the statement-atomic undo log already captures the no-heap-writes state. Then deletes tombstone, updates rewrite, inserts append, in that order.
5. **Phase D — OUTPUT**: walk queued INSERT rows → UPDATE rows → DELETE rows; the `MergeOutputProjection` resolves `INSERTED.col` / `DELETED.col` / source-alias / `$action`. For each row, the unmatched side projects all-NULL.
6. **Phase E — triggers**: INSERT triggers fire once with the combined inserted set, then UPDATE triggers once with both inserted + deleted, then DELETE triggers once with the deleted set. Order is probe-confirmed (INSERT → UPDATE → DELETE); each kind fires once total per MERGE, regardless of how many WHEN clauses contributed to that kind.

### `$action` pseudo-column

Recognized in OUTPUT only. Tokenizer special-cases `$action` (case-insensitive, word-boundary terminated) into a single `UnquotedString` token rather than the default `$`-as-money-literal + `action`-as-name split. The OUTPUT parser detects it by string compare and synthesizes a private `MergeActionReference` expression whose runtime value is the action verb (`INSERT` / `UPDATE` / `DELETE` uppercase nvarchar). Surfaces through any wrapping `AS alias` thanks to `IsMergeActionRef` drilling past `NamedExpression`. Default column name is `$action`.

### Triggers + identity

Each MERGE invocation fires its triggers AFTER all queued mutations apply (matching real SQL Server's "statement-after" semantic). Identity counter advances per insert as expected; `SCOPE_IDENTITY` (and `@@IDENTITY` collapsed onto the same slot) holds the last inserted row's identity at MERGE completion. Trigger bodies see the post-MERGE state in INSERTED/DELETED.

### EF Core reach

EF Core 7+ emits MERGE for SaveChanges batch INSERT (the OUTPUT-INSERTED-id shape). Multi-action MERGE through EF requires raw SQL (`FromSqlInterpolated` for the body) — EF's LINQ surface doesn't reach the multi-branch form. EF Core's `ExecuteUpdate` / `ExecuteDelete` for batched single-statement DML emits regular `UPDATE FROM` / `DELETE FROM` (the simulator's existing joined-source UPDATE/DELETE paths handle those), not MERGE.

### Not modeled

- `WHEN NOT MATCHED BY SOURCE` with `THEN INSERT` — Msg 10711 (parsing rejects).
- MERGE into a view (real SQL Server allows updatable views as MERGE targets) — only base tables and table variables ship.
- Source as a CTE-prefixed SELECT (`USING (WITH cte AS … SELECT …)`) — Selection.Parse doesn't reach CTEs from a subquery slot. Wrap the CTE inside a non-CTE SELECT instead.
- `OUTPUT … INTO @t` with `$action` — the existing `OUTPUT INTO @t` path uses `MutationOutputProjection`, which doesn't carry the `$action` slot. INTO-less OUTPUT works fully.
- Multi-statement WHEN-clause bodies (real SQL Server only allows the one DML action per WHEN — same restriction here).
