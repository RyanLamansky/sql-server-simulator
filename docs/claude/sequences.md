# Sequence objects

`CREATE SEQUENCE [schema.]name [AS <type>] [START WITH n] [INCREMENT BY n] [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE] [CACHE n | NO CACHE]`, dropped via `DROP SEQUENCE [IF EXISTS]`, mutated via `ALTER SEQUENCE`, consumed by `NEXT VALUE FOR [schema.]seqname [OVER (ORDER BY ...)]`.
Lives in its owning `Schema`'s `Sequences` dict; shares the object namespace with tables / views / functions / procs (Msg 2714 on cross-kind collision).
Probed against SQL Server 2025.

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


## Resolution / lookup

- `BatchContext.TryResolveSequence(MultiPartName)` — accepts 1-part names (falls back to `dbo`), 2-part (`schema.seq`), 3-part (`db.schema.seq`, db must match current).
- `NEXT VALUE FOR` on a non-sequence object that exists as a table / view / etc. → **Msg 11726** (probe-confirmed wording uses the qualified `dbo.name` form).
- `NEXT VALUE FOR` on a totally missing name → **Msg 208** (the standard "invalid object name").

## `sys.sequences` catalog view

Columns: `name`, `object_id`, `schema_id`, `principal_id` (always NULL — ownership follows the schema), `create_date`, `modify_date` (both the ALTER-preserving `SchemaObject` timestamps), `start_value`, `increment`, `minimum_value`, `maximum_value`, `is_cycling`, `is_cached` (always `true`), `cache_size` (always NULL), `current_value`, `last_used_value`, `system_type_id`, `user_type_id`, `is_exhausted`, `precision tinyint`, `scale tinyint` (nullable).
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

## One value per row

Every reference to one sequence within a **single inserted row** returns the same value — including a DEFAULT-clause reference on a column the INSERT didn't list.
Probe-confirmed against SQL Server 2025: `INSERT INTO d (v) VALUES (NEXT VALUE FOR s)` against `d.id int DEFAULT (NEXT VALUE FOR s)` stores `(1, 1)` and consumes exactly one value.

The two references are evaluated in different phases — the VALUES tuple in `EvaluateParsedTuples`, the DEFAULT in the row-encode loop — so the encode loop **restores** the stamp its tuple was evaluated under rather than bumping to a fresh one, letting the DEFAULT hit `BatchContext.SequenceRowCache`.
(Bumping there drew a second value and silently stored `(2, 1)`.)
SELECT / EXEC row sources carry no per-tuple stamp and keep the fresh per-row bump.

**Msg 11731** gates the shape real declines to define: a **multi-row** `VALUES` constructor referencing a sequence that an *unlisted* target column also defaults from raises `A column that uses a sequence object in the default constraint must be present in the target columns list, if the same sequence object appears in a row constructor.` at bind time.
The single-row form is accepted (it shares one value, above); only the row-constructor form rejects — probe-confirmed both ways.
Detection collects the tuples' sequence references through `ParserContext.SequenceCollector` (the same collector pattern aggregates and windows use, so a reference at any nesting depth is caught) and matches them against each column's DEFAULT peeled to a bare `NEXT VALUE FOR`.
A sequence buried inside a larger default expression (`NEXT VALUE FOR s + 1`) isn't detected — the bare form is what a sequence default takes in practice.

These shapes stay legal and each advance once per row, matching real: the defaulted column listed explicitly, a *different* sequence in the constructor, and a multi-row insert whose tuples reference no sequence.

## Catalog state: `current_value` vs `last_used_value`

`sys.sequences.current_value` is the value most recently **emitted**, or — before anything has been — the position the next `NEXT VALUE FOR` will return.
Probe-confirmed against SQL Server 2025 across every state of a `START WITH 10 INCREMENT BY 5` sequence:

| Stage | `current_value` | `last_used_value` |
|---|---|---|
| fresh | 10 | NULL |
| after issuing 10 | 10 | 10 |
| after `RESTART WITH 100` | 100 | NULL |
| after issuing 100, 105 | 105 | 105 |
| after bare `RESTART` | 100 | NULL |

