# System-versioned temporal tables

Read this when working on `PERIOD FOR SYSTEM_TIME`, `GENERATED ALWAYS AS ROW START / END`, `WITH (SYSTEM_VERSIONING = ON (…))`, the history sibling (named or auto-named) and the shape validation that adopts an existing one, `HISTORY_RETENTION_PERIOD`, the `FOR SYSTEM_TIME` query forms, or related `HeapTable` / `HeapColumn` metadata.

## What's modeled

- **CREATE TABLE** with `PERIOD FOR SYSTEM_TIME (startCol, endCol)` table-level declaration + per-column `GENERATED ALWAYS AS ROW START | END [HIDDEN] NOT NULL`.
  The two period columns must be `datetime2(N)` NOT NULL; nullable or non-datetime2 raises Msg 13501 / 13587.
  A period column is NOT NULL without saying so, and the `GENERATED` clause comes ahead of any `NULL` / `NOT NULL` — written after one it is Msg 102 at `generated`, and `HIDDEN` likewise precedes the nullability; `ALTER TABLE … ADD PERIOD` over a nullable column reports its Msg 13587 at state 3 (probed 2026-09-25).
  Asymmetric definitions raise Msg 13504 / 13505; period names not matching the GENERATED columns raise Msg 13506 / 13507; orphan GENERATED-AS-ROW columns without a `PERIOD` declaration raise Msg 13509.
  Probe-confirmed verbatim wording against SQL Server 2025.
  Two ROW START or ROW END columns are Msg 13502 / 13503, two periods Msg 13508, period columns of different precisions Msg 13513, and a nullable end column reports Msg 13587 at state 2 where a start column's is state 1; a `COLLATE` or `SPARSE` after the `GENERATED` clause is a syntax error at itself (probed 2026-10-04 against SQL Server 2025).
  All of these are checked as the batch compiles, so one refuses the whole batch, from a branch never taken too.
  A table variable declaring a period column is Msg 13572.
