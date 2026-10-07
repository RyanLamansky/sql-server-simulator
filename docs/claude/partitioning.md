# Table and index partitioning

Partition functions and schemes, `$PARTITION`, tables and indexes placed `ON scheme(column)`, `TRUNCATE TABLE … WITH (PARTITIONS …)`, `ALTER TABLE … SWITCH`, and the filegroup statements a scheme leans on.
Everything here was probed 2026-09-27 against SQL Server 2025; the refusals' numbers, states and wordings live on the factories in `SimulatedSqlException.PartitionErrors.cs`, and `PartitioningTests.cs` is the behavior contract.

**Rows are not stored apart.**
A partition is a logical assignment: `PartitionPlacement` maps a row to its partition by decoding the partition column and asking the function, so a `SPLIT` or `MERGE` re-partitions existing rows for free and nothing ever moves between pages.
The cost is paid by readers of per-partition counts — `sys.partitions`, `sys.dm_db_partition_stats`, `sys.allocation_units`, `sp_spaceused`, a partition `TRUNCATE`, a `SWITCH` — which read every row of the table (`PartitionPlacement.Census`, shared across the indexes aligned on one placement within one catalog read).

## Partition functions

A function is database-scoped, not a schema object: its name is one part (a dotted name is Msg 102 at the dot), it is compared under the database collation, it never appears in `sys.objects`, and a scheme may share its name.
Its id comes from a per-database counter starting at 65536; **every** `CREATE PARTITION FUNCTION` that gets past its parse spends one — a duplicate name, a bad type, an unconvertible boundary — and a dropped function's id is never reused.

- **Types.** Refused (Msg 7704): the LOB and MAX types, `timestamp`, `json`, `xml` at state 1, and `vector`, named `sys.vector`; a name that isn't a type at state 2; the CLR types and every alias type at state 3, `sysname` among them, an alias named as written (probed 2026-10-05).
  `bit`, `float`, `money`, `sql_variant`, `smalldatetime` and `time` are accepted.
  A parameter declared `numeric` keeps the spelling (`PartitionFunction.SpelledNumeric`): `sys.partition_parameters` reports type 108, and only a column spelled `numeric` partitions on it, a `decimal` one being Msg 7726, as the reverse is (probed 2026-10-05 against SQL Server 2025).
  A string type keeps a written `COLLATE`, else the database's; a collation that doesn't exist is Msg 448 state 3, and an empty parameter list `pf ()` Msg 7702, both while the batch compiles.
- **Boundaries** are any constant expressions (`1+1` works, a variable is Msg 137, a subquery Msg 1046 while compiling), converted to the parameter type as an assignment converts: a decimal rounds to the parameter's scale, a decimal into `int` truncates.
  A refused conversion is Msg 7705 at the written ordinal — state 1 for a pair an assignment can't convert implicitly (`getdate()` into `int`, `20200101` into `date`), state 2 for a value whose conversion fails or overflows.
  A string or binary longer than the parameter is Msg 7720, unless what runs past it is only trailing spaces or trailing zero bytes; and a `varchar` boundary keeps no trailing spaces at all, a `varbinary` one no trailing zero bytes, where an `nvarchar` one loses only what its length can't hold (`'a  '` in `varchar(5)` is `'a'`, `0x0100` in `varbinary(4)` is `0x01`; probed 2026-10-05).
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
Both bump `modify_date`, and both carry each rowset's per-partition compression along (see [Data compression per partition](#data-compression-per-partition)).
Any other word after `pf ()` is a syntax error; a `DROP` there is Msg 156 followed by Msg 343 for the word after it, real reading it on as a statement of its own.
Every function and scheme statement rejects a token left after it (Msg 102), as `… VALUES (1), (2)` meets at the comma, and needs `ALTER ANY DATASPACE` — `db_ddladmin`'s, granted or denied like any database permission — without which it is Msg 6004 state 2, ending the batch (probed 2026-10-06 against SQL Server 2025).

## Partition schemes

