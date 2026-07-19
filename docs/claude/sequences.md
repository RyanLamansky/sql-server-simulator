# Sequence objects

`CREATE SEQUENCE [schema.]name [AS <type>] [START WITH n] [INCREMENT BY n] [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE] [CACHE n | NO CACHE]`, dropped via `DROP SEQUENCE [IF EXISTS]`, mutated via `ALTER SEQUENCE`, consumed by `NEXT VALUE FOR [schema.]seqname [OVER (ORDER BY ...)]`.
Lives in its owning `Schema`'s `Sequences` dict; shares the object namespace with tables / views / functions / procs (Msg 2714 on cross-kind collision).
Probed against SQL Server 2025 (2026-05-12).

## Type, start, range, cycle

- **Allowed types**: `tinyint`, `smallint`, `int`, `bigint`, `decimal(p, 0)`, `numeric(p, 0)`.
  Non-zero decimal scale → **Msg 11702**.
  Non-integer types (`float`, `real`, string family) → same Msg 11702.
  Default type when `AS` omitted: `bigint`.
- **Default start**: `minvalue` for ascending increment, `maxvalue` for descending.
  The first `NEXT VALUE FOR` returns `start_value` itself (NOT `start + increment`) — verified.
- **Default increment**: 1.
- **Default min/max**: the natural bounds of the declared type.
  For decimal, `10^precision - 1` capped at long range (precision ≥ 19 saturates to `[long.MinValue, long.MaxValue]` since the simulator tracks values in `long`).
- **`INCREMENT BY 0`** → **Msg 11700**.
- **`START WITH` outside `[minvalue, maxvalue]`** → **Msg 11703**.
- **Cycle**: ascending wrap → `minvalue`; descending wrap → `maxvalue`.
  No-cycle exhaustion sticks (`Sequence.IsExhausted`) until `ALTER SEQUENCE … RESTART`; subsequent `NEXT VALUE FOR` → **Msg 11728**.