- **`WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = schema.table))`** trailing clause auto-creates the sibling history `HeapTable` at parent-creation time; `= OFF` declares the table as it would be without the clause.
  The refusals, probed 2026-10-04 against SQL Server 2025: no primary key is Msg 13553 (catchable, ending the batch uncaught, from the `ALTER` path too); a history name in one or three parts Msg 13539, a temporary one Msg 13567 and a temporary base Msg 13568; the table's own name as its history Msg 13574; a name taken by a view or another non-table object Msg 13511 naming `schema.name`; an option written twice Msg 102 at severity 16 naming the option upper-cased (state 12 for `HISTORY_TABLE`, 13 for `DATA_CONSISTENCY_CHECK`).
  The history table mirrors the parent's column shape — names, types, nullability, sparseness — but strips engine-managed flags (IDENTITY, GENERATED ALWAYS), the `HIDDEN` flag (a history sibling's period columns are never hidden, probed 2026-10-02 against SQL Server 2025), inline constraints, and DEFAULTs (history rows carry materialized values from the parent), and a computed column becomes a plain one holding the value the base row computed.
  An engine-built history is `PAGE`-compressed unless a column is sparse, which `sys.partitions` reports and which is why a sparse column can't be added to its base later (Msg 11418); rows are stored the same either way (probed 2026-10-04 against SQL Server 2025).
  `sys.tables.temporal_type = 2` for the parent, `1` for history; `sys.tables.history_table_id` references the sibling's object_id (NULL for non-temporal and history tables themselves).
  An engine-**built** sibling also gets the non-unique clustered index real builds with it — see [The history cleanup index](#the-history-cleanup-index) — so its `sys.indexes` is a single CLUSTERED row at `index_id 1` with **no** phantom heap row (the index-id allocation authority suppresses it; see [`indexes.md`](indexes.md#index-id-allocation)).
  An **adopted** table keeps whatever indexing it already had, so a plain heap stays a single HEAP row until a `CREATE CLUSTERED INDEX` lands — as the bacpac loader emits for the WWI `*_Archive` siblings.
  A history table therefore always projects exactly one `sys.indexes` row with `index_id < 2`, which is what DacFx's `SqlTable` export query joins against.
- **Auto-named history (`HISTORY_TABLE` omitted)** generates `MSSQL_TemporalHistoryFor_<base object_id>` in **the base table's own schema** (probe-confirmed: a base in `app` keeps its sibling in `app`, not `dbo`), from both the CREATE and the ALTER form.
  A name collision — reachable by turning versioning off, leaving the old sibling behind, and turning it back on — appends an 8-hex suffix (`MSSQL_TemporalHistoryFor_1221579390_F058EC24`).
  Real's suffix is random per attempt; the simulator's is a deterministic 32-bit FNV-1a of the colliding name plus the attempt number, so the shape matches and the value doesn't.
  EF Core 10 always emits the explicit form, so nothing in the EF path depends on this.
- **The time a write stamps** is its transaction's begin time inside a transaction (`BatchContext.SystemTimeUtc`), the statement's frozen `BatchContext.CurrentStatement.UtcNow` outside one — every row a transaction writes starts its period, and closes its history row, at the moment the transaction began (probed 2026-10-03 against SQL Server 2025).
  A row another transaction wrote after this one began carries a period start later than this transaction's time, so this one updating or deleting it is **Msg 13535** (`Data modification failed on system-versioned table '<db>.<schema>.<table>' because transaction time was earlier than period start time for affected records.`), the statement ended.
  Stamping each statement's own time, as it once did, let a transaction's rows start at different moments and a history row end before it began.
- **INSERT** on a system-versioned parent auto-populates the period columns: ROW START = the write's time (above), ROW END = `DateTime.MaxValue` (datetime2(7) precision = `9999-12-31 23:59:59.9999999`).
  Explicit values for a GENERATED ALWAYS column raise Msg 13536.
  Implicit insert column lists exclude GENERATED columns (so `INSERT INTO Customers (Id, Name) VALUES (...)` works without listing period columns).
- **UPDATE** on a system-versioned parent: pre-update full row is captured (`oldSnapshotNeeded` forced true), the post-SET row's ROW START is bumped to UtcNow, then `WriteHistoryRowsForUpdate` writes the captured pre-update row to the history sibling with ROW END overwritten to UtcNow (the period during which the row was current).
  Setting a GENERATED ALWAYS column in SET raises Msg 13537.
- **DELETE** on a system-versioned parent: pre-delete full row captured (`needsFullForHistory` forced true), then history row written with ROW END = UtcNow before tombstoning the current row.
- **MERGE** does all three as the lone statements do — the insert action stamps the period, the update action advances ROW START, and both the update and delete actions write the old row to history (`StampInsertedPeriod` / `StampUpdatedRow`, probed 2026-10-02 against SQL Server 2025); EF Core sends every multi-row insert as a `MERGE`.
- **A foreign key's referential action** on a system-versioned child versions the child as the matching DML does (`Simulation.WriteHistoryRow`): a cascaded delete writes the row to history, and a CASCADE / SET NULL / SET DEFAULT rewrite writes the old row to history and starts the new one's period at the statement's time (probed 2026-10-01 against SQL Server 2025).
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
- **Zero-duration versions are invisible to every form**, `ALL` included, while a row ending before it starts — which only an adopted history can hold — stays visible to `ALL` (probed 2026-10-04 against SQL Server 2025): a row updated more than once inside one transaction leaves a history row whose ROW START equals its ROW END, and real hides it from `FOR SYSTEM_TIME` while a direct `SELECT` against the history table still returns it.
  Probe-confirmed on both an engine-produced row (two UPDATEs in one transaction) and a hand-written one.
  Inside one transaction the simulator reaches the same state, every write stamping the transaction's begin time; two autocommit statements reach it only when they land on the same clock tick — which is why the tests separate them by a `WAITFOR` / sleep.
- **Time arguments are a literal, an ODBC escape (`{ts '…'}`) or a variable**, which is all real's grammar admits: a function call (`AS OF SYSUTCDATETIME()`), a parenthesized subquery, or a column reference is **Msg 102** (or **Msg 156** when the offending token is a reserved keyword, e.g. `BETWEEN 't' TO 't'`).
  A number is typed as the narrowest of `tinyint`, `smallint` and `int` it fits, `numeric` past them or with a fraction, and clashes as the statement compiles, so `AS OF 1` is Msg 206 naming `tinyint` (probed 2026-10-06 against SQL Server 2025).
  A word in the form's place is read as one real doesn't know, and the token after it refused — `FOR SYSTEM_TIME latest;` is Msg 102 at `;`.
  They're evaluated once on iteration start — no per-row re-evaluation, matching SQL Server's "constant per query" contract.
  ISO 8601 string literals with a trailing `Z` (UTC marker — EF Core 10 emits this) are accepted by datetime2 coercion; an unparseable string raises **Msg 241**.
  The argument's type is gated the way real gates it, as a comparison against the period columns: strings, the date/time family and a `sql_variant` holding either convert, `time` and binary raise **Msg 402** (`… incompatible in the greater than operator.`, or `less than or equal to` for `AS OF`), and everything else — integer, decimal, money, float, bit, uniqueidentifier — raises **Msg 206** (`Operand type clash: datetime2 is incompatible with int`), a variable's declared type as the statement compiles, so a module body is refused at `CREATE` (probed 2026-10-04 against SQL Server 2025).
- **`FOR SYSTEM_TIME` after a view's name** applies to every system-versioned table the body reads, a nested view's included (`BatchContext.InheritedSystemTime`); one of them carrying its own clause is Msg 13590 followed by Msg 4413, a body reading none is Msg 13544 naming the view at state 1, and the clause after an alias or a hint — or after any other source — is Msg 102 at `for` (probed 2026-10-04 against SQL Server 2025).
- **`FOR SYSTEM_TIME` after a CTE's name** is Msg 13544 naming the CTE at state 1, refusing the batch as it compiles (probed 2026-10-06 against SQL Server 2025).
- **A write through a `FOR SYSTEM_TIME` source's alias** — `UPDATE a … FROM t FOR SYSTEM_TIME ALL a` — is refused as the batch compiles, the source being the union of the table and its history: an `UPDATE` is Msg 4421 and a `DELETE` Msg 4417, every form alike; naming the table itself beside such a source writes it as one more source of the statement (probed 2026-10-06 against SQL Server 2025).
- **Row order**: a `FOR SYSTEM_TIME` source reads the current rows in their clustered key's order, then the history's in its own, and its columns describe as not updatable, as does a period column read from the table (probed 2026-10-04 against SQL Server 2025).
- **DROP TABLE** on a system-versioned parent or its history sibling raises Msg 13552; caller must `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)` first.
- **`ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = OFF)`** flips the parent's `HeapTable.SystemVersioning` to `null` and the history sibling's `HeapTable.IsHistoryTable` to `false` — both tables revert to plain regular status, and `DROP TABLE` on either now succeeds.
  Period definition and GENERATED ALWAYS / HIDDEN column metadata stay intact on the parent (probe-confirmed against SQL Server 2025: `sys.tables.temporal_type` flips to 0 on both, `history_table_id` clears to NULL, but the parent's `sys.columns.generated_always_type_desc` keeps `AS_ROW_START` / `AS_ROW_END` and `is_hidden` stays `True`).
  Post-SET-OFF DML semantics: INSERT still auto-populates the period columns (the per-column GENERATED ALWAYS marker drives the auto-populate in `Simulation.Insert.cs`, independent of the versioning link); explicit INSERT into a GENERATED ALWAYS column still raises Msg 13536; UPDATE and a MERGE's update still advance ROW START, through the same marker (probed 2026-10-02); DELETE doesn't copy to the (former) history table.
  Error paths: Msg 4902 if the target name doesn't resolve (the alter-table-specific table-not-found wording, distinct from Msg 208's generic name-resolution); Msg 13591 if the target exists but isn't system-versioned (fires both for plain regular tables and for the history sibling itself — the history sibling carries the `HISTORY_TABLE` role but doesn't "have" versioning, only the parent does).