Ids come from a second per-database counter starting at 65601, never reused.
Unlike a function, not every failure spends one: an unknown function (Msg 208), a name another scheme holds (Msg 2714) and too few filegroups (Msg 7707) are checked first and spend nothing, while `ALL TO` with several filegroups (Msg 7723), an unknown filegroup (Msg 208) and a name a **filegroup** holds (Msg 2714) come after the allocation.
A filegroup is any identifier form or a string literal; the bare word `PRIMARY` is reserved (Msg 156).

One filegroup per partition; one more becomes the `NEXT USED` filegroup, announced by the class-0 Msg 7712, and any past that are ignored with Msg 7713.
`ALL TO (fg)` maps every partition and marks `fg` next used; `[default]`, in either list or `NEXT USED`, names the default filegroup.
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
A clustered index created `ON` a scheme moves the rows there, one created `ON [PRIMARY]` moves them off, and dropping the clustered index leaves the heap where the index was — unless the drop says `WITH (MOVE TO …)`, which puts the heap on that scheme by the column it names or unpartitioned on that filegroup (probed 2026-10-07 against SQL Server 2025).

The partition column must resolve (Msg 1911), be named once (Msg 2703, ahead of the one-column count check, Msg 2726 — also what a scheme written without a column list gets), be persisted if computed (Msg 7724), not be sparse (Msg 1978 state 2), and have exactly the parameter's type, length included (Msg 7726), and collation (Msg 7727).
A key constraint's own `ON` refused any of these ways is followed by Msg 1750 (probed 2026-10-05).
A unique index or key must carry the partition column in its key (Msg 1908, followed by Msg 1750 for a constraint), `TEXTIMAGE_ON` on a partitioned table is Msg 1707, and the partition column can be neither dropped nor altered — even a nullability change — while the table is partitioned on it (Msg 5074 naming the table, then 4922).

**Catalog.**
`sys.indexes.data_space_id` and `sys.data_spaces`' `PS` rows are what SMO's `IsPartitioned` probe reads; `sys.tables.lob_data_space_id` names the scheme for a partitioned table with a LOB column.
The partition column carries `partition_ordinal` 1 in `sys.index_columns`: flagged where the index already lists it, else added as one more column that is neither key nor included — after the includes for a nonclustered index, in column order for a clustered index, and as the heap's only row (index_id 0).
`sys.stats_columns` lists it at the same position, though never an included column.
`sys.partitions` / `sys.dm_db_partition_stats` / `sys.allocation_units` report one row per partition of each aligned index, `sys.allocation_units.data_space_id` naming the partition's filegroup; `sp_help` says the table and its indexes are located on the scheme, and an XML index's `data_space_id` names it too.
Every row count these and `OBJECTPROPERTYEX`'s `Cardinality`, `sp_statistics` and `DBCC CHECKTABLE` report is of the rows a scan reads (`Heap.CountLiveRows`) — never `Heap.RowCount`, which keeps a deleted row's slot.

## Filegroup placement

Off a scheme, the rows — the heap, or the clustered index — sit on a filegroup (`HeapTable.FilegroupId`), as does each nonclustered index or key (`Index.FilegroupId`, `KeyConstraint.FilegroupId`) and the LOB data (`HeapTable.LobFilegroupId`); all probed 2026-09-28 against SQL Server 2025.
- A `CREATE TABLE` without `ON` lands on the database's **default** filegroup, as does `SELECT … INTO`; `ON [default]` names it too.
  `SELECT … INTO t ON fg` places the new table on a filegroup, named or as a string; an unknown one is Msg 1921, and a scheme — with a column list or without — Msg 2726, as the statement runs (probed 2026-09-30 against SQL Server 2025).
- A clustered key or index moves the rows onto its own `ON`, and dropping it leaves the heap there.
- A nonclustered index or key without its own `ON` lands where the rows are when it is created.
- The LOB data lands on `TEXTIMAGE_ON`'s filegroup, else where the rows were at creation, and stays there when a clustered index moves them.
  `TEXTIMAGE_ON` on a table without a LOB column is **Msg 1709**.

