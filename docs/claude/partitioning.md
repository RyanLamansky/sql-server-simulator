# Table and index partitioning

Partition functions and schemes, `$PARTITION`, tables and indexes placed `ON scheme(column)`, `TRUNCATE TABLE … WITH (PARTITIONS …)`, `ALTER TABLE … SWITCH`, and the filegroup statements a scheme leans on.
Everything here was probed 2026-09-27 against SQL Server 2025; the refusals' numbers, states and wordings live on the factories in `SimulatedSqlException.PartitionErrors.cs`, and `PartitioningTests.cs` is the behavior contract.

**Rows are not stored apart.**
A partition is a logical assignment: `PartitionPlacement` maps a row to its partition by decoding the partition column and asking the function, so a `SPLIT` or `MERGE` re-partitions existing rows for free and nothing ever moves between pages.
The cost is paid by readers of per-partition counts — `sys.partitions`, `sys.dm_db_partition_stats`, `sys.allocation_units`, `sp_spaceused`, a partition `TRUNCATE`, a `SWITCH` — which read every row of the table (`PartitionPlacement.Census`, shared across the indexes aligned on one placement within one catalog read).

## Partition functions

A function is database-scoped, not a schema object: its name is one part (a dotted name is Msg 102 at the dot), it is compared under the database collation, it never appears in `sys.objects`, and a scheme may share its name.
Its id comes from a per-database counter starting at 65536; **every** `CREATE PARTITION FUNCTION` that gets past its parse spends one — a duplicate name, a bad type, an unconvertible boundary — and a dropped function's id is never reused.

- **Types.** Refused (Msg 7704): the LOB and MAX types, `timestamp`, `json`, `xml` at state 1; a name that isn't a type at state 2; the CLR types and every alias type at state 3.
  `bit`, `float`, `money`, `sql_variant`, `smalldatetime` and `time` are accepted.
  A string type keeps a written `COLLATE`, else the database's.
- **Boundaries** are any constant expressions (`1+1` works, a variable is Msg 137), converted to the parameter type as an assignment converts: a decimal rounds to the parameter's scale, a decimal into `int` truncates.
  Any refused conversion — illegal pair, failed parse, overflow — is Msg 7705 at the written ordinal; a string or binary longer than the parameter is Msg 7720.
  The list is sorted (a `sql_variant` one by the variant family ordering), with the class-0 Msg 7709 when it wasn't written sorted.
  Duplicates are Msg 7708 naming the two **written** ordinals — checked before the unsorted warning, which a failing statement never sends.
  `NULL` is a legal boundary and sorts first; `VALUES ()` is a one-partition function; past 14,999 boundaries is Msg 7719.
- **Mapping.** `RANGE LEFT` puts a boundary value in the partition it closes, `RANGE RIGHT` in the one it opens; NULL orders below every value, so it lands in partition 1 unless a `RANGE RIGHT` function has a NULL boundary, which gives NULL its own partition 2.

`$PARTITION.function(value)` tokenizes `$partition` as one word (like `$action`), so a bracketed `[$partition]` is an ordinary identifier and reads as a UDF qualifier (Msg 4121), exactly as real.
It takes a database qualifier (`db.$partition.f(…)`); a qualifier naming no database is Msg 208 state 212 and a missing function state 213, both naming the call as written.
The value converts as an assignment does — Msg 245 for a failed parse, Msg 8115 for an overflow — and the function is looked up by name at every evaluation, so a cached plan follows a split, merge or drop-and-recreate.
A second argument is Msg 8144 state 80.

**`SPLIT RANGE` / `MERGE RANGE`.**
Splitting at an existing boundary is Msg 7721 naming its 1-based position; merging a value that isn't a boundary is Msg 7715, and merging anything out of a function with no boundaries left is Msg 7716 — merging the last boundary itself succeeds.
A split needs every scheme on the function to have a `NEXT USED` filegroup (Msg 7710 as an error, naming the first scheme by data-space id that lacks one).
The new partition — the one holding the new boundary value: the left half under `RANGE LEFT`, the right half under `RANGE RIGHT` — takes each scheme's next-used filegroup, which is then cleared; a merge drops the partition holding the merged value, along with its filegroup slot.
Both bump `modify_date`.