- **`ALTER TABLE [schema.]name SET (SYSTEM_VERSIONING = ON [(<options>)])`** is the inverse of OFF: takes a base with a `PERIOD FOR SYSTEM_TIME` declaration and sets `base.SystemVersioning = history` + `history.IsHistoryTable = true`.
  The option list is the same one CREATE TABLE's `WITH` clause takes — `HISTORY_TABLE`, `HISTORY_RETENTION_PERIOD` and `DATA_CONSISTENCY_CHECK`, comma-separated **in any order, each optional, and the parenthesized list itself optional** (a bare `= ON` auto-names).
  The bacpac loader's phase-5 wire-up step emits this for every SqlTable carrying a `TemporalSystemVersioningHistoryTable` relationship (SqlPackage's emit shape).
  A named history table that **doesn't exist yet is created** from the base's shape rather than rejected (probe-confirmed — real builds it, so Msg 4902 never fires for the history argument); one that exists is shape-validated (below) and adopted.
  `DATA_CONSISTENCY_CHECK`, on unless written `OFF`, checks an adopted history's rows (`CheckHistoryConsistency`) on both paths: a row ending before it starts is Msg 13541, one ending in the future — the maximum included — Msg 13543, and two rows of one key overlapping Msg 13573, identical rows included, each at state 0 and ending the batch uncaught; a row ending as it starts passes (probed 2026-10-04 against SQL Server 2025).
  Setting versioning on or off rolls back with its transaction, a history the statement built going with it.
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
  | history table has a computed column | 13519 |
  | history table has a `ROWGUIDCOL` | 13580 |
  | column counts differ | 13523 |
  | column names differ at an ordinal | 13524 |
  | declared types differ (including `nvarchar` length and `datetime2` precision) | 13525 |
  | collations differ | 13526 |
  | a period column's counterpart is nullable | 13530 |
  | nullability differs, in either direction | 13531 |
  | one column is sparse and the other isn't | 13533 |

  **DEFAULT constraints and non-unique indexes on the history table are accepted**, as is a history table in a different schema from the base — all probe-confirmed.
  Every refusal in the table is catchable and ends the batch uncaught, and naming a system-versioned base as the history is Msg 13566 where another base's history is Msg 13514 (probed 2026-10-04 against SQL Server 2025).
  Msg 13510 (`Cannot set SYSTEM_VERSIONING to ON when SYSTEM_TIME period is not defined and the LEDGER=ON option is not specified.`) covers a base without a period from both paths, at state 1 for ALTER and state 2 for CREATE.
