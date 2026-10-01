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
| Unknown aggregate operand, then unknown FOR column | Msg 207 each, in that order |
| A grouping column (every source column but the operand and the FOR column) of a type real can't compare — `text`, `xml`, `vector` … | Msg 488, after the names (probed 2026-09-28) |
| Duplicate `IN` value — names compare case- and trailing-space-insensitively, so `[a]` / `[A]` and `[x]` / `[x ]` repeat | Msg 8156 (`The column 'X' was specified multiple times for '<alias>'.`) |
| An `IN` value naming a grouping column | Msg 265, then Msg 8156 |
| An `IN` value that doesn't convert to the FOR column's type | Msg 8114 naming `nvarchar`, then Msg 473 naming the value |
| `CHECKSUM_AGG` (real's `JSON_ARRAYAGG` too) | Msg 406 — not invariant to NULLs |
| `STRING_AGG(col, sep)` | Msg 102 at the separator's comma |

The `IN` entries must be identifiers (`[2020]`, `[East]`, bare names): SQL Server rejects string/numeric literals here.
The identifier *text* is both the output column name and (coerced to the FOR column's type) the comparison value.
The rows added to the error table and the passthrough nullability below were probed 2026-10-01 against SQL Server 2025.

A grouping column keeps its source's nullability, and every pivoted column is nullable, `COUNT` included.

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
  An untyped NULL column (`SELECT NULL AS x`, an all-NULL `VALUES` column) conflicts with every typed one the same way, while two of them agree (probed 2026-09-26).
  Missing alias → Msg 102; unknown `IN` column → Msg 207; a value or name column named after a passthrough column → Msg 265, then Msg 8156.
- A passthrough column keeps its source's nullability, and the value column is NOT NULL when every column it folds is.

The type match is exact on real too: `varchar(10)` beside `varchar(5)`, `char(3)` beside `varchar(3)`, `decimal(9, 2)` beside `decimal(10, 2)` and two collations of one `varchar(10)` are all Msg 8167 (probed 2026-10-01 against SQL Server 2025), so the exact `SqlType` equality the check uses is real's rule rather than an approximation of a widening one.