- **`CACHE n` / `NO CACHE`**: parse-and-ignore (the simulator doesn't model the batched-allocation optimization that real SQL Server's CACHE represents).
  `sys.sequences.is_cached` reports `true` unconditionally; `cache_size` always NULL (matches real SQL Server's reported behavior when no explicit size is supplied).

## `NEXT VALUE FOR` semantics — per-row dedup

SQL Server's defining rule: **within one Transact-SQL statement, all `NEXT VALUE FOR <seq>` references to the same sequence emit the same value for a given row processed**.
Two consequences:

- `SELECT next, next` (single-row SELECT, no FROM) → both columns get the same value.
- `INSERT t VALUES (next, next), (next, next)` → row 1 has (N, N), row 2 has (N+inc, N+inc).
- `SELECT next FROM 3-row-table` → three different values, advancing per row.

Implementation: `BatchContext.CurrentRowStamp` is a monotonically-increasing per-row counter that the per-row iterators bump at each boundary.
`BatchContext.SequenceRowCache` holds `(stamp, lastValue)` per sequence; `NextValueFor.Run` returns the cached value when the stamp matches, otherwise advances the sequence and updates the cache.

Bump sites:
- **Statement boundary** — dispatch loop bumps at the top of `DispatchOneStatement`, so one-shot statements (`SET @v = next value for seq`, scalar `SELECT next value for seq`, `DECLARE @v int = next value for seq`) each start a fresh stamp.
- **`INSERT VALUES` row** — `EvaluateValuesTuples` bumps per tuple.
  Different tuples advance the sequence; multiple `NEXT VALUE FOR` instances within one tuple dedupe.
- **`INSERT` destination row** — the destination loop bumps so DEFAULT-clause evaluation per inserted row picks up a fresh stamp.
- **`SELECT` projection row** — both streaming (`ProjectStreaming`) and buffered (`ProjectBuffered`) paths bump on each row that passes WHERE.
- **`UPDATE` per-row** — both single-table and joined paths bump before evaluating the SET-list expressions.

## Restricted contexts (Msg 11720)

`NEXT VALUE FOR` is rejected at parse in: `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `TOP`, `OVER`, `OUTPUT`, `ON` (probe-confirmed wording).
Implementation: `ParserContext.RejectNextValueFor` is set inside `ParseWhereGroupByHavingOrderBy` and consumed by `NextValueFor`'s constructor.
ORDER BY toggles the flag separately from WHERE/GROUP BY/HAVING because ORDER BY allows windowed functions but still rejects sequences.

**Gaps** (not yet enforced): `ON` (JOIN predicates), `OUTPUT`, `TOP` — these don't yet set `RejectNextValueFor`.
The other clauses are covered.

## Resolution / lookup

- `BatchContext.TryResolveSequence(MultiPartName)` — accepts 1-part names (falls back to `dbo`), 2-part (`schema.seq`), 3-part (`db.schema.seq`, db must match current).
- `NEXT VALUE FOR` on a non-sequence object that exists as a table / view / etc. → **Msg 11726** (probe-confirmed wording uses the qualified `dbo.name` form).
- `NEXT VALUE FOR` on a totally missing name → **Msg 208** (the standard "invalid object name").

## `sys.sequences` catalog view

Shipped columns: `name`, `object_id`, `schema_id`, `principal_id` (always NULL — ownership follows the schema), `create_date`, `modify_date` (both the ALTER-preserving `SchemaObject` timestamps), `start_value`, `increment`, `minimum_value`, `maximum_value`, `is_cycling`, `is_cached` (always `true`), `cache_size` (always NULL), `current_value`, `last_used_value`, `system_type_id`, `user_type_id`, `is_exhausted`, `precision tinyint`, `scale tinyint` (nullable).
**`last_used_value`** is a genuine `sql_variant` (the one value column that isn't bigint-substituted): NULL until the first `NEXT VALUE FOR` in the process, then the last emitted value wrapped in the sequence's declared type, and reset to NULL by `ALTER SEQUENCE … RESTART`.
Backed by the nullable `Sequence.LastUsedValue` (set to the emitted value in `Advance`), distinct from `current_value` (which tracks the *next* value to emit).
Probe-confirmed: a fresh sequence reports `last_used_value` NULL even though `current_value` is the start value, and a bacpac-restored sequence reports NULL here (it's per-instance runtime state, not persisted) even when `current_value` is advanced.
`precision` / `scale` mirror the declared numeric type — `int` → 10/0, `bigint` → 19/0, `decimal(p, s)` → p/s — and the SMO **Sequence property-bag** reads them (projected `AS [NumericPrecision]` / `[NumericScale]`); a single missing column fails the whole bag query Msg 207 and every Sequence property errors.
The numeric range columns surface as `bigint` because the simulator tracks all sequence state in `long`; real SQL Server uses `sql_variant`, but SqlClient surfaces those as long-typed values for integer sequences anyway.
Probe-confirmed: HiLo apps that read `current_value` get an `Int64` either way.
**`create_date` / `principal_id` are load-bearing for the SSMS Sequences node**: SMO's enumeration selects `seq.create_date` and `ISNULL(seq.principal_id, OBJECTPROPERTY(seq.object_id, 'OwnerId'))`; before these columns existed the query raised Msg 207 and the node showed empty even when the database had sequences.

## EF Core HiLo (the main reach)

EF Core's `.UseHiLo("seqname")` allocates IDs in client-side batches by issuing `SELECT NEXT VALUE FOR seqname` at SaveChanges time.
End-to-end coverage in `EFCoreHiLo.cs`: the test boots a sequence + table manually (no `EnsureCreated` path exercised here), then SaveChanges three entities.
EF's SqlServer provider issues `SELECT NEXT VALUE FOR HiLoSeq` once per allocation range; the simulator emits monotonically-advancing values, EF distributes them client-side, and the resulting INSERT rows get sequential IDs.
Validates the LINQ→SQL pipeline against the simulator's NEXT VALUE FOR shape.

## Deferred

- `NEXT VALUE FOR ... OVER (ORDER BY ...)` — the OVER clause is parsed and discarded (the simulator iterates in a single deterministic order regardless of the OVER's ordering hint; the row-by-row sequence-advance pattern is the same with or without OVER).
- Multi-name `DROP SEQUENCE a, b, c` — the comma-separated form works (inherited from the shared DROP parser); each name is dropped independently with `IF EXISTS` applied uniformly.
- `INFORMATION_SCHEMA.SEQUENCES` — ISO-standard surface, not shipped.
  Apps that query catalogs typically use `sys.sequences` instead.
- **Bumps not wired into `ON` / `OUTPUT` clauses** — `NEXT VALUE FOR` inside a JOIN's ON predicate or an OUTPUT clause won't raise Msg 11720; it'll run and emit values.
  Real SQL Server rejects both.
  Apps that emit those shapes typically don't (EF Core never does), so the gap is documented rather than fixed in v1.
- **VALUES + DEFAULT mixing** — when an INSERT row has both a `NEXT VALUE FOR seq` in the VALUES tuple AND a DEFAULT-clause `NEXT VALUE FOR seq` for an unspecified column, the simulator advances the sequence twice (different row stamps for the two evaluation phases).
  Real SQL Server returns the same value for both.
  Workaround: explicitly supply the column in the INSERT column list so DEFAULT doesn't fire.
- **CREATE SEQUENCE in transaction undo log** — sequence creation isn't logged.
  Same asymmetry as CREATE TABLE for regular (non-temp) tables, documented as a quirk.