- **`HISTORY_RETENTION_PERIOD = <count> DAY[S] | WEEK[S] | MONTH[S] | YEAR[S] | INFINITE`** parses on both paths (singular and plural unit spellings both accepted) and lands on `sys.tables.history_retention_period` / `history_retention_period_unit` / `history_retention_period_unit_desc`.
  Unit codes are real's: DAY 3, WEEK 4, MONTH 5, YEAR 6, INFINITE -1 paired with period -1.
  Every system-versioned table reports the triple (INFINITE until one is set); history siblings and non-temporal tables report NULL for all three.
  A count of zero or less is **Msg 13743** (`0 is not a valid value for system versioning history retention period.` — the number unquoted), an unrecognized unit is **Msg 13744** at **severity 15** with the unit echoed as written, and a count with no unit is Msg 102.
  The unit is validated before the count, so `3 HOURS` is 13744 rather than anything about the 3.
  Real keeps at most 1,000 years — 365,242 days, 52,177 weeks, 12,000 months — and a longer period is Msg 13749 as the batch compiles, while a count past `int` or a fractional one is Msg 1080 (probed 2026-10-04 against SQL Server 2025).
  A **finite** period additionally requires the history cleanup index (**Msg 13765**) — next section.
- **Retention pruning is a read-side filter**: a history version whose ROW END fell before `now - retention` is invisible to every `FOR SYSTEM_TIME` form while a direct `SELECT` against the history table still returns it.
  That's real's own observable behavior — real filters aged rows out of `FOR SYSTEM_TIME` the moment the window passes and deletes them later from a background task (`sys.sp_cleanup_temporal_history` forces the delete) — see [Divergences](#divergences) for the part that isn't modeled.
  The cutoff is measured from the statement's frozen `UtcNow` and recomputed per enumeration, so widening the window back to INFINITE makes aged versions visible again.
- **Direct INSERT into history** raises Msg 13559, and an `UPDATE`, `DELETE` or `MERGE` of it Msg 13561 / 13560 / 13562, refused as the batch compiles; `TRUNCATE TABLE` of either table is Msg 13545, catchable and ending the batch uncaught; a `MERGE`'s insert action naming a period column is Msg 13536 and its update action setting one Msg 13537; `DROP TABLE t, t_h` reports Msg 13552 for each (probed 2026-10-04 against SQL Server 2025).
  History rows are populated only by the engine via parent UPDATE / DELETE.
- **Triggers**: a history table takes none and a system-versioned table no `INSTEAD OF` one (Msg 13569, state 1 and 2), and an `AFTER` trigger's writes share the firing statement's time (`BatchContext.FiringStatementSystemTimeUtc`), its transaction being the statement's (probed 2026-10-04 against SQL Server 2025).
- **Schema changes propagate to the history**: `ALTER TABLE … ADD` adds plain counterparts filled as the base's rows were (a NOT NULL column's default included), `DROP COLUMN` and `ALTER COLUMN` reach the history too — a NULL there refuses `NOT NULL` with Msg 515 naming the history — and `sp_rename` renames its column (probed 2026-10-04 against SQL Server 2025).
  Adding an identity column is Msg 13704 and a computed one Msg 13724, dropping a period column meets the history's cleanup index (Msg 5074, then Msg 4922), and the history itself takes no column change (Msg 13550 / 13551 / 13548), no CHECK (Msg 13564) and no PRIMARY KEY (Msg 13558), each of the last two followed by Msg 1750 at state 0.