## Partition schemes

Ids come from a second per-database counter starting at 65601, never reused.
Unlike a function, not every failure spends one: an unknown function (Msg 208), a name another scheme holds (Msg 2714) and too few filegroups (Msg 7707) are checked first and spend nothing, while `ALL TO` with several filegroups (Msg 7723), an unknown filegroup (Msg 208) and a name a **filegroup** holds (Msg 2714) come after the allocation.
A filegroup is any identifier form or a string literal; the bare word `PRIMARY` is reserved (Msg 156).

One filegroup per partition; one more becomes the `NEXT USED` filegroup, announced by the class-0 Msg 7712, and any past that are ignored with Msg 7713.
`ALL TO (fg)` maps every partition and marks `fg` next used.
`ALTER PARTITION SCHEME … NEXT USED fg` marks one (Msg 208 state 61 for an unknown one); `NEXT USED` with no filegroup clears the mark, sending the class-0 Msg 7710 when there was none.
`sys.destination_data_spaces` lists the next-used filegroup as one destination past the partitions.

Dropping a function a scheme uses is Msg 7706, one a schema-bound module's body calls through `$PARTITION` is Msg 3729 state 3 (`SchemaBinding.FindPartitionFunctionReference`), and a scheme a table or index is placed on Msg 7717.

## Filegroups

