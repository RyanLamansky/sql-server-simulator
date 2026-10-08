# Sequence objects

`CREATE SEQUENCE [schema.]name [AS <type>] [START WITH n] [INCREMENT BY n] [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE] [CACHE n | NO CACHE]`, dropped via `DROP SEQUENCE [IF EXISTS]`, mutated via `ALTER SEQUENCE`, consumed by `NEXT VALUE FOR [schema.]seqname [OVER (ORDER BY ...)]`.
Lives in its owning `Schema`'s `Sequences` dict; shares the object namespace with tables / views / functions / procs (Msg 2714 on cross-kind collision).
Probed against SQL Server 2025.

## Type, start, range, cycle

- **Allowed types**: `tinyint`, `smallint`, `int`, `bigint`, `decimal(p, 0)`, `numeric(p, 0)`.
  A `numeric` sequence keeps the spelling (`Sequence.SpelledNumeric`): `sys.sequences` reports type 108, and a drawn value's `sql_variant` base type is `numeric`, a `decimal` one's `decimal` (probed 2026-10-04 against SQL Server 2025).
  Non-zero decimal scale → **Msg 11702**.
  Non-integer types (`float`, `real`, string family) → same Msg 11702.
  Default type when `AS` omitted: `bigint`.
- **Default start**: `minvalue` for ascending increment, `maxvalue` for descending.
  The first `NEXT VALUE FOR` returns `start_value` itself (NOT `start + increment`) — verified.
- **Default increment**: 1.
- **Default min/max**: the natural bounds of the declared type.
  For decimal, `10^precision - 1` at every precision through 38: sequence state is tracked in `Int128`, which holds `decimal(38, 0)`'s whole range.
  A written `INCREMENT BY`, `MINVALUE`, `MAXVALUE`, `START WITH` or `RESTART WITH` outside the declared type is **Msg 11708** naming the argument — checked increment (after its zero check, Msg 11700) then minimum, maximum and start, whatever the written order — and a `CACHE` wider than `int` is a syntax error at the number (probed 2026-10-02 against SQL Server 2025).
  A refused `ALTER SEQUENCE` leaves the sequence as it was.
- **`INCREMENT BY 0`** → **Msg 11700**.
- **`START WITH` outside `[minvalue, maxvalue]`** → **Msg 11703**, checked after a minimum not below the maximum — equal bounds included — which is **Msg 11705**.
- **The option grammar** (probed 2026-10-04 against SQL Server 2025): an option written twice is **Msg 11712** naming it, a `NO` form and its positive counting as one; a literal with a decimal point is **Msg 11708** for its argument and a float literal a syntax error at itself; options take no separating comma; `CACHE 0` is **Msg 11706**, `CACHE 1` sends Msg 11707 (`… has been set to NO CACHE.`) yet reports a cache of 1, and a signed cache is a syntax error at the sign; a temporary name is **Msg 11714**.
  The data-dependent refusals — 11700, 11703, 11704, 11705, 11708 and an exhausted draw's 11728 — are catchable and end the batch uncaught.
- **Cycle**: ascending wrap → `minvalue`; descending wrap → `maxvalue`.
  No-cycle exhaustion sticks (`Sequence.IsExhausted`) until `ALTER SEQUENCE … RESTART`; subsequent `NEXT VALUE FOR` → **Msg 11728**.
- **`CACHE n` / `NO CACHE`**: parse-and-ignore (the simulator doesn't model the batched-allocation optimization that real SQL Server's CACHE represents).
  `sys.sequences` reports the declaration as real does (probed 2026-09-26): `is_cached` is 0 only under `NO CACHE`, and `cache_size` is an explicit `CACHE n`'s size, NULL for the default, a bare `CACHE` and `NO CACHE`.
  The declared size does one more thing: the `CREATE` or `ALTER SEQUENCE` that leaves the cache — 50 values by default — longer than the values left before the bound sends real's **Msg 11729** (`The sequence object 's' cache size is greater than the number of available values.`), and neither `NO CACHE`, a cache of 1 nor `CYCLE` ever does; no draw sends it, a `tinyint` sequence drawn from 100 to its end included (probed 2026-10-04 against SQL Server 2025).

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


## `ALTER SEQUENCE`

The options are read whole before anything is checked, so their grammar refusals are real's parse errors: `START WITH` is **Msg 11710**, `AS` **Msg 11711**, no option at all **Msg 11715** and one written twice **Msg 11712** (probed 2026-10-04 against SQL Server 2025).
`NO MINVALUE` / `NO MAXVALUE` restore the type's bounds.
Then, against the new options: equal or crossed bounds are **Msg 11705**, a `RESTART WITH` outside them **Msg 11703**, and without a restart a current value (`sys.sequences.current_value`) outside them **Msg 11704** naming it; a missing sequence, or another kind of object, is **Msg 15151**.
Without `RESTART` the next draw follows the last one by the new increment, wrapping or exhausting against the new bounds — so an exhausted sequence whose new options leave room draws again — while one nothing has been drawn from keeps its position (`Sequence.RepositionAfterAlter`).
The statement rolls back with its transaction, and sends Msg 11729 as `CREATE` does.