- **A `HIDDEN` period column leaves the positional insert list**, so `INSERT t VALUES (…)` names the visible columns alone (probed 2026-10-04 against SQL Server 2025).
- **`sys.periods`** projects one row per table with a `PERIOD FOR SYSTEM_TIME` declaration of its own (`HeapTable.PeriodColumns` without `PeriodInheritedFromBase`), **excluding history siblings** — they carry a copied `PeriodColumns` for the `FOR SYSTEM_TIME` query machinery but hold no period of their own, and keying the exclusion on the inherited-copy flag rather than the history role keeps an ex-sibling out of the view after `SET (SYSTEM_VERSIONING = OFF)` clears that role.
  Row shape: `name` = `SYSTEM_TIME`, `period_type` = 1 / `period_type_desc` = `SYSTEM_TIME_PERIOD`, `object_id`, `start_column_id` / `end_column_id` = the 1-based ordinals of the ROW START / ROW END columns.
  `sys.columns.generated_always_type` reports 1 (`AS_ROW_START`) / 2 (`AS_ROW_END`) for the period columns.
  See [`catalog-views.md`](catalog-views.md).
- **`GENERATED ALWAYS AS TRANSACTION_ID | SEQUENCE_NUMBER START | END [HIDDEN]`** columns, the ledger kinds, sit on any table — a system-versioned one, one without a period, a table variable — added by `CREATE` or `ALTER TABLE … ADD`, and report `generated_always_type` 7 to 10 (`AS_TRANSACTION_ID_START` …) while their history counterparts are plain (probed 2026-10-06 against SQL Server 2025).
  A `START` is `NOT NULL` whatever the declaration omits and an `END` nullable: a non-`bigint` one is Msg 37346, a nullable `START` Msg 37347, a `NOT NULL` `END` Msg 37348, and a second column of one kind Msg 37345, each state the kind's place in that order.
  A write stamps the `START` columns with its transaction's id (`BatchContext.CurrentTransactionId`) and its row operation's number in the transaction, counted from 0 on the transaction's undo log; a rewritten row of a system-versioned table takes the next number for its new version and leaves the one after to the version it replaces, whose history row's `END` columns take it — so an `UPDATE` of two rows numbers them new, old, new, old — and a deleted row's history takes the next, while a table keeping no history numbers only its new versions.
  Explicit values are Msg 13536 and Msg 13537, as for the period columns.