`sys.indexes.data_space_id`, `sys.tables.lob_data_space_id`, `sys.allocation_units.data_space_id` (the LOB unit's the LOB filegroup) and `sp_help`'s `Data_located_on_filegroup` and `located on …` descriptions read it.

A filegroup constrains what lands on it:
- an unknown one is **Msg 1921**, and one that is read-only takes no new table or index (**Msg 1924** state 2);
- one without files takes a table but not its rows: an `INSERT` into it, or an index built on it over a table with rows, is **Msg 622** state 3 — the index build ending its statement;
  so does a `TEXTIMAGE_ON` filegroup without files, row by row, for a value the LOB unit takes — any `text`, `ntext` or `image` value, or a `(max)` or `xml` one past 8,000 bytes — while a shorter one stays in the row and is written (`RowDecoder.HoldsLobUnitValue`); the error ends the batch and rolls the transaction back, and a `TRY` catches it (probed 2026-10-06 against SQL Server 2025);
- a read-only one refuses a write reaching a rowset on it with **Msg 652**, naming the heap as `""` — an `UPDATE` reaching a nonclustered index only when it changes a column the index keys or includes;
- `REMOVE FILEGROUP` refuses one holding a table, index or LOB data (**Msg 5042** state 8), and `REMOVE FILE` a non-primary file whose filegroup holds a table with rows or with the pages deleted rows left (**Msg 5042** state 1).

**Divergences.**
Msg 652's `RowsetId` is the simulator's synthetic `sys.partitions.partition_id`, not real's allocation-derived one; the write refusals are settled once per statement, so a statement writing no row is refused too; and the checks read an unpartitioned table only — a scheme's read-only or file-less partition filegroup takes writes.

## `TRUNCATE TABLE … WITH (PARTITIONS (…))`

The option list takes `PARTITIONS` alone: any other word is refused at itself when a list follows it and at the token after it otherwise, so `WITH (MAXDOP = 1)` is Msg 102 at `=` (probed 2026-10-06 against SQL Server 2025).
Each bound is any expression — a variable, `$PARTITION.pf(x)`, `1.9` (partition 1), `'3'` — and `n TO m` a range.
An unpartitioned table is Msg 7729 state 3 whatever the number; then per bound Msg 7722 for a number out of range, Msg 7728 for a reversed range, and Msg 7711 for a partition listed twice (ranges included); 7729 and 7722 name the table as written (`'dbo.t'`), as does Msg 4708 for a view.
Every index must be aligned on the table's function: one partitioned on another column is Msg 4716, any other Msg 3756 naming the first that isn't (probed 2026-10-05).
The rows go as ordinary deletes — rolled back with a transaction — and, unlike a whole-table truncate, the identity high-water mark stays where it was.

## `ALTER TABLE … SWITCH`

`ALTER TABLE source SWITCH [PARTITION n] TO target [PARTITION m] [WITH (WAIT_AT_LOW_PRIORITY (…))]` — a word straight after the target is read on and the token past it refused, so `… TO t garbage;` is Msg 102 at `;` (probed 2026-10-05 against SQL Server 2025); a partition number is an integer-typed expression (see [Partition numbers](#partition-numbers)), and the option list takes `WAIT_AT_LOW_PRIORITY` alone (Msg 102 state 170 otherwise), whose `ABORT_AFTER_WAIT` takes `NONE`, `SELF` or `BLOCKERS`.
Checks, in real's order (probed 2026-09-27 and 2026-10-05): the target resolves — Msg 1088 state 29, or Msg 4949 for a view, both naming it as written; the two differ (Msg 4955, the source by its name and the target as written); a system-versioned source is Msg 13546 and a period-less source into a target with a period Msg 13577; a number on an unpartitioned side is ignored with the class-0 Msg 4903 (state 1 source, 2 target), a partitioned side needs one (Msg 4911) in range (Msg 4950); an index of a partitioned side that isn't partitioned is Msg 7733 state 4; then the target — or its partition — must be empty (Msg 4905 / 4904), **ahead of every shape check**, and change tracking on either side is Msg 4900 (the target's first, state 1).
Then the shapes: column count, and per column name, type, collation, nullability, persistence (Msg 4946), computed definition, sparse storage (Msg 11412) and `ROWGUIDCOL` (Msg 4958); two partitioned sides' partition columns (Msg 4953); the switched rowsets' `DATA_COMPRESSION` (Msg 11406, per partition) and filegroups (Msg 4938 between partitions, 4939 naming the unpartitioned table first, 4940 between two unpartitioned tables, probed 2026-10-06); a clustered index on one side only; every enabled target index needs an identical source index (a source may carry extra ones, a disabled target index asks nothing), a partitioned nonclustered index carrying its partition column as one more included column when its key leaves it out, so an aligned `(b)` matches an unpartitioned `(b) INCLUDE (a)`; an indexed view over a partitioned source is Msg 11401 (no view index here is partitioned), and one over the target the source lacks Msg 11402; a target foreign key needs a matching source one; a source other tables' foreign keys reference is Msg 4967.

The last check is static reasoning over CHECK constraints (`ValueDomain` in `Simulation.PartitionSwitch.cs`), and it is where real is most particular:

- What the source admits for a column is its partition's range when it is the source's partition column, narrowed by its enabled, **trusted** CHECK constraints over that column **alone** — a constraint that also reads another column counts for nothing, even as a conjunct.
- Readable shapes are comparisons against constants either way round, `BETWEEN`, `IN` lists, `AND`, `OR`, `IS [NOT] NULL`, and `NOT`, `<>`, `NOT BETWEEN` and `NOT IN` over any of those as the complement.
  Bounds compare as the numbers they are, with no integer closure (probed 2026-10-05): `a >= 11` proves `a > 10` and `a > 10.5` proves `a > 10`, but `a > 10` doesn't prove `a >= 11` nor `a < 21` prove `a <= 20`, so a `RANGE LEFT` partition `(10, 20]` doesn't fit a `RANGE RIGHT` one `[11, 21)`.
  A string constant bounding a date or datetime column reads as the value it converts to.
  NULL gets through a CHECK unless something rules it out, so a nullable source needs `IS NOT NULL` for any partition but the one NULL maps to.
- Each enabled target CHECK constraint must then hold: one over a single column the source partitions on or constrains is reasoned about the same way (Msg 4972 when it fails); any other needs a source constraint of the same definition (Msg 4971).
- Last, what the source admits for the partition column must fit the target partition: Msg 4973 from a partition, and from a table Msg 4982 when no CHECK — trusted or not — over that column alone exists at all, else Msg 4972.

The rows move as deletes and inserts in the transaction's undo log, so a rollback restores both tables; no trigger fires, identity values travel as they are without touching the target's high-water mark, and `@@ROWCOUNT` reads 0.

## Rebuilding one partition

`ALTER INDEX … REBUILD | REORGANIZE PARTITION = n` and `ALTER TABLE … REBUILD PARTITION = n` accept a partition of a partitioned index, rebuilding nothing but its compression; a number past its partitions is Msg 7730 in the alter-index wording naming the index — Msg 2586 for `REORGANIZE` — or for a partitioned heap the alter-table wording naming the table.
An unpartitioned target keeps the Msg 7729 / 7735 refusals ([`indexes.md`](indexes.md), [`alter-table.md`](alter-table.md)).
`ALTER INDEX ALL … PARTITION = n` over a table with an index that isn't partitioned is Msg 7733 state 2, naming the first partitioned index and the first that isn't; a rebuild of one partition of a disabled index is Msg 1973, and takes only `SORT_IN_TEMPDB`, `MAXDOP`, `ONLINE`, `RESUMABLE`, `MAX_DURATION` and the two compression options (Msg 155 in the `ALTER INDEX REBUILD PARTITION` wording otherwise; probed 2026-10-05).

A partitioned table's `UPDATE STATISTICS` takes `ON PARTITIONS (…)` whatever its statistics, and `INCREMENTAL = ON` unless an index isn't partitioned (Msg 9108 state 3); an unpartitioned table keeps Msg 9108 / 9111 ([`indexes.md`](indexes.md)).

## Partition numbers

Wherever `ALTER INDEX`, `ALTER TABLE … REBUILD` and `SWITCH` take a partition number, it is any expression of an integer type — `1 + 1`, a `bigint` variable, `$PARTITION.pf(x)` — read before anything looks at the object's partitions (probed 2026-10-05): any other type, `bit`, a decimal and a NULL literal included, is Msg 4957 state 3, and a number outside 1 to 15,000 Msg 7722 state 1, both naming the index, or the table — a `SWITCH` source by its name, a target as written.
`TRUNCATE`'s bounds instead convert as their values do (see above).

## Data compression per partition

A partitioned rowset — the heap, an index, a key constraint — keeps a `DATA_COMPRESSION` per partition once its partitions differ (`PartitionCompression`), which `sys.partitions` reports and `SWITCH` compares (all probed 2026-10-05 against SQL Server 2025):

- `CREATE TABLE … ON scheme(c) WITH (DATA_COMPRESSION = level ON PARTITIONS (n | n TO m, …), …)` and `CREATE INDEX`'s equivalent set the listed partitions, the rest `NONE`; a number past the partitions is Msg 7722 state 2 naming the table or index, a partition listed twice or a whole-object level beside a list Msg 7711.
  On an unpartitioned table it is Msg 7729 in the create table wording, or for a clustered key the create index wording followed by Msg 1750.
- A rebuild of one partition sets its level: the whole-object one, or that of a clause listing it — `ON PARTITIONS` is legal beside `PARTITION = n` — else nothing.
  A rebuild of every partition sets the level of all, or of those its lists name, which need `PARTITION = ALL` written (Msg 10737, ahead of an unpartitioned table's Msg 7729, for `ALTER TABLE` as for `ALTER INDEX`).
- A `SPLIT RANGE` gives the new partition the level of the one it split; a `MERGE RANGE` keeps the surviving partition's.

`XML_COMPRESSION` keeps a level per partition the same way, beside `DATA_COMPRESSION`'s, with its own refusals (probed 2026-10-06 against SQL Server 2025): naming it for the whole object and for partitions is **Msg 7741** state 1 — followed by Msg 1750 state 0 from `CREATE TABLE` and a rebuild — and a partition twice state 2; a rebuild listing partitions without `PARTITION = ALL` is **Msg 16209**, after `DATA_COMPRESSION`'s Msg 10737.

The levels describe the catalog only: rows are stored the same either way, and a rollback doesn't restore them.

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
- **A `$PARTITION` call a view can no longer bind** reports its Msg 208 at the reading statement's line, where real reports the call's own line in the view; the Msg 4413 after it matches (probed 2026-10-06).
- **A partition number that won't convert** (`PARTITIONS ('x')`, `NULL`) is reported as 0 where real prints an arbitrary number.
  **Settled — don't re-pitch**: real's number is whatever its uninitialized variable held, which no deterministic model can reproduce.

## Not modeled yet

- **A `FILESTREAM` column** is a syntax error here, where real raises Msg 5508 for a string `(max)` type, Msg 1969 for `varbinary(max)` with no FILESTREAM filegroup to default to, Msg 1921 state 3 for a `FILESTREAM_ON` naming no filegroup and Msg 1724 for one naming a filegroup that isn't a FILESTREAM one (probed 2026-09-30); `FILESTREAM_ON` on a table without one is Msg 1716.
- **An indexed view's index on a scheme** reports `data_space_id` 1 and no `sys.partitions` rows at all, where real reports the scheme and one row per partition counting the view's rows (probed 2026-09-30): the simulator never materializes the view, so it has no row counts to place — storage the simulator doesn't keep, not chased.
- **Partition-level lock escalation** — `LOCK_ESCALATION = AUTO` still escalates to the table; which lock real escalates to rides its storage engine's per-partition lock counts, not chased.
- **SWITCH's remaining checks**: reasoning beyond the readable shapes above (functions), and a switch into a `#temp` table, which real meets with an internal Msg 608 naming its own partition and database ids.
