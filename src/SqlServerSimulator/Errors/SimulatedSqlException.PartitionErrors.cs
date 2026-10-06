namespace SqlServerSimulator;

// Partition functions, partition schemes, partitioned tables and indexes,
// TRUNCATE … WITH (PARTITIONS …) and ALTER TABLE … SWITCH. Every wording and
// state here was probed 2026-09-27 against SQL Server 2025, as was each one
// ending its batch whatever SET XACT_ABORT says.
partial class SimulatedSqlException
{
    /// <summary>Msg 7702: a partition function declared with an empty parameter list, refused as the batch compiles (probed 2026-10-05 against SQL Server 2025).</summary>
    internal static SimulatedSqlException PartitionFunctionEmptyParameterList() =>
        new("Empty Partition function type-parameter-list is not allowed when defining a partition function.", 7702, 16, 1);

    /// <summary>Msg 7703: a partition function declared with more than one parameter.</summary>
    internal static SimulatedSqlException PartitionFunctionMultipleParameters() =>
        new("Can not create RANGE partition function with multiple parameter types.", 7703, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 7704: a partition function parameter type real refuses — state 1
    /// for a LOB, MAX, <c>timestamp</c> or <c>json</c> type, 2 for a name that
    /// isn't a type, 3 for a CLR or alias type.
    /// </summary>
    internal static SimulatedSqlException PartitionFunctionInvalidType(string typeName, byte state) =>
        new($"The type '{typeName}' is not valid for this operation.", 7704, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 7705: a boundary value that doesn't convert to the parameter type,
    /// by its 1-based written ordinal — state 1 for a type pair an assignment
    /// can't convert implicitly, state 2 for a value whose conversion fails
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException PartitionRangeValueNotConvertible(int ordinal, byte state) =>
        new($"Could not implicitly convert range values type specified at ordinal {ordinal} to partition function parameter type.", 7705, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7720: a boundary value the parameter type would truncate.</summary>
    internal static SimulatedSqlException PartitionRangeValueTruncated(int ordinal) =>
        new($"Data truncated when converting range values to the partition function parameter type. The range value at ordinal {ordinal} requires data truncation.", 7720, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7708: two equal boundary values, by their written ordinals.</summary>
    internal static SimulatedSqlException PartitionDuplicateBoundaries(int first, int second) =>
        new($"Duplicate range boundary values are not allowed in partition function boundary values list. Partition boundary values at ordinal {first} and {second} are equal.", 7708, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7721: <c>SPLIT RANGE</c> at a value already a boundary, by its 1-based position.</summary>
    internal static SimulatedSqlException PartitionSplitDuplicate(int ordinal) =>
        new($"Duplicate range boundary values are not allowed in partition function boundary values list. The boundary value being added is already present at ordinal {ordinal} of the boundary value list.", 7721, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>The class-0 Msg 7709 real sends when a boundary list isn't written in ascending order.</summary>
    internal static string PartitionValuesNotSortedMessage(string functionName) =>
        $"Warning: Range value list for partition function '{functionName}' is not sorted by value. Mapping of partitions to filegroups during CREATE PARTITION SCHEME will use the sorted boundary values if the function '{functionName}' is referenced in CREATE PARTITION SCHEME.";

    /// <summary>Msg 7719: a partition function past 15,000 partitions.</summary>
    internal static SimulatedSqlException PartitionFunctionTooManyPartitions() =>
        new("CREATE/ALTER partition function failed as only a maximum of 15000 partitions can be created.", 7719, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7715: <c>MERGE RANGE</c> at a value that isn't a boundary.</summary>
    internal static SimulatedSqlException PartitionRangeValueNotFound() =>
        new("The specified partition range value could not be found.", 7715, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7716: <c>MERGE RANGE</c> of a function's last boundary.</summary>
    internal static SimulatedSqlException PartitionFunctionZeroPartitions() =>
        new("Can not create or alter a partition function to have zero partitions.", 7716, 16, 2) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 7710 wording: a split through a scheme with no <c>NEXT USED</c>
    /// filegroup raises it as an error, and <c>ALTER PARTITION SCHEME … NEXT
    /// USED</c> with no filegroup where none is marked sends it at class 0.
    /// </summary>
    internal static string NoNextUsedFilegroupMessage(string schemeName) =>
        $"Warning: The partition scheme '{schemeName}' does not have any next used filegroup. Partition scheme has not been changed.";

    /// <inheritdoc cref="NoNextUsedFilegroupMessage"/>
    internal static SimulatedSqlException NoNextUsedFilegroup(string schemeName) =>
        new(NoNextUsedFilegroupMessage(schemeName), 7710, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7706: dropping a partition function a scheme uses.</summary>
    internal static SimulatedSqlException PartitionFunctionInUse(string functionName) =>
        new($"Partition function '{functionName}' is being used by one or more partition schemes.", 7706, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7717: dropping a partition scheme a table or index is placed on.</summary>
    internal static SimulatedSqlException PartitionSchemeInUse(string schemeName) =>
        new($"The partition scheme \"{schemeName}\" is currently being used to partition one or more tables.", 7717, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 15151 for a partition function or scheme <c>ALTER</c> / <c>DROP</c>
    /// names that doesn't exist; <paramref name="kind"/> is <c>function</c> or
    /// <c>scheme</c>.
    /// </summary>
    internal static SimulatedSqlException PartitionObjectNotFound(string verb, string kind, string name) =>
        new($"Cannot {verb} the partition {kind} '{name}', because it does not exist or you do not have permission.", 15151, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 208 for a partition-DDL name that resolves to nothing, at the state the site reports.</summary>
    internal static SimulatedSqlException PartitionInvalidObjectName(string name, byte state) =>
        new($"Invalid object name '{name}'.", 208, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7707: a scheme listing fewer filegroups than its function has partitions.</summary>
    internal static SimulatedSqlException PartitionSchemeTooFewFilegroups(string functionName, string schemeName) =>
        new($"The associated partition function '{functionName}' generates more partitions than there are file groups mentioned in the scheme '{schemeName}'.", 7707, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7723: <c>ALL TO</c> with more than one filegroup.</summary>
    internal static SimulatedSqlException PartitionSchemeAllWithSeveral() =>
        new("Only a single filegroup can be specified while creating partition scheme using option ALL to specify all the filegroups.", 7723, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>The class-0 Msg 7712 a scheme created with a filegroup to spare sends.</summary>
    internal static string PartitionSchemeCreatedMessage(string schemeName, string filegroupName) =>
        $"Partition scheme '{schemeName}' has been created successfully. '{filegroupName}' is marked as the next used filegroup in partition scheme '{schemeName}'.";

    /// <summary>The class-0 Msg 7713 for filegroups listed past the next-used one.</summary>
    internal static string PartitionSchemeIgnoredFilegroupsMessage(int count) =>
        $"{count} filegroups specified after the next used filegroup are ignored.";

    /// <summary>Msg 1921: an <c>ON</c> clause naming a partition scheme or filegroup that doesn't exist.</summary>
    internal static SimulatedSqlException InvalidDataSpace(bool scheme, string name) =>
        new($"Invalid {(scheme ? "partition scheme" : "filegroup")} '{name}' specified.", 1921, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 1911: a partition column that isn't a column of the table.</summary>
    internal static SimulatedSqlException PartitionColumnNotFound(string columnName) =>
        new($"Column name '{columnName}' does not exist in the target table, index or view.", 1911, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2726: a partition column list whose length doesn't match the function's parameter count.</summary>
    internal static SimulatedSqlException PartitionColumnCountMismatch(string functionName) =>
        new($"Partition function '{functionName}' uses 1 columns which does not match with the number of partition columns used to partition the table or index.", 2726, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2703: the same partition column named twice.</summary>
    internal static SimulatedSqlException PartitionColumnDuplicate(string columnName) =>
        new($"Cannot use duplicate column names in the partition columns list. Column name '{columnName}' appears more than once.", 2703, 16, 2) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7726: a partition column whose type isn't exactly the function's parameter type.</summary>
    internal static SimulatedSqlException PartitionColumnTypeMismatch(string columnName, string columnType, string functionName, string parameterType) =>
        new($"Partition column '{columnName}' has data type {columnType} which is different from the partition function '{functionName}' parameter data type {parameterType}.", 7726, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7727: a string partition column of another collation than the function's parameter.</summary>
    internal static SimulatedSqlException PartitionColumnCollationMismatch(string columnName, string functionName) =>
        new($"Collation of partition column '{columnName}' does not match collation of corresponding parameter in partition function '{functionName}'.", 7727, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7724: a computed partition column that isn't persisted.</summary>
    internal static SimulatedSqlException PartitionColumnNotPersisted(string columnName, string tableName) =>
        new($"Computed column cannot be used as a partition key if it is not persisted. Partition key column '{columnName}' in table '{tableName}' is not persisted.", 7724, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 1709: <c>TEXTIMAGE_ON</c> on a table without a LOB column (probed 2026-09-28 against SQL Server 2025).</summary>
    internal static SimulatedSqlException TextImageOnWithoutLobColumn() =>
        new("Cannot use TEXTIMAGE_ON when a table has no text, ntext, image, varchar(max), nvarchar(max), non-FILESTREAM varbinary(max), xml or large CLR type columns.", 1709, 16, 1);

    /// <summary>Msg 1716: <c>FILESTREAM_ON</c> on a table without a FILESTREAM column (probed 2026-09-30 against SQL Server 2025).</summary>
    internal static SimulatedSqlException FileStreamOnWithoutFileStreamColumns(byte state = 1) =>
        new("FILESTREAM_ON cannot be specified when a table has no FILESTREAM columns. Remove the FILESTREAM_ON clause from the statement, or add a FILESTREAM column to the table.", 1716, 16, state);

    /// <summary>Msg 1924 state 2: a table or index created on a read-only filegroup (probed 2026-09-28 against SQL Server 2025).</summary>
    internal static SimulatedSqlException FilegroupIsReadOnly(string name) =>
        new($"Filegroup '{name}' is read-only.", 1924, 16, 2);

    /// <summary>
    /// Msg 622 state 3: rows written to a filegroup without files — an
    /// <c>INSERT</c> into a table on one, or an index built on one over a table
    /// with rows (probed 2026-09-28 against SQL Server 2025). Uncaught, it
    /// ends the batch and rolls the transaction back; a <c>TRY</c> frame
    /// catches it (probed 2026-10-06).
    /// </summary>
    internal static SimulatedSqlException FilegroupHasNoFiles(string name) =>
        new($"The filegroup \"{name}\" has no files assigned to it. Tables, indexes, text columns, ntext columns, and image columns cannot be populated on this filegroup until a file is added.", 622, 16, 3) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 652: a write reaching a rowset on a read-only filegroup — the heap
    /// (named <c>""</c>), a clustered or a nonclustered index — naming its
    /// <c>sys.partitions.partition_id</c> as the RowsetId (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException RowsetOnReadOnlyFilegroup(string indexName, string qualifiedTableName, long rowsetId, string filegroupName) =>
        new($"The index \"{indexName}\" for table \"{qualifiedTableName}\" (RowsetId {rowsetId}) resides on a read-only filegroup (\"{filegroupName}\"), which cannot be modified.", 652, 16, 1);

    /// <summary>Msg 5042 state 1: <c>REMOVE FILE</c> of a file whose filegroup holds rows (probed 2026-09-28 against SQL Server 2025).</summary>
    internal static SimulatedSqlException FileNotEmpty(string name) =>
        new($"The file '{name}' cannot be removed because it is not empty.", 5042, 16, 1);

    /// <summary>Msg 1707: <c>TEXTIMAGE_ON</c> on a partitioned table.</summary>
    internal static SimulatedSqlException TextImageOnPartitionedTable() =>
        new("Cannot specify TEXTIMAGE_ON filegroup for a partitioned table.", 1707, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 1908: a unique index or key whose key columns leave out its partition column.</summary>
    internal static SimulatedSqlException PartitionColumnNotInUniqueKey(string columnName, string indexName) =>
        new($"Column '{columnName}' is partitioning column of the index '{indexName}'. Partition columns for a unique index must be a subset of the index key.", 1908, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7722 state 2: a partition number outside a table's or index's partitions (<paramref name="kind"/> names which).</summary>
    internal static SimulatedSqlException InvalidPartitionNumber(long number, string name, int fanout, string kind = "table") =>
        new($"Invalid partition number {number} specified for {kind} '{name}', partition number can range from 1 to {fanout}.", 7722, 16, 2) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7728: a <c>PARTITIONS (n TO m)</c> range whose bounds are reversed.</summary>
    internal static SimulatedSqlException InvalidPartitionRange(long lower, long upper) =>
        new($"Invalid partition range: {lower} TO {upper}. Lower bound must not be greater than upper bound.", 7728, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7711: a partition named twice in one <c>PARTITIONS</c> list.</summary>
    internal static SimulatedSqlException PartitionsOptionRepeated() =>
        new("The PARTITIONS option was specified more than once for the table, or for at least one of its partitions if the table is partitioned.", 7711, 16, 2) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7729 state 3: <c>TRUNCATE TABLE … WITH (PARTITIONS …)</c> of an unpartitioned table.</summary>
    internal static SimulatedSqlException TruncatePartitionOnUnpartitioned(string tableName) =>
        new($"Cannot specify partition number in the truncate table statement as the table '{tableName}' is not partitioned.", 7729, 16, 3) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 3756: a partition truncate through a table with an index on another partitioning.</summary>
    internal static SimulatedSqlException TruncatePartitionUnalignedIndex(string indexName, string tableName, string functionName) =>
        new($"TRUNCATE TABLE statement failed. Index '{indexName}' is not partitioned, but table '{tableName}' uses partition function '{functionName}'. Index and table must use an equivalent partition function.", 3756, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4716: a partition truncate through a table with an index partitioned on another column (probed 2026-10-05 against SQL Server 2025).</summary>
    internal static SimulatedSqlException TruncatePartitionColumnSetDiffers(string tableName, string indexName) =>
        new($"TRUNCATE TABLE statement failed. The column set used to partition the table '{tableName}' is different from the column set used to partition index '{indexName}'", 4716, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7730: an <c>ALTER INDEX</c> / <c>ALTER TABLE … REBUILD</c> partition number the index doesn't have.</summary>
    internal static SimulatedSqlException AlterIndexPartitionNotFound(long number, string indexName) =>
        new($"Alter index statement failed because partition number {number} does not exist in index '{indexName}'.", 7730, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 4957: a partition number given by an expression not of an integer
    /// type, worded for <paramref name="statement"/> (<c>ALTER INDEX</c> or
    /// <c>ALTER TABLE</c>) and the <paramref name="kind"/> it names (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException PartitionNumberNotInteger(string statement, string kind, string name) =>
        new($"'{statement}' statement failed because the expression identifying partition number for the {kind} '{name}' is not of integer type.", 4957, 16, 3) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 7722 state 1: an <c>ALTER INDEX</c>, <c>ALTER TABLE … REBUILD</c> or
    /// <c>SWITCH</c> partition number outside the 1 to 15,000 any partition
    /// function allows, whatever the object's own count (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException PartitionNumberOutOfRange(long number, string kind, string name) =>
        new($"Invalid partition number {number} specified for {kind} '{name}', partition number can range from 1 to 15000.", 7722, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 2586: <c>ALTER INDEX … REORGANIZE PARTITION = n</c> past the index's partitions (probed 2026-10-05 against SQL Server 2025).</summary>
    internal static SimulatedSqlException ReorganizePartitionNotFound(long number, string indexName, string tableName) =>
        new($"Cannot find partition number {number} for index \"{indexName}\", table \"{tableName}\".", 2586, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 7733 state 2: <c>ALTER INDEX ALL … PARTITION = n</c> over a table
    /// one of whose indexes isn't partitioned, naming the first partitioned
    /// index and the first that isn't (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException AlterIndexAllUnalignedIndex(string partitionedIndex, string unalignedIndex) =>
        new($"'ALTER INDEX' statement failed. The index '{partitionedIndex}' is partitioned while index '{unalignedIndex}' is not partitioned.", 7733, 16, 2) { AbortsAsUnderXactAbort = true };

    // ALTER TABLE … SWITCH. Table names are three-part, database first.

    /// <summary>Msg 4905: the target of a switch into a whole table holds rows.</summary>
    internal static SimulatedSqlException SwitchTargetNotEmpty(string target) =>
        new($"ALTER TABLE SWITCH statement failed. The target table '{target}' must be empty.", 4905, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4904: the target partition of a switch holds rows.</summary>
    internal static SimulatedSqlException SwitchTargetPartitionNotEmpty(int partition, string target) =>
        new($"ALTER TABLE SWITCH statement failed. The specified partition {partition} of target table '{target}' must be empty.", 4904, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4943: source and target column counts differ.</summary>
    internal static SimulatedSqlException SwitchColumnCountMismatch(string source, int sourceCount, string target, int targetCount) =>
        new($"ALTER TABLE SWITCH statement failed because table '{source}' has {sourceCount} columns and table '{target}' has {targetCount} columns.", 4943, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4942: a column name differs at one ordinal.</summary>
    internal static SimulatedSqlException SwitchColumnNameMismatch(string sourceColumn, int ordinal, string source, string targetColumn, string target) =>
        new($"ALTER TABLE SWITCH statement failed because column '{sourceColumn}' at ordinal {ordinal} in table '{source}' has a different name than the column '{targetColumn}' at the same ordinal in table '{target}'.", 4942, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4944: a column's type differs.</summary>
    internal static SimulatedSqlException SwitchColumnTypeMismatch(string column, string sourceType, string source, string targetType, string target) =>
        new($"ALTER TABLE SWITCH statement failed because column '{column}' has data type {sourceType} in source table '{source}' which is different from its type {targetType} in target table '{target}'.", 4944, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4945: a column's collation differs.</summary>
    internal static SimulatedSqlException SwitchColumnCollationMismatch(string column, string source, string target) =>
        new($"ALTER TABLE SWITCH statement failed because column '{column}' does not have the same collation in tables '{source}' and '{target}'.", 4945, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4985: a column's nullability differs.</summary>
    internal static SimulatedSqlException SwitchColumnNullabilityMismatch(string column, string source, string target) =>
        new($"ALTER TABLE SWITCH statement failed because column '{column}' does not have the same nullability attribute in tables '{source}' and '{target}'.", 4985, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4950: a source partition number the table doesn't have.</summary>
    internal static SimulatedSqlException SwitchPartitionNotFound(long number, string table) =>
        new($"ALTER TABLE SWITCH statement failed because partition number {number} does not exist in table '{table}'.", 4950, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4955: a switch whose source and target are one table.</summary>
    internal static SimulatedSqlException SwitchSameTable(string source, string target) =>
        new($"ALTER TABLE SWITCH statement failed. The source table '{source}' and target table '{target}' are same.", 4955, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4911: a partitioned table named without a partition number — state 1 for the source, 2 for the target.</summary>
    internal static SimulatedSqlException SwitchPartitionNumberRequired(string table, byte state) =>
        new($"Cannot specify a partitioned table without partition number in ALTER TABLE SWITCH statement. The table '{table}' is partitioned.", 4911, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>The class-0 Msg 4903 for a partition number given on an unpartitioned table — state 1 for the source, 2 for the target.</summary>
    internal static string SwitchPartitionIgnoredMessage(long number, string table) =>
        $"Warning: The specified partition {number} for the table '{table}' was ignored in ALTER TABLE SWITCH statement because the table is not partitioned.";

    /// <summary>Msg 4982: an unpartitioned source whose CHECK constraints don't confine the partition column to the target partition's range.</summary>
    internal static SimulatedSqlException SwitchSourceCheckOutsideRange(string source, int partition, string target) =>
        new($"ALTER TABLE SWITCH statement failed. Check constraints of source table '{source}' allow values that are not allowed by range defined by partition {partition} on target table '{target}'.", 4982, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4972: a source whose check constraints or partition allow values the target's don't.</summary>
    internal static SimulatedSqlException SwitchSourceAllowsMore(string source, string target) =>
        new($"ALTER TABLE SWITCH statement failed. Check constraints or partition function of source table '{source}' allows values that are not allowed by check constraints or partition function on target table '{target}'.", 4972, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4973: a source partition's range that isn't inside the target partition's.</summary>
    internal static SimulatedSqlException SwitchRangeNotSubset(int sourcePartition, string source, int targetPartition, string target) =>
        new($"ALTER TABLE SWITCH statement failed. Range defined by partition {sourcePartition} in table '{source}' is not a subset of range defined by partition {targetPartition} in table '{target}'.", 4973, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4913: one table clustered and the other not — state 1 when the source is, 2 when the target is.</summary>
    internal static SimulatedSqlException SwitchClusteredMismatch(string clustered, string indexName, string other, byte state) =>
        new($"ALTER TABLE SWITCH statement failed. The table '{clustered}' has clustered index '{indexName}' while the table '{other}' does not have clustered index.", 4913, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4947: a target index with no identical source index.</summary>
    internal static SimulatedSqlException SwitchNoIdenticalIndex(string source, string indexName, string target) =>
        new($"ALTER TABLE SWITCH statement failed. There is no identical index in source table '{source}' for the index '{indexName}' in target table '{target}' .", 4947, 16, 1) { AbortsAsUnderXactAbort = true };

    // The SWITCH refusals below were probed 2026-10-05 against SQL Server 2025.

    /// <summary>Msg 4949: a SWITCH target that is a view, named as written.</summary>
    internal static SimulatedSqlException SwitchTargetNotATable(string writtenName) =>
        new($"ALTER TABLE SWITCH statement failed because the object '{writtenName}' is not a user defined table.", 4949, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4900: a SWITCH side with change tracking enabled — state 1 for the target, 2 for the source.</summary>
    internal static SimulatedSqlException SwitchChangeTracked(string table, byte state) =>
        new($"The ALTER TABLE SWITCH statement failed for table '{table}'. It is not possible to switch the partition of a table that has change tracking enabled. Disable change tracking before using ALTER TABLE SWITCH.", 4900, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// A column attribute the two SWITCH sides differ on: Msg 4946 its
    /// persistence, 11412 its sparse storage, 4958 its <c>ROWGUIDCOL</c>.
    /// </summary>
    internal static SimulatedSqlException SwitchColumnAttributeMismatch(int number, string attribute, string column, string source, string target) =>
        new($"ALTER TABLE SWITCH statement failed because column '{column}' does not have {attribute} in tables '{source}' and '{target}'.", number, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4953: two partitioned SWITCH sides partitioned on different columns.</summary>
    internal static SimulatedSqlException SwitchPartitionColumnsDiffer(string source, string target) =>
        new($"ALTER TABLE SWITCH statement failed. The columns set used to partition the table '{source}' is different from the column set used to partition the table '{target}'.", 4953, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 11406: the switched rowsets' <c>DATA_COMPRESSION</c> differ.</summary>
    internal static SimulatedSqlException SwitchCompressionMismatch() =>
        new("ALTER TABLE SWITCH statement failed. Source and target partitions have different values for the DATA_COMPRESSION option.", 11406, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4938: two switched partitions on different filegroups.</summary>
    internal static SimulatedSqlException SwitchPartitionFilegroupMismatch(int sourcePartition, string source, string sourceFilegroup, int targetPartition, string target, string targetFilegroup) =>
        new($"ALTER TABLE SWITCH statement failed. Partition {sourcePartition} of table '{source}' is in filegroup '{sourceFilegroup}' and partition {targetPartition} of table '{target}' is in filegroup '{targetFilegroup}'.", 4938, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4939: an unpartitioned SWITCH side on another filegroup than the partition, the unpartitioned table named first.</summary>
    internal static SimulatedSqlException SwitchTableFilegroupMismatch(string table, string tableFilegroup, int partition, string partitioned, string partitionFilegroup) =>
        new($"ALTER TABLE SWITCH statement failed. table '{table}' is in filegroup '{tableFilegroup}' and partition {partition} of table '{partitioned}' is in filegroup '{partitionFilegroup}'.", 4939, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4940: two unpartitioned SWITCH sides on different filegroups (probed 2026-10-06 against SQL Server 2025).</summary>
    internal static SimulatedSqlException SwitchTablesFilegroupMismatch(string source, string sourceFilegroup, string target, string targetFilegroup) =>
        new($"ALTER TABLE SWITCH statement failed. table '{source}' is in filegroup '{sourceFilegroup}' and table '{target}' is in filegroup '{targetFilegroup}'.", 4940, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7733 state 4: a partitioned SWITCH side carrying an index that isn't partitioned.</summary>
    internal static SimulatedSqlException SwitchUnalignedIndex(string table, string indexName) =>
        new($"'ALTER TABLE SWITCH' statement failed. The table '{table}' is partitioned while index '{indexName}' is not partitioned.", 7733, 16, 4) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 11401: an indexed view over a partitioned SWITCH source whose index isn't partitioned.</summary>
    internal static SimulatedSqlException SwitchIndexedViewUnpartitioned(string source, string indexName, string viewName) =>
        new($"ALTER TABLE SWITCH statement failed. Table '{source}' is partitioned, but index '{indexName}' on indexed view '{viewName}' is not partitioned.", 11401, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 11402: a SWITCH target referenced by more indexed views than its source.</summary>
    internal static SimulatedSqlException SwitchIndexedViewsMissing(string target, int targetViews, string source, int sourceViews) =>
        new($"ALTER TABLE SWITCH statement failed. Target table '{target}' is referenced by {targetViews} indexed view(s), but source table '{source}' is only referenced by {sourceViews} indexed view(s). Every indexed view on the target table must have at least one matching indexed view on the source table.", 11402, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4968: a target carrying a foreign key the source lacks.</summary>
    internal static SimulatedSqlException SwitchTargetForeignKey(string target, string constraintName, string source) =>
        new($"ALTER TABLE SWITCH statement failed. Target table '{target}' has foreign key for constraint '{constraintName}' but source table '{source}' does not have corresponding key.", 4968, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 5035: <c>ADD FILEGROUP</c> naming a filegroup the database has.</summary>
    internal static SimulatedSqlException FilegroupAlreadyExists(string name) =>
        new($"Filegroup '{name}' already exists in this database. Specify a different name or remove the conflicting filegroup if it is empty.", 5035, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 5014: <c>REMOVE FILEGROUP</c> naming a filegroup the database doesn't have.</summary>
    internal static SimulatedSqlException FilegroupDoesNotExist(string name, string database) =>
        new($"The filegroup '{name}' does not exist in database '{database}'.", 5014, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 5042: <c>REMOVE FILEGROUP</c> of <c>PRIMARY</c> (state 6) or of a filegroup a partition scheme still maps to (state 12).</summary>
    internal static SimulatedSqlException FilegroupNotEmpty(string name, byte state) =>
        new($"The filegroup '{name}' cannot be removed because it is not empty.", 5042, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>The class-0 Msg 5044 a removed filegroup reports.</summary>
    internal static string FilegroupRemovedMessage(string name) => $"The filegroup '{name}' has been removed.";

    /// <summary>Msg 4966: a computed column defined differently on the two sides of a SWITCH.</summary>
    internal static SimulatedSqlException SwitchComputedColumnMismatch(string column, string sourceDefinition, string source, string targetDefinition, string target) =>
        new($"ALTER TABLE SWITCH statement failed. Computed column '{column}' defined as '{sourceDefinition}' in table '{source}' is different from the same column in table '{target}' defined as '{targetDefinition}'.", 4966, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4971: a target CHECK constraint the source has no constraint of the same definition for.</summary>
    internal static SimulatedSqlException SwitchTargetCheckConstraint(string target, string constraintName, string source) =>
        new($"ALTER TABLE SWITCH statement failed. Target table '{target}' has a column level check constraint '{constraintName}' but the source table '{source}' does not have a corresponding constraint.", 4971, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 7730's <c>ALTER TABLE … REBUILD</c> form for a partitioned heap.</summary>
    internal static SimulatedSqlException AlterTablePartitionNotFound(long number, string tableName) =>
        new($"Alter table statement failed because partition number {number} does not exist in table '{tableName}'.", 7730, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 3729 state 3: dropping a partition function a schema-bound module's body calls.</summary>
    internal static SimulatedSqlException PartitionFunctionReferenced(string functionName, string moduleName) =>
        new($"Cannot DROP PARTITION FUNCTION '{functionName}' because it is being referenced by object '{moduleName}'.", 3729, 16, 3) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 1088 state 29: a SWITCH target that doesn't resolve.</summary>
    internal static SimulatedSqlException SwitchTargetNotFound(string writtenName) =>
        new($"Cannot find the object \"{writtenName}\" because it does not exist or you do not have permissions.", 1088, 16, 29) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 13546: a SWITCH out of a system-versioned table.</summary>
    internal static SimulatedSqlException SwitchSystemVersionedSource(string source) =>
        new($"Switching out partition failed on table '{source}' because it is not a supported operation on system-versioned tables. Consider setting SYSTEM_VERSIONING to OFF and trying again.", 13546, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 13577: a SWITCH into a table with a <c>SYSTEM_TIME</c> period from one without, naming the source.</summary>
    internal static SimulatedSqlException SwitchTargetHasPeriod(string source) =>
        new($"ALTER TABLE SWITCH statement failed on table '{source}' because target table has SYSTEM_TIME PERIOD while source table does not have it.", 13577, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4967: a SWITCH out of a table another table's foreign key references.</summary>
    internal static SimulatedSqlException SwitchSourceReferenced(string source, string constraintName) =>
        new($"ALTER TABLE SWITCH statement failed. SWITCH is not allowed because source table '{source}' contains primary key for constraint '{constraintName}'.", 4967, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 10737: a rebuild's compression option names partitions without the
    /// statement's <c>PARTITION = ALL</c> (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException CompressionPartitionsWithoutPartitionAll() =>
        new("In an ALTER TABLE REBUILD or ALTER INDEX REBUILD statement, when a partition is specified in a DATA_COMPRESSION clause, PARTITION=ALL must be specified. The PARTITION=ALL clause is used to reinforce that all partitions of the table or index will be rebuilt, even if only a subset is specified in the DATA_COMPRESSION clause.", 10737, 15, 1);
}
