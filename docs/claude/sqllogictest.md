# sqllogictest as a differential oracle

SQLite's sqllogictest corpus — **7,195,342 query and 225,371 statement records** across 622 machine-generated scripts — replayed against the simulator and a live SQL Server side by side, diffing both directions.
It reaches shapes no application emits: deep operator nesting, join-order permutations, sign chains, overflow edges, and a large body of `statement error` records that probe the *rejection* boundary rather than the answer.

The harness is committed under [`tools/sqllogictest/`](../../tools/sqllogictest/README.md), whose README covers setting it up on a new machine and running it; the corpus and the captured references are downloaded and regenerated into a gitignored data directory rather than checked in.
This file records only what would have to be re-derived rather than re-downloaded: why the oracle is built this way, and the methodology traps that produced wrong conclusions in practice.

## The corpus's own expected results are not the oracle

Each script ships expected results, but they are **2008-era canonicalizations produced by SQLite**, cross-validated against SQL Server *2005*.
They disagree with SQL Server 2025 wherever T-SQL is simply different — the unary operators bind looser than `*` `/` `%`, for instance, so the corpus agrees with the simulator's *pre-fix* behavior and real is the outlier.
Trusting the file there would have entrenched that bug instead of revealing it.

So a live server is the oracle and the stored results are a tie-breaker signal only: worth tallying, because "both engines agree with each other and differ from the file" is interesting metadata, but never authoritative.

**Diff both directions.**
The two deltas mean different things, and the second matters more:

- **simulator differs from real** — an ordinary fidelity gap.
- **real rejects, simulator accepts** — the over-permissive direction, where a query passes here and fails in production; see the register in [`backlog.md`](backlog.md).

## Methodology traps

Each of these produced a wrong conclusion before it was understood, and each generalizes to any differential harness:

- **A swallowed exception reports as a clean run**, fabricating entries in the over-permissive class specifically — the one class the exercise exists to find.
  The simulator materializes a row-returning statement's error on the first `Read` rather than at `ExecuteReader`, so a comparison loop that skips `Read` when `FieldCount == 0` never sees it and records success.
- **A capped findings log reports presence, never magnitude.**
  Per-(script, class) emit caps make the log a sample; only the summary carries counts.
  Reading the log as a count inflated a single root into "340 findings" once and "100" another time.
- **State divergence has to taint the rest of a script.**
  A multi-statement record that errors on *both* engines can still have applied a different prefix to each: they part wherever one raises an error while the batch compiles and the other only when the statement runs.
  Without propagating that, every later mismatch reads as an independent wrong-answer bug — two "wrong `SUM`" findings were exactly this.
- **Order-insensitive records must compare as sets.**
  The corpus's `rowsort` / `valuesort` modes do not assert order; treating a permutation as a divergence produced ~1,000 false positives.
- **Compare more than values.**
  CLR type, store type name, error number and rows-affected each surfaced real divergences; one type-only class was 29,394 records.
  A values-only comparison would have called that run clean.
- **A harness that references the simulator by path holds a *copy*.**
  Rebuilding the simulator alone leaves it stale and the sweep measures old code — which looks exactly like a fix that did not work.
  Rebuild both, always.

## Capture and replay

Once the differential count reaches zero, a live server is no longer needed to *supply* answers — only to detect that real has changed.
The oracle splits into two harnesses with different jobs:

