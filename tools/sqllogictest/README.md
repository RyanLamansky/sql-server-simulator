# sqllogictest differential harness

A C# console runner that plays SQLite's sqllogictest scripts against the simulator and a live SQL Server 2025 side by side, and diffs the two in both directions.
Why the live server is the oracle, and the methodology traps any differential harness falls into, are in [`docs/claude/sqllogictest.md`](../../docs/claude/sqllogictest.md).

The tooling is committed here; the data is not.
The corpus extracts to over a gigabyte and a capture's references run near 100 MB, so both live in a data directory that is gitignored: `$SLT_DATA`, or `.vs/sqllogictest` under the repo root.
Every path a script or file list names is relative to that directory.
The runner is in the solution, under a `tools` folder, so every build and CI compile it; it isn't a test project, so `dotnet test` never runs it, and its own `Directory.Build.*` files keep the repo's analyzers off it.

## Setting up on a new machine

1. **Fetch the corpus**: `./tools/sqllogictest/fetch-corpus.sh`.
   It downloads the GitHub mirror of SQLite's Fossil repository (`https://github.com/gregrahn/sqllogictest`) and extracts `test/` — the `.test` scripts — beside the archive.
   The `random/` slice the sweeps use is 391 scripts and 5,295,251 records.
2. **Point it at a SQL Server 2025**: set `SLT_SERVER` to a SqlClient connection string naming the server and credentials, for example `Server=localhost,1433;User ID=sa;Password=…;TrustServerCertificate=True;Encrypt=True`.
   The runner sets the catalog, timeout and pooling itself.
   The login needs to create and drop databases; each shard works in its own `slt_work_<n>`.
3. **Capture a reference**: `./tools/sqllogictest/sweep-parallel.sh lists/sweep-random.txt results-capture 16 refs/random`.
   Every record runs on both engines, and each script's outcomes on real are written to `refs/random`.
   It took about seven minutes over 16 shards against a local server.
4. **Replay**: `./tools/sqllogictest/replay.sh`, from then on, whenever you want the sweep.
   It runs the simulator alone, in-process, against the reference, in about a minute, and needs no server.

Both scripts build the runner in Release first, and its project reference rebuilds the simulator along with it, so a run always measures the working tree.

## The three modes

- **Differential** (the default): every record runs on real and on the simulator and the two are diffed.
  This is the oracle, and it is slow: most of the wall time is spent waiting on the server, so it scales by sharding across processes (`sweep-parallel.sh`), not threads.
- **Capture** (`--capture <ref-dir>`): a differential run that also writes one reference file per script, with the same comparison and summary.
- **Replay** (`--replay <ref-dir>`): the simulator only, in-process, diffed against the reference.
  No connection is opened on this path — no server, no per-script `DROP` / `CREATE DATABASE`, no client driver — and scripts run in parallel inside the one process.

Replay proves **self-consistency, not fidelity**.
It catches simulator regressions against a frozen snapshot of real's answers; it can't see real changing, or both engines having been wrong together when the reference was captured.
Re-capture when a new SQL Server build ships — the captured `@@VERSION` is in every reference header and in the replay summary's `mode=` line — or when the corpus or a file list changes.

A replay is **clean when its `findings.jsonl` is empty**.
The summary still tallies the known-divergent records (see below) under their ordinary classes; they aren't new.

## Running the runner directly

```
dotnet tools/sqllogictest/runner/bin/Release/net10.0/SltRunner.dll --files <list> --out <dir> [options]
```

Run it from the data directory.

| Option | Meaning |
| --- | --- |
| `--files` | Newline-delimited list of `.test` paths (`#` comments allowed). |
| `--out` | Output directory: `findings.jsonl` and `summary.txt`. |
| `--ignore-halt` | Run past `halt` records. The corpus halts non-SQLite engines out of its SQLite-only sections; ignoring that adds dialect noise but also extra probing surface. The sweeps pass it. |
| `--emit-cap` | Maximum findings logged per (script, class). Counts in the summary stay exact. |
| `--max-records` | Cap on records per script. |
| `--db` | Work database on the live server (one per shard). |
| `--capture` | Also write a replay reference. |
| `--shard-tag` | Suffix for this process's part of the known-divergent register. |
| `--replay` | Replay against a reference instead of the live server. |
| `--parallel` | Replay only: scripts run concurrently in-process (default: the core count). |

`--repl <file>` is an ad-hoc probe mode: `;;`-separated batches, run on both engines and printed.
`SLT_DIAG=1` also drains the simulator's reader row by row and reports each result set's shape; `SLT_NONQUERY=1` also compares `ExecuteNonQuery` return values.

The file lists under `lists/` are `sweep-random.txt` (the whole `random/` slice, what the sweeps run) and the pilot lists the harness was built against: `pilot-phase1.txt` (the `select1`–`select5` scripts plus `evidence/`), `pilot-phase2.txt` (a seeded sample of `index/` and `random/`), `pilot-all.txt` (both), and `pilot-capture.txt` (the capture/replay pilot).

## Per-record protocol

Each script gets a fresh database on both sides: `DROP` / `CREATE DATABASE` on real, a new `Simulation` and connection on the simulator.
Each record's SQL runs on real first, then on the simulator, through identical ADO.NET code.
The reader is drained with `Read()` even when `FieldCount` is 0 — the simulator raises a row-returning statement's error on the first `Read`, so skipping `Read` on an empty result set would silently turn errors into successes.
Replay runs the identical drain against the identical simulator API; only real's side comes from the reference instead of from the wire.

## Outcome classes