So the projection is `LastUsedValue ?? CurrentValue` (`Sequence.CurrentValueAsVariant`) — the internal `CurrentValue` field holds the *next* value to issue, which is the right answer only while nothing has been issued.
Projecting it unconditionally reported one increment ahead of real after any use, and disagreed with `last_used_value` where real has the two equal.

**`RESTART WITH n` moves the start value**, not just the position: `start_value` reports n afterwards and a later bare `RESTART` returns to n rather than to the declared origin (probe-confirmed — the last row above returns to 100, not 10).

## Where `NEXT VALUE FOR` is rejected

Real refuses a sequence draw with **nine** different messages, and all nine ship — every one at parse, which is what keeps the sequence from advancing, since the batch never runs.
`ParserContext.NextValueForRejection` carries which one applies (`NextValueForScope`), each construct's parse raises it as a *floor* through `ParserContext.EnterNextValueForScope` for its own duration, and `NextValueFor`'s constructor consumes it.

**`NextValueForScope`'s declaration order is real's precedence order.**
A reference under two restrictions at once reports the earlier arm, which is why the floor is a min rather than a set: a `DISTINCT` statement whose `WHERE` holds the reference is Msg 11721, not the `WHERE`'s own 11720.
Every neighbouring pair below was probed directly (SQL Server 2025, 2026-08-05).

| # | Msg | the reference sits in | probed refusals |
|---|---|---|---|
| 1 | **11719** | a nested query or stored expression | derived table, CTE, subquery, `EXISTS` / `APPLY` body, view / function body, **CHECK constraint**, **computed column**, a `MERGE`'s `USING` derived table |
| 2 | **11725** | an aggregate's argument | `SUM` / `MAX` / `MIN` / `COUNT` / `STRING_AGG`, `DISTINCT` argument, the reference nested inside a larger argument expression |
| 3 | **11721** | a statement that dedupes or combines rowsets | `DISTINCT`, `UNION`, `UNION ALL`, `EXCEPT`, `INTERSECT` — in *either* branch; a nested query's own `DISTINCT` doesn't count |
| 4 | **11723** | a statement carrying an `ORDER BY`, the reference naming no `OVER` | the select list, or a clause of the same statement |
| 5 | **11720** | one of the eight clauses its own text names | `TOP`, `OVER`, `OUTPUT`, `ON` (a `MERGE`'s as well as a join's), `WHERE` (an `UPDATE` / `DELETE`'s as well as a `SELECT`'s), `GROUP BY`, `HAVING`, `ORDER BY` |
| 6 | **11739** | a statement carrying a `TOP` or an `OFFSET`, or running under a session `SET ROWCOUNT` | either clause or the option, whatever the reference's own position |
| 7 | **11741** | an arm of the conditional family | `CASE` (simple and searched — input expression, `WHEN` operand, `THEN`, `ELSE`), `IIF`, `COALESCE`, `ISNULL`, `NULLIF` |
| 8 | **11742** | a `MERGE` action's own expression | a `WHEN MATCHED … UPDATE SET`, a `WHEN NOT MATCHED … INSERT … VALUES` |
| 9 | **11738** | a statement real declines to define it in at all | `PRINT` |

**`CHOOSE` is named in Msg 11741's text and accepts a reference anyway** — in the index slot as much as a value slot (probe-confirmed), so it doesn't route through the refusal.

**An `OVER` on the reference lifts exactly one refusal, #4.**
`SELECT NEXT VALUE FOR s OVER (ORDER BY id) FROM t ORDER BY id` runs; the same reference under a `DISTINCT`, an aggregate, a `CASE`, a restricted clause or a `TOP` / `OFFSET` is refused as it would be without the `OVER`.

Two of the refusals are properties of the *finished statement* rather than of the reference's position, and neither is knowable when the reference parses — a set operator and an `ORDER BY` both sit past the select list.
Those are settled by comparing `ParserContext.SequenceDrawsParsed` / `UnwindowedSequenceDrawsParsed` against a snapshot taken where the statement began: at each set operator as it is consumed (and eagerly for every branch after it), and once the query spec's `ORDER BY` / `OFFSET` have been read.
A **FROM-less** first branch of a set operation looks the one token ahead itself, because its projection would otherwise be *baked* — evaluated at parse — before the operator refused the statement, and real draws nothing there.