- **Capture** is a differential run that also writes a per-script reference file: error number and kind, rows-affected, CLR and store type names, and the rendered values — everything the comparison discriminates on.
  Values are stored in produced order rather than sort-canonicalized, because replay re-runs the same two-stage comparison (exact, then per the record's sort mode) and canonicalizing at capture would collapse the order-asserting and order-insensitive agreement classes into one.
- **Replay** runs the simulator **in-process only**, with no client driver, server or database lifecycle, and diffs against that reference, parallelized across scripts inside the one process.

Replay proves **self-consistency, not fidelity**: it catches simulator regressions, and cannot catch real changing or both engines being wrong together.
Differential mode stays as the periodic check.
The throughput gap is what makes replay the routine sweep: the differential run is ~87% server wait, and the same slice that takes a 16-shard differential sweep minutes takes an in-process 16-thread replay well under a minute, at two orders of magnitude more records per second.

A reference that no longer describes its script must refuse, not drift: every record carries a fingerprint of its SQL, and any mismatch — text, kind, line, skip state, or record count — abandons the script as stale rather than comparing misaligned slots.
A bumped reference format version refuses old files the same way.

**Re-capture when a new SQL Server version ships**, not routinely; the captured `@@VERSION` is stamped into the reference and echoed in every replay summary so a version difference surfaces rather than silently aging.
A frozen reference is only dangerous when it outlives the behavior it recorded — which is exactly what happened to the corpus's own 2008 expectations.
Anything still divergent at capture time is frozen in as permanent expected divergence, so reaching zero first is a precondition rather than a nicety; the capture records each such case in an explicit known-divergent register rather than as expected values.
Replay reconciles every register entry: an identical divergence tallies as known-divergent-match and is not a finding, while one that *changed shape* — or a clean record that starts diverging — is always reported.

**Replay is also the only honest profiling workload.**
A sampled trace of a differential run attributes ~87% of wall time to waiting on the server, with under 4% of samples in simulator frames — so optimizing the simulator cannot meaningfully speed a differential sweep, and parallelism is what does (measured ~3,270 s to ~460 s at 16 shards).
Replay is simulator-bound end to end.

## Standing result

The `random/` slice (391 scripts, 5,295,251 records) sits at **5 divergent records**, all `sim_error_real_ok` — the simulator raises where real answers, never the reverse.

Each is demonstrated irreducible, with a probe showing real's own answer flipping under something no semantics-preserving rule can reproduce: two are the trivial-plan boundary (the statement raises as written and returns no rows once `DISTINCT`, `GROUP BY`, `TOP 2` or a join is added, while `ORDER BY` / `MAX()` / `COUNT(*)` leave it raising), one is written order inside an un-negated `IN` list (`x IN (x/0, x)` answers, `x IN (x, x/0)` raises), and two are per-row short-circuiting that flips with the *data* rather than the text.
**Settled — don't re-pitch:** real itself doesn't guarantee any of the five answers — each is an accident of the plan it happens to pick (trivial or not, which `IN` element it reaches first, which conjunct its cost model evaluates first per row), so a rule reproducing one would be pinning that accident.

The same comparison in a **`HAVING`** folds unconditionally, because a HAVING always carries a grouping and so never gets the trivial plan — which is why that position is modeled and `WHERE` is not.
The per-shape evidence is in the "Not folded yet" list in [`query.md`](query.md); the rules the sweep did close are in the "Compile-time predicate folding" section above it.

The `index/` slice (213 non-empty scripts, 2,114,262 records) had **no divergent record** in its first whole differential run (2026-09-27, SQL Server 2025): every query matched, every statement succeeded on both engines, and nothing timed out.
It is the one slice whose records scan hundreds to thousands of rows, so it is the better profiling workload for scans and predicate evaluation; per-row work shows there that `random/`'s per-statement fixed cost hides.

The `evidence/` slice (12 scripts, 494 records of which 112 are excluded by the engine conditionals, 2026-09-27, SQL Server 2025) is the one written feature by feature, much of it for other engines, so over half its records are rejections both engines raise, and they have to raise the *same* error to agree.
Its first capture had one divergent record, SQLite's `group_concat(DISTINCT x, ':')`: real reads any `name(DISTINCT …)` as an aggregate call before it knows the name, so it was Msg 102 at the comma there and Msg 195 here.
Fixing that exposed the whole quantified-call family and, beside it, `[abs](1)` running here where real refuses a delimited one-part name as a function — see [`query.md`](query.md#a-quantified-call-is-an-aggregate-call-whatever-the-name) and [`grammar.md`](grammar.md#a-delimited-one-part-name-doesnt-call-anything).
The slice now agrees record for record, and replays in under a second as part of the routine replay.

The top-level `select1`–`select5` scripts have no captured reference yet, so the routine replay doesn't cover them; a differential run of `lists/pilot-phase1.txt` (those five plus `evidence/`) had every query agree with real (2026-09-27, SQL Server 2025), `select5`'s many-way comma joins included.

Re-run the sweep after any bundle touching the parser, the expression evaluator or the type system.

## Other differential oracles

Four more harnesses run real applications' and tools' SQL against the simulator's TDS endpoint and against SQL Server 2025 side by side: Django 5.1's ORM test suite through mssql-django, EF Core's own SQL Server functional suite, an SMO property-and-script drain, and hand-written edge-case corpora compared case by case.
Each is local-only under the gitignored `.vs/`, with its own README for running it; their open findings live in the feature docs and the [backlog](backlog.md).
What carries over from them:

- **The bar is parity with real, not a clean run.**
  Many Django and EF tests fail on real too, so the target is that the simulator fails exactly the set real fails.
  Take the delta both ways — `comm -23` of the sorted failing-test names for simulator-only, `comm -13` for real-only — since the real-only set is the over-permissive direction, and a "matches real" claim needs both empty.
- **Group failures by cause, not by test.**
  One statement the simulator couldn't run once killed the TDS connection and failed every later test in its class, 27 of 50 failures at the time; a test that leaves state behind fails every later one in its cleanup.
  Counted by cause, Django's first hundreds of failures were eleven roots, the largest a qualifier-blind name resolver that silently bound to the wrong same-named column.
- **Re-run the oracle that found a bug rather than reconstructing it from its description.**
  A hand-built probe that passes is not proof a finding is stale: one was once deleted on the strength of a wide matrix of hand-written `OUTPUT … INTO` shapes that all passed, when the trigger was a destination-column type mismatch none of them had.
- **Give each probed claim its own batch.**
  A probe that creates a table and queries it in one batch fails real's compile-time column resolution (Msg 207) for reasons unrelated to the claim, and reads exactly like the divergence being looked for.
- **An existing test is not evidence of real's behavior.**
  EF Core's suite corrected two of this repo's tests that had pinned unprobed behavior (a table after `APPLY` refused, `ALTER COLUMN` keeping a declared collation); hand-written tests had agreed with the simulator there.
- **Two runs sharing one database or one endpoint wedge each other on locks**, which reads exactly like a simulator blocking bug — run the two sides one at a time.

Standing results, each the last full run:

- **Django** (2026-10-02): the 132-app ORM slice (9,295 tests), `schema` and 70 further apps (7,263 tests) — the whole suite but GIS, PostgreSQL and two apps whose migrations fail on both — fail identically on both engines: 0 simulator-only, 0 real-only.
- **EF Core** `v10.0.2` (2026-10-02): of 51,446 results real fails none and the simulator 2, both asserting the order of rows an `ORDER BY` leaves tied, which is settled as a plan accident (see the backlog's row-order entry).
- **SMO** (2026-10-02): no simulator bug among the non-matching rows; what is unbuilt is listed in the backlog's TDS section.