## The history cleanup index

Real gives every history table it **builds** a non-unique clustered index named `ix_<history table leaf>` keyed on `(period end, period start)` — the index its background aged-data cleanup seeks through — and that index is what a finite `HISTORY_RETENTION_PERIOD` requires.
The simulator builds the same one in `BuildHistoryTable`, so it rides both sibling-building paths (CREATE TABLE's `WITH` clause and `ALTER TABLE … SET (SYSTEM_VERSIONING = ON …)` naming a table that doesn't exist yet) and both naming forms (`ix_MSSQL_TemporalHistoryFor_<id>` for an auto-named sibling).
Storage is unchanged — a clustered index is metadata plus index-id allocation, never row ordering (see [`indexes.md`](indexes.md#index-id-allocation)) — so the index's whole visible footprint is the catalog: `sys.indexes` (`index_id 1`, `CLUSTERED`, `is_unique = 0`), `sys.index_columns` (`key_ordinal` 1 = end, 2 = start, ascending, no INCLUDE), `sys.stats`, and `sp_helpindex` (`ix_CustomersHistory | clustered located on PRIMARY | Vt, Vf`).

An **adopted** history table gets no index built for it — probe-confirmed, real leaves a heap a heap — which is what makes the retention gate observable:

- **Msg 13765** (`Setting finite retention period failed on system-versioned temporal table '<db.schema.base>' because the history table '<db.schema.history>' does not contain required clustered index. Consider creating a clustered columnstore or B-tree index starting with the column that matches end of SYSTEM_TIME period, on the history table.`) fires when a finite retention period is asked for and the history table carries no clustered index whose **leading** key column is the period end column.
  **State 1** when it has no clustered index at all — a nonclustered one on the right columns doesn't count — and **state 2** when it has one leading with another column.
  Everything past the leading column is irrelevant: `(PeriodEnd)`, `(PeriodEnd DESC)`, `(PeriodEnd, PeriodStart)` and `(PeriodEnd, Id)` all satisfy it.
  A clustered columnstore index satisfies it too (probed 2026-10-04 against SQL Server 2025).
  All three entry points check identically (CREATE TABLE adopting an existing sibling, `SET (SYSTEM_VERSIONING = ON …)` turning versioning on, and a re-issue against an already-versioned base), and the statement is refused whole — a rejected CREATE TABLE leaves no base behind and a rejected ALTER leaves the link and the old retention pair untouched.
  `HISTORY_RETENTION_PERIOD = INFINITE` (and an omitted clause) is accepted on a plain heap history table.
- **Msg 13766** (`Cannot drop the clustered index '<table as written>.<index>' because it is being used for automatic cleanup of aged data. Consider setting HISTORY_RETENTION_PERIOD to INFINITE on the corresponding system-versioned temporal table if you need to drop this index.`) refuses a `DROP INDEX` of a history table's clustered index while its base is on a finite retention period, through both the `name ON table` and the deprecated `table.name` forms.
  The pin is on the live link rather than a flag: relaxing the base back to INFINITE — or turning versioning off — releases the index immediately, and a *nonclustered* index on the same history table drops as usual.

`ALTER INDEX … DISABLE` against the cleanup index is **not** gated; real accepts it (probe-confirmed).

## Data-model footprint

- **`HeapColumn`** gained `GeneratedAs` (`GeneratedAlwaysAsRow.None / Start / End`) and `IsHidden` fields.
- **`HeapTable`** gained `PeriodColumns: (int StartOrdinal, int EndOrdinal)?` (set at construction), `SystemVersioning: HeapTable?` (mutable; set after history is auto-created — points from parent → history; null on regular and history tables), `IsHistoryTable: bool` (mutable; true on the history sibling), the `HistoryRetentionPeriod` / `HistoryRetentionUnit` pair with its `HistoryRetentionCutoff(asOf)` reader, and `PeriodInheritedFromBase: bool`.
  That last flag is what keeps Msg 13574 honest: `BuildHistoryTable` copies the base's `PeriodColumns` onto the sibling so the `FOR SYSTEM_TIME` row source can read the ordinals off either side, and without the flag an ex-sibling could never be re-linked after `SET (SYSTEM_VERSIONING = OFF)`.
- **Parser scope:** `ParseColumnList`'s `pendingPeriod: List<(string StartCol, string EndCol)>?` carries the period names through column-list parsing; `ResolvePeriodColumns` (in `Simulation.Create.cs`) validates and resolves to ordinals.
  `ParseSystemVersioningOnOptions` parses the option list into a `SystemVersioningOptions` and is shared by the CREATE `WITH (…)` and ALTER `SET (…)` wrappers, so the two grammars can't drift.
  `BuildHistoryTable` mirrors the parent column shape into the history sibling and adds the `ix_…` cleanup index, `AutoHistoryTableName` generates the `MSSQL_TemporalHistoryFor_…` name, `ValidateHistoryTableShape` runs the adoption checks, and `RequireHistoryCleanupIndex` is the Msg 13765 gate all three linking sites call.
  Msg 13766 rides `DropOneIndex`'s `RetentionCleanupDependsOn` (`Simulation.Drop.cs`), which walks the owning database for the base pointing at this history table rather than storing a back-reference.
- **Query scope:** `Selection.ParseOptionalForSystemTime` peeks for `FOR SYSTEM_TIME` between the FROM source's table name and any alias, and returns `null` when absent (cursor restored) or a `TemporalRowSource` when present.
  The FROM machinery decodes every row of the source against the parent's heap, so the row source brings a history row's off-row values inline before yielding it; read through the parent, its LOB page indexes named the parent's unrelated chains — an old version showed a newer row's text, or Msg 601 when the lengths differed.
  That one row source serves all five forms — a `TemporalQueryKind` discriminator plus up to two bound expressions, parsed by `ParseTemporalTimeArgument` (the literal-or-variable grammar) and evaluated once per enumeration.
- **Cursor scope:** a `FOR SYSTEM_TIME` FROM slot never resolves into a `CursorSourcePlan` (`TryBuildCursorPlan` declines a source whose rows a `TemporalRowSource` produces), so such a cursor is a read-only snapshot.
  That matches real, probe-confirmed: `sys.dm_exec_cursors(@@SPID).properties` reports `Snapshot | Read Only` with the row count for `AS OF` / `ALL` / `BETWEEN` / `FROM…TO` / `CONTAINED IN` alike, `SCROLL` included, so positioned `WHERE CURRENT OF` DML through one is **Msg 16929** (`The cursor is READ ONLY.`) and `DYNAMIC TYPE_WARNING` fires Msg 16956.
  A cursor over a versioned table *without* a `FOR SYSTEM_TIME` clause reads the base heap, so it stays DYNAMIC and updatable like any other → [`cursors.md`](cursors.md#which-shapes-are-navigable).

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
- **Auto-generated history names carry the simulator's own object ids**, which start at ~100 rather than real's allocator range, so `MSSQL_TemporalHistoryFor_102` is structurally right and never byte-matches a real server's name for the same DDL.
  The collision suffix is deterministic where real's is random, and the cleanup index's `ix_`-prefixed name inherits both.
- **The clock**: real's `SYSUTCDATETIME` advances in steps of about 4 ms on the Linux reference (26 distinct values across 20,000 calls in 100 ms, probed 2026-10-04), so two autocommit writes a few milliseconds apart share a time there and leave a zero-duration version every form hides, where the simulator's clock is the host's and keeps them apart.
  A differential probe of history counts has to separate its writes by more than that step.
- **A ledger column's transaction id** is real's internal log transaction id, which nothing else reports; the simulator stamps `CURRENT_TRANSACTION_ID()`'s, so the values differ while equality within a transaction and order across them hold.
- **Msg 13544 qualified-name format**: real SQL Server pads temp-table names with their internal allocation suffix (`#x____...___…000000000148`); the simulator emits the bare `tempdb.dbo.#x` form.
  Same Msg number / framing, less verbose name.

## Not modeled yet

- **Msg 13590's line** is a line of the view's definition on real; here it is the referencing statement's.
