# Plan cache

Per-`Simulation` cache of parsed `Selection` objects for single-SELECT command batches — the EF Core query shape.
A repeat call against the same `CommandText` (with matching parameter types) skips tokenization and parsing entirely; only the row source executes.

## Cache key

`(string CommandText, string DatabaseName, string ParameterSignature, bool QuotedIdentifiers)` keyed by command text, the connection's current database, a parameter-type signature folded from each `SimulatedDbCommand.Parameters` entry's name + `DbType` + `Size` + `Precision` + `Scale` (declaration order), and the session's effective `QUOTED_IDENTIFIER` setting.
Any of those can affect parse-time type inference, so a mismatch demands a fresh parse.
`QUOTED_IDENTIFIER` is in the key because it changes what the *same text* tokenizes to — `"x"` is a delimited identifier when on, a varchar literal when off — so a cached plan from one setting is wrong under the other.
Backed by `ConcurrentDictionary` with the default ordinal-case-sensitive string comparer.

The parameter signature is built defensively: a TVP whose `Value` is an `IDataReader` causes `SimulatedDbParameter.DbType`'s getter to throw `ArgumentException` (CA1065 — documented at the property), and the signature builder catches it and returns `null` for that command.
A null signature means "skip the cache for this call" — caching wouldn't help anyway because structured parameters carry session-scoped data the cache doesn't model.

## Invalidation: `Simulation.SchemaVersion`

Each cache entry stamps the `Simulation.SchemaVersion` it was parsed under.
A lookup compares against the live version; mismatch = stale = re-parse.
The fresh parse overwrites the stale entry via the dictionary's indexer rather than `TryAdd`, so DDL doesn't accumulate orphaned entries under the same key.

Bump sites:

- CREATE / DROP / ALTER (the dispatch arm in `DispatchOneStatementCore` — these three cases were peeled out of the unified arm specifically to host the bump).
- `Simulation.ImportBacpac` (new database adds schema).
- `Simulation.AddRemoteSimulation` (changes what an active linked-server name will resolve to at the next `sp_addlinkedserver`).
- `sp_addlinkedserver` / `sp_dropserver` (changes the active linked-server table, which four-part-name FROM clauses resolve against at parse time).

Non-DDL statements that touch principal / permission / extended-property / trigger-enable state don't bump — cached SELECT plans don't depend on those for parse-time validity.

## Promotion happens inline in the SELECT arm

The natural place for cache-add would be "after the dispatch loop, before the iterator returns".
The dispatch is an iterator method, though: code after `yield return outcome` runs only when the consumer pulls the next value.
For `ExecuteReader` with a single SELECT — the dominant EF shape — the consumer reads rows and holds the reader; the iterator pauses at the yield, and until the reader advances or disposes the post-yield code never runs.
A post-yield promotion wouldn't fire until then — too late for a caller that reuses the plan while the reader is still open.
(Reader `Dispose` *does* drain the outcome stream — the statement-level drain for batch-error continuation — so it would eventually reach the post-yield code, but relying on that would still miss the pre-dispose window.)

So `Simulation.CreateResultSetsForCommand` stashes the cache-key components on the `BatchContext`, and the SELECT arm of `DispatchOneStatementCore` calls `TryPromoteSelectionToPlanCache` **inline before the `yield return outcome`**, after rows are materialized.
Gates checked at the SELECT arm: `BlockDepth == 0` (top-level statement, not inside an IF / WHILE / BEGIN / TRY block), `!HasDispatchedStatement` (first statement of the batch — second-and-later top-level statements skip caching), `!IsAssignmentOnly`, `!HasSessionScopedReference` (next section), and `context.Token is null` (parser consumed the whole text — no trailing statements after this one).
Trailing semicolons disqualify; EF queries don't emit them.

One gate sits further out, in `TryBuildPlanCacheKey`, so it suppresses the **lookup** as well as the promotion: the session must be at the default **READ COMMITTED**.
A plan's FROM sources carry the lock acquisitions their parsing session made, so replaying one under a different isolation level would settle the wrong session's protection, or none at all — a SERIALIZABLE reader's key-range fence most visibly (see [`locking.md`](locking.md#key-range-locks)).
A session at any other level re-parses per execution.

