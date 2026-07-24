# Backlog

Forward-looking work list: missing features, fidelity gaps in shipped behavior, and design choices worth revisiting.
**Not a checklist** — completed work is removed, not ticked.

Ordering within each section leans toward predicted importance (popularity × ease, the Operating-goal weighting in [`../../CLAUDE.md`](../../CLAUDE.md)), but is **explicitly non-authoritative**: anything here is valid to pick up, and so is anything *not* here.

## Completion process

When an item ships:

1. **Remove it from this file.**
   No checkmarks, no archive section — git records *what* changed; this file is only the open list.
2. **Ensure a `docs/claude/` deep-dive documents it** — explicit function/feature names, operational structure, probe-confirmed quirks and divergences.
3. **Ensure CLAUDE.md carries trigger keywords** linking to that deep-dive.
   The detail must be reachable from a fresh clone in one hop: CLAUDE.md keyword/phrase → deep-dive.
   Never rely on git history (or this file) for the *how*.

Per project convention, probe the live SQL Server 2025 reference instance before encoding "matches SQL Server" behavior.

This file is the home for net-new non-function feature proposals too.
CLAUDE.md's **Not modeled yet** section is the complementary *descriptive* map (what raises `NotSupportedException` / Msg today, so the surface isn't over-promised); this list is the *prospective* one.
An item can appear in both with opposite intent.

## Missing features

### TDS network endpoint — follow-up phases

The endpoint ships with SQLBatch + RPC + Transaction Manager support and credential enforcement via the `CREATE LOGIN` registry (see [`tds-endpoint.md`](tds-endpoint.md)); EF Core runs over the wire through vanilla `UseSqlServer`.
Remaining phases, roughly in value order:

- **Tool shakedown** — point real client tools at the endpoint and harvest their exotic catalog queries / SET shapes into this backlog.
  Tool scope (user decision, 2026-07-16): tools a SQL Server + .NET developer already has — SSMS, sqlcmd, Visual Studio (SQL Server Object Explorer / DacFx), LINQPad; DBA-flavored tools like DBeaver are out of scope.
  **SSMS is the final boss** — an ongoing campaign, not a single leg: each surface is its own multi-round harvest, and clearing one unlocks the next.
  Cleared legs are recorded in the per-feature deep-dives (catalog surface in [`catalog-views.md`](catalog-views.md), wire behavior in [`tds-endpoint.md`](tds-endpoint.md)); the discovery harnesses are the gitignored `.vs/ssms-host` TDS host and the headless SMO property-bag drain.
  **Remaining frontier**: Table Designer, Activity Monitor, standard reports, and IntelliSense's background metadata harvest.
  Candidate follow-on legs within tool scope: Visual Studio's SQL Server Object Explorer (DacFx-driven, a different query dialect from SMO) and LINQPad.
- **SMO API sweep campaign** — `.vs/smo-sweep` (gitignored local harness) walks SMO's full reachable read surface against the self-hosted simulator and, identically, against the live reference, draining every `Property.Value` and `Script()`-ing every `IScriptable`; modes `sweep` / `sweep --live` / `diff` → sorted JSON reports + `reports/triage.md`; workflow = sweep both sides → triage → fix bundles → graduated `Tests.Smo` tests → re-sweep.
  Open items from the latest triage: (a) `DBCC SHOW_STATISTICS … WITH STATS_STREAM` (SMO `Statistic.Stream`) stays `NotSupportedException` — it wants the raw serialized statistics-histogram blob, which the simulator has no faithful source for; (b) the unmodeled runtime/OS surfaces SMO reaches as absent objects (backup history `msdb.dbo.backupset`, `sys.dm_tran_persistent_version_store_stats`, file-space/IO DMVs, `sys.dm_os_process_memory`, `master.dbo.sysprocesses`, registry/OS xps) — surfaced as `PropertyCannotBeRetrievedException` / defaults, the legitimate-gap category (`FILEPROPERTY` now ships — see [`catalog-views.md`](catalog-views.md)).