| Class | Meaning |
| --- | --- |
| `q:match` / `q:match_after_sort` | Agreement; the second for `rowsort` / `valuesort` records, where order isn't asserted. |
| `q:ORDER_DIFF_nosort` | Order differs on an order-asserting record. |
| `q:VALUE_DIFF` / `q:row_count_diff` / `q:clr_type_diff` / `q:store_type_name_diff` | The named axis differs. |
| `q:float_epsilon_diff` | Values differ only within 1e-9 relative. |
| `q:` / `s:OVERPERMISSIVE_real_error_sim_ok` | Real rejects, the simulator accepts — the direction that matters most. |
| `q:` / `s:sim_error_real_ok` | The simulator rejects, real accepts. |
| `q:sim_timeout_perf` | The simulator hit its command timeout where real answered. |
| `q:` / `s:both_error_same_msg` | Both rejected with the same message number: dialect noise, or positive fidelity evidence. |
| `q:` / `s:both_error_diff_msg` | Both rejected, with different numbers — inspect. |
| `s:rowsaffected_diff` | `RecordsAffected` differs. |

Replay adds four:

| Class | Meaning |
| --- | --- |
| `known_divergent_match` | The record diverged exactly as the reference says it did; the ordinary class is still tallied, but nothing is logged. |
| `known_divergent_changed` | A frozen divergence changed class, changed error, or now agrees — always logged, never emit-capped. |
| `newly_divergent` | A record the reference had clean now diverges. |
| `ref_truncated` / `stale_reference` | See the alignment guard below. |

## Taint

A `tainted` flag is set once a *statement* record fails to succeed cleanly on both engines, including when both reject it: a multi-statement record that errors on both sides can still have applied a different prefix on each, wherever one engine raises while compiling the batch and the other only when the statement runs.
Every later finding in that script carries the flag.
Tainted findings are consequences of divergent table state, not independent defects, and need re-verifying from a clean state before they're reported.
Replay computes taint at replay time from the reference's outcome and the simulator's, as the differential run does from real's; it isn't frozen at capture, so a simulator change that stops tainting a script changes the taint of everything after it.

## The reference format

One gzipped binary file per script, mirroring the script's relative path with a `.ref` extension (`refs/random/test/random/select/slt_good_0.ref`).
The header holds the format version, the capture time, the server's `@@VERSION`, whether `--ignore-halt` was in effect, and the script's path.
Then comes one entry per parsed record, in parse order, holding exactly what the comparison discriminates on:
- the record's line, kind and a 32-bit FNV-1a hash of its SQL, which is the alignment guard;
- a skipped marker for records the engine conditionals exclude, which still occupy their slot;
- real's outcome: success, error number, kind and message (truncated to 400 characters), `RecordsAffected`, and for a successful query the row count, field types, data type names and every row's rendered values.

Values are stored in produced order rather than sorted, so replay runs the same two-stage comparison the differential run does and reproduces `q:match` versus `q:match_after_sort` record for record.
Each entry also carries whether real's canonical rendering matched the script's stored expected values, since replay holds only rendered strings and couldn't recompute it.
A trailer records why the capture stopped early, if it did.

**The known-divergent register.**
Any record that diverges at capture is marked in its reference (its divergence classes and the simulator's error) and appended to `<ref-dir>/known-divergent.jsonl`.
The register is a census, not a sample — no emit cap — and it is written by the capture, never by hand.
A sharded capture writes one part per shard, which `sweep-parallel.sh` concatenates.
At replay a record that diverges identically tallies `known_divergent_match` and logs nothing; one that changed tallies `known_divergent_changed` and is always logged.
The random slice's register held five records (three Msg 8134, two Msg 8115), each a divergence real's own plan choice makes irreducible.

**The alignment guard.**
Replay re-parses each script and checks every record against its reference entry: line, kind, SQL hash and skipped state.
A mismatch, a script that lost records, or a missing reference file abandons that script with a `stale_reference` finding, prints `STALE REFERENCES` in the summary, and exits 2 — nothing is silently absorbed.
The one exception is a reference the capture itself cut short (a simulator timeout, or `--max-records`): replay stops there and tallies `ref_truncated`.

**Parallelism.**
Each script gets its own `Simulation` and connection, so scripts are independent by construction.
The runner hands them to dedicated threads rather than pool threads — each record's execution already occupies a pool thread through the timeout wrapper, and pool starvation would look like spurious simulator timeouts.
Per-script state folds into the global counters under one lock when the script ends, which is also when its findings are appended.

## Timing

Measured in the 16-core dev container (2026-08-03); measure locally rather than trusting these.
The full `random/` slice took 424 s as a 16-shard differential capture and 34 s as a `--parallel 16` replay, and the replay reconciled exactly with the capture: every class tally identical, the five register records all `known_divergent_match`, `findings.jsonl` empty.
Server GC beat workstation GC for replay at `--parallel 16` (3.3 s against 4.2 s on the pilot list), so `replay.sh` exports `DOTNET_gcServer=1`; the csproj pins workstation GC for the differential sweep, where each single-threaded shard process wants one heap.
Replay is simulator-bound end to end, which also makes it an honest profiling workload for the simulator itself.

## Known limitations

- A simulator query that outruns its command timeout and doesn't honour it is abandoned after a hard 40 s wait; its thread keeps spinning for the rest of the process and the script stops there, so timing from a run with timeouts is pessimistic.
- `findings.jsonl` is emit-capped per (script, class), so it is a sample; the summary's counts and the known-divergent register are complete.
- A capture that stops early freezes that stopping point: replay reports `ref_truncated` where the simulator gets further, and can't compare past it.
- Replay can't see anything real does that the capture didn't record.
  Adding a comparison axis means bumping the reference format version, which replay then refuses to read as stale.
