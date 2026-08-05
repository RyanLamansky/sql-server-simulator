# Plan cache

Two per-`Simulation` reuse layers over a repeated `CommandText`, one stacked on the other:

- **The plan cache** stores the parsed `Selection` sequence of a batch whose every top-level statement is a SELECT — the EF Core query shape.
  A repeat call against the same text (with matching parameter types) skips tokenization and parsing entirely; only the row source executes.
- **The token memo** stores the tokenized form of *any* command text.
  A repeat call re-parses but scans no characters and allocates no tokens.
  It is what serves the statement kinds that have no plan to cache — the DML that parses and executes in one interleaved pass — and it also backs the first parse of a text the plan cache will go on to store.

The two are independent: a plan-cache hit never consults the memo (it doesn't parse at all), and a memo hit says nothing about whether a plan will be stored.
[Which statement kinds reach which layer](#statement-kind-eligibility-what-can-be-replayed) is the substance of the split.

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

So `Simulation.CreateResultSetsForCommand` stashes the cache-key components on the `BatchContext`, and the SELECT arm of `DispatchOneStatementCore` calls `TryPromoteSelectionsToPlanCache` **inline before the `yield return outcome`**, after rows are materialized.
Gates checked at the SELECT arm: `BlockDepth == 0` (top-level statement, not inside an IF / WHILE / BEGIN / TRY block), `!IsAssignmentOnly`, and `!HasSessionScopedReference` (next section).
A SELECT passing those joins `BatchContext.PlanCacheSequence`; the promotion itself fires at the statement that finds nothing but separators left.

## The entry is a statement *sequence*

A batch of several top-level SELECTs caches as the sequence it is, and the replay yields one result set per plan.
Two mechanisms carry it:

- **Eligibility by counting.** `BatchContext.TopLevelStatementsDispatched` counts what the dispatch loop ran; `PlanCacheSequence` counts what the SELECT arm collected.
  Equal counts at the promotion site mean every statement the batch ran was an admitted SELECT — anything else (a `SET`, a DML write, a `BEGIN…END` block, an `EXEC`) advances the counter without contributing a plan and so declines the whole batch.
  Counting keeps the eligibility rule in one place instead of a decline call in every arm of the dispatch switch.
  (The loop counts the current statement only after the arm returns, hence the `+ 1` at the comparison.)
- **End-of-batch by probe.** `IsAtEndOfBatch` walks forward over `;` separators from the parser's lookahead position and restores it, so **a trailing semicolon no longer disqualifies** — which is the difference between a cache that serves an ORM and one that serves only text with no trailing punctuation.
  The probe swallows a tokenizer error rather than raising it: text the tokenizer refuses lying past the separators is not the end of the batch, and reporting it from here would put the error ahead of the result set the statement has already produced.

`ReplayCachedSelections` loops the sequence, and each iteration re-stamps the per-statement frame the dispatch loop's top-of-iteration would have — `UtcNow`, `StatementScopedValues`, `SubqueryResults`, `RcsiStatementSnapshotXid`, `BumpRowStamp` — so a second statement neither reads the first's frozen `RAND()` draw nor its cached subquery results, and `LastStatementRowCount` advances statement by statement.

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

## Statement-kind eligibility: what can be replayed

A cache entry has to be a **re-executable artifact**, and only one statement kind in the parser produces one.
`Selection.Parse` returns a plan and `Selection.Execute` runs it; that split is what the plan cache stores.
Every other statement family the dispatch switch routes to **parses and executes in a single interleaved pass** — `ParseInsert` walks tokens, resolves the target, reads the column list, parses the VALUES tuples and writes the rows, all inside one method that takes a `ParserContext` and returns a finished `SimulatedStatementOutcome`.
There is no object between "text" and "rows written" to hold on to.

That is why the audit below is short on replay-safety findings and long on structure: the question "does this statement kind carry parse-time per-execution state?" — the question the `Selection` cache had to answer — presupposes a statement object, and outside SELECT there isn't one.

| Statement kind | Plan cache | Why |
|---|---|---|
| `SELECT` (first) | **Admitted** | `Selection` is a plan; the shared-plan contract below governs it. |
| `SELECT` (second and later, top-level) | **Admitted** | Cached as a sequence; the replay re-stamps each statement's own frame. |
| `SELECT … ;` (trailing separators) | **Admitted** | The end-of-batch probe walks separators. |
| Assignment-only `SELECT`, `SELECT … INTO` | Declined | Neither yields the result-set shape the entry models; both fall out before the accumulation point, which declines the batch by count. |
| `INSERT` / `UPDATE` / `DELETE` / `MERGE` | **Declined — no artifact exists** | Parse and execution are one pass (`Simulation.Insert.cs`, `.Update.cs`, `.Delete.cs`, `.Merge.cs`, ~5,500 lines between them). Caching them means first splitting each into a plan and an executor, past error-ordering that is probe-pinned to the interleaving — a change of a different size and risk than this one. The token memo serves them meanwhile. |
| `SET` (the whole family) | Declined | A `SET` carries a session effect, not a plan. Recording the effect is possible per option, but the family is large and each option's scoping (session vs batch vs suppressed-in-a-module) is its own rule; a batch containing one declines. |
| `DECLARE` / `SET @v` | Declined | Same: the artifact would be the variable write, not a plan. |
| DDL, `EXEC`, control flow, transactions, cursors | Declined | DDL bumps `SchemaVersion` (it invalidates rather than caches); control flow re-parses branches under skip semantics that are per-execution by construction; the rest have no plan object either. |

Everything in the declined column still gets the token memo, which is the part of the front half that can be shared with no replay-safety question at all — because parsing still runs, so nothing parse-time is reused across executions.

## The token memo

`Simulation.TokenMemo` (`Parser/TokenMemo.cs`) maps a tokenization identity to the `Token[]` a `ParserContext` walks.
`ParserContext.MoveNext` reads from the array when one is bound and tokenizes live otherwise, collecting as it goes.

**Key** — `(CommandText, Collation, CompatibilityLevel, QuotedIdentifiers)`: every input `Tokenizer.NextToken` reads.
The text and the `QUOTED_IDENTIFIER` setting decide the token shapes, the collation tags string-literal `SqlValue`s, and the compatibility level decides which words are reserved (`REGEXP_LIKE` at 170).
The collation is compared by reference — a database holds one instance and re-collating installs a different one.

**No invalidation.**
Tokenization is a pure function of those four, so an entry can never go stale; unlike a plan, which stamps `SchemaVersion` because it holds resolved schema objects, a memo holds tokens, which *name* schema objects without resolving them.
There is no bump site to maintain.

**It reaches every parse**, not just top-level batches: a procedure / function / trigger body re-tokenizes its stored text through a synthesized `SimulatedDbCommand` on every invocation, and that goes through the same store.

Four rules keep a shared sequence honest — three of them found by things that broke:

1. **Published only on a complete, error-free tokenization.**
   The publish sits where `NextToken` returns null.
   A text the tokenizer refuses (Msg 102 / 103 / 105 / 113) additionally **abandons** the collection at the throw, because the tokenizer leaves its index past the span it was reading, so the dispatch loop's error recovery can resume beyond the refused text and still reach end-of-text — publishing a sequence with the refused span missing.
   Without the abandon, `select 'unterminated` reported Msg 105 on its first execution and Msg 102 forever after.
2. **Collected by ordinal, not by appending.**
   The parser re-reads: `SaveCheckpoint` / `RestoreCheckpoint` moves the cursor backwards *and forwards* (the `FROM`-clause probe scans ahead, rewinds to re-read the select list, then jumps back to where the scan stopped).
   Each token is written at `memoPosition`, the ordinal it belongs at; an appending collector produced a spliced sequence the moment a restore jumped forward, and the replay then parsed to something else entirely.
3. **A text that rewrites the tokenizer's own inputs is never published.**
   `SET QUOTED_IDENTIFIER` (and the `ANSI_DEFAULTS` bundle carrying it), `USE`, and `ALTER` of the current database's collation or compatibility level all change what the characters *after* them tokenize to, so no single sequence is correct for such a text.
   The live parse abandons the memo the moment the inputs move, but that alone isn't enough: tokenizing runs ahead of dispatch, and a lookahead reaching end-of-text over a batch with no separators completes the sequence *before* the statement that flips the setting has run.
   Judging the finished sequence — a scan for those tokens at publish time — needs no ordering assumption at all.
4. **Abandoned mid-parse if the inputs move anyway.**
   Checked per `MoveNext` against the bound key.
   Everything consumed so far was read under the old inputs and stays valid — the character index is one past it — so live tokenization simply resumes.

Tokens themselves are immutable: `Token` holds `(command, startIndex, length)` readonly, and the one mutable member in the hierarchy is `UnquotedString.ContextualKeyword`'s lazy classification, which is an idempotent pure function of the token's own span written to an enum field — a benign race whichever thread gets there first.

Capacity is the plan cache's: 1024 entries, "first 1024 unique texts win", no LRU.

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
  `MaterializeRows()` drains them, mirroring the standard path's `LastStatementRowCount` accounting — and, like the standard path, keeping the producer's own row form (see [`data-reader.md`](data-reader.md#the-row-form-the-reader-reads)).
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

`Simulation.PlanCacheHits` and `PlanCacheMisses` (`long`, `Interlocked.Increment`-mutated) plus `PlanCacheCount` (live dict count) are `internal` and consumed by `PlanCacheTests` to assert hit / miss behavior at boundary conditions: identical-query replay, distinct CommandTexts get distinct entries, DDL invalidation, temp-table disqualification, table-variable disqualification, distinct parameter types get distinct entries, identical parameter types with different values still hit, result correctness across hit / miss, non-SELECT batch bypass.
The sequence rules add: a multi-SELECT batch caches as one entry whose replay reproduces both result sets, a trailing semicolon still caches, a batch mixing a SELECT with an `INSERT` or a `SET` doesn't, the replay refreshes per-statement state (two `RAND()` statements draw twice, on the cached path as on the uncached one), and `@@ROWCOUNT` after a replayed sequence reads the last statement's count.
The shared-plan contract has its own section of tests there: parameterized TOP / OFFSET-FETCH replay resolves new values, RAND re-draws per execution, GETDATE reads the current clock on replay, recursive CTEs decline caching under either anchor shape, and two 8-worker concurrency tests hammer one cached aggregate / window plan asserting zero cross-execution contamination.

`Simulation.TokenMemo`'s `Hits` / `Misses` / `Count` back `TokenMemoTests`: a repeated DML batch is served on its second execution, a text carrying every token shape replays identically, each `QUOTED_IDENTIFIER` setting gets its own entry while a text that *flips* it mid-batch is never served, a tokenizer error reports the same message on every execution, the back-and-forth-lookahead shape memoizes what it parsed, a procedure body is served across invocations, and 8 workers share one sequence with no divergence.
Those tests deliberately use plan-cache-declined shapes: a bare repeated SELECT is served by the plan cache and never reaches the memo at all, which makes a memo-hit assertion over one silently vacuous.

## Performance impact

### The plan cache

Measured against `.vs/workload/` benches:

- Point lookup (`SELECT … WHERE pk = @v`): ~0.020 → 0.005 ms steady-state (~4×).
- Multi-join EF shape (3 tables, complex projection, indexed WHERE): ~0.058 ms steady-state.
- AW workload @16 workers: 884 → ~940 qps (~+6%).
- WWI workload @16 workers: flat — runtime is dominated by execution of large report queries, not parse cost.

The cache pays off proportionally to the parse-cost-to-execution-cost ratio.
The complex EF projections with many joins and `OUTER APPLY` chains for owned types — where parse can hit several milliseconds — see the biggest absolute savings.

### The token memo

A/B against the same build with the memo's binding disabled, EF Core 10's own emitted batch texts, steady-state (the row inserted each iteration is deleted again so the table doesn't grow), best of three runs:

| Batch | Memo off | Memo on | Δ |
|---|---|---|---|
| `SET …; SET NOCOUNT ON; INSERT … OUTPUT INSERTED.[Id] VALUES (…)` | 38.4 µs | 34.6 µs | −10% |
| `SET …; SET NOCOUNT ON; UPDATE … OUTPUT 1 WHERE …` | 14.6 µs | 12.4 µs | −15% |
| `SET …; SET NOCOUNT ON; DELETE … OUTPUT 1 WHERE …` | 12.9 µs | 11.0 µs | −14% |
| `SET …; SET NOCOUNT ON; MERGE … OUTPUT` (3 rows) | 199.7 µs | 185.3 µs | −7% |
| The `SET` pair alone | 1.9 µs | 1.2 µs | −35% |
| `EXEC` of a procedure (body re-parses per call) | 29.1 µs | 25.3 µs | −13% |
| A plan-cached `SELECT` | 1.54 µs | 1.55 µs | none — the replay path never tokenizes |

The pattern is the same one the plan cache shows: the saving is a fixed per-text cost, so it reads as a large fraction of a small batch and a small one of a heavy `MERGE`.

**These deltas exceed what a tokenize-only measurement predicts, and the gap is the point.**
Timing `Tokenizer.NextToken` in a loop over the same texts gives 2.4–2.9 µs for the INSERT batch, where the end-to-end saving is ~3.8 µs.
A memo skips more than the character scan: it skips **constructing** the tokens, and construction is where `UnquotedString.CheckReserved` runs `Enum.TryParse<Keyword>` over every word.
It also makes `UnquotedString.ContextualKeyword`'s lazy classification a once-ever cost rather than a once-per-parse one, because the token instance carrying the memoized field is shared across executions.
An earlier estimate (2026-07-30) put a token cache at "~10% of parse cost, ~5–8% of the operation" and shelved it on that basis; it was measuring the scan and missing both amortizations.
The lesson generalizes: **for a cache, measure by disabling it in the real pipeline, not by timing the work you think it removes.**

**At the EF Core level the win is much smaller**, and the honest number is worth recording: a `SaveChanges` inserting one row measured 203.8 µs against 198.7 µs, and updating one row 52.4 µs against 47.0 µs — roughly 2–9%, because EF's own change tracking, SQL generation and materialization dominate a round trip that spends ~35 µs inside the simulator.
The 3-row `MERGE` shape is dominated by execution (~2.3 ms per `SaveChanges`) and the memo is invisible in it.
The simulator-side batch cost is what improves 10–35%; the fraction of a caller's time that is is the caller's business.

## Not modeled / future

- **A plan artifact for DML.** `INSERT` / `UPDATE` / `DELETE` / `MERGE` would each need a parse/execute split before there is anything to cache — the shape `Selection.Parse` / `Selection.Execute` already has.
  The pieces a plan would hold are visible in the code (target table, resolved column list, the `Expression[]` value tuples, the OUTPUT clause, the TOP cap); what stands in the way is that the error ordering between them is probe-pinned to the interleaving, so the split has to preserve it statement kind by statement kind.
  Their token memo removes the character scan meanwhile.
- **`SET` / `DECLARE` as recordable effects**, which is what a batch mixing them with a SELECT would need to cache as a sequence — the EF modification-batch prefix is exactly this shape.
- LRU eviction, for both layers (the cap is hard FIFO-ish, and a one-shot migration script run first can fill it ahead of the steady-state working set).
- Parameter-sniffing-style value-dependent plan selection (the simulator has no cost-based optimizer, so this doesn't apply).
- A memo entry per *distinct* text is stored on first sighting, so a workload of unique texts fills the cap with entries nothing will read again.
  Two-phase admission (store on second sighting) would keep the cap for texts that repeat.