- **Open residuals of shipped wire features** (details in [`tds-endpoint.md`](tds-endpoint.md)): cancel/attention reaction is bounded by the in-flight statement's materialization, not interruptible inside a single statement's row loop; MARS never raises Msg 8628/8651 and fully materializes each session's response under the execution gate.
- **Chunked `OFFSET/FETCH` paging: per-page constant factor, complexity class matches real** (probed 2026-07-18, plans + timings): real SQL Server also redoes the work on every page — `Top(OFFSET…)` over an ordered index scan reading offset+fetch rows when an index supplies the order, or a full scan + `Sort(TOP offset+fetch)` re-sorted per page when not; no cross-query sorted-result caching exists on either side, so "sort once, serve many pages" is rejected (it would invent behavior real doesn't have).
  The residual is constant-factor only: the simulator materializes (and for non-index order, sorts) all n rows per page regardless of offset where real's indexed plan touches only offset+fetch (measured at 150k rows / fetch 100 / offset 140k: sim ~41 ms vs real ~13 ms indexed, ~116 ms vs ~22 ms unindexed — real's sort is also 16-way parallel).
  Possible lever if paged drains ever matter: recognize index-supplied order in the `OFFSET/FETCH` path (real's plan shape) to skip the sort and bound the scan.
  Perf polish, not a fidelity gap.

### Django ORM test-suite shakedown — surfaced gaps

Running Django 5.1's own ORM test apps over the wire (mssql-django 1.7 / pyodbc) against the endpoint is a high-yield real-application oracle (harness: the runner's own `test_*` database via real `CREATE`/`DROP DATABASE` — no configuration override needed since those ship — `other` alias as a `TEST MIRROR`, incremental failing-SQL logger).
**The bar is parity with real, not absolute 100%**: many Django ORM tests fail on *real* SQL Server + mssql-django too (its own emulation limits), so the target is that the simulator fails exactly the tests real fails. Measured on a 20-app ORM slice (1021 tests): real fails 42, the simulator fails 43 — a **13-test sim-only delta** (the other 30 sim failures also fail on real). Compute the delta with `comm -23 <sorted sim FAIL/ERROR test names> <sorted real ones>`, not the raw sim count.