`ALTER DATABASE … ADD FILEGROUP name [CONTAINS …]` and `REMOVE FILEGROUP name` maintain `Database.Filegroups`; `CREATE DATABASE`'s file list registers each `FILEGROUP name` it declares, in order.
A new filegroup takes one past the highest id held, so removing the newest and adding another reuses its id.
Removal refuses `PRIMARY`, a filegroup with files, and a filegroup a scheme maps a partition or its next-used slot to (Msg 5042 at three states).
The files in a filegroup, and `MODIFY FILEGROUP`, are in [`database-options.md`](database-options.md#files-and-filegroups).
An `ON` clause naming neither a scheme nor a registered filegroup (nor `"default"`) is Msg 1921.

## Placement

`CREATE TABLE … ON scheme(column)` places the table's rows — the heap, or whichever clustered index it gets — on the scheme (`HeapTable.Partitioning`).
A key constraint or index written without its own `ON` is **aligned**: it lands where the rows are.
One written `ON [filegroup]` is not, and lands on that filegroup — see [Filegroup placement](#filegroup-placement).
A clustered index created `ON` a scheme moves the rows there, one created `ON [PRIMARY]` moves them off, and dropping the clustered index leaves the heap where the index was.

The partition column must resolve (Msg 1911), be named once (Msg 2703, ahead of the one-column count check, Msg 2726 — also what a scheme written without a column list gets), be persisted if computed (Msg 7724), and have exactly the parameter's type, length included (Msg 7726), and collation (Msg 7727).
A unique index or key must carry the partition column in its key (Msg 1908, followed by Msg 1750 for a constraint), `TEXTIMAGE_ON` on a partitioned table is Msg 1707, and the partition column can be neither dropped nor altered — even a nullability change — while the table is partitioned on it (Msg 5074 naming the table, then 4922).

**Catalog.**
`sys.indexes.data_space_id` and `sys.data_spaces`' `PS` rows are what SMO's `IsPartitioned` probe reads; `sys.tables.lob_data_space_id` names the scheme for a partitioned table with a LOB column.
The partition column carries `partition_ordinal` 1 in `sys.index_columns`: flagged where the index already lists it, else added as one more column that is neither key nor included — after the includes for a nonclustered index, in column order for a clustered index, and as the heap's only row (index_id 0).
`sys.stats_columns` lists it at the same position, though never an included column.
`sys.partitions` / `sys.dm_db_partition_stats` / `sys.allocation_units` report one row per partition of each aligned index, `sys.allocation_units.data_space_id` naming the partition's filegroup; `sp_help` says the table and its indexes are located on the scheme.

## Filegroup placement

Off a scheme, the rows — the heap, or the clustered index — sit on a filegroup (`HeapTable.FilegroupId`), as does each nonclustered index or key (`Index.FilegroupId`, `KeyConstraint.FilegroupId`) and the LOB data (`HeapTable.LobFilegroupId`); all probed 2026-09-28 against SQL Server 2025.
- A `CREATE TABLE` without `ON` lands on the database's **default** filegroup, as does `SELECT … INTO`; `ON [default]` names it too.
- A clustered key or index moves the rows onto its own `ON`, and dropping it leaves the heap there.
- A nonclustered index or key without its own `ON` lands where the rows are when it is created.
- The LOB data lands on `TEXTIMAGE_ON`'s filegroup, else where the rows were at creation, and stays there when a clustered index moves them.
  `TEXTIMAGE_ON` on a table without a LOB column is **Msg 1709**.

`sys.indexes.data_space_id`, `sys.tables.lob_data_space_id`, `sys.allocation_units.data_space_id` (the LOB unit's the LOB filegroup) and `sp_help`'s `Data_located_on_filegroup` and `located on …` descriptions read it.

A filegroup constrains what lands on it:
- an unknown one is **Msg 1921**, and one that is read-only takes no new table or index (**Msg 1924** state 2);
- one without files takes a table but not its rows: an `INSERT` into it, or an index built on it over a table with rows, is **Msg 622** state 3 — the index build ending its statement;
- a read-only one refuses a write reaching a rowset on it with **Msg 652**, naming the heap as `""` — an `UPDATE` reaching a nonclustered index only when it changes a column the index keys or includes;
- `REMOVE FILEGROUP` refuses one holding a table, index or LOB data (**Msg 5042** state 8), and `REMOVE FILE` a non-primary file whose filegroup holds a table with rows or with the pages deleted rows left (**Msg 5042** state 1).

**Divergences.**
Msg 652's `RowsetId` is the simulator's synthetic `sys.partitions.partition_id`, not real's allocation-derived one; the write refusals are settled once per statement, so a statement writing no row is refused too; and the checks read an unpartitioned table only — a scheme's read-only or file-less partition filegroup takes writes.

## `TRUNCATE TABLE … WITH (PARTITIONS (…))`

Each bound is any expression — a variable, `$PARTITION.pf(x)`, `1.9` (partition 1), `'3'` — and `n TO m` a range.
An unpartitioned table is Msg 7729 state 3 whatever the number; then per bound Msg 7722 for a number out of range, Msg 7728 for a reversed range, and Msg 7711 for a partition listed twice (ranges included).
Every index must be aligned on the table's function (Msg 3756 naming the first that isn't).
The rows go as ordinary deletes — rolled back with a transaction — and, unlike a whole-table truncate, the identity high-water mark stays where it was.

## `ALTER TABLE … SWITCH`

`ALTER TABLE source SWITCH [PARTITION n] TO target [PARTITION m] [WITH (WAIT_AT_LOW_PRIORITY (…))]`; a partition number is any expression.
Checks, in real's order: the target resolves (Msg 1088 state 29); the two differ (Msg 4955, bare names); a system-versioned source is Msg 13546 and a period-less source into a target with a period Msg 13577; a number on an unpartitioned side is ignored with the class-0 Msg 4903 (state 1 source, 2 target), a partitioned side needs one (Msg 4911) in range (Msg 4950); then the target — or its partition — must be empty (Msg 4905 / 4904), **ahead of every shape check**.
Then the shapes: column count, and per column name, type, collation, nullability and computed definition; a clustered index on one side only; every target index needs an identical source index (a source may carry extra ones); a target foreign key needs a matching source one; a source other tables' foreign keys reference is Msg 4967.

The last check is static reasoning over CHECK constraints (`ValueDomain` in `Simulation.PartitionSwitch.cs`), and it is where real is most particular:

- What the source admits for a column is its partition's range when it is the source's partition column, narrowed by its enabled, **trusted** CHECK constraints over that column **alone** — a constraint that also reads another column counts for nothing, even as a conjunct.
- Readable shapes are comparisons against constants either way round, `BETWEEN`, `IN` lists, `AND`, `OR` and `IS [NOT] NULL`; integer bounds close over the next integer, so `a >= 11` proves `a > 10`.
  NULL gets through a CHECK unless something rules it out, so a nullable source needs `IS NOT NULL` for any partition but the one NULL maps to.
- Each enabled target CHECK constraint must then hold: one over a single column the source partitions on or constrains is reasoned about the same way (Msg 4972 when it fails); any other needs a source constraint of the same definition (Msg 4971).
- Last, what the source admits for the partition column must fit the target partition: Msg 4973 from a partition, and from a table Msg 4982 when no CHECK — trusted or not — over that column alone exists at all, else Msg 4972.

The rows move as deletes and inserts in the transaction's undo log, so a rollback restores both tables; no trigger fires, identity values travel as they are without touching the target's high-water mark, and `@@ROWCOUNT` reads 0.

## Rebuilding one partition

`ALTER INDEX … REBUILD | REORGANIZE PARTITION = n` and `ALTER TABLE … REBUILD PARTITION = n` accept a partition of a partitioned index as the no-ops they are; a number past its partitions is Msg 7730 in the alter-index wording naming the index, or for a partitioned heap the alter-table wording naming the table.
An unpartitioned target keeps the Msg 7729 / 7735 refusals ([`indexes.md`](indexes.md), [`alter-table.md`](alter-table.md)).

## Errors end the batch

Every partitioning refusal above ends its batch, whatever `SET XACT_ABORT` says — including the SWITCH and TRUNCATE ones and the `$PARTITION` conversion failure — and rolls back an open transaction (probed for a `CREATE PARTITION FUNCTION`; the factories carry it as one class).
The DDL itself rolls back with a transaction (function, scheme, split / merge and next-used state), and raises the six `*_PARTITION_FUNCTION` / `*_PARTITION_SCHEME` DDL events with no schema name.

## BACPAC

The loader creates the functions and schemes and places tables, indexes and key constraints on them; see [`bacpac-loader.md`](bacpac-loader.md).

## Divergences

- **Page counts.** A partition's in-row pages are the heap pages holding at least one of its rows, so partitions sharing a page each count it; the LOB allocation unit stays on partition 1 where real gives every partition one.
  The page-count divergence unpartitioned tables already carry ([`catalog-views.md`](catalog-views.md)) applies too.
- **`sys.partitions` of a filtered index** counts every row, as it does unpartitioned.
- **Boundary variants.** A `decimal` boundary's `SQL_VARIANT_PROPERTY` reports `numeric` and the simulator's decimal width, and a `varbinary(n)` one its actual length rather than `n` — the variant surface's own limits.
- **A partition number that won't convert** (`PARTITIONS ('x')`, `NULL`) is reported as 0 where real prints an arbitrary number.
- **Permissions.** The DDL is gated on `db_owner` / `db_ddladmin` membership, where real checks `ALTER ANY DATASPACE`.

## Not modeled yet

- **A `FILESTREAM` column** is a syntax error here, where real raises Msg 5508 for a string `(max)` type, Msg 1969 for `varbinary(max)` with no FILESTREAM filegroup to default to, Msg 1921 state 3 for a `FILESTREAM_ON` naming no filegroup and Msg 1724 for one naming a filegroup that isn't a FILESTREAM one (probed 2026-09-30); `FILESTREAM_ON` on a table without one is Msg 1716.
- **LOB data spilling onto a filegroup without files** is written, where real refuses it with Msg 622 once a value leaves the row.
- **An indexed view's index on a scheme** reports `data_space_id` 1 and no `sys.partitions` rows at all, where real reports the scheme and one row per partition counting the view's rows (probed 2026-09-30): the simulator never materializes the view, so it has no row counts to place.
- **Per-partition data compression** (`REBUILD PARTITION = n WITH (DATA_COMPRESSION = …)`), and so SWITCH's compression check (Msg 11406, which real raises switching out of a page-compressed history table).
- **`SELECT … INTO … ON filegroup`** is a syntax error here, where real places the table on the filegroup (`data_space_id` 1 for `[primary]` and `[default]`), refuses an unknown one with Msg 1921 and a scheme name, written with or without a column list, with Msg 2726 (probed 2026-09-30).
- **Partition-level lock escalation** — `LOCK_ESCALATION = AUTO` still escalates to the table.
- **SWITCH's remaining checks**: filegroup agreement between the two sides, non-aligned indexes on a partitioned side, and reasoning beyond the readable shapes above (`NOT`, functions, discrete non-integer types such as `date`).
