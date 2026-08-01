# System-versioned temporal tables

Read this when working on `PERIOD FOR SYSTEM_TIME`, `GENERATED ALWAYS AS ROW START / END`, `WITH (SYSTEM_VERSIONING = ON (…))`, the history sibling (named or auto-named) and the shape validation that adopts an existing one, `HISTORY_RETENTION_PERIOD`, the `FOR SYSTEM_TIME` query forms, or related `HeapTable` / `HeapColumn` metadata.

## What's modeled

- **CREATE TABLE** with `PERIOD FOR SYSTEM_TIME (startCol, endCol)` table-level declaration + per-column `GENERATED ALWAYS AS ROW START | END [HIDDEN] NOT NULL`.
  The two period columns must be `datetime2(N)` NOT NULL; nullable or non-datetime2 raises Msg 13501 / 13587.
  Asymmetric definitions raise Msg 13504 / 13505; period names not matching the GENERATED columns raise Msg 13506 / 13507; orphan GENERATED-AS-ROW columns without a `PERIOD` declaration raise Msg 13509.
  Probe-confirmed verbatim wording against SQL Server 2025.
- **`WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = schema.table))`** trailing clause auto-creates the sibling history `HeapTable` at parent-creation time.
  The history table mirrors the parent's column shape — names, types, nullability, hidden flag, persisted-computed expressions — but strips engine-managed flags (IDENTITY, GENERATED ALWAYS), inline constraints, and DEFAULTs (history rows carry materialized values from the parent).
  `sys.tables.temporal_type = 2` for the parent, `1` for history; `sys.tables.history_table_id` references the sibling's object_id (NULL for non-temporal and history tables themselves).
  The auto-created history table starts as a plain heap (no PK), so its `sys.indexes` is a single HEAP row until a `CREATE CLUSTERED INDEX` is added — as the bacpac loader emits for the WWI `*_Archive` siblings — at which point it becomes a single CLUSTERED row at `index_id 1` with **no** phantom heap row (the index-id allocation authority suppresses it; see [`indexes.md`](indexes.md#index-id-allocation)).
  A history table therefore always projects exactly one `sys.indexes` row with `index_id < 2`, which is what DacFx's `SqlTable` export query joins against.
- **Auto-named history (`HISTORY_TABLE` omitted)** generates `MSSQL_TemporalHistoryFor_<base object_id>` in **the base table's own schema** (probe-confirmed: a base in `app` keeps its sibling in `app`, not `dbo`), from both the CREATE and the ALTER form.
  A name collision — reachable by turning versioning off, leaving the old sibling behind, and turning it back on — appends an 8-hex suffix (`MSSQL_TemporalHistoryFor_1221579390_F058EC24`).
  Real's suffix is random per attempt; the simulator's is a deterministic 32-bit FNV-1a of the colliding name plus the attempt number, so the shape matches and the value doesn't.
  EF Core 10 always emits the explicit form, so nothing in the EF path depends on this.
- **INSERT** on a system-versioned parent auto-populates the period columns: ROW START = the statement's frozen `BatchContext.CurrentStatement.UtcNow`, ROW END = `DateTime.MaxValue` (datetime2(7) precision = `9999-12-31 23:59:59.9999999`).
  Explicit values for a GENERATED ALWAYS column raise Msg 13536.
  Implicit insert column lists exclude GENERATED columns (so `INSERT INTO Customers (Id, Name) VALUES (...)` works without listing period columns).
- **UPDATE** on a system-versioned parent: pre-update full row is captured (`oldSnapshotNeeded` forced true), the post-SET row's ROW START is bumped to UtcNow, then `WriteHistoryRowsForUpdate` writes the captured pre-update row to the history sibling with ROW END overwritten to UtcNow (the period during which the row was current).
  Setting a GENERATED ALWAYS column in SET raises Msg 13537.
- **DELETE** on a system-versioned parent: pre-delete full row captured (`needsFullForHistory` forced true), then history row written with ROW END = UtcNow before tombstoning the current row.
- **`SELECT *`** excludes hidden columns (probe-confirmed: real SQL Server omits `IsHidden` columns from star expansion).
  Explicit references continue to bind by name, including in INSERT column lists and OUTPUT clauses (EF Core 10 emits `OUTPUT INSERTED.[PeriodEnd], INSERTED.[PeriodStart]` and lists period columns by name in tracked-entity SELECTs).
- **`FROM <table> FOR SYSTEM_TIME <form> [AS] <alias>`** — all five forms ship, each a filter over the union of the parent's and the history sibling's rows.
  Writing `s` for a row version's ROW START and `e` for its ROW END:

  | Form | Rows it takes |
  | --- | --- |
  | `ALL` | every version |
  | `AS OF t` | `s <= t < e` |
  | `BETWEEN t1 AND t2` | `s <= t2 AND e > t1` — active anywhere in the closed range |
  | `FROM t1 TO t2` | `s < t2 AND e > t1` — same, upper endpoint exclusive |
  | `CONTAINED IN (t1, t2)` | `s >= t1 AND e <= t2` — whole validity period inside, both endpoints inclusive |

  Probe-confirmed against SQL Server 2025 on the boundary the range forms disagree about: for versions `v1 = [T1, T2)` and `v2 = [T2, T3)`, `BETWEEN T2 AND T2` takes `v2` alone (the version *ending* at `T2` is out, the one *starting* there is in) while `FROM T2 TO T2` takes nothing, and `CONTAINED IN (T1, T2)` takes `v1` alone.
  A current version's ROW END is max datetime2, so `CONTAINED IN` reaches it only when `t2` is that same value.
  A misordered range (`t2 < t1`) is not an error — the predicate simply can't hold, and all three forms return no rows.
  A NULL bound likewise returns no rows.
- **Zero-duration versions are invisible to every form**, `ALL` included: a row updated more than once inside one transaction leaves a history row whose ROW START equals its ROW END, and real hides it from `FOR SYSTEM_TIME` while a direct `SELECT` against the history table still returns it.
  Probe-confirmed on both an engine-produced row (two UPDATEs in one transaction) and a hand-written one.
  The simulator freezes ROW START per statement rather than per transaction, so it reaches the same state when two mutations land on the same clock tick — which is why the tests separate mutations by a `WAITFOR` / sleep.
- **Time arguments are a literal or a variable**, which is all real's grammar admits: a function call (`AS OF SYSUTCDATETIME()`), a parenthesized subquery, or a column reference is **Msg 102** (or **Msg 156** when the offending token is a reserved keyword, e.g. `BETWEEN 't' TO 't'`).
  They're evaluated once on iteration start — no per-row re-evaluation, matching SQL Server's "constant per query" contract.
  ISO 8601 string literals with a trailing `Z` (UTC marker — EF Core 10 emits this) are accepted by datetime2 coercion; an unparseable string raises **Msg 241**.
  The argument's type is gated the way real gates it, as a comparison against the period columns: strings and the date/time family convert, `time` and binary raise **Msg 402** (`The data types datetime2 and time are incompatible in the greater than operator.`), and everything else — integer, decimal, money, float, bit, uniqueidentifier — raises **Msg 206** (`Operand type clash: datetime2 is incompatible with int`).
- **DROP TABLE** on a system-versioned parent or its history sibling raises Msg 13552; caller must `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)` first.
- **`ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = OFF)`** flips the parent's `HeapTable.SystemVersioning` to `null` and the history sibling's `HeapTable.IsHistoryTable` to `false` — both tables revert to plain regular status, and `DROP TABLE` on either now succeeds.
  Period definition and GENERATED ALWAYS / HIDDEN column metadata stay intact on the parent (probe-confirmed against SQL Server 2025: `sys.tables.temporal_type` flips to 0 on both, `history_table_id` clears to NULL, but the parent's `sys.columns.generated_always_type_desc` keeps `AS_ROW_START` / `AS_ROW_END` and `is_hidden` stays `True`).
  Post-SET-OFF DML semantics: INSERT still auto-populates the period columns (the per-column GENERATED ALWAYS marker drives the auto-populate in `Simulation.Insert.cs`, independent of the versioning link); explicit INSERT into a GENERATED ALWAYS column still raises Msg 13536; UPDATE does **not** bump ROW START (the engine treats the marker as "no longer engine-maintained" for writes once versioning is off); DELETE doesn't copy to the (former) history table.
  Error paths: Msg 4902 if the target name doesn't resolve (the alter-table-specific table-not-found wording, distinct from Msg 208's generic name-resolution); Msg 13591 if the target exists but isn't system-versioned (fires both for plain regular tables and for the history sibling itself — the history sibling carries the `HISTORY_TABLE` role but doesn't "have" versioning, only the parent does).
- **`ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = ON [(<options>)])`** is the inverse of OFF: takes a base with a `PERIOD FOR SYSTEM_TIME` declaration and sets `base.SystemVersioning = history` + `history.IsHistoryTable = true`.
  The option list is the same one CREATE TABLE's `WITH` clause takes — `HISTORY_TABLE`, `HISTORY_RETENTION_PERIOD` and `DATA_CONSISTENCY_CHECK`, comma-separated **in any order, each optional, and the parenthesized list itself optional** (a bare `= ON` auto-names).
  The bacpac loader's phase-5 wire-up step emits this for every SqlTable carrying a `TemporalSystemVersioningHistoryTable` relationship (SqlPackage's emit shape).
  A named history table that **doesn't exist yet is created** from the base's shape rather than rejected (probe-confirmed — real builds it, so Msg 4902 never fires for the history argument); one that exists is shape-validated (below) and adopted.
  `DATA_CONSISTENCY_CHECK = ON|OFF` parses-and-discards — the simulator doesn't enforce the temporal-data-consistency rules that the option toggles (caller-trusted history rows in the loader path).
  Other `ALTER TABLE` shapes (ADD / DROP COLUMN, ADD / DROP CONSTRAINT, DROP PERIOD, REBUILD, etc.) raise `NotSupportedException`.
