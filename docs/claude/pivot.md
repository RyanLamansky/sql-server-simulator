# PIVOT / UNPIVOT

Postfix table operators that rotate a FROM source.
Both attach after a parsed source (table, derived table, …) via `Selection.ParseSingleFromSource`'s wrapper → `ApplyOptionalPivotUnpivot`, and produce a new `FromSource` whose deferred `LateralPlan` computes the rotated rowset — so the enclosing query, the JOIN driver, and correlation plumbing treat a pivoted source exactly like a derived table.
Implementation: `Parser/Selection.Pivot.cs`.

Behavior probed against SQL Server 2025.

## PIVOT

```sql
source PIVOT ( agg(argCol) FOR forCol IN ([v1], [v2], …) ) AS alias
```

Desugars to grouped conditional aggregation, built through the shared `BuildSqlProjection` planner:

- **Implicit grouping key = every column of the inner source except `forCol` and `argCol`.**
  This is SQL Server's signature footgun: a stray column carried into the source splits the groups.
  Control it by projecting only the needed columns in a derived table (the common idiom).
- Each `IN` value becomes a projection of `agg(CASE forCol WHEN value THEN argCol END)`, named after the value's identifier text.
  The simple-form CASE aligns the value to `forCol`'s type via `CompareValuesPromoted`; the value literal is coerced to `forCol`'s type at parse time.
- Output schema = grouping columns, then one column per `IN` value.
  The pivoted column's type is the **aggregate result type** (`SUM(decimal)`→ decimal, `AVG(decimal(p,s))`→`decimal(38,max(s,6))`, `COUNT`→int).
- Empty-group semantics fall out of the aggregate path for free: `SUM` over a group with no matching rows → NULL; `COUNT` → 0.
  An `IN` value that matches no source row produces an all-NULL (or all-zero for COUNT) column.

Supported aggregates (single bare-column argument): `SUM`, `COUNT`, `COUNT_BIG`, `AVG`, `MAX`, `MIN`, `STDEV`, `STDEVP`, `VAR`, `VARP`, `APPROX_COUNT_DISTINCT`, `CHECKSUM_AGG`.
`STRING_AGG` has no PIVOT form (it needs a separator).

WHERE / ORDER BY / further joins on the pivoted source operate on its rotated output, as with any derived table.

### PIVOT error paths

| Input | Result |
|---|---|
| `COUNT(*)` (or any non-column aggregate arg, e.g. `SUM(x*2)`) | Msg 102 |
| Two aggregates (`SUM(a), COUNT(b) FOR …`) | Msg 102 |
| `IN` entries that aren't identifiers (`'East'`, `N'East'`, bare `2020`) | Msg 102 |
| Missing `AS alias` | Msg 102 |
| Unknown FOR column | Msg 207 |
| Duplicate `IN` value | Msg 8156 (`The column 'X' was specified multiple times for '<alias>'.`) |

The `IN` entries must be identifiers (`[2020]`, `[East]`, bare names): SQL Server rejects string/numeric literals here.
The identifier *text* is both the output column name and (coerced to the FOR column's type) the comparison value.

## UNPIVOT

```sql
source UNPIVOT ( valueCol FOR nameCol IN (col1, col2, …) ) AS alias
```

An unfold, not an aggregation — built as a `Selection` with a custom row-producer (`UnpivotRows`) so it rides the same `LateralPlan` seam as PIVOT:

- Each inner row emits one output row per `IN` column whose value is **non-NULL** (NULLs are dropped; a row whose every `IN` column is NULL vanishes entirely).
- Output shape = passthrough columns (every inner column not in the `IN` list), then the **value column**, then the **name column** — that's SQL Server's `SELECT *` ordering.
  The name column is `nvarchar(128)` holding the source column names.
- The `IN` columns fold into one value column, so they **must all share a type**.
  SQL Server doesn't promote here: `int` + `bigint` conflicts → Msg 8167 (`The type of column "X" conflicts with the type of other columns specified in the UNPIVOT list.`).
  Missing alias → Msg 102; unknown `IN` column → Msg 207.

### Divergence — UNPIVOT length unification

The value-type check uses exact `SqlType` equality, so two same-base-type columns of differing declared length (`varchar(10)` + `varchar(20)`) are rejected rather than unified to the widest.
Real SQL Server unifies same-family differing lengths; the differing-*type* rejection (int/bigint) matches.
The AW crosscheck driver is PIVOT, not UNPIVOT, so this gap is unexercised in practice — widen to family-equality + max-length if a workload needs it.
