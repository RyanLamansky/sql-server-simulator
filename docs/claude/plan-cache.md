# Plan cache

Two per-`Simulation` reuse layers over a repeated `CommandText`, one stacked on the other:

- **The plan cache** stores two kinds of plan.
  For a batch whose every top-level statement is a SELECT — the EF Core query shape — it stores the parsed `Selection` sequence, and a repeat call against the same text (with matching parameter types) skips tokenization and parsing entirely; only the row source executes.
  For any other batch it stores a plan per top-level `INSERT … VALUES`, single-table `UPDATE`, single-table `DELETE` and `MERGE` from a `VALUES` list or a table — the EF Core modification shapes — and a repeat call walks the batch as usual but skips parsing those statements ([DML statement plans](#dml-statement-plans)).
- **The token memo** stores the tokenized form of *any* command text.
  A repeat call re-parses but scans no characters and allocates no tokens.
  It is what serves the statements that have no plan to cache, it backs the first parse of a text the plan cache will go on to store, and it is what a DML statement plan jumps over.

The two are independent: a plan-cache hit never consults the memo (it doesn't parse at all), and a memo hit says nothing about whether a plan will be stored.
[Which statement kinds reach which layer](#statement-kind-eligibility-what-can-be-replayed) is the substance of the split.

## Cache key

`Simulation.PlanCacheKey` holds the command text, the connection's current database, a parameter-type signature folded from each `SimulatedDbCommand.Parameters` entry's name + `DbType` + `Size` + `Precision` + `Scale` (declaration order), the session's effective `QUOTED_IDENTIFIER` setting, its `SET DATEFORMAT` order (which decides how a date string read while parsing reads), and its `ANSI_NULLS` and `CONCAT_NULL_YIELDS_NULL` settings.
Any of those can affect what the parse produces, so a mismatch demands a fresh parse.
Every other session setting is read by the executing batch at run time, which `PlanCacheSessionTests` pins by differential: a replay under `DATEFIRST`, `LANGUAGE`, `ARITHABORT` / `ANSI_WARNINGS`, `NUMERIC_ROUNDABORT`, `TEXTSIZE`, `CONTEXT_INFO`, session context, an open transaction, a `#temp` table or another principal must answer what a fresh parse under that setting answers, in both directions.
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

Non-DDL statements that touch principal / permission / extended-property / trigger-enable state don't bump — cached plans don't depend on those for parse-time validity ([principal independence](#principal-independence)).
Every bump also invalidates the catalog row cache, which the non-DDL metadata changes invalidate on their own — see [`catalog-views.md`](catalog-views.md#cross-statement-row-cache-and-indexes).

## Clearing: `DBCC FREEPROCCACHE`

`Simulation.ClearPlanCache` removes entries rather than staling them: `DBCC FREEPROCCACHE` (bare, or naming the `default` pool, which holds every plan here) and `DBCC FREESYSTEMCACHE('ALL' | 'SQL Plans')` empty the plan cache (both kinds of plan), the compiled-batch memo and the token memo, so the next execution of any text tokenizes, compiles and parses afresh.
Given a `sql_handle` (the value `sys.dm_exec_requests.sql_handle` reports), `FREEPROCCACHE` removes the plan cache and compiled-batch entries whose command text hashes to it, and leaves the token memo alone.
`ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE` removes the plan cache and compiled-batch entries compiled in the session's database, and leaves the token memo alone.
The `internal` pool and a plan handle name nothing, the simulator exposing no plans; `DbccPlanCacheTests` (Tests.Internal) pins each scope.
A session with `STATISTICS IO` or `TIME` on neither reads nor fills either cache, so its every batch compiles and reports ([`session-options.md`](session-options.md#statistics-time)).

What a compile sends rather than refuses rides on whether it happens: the Msg 208 a scalar function call that couldn't inline earns goes out only when its batch's text compiles, so a cache hit sends nothing, as real's plan reuse does; the texts that sent theirs are remembered under the schema version they compiled under (`Simulation.SendsInliningFailures`) for the compiles no cache skips, and the three clearings above forget them too.
A batch holding an `OPTION (RECOMPILE)` statement with such a call is never remembered as compiled, since real compiles it afresh every time ([`programmable.md`](programmable.md#inlining-a-call-as-the-query-compiles)).

## Promotion happens inline in the SELECT arm

The natural place for cache-add would be "after the dispatch loop, before the iterator returns".
The dispatch is an iterator method, though: code after `yield return outcome` runs only when the consumer pulls the next value.
For `ExecuteReader` with a single SELECT — the dominant EF shape — the consumer reads rows and holds the reader; the iterator pauses at the yield, and until the reader advances or disposes the post-yield code never runs.
A post-yield promotion wouldn't fire until then — too late for a caller that reuses the plan while the reader is still open.
(Reader `Dispose` *does* drain the outcome stream — the statement-level drain for batch-error continuation — so it would eventually reach the post-yield code, but relying on that would still miss the pre-dispose window.)

So `Simulation.CreateResultSetsForCommand` stashes the cache-key components on the `BatchContext`, and the SELECT arm (`RunSelectStatement`) calls `TryPromoteSelectionsToPlanCache` **inline, before the dispatch yields the outcome**, after rows are materialized.
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
The table-level locks a parse takes, and the per-row lock plan it hands its FROM sources, are chosen by the isolation level, so a plan parsed under one level replayed under another would take the wrong locks — a SERIALIZABLE reader's key-range fence most visibly (see [`locking.md`](locking.md#key-range-locks)).
A session at any other level re-parses per execution.
The same gate holds `SET IMPLICIT_TRANSACTIONS`, `NOEXEC`, `PARSEONLY` and `FMTONLY` off: each is settled while a statement parses — the transaction it opens, whether it runs at all, and whether it returns only metadata — so a replay would skip it (see [`session-options.md`](session-options.md)).

## Disqualifying state: `HasSessionScopedReference`

A batch's `HasSessionScopedReference` flag suppresses cache promotion.
It's set in three places, all at parse time:

1. **`BatchContext.TryResolveTable` for `#temp` / `##gtemp` / `@t`**: those bindings hold a specific `HeapTable` instance whose identity is meaningful only to this session (or this batch, for `@t`).
   A cross-session plan-cache replay would project the wrong instance.
2. **`BuildSynthesizedSqlRow` (the FROM-less SELECT path)**: that path types, then evaluates, projection expressions at parse time and bakes the resulting `SqlValue`s into the row source.
   Caching would emit those stale values forever; `NEWID()` / `GETDATE()` / `@@TRANCOUNT` / `NEXT VALUE FOR seq` need a fresh parse per call.
3. **The recursive-CTE builder** (`Simulation.With.cs`): a recursive-CTE plan rebinds `CteBinding.CurrentIterationRows` at execution time, so a cached copy replayed by two commands concurrently would cross-feed iteration rowsets.
   A FROM-less anchor (`SELECT 1 … UNION ALL …`) was already disqualified by rule 2; the builder's own flag covers FROM-ful anchors.
   Non-recursive CTEs stay cacheable (their bindings are read-only after parse).

All conditions disqualify identically at the promotion site.
The flag name is intentionally general — what matters is "this plan can't be safely replayed", not the cause.

## Statement-kind eligibility: what can be replayed

A cache entry has to be a **re-executable artifact**: an object between "text" and "rows" that a second execution can run without the first one's parse.
`Selection.Parse` returns a plan and `Selection.Execute` runs it; that split is what the SELECT sequence stores.
`INSERT … VALUES`, single-table `UPDATE`, single-table `DELETE` and `MERGE` hand their parse to an execution half at a split point ([DML statement plans](#dml-statement-plans)), and that plan is what a DML entry stores.
Every other statement family the dispatch switch routes to **parses and executes in a single interleaved pass**, so there is nothing to hold on to.

| Statement kind | Plan cache | Why |
|---|---|---|
| `SELECT` (first) | **Admitted** | `Selection` is a plan; the shared-plan contract below governs it. |
| `SELECT` (second and later, top-level) | **Admitted** | Cached as a sequence; the replay re-stamps each statement's own frame. |
| `SELECT … ;` (trailing separators) | **Admitted** | The end-of-batch probe walks separators. |
| Assignment-only `SELECT`, `SELECT … INTO` | Declined | Neither yields the result-set shape the entry models; both fall out before the accumulation point, which declines the batch by count. |
| `INSERT … VALUES`, `UPDATE` / `DELETE` of one table (no `FROM`), `MERGE` into a table from a `VALUES` list or a table | **Admitted per statement** | In any batch, beside statements with no plan; the gates are [below](#what-a-dml-plan-declines). |
| `INSERT … SELECT` / `EXEC` / `DEFAULT VALUES`, joined `UPDATE` / `DELETE`, DML through a view, `MERGE` from a query | Declined | The source query or the `FROM` clause is read as the statement parses; no split point exists. |
| `SET` (the whole family), `DECLARE`, `SET @v` | Declined | A `SET` carries a session effect, not a plan; it parses every time (the pair EF emits costs about a microsecond with the token memo), and a batch holding one keeps its DML plans. |
| DDL, `EXEC`, control flow, transactions, cursors | Declined | DDL bumps `SchemaVersion` (it invalidates rather than caches); control flow re-parses branches under skip semantics that are per-execution by construction; the rest have no plan object either. |

Everything in the declined column still gets the token memo, which is the part of the front half that can be shared with no replay-safety question at all — because parsing still runs, so nothing parse-time is reused across executions.

## DML statement plans

A DML plan is cached **per statement**, not per batch: `Simulation.dmlPlanSets` maps the batch's `PlanCacheKey` to a `DmlPlanSet`, which maps the ordinal in the text's memoized token sequence where a statement starts to a `DmlPlanEntry`.
The batch looks its set up once as it starts, and the dispatch loop runs as it always does — every `SET`, `DECLARE` and `SELECT` parses — until an `INSERT` / `UPDATE` / `DELETE` / `MERGE` arm reaches `RunDmlStatement`, which finds the entry for the statement starting at the cursor.

**The split point.**
Each of the four parsers builds its plan (`InsertPlan`, `UpdatePlan`, `DeletePlan`, `MergePlan`) where its last token is consumed and hands it to the execution half (`RunInsertValues`, `RunUpdate`, `RunDelete`, `RunMerge`) — on every execution, cached or not, so the fresh path and the replay run the same code.
What moved to put that point where it is: `IDENTITY_INSERT` and an `INSTEAD OF INSERT` trigger's enabled state are read by the execution half, and neither raises, so the move reorders no error.
A `MERGE`'s point is its required `;`, ahead of the permission checks, which were already the first thing its execution did.
The execution half reads no tokens; a Debug build throws if the cursor moved past the recorded end while it ran.

**Recording.**
`RunDmlStatement` arms `BatchContext.DmlPlanRecording` and the `ReplayLockLog` around the parse; the split point (`NoteDmlPlan`) takes the plan, the cursor's checkpoint, the locks the parse took and the statement-frame flags it set (`OpensTransaction`, `CallsUserFunction`, the permanent / temporary object reads, the client `OUTPUT` shape), and disarms the log so the execution half's row locks stay out.
A statement whose execution half fails is still recorded: its parse was complete.
A statement the split point declines is recorded as declined — an entry with no plan, stamped with the schema version — so its later runs parse without arming anything.

**Replay.**
Inside `RunMutation`, exactly where the parse would have run, the replay jumps the cursor to the recorded end (`ParserContext.JumpTo`, only when the token there is the memoized sequence's own), retakes the recorded locks as the replaying session, restores the flags, then runs the execution half.
Everything around the statement — `RunMutation`'s atomicity and undo log, the dispatch loop's error handling, `@@ROWCOUNT`, `NOCOUNT`, Query Store capture, the trailing-token check — is the ordinary path, which is what keeps a replayed statement's errors, their lines and their order the parsed statement's.
Locks come before flags because a parse takes its locks before it reaches anything that sets one; a lock wait that ends the statement ends it with the frame the parse had at that point.

**Why the parse half is safe to skip.**
What a DML parse checks is either decided by the text and the schema — the same key and `SchemaVersion` decide it the same way, and a parse that raised records nothing — or read from the session.
The session reads are handled one of three ways: moved into the execution half (above), repeated from the recording (locks, flags, and the one permission check a parse makes, `INSERT`'s on its target), or gated so they can't differ (below).
Nothing a plan holds depends on the principal that parsed it ([below](#principal-independence)).
`DmlPlanReplayTests` (public API) is the differential: each test runs a text several times against one simulation, the later runs replaying, and against a second whose plan cache is emptied before every run, and compares the transcripts — result sets, row counts, every error's number, class, state, line and message, info messages and the table afterwards — over constraint, conversion, truncation (either message, as `VERBOSE_TRUNCATION_WARNINGS` picks), `NOT NULL`, foreign-key, duplicate-key, divide-by-zero and trigger-rollback errors, `XACT_ABORT`, `IDENTITY_INSERT`, `SET ROWCOUNT` and trigger state flipped between runs, a `TOP (@n)` that trims the parsed tuples, identity, rowversion, sequence-default and computed values, a schema change under `OUTPUT INSERTED.*`, explicit transactions, `TRY` / `CATCH`, error lines deep in a batch, Query Store's record of the runs, another session's lock and 8 concurrent replayers — and for `MERGE`, the EF shapes' errors, a trigger rollback under `OUTPUT … INTO @inserted0`, an upsert from a table under `TOP (@n)` and `SET ROWCOUNT`, an `INSTEAD OF` trigger created and toggled between runs, `OUTPUT … INTO` a table whose schema changes, and 8 sessions replaying one plan into their own table variables.

### What a DML plan declines

Checked per statement, since an earlier statement in the batch can change what the key was taken under (`MayCacheDmlPlan`):

- The batch has a plan-cache key (so the READ COMMITTED and session-option gates [above](#the-entry-is-a-statement-sequence) hold), and the statement is top-level, not skipping, not being read again for a binder report, and not in a module, trigger or procedure body.
- `QUOTED_IDENTIFIER`, `DATEFORMAT`, `ANSI_NULLS`, `CONCAT_NULL_YIELDS_NULL` and the database still equal the key's; the isolation level and the gated options are read live.
- Any principal: the plans are principal-independent ([below](#principal-independence)), so a restricted principal, an `EXECUTE AS` frame and an application role replay what another principal recorded, and a masked column changes nothing.

And per shape, at the split point (`NoteDmlPlan` plus each parser's `admitted`):

- A plain table in the session's own database: not a view, a table variable, a `#temp` table (any session-scoped reference in the statement), a TVP, a linked server's table, or a table whose writes check the session's SET options (an indexed view, a filtered index, a persisted computed column — `RequiresCorrectSetOptions`).
- No nested query: a subquery holds closures over the parse (`ParserContext.QueriesParsed` counts them), and no `.modify()` / CLR mutator or variable assignment in an `UPDATE`'s `SET` list, for the same reason.
- An `OUTPUT … INTO` target that is a table variable the batch declares or a table passing the same checks as the statement's own; and a client `OUTPUT` only on a table with no trigger at all, since Msg 334 is settled while parsing and `ENABLE TRIGGER` doesn't bump the schema version.
  A table variable belongs to its batch, so the plan holds its name and each execution writes the executing batch's (`OutputTarget`): every batch running the same text declares it the same way, which keeps the parse's column mapping right for each.
  A table-valued parameter is declined, its type standing outside the key.
- For `MERGE`: a `VALUES` source or a table in the session's database (a query source is a nested query; a CTE-prefixed `MERGE` doesn't reach the cache), a table target rather than a view, no `INSTEAD OF` trigger on the target, enabled or not — which actions such triggers take is settled while parsing from their enabled state (Msg 5316) — and no `.modify()` in an `UPDATE SET`.
- No `WHERE CURRENT OF` and no `XACT_STATE()` (whose mark is a per-statement object).

EF Core's shapes for a table with triggers — `DECLARE @inserted0 TABLE`, then an `INSERT` or `MERGE … OUTPUT … INTO @inserted0` and a `SELECT` from it, and `UPDATE … ; SELECT @@ROWCOUNT` — all replay their DML; the `SELECT` over the table variable parses each time.

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

Capacity is the plan cache's: 1024 entries, "first 1024 unique texts win", no LRU — until a [clear](#clearing-dbcc-freeproccache) empties it.
A parse past capacity still collects its sequence and publishes nothing: Query Store reads a statement's tokens back from it (`ParserContext.StatementTokens`) rather than tokenizing the text again.

## The shared-plan contract: per-execution state lives per execution

A cached `Selection` is **one object executed by many commands, possibly concurrently**.
Anything that varies per execution must therefore live in execution-scoped state (`BatchContext` / `StatementContext`), never on the plan or its expression tree.
The original single-owner assumption ("Expression instances aren't shared across queries, and query execution is single-threaded") predated the cache; latent violations shipped with it, the first four fixed together after the AW / WWI workload driver surfaced intermittent sim-vs-live divergences in aggregate / window templates under 8-worker concurrency, the rest as a replay on a second connection surfaced them:

- **Aggregate / window bind results** — `AggregateExpression` / `WindowExpression` bound each group's / row's computed value into instance fields before projecting; two concurrent executions interleaved binds and projected each other's values (measured ~1% of reads wrong; zero single-threaded).
  The results move to `BatchContext.BoundProjectionResults` (lazily-allocated, reference-keyed by expression instance); `BindResult(batch, value)` writes it, `Run` reads it through `runtime.Batch`.
- **`TOP (@p)` / `OFFSET @o` / `FETCH @f` counts** — parse-time-resolved ints baked the first execution's parameter values into the plan, freezing EF `Take`/`Skip` pagination deterministically (`@p = 2` then `@p = 5` both returned 2 rows).
  The expressions are stored (`FromClause.OffsetExpression` / `FetchExpression`, `topExpression`) and re-resolved per execution at the top of the row-source closure (`ResolveRowCountLimit` — also applied by the set-op chain's `ApplyTopLevelOrderBy`); parse still resolves once for immediate literal validation (Msg 10742 / 10744 fidelity).
- **`RAND()` draws** — instance-cached, so a cached plan replayed the same "random" value forever.
  The draw freezes in `StatementContext.StatementScopedValues` (per statement execution — cleared by the dispatch loop's top-of-iteration alongside the `UtcNow` refresh), preserving the probe-confirmed per-call-site-per-statement semantics.
- **The statement clock on replay** — `ReplayCachedSelection` bypasses the dispatch loop and never stamped `CurrentStatement.UtcNow`, so a replayed `GETDATE()` read `default(DateTime)`.
  The replay path stamps `UtcNow` + `StartLine` itself.
- **The session scalars** — `@@SPID`, `@@TRANCOUNT`, `@@DATEFIRST`, `@@LANGUAGE`, `@@LANGID`, `@@TEXTSIZE` and `@@LOCK_TIMEOUT` read the `ParserContext` they were parsed under, so a plan one session cached answered every later session with the first one's values (`WHERE session_id = @@SPID` over a DMV found no row).
  They read `runtime.Batch.Connection` instead; a primary-constructor `ParserContext` an expression's `Run` touches is the shape to look for.
- **The session a replay reads as** — a base-table FROM source held the lock-checked scan iterator built over the *parsing* batch, so every replay probed and took row locks as the session that compiled the plan.
  A replay then read past that session's own uncommitted writes, waited with its lock timeout (and ignored its own `NOWAIT`), leaked an `UPDLOCK` read's statement-scoped U lock onto a batch that had already ended, and — once the compiling connection was disposed — raised `ObjectDisposedException` from its cancellation source on meeting another session's lock, which over TDS ended the session.
  A `FOR SYSTEM_TIME AS OF @p` source likewise evaluated its bounds against the parsing batch, answering every replay with the first execution's parameter.
  Both are now a `PerExecutionRows` — `LockCheckedScanRows` and `TemporalRowSource` — built per execution from the batch `FromSource.RowsFor` is handed; enumerating one directly throws, so a missed enumeration site fails loudly.
- **The locks a parse takes** — schema-stability locks as a name resolves, and a FROM source's table-level IS / IX / S / X — were taken by the first execution only, so a replay read through another session's `TABLOCKX` without waiting.
  The SELECT arm records them (`BatchContext.ReplayLockLog`, armed only while a cacheable top-level SELECT parses) into the cache entry beside its plan, and the replay retakes them as its own session before it runs and releases the statement-scoped ones when the statement ends, as the dispatch loop does.
- **What a plan keeps alive** — a closure built while parsing shares its compiler-generated display class with every other closure of the same method, so a runtime row source that captured nothing session-scoped still rooted the parsing batch when a sibling parse-time lambda read it.
  Every cached plan held its compiling connection alive, and an abandoned connection that had compiled one was never finalized and never reclaimed (see [`locking.md`](locking.md#abandoned-session-reclamation)).
  The fix is shape, not policy: a parse-time lambda that reads the batch moves into a static helper (`Selection.ProjectionMasks`, `Selection.TypeResolverOver`) so nothing the plan holds reaches it.
- **Settings the dispatch loop applies around a statement** — a replay under `SET FMTONLY ON` returned rows where the dispatch loop answers metadata alone, and one under `SET TEXTSIZE` returned untruncated values because the loop's post-statement walk is what stamps the client limit.
  `FMTONLY` joined the lookup gate, and the replay stamps `TEXTSIZE` itself.
- **The `OUTPUT` projection** — `OutputProjection` and its `INTO` target held the parsing batch for the rows they project and write, and cached which masks apply on the instance, so a DML plan could not be shared at all.
  Both take the executing batch per row instead, and the masks resolve per call, which only a clause over masked columns pays for.
  An `INTO` table variable is the parsing batch's own table, which a replay would have written in place of its own; the target keeps its name and finds it in the executing batch, and resolving it no longer marks the statement session-scoped.

When adding any executor or expression feature that computes per-row / per-group / per-execution values, bind them through `BatchContext` / `StatementContext` — never through fields on parse-time objects.

**The contract is checked in Debug builds.**
`PlanCacheCaptureAudit.Verify` runs at every promotion and walks everything a cached plan reaches — fields, closures' targets, iterator state machines, collections — stopping at the shared server objects a plan may hold (tables, databases, schema objects, collations, types), and throws on reaching a `BatchContext`, `ParserContext`, `StatementContext`, connection, command, transaction, `SessionToken`, security context, `VariableSlot`, undo log, `#temp` table, table variable or SERIALIZABLE fence state.
Every test that caches a plan therefore checks it, and CI's Debug leg fails on a new capture; the Release build carries no cost.
It catches a reference, not a value: state a parse *copies* out of the session — a folded constant, a setting read into a field — is what the differential tests in `PlanCacheSessionTests` are for.
The one copied value it does catch is the principal's: the parse of every statement the cache may keep is watched (`PlanCacheCaptureAudit.WatchPrincipalReads`, over reads of `SessionSecurityContext.Effective`), and a plan whose parse read it outside a step its replay repeats is refused as it enters the cache ([below](#principal-independence)).

## Principal independence

A cached plan is shared by every principal that sends its text, so it carries definitions and lists, never a decision made for the principal that parsed it; each replay makes those decisions again as its own principal.
The per-principal state a parse meets, and where each piece lives:

| State | On the plan | Decided |
|---|---|---|
| A query's read permissions (`SELECT`, a scalar UDF's `EXECUTE`, a view's broken ownership chain) | `Selection.ReferencedSecurables` / `ReadColumnsByObject` and each view's body reads | Per execution (`CheckReadSources`), replay included |
| `UPDATE` / `DELETE` / `MERGE` target permissions | The target and the written name | In the execution half |
| `INSERT`'s target permission, `CHANGETABLE`'s `VIEW CHANGE TRACKING` | A `CompiledPermissionCheck` among the recorded locks | While parsing — after the compile walk's binder errors and ahead of any row's, which is where real's Msg 229 falls (probed 2026-10-04 against SQL Server 2025) — and again by the replay at the same step |
| A query's output masks (`Selection.ColumnMasks`), a cursor's, `OUTPUT`'s, `SELECT … INTO`'s and `INSERT … SELECT`'s | `DataMask` definitions | At the sink, per execution |
| `UPDATE … SET`'s and `MERGE`'s write masks | `UpdatePlan.SetMasks` / `MergePlan.WriteMasks` as `DataMask` definitions | Once per execution, by the execution half |
| A conversion error's redaction, a variable assignment's mask, a scalar UDF's return mask | Definitions on the expression or the function | When the value is computed |
| A `NEXT VALUE FOR`'s `UPDATE`, metadata visibility in the catalog views and `OBJECT_ID`, the identity scalars | Nothing | When the expression runs |
| `EXECUTE AS` / application-role identity | Nothing — the capture audit refuses a `SessionSecurityContext` | Read from the executing session |
| A table's row-level security predicates | Nothing — the table, whose predicates each execution looks up | As the statement reads or writes, its `USER_NAME()` / `SESSION_CONTEXT` read then ([`row-level-security.md`](row-level-security.md#plan-cache-and-cost)) |
| Name resolution | Objects an unqualified name resolved to through the principal's default schema | While parsing, through the default schema the key holds (below) |

The default schema an unqualified name searches before `dbo` ([`schemas.md`](schemas.md#default-schemas-and-unqualified-names)) is the one binding a plan takes from its principal, so it is a `PlanCacheKey` component: principals sharing a default schema — `dbo` and every user declaring none, the common case — share plans, and a principal with another parses its own.
The batch takes it from the key (`BatchContext.SeedDefaultSchemaName`) rather than reading the session as it parses, and `MayCacheDmlPlan` compares it per statement, since an `EXECUTE AS` earlier in the batch changes it; a recomputation while a parse is watched is excused for the same reason.
The schema name a plan records for a permission message (`ReferencedSecurable`) is the resolved schema's.
Real keys the same way — the `user_id` plan attribute holds the default schema's id, not the user's, so two users sharing a default schema reuse one plan (probed 2026-10-04 against SQL Server 2025) — but shares a text naming nothing unqualified across every default schema (`user_id` -2), which the simulator doesn't distinguish.
`PlanCacheDefaultSchemaTests` (Tests.Internal) runs principals with different and with shared default schemas through one text in alternation, and an `EXECUTE AS` mid-batch.

`MayCacheDmlPlan` once kept DML plans to `dbo` and to simulations with no masked column, because `UPDATE` and `MERGE` settled their write masks for the parsing principal and `INSERT` checked its target as it parsed; with both following the table, every principal caches.
The `SELECT` cache never had such a gate, and a `CHANGETABLE` reference was the one parse-time check the principal-read watch found it skipping on replay: a plan `dbo` compiled answered a principal without `VIEW CHANGE TRACKING` with rows where a fresh parse refuses it.

A view body bound while the referencing statement parses reads the principal too — a FROM-less body evaluates a scalar UDF there — but nothing it settles changes by principal: any error but a missing name leaves the view's recorded columns, and execution parses the body again as the executing principal, so the watch excuses it.
The compiled-batch memo and the module plans record only whether a compile walk passed, and that walk runs in skip mode, which no permission check reads.

`PlanCachePrincipalTests` (Tests.Internal) runs two principals — logins on their own connections, `EXECUTE AS` frames on theirs, and one connection switching between them — through one text in alternation, the one able to unmask or write beside the one not, and asserts each gets its own answer from a plan recorded once and replayed by both: a masked `UPDATE`, `MERGE` and `DELETE … OUTPUT`, an `INSERT` and an `UPDATE` one principal may not make, and a `CHANGETABLE` read; `DmlPlanReplayTests` holds the same alternations to a fresh parse's transcript.

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
- Per statement: the recorded parse-time locks and permission checks are retaken as the replaying session, the read permission check reruns against its principal, and the statement-scoped locks are released when the statement ends.
- `selection.Execute(batch)` runs the cached Selection.
  `MaterializeRows()` drains them, mirroring the standard path's `LastStatementRowCount` accounting — and, like the standard path, keeping the producer's own row form (see [`data-reader.md`](data-reader.md#the-row-form-the-reader-reads)).
- Outcome shape: `SimulatedSqlResultSet` (the only shape we cache — assignment-only Selections never cache).
- `NOCOUNT` and `TEXTSIZE` are stamped on the outcome as the dispatch loop's post-statement walk would, `WriteBackOutputParameters` runs, and queued messages are placed in the outcome stream, same as the standard path.
- Each statement records its Query Store execution, under the command-text span its entry kept (`PlanCacheEntry.Spans`), as the dispatch loop records a parsed one ([`database-options.md`](database-options.md#capture)).

The replay path is also where `PlanCacheHits` increments; misses increment in `CreateResultSetsForCommand` on the fall-through.

## Capacity

Hard cap at 1024 entries.
New entries beyond cap are silently dropped: a known key is refreshed through the indexer, and a new one is added only while `planCacheCount` — maintained on each successful add, since `ConcurrentDictionary.Count` takes every lock the dictionary holds — is under the cap.
The cap is defensive — a stable EF app's working set is dozens of unique queries — and refresh-in-place via the indexer means DDL invalidation overwrites under the same key without growing the dictionary.

No LRU.
The "first 1024 unique queries" win; subsequent novel CommandTexts miss every time.
If this becomes a real problem an LRU layer can land later.

## Test observability

`Simulation.PlanCacheHits` and `PlanCacheMisses` (`long`, `Interlocked.Increment`-mutated) plus `PlanCacheCount` (the maintained entry count) are `internal` and consumed by `PlanCacheTests` to assert hit / miss behavior at boundary conditions: identical-query replay, distinct CommandTexts get distinct entries, DDL invalidation, temp-table disqualification, table-variable disqualification, distinct parameter types get distinct entries, identical parameter types with different values still hit, result correctness across hit / miss, non-SELECT batch bypass.
The sequence rules add: a multi-SELECT batch caches as one entry whose replay reproduces both result sets, a trailing semicolon still caches, a batch mixing a SELECT with an `INSERT` or a `SET` doesn't, the replay refreshes per-statement state (two `RAND()` statements draw twice, on the cached path as on the uncached one), and `@@ROWCOUNT` after a replayed sequence reads the last statement's count.
The shared-plan contract has its own section of tests there: parameterized TOP / OFFSET-FETCH replay resolves new values, RAND re-draws per execution, GETDATE reads the current clock on replay, recursive CTEs decline caching under either anchor shape, and two 8-worker concurrency tests hammer one cached aggregate / window plan asserting zero cross-execution contamination.
`PlanCacheSessionTests` (public API) replays one text across connections: a replay meeting another session's lock after its compiler was disposed times out rather than raising `ObjectDisposedException`, it doesn't read past the compiler's own uncommitted write or another session's `TABLOCKX` insert, it waits with its own lock timeout and honors `NOWAIT`, its `UPDLOCK` read holds for its own transaction and releases outside one, an RCSI replay reads its own statement's snapshot, `FOR SYSTEM_TIME AS OF @p` reads each execution's parameter, and the session-setting differential above.
`PlanCacheRetentionTests` (Tests.Internal) pins that an abandoned connection whose SELECT became a cached plan is still finalized and reclaimed.

`Simulation.DmlPlanHits` and `DmlPlanRecordings` back `DmlPlanCacheTests` (Tests.Internal): EF Core's insert, update-batch, delete, `MERGE` and trigger-table shapes (`OUTPUT … INTO @inserted0` included) and a `MERGE` from a table replay statement by statement; a `MERGE` from a query, through a view, into a table variable or onto a table with an `INSTEAD OF` trigger, a client `OUTPUT` on a triggered table, a subquery, `INSERT … SELECT`, a `#temp` target, a statement inside a block, another isolation level and a key option changed mid-batch all re-parse, while an impersonated principal replays; a schema change re-parses once and then replays again, `FREEPROCCACHE` drops the plans, parameter types keep separate plans, and an abandoned connection that recorded a plan is still reclaimed.
Whether a replay reports what a fresh parse reports is `DmlPlanReplayTests`' ([above](#dml-statement-plans)).

`Simulation.TokenMemo`'s `Hits` / `Misses` / `Count` back `TokenMemoTests`: a repeated DML batch is served on its second execution, a text carrying every token shape replays identically, each `QUOTED_IDENTIFIER` setting gets its own entry while a text that *flips* it mid-batch is never served, a tokenizer error reports the same message on every execution, the back-and-forth-lookahead shape memoizes what it parsed, a procedure body is served across invocations, and 8 workers share one sequence with no divergence.
Those tests deliberately use plan-cache-declined shapes: a bare repeated SELECT is served by the plan cache and never reaches the memo at all, which makes a memo-hit assertion over one silently vacuous.

## Performance impact

Four measurement rules every figure below follows, each learned from a number that misled:

- **One case per process.** Measuring several cases in one process made the results order-dependent by up to 2×, since whichever case ran first absorbed the tiered JIT's warm-up ("fixed text" read 28.3 µs first and 14.5 µs last).
- **Warm by elapsed time, not by an iteration count.** A single-row `UPDATE` batch read ~100 µs after 3,000 iterations and 12 µs after 100,000.
- **How a benchmark resets its table is part of what it measures.** `DELETE` leaves dead pages an insert's reuse walk visits and `TRUNCATE` doesn't, and a table that only grows makes any target scan grow with it — so measure the shape you mean, and say which.
- **Run each case in several processes and compare their allocation counts too.** String hashing is randomized per process, so a cost that depends on a dictionary's enumeration order differs between two processes of one build: a non-`dbo` `SELECT` whose permission check walked `Database.Schemas` until it met `dbo` read 3.9 µs and 10.6 KB per execution in one process and 9.0 µs and 15.8 KB in the next, and a pair of single runs compared that way can show a change that isn't there.

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

### DML statement plans

EF Core 10 `SaveChanges` through `UseSqlServerSimulator`, one case per process, each process warmed for 8 s and then timed for 6 s (median `SaveChanges`), three processes per case alternating the build without DML plans (A) and with them (B), median of the three (measured 2026-09-29):

| Case | Rows | A | B | Δ |
|---|---|---|---|---|
| Insert (`INSERT … OUTPUT` for one row) | 1 | 39.4 µs | 30.7 µs | −22% |
| Insert (`MERGE`) | 10 | 280.2 µs | 280.8 µs | none |
| Insert (`MERGE`) | 100 | 7.61 ms | 7.74 ms | none (bimodal, p25 ~1.5 ms, p75 ~12.8 ms either way) |
| Update (`UPDATE … OUTPUT 1` per row) | 1 | 35.1 µs | 26.6 µs | −24% |
| Update | 10 | 165.7 µs | 116.1 µs | −30% |
| Update | 100 | 1.55 ms | 1.09 ms | −30% |
| Delete (`DELETE … OUTPUT 1` per row) | 1 | 35.6 µs | 27.8 µs | −22% |
| Delete | 10 | 155.9 µs | 118.5 µs | −24% |
| Delete | 100 | 1.34 ms | 0.97 ms | −28% |

Inside the simulator the single-row `UPDATE` batch goes from 12.2 µs to 7.9 µs and the ten-statement one from 104 µs to 63 µs.
A `Stopwatch` split of the interleaved statement before the change put its parse half at 4.2 µs (`UPDATE`), 3.5 µs (`DELETE`) and 5.5 µs (`INSERT`) against 5.7, 3.6 and 4.5 µs of execution, which matches the in-simulator saving.
The `SaveChanges` saving is about twice that (8.5 µs for the one-row update); the difference wasn't isolated.
A 10-row `MERGE` spent about 17 µs parsing and about 450 µs executing then, which is why its plan waited until [its execution costs](#ef-cores-multi-row-insert) came down.

**The per-statement trigger lookup** is memoized per parent.
A DML statement asks two to five times whether its target carries a trigger, and each ask walked every schema's `Triggers` through `ConcurrentDictionary.Values`, which takes every bucket lock and copies the contents.
In a 10-row EF `MERGE` insert those asks were 8.2% of the simulator's time, `GetValues` alone 6.9%, most of it `AcquireAllLocks`.
`Simulation.TriggersAttachedTo` keeps the answer on the parent stamped with `SchemaVersion`, and the catalog walks that read `.Values` enumerate the dictionary instead (SSS012).
Same method, the build with neither change (A) against both (B) (measured 2026-09-29):

| Case | A | B | Δ |
|---|---|---|---|
| Insert (`MERGE`), 10 rows | 279.3 µs | 272.5 µs | −2% |
| Insert, 1 row | 30.9 µs | 28.6 µs | −7% |
| Update, 1 row | 26.5 µs | 25.4 µs | −4% |
| `OBJECTPROPERTY` / `OBJECT_ID` over `sys.objects`, 50 tables in 3 schemas (ADO.NET, one `SELECT`) | 745.9 µs | 490.0 µs | −34% |

The lookup disappears from the `MERGE` profile, but the `MERGE` stays dominated by its execution, so the EF-level saving there is small.
The catalog query gains most because `OBJECTPROPERTY` finds its object by id through every schema's nine object dictionaries, which each row copied through `.Values`.
The sqllogictest index replay (60.3 s against 61.5 s) and the `SqlServerSimulator.Tests` run (39 s either way) didn't move.

The token memo's EF-level figures above (~200 µs for a one-row insert) came from shorter runs; after a time-based warm-up the same insert measures ~39 µs, so read those as un-warmed.

### EF Core's multi-row insert

EF Core 10's `SaveChanges` for several new rows sends one batch per 42 rows: a `MERGE … USING (VALUES …) ON 1=0 … OUTPUT` for an identity key (into `@inserted0` beside a trigger, which also sends two-row batches as single-row `INSERT`s), and a multi-row `INSERT … VALUES` for a client-generated key.
Those exact texts and parameters were captured from `SaveChanges` and replayed through ADO.NET, one case per process, each warmed for half its run and timed for the other half (8 s runs, 10 s over the 5,000-row table; median batch), at 2, 10 and 100 rows (measured 2026-10-01).
With the target emptied between batches (`TRUNCATE`):

| Shape | Rows | Before | After | Δ |
|---|---|---|---|---|
| Identity key (`MERGE`) | 2 | 14.7 µs | 9.9 µs | −33% |
| Identity key | 10 | 40.6 µs | 21.5 µs | −47% |
| Identity key | 100 | 564 µs | 175 µs | −69% |
| GUID key (`INSERT … VALUES`) | 2 | 8.1 µs | 5.8 µs | −28% |
| GUID key | 10 | 35.0 µs | 16.6 µs | −53% |
| GUID key | 100 | 613 µs | 151 µs | −75% |
| Triggered table | 2 | 53.2 µs | 46.3 µs | −13% |
| Triggered table (`MERGE … INTO @inserted0`) | 10 | 94.2 µs | 65.4 µs | −31% |
| Triggered table | 100 | 866 µs | 420 µs | −51% |
| Computed + `rowversion` columns (`MERGE`) | 2 | 17.6 µs | 11.7 µs | −34% |
| Computed + `rowversion` columns | 10 | 51.6 µs | 27.2 µs | −47% |
| Computed + `rowversion` columns | 100 | 695 µs | 228 µs | −67% |

Over a table already holding 5,000 rows, the inserted ones deleted between batches:

| Shape | Rows | Before | After |
|---|---|---|---|
| Identity key | 2 | 844 µs | 28 µs |
| Identity key | 10 | 1.62 ms | 46 µs |
| Identity key | 100 | 14.7 ms | 0.52 ms |
| GUID key | 100 | 7.0 ms | 0.96 ms |
| Triggered table | 10 | 2.71 ms | 114 µs |

What the time went to, in the order the fixes took it, cumulatively on the identity key at 10 / 100 rows over the emptied target:

- **Query Store's declaration prefix searched the variables quadratically** (41 → 37 µs; 564 → 406 µs, GUID 100 rows 614 → 259 µs).
  It found each named variable's declared spelling by walking every key with a culture-aware compare; it now looks a name up ordinally first.
- **A `MERGE` read its whole target for `ON 1=0`** (406 → 325 µs at 100 rows; the 5,000-row table's 33× above).
  It decoded every target row and ran the ON per target × source pair, so the cost grew with the table, which is what the [DML-plan measurement](#dml-statement-plans-1)'s bimodal 100-row `MERGE` was.
  An ON settled non-TRUE while compiling, with no `NOT MATCHED BY SOURCE` clause, now reads no target, as real's plan doesn't ([`dml.md`](dml.md#match-strategies)).
- **`ConcurrentDictionary.IsEmpty` takes every bucket lock when the answer is yes** — the per-row uniqueness check's test for another session's pending key asked it once per row (325 → 313 µs); SSS012 now refuses it.
- **Every variable lookup built an ICU sort key** (~80 ns) to hash the name (37 → 29 µs; 313 → 241 µs; GUID 249 → 157 µs): a batch seeds, binds, reads and Query-Store-describes each parameter through `BatchContext.Variables`; `MemoizedNameComparer` memoizes the hash by ordinal text.
- **Every identifier match through a collation ran its full weight compare** (29 → 22 µs; 241 → 178 µs) — resolving `i.[Name]` against the target, the source alias and `INSERTED` once per row; `SQL_Latin1_General_CP1_CI_AS`'s compare of two equal strings also allocated two lists for its ignorable-character tiebreak.
  Identical text now answers equal before either collation family compares.
- Closures allocated per row whether or not a table had a rule or `CHECK`, a `RuntimeContext` per value or OUTPUT column, an unsized parameter dictionary, and a dedup set for a uniqueness probe that found nothing (22 → 21 µs; 178 → 174 µs).
- **Delete-and-insert churn left pages whose slot directory filled them** — each still a reuse candidate for the one dead row it couldn't hold — and every insert that missed the tail page walked each candidate's whole directory ([`heap-storage.md`](heap-storage.md)); the 5,000-row table's 100-row batch went from 6.0 ms to 0.52 ms on that alone.

After them, a thread-time profile of the 10-row identity `MERGE` put about a fifth of the batch in parsing the statement, which the [`MERGE` plan](#merge-statement-plans) then took.

### `MERGE` statement plans

Same captured texts and method as [above](#ef-cores-multi-row-insert) (8 s per process, half warm-up, median batch; two processes per case alternating the build without `MERGE` plans and with them, each case's two runs agreeing within 2%), target emptied between batches (measured 2026-10-01):

| Shape | Rows | Before | After | Δ |
|---|---|---|---|---|
| Identity key (`MERGE … OUTPUT`) | 2 | 9.9 µs | 6.8 µs | −31% |
| Identity key | 10 | 21.4 µs | 16.8 µs | −21% |
| Identity key | 100 | 171 µs | 143 µs | −17% |
| Triggered table (`MERGE … OUTPUT … INTO @inserted0`) | 10 | 66.3 µs | 55.2 µs | −17% |
| Triggered table | 100 | 408 µs | 366 µs | −10% |
| Computed + `rowversion` columns (`MERGE`) | 2 | 11.5 µs | 8.1 µs | −30% |
| Computed + `rowversion` columns | 10 | 27.0 µs | 21.8 µs | −19% |
| Computed + `rowversion` columns | 100 | 226 µs | 194 µs | −14% |

The GUID-key shape (`INSERT … VALUES`) and the triggered table's two-row batch (single-row `INSERT`s with no `OUTPUT`) were plan-cached already and didn't move.
Over the 5,000-row table (10 s runs, inserted rows deleted between batches) the 10-row identity batch went from 45.5 to 36.4 µs and the triggered one from 111 to 101 µs.
The saving is the statement's parse, a fixed cost per statement plus a share per `VALUES` row, so it reads as a third of the smallest batch and a sixth of the largest.

**The compile walk needs no plan.**
A batch that compiled cleanly is remembered under its key and schema version (`compiledBatches`), and a repeat of it skips the walk outright, so a repeated `MERGE` batch parses its statement once per run, not twice — a thread-time profile of the 10-row identity batch puts `CompileBatch` under 1%.
The walk still runs where the memo can't help — a first execution, a batch that resolved a `#temp` table, one holding an `OPTION (RECOMPILE)` inlining failure — and there a plan recorded by a run would rarely exist yet; the walk also binds on a throwaway context whose statements record nothing, so it stays a parse.

### Plans for every principal

EF Core 10's update and identity-key insert texts with a masked column in the table (`email()` on an `nvarchar(100)`, the values written from parameters), replayed through ADO.NET, one case per process, 4 s warm-up then 4 s timed (median batch), three processes per case alternating the build where `MayCacheDmlPlan` declined both principal kinds below (A) and the principal-independent one (B) (measured 2026-10-04):

| Case | Principal | A | B | Δ |
|---|---|---|---|---|
| `UPDATE … OUTPUT 1`, 1 row | a login's non-`dbo` user | 29.8 µs | 27.1 µs | −9% |
| `MERGE … OUTPUT`, 10 rows (target truncated between batches by a `dbo` session) | a login's non-`dbo` user | 44.3 µs | 27.1 µs | −39% |
| `UPDATE … OUTPUT 1`, 1 row | `dbo` | 9.2 µs | 6.3 µs | −32% |
| `MERGE … OUTPUT`, 10 rows | `dbo` | 36.5 µs | 22.1 µs | −39% |

`dbo` gains too, since one masked column anywhere in the simulation had declined every DML plan.

The non-`dbo` update still ran about three times as long as `dbo`'s, and the gap was the permission checks after all: making every check bypass (`PermissionEnforcement.Bypasses` answering true) put each non-`dbo` case below at `dbo`'s time.
Each check walked every stored permission row — a user database starts with 235, nearly all `public`'s `SELECT` on the system objects — once per object and again per column the statement named, and found the object again by id in a walk of every schema's objects to ask for its owner.
The sampling profiler attributed 29–45% of the non-`dbo` `SELECT` to `BeginQueryStoreCapture`'s `Stopwatch.GetTimestamp`, a safe point, rather than to the checks.

Kept, each by its own A/B (alternating builds, one case per process) and documented on its declaration:

- the checker reads the rows stored on each satisfier's securable from an index (`PermissionRows`) rather than walking them all — the non-`dbo` `SELECT` 19.4–22.3 → 3.9–6.2 µs, the masked `UPDATE` 17.9–23.4 → 7.3 µs;
- a check holding the object asks it for its owner (`Ownership.RegisteredOwnerId`) — the object a column target, a recorded read (`ReferencedSecurable.Securable`), a procedure or a scalar function carries — so no check walks the schemas by id, which took the process-to-process spread out of every case (the `SELECT` 3.8–8.8 → 3.8 µs, a procedure call 10.4–11.8 → 10.0 µs);
- the satisfiers a check builds are enumerated from the permission graph's links rather than collected into a list (−1.4 KB per `SELECT`);
- an object with no column-level row answers every column as it answers itself (`PermissionChecker.HasColumnRows`), so a granted object skips the per-column checks (the `SELECT` 3.7 → 3.1 µs);
- an `UPDATE` plan keeps the columns its statement reads and assigns (`UpdatePlan.ColumnTargets`) rather than visiting its expressions per execution (7.0 → 6.5 µs, −2 KB);
- a database request falling through to the login's server permissions builds its server-principal closure once for the whole covering chain (`ServerLoginRights.Implies`), which every unmasked column's refused `UNMASK` reaches (the masked `SELECT` 4.9 → 4.45 µs).

Not built: a memo of check answers per principal and securable.
It would have to follow the permission rows, role membership — which `sp_addrolemember` changes without a schema-version bump — object, schema and role ownership, and the login's server permissions and roles, for the 0.3–0.6 µs a single-row statement's checks still cost; a closure cached per principal was passed over for the same reason.
`PermissionChangeReplayTests` (Tests.Internal) holds a cached plan to a fresh parse's answers across a grant and revoke, a column deny, a role-membership change in both forms, `ALTER AUTHORIZATION` on an object and a schema, a rolled-back revoke, an ownership chain an owner change breaks, and `EXECUTE AS` / `REVERT`.

The same texts in every shape the work touched, the target table 100 rows (the `INSERT` and `MERGE` target truncated every 1,000 and 100 executions by a `dbo` session), one case per process, 3 s warm-up then 3 s timed (median batch), the two builds alternating (measured 2026-10-05).
A non-`dbo` login and an `EXECUTE AS USER` frame, before → after; an `email()` mask on the table's `nvarchar` column changed nothing for the statements that write around it (`UPDATE`, `INSERT`, `MERGE`), and a security policy on another table nothing for any case, so the masked rows below are the reads that return the column:

| Case | `dbo` | Login | `EXECUTE AS USER` |
|---|---|---|---|
| `SELECT` one row by key | 2.7 µs | 17.1 → 3.1 µs | 22.2 → 3.0 µs |
| … reading the masked column | 2.7 µs | 21.1 → 4.3 µs | 19.4 → 3.6 µs |
| EF Core's `UPDATE … OUTPUT 1`, one row | 5.9 µs | 17.9 → 6.4 µs | 17.9 → 6.4 µs |
| EF Core's identity `INSERT … OUTPUT` | 4.8 µs | 8.5 → 5.3 µs | 7.1 → 5.2 µs |
| EF Core's `MERGE … OUTPUT`, 10 rows | 19.3 µs | 25.6 → 20.0 µs | 25.8 → 19.7 µs |
| `SELECT` of 1,000 rows | 163 µs | 148 → 173 µs | 178 → 166 µs |
| … reading the masked column | 162 µs | 264 → 260 µs | 257 → 217 µs |
| `CommandType.StoredProcedure` call of a one-row `SELECT` | 9.5 µs | 12.2 → 10.1 µs | 14.4 → 10.1 µs |
| … reading the masked column | 10.0 µs | 19.8 → 12.2 µs | 18.3 → 11.3 µs |

The 1,000-row scan is within its run-to-run noise (±15%) for every principal before and after; with a masked column the remainder is masking 1,000 values, which `dbo` doesn't do.

### EF Core functional workload

Measured 2026-10-02 with four classes of EF Core 10.0.2's own `EFCore.SqlServer.FunctionalTests` — `NorthwindMiscellaneousQuerySqlServerTest` (942 tests), `GearsOfWarQuerySqlServerTest` (1,195), `GraphUpdatesSqlServerIdentityTest` (1,788) and `NorthwindBulkUpdatesSqlServerTest` (180) — one class per process, through the in-process connection (the local `.vs/efcore-shakedown` harness's `EFSIM_INPROC=1` mode) and once over the TDS endpoint.
The plan cache is not where that workload's time goes; this records where it does, because the answer decided which passes changed.

**The simulator is the minority of a class's wall time.**
A class run is 14–41 s, of which about 10 s is the test platform discovering the suite's 51k tests, and in the query classes 11–20 s is the JIT compiling the dynamic methods EF Core builds for its shapers and the tests' expected results.
The simulator's own share was isolated by capturing every ADO.NET call a class made and replaying the stream against a fresh `Simulation` in a process of its own (a scratch harness, not in the repo): cold — one replay, JIT included, which is what a class run pays — and warm, a later pass in the same process.

| Class | Run, before → after | Simulator cold | Simulator warm |
|---|---|---|---|
| Northwind miscellaneous | 41.3 s → 38.7 s | 5.60 s → 4.64 s | 2.86 s → 1.06 s |
| Northwind bulk updates | 14.9 s → 13.6 s | 3.80 s → 3.08 s | 1.45 s → 0.68 s |
| Graph updates | 23.1 s → 23.1 s | 2.84 s → 2.91 s | 0.67 s → 0.65 s |
| Gears of War | 28.6 s → 28.8 s | 4.23 s → 4.23 s | 3.13 s → 3.13 s |

Gears of War's 3 s is real time: its seed script waits `WAITFOR DELAY '00:00:03'` for full-text population, which the simulator honors.
The cold-minus-warm gap, 2.2–2.8 s in every class, is the runtime's tiered JIT over the simulator's code — about 0.6 s compiling some 4,500 methods at tier 0, the rest running them unoptimized — and dominates the graph-updates class, whose 19k commands cost ~30 µs each warm.
A replayed run checks behavior too: each command's results hash into a digest, and every kept change left all four digests byte-identical.

Four passes were kept, each measured by replay A/B (alternating builds, one case per process), and each documented on its declaration:

- the scan prefilter pushes `[NOT] LIKE` (`IsSourceLocalLike`), so a `LIKE N'A%'` leftmost source of a cross join feeds its 30 rows to the join rather than 689k tuples;
- a joined `UPDATE` / `DELETE` prefilters its target (`PrefilterMutationTarget`), so EF Core's `ExecuteDelete` over a navigation runs its `APPLY` body only for target rows the WHERE keeps;
- the semi-join switch keys EF Core's NULL-matching correlation (`TryClassifyNullMatchingCorrelation`);
- `Token.LineAt` counts with a vectorized scan — every statement asks for its starting line, so a long seed script paid a scan of its prefix per statement.

**The sampling profiler misled repeatedly.**
EventPipe's sampler stops a thread at a safe point, so a sample lands where the code polls, not where it spends: `Thread.PollGC`, `Monitor.Enter_Slowpath`, `Array.Copy`, `DateTime.UtcNow` and `Stopwatch.GetTimestamp` led the leaf lists while the work sat in their callers.
Three changes chasing such leaves measured flat and were dropped: `SourceColumnMemo` growing by doubling with a next-slot cursor (allocation −8%, time ±1%), `LockManager` pulsing its gate only when a waiter waits (0%), and settling a sorted projection's `NEXT VALUE FOR` walk once per plan (±1%, where the sampler said 4%).
What the replay's per-statement timing found instead — a handful of statements carrying most of two classes' warm time — is what the kept passes answer.

Two runtime-level levers were measured and are not the library's to pull: ReadyToRun-compiling the simulator cut the cold replay by ~0.5 s in two classes and nothing in the third, but slowed the warm passes 8–30% and doubles the assembly (7 MB → 14.5 MB); `DOTNET_TieredPGO=0` cut the graph-updates cold replay 28%, a setting of the consumer's process.

Over TDS a `SELECT 1` round trip is ~180 µs against ~8 µs in process (real SQL Server 2025 on the same machine: ~320 µs), which is the whole difference in the one class it shows in: graph updates runs ~11 s of tests in process and ~17 s over the wire, while the query classes, whose time is EF Core's own, run the same either way.

#### Second pass: the simulator inside the test process

Measured 2026-10-03 over the same four classes plus `ComplexNavigationsQuerySqlServerTest` (622 tests) and `JsonQuerySqlServerTest` (445), with a third measure beside the replay's two: the time a class run spends inside the simulator's own calls (execute, read, next result, close), summed by a timing wrapper around the in-process connection.
That in-process figure is the one the earlier replays understated.

| Class | In process, before → after | Replay cold | Replay warm |
|---|---|---|---|
| Northwind miscellaneous | 3.36 s → 3.00 s | 4.45 s → 3.51 s | 1.08 s → 0.93 s |
| Northwind bulk updates | 2.90 s → 2.12 s | 2.84 s → 2.32 s | 0.63 s → 0.56 s |
| Graph updates | 3.49 s → 3.30 s | 2.75 s → 2.67 s | 0.62 s → 0.62 s |
| Gears of War (less its 3 s `WAITFOR`) | 1.08 s → 1.09 s | 1.24 s → 1.19 s | 0.15 s → 0.15 s |
| Complex navigations | 0.79 s → 0.76 s | 0.94 s → 0.93 s | 0.29 s → 0.26 s |
| JSON query | 0.58 s → 0.59 s | 0.74 s → 0.73 s | 0.18 s → 0.17 s |

The replay's cold column now includes constructing the `Simulation`, which the earlier table's didn't; every replayed result hashed identically before and after.

**Inside the test process the simulator runs mostly unoptimized.**
Graph updates spends 3.5 s in the simulator in process against 2.6 s for a cold replay and 0.6 s warm, and a JIT trace of the class run shows why: of the ~2,900 simulator methods it compiles, 187 reach the optimized tier and 1,730 stop at the instrumented copy the runtime builds on the way there.
A method only starts counting its calls toward tier-up once no new method has been compiled for 100 ms, and EF Core compiles methods for every query it shapes, so in a test process that pause rarely comes.
Two process settings confirm it and stay the consumer's to choose: `DOTNET_TC_CallCountingDelayMs=0` cut graph updates from 3.5 s to 1.7 s in the simulator and from 23.5 s to 16.3 s of wall time, and `DOTNET_TieredPGO=0` to 2.3 s and 17.9 s.
The library's own lever is `[MethodImpl(Tiering.OptimizeFirstCall)]` (`MethodImplOptions.AggressiveOptimization`), now on twenty methods of per-statement and per-row bookkeeping — lock grant and release, the seek cache's entry resolution and rebuild, I/O statistics, the name memo, statement begin — chosen because they make no virtual or delegate call, the only thing the profile-guided recompile they give up would have bought them.
Measured alone it took 4% off graph updates and 6% off Northwind in process and 13% off the Northwind cold replay, at the price of ~10 ms of optimized compilation per process; the warm replays moved under 1%.
A wider set taking in the join and seek planners, whose expression trees call virtually, took 6–7% in process but slowed the warm replays 5–11%, so it was dropped.

**Startup** — a bare `new Simulation()`, connection, `SELECT 1`, `sys.databases` probe and a short CREATE / INSERT / SELECT script in a fresh process — went from ~398 ms to ~368 ms, and constructing the `Simulation` from ~131 ms to ~110 ms.
The constructor's share was mostly the type initializers: `Simulation`'s 43 KB of static-field IL (23 ms to compile before it ran), half of it the datatype-info tables' 3,000-odd boxed cells, a `RegexOptions.Compiled` collation-name pattern (emitted and compiled at startup, which also made `CreateDbConnection` cost 20–60 ms by chance of timing), and `BuiltInResources`' built-in permission table.
The datatype-info tables and the permission table moved into types of their own, initialized on first read, and both regexes became `[GeneratedRegex]`; a catalog view's seek-column ranking dropped the LINQ sort a first catalog read compiled for it.
Part of the constructor's gain reappears in later statements, which now compile the BCL code the compiled regex used to compile first, so the whole-script figure is the honest one.
The first command of every class, the fixture's `sys.databases` probe, still costs ~160 ms cold: building every catalog view (~50 ms, about half of it compiling the `Register*` methods) and compiling the parse and execute path.
What remains of the cold cost is a long tail — ~5,400 methods at ~0.1 ms each for Gears of War, whose cold replay is 55% compilation.

Kept, each measured by alternating builds one case per process:

- an uncorrelated deferred source inside a per-row body is materialized once per statement rather than once per enclosing row (see [`joins.md`](joins.md#deferred-sources-materialize-once-per-enumeration)) — EF Core's `ExecuteDelete` over a navigation, 91 → 42 ms, and the bulk-updates class 21% warm;
- ORDER BY elimination over a join sorted by its leftmost source's unique key ([`indexes.md`](indexes.md#order-by-elimination)) — the three-way cross join `TOP (1)` from ~92 ms to ~31 µs, Northwind 16% warm;
- the seek journal finds its replay start by binary search (`Heap.FirstSeekJournalEventAfter`) instead of walking all 512 events, the third cost the first pass left — the Northwind seed's inserts 170 → 160 ms cold;
- the tiering attribute and the startup changes above.

Measured flat and dropped: the name memo (`SourceColumnMemo`) starting its scan after its last hit, and again growing by doubling over immutable entries (allocation −4% per statement, time unchanged in replay and in process) — the sampler put an eighth of graph updates' simulator time in it, which was its safe-point bias rather than the memo's work; building the eight non-default Latin1 weight tables lazily (2 ms); and moving `BuiltInResources`' other large tables out of its initializer, worth ~7 ms on the first catalog read, left in the backlog.

## Not modeled / future

- **DML plans for the declined shapes** — `INSERT … SELECT`, a `MERGE` from a query, the joined `UPDATE` / `DELETE` forms, DML through a view, and a statement holding a subquery.
  Each needs its own split point, and a subquery's plan its closures moved off the parse (the `OuterTypeResolver` a nested query captures reaches the `ParserContext`).
- **`SET` / `DECLARE` as recordable effects**, which is what a batch mixing them with a SELECT would need to cache as a SELECT sequence; the DML statement plans don't need it, since they sit beside statements that still parse.
- LRU eviction, for both layers (the cap is hard FIFO-ish, and a one-shot migration script run first can fill it ahead of the steady-state working set).
- Parameter-sniffing-style value-dependent plan selection (the simulator has no cost-based optimizer, so this doesn't apply).
- A memo entry per *distinct* text is stored on first sighting, so a workload of unique texts fills the cap with entries nothing will read again.
  Two-phase admission (store on second sighting) would keep the cap for texts that repeat.