Fixed across the passes (parity-closing): `SET NOCOUNT ON` count suppression (blocked every identity insert — [`control-flow.md`](control-flow.md) / the DONE-token contract); year-first slash/dot date parsing ([`casting.md`](casting.md)); `INSERT … VALUES (DEFAULT)` / `db_default` ([`dml.md`](dml.md)); the implicit-conversion cluster (varchar→temporal in DATEDIFF/DATEPART, varchar operand in numeric arithmetic, DATEADD `bigint` interval — [`casting.md`](casting.md) / [`arithmetic.md`](arithmetic.md)); universal non-string→varchar coercion in `LIKE`; `@ $ #` in unquoted identifier bodies ([`grammar.md`](grammar.md)).
A `dbo.REGEXP_LIKE` built-in was **tried and reverted** — it's a fidelity break: SQL Server 2025's `REGEXP_LIKE` is a native reserved predicate, and `dbo.REGEXP_LIKE(...)` (mssql-django's `__regex` form) resolves on real *only* with mssql-django's regex **CLR assembly** installed. Modeling it as a built-in made the simulator *over-pass* the `test_regex*` tests, which real *fails* (Msg 156 — `REGEXP_LIKE` can't be schema-qualified) without the assembly. Since the simulator doesn't model CLR, authentically lacking `dbo.REGEXP_LIKE` is correct and parity-preserving.

Remaining **sim-only** delta (real passes, simulator fails), roughly in breadth order:

- **`OUTPUT … INTO <target>` over the ODBC/pyodbc wire** — an INSERT/UPDATE/DELETE whose OUTPUT clause has an `INTO @tablevar` **or** `INTO #temp` target produces a **malformed TDS response** over ODBC Driver 18 (client reports `HY000 "A severe error occurred"` and the connection breaks) — yet **Microsoft.Data.SqlClient tolerates it**, and the in-process ADO path works, so the engine is correct and only the wire response for the OUTPUT-INTO statement is wrong. `OUTPUT … VALUES(…)` *without* INTO works over ODBC. Blocks Django's `db_default` returning path (`SELECT TOP 0 … INTO #tmp; INSERT … OUTPUT INSERTED.* INTO #tmp VALUES(DEFAULT…); SELECT … FROM #tmp`) — ~7 `field_defaults` tests. Narrowed to the response of the single OUTPUT-INTO statement (returns `SimulatedNonQuery`, so the fault is in the DONE/completion-token sequence the wire emits); no `TdsSession` backstop fires and `TdsTokenWriter.FlushAsync` isn't reached, so response generation aborts before flush. A deep TDS-path bug — needs a sim-vs-real byte capture (cleartext proxy) of the OUTPUT-INTO statement's completion tokens. Home: `Network/`.
- **`GREATEST` / `LEAST` over aggregates + subtle aggregate-result divergences** (~5 `aggregation`/`annotation`/`expressions` tests). The `Greatest`/`Least` shape is `(SELECT MIN(value) FROM (VALUES (MIN(col)),(x)) AS _LEAST(value))` — a **correlated aggregate inside a VALUES-constructor tuple** (Msg 207: `value` doesn't resolve / the outer aggregate in the nested VALUES isn't evaluated). Isolated: a plain `(VALUES(1),(5)) v(value)` derived table and `MIN`-over-VALUES both work; only the aggregate-referencing-the-outer-table-inside-VALUES case fails. The rest are wrong-result divergences in aggregate/annotation computation (assertion mismatches, no SQL error). Deep query-engine work. Home: `Selection.cs`.

**Over-permissive validation — the simulator *accepts* what real *rejects*** (the more dangerous divergence direction: an app query works on the simulator but breaks on real). Surfaced by comparing the *reverse* delta `comm -13 <sim fails> <real fails>` (real-only failures = simulator over-passes). All confirmed sim-accepts / real-rejects (2026-07-24); the simulator's aggregate / GROUP BY binding validation was missing these SQL Server rules:

- **Msg 8120 / 8121 / 8127** — a SELECT (8120) / HAVING (8121) / ORDER BY (8127) column that is neither in the `GROUP BY` list nor inside an aggregate (`SELECT a, b, COUNT(*) FROM t GROUP BY a` — `b` is invalid). The classic GROUP BY containment rule; SQL Server is strict (no functional-dependency relaxation) and binds it at parse time. **Fixed 2026-07-24** — `Selection.Execution.cs` `ValidateGroupByReferences` on the cached plan build; `VisitColumnReferences` already excludes aggregate-internal columns, so the check resolves each bare reference to a source column and requires it to be a bare GROUP BY column. Conservative seam (deliberate, rare miss): a column appearing only *inside* a compound GROUP BY expression (`GROUP BY a+1`, `SELECT a`) is left unflagged — distinguishing it from the valid `SELECT (a+1)*2` needs sub-expression structural matching the simulator doesn't do, so it errs toward no false positive. Oracle: `GroupByContainmentTests`.
- **Msg 130** — an aggregate over an expression that itself contains an aggregate or a subquery (`MAX(CASE WHEN EXISTS(<subquery>) THEN col END)`). mssql-django emits this for `aggregate(filter=Exists(...))`. Still open.
- **Msg 8117** — `COUNT_BIG(NULL)` / an aggregate over the untyped-NULL "void" type (`Operand data type NULL is invalid for count_big operator`). mssql-django's empty-`filter` aggregate degrades to `COUNT_BIG(NULL)`. Still open.
- **Msg 164** — a `GROUP BY` expression that contains no non-outer column / is non-deterministic (`GROUP BY CAST(SYSDATETIME() AS date)`). mssql-django groups by a `db_default`-derived runtime expression. Still open.

These are `aggregation` / `ordering` tests. Whole-suite audits should always run the reverse-delta too, not just sim-only failures — a green "matches real" claim requires both directions.

Not sim bugs (**fail on real too** — leave alone): boolean-expression `=` comparison `WHERE (a<%s)=(b<%s)` → Msg 4145 on both; `CAST(<numeric> AS datetime2)` → Msg 529 on both (Django's DurationField tests expect it); most `get_or_create` `manual_pk`/duplicate IntegrityError tests (the savepoint-rollback-after-constraint pattern was probed identical to real). Pre-existing, not Django-specific: default-path string→date parsing is language-neutral, so `'1/2/3'` raises Msg 241 where real's `us_english` reads it mdy (deliberate — see [`casting.md`](casting.md)).

### Result-set serialization: `FOR XML`

The JSON/XML *functions* (OPENJSON / JSON_VALUE / JSON_QUERY / JSON_MODIFY / JSON_OBJECT / JSON_ARRAY / etc.; the XML type + XQuery-subset methods — see [`json.md`](json.md), [`xml.md`](xml.md)) all ship.

**`FOR JSON` ships** — PATH (fully, incl. dotted-alias nesting + all four options), AUTO flat, the probed value-formatting/escaping table, raw-embedding of nested FOR JSON / JSON_QUERY, Msg 13601 / 13605 / 13620.
See [`json.md`](json.md#for-json-result-serialization).
Two deferrals within it, both low-demand:
- **AUTO join-nesting** (nesting a secondary table as a sub-array) — raises `NotSupportedException`; PATH covers the same cases.
- **One-row chunking** — real chunks the string across ~2033-char rows; the simulator returns it whole.

**`FOR XML` ships** — RAW / AUTO (flat) / PATH (fully: `@attr` / element / `parent/child` nesting / `text()` / `data()` / unnamed-as-text / `PATH('')` row-tag omission / same-name concatenation), the `ELEMENTS [XSINIL|ABSENT]` and `ROOT[('name')]` options, the probed value-formatting (bit → `1`/`0`, scientific float, ISO dates, base64 binary, uppercase GUID) + position-dependent escaping table, NULL handling, empty-rowset → NULL, and Msg 6809 / 6864 / 6852 / 6861 / 6829 / 6830.
See [`xml.md`](xml.md#for-xml-result-serialization).

Deferrals within it (each raises `NotSupportedException` naming the feature, or the noted Msg):
- **EXPLICIT mode** — the universal-table format; complex, rarely hand-authored.
- **`TYPE` option** — typed-xml node embedding (nested `(SELECT … FOR XML …, TYPE)` embeds as raw child nodes rather than escaped text); the untyped escaped-text nesting is real's default and ships.
- **AUTO join-nesting** — nesting a secondary table under the first; PATH covers the same cases.
- **`BINARY BASE64`/`HEX`, `XMLSCHEMA`, `WITH NAMESPACES`** options, and the exotic PATH node functions beyond `text()`/`data()` (`comment()`, `processing-instruction()`, `node()`, `*`, `@*`).
- **One-row chunking** — real chunks the string across ~2033-char rows; the simulator returns it whole (shared with FOR JSON).

### Built-in functions

Captured from a Microsoft Learn category-by-category audit (cross-checked against `Parser/Expression.cs::ResolveBuiltIn`, `Parser/AtAtKeyword.cs` + `Value.cs`, `Parser/Expressions/AggregateExpression.cs`, `Parser/Expressions/WindowExpression.cs`, and the FROM-source rowset dispatch in `Parser/Selection.{OpenJson,StringSplit,ListExtendedProperty}.cs`).
Re-fetch <https://learn.microsoft.com/en-us/sql/t-sql/functions/functions> before declaring the function surface complete. 🎯 marks an item whose completion closes a Microsoft category.

Blocked on a larger unmodeled parent feature (shipping a function here implies the parent ships too):

- **Graph** (node/edge tables) — EDGE_ID_FROM_PARTS / GRAPH_ID_FROM_EDGE_ID / GRAPH_ID_FROM_NODE_ID / NODE_ID_FROM_PARTS / OBJECT_ID_FROM_EDGE_ID / OBJECT_ID_FROM_NODE_ID.
- **Change tracking** — CHANGETABLE(CHANGES …) / CHANGETABLE(VERSION …).
- **Partitioning** — `$PARTITION.partition_function_name(value)`.
- **CLR assemblies / functions** (`CREATE ASSEMBLY` rejected — Msg 102; CLR UDF/proc bodies parse but `EXEC` no-ops) — ASSEMBLYPROPERTY.
  Django-surfaced motivation (2026-07-24): mssql-django's `__regex`/`__iregex` lookups call a CLR scalar UDF `dbo.REGEXP_LIKE(input, pattern, caseSensitive)` it installs from `regex_clr.dll`; without a CLR model the simulator can't host it (and must NOT fake it as a built-in — a `dbo.REGEXP_LIKE` built-in over-passes the `test_regex*` tests real fails, see the Django section above). Modeling CLR (at least a .NET-backed shim for a registered assembly's exported scalar functions) is the authentic path to that + any other CLR-UDF-dependent library. SQL Server 2025's *native* bare `REGEXP_LIKE(col, pattern [, flags])` **predicate** (a reserved keyword, distinct from the UDF) is a separate, genuinely-faithful builtin worth adding independently.
- **ML scoring** (PREDICT surface not modeled) — PREDICT(MODEL = …, DATA = …).
- **Ad-hoc data sources** — OPENROWSET (file/bulk + provider rowsets); OPENDATASOURCE (the inline four-part-name form; `OPENQUERY` ships — see [`linked-servers.md`](linked-servers.md)); OPENXML (pre-`OPENJSON` XML rowset, still hit in legacy code).
  Probed 2026-07-21: real *parses* `OPENROWSET('MSDASQL', …)` then errors on disabled ad-hoc access (**Msg 7222**) and `OPENROWSET(BULK 'file', SINGLE_CLOB)` on the missing file (**Msg 4860**); the simulator doesn't parse the FROM-source form at all (Msg 102). Ad-hoc / external data access is a feature, not a syntax tweak — the parse-then-runtime-error shape depends on the whole external-data model.

- **System stored procedures** (`sp_*` family) — `sp_help` / `sp_helptext` / `sp_columns` / etc.: formatted-metadata / management procs invoked via `EXEC sp_name` (`sp_tables` / `sp_columns_100` / `sp_pkeys` / `sp_rename` ship — see [`catalog-views.md`](catalog-views.md)).
  Probed 2026-07-21: `EXEC sp_help 't'` runs on real (multi-result-set formatted output); the simulator has no such proc registered → **Msg 2812** ("Could not find stored procedure 'sp_help'.").
  A broad surface — each proc is its own result-shape contract over the catalog views. Ships piecemeal by popularity, not as a bundle.

Low priority / niche — simulatable (as placeholder constants or a small model) but rarely hit, so not worth attention yet:

- **`sql_variant` minor quirk** (cross-type family ordering and one-side-variant comparison both shipped 2026-07-19 — see [`scalars.md`](scalars.md#sql_variant-expression-semantics)): a decimal-declared inner reports BaseType `numeric` rather than real's `decimal`.
  Probed 2026-07-19: real preserves the declared keyword *distinctly* — `decimal` and `numeric` never collapse — through literals, table columns, variant columns, and variant variables assigned from typed variables.
  The faithful fix splits the per-`(p, s)` `DecimalSqlType` singleton by declared keyword, forking the reference-identity space the row encoder, promote paths, and catalog surfaces key on — a medium refactor whose blast radius far exceeds the one metadata string it corrects, so it's deliberately deferred.
  Deliberate exclusion, don't re-pitch: `msdb.dbo.syspolicy_configuration.current_value` stays `nvarchar` — it's a *view-body* projection (not a resource column) mixing `int` rows with a `binary` GUID row, every consumer reads a single named row and CASTs it, so a variant migration there would only touch the view SQL text for no observable gain.

## Fidelity gaps in shipped behavior

Real bugs / limitations against shipped behavior — fixes are concrete work, not design decisions.

- **Per-object creation-time `QUOTED_IDENTIFIER` capture not modeled** — real SQL Server stamps procedures / views / triggers / tables with the QI setting in effect at CREATE (`sys.sql_modules.uses_quoted_identifier`, `OBJECTPROPERTY(id, 'IsQuotedIdentOn')`) and executes bodies under the captured setting; the simulator re-parses bodies under the executing session's current setting.
  See [`grammar.md`](grammar.md).
  Rare legacy-pattern impact.
- **Skip-mode deferred name resolution — DML target tables not placeholder-continued** — the skip-mode parse-continuation fix (2026-07-17) substitutes placeholder metadata for a missing *FROM-clause table* or *schema-qualified function* so an un-taken branch parses to completion and is discarded whole (killing the orphaned-`ELSE` cascade — see [`control-flow.md`](control-flow.md)).
  A missing **DML target table** (INSERT / UPDATE / DELETE / MERGE) still resolves inline and throws Msg 208, caught by the residual object-name swallow whose flat recovery scan can orphan a trailing `ELSE` / `END` when the throw fires before the statement's own body is consumed.
  Probe-confirmed real SQL Server defers these (`IF 1=0 INSERT INTO missing SELECT * FROM other; SELECT 'after'` → `after`; the ELSE form runs the ELSE).
  The simulator instead surfaces a spurious Msg 208 (or Msg 102 for MERGE) and skips the ELSE.
  Narrow (requires a *missing* DML target in a dead branch — the common safe-guard idiom targets an existing table), and pre-existing.
  The faithful fix is placeholder-continuation through the DML column-validation surface (INSERT column-list / arity, UPDATE SET / DELETE WHERE against a placeholder target), which is a broad, per-processor change — deferred as low-frequency.
- **Nested paren/subquery/function caps below real's absolute thresholds** — the expression-depth restructure (2026-07-18: iterative precedence-climbing parse, iterative `Run`/`GetSqlType`, n-ary `AND`/`OR`, NOT-collapse) removed the process-death risk and lifted flat operator chains to no artificial cap.
  What remains is a *fidelity* gap on the deterministic nesting caps (see [`grammar.md`](grammar.md) "Expression depth limits"): the shared paren/subquery/function budget caps at 500 units (paren 500, subquery 83) vs real's stack-dependent 1015/168, because the simulator's parse frames are fatter (a 1 MB Debug thread parses only ~990 nested parens).
  The subquery ≈ 6× paren ratio matches real; the absolute numbers are lower to keep Msg 191 firing with headroom before the stack probe.
  Deep *function* nesting additionally surfaces Msg 8631 instead of Msg 191 on tight (≤1 MB) threads (its frames are fattest).
  Closing the gap toward real's numbers requires slimming the function-argument recursion frame (`ResolveBuiltIn` + per-function ctors are on the live path).
  Low demand — generated SQL rarely nests past tens; both outcomes are graceful.
  CASE/IIF nesting (cap 10, Msg 125) already matches real exactly.
- **Result-set `fNullable` inference — remaining long tail** — the projection nullability that drives the COLMETADATA `fNullable` flag (see `Expression.ResultIsNullable`) covers the structural cases: direct refs, literals, ISNULL, CASE, and (added in the go-mssqldb pass, 2026-07-23) CONCAT / CONCAT_WS (always NOT NULL), IIF (both-arm rule), and VALUES row-constructor columns (OR over rows).
  Still over-claiming nullable vs real: (1) **per-function** signatures — `CEILING` / `FLOOR` / `ROUND` / `SIGN` / `GETDATE` project NOT NULL on real while `ABS` / `POWER` / `SQUARE` / `NEWID` / `RAND` stay nullable, an idiosyncratic per-builtin table with no clean rule; (2) **`@@`-variable** nullability (`@@ROWCOUNT` / `@@SPID` are NOT NULL on real); (3) **string `+` concatenation** of two non-null operands (real projects NOT NULL, but the resolver has no `BatchContext` to distinguish string-vs-arithmetic `+` — `1+1` stays nullable on real, so it can't blanket-propagate); (4) **constant-fold** cases where real eliminates a null arm — `NULLIF(1,2)`, no-ELSE `CASE WHEN <constant> …`, all-constant `COALESCE(NULL,5)` (realistic `COALESCE(agg,0)` already matches: nullable on both).
  All are metadata-only over-claims (nullable is the safe direction); low demand, no clean rule.
- **`text` / `ntext` → non-string explicit CAST not rejected** — real disallows `CAST(<text/ntext> AS int / decimal / date / …)` outright with **Msg 529** (`"Explicit conversion from data type text to int is not allowed."`), even for a parseable value like `'5'`; the simulator treats `text`/`ntext` as string-category and routes through the string-parse path (returns the parsed value, or Msg 245 on a non-parseable string).
  Sibling of the `image → string` Msg 529 rejection (shipped) — but a distinct code path, since `text`/`ntext` are string-category and legitimately convert to `varchar`/`nvarchar`; the fix must intercept the non-string target before the parse paths in `SqlValue.CoerceTo`.
  Probe-confirmed 2026-07-23 via tiberius; niche (LOB-to-scalar casts are rare).
- **Integer arithmetic overflow not raised** — `2147483647 + 1` wraps to `-2147483648` on the simulator; real raises Msg 8115 (arithmetic overflow converting to int).
  A faithful fix means overflow-checking every int/bigint `+`/`-`/`*` on the hot arithmetic path; deferred as a broad change against a rarely-hit case.
- **Runtime-error streaming shape** — a per-row runtime error (`SELECT 10/0`, arithmetic overflow) is emitted by real *after* COLMETADATA, so a streaming client surfaces it while draining rows; the simulator raises it at execute-time before any COLMETADATA, so the client sees it from the initial execute call.
  Message / number / class match; only the wire position differs.
  Deferred — deep change to statement execution ordering, low practical impact.
- **Trailing-space MIN/MAX representative** — for a group of values differing only in trailing spaces (sort-equal under SQL Server), MIN/MAX returns a different byte-variant than the live server's scan-order representative.
  Surfaced by the AdventureWorks crosscheck on synthetic XML data (`vJobCandidateEducation._max_Edu_Loc_CountryRegion`).
  Needs trailing-space-insensitive compare + SQL Server's unspecified MAX-tie scan-order.
  See [`collations.md`](collations.md) "byte-exact sort" trailing-space note.
  **Deferred** — synthetic data, and the representative is unspecified scan-order on the live side.
- **Leaked-connection session cleanup** — a `SimulatedDbConnection` that's never `Dispose`d never reclaims its session state: an open transaction holds its locks and pins the MVCC version store, `##temp` tables linger, session-owned app locks stay held, and the SPID accumulates.
  Real SqlClient's GC-finalization eventually closes a leaked connection and the server resets the session, so this is a genuine fidelity divergence.
  **Scope correction (2026-07-11): the fix is bigger than "weaken the registry."**
  Investigation found *three* global strong-reference cycles that pin exactly the connections that hold session state, so GC can't collect them and a finalizer never fires: (1) `LockResource.Hold.Owner` is a strong `SimulatedDbConnection` (reachable Database → table → lock → hold) — pins any lock- or session-app-lock-holding connection; (2) `HeapTable.OwnerConnection` is strong and `Simulation.GlobalTempTables` holds the table — pins any `##temp` owner; (3) `Database.ActiveSnapshotTxs` holds the transaction, which strongly refs its connection — pins any open-snapshot session.
  Weakening `Simulation.Connections` alone accomplishes nothing because the resource *is* the pin.
  A correct fix must break all three cycles — cleanest via a one-way `SessionToken` indirection (resources reference a lightweight token identity; the connection references the token, not vice versa) plus a finalizer that enqueues a **deferred teardown** drained on a normal worker thread (next `CreateDbConnection` / version-store GC) so transaction rollback stays off the finalizer thread.
  This is a broad, mechanical owner-indirection refactor landing on the most regression-sensitive subsystem (lock manager × GC timing × threading).
  Payoff is bounded (EF disposes scrupulously; only buggy consumer code leaks), so it's **deliberately deferred** as high-risk / low-frequency.
  Eventual home: [`locking.md`](locking.md).
- **Workload-harness divergence reporting quirks** (`.vs/workload/Program.cs`, local-only) — the parity report's example line rebuilds parameters from the op seed and can mismatch the actual divergent instance, and divergent instances aren't re-run single-threaded to classify transient-vs-stable.
  Both made the 2026-07-10 shared-plan-state hunt slower than it needed to be (the fixed bug class itself — instance-bound aggregate/window results, baked TOP/OFFSET counts, frozen RAND, unstamped replay clock — is documented in [`plan-cache.md`](plan-cache.md)'s shared-plan contract section).

## Design choices to revisit

Shipped intentionally and correct under their documented contract, but the original rationale may have aged.
Worth a look before re-affirming or changing.
(Rationale lives in [`scalars.md`](scalars.md)'s divergence notes and CLAUDE.md's Quirks.)

- **APPROX_COUNT_DISTINCT** implemented as exact `COUNT(DISTINCT)`.
  Original rationale: same semantic guarantee, no HyperLogLog dependency.
  Review: is the perf gap visible against in-process workloads?
  If not, the simpler form stays defensible.
- **CHECKSUM_AGG** uses an order-independent XOR fold.
  Rationale: same-multiset-same-checksum preserved, bit-identical wasn't required.
  Review: has any consumer needed bit-identical checksums (e.g. replication-comparison parity)?
- **`float` CAST/CONVERT** text formatting uses .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific.
  Rationale: .NET formatting is the default; the specific format wasn't a fidelity-oracle requirement.
  Review: do users hit float-as-string comparisons in real workloads?
- **`decimal` / `numeric`** backed by .NET `decimal`; values needing more than 28 significant digits aren't modeled (declarations through `decimal(38, *)` accepted so storage byte-width matches).
  Rationale: .NET decimal is the simplest path.
  Review: do real schemas use the high-precision range, or is 28 sig digits enough in practice?

## Won't-model / explicitly excluded

Excluded on **correctness**, not priority: these are cloud-only surfaces the SQL Server 2025 RTM box product itself rejects, so modeling them would *diverge* from the box-product fidelity oracle.
Don't re-surface as candidates (unless a future box release promotes one).

- **ANY_VALUE(expr)** — Azure/Fabric-only, not in the box product (probe-confirmed 2026-05-27).
  With it excluded, the **Analytic** category is complete for the box product (CUME_DIST / PERCENT_RANK / PERCENTILE_CONT / PERCENTILE_DISC all ship).
- **SESSION_ID()** — dedicated-SQL-pool / cloud surface; the box raises Msg 195 (probe-confirmed).
  `@@SPID` is the box session-id mechanism.
