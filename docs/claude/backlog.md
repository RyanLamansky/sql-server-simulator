# Backlog

Forward-looking work list: missing features, fidelity gaps in shipped behavior, and design choices worth revisiting. **Not a checklist** — completed work is removed, not ticked.

Ordering within each section leans toward predicted importance (popularity × ease, the Operating-goal weighting in [`../../CLAUDE.md`](../../CLAUDE.md)), but is **explicitly non-authoritative**: anything here is valid to pick up, and so is anything *not* here.

## Completion process

When an item ships:

1. **Remove it from this file.** No checkmarks, no archive section — git records *what* changed; this file is only the open list.
2. **Ensure a `docs/claude/` deep-dive documents it** — explicit function/feature names, operational structure, probe-confirmed quirks and divergences.
3. **Ensure CLAUDE.md carries trigger keywords** linking to that deep-dive. The detail must be reachable from a fresh clone in one hop: CLAUDE.md keyword/phrase → deep-dive. Never rely on git history (or this file) for the *how*.

Per project convention, probe the live SQL Server 2025 reference instance before encoding "matches SQL Server" behavior.

This file is the home for net-new non-function feature proposals too. CLAUDE.md's **Not modeled** section is the complementary *descriptive* map (what currently raises `NotSupportedException` / Msg, so the surface isn't over-promised); this list is the *prospective* one. An item can appear in both with opposite intent.

## Missing features

### Built-in functions

Captured from a Microsoft Learn category-by-category audit (cross-checked against `Parser/Expression.cs::ResolveBuiltIn`, `Parser/AtAtKeyword.cs` + `Value.cs`, `Parser/Expressions/AggregateExpression.cs`, `Parser/Expressions/WindowExpression.cs`, and the FROM-source rowset dispatch in `Parser/Selection.{OpenJson,StringSplit,ListExtendedProperty}.cs`). Re-fetch <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the function surface complete. 🎯 marks an item whose completion closes a Microsoft category.

Buildable now (infrastructure exists):

- **FORMATMESSAGE(msg_id_or_string, args...)** — printf-style with `sys.messages` fallback (sys.messages not modeled).
- **PWDCOMPARE(clear, hash) / PWDENCRYPT(clear)** — password hashing helpers.
- **LOGINPROPERTY(login, prop)** — login-property switch.

Blocked on a larger unmodeled parent feature (shipping a function here implies the parent ships too):

- **Graph** (node/edge tables) — EDGE_ID_FROM_PARTS / GRAPH_ID_FROM_EDGE_ID / GRAPH_ID_FROM_NODE_ID / NODE_ID_FROM_PARTS / OBJECT_ID_FROM_EDGE_ID / OBJECT_ID_FROM_NODE_ID.
- **Application locks** (sp_getapplock / sp_releaseapplock) — APPLOCK_MODE / APPLOCK_TEST (real apps use these for cross-session coordination).
- **Change tracking** — CHANGETABLE(CHANGES …) / CHANGETABLE(VERSION …).
- **Partitioning** — `$PARTITION.partition_function_name(value)`.
- **CLR assemblies** (`CREATE ASSEMBLY` rejected) — ASSEMBLYPROPERTY.
- **ML scoring** (PREDICT surface not modeled) — PREDICT(MODEL = …, DATA = …).
- **Ad-hoc data sources** — OPENROWSET (file/bulk + provider rowsets); OPENDATASOURCE / OPENQUERY (four-part-name reads already ship — these are the inline ad-hoc forms); OPENXML (pre-`OPENJSON` XML rowset, still hit in legacy code).

Low priority / niche — simulatable (as placeholder constants or a small model) but rarely hit, so not worth attention yet:

- **Legacy text/image** — TEXTPTR / TEXTVALID (the `text` / `ntext` / `image` types ship; these navigate the deprecated READTEXT / WRITETEXT / UPDATETEXT pointer path).
- **System statistical** (DBA introspection) — @@CONNECTIONS / @@CPU_BUSY / @@IDLE / @@IO_BUSY / @@PACK_RECEIVED / @@PACK_SENT / @@PACKET_ERRORS / @@TIMETICKS / @@TOTAL_ERRORS / @@TOTAL_READ / @@TOTAL_WRITE / fn_virtualfilestats. Plausible constants would satisfy most callers; rarely hit from app code.
- **Files / filegroups** — FILE_ID / FILE_IDEX / FILE_NAME / FILEGROUP_ID / FILEGROUP_NAME / FILEGROUPPROPERTY / FILEPROPERTY. No physical file model, but a synthetic single-`PRIMARY`-filegroup / `file_id` 1 placeholder would cover the common reads.
- **Certificates** — CERTENCODED / CERTPRIVATEKEY (needs a small certificate-name → bytes model, or NULL placeholders).
- **Full-text properties** — FULLTEXTCATALOGPROPERTY / FULLTEXTSERVICEPROPERTY (the `CREATE FULLTEXT CATALOG` / `INDEX` DDL already ships; these read its metadata or return service-config constants).
- **FILESTREAM** — GET_FILESTREAM_TRANSACTION_CONTEXT (needs a FILESTREAM storage binding; NULL is a faithful "no FILESTREAM context" placeholder).

## Fidelity gaps in shipped behavior

Real bugs / limitations against shipped behavior — fixes are concrete work, not design decisions.

