# sqllogictest as a differential oracle

SQLite's sqllogictest corpus — **7,195,342 query and 225,371 statement records** across 622 machine-generated scripts — replayed against the simulator and a live SQL Server side by side, diffing both directions.
It reaches shapes no application emits: deep operator nesting, join-order permutations, sign chains, overflow edges, and a large body of `statement error` records that probe the *rejection* boundary rather than the answer.

Neither the corpus nor the harness is checked in — the corpus is downloadable and the runner is local tooling under `.vs/`, with provenance, invocation and flags documented beside it.
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
  Real compiles a whole batch before running any of it while the simulator dispatches statement by statement, so a multi-statement record that errors on *both* engines can still have applied a different prefix to each.
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
The oracle then splits into two harnesses with different jobs:

- **Capture** runs once in differential mode and writes a per-record reference: value rendering, CLR type, store type name, error number, rows-affected — everything the comparison discriminates on — canonicalized per the corpus's own sort modes so order-insensitive records don't flap.
- **Replay** runs the simulator **in-process only**, with no client driver, server or database lifecycle, and diffs against that reference.

Replay proves **self-consistency, not fidelity**: it catches simulator regressions, and cannot catch real changing or both engines being wrong together.
Differential mode stays as the periodic check.

**Re-capture when a new SQL Server version ships**, not routinely, and stamp the captured build into the reference so a version difference surfaces as a mismatch rather than silently.
A frozen reference is only dangerous when it outlives the behavior it recorded — which is exactly what happened to the corpus's own 2008 expectations.
Anything still divergent at capture time is frozen in as permanent expected divergence, so reaching zero first is a precondition rather than a nicety; record known-irreducible cases as explicitly known-divergent rather than as expected values, so a change in that set stays visible.

**Replay is also the only honest profiling workload.**
A sampled trace of a differential run attributes ~87% of wall time to waiting on the server, with under 4% of samples in simulator frames — so optimizing the simulator cannot meaningfully speed a differential sweep, and parallelism is what does (measured ~3,270 s to ~460 s at 16 shards).
Replay is simulator-bound end to end.

## Standing result

The `random/` slice (391 scripts, 5,295,251 records) sits at **5 divergent records**, all `sim_error_real_ok` — the simulator raises where real answers, never the reverse.

Each is demonstrated irreducible, with a probe showing real's own answer flipping under something no semantics-preserving rule can reproduce: two are the trivial-plan boundary (the statement raises as written and returns no rows once `DISTINCT`, `GROUP BY`, `TOP 2` or a join is added, while `ORDER BY` / `MAX()` / `COUNT(*)` leave it raising), one is written order inside an un-negated `IN` list (`x IN (x/0, x)` answers, `x IN (x, x/0)` raises), and two are per-row short-circuiting that flips with the *data* rather than the text.

The same comparison in a **`HAVING`** folds unconditionally, because a HAVING always carries a grouping and so never gets the trivial plan — which is why that position is modeled and `WHERE` is not.
The per-shape evidence is in the "Not folded yet" list in [`query.md`](query.md); the rules the sweep did close are in the "Compile-time predicate folding" section above it.

Re-run the sweep after any bundle touching the parser, the expression evaluator or the type system.