- **Re-issuing `SET (SYSTEM_VERSIONING = ON …)` on an already-versioned base** is how a retention period changes in place, and real splits the rejections by what the statement named — all four probe-confirmed:

  | What the re-issue names | Result |
  | --- | --- |
  | the base's current history table | succeeds; the retention pair is rewritten (an omitted `HISTORY_RETENTION_PERIOD` resets it to INFINITE) |
  | another existing table | **Msg 13595** `Temporal history table name '…' is not correct for current table '…'.` |
  | a name that doesn't resolve | **Msg 13757** `Temporal table '…' already has history table defined. …` — the link is reported before the name is resolved, so Msg 4902 never surfaces here |
  | nothing (bare `= ON`) | **Msg 13596** `SYSTEM_VERSIONING is already turned ON for table '…'.` |

- **Base-vs-history column-shape validation** runs whenever an *existing* table is adopted as the history sibling, from either the CREATE or the ALTER path (a freshly built sibling matches by construction).
  Real's check order is probe-confirmed and the simulator follows it exactly — the whole-table rejections first, then the count, then a single ordinal walk that reports the first column differing on **any** of name / type / collation / nullability (so a type mismatch at ordinal 1 wins over a name mismatch at ordinal 2):

  | Condition | Msg |
  | --- | --- |
  | history table is already another base's sibling, or is a versioned base itself | 13514 |
  | history table declares its own `PERIOD FOR SYSTEM_TIME` | 13574 |
  | history table has a PRIMARY KEY / UNIQUE constraint or unique index | 13515 |
  | history table has FOREIGN KEYs | 13516 |
  | history table has CHECK constraints | 13517 |
  | history table has an IDENTITY column | 13518 |
  | column counts differ | 13523 |
  | column names differ at an ordinal | 13524 |
  | declared types differ (including `nvarchar` length and `datetime2` precision) | 13525 |
  | collations differ | 13526 |
  | nullability differs, in either direction | 13531 |

  **DEFAULT constraints and non-unique indexes on the history table are accepted**, as is a history table in a different schema from the base — all probe-confirmed.
  Msg 13510 (`Cannot set SYSTEM_VERSIONING to ON when SYSTEM_TIME period is not defined and the LEDGER=ON option is not specified.`) covers a base without a period from both paths, at state 1 for ALTER and state 2 for CREATE.