## `sp_sequence_get_range`

`sp_sequence_get_range @sequence_name, @range_size, @range_first_value OUTPUT [, @range_last_value OUTPUT [, @range_cycle_count OUTPUT [, @sequence_increment OUTPUT [, @sequence_min_value OUTPUT [, @sequence_max_value OUTPUT]]]]]` reserves a range (`Sequence.DrawRange`), reporting it through `sql_variant`s of the sequence's type: a cycling sequence wraps to its opposite bound as often as it needs, counting the wraps — `tinyint` from 250 for 20 values ends at 13 having wrapped once (probed 2026-10-04 against SQL Server 2025).
Binding refusals are the procedure's own at line 0: a missing `@range_first_value` **Msg 201**, an output variable of another type **Msg 257**.
The body's come from `sys.sp_sequence_get_range_internal` at line 1: a name that isn't a sequence **Msg 208** at state 134, a size that isn't positive **Msg 11733**, and a no-cycle range past the bound **Msg 11732**, which ends the batch uncaught and reserves nothing.
Like a draw, a reserved range isn't handed back by a rollback.

## Resolution / lookup

- `BatchContext.TryResolveSequence(MultiPartName)` — accepts 1-part names (falls back to `dbo`), 2-part (`schema.seq`), 3-part (`db.schema.seq`, db must match current).
- `NEXT VALUE FOR` on a non-sequence object that exists as a table / view / etc. → **Msg 11726** (probe-confirmed wording uses the qualified `dbo.name` form); a variable after `FOR` is a syntax error at itself (probed 2026-10-04).
- `DROP SEQUENCE` of a sequence a column default draws from is **Msg 3729** naming the default constraint, and `sys.sql_expression_dependencies` lists the default as referencing the sequence (probed 2026-10-04 against SQL Server 2025).
- `NEXT VALUE FOR` on a totally missing name → **Msg 208** (the standard "invalid object name"), at state 1 where real raises state 211 (probed 2026-10-04 against SQL Server 2025).
- A principal other than `dbo` needs `UPDATE` on the sequence (Msg 229), except in a column `DEFAULT`, which ownership chaining covers.
- A `#temp` table's default resolves the name in tempdb, its schema defaulting to `dbo`, so a sequence of the user database is **Msg 208** at state 211 there (probed 2026-10-06 against SQL Server 2025); a table variable's default resolves in the current database.
- `EXEC p NEXT VALUE FOR s` is **Msg 102** at `next`, which no argument begins with.

## `sys.sequences` catalog view