- **REPLICATE** of a MAX-typed *column* reference truncates to 8000 bytes (the parse-time type resolver doesn't reach FROM-source columns; literal / CAST-target inputs work).
- **GROUPING / GROUPING_ID** only accept `Reference` arguments — `GROUPING(a+1)` paired with `GROUP BY a+1` raises Msg 8161 instead of matching structurally.
- **STRING_SPLIT(…, …, CAST(@v AS INT))** wrapped-variable accepted; real SQL Server rejects all variable-bearing `enable_ordinal` shapes regardless of wrapping.
- **Trailing-space MIN/MAX representative** — for a group of values differing only in trailing spaces (sort-equal under SQL Server), MIN/MAX returns a different byte-variant than the live server's scan-order representative. Surfaced by the AdventureWorks crosscheck on synthetic XML data (`vJobCandidateEducation._max_Edu_Loc_CountryRegion`). Needs trailing-space-insensitive compare + SQL Server's unspecified MAX-tie scan-order. See [`collations.md`](collations.md) "byte-exact sort" trailing-space note. **Deferred** — synthetic data, and the representative is unspecified scan-order on the live side.
- **Leaked-connection session cleanup** — a `SimulatedDbConnection` that's never `Dispose`d is pinned indefinitely by the strong `Simulation.Connections` registry, so its session state never reclaims: an open transaction holds its locks and pins the MVCC version store forever, `##temp` tables linger, and the SPID accumulates. Real SqlClient's GC-finalization eventually closes a leaked connection and the server resets the session, so this is a genuine fidelity divergence. Faithful fix is **weak detection + deferred teardown**: weaken the registry (or add a parallel weak set) so a collected connection is noticed, and tear the session down lazily on a normal worker thread (next lock-manager pass / version-store GC / `CreateDbConnection`) — keeping transaction rollback off the finalizer thread and under the engine's existing synchronization. A bare finalizer can't work because the strong registry pins the object so the finalizer never runs, and rolling back from the finalizer thread is the one genuinely unsafe part. Interacts with deadlock detection / `sys.dm_*` waiter enumeration, which the registry currently backs. Low traffic (EF Core disposes scrupulously; only buggy consumer code hits it), but on-mission under authenticity. Eventual home: [`locking.md`](locking.md).
- **Workload-harness divergence reporting quirks** (`.vs/workload/Program.cs`, local-only) — the parity report's example line rebuilds parameters from the op seed and can mismatch the actual divergent instance, and divergent instances aren't re-run single-threaded to classify transient-vs-stable. Both made the 2026-07-10 shared-plan-state hunt slower than it needed to be (the fixed bug class itself — instance-bound aggregate/window results, baked TOP/OFFSET counts, frozen RAND, unstamped replay clock — is documented in [`plan-cache.md`](plan-cache.md)'s shared-plan contract section).

## Design choices to revisit

Shipped intentionally and correct under their documented contract, but the original rationale may have aged. Worth a look before re-affirming or changing. (Rationale lives in [`scalars.md`](scalars.md)'s divergence notes and CLAUDE.md's Quirks.)

- **APPROX_COUNT_DISTINCT** implemented as exact `COUNT(DISTINCT)`. Original rationale: same semantic guarantee, no HyperLogLog dependency. Review: is the perf gap visible against in-process workloads? If not, the simpler form stays defensible.
- **CHECKSUM_AGG** uses an order-independent XOR fold. Rationale: same-multiset-same-checksum preserved, bit-identical wasn't required. Review: has any consumer needed bit-identical checksums (e.g. replication-comparison parity)?
- **DATALENGTH** returns `int` for MAX-typed inputs; real returns `bigint`. Rationale: result fits in int for any value the simulator can produce. Review: does the projection-schema mismatch break any consumer (EF Core mappings, ORM projections) now that storage scales further?
- **`float` CAST/CONVERT** text formatting uses .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific. Rationale: .NET formatting is the default; the specific format wasn't a fidelity-oracle requirement. Review: do users hit float-as-string comparisons in real workloads?
- **`decimal` / `numeric`** backed by .NET `decimal`; values needing more than 28 significant digits aren't modeled (declarations through `decimal(38, *)` accepted so storage byte-width matches). Rationale: .NET decimal is the simplest path. Review: do real schemas use the high-precision range, or is 28 sig digits enough in practice?
- **`hierarchyid` / `geography` / `geometry` CAST** encoding is simulator-native. Rationale: byte-identical transfer wasn't a fidelity-oracle requirement. Review: would byte-identical encoding unlock cross-engine data movement that's now in scope?

## Won't-model / explicitly excluded

Excluded on **correctness**, not priority: these are cloud-only surfaces the SQL Server 2025 RTM box product itself rejects, so modeling them would *diverge* from the box-product fidelity oracle. Don't re-surface as candidates (unless a future box release promotes one).

- **ANY_VALUE(expr)** — Azure/Fabric-only, not in the box product (probe-confirmed 2026-05-27). With it excluded, the **Analytic** category is complete for the box product (CUME_DIST / PERCENT_RANK / PERCENTILE_CONT / PERCENTILE_DISC all ship).
- **SESSION_ID()** — dedicated-SQL-pool / cloud surface; the box raises Msg 195 (probe-confirmed). `@@SPID` is the box session-id mechanism.