## Disqualifying state: `HasSessionScopedReference`

A batch's `HasSessionScopedReference` flag suppresses cache promotion.
It's set in three places, all at parse time:

1. **`BatchContext.TryResolveTable` for `#temp` / `##gtemp` / `@t`**: those bindings hold a specific `HeapTable` instance whose identity is meaningful only to this session (or this batch, for `@t`).
   A cross-session plan-cache replay would project the wrong instance.
2. **`BuildSynthesizedSqlRow` (the FROM-less SELECT path)**: that path evaluates projection expressions at parse time (the documented Run-then-GetSqlType ordering for error-message fidelity) and bakes the resulting `SqlValue`s into the row source.
   Caching would emit those stale values forever; `NEWID()` / `GETDATE()` / `@@TRANCOUNT` / `NEXT VALUE FOR seq` need a fresh parse per call.
3. **The recursive-CTE builder** (`Simulation.With.cs`): a recursive-CTE plan rebinds `CteBinding.CurrentIterationRows` at execution time, so a cached copy replayed by two commands concurrently would cross-feed iteration rowsets.
   A FROM-less anchor (`SELECT 1 … UNION ALL …`) was already disqualified by rule 2; the builder's own flag covers FROM-ful anchors.
   Non-recursive CTEs stay cacheable (their bindings are read-only after parse).

All conditions disqualify identically at the promotion site.
The flag name is intentionally general — what matters is "this plan can't be safely replayed", not the cause.

## The shared-plan contract: per-execution state lives per execution

A cached `Selection` is **one object executed by many commands, possibly concurrently**.
Anything that varies per execution must therefore live in execution-scoped state (`BatchContext` / `StatementContext`), never on the plan or its expression tree.
The original single-owner assumption ("Expression instances aren't shared across queries, and query execution is single-threaded") predated the cache; four latent violations shipped with it and were fixed together after the AW / WWI workload driver surfaced intermittent sim-vs-live divergences in aggregate / window templates under 8-worker concurrency:

- **Aggregate / window bind results** — `AggregateExpression` / `WindowExpression` bound each group's / row's computed value into instance fields before projecting; two concurrent executions interleaved binds and projected each other's values (measured ~1% of reads wrong; zero single-threaded).
  The results move to `BatchContext.BoundProjectionResults` (lazily-allocated, reference-keyed by expression instance); `BindResult(batch, value)` writes it, `Run` reads it through `runtime.Batch`.