Columns: `name`, `object_id`, `schema_id`, `principal_id` (always NULL — ownership follows the schema), `create_date`, `modify_date` (both the ALTER-preserving `SchemaObject` timestamps), `start_value`, `increment`, `minimum_value`, `maximum_value`, `is_cycling`, `is_cached`, `cache_size`, `current_value`, `last_used_value`, `system_type_id`, `user_type_id`, `is_exhausted`, `precision tinyint`, `scale tinyint` (nullable).
**`last_used_value`** is NULL until the first `NEXT VALUE FOR` in the process, then the last emitted value wrapped in the sequence's declared type, and reset to NULL by `ALTER SEQUENCE … RESTART`.
Backed by the nullable `Sequence.LastUsedValue` (set to the emitted value in `Advance`), distinct from `current_value` (which tracks the *next* value to emit).
Probe-confirmed: a fresh sequence reports `last_used_value` NULL even though `current_value` is the start value, and a bacpac-restored sequence reports NULL here (it's per-instance runtime state, not persisted) even when `current_value` is advanced.
`precision` / `scale` mirror the declared numeric type — `int` → 10/0, `bigint` → 19/0, `decimal(p, s)` → p/s — and the SMO **Sequence property-bag** reads them (projected `AS [NumericPrecision]` / `[NumericScale]`); a single missing column fails the whole bag query Msg 207 and every Sequence property errors.
The numeric range columns are `sql_variant` carrying the declared type, as real's are.
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
A `SELECT` source shares its draws the same way: a row's default takes the value its select list drew from the same sequence, and draws afresh from any other (probed 2026-10-06 against SQL Server 2025), so the INSERT records the stamp each source row was projected under and re-enters it.
A source that buffers its rows before the first is read carries no per-row stamps and keeps the fresh bump; an `EXEC` source has none either.

**Msg 11731** gates the shape real declines to define: a **multi-row** `VALUES` constructor referencing a sequence that an *unlisted* target column also defaults from raises `A column that uses a sequence object in the default constraint must be present in the target columns list, if the same sequence object appears in a row constructor.` at bind time.
The single-row form is accepted (it shares one value, above); only the row-constructor form rejects — probe-confirmed both ways.
Detection collects the tuples' sequence references through `ParserContext.SequenceCollector` (the same collector pattern aggregates and windows use, so a reference at any nesting depth is caught) and matches them against each column's DEFAULT peeled to a bare `NEXT VALUE FOR`.
A sequence buried inside a larger default expression (`NEXT VALUE FOR s + 1`) isn't detected — the bare form is what a sequence default takes in practice.

These shapes stay legal and each advance once per row, matching real: the defaulted column listed explicitly, a *different* sequence in the constructor, and a multi-row insert whose tuples reference no sequence.
A listed column's `DEFAULT` keyword shares its row's draw too, in a multi-row constructor as well (probed 2026-10-04): every tuple computes ahead of the first write, so `BatchContext.SequenceValuesByRow` keeps each tuple's draws by its stamp for the row's defaults to find.
`ALTER TABLE … ADD` of a column defaulting to a draw gives each existing row its own (probed 2026-10-04).

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
| 1 | **11719** | a nested query or stored expression | derived table (a `VALUES` one included), CTE, subquery, `EXISTS` / `APPLY` body, view / function body, **CHECK constraint**, **computed column**, a table type's default, a procedure's `RETURN` value, a `MERGE`'s `USING` derived table |
| 2 | **11725** | an aggregate's argument | `SUM` / `MAX` / `MIN` / `COUNT` / `STRING_AGG`, `DISTINCT` argument, the reference nested inside a larger argument expression; a **windowed** call (`SUM(…) OVER ()`) is 11720 instead, the trailing `OVER` being found by a token scan past the argument list |
| 3 | **11721** | a statement that dedupes or combines rowsets | `DISTINCT`, `UNION`, `UNION ALL`, `EXCEPT`, `INTERSECT` — in *either* branch; a nested query's own `DISTINCT` doesn't count |
| 4 | **11723** | a statement carrying an `ORDER BY`, the reference naming no `OVER` | the select list, or a clause of the same statement |
| 5 | **11720** | one of the eight clauses its own text names | `TOP`, `OVER`, `OUTPUT`, `ON` (a `MERGE`'s as well as a join's), `WHERE` (an `UPDATE` / `DELETE`'s as well as a `SELECT`'s), `GROUP BY`, `HAVING`, `ORDER BY` |
| 6 | **11739** | a statement carrying a `TOP` or an `OFFSET`, or running under a session `SET ROWCOUNT` | either clause or the option, whatever the reference's own position |
| 7 | **11741** | an arm of the conditional family | `CASE` (simple and searched — input expression, `WHEN` operand, `THEN`, `ELSE`), `IIF`, `COALESCE`, `ISNULL`, `NULLIF` |
| 8 | **11742** | a `MERGE` action's own expression | a `WHEN MATCHED … UPDATE SET`, a `WHEN NOT MATCHED … INSERT … VALUES` |
| 9 | **11738** | a statement real declines to define it in at all | `PRINT` |

**`CHOOSE` is named in Msg 11741's text and accepts a reference anyway** — in the index slot as much as a value slot (probe-confirmed), so it doesn't route through the refusal.

**Msg 11736** refuses one sequence drawn twice across a variable-assigning `SELECT`, as the statement runs, ending the batch (probed 2026-10-04).
A `TOP` count's reference is never evaluated before its refusal settles, so it draws nothing.

**An `OVER` on the reference lifts exactly one refusal, #4.**
`SELECT NEXT VALUE FOR s OVER (ORDER BY id) FROM t ORDER BY id` runs; the same reference under a `DISTINCT`, an aggregate, a `CASE`, a restricted clause or a `TOP` / `OFFSET` is refused as it would be without the `OVER`.

Two of the refusals are properties of the *finished statement* rather than of the reference's position, and neither is knowable when the reference parses — a set operator and an `ORDER BY` both sit past the select list.
Those are settled by comparing `ParserContext.SequenceDrawsParsed` / `UnwindowedSequenceDrawsParsed` against a snapshot taken where the statement began: at each set operator as it is consumed (and eagerly for every branch after it), and once the query spec's `ORDER BY` / `OFFSET` have been read.
A reference under a restriction those two statement-level refusals outrank — a clause (Msg 11720), a row limit (11739) or a conditional arm (11741) — can't be refused where it parses either, so it is **recorded** (`ParserContext.DeferredNextValueRefs`) and the query spec judges the list once its `ORDER BY` and `OFFSET` are read (`Selection.SettleDeferredNextValueRefs`).
Each reference takes the highest-precedence refusal that applies to it and the first in bind order raises it: Msg 11723 for an `ORDER BY` unless the reference names its own `OVER`, its own restriction, Msg 11739 for an `OFFSET`.
Real binds the select list after the other clauses, so its references are judged last, and a reference inside an **`OVER` body** — a window's `PARTITION BY` / `ORDER BY` or a windowed aggregate's argument — stays Msg 11720 whatever else the statement carries.
A branch a set operator follows leaves its list to the chain, whose Msg 11721 outranks all of these, and an `ORDER BY` item's own reference is refused where it parses (all probed 2026-09-29 against SQL Server 2025).
A **`TOP` count's** reference is judged after the select list's, and skips Msg 11723 when every `ORDER BY` key names a select-list item that is a written constant (`Expression.IsWrittenConstant`, the same fold Msg 408 uses), by ordinal or alias: real drops that sort, so `SELECT TOP (NEXT VALUE FOR s) 1 x FROM t ORDER BY x` is Msg 11720 where a column, a variable, `GETDATE()`, `UPPER('a')` or a subquery item keeps 11723 (`Selection.OrderByNamesOnlyConstants`; probed 2026-09-29 against SQL Server 2025).
A subquery of its own `TOP` inside the count doesn't end it: a reference after that subquery is still the count's (probed 2026-09-30).
A `DISTINCT` statement's Msg 11721 and a DML `TOP`'s Msg 11720 outrank that count's own refusal.
**Msg 11727** (severity 16) is real's last word: two references to one sequence in a statement whose `OVER` definitions differ — no `OVER` being a definition too — are refused only after every refusal above has had its say, so it is recorded (`ParserContext.SequenceOverMismatch`) and raised when the query spec settles.
An explicit `ASC`, a column qualifier and the sequence's schema spelling don't change a definition (probed 2026-09-29), and the comparison here is over normalized tokens, so two qualifiers naming different sources' columns of one name compare equal.
It is not raised for a reference outside a query spec (an `UPDATE`'s `SET` list), which is unprobed.
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

### A parse that isn't going to run draws nothing

A FROM-less `SELECT` bakes its projection at parse time, which *evaluates* it — so `CREATE PROCEDURE p AS SELECT NEXT VALUE FOR s` drew a value while binding the body, where real leaves `last_used_value` NULL (probe-confirmed 2026-08-05).
The bake declines whenever the parsing batch is skipping — an un-taken branch or a module body being bound at `CREATE` — which costs nothing, since a skipped statement yields no rows for anyone to read.

## `NEXT VALUE FOR … OVER (ORDER BY …)`

The values follow the `OVER` ordering, not the order rows are projected in: the reference registers a `ROW_NUMBER()` over the ordering with its query block (`NextValueFor.OverRank`), and the row ranked k takes the statement's k-th draw from the sequence (`StatementContext.OrderedSequenceDraws`) — so `INSERT … SELECT NEXT VALUE FOR s OVER (ORDER BY k DESC), …` numbers the rows by descending `k` (probed 2026-10-04 against SQL Server 2025).
A named window, `OVER w`, orders the draws as its `WINDOW` clause defines it, resolved once the clause has parsed (probed 2026-10-06 against SQL Server 2025).
A `VALUES` row has no query block to rank it and draws as it would without the clause.
`PARTITION BY` is **Msg 11716**, an empty `OVER ()` **Msg 11718**, and an `OVER` in a default, an `UPDATE` or a `MERGE` **Msg 11717**.

## Deferred

- Multi-name `DROP SEQUENCE a, b, c` — the comma-separated form works (inherited from the shared DROP parser); each name is dropped independently with `IF EXISTS` applied uniformly.
- `INFORMATION_SCHEMA.SEQUENCES` — ISO-standard surface, not shipped.
  Apps that query catalogs typically use `sys.sequences` instead.

## Not modeled yet

- **A table variable whose column defaults to a sequence missing as its batch compiles** — never created, or created earlier in the same batch — ends the whole batch on real with no message: nothing it would run runs, the `CREATE SEQUENCE` beside it included, and `@@ERROR` reads 0 in the next batch; run by `EXEC (…)`, the call ends the caller's batch too, which SqlClient reports as `A severe error occurred on the current command` (probed 2026-10-04, re-probed 2026-10-08 against SQL Server 2025).
  Here a sequence the batch creates leaves the declaration unbound, so each later reference to the table variable is Msg 1087 as the batch compiles and a batch with none runs, while a sequence never created is Msg 208.
  A sequence created by an earlier batch is drawn as for a table (the shape behind [Resolution / lookup](#resolution--lookup)'s note, probed 2026-10-06 and 2026-10-08).