- **`HISTORY_RETENTION_PERIOD = <count> DAY[S] | WEEK[S] | MONTH[S] | YEAR[S] | INFINITE`** parses on both paths (singular and plural unit spellings both accepted) and lands on `sys.tables.history_retention_period` / `history_retention_period_unit` / `history_retention_period_unit_desc`.
  Unit codes are real's: DAY 3, WEEK 4, MONTH 5, YEAR 6, INFINITE -1 paired with period -1.
  Every system-versioned table reports the triple (INFINITE until one is set); history siblings and non-temporal tables report NULL for all three.
  A count of zero or less is **Msg 13743** (`0 is not a valid value for system versioning history retention period.` — the number unquoted), an unrecognized unit is **Msg 13744** at **severity 15** with the unit echoed as written, and a count with no unit is Msg 102.
  The unit is validated before the count, so `3 HOURS` is 13744 rather than anything about the 3.
- **Retention pruning is a read-side filter**: a history version whose ROW END fell before `now - retention` is invisible to every `FOR SYSTEM_TIME` form while a direct `SELECT` against the history table still returns it.
  That's real's own observable behavior — real filters aged rows out of `FOR SYSTEM_TIME` the moment the window passes and deletes them later from a background task (`sys.sp_cleanup_temporal_history` forces the delete) — see [Divergences](#divergences) for the part that isn't modeled.
  The cutoff is measured from the statement's frozen `UtcNow` and recomputed per enumeration, so widening the window back to INFINITE makes aged versions visible again.
- **Direct INSERT into history** raises Msg 13559.
  History rows are populated only by the engine via parent UPDATE / DELETE.
- **`sys.periods`** projects one row per table with a `PERIOD FOR SYSTEM_TIME` declaration of its own (`HeapTable.PeriodColumns` without `PeriodInheritedFromBase`), **excluding history siblings** — they carry a copied `PeriodColumns` for the `FOR SYSTEM_TIME` query machinery but hold no period of their own, and keying the exclusion on the inherited-copy flag rather than the history role keeps an ex-sibling out of the view after `SET (SYSTEM_VERSIONING = OFF)` clears that role.
  Row shape: `name` = `SYSTEM_TIME`, `period_type` = 1 / `period_type_desc` = `SYSTEM_TIME_PERIOD`, `object_id`, `start_column_id` / `end_column_id` = the 1-based ordinals of the ROW START / ROW END columns.
  `sys.columns.generated_always_type` reports 1 (`AS_ROW_START`) / 2 (`AS_ROW_END`) for the period columns.
  See [`catalog-views.md`](catalog-views.md).

## Data-model footprint

- **`HeapColumn`** gained `GeneratedAs` (`GeneratedAlwaysAsRow.None / Start / End`) and `IsHidden` fields.
- **`HeapTable`** gained `PeriodColumns: (int StartOrdinal, int EndOrdinal)?` (set at construction), `SystemVersioning: HeapTable?` (mutable; set after history is auto-created — points from parent → history; null on regular and history tables), `IsHistoryTable: bool` (mutable; true on the history sibling), the `HistoryRetentionPeriod` / `HistoryRetentionUnit` pair with its `HistoryRetentionCutoff(asOf)` reader, and `PeriodInheritedFromBase: bool`.
  That last flag is what keeps Msg 13574 honest: `BuildHistoryTable` copies the base's `PeriodColumns` onto the sibling so the `FOR SYSTEM_TIME` row source can read the ordinals off either side, and without the flag an ex-sibling could never be re-linked after `SET (SYSTEM_VERSIONING = OFF)`.
- **Parser scope:** `ParseColumnList`'s `pendingPeriod: List<(string StartCol, string EndCol)>?` carries the period names through column-list parsing; `ResolvePeriodColumns` (in `Simulation.Create.cs`) validates and resolves to ordinals.
  `ParseSystemVersioningOnOptions` parses the option list into a `SystemVersioningOptions` and is shared by the CREATE `WITH (…)` and ALTER `SET (…)` wrappers, so the two grammars can't drift.
  `BuildHistoryTable` mirrors the parent column shape into the history sibling, `AutoHistoryTableName` generates the `MSSQL_TemporalHistoryFor_…` name, and `ValidateHistoryTableShape` runs the adoption checks.
- **Query scope:** `Selection.ParseOptionalForSystemTime` peeks for `FOR SYSTEM_TIME` between the FROM source's table name and any alias, and returns `null` when absent (cursor restored) or a `TemporalRowSource` when present.
  That one row source serves all five forms — a `TemporalQueryKind` discriminator plus up to two bound expressions, parsed by `ParseTemporalTimeArgument` (the literal-or-variable grammar) and evaluated once per enumeration.

## EF Core 10 emit shape

`ModelBuilder.Entity<T>().ToTable("Customers", b => b.IsTemporal())` defaults the period columns to **`PeriodStart`** and **`PeriodEnd`** (not `ValidFrom` / `ValidTo` — that's the SQL Server documentation convention but not EF's default).
Tests bootstrap their tables with EF's expected names.
INSERT emits `INSERT INTO [tbl] ([cols-without-period]) OUTPUT INSERTED.[PeriodEnd], INSERTED.[PeriodStart] VALUES (...)`; UPDATE emits `UPDATE [tbl] SET [col] = @p OUTPUT INSERTED.[PeriodEnd], INSERTED.[PeriodStart] WHERE [Id] = @p`; tracked-entity SELECTs explicitly list period columns by name.
`.TemporalAll()` → `FROM [tbl] FOR SYSTEM_TIME ALL AS [c]`; `.TemporalAsOf(t)` → `FROM [tbl] FOR SYSTEM_TIME AS OF '<iso-literal-with-Z>' AS [c]`.
`.TemporalBetween(t1, t2)` / `.TemporalFromTo(t1, t2)` / `.TemporalContainedIn(t1, t2)` emit the matching range clause with both endpoints as ISO literals.

## Divergences

- **Aged-out history rows are never physically deleted.**
  Real filters them out of `FOR SYSTEM_TIME` at query time and a background task deletes them afterwards, so a direct `SELECT` against the history table returns an aged row until the cleanup runs (probe-confirmed on both halves, with `sys.sp_cleanup_temporal_history` forcing the delete).
  The simulator has no background task, so it models the half that `FOR SYSTEM_TIME` results depend on and leaves the rows in place: `select count(*)` against the history sibling keeps counting them, and the space is never reclaimed.
  Nothing observable through the temporal query surface differs.
- **The auto-created history table is a plain heap**, where real gives it a clustered index named `ix_<history table name>` on `(period end, period start)`.
  Two knock-ons: real's Msg 13765 — a finite retention period requires that clustered index — has nothing to fire on, so the simulator accepts a retention period on any history table; and a history table's `sys.indexes` stays the single HEAP row described above until a `CREATE CLUSTERED INDEX` lands (which is what the bacpac loader emits for the WWI `*_Archive` siblings).
- **Auto-generated history names carry the simulator's own object ids**, which start at ~100 rather than real's allocator range, so `MSSQL_TemporalHistoryFor_102` is structurally right and never byte-matches a real server's name for the same DDL.
  The collision suffix is deterministic where real's is random.
- **Msg 13544 qualified-name format**: real SQL Server pads temp-table names with their internal allocation suffix (`#x____...___…000000000148`); the simulator emits the bare `tempdb.dbo.#x` form.
  Same Msg number / framing, less verbose name.

## Not modeled yet

- **Temporal temp tables** — `SYSTEM_VERSIONING = ON` on a `#temp` / `##temp` raises `NotSupportedException` (real rejects them too, with its own error).
- **LOB-eligible columns** (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) on a temporal table — the `FOR SYSTEM_TIME` row source presents one `lobStore` reference to the FROM machinery; mixing parent and history rows in the same enumerator would need per-row LOB-store dispatch.
  No test scenario reaches this path.