- **`TOP (@p)` / `OFFSET @o` / `FETCH @f` counts** — parse-time-resolved ints baked the first execution's parameter values into the plan, freezing EF `Take`/`Skip` pagination deterministically (`@p = 2` then `@p = 5` both returned 2 rows).
  The expressions are stored (`FromClause.OffsetExpression` / `FetchExpression`, `topExpression`) and re-resolved per execution at the top of the row-source closure (`ResolveRowCountLimit` — also applied by the set-op chain's `ApplyTopLevelOrderBy`); parse still resolves once for immediate literal validation (Msg 10742 / 10744 fidelity).
- **`RAND()` draws** — instance-cached, so a cached plan replayed the same "random" value forever.
  The draw freezes in `StatementContext.StatementScopedValues` (per statement execution — cleared by the dispatch loop's top-of-iteration alongside the `UtcNow` refresh), preserving the probe-confirmed per-call-site-per-statement semantics.
- **The statement clock on replay** — `ReplayCachedSelection` bypasses the dispatch loop and never stamped `CurrentStatement.UtcNow`, so a replayed `GETDATE()` read `default(DateTime)`.
  The replay path stamps `UtcNow` + `StartLine` itself.

When adding any executor or expression feature that computes per-row / per-group / per-execution values, bind them through `BatchContext` / `StatementContext` — never through fields on parse-time objects.

## Co-fix: `VariableReference` resolves at Run time

Pre-cache, `VariableReference` captured the live `VariableSlot` instance at parse time (`context.Batch.GetVariableSlot(name)`).
That worked cleanly within a single batch because the captured slot is the same one `SET @v` mutates — the slot reference threads the parse-then-execute lifetime.

But that capture binds the reference to the **parsing batch's** `Variables` dict.
A cached `Selection` replayed under a fresh `BatchContext` would still read the original batch's slot — projecting the parse-time parameter value forever, ignoring the new call's parameter binding.

`VariableReference.Run(runtime)` reads `runtime.Batch.Variables[name].Value` at each call.
Intra-batch `SET @v` mutations still surface because the lookup returns the same slot those statements mutate; cross-batch replay correctly picks up the new batch's binding.
Parse-time `context.Batch.GetVariableSlot(name)` is still called once (for the Msg 137 "must declare scalar variable" check and for the `DeclaredType` capture `GetSqlType` needs at parse time).

## Replay path

A cache hit short-circuits the full dispatch via `ReplayCachedSelection`:

- New `BatchContext` for the incoming command (seeds `Variables` from parameters, allocates the same lock / undo / lifecycle scaffolding the standard path would).
- `selection.Execute(batch)` runs the cached Selection.
  `RowBytes.ToList()` materializes the rows, mirroring the standard path's `LastStatementRowCount` accounting.
- Outcome shape: `SimulatedSqlResultSet` (the only shape we cache — assignment-only Selections never cache).
- `WriteBackOutputParameters` and `FlushPrintMessages` run, same as the standard path.

The replay path is also where `PlanCacheHits` increments; misses increment in `CreateResultSetsForCommand` on the fall-through.

## Capacity

Hard cap at 1024 entries.
New entries beyond cap are silently dropped (the indexer-set is guarded by a `ContainsKey || Count < cap` check).
The cap is defensive — a stable EF app's working set is dozens of unique queries — and refresh-in-place via the indexer means DDL invalidation overwrites under the same key without growing the dictionary.

No LRU.
The "first 1024 unique queries" win; subsequent novel CommandTexts miss every time.
If this becomes a real problem an LRU layer can land later.

## Test observability

`Simulation.PlanCacheHits` and `PlanCacheMisses` (`long`, `Interlocked.Increment`-mutated) plus `PlanCacheCount` (live dict count) are `internal` and consumed by `PlanCacheTests` to assert hit / miss behavior at boundary conditions: identical-query replay, distinct CommandTexts get distinct entries, DDL invalidation, temp-table disqualification, table-variable disqualification, multi-statement disqualification, distinct parameter types get distinct entries, identical parameter types with different values still hit, result correctness across hit / miss, non-SELECT batch bypass.
The shared-plan contract has its own section of tests there: parameterized TOP / OFFSET-FETCH replay resolves new values, RAND re-draws per execution, GETDATE reads the current clock on replay, recursive CTEs decline caching under either anchor shape, and two 8-worker concurrency tests hammer one cached aggregate / window plan asserting zero cross-execution contamination.

## Performance impact

Measured against `.vs/workload/` benches:

- Point lookup (`SELECT … WHERE pk = @v`): ~0.020 → 0.005 ms steady-state (~4×).
- Multi-join EF shape (3 tables, complex projection, indexed WHERE): ~0.058 ms steady-state.
- AW workload @16 workers: 884 → ~940 qps (~+6%).
- WWI workload @16 workers: flat — runtime is dominated by execution of large report queries, not parse cost.

The cache pays off proportionally to the parse-cost-to-execution-cost ratio.
The complex EF projections with many joins and `OUTER APPLY` chains for owned types — where parse can hit several milliseconds — see the biggest absolute savings.

## Not modeled / future

- LRU eviction (current cap is hard FIFO-ish).
- Multi-statement batch caching (each EF SaveChanges' INSERT-then-SELECT-scope_identity round-trip re-parses).
- Trailing-semicolon CommandTexts (the EOB gate is `Token is null` rather than a lookahead through `;`s — EF doesn't emit them on its SELECTs, so the simple gate suffices).
- Parameter-sniffing-style value-dependent plan selection (the simulator has no cost-based optimizer, so this doesn't apply).