**Msg 11719**'s own family, severity 15 state 1 (probed against SQL Server 2025, 2026-08-05):

| context | probe |
|---|---|
| derived table, with or without its own FROM | N2.02 / N2.03 |
| common table expression | N2.04 |
| scalar subquery in the select list | N2.05 |
| a subquery in the `WHERE` (the nested error, not the clause one) | N2.06 |
| `EXISTS` body | N2.29 |
| `APPLY` body | N2.28 |
| the derived table an `INSERT … SELECT` or `SELECT … INTO` reads | N2.30 / N2b.04 |
| a `MERGE`'s `USING` source | N2.32 |
| view body — at **`CREATE`**, attributed to the view | N2.18 |
| scalar UDF / inline TVF / multi-statement TVF body — at `CREATE`, attributed to the function | N2.19 / N2.20 / N2.21 |
| a derived table inside a **procedure** body — at `CREATE` | N2.26 |

A sequence rejected this way is untouched: real reports the same `current_value` afterwards as before, and so does the simulator.

**The positions that stay legal** are a bare `SELECT`, a `VALUES` tuple, a projection over a FROM source, a column `DEFAULT` (including the one a `MERGE`'s insert action reaches without writing `NEXT VALUE FOR` at all), an `UPDATE`'s SET list, a `SET` / `DECLARE` initializer, an `IF` / `WHILE` condition, a `CHOOSE` argument, and a stored **procedure's** own statements (a procedure is not one of the module kinds real names — N2.25, it draws a value per call).
One more is legal and looks like it shouldn't be: **a joined `UPDATE` / `DELETE`'s own FROM-clause derived table**.
Probed both spellings — `UPDATE t SET … FROM (SELECT NEXT VALUE FOR s AS n) d` and the `JOIN` form — run and draw their value on real, where the identical derived table under a `SELECT`, an `INSERT … SELECT` or a `MERGE … USING` is refused (N2b.01-03 against N2b.04-05).
`ParserContext.AllowNextValueForInFromClause`, set around the mutation's `ParseSourcesAndJoins`, is that exemption.
Such a derived table is *uncorrelated*, so real evaluates it **once** for the whole statement — every target row takes the same value, and the sequence advances by one.

### Divergences

Each is a case where both engines refuse and only the message differs.

- A reference sitting in a **restricted clause** (Msg 11720) or a **conditional arm** (Msg 11741) in a statement that *also* carries an `ORDER BY` reports its own message where real reports Msg 11723.
  Those two refusals fire where the reference parses, which is before the `ORDER BY` is read; `DISTINCT` and `TOP` are known by then and do report real's message.
- A `NEXT VALUE FOR` inside a **windowed aggregate**'s argument (`SUM(NEXT VALUE FOR s) OVER ()`) reports Msg 11725 where real reports 11720, since the trailing `OVER` is read after the argument.

### A parse that isn't going to run draws nothing

A FROM-less `SELECT` bakes its projection at parse time, which *evaluates* it — so `CREATE PROCEDURE p AS SELECT NEXT VALUE FOR s` drew a value while binding the body, where real leaves `last_used_value` NULL (probe-confirmed 2026-08-05).
The bake declines whenever the parsing batch is skipping — an un-taken branch or a module body being bound at `CREATE` — which costs nothing, since a skipped statement yields no rows for anyone to read.

## Deferred

- `NEXT VALUE FOR ... OVER (ORDER BY ...)` — the OVER clause is parsed and discarded (the simulator iterates in a single deterministic order regardless of the OVER's ordering hint; the row-by-row sequence-advance pattern is the same with or without OVER).
- Multi-name `DROP SEQUENCE a, b, c` — the comma-separated form works (inherited from the shared DROP parser); each name is dropped independently with `IF EXISTS` applied uniformly.
- `INFORMATION_SCHEMA.SEQUENCES` — ISO-standard surface, not shipped.
  Apps that query catalogs typically use `sys.sequences` instead.
- *(the VALUES + DEFAULT double-advance is fixed — see [One value per row](#one-value-per-row) below)*
- **CREATE SEQUENCE in transaction undo log** — sequence creation isn't logged.
  Same asymmetry as CREATE TABLE for regular (non-temp) tables, documented as a quirk.
