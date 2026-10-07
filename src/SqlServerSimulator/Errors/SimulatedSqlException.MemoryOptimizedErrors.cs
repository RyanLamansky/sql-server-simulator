namespace SqlServerSimulator;

// In-Memory OLTP: memory-optimized tables and table types, their filegroup,
// hash indexes, the isolation rules their access follows, write conflicts and
// natively compiled modules. Every wording and state here was probed
// 2026-10-02 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 10794, the family real raises for a construct memory-optimized
    /// tables don't take: <paramref name="what"/> is the whole subject phrase
    /// (<c>"The type 'xml'"</c>, <c>"The operation 'CREATE INDEX'"</c>, …),
    /// each site with its own state.
    /// </summary>
    internal static SimulatedSqlException NotSupportedWithMemoryOptimized(string what, byte state) =>
        new($"{what} is not supported with memory optimized tables.", 10794, 16, state);

    /// <summary>Msg 10794 state 81: an index option memory-optimized tables' indexes don't take, spelled lower-case.</summary>
    internal static SimulatedSqlException IndexOptionOnMemoryOptimized(string option) =>
        new($"The index option '{option}' is not supported with indexes on memory optimized tables.", 10794, 16, 81);

    /// <summary>
    /// Msg 10785: <c>ALTER TABLE … ADD INDEX</c> (state 1) or <c>DROP INDEX</c>
    /// (state 2) on a disk-based table, then Msg 1750.
    /// </summary>
    internal static SimulatedSqlException AlterTableIndexOnDiskTable(bool drop) =>
        FollowedByConstraintNotCreated(new($"The operation 'ALTER TABLE {(drop ? "DROP" : "ADD")} INDEX' is supported only with memory optimized tables.", 10785, 16, drop ? (byte)2 : (byte)1));

    /// <summary>
    /// Msg 3701: <c>ALTER TABLE … DROP INDEX</c> (state 21) or <c>ALTER
    /// INDEX</c> (state 22) naming an index the memory-optimized table lacks.
    /// </summary>
    internal static SimulatedSqlException MemoryOptimizedIndexMissing(string indexName, bool alter) =>
        new($"Cannot {(alter ? "alter" : "drop")} the index '{indexName}', because it does not exist or you do not have permission.", 3701, 16, alter ? (byte)22 : (byte)21);

    /// <summary>Msg 10790: <c>BUCKET_COUNT</c> on an index that isn't a hash index.</summary>
    internal static SimulatedSqlException BucketCountOnRangeIndex() =>
        new("The option 'BUCKET_COUNT' can be specified only for hash indexes.", 10790, 16, 4);

    /// <summary>
    /// Msg 10790 state 1: <c>BUCKET_COUNT</c>, named as written, on a key or
    /// inline index a table's or table type's definition declares without
    /// <c>HASH</c> — a compile error, where an <c>ALTER INDEX</c> of a range
    /// index raises state 4 as it runs (probed 2026-10-07 against SQL Server
    /// 2025).
    /// </summary>
    internal static SimulatedSqlException BucketCountOnRangeIndexWhileCompiling(string writtenName) =>
        new($"The option '{writtenName}' can be specified only for hash indexes.", 10790, 15, 1);

    /// <summary>Msg 10794 state 1: a filtered index on a memory-optimized table.</summary>
    internal static SimulatedSqlException FilteredIndexOnMemoryOptimized() =>
        new("The feature 'WHERE' is not supported with indexes on memory optimized tables.", 10794, 16, 1);

    /// <summary>
    /// Msg 10794 state 76: a memory-optimized table read or written at an
    /// isolation level it can't run at. Ends the batch and rolls back, as
    /// under <c>XACT_ABORT</c>.
    /// </summary>
    internal static SimulatedSqlException IsolationLevelNotSupportedWithMemoryOptimized(string level) =>
        new($"The transaction isolation level '{level}' is not supported with memory optimized tables.", 10794, 16, 76) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 10794 state 77: a natively compiled module's atomic block at READ COMMITTED.</summary>
    internal static SimulatedSqlException ReadCommittedNativeModule() =>
        new("The transaction isolation level 'READ COMMITTED' is not supported with natively compiled modules.", 10794, 16, 77);

    /// <summary>Msg 10664: an <c>INCLUDE</c> list on a memory-optimized table's index, then Msg 1750.</summary>
    internal static SimulatedSqlException IncludedColumnsOnMemoryOptimized() =>
        FollowedByConstraintNotCreated(new("Cannot specify included columns for indexes on memory optimized tables.", 10664, 16, 1));

    /// <summary>
    /// Msg 10779: <c>DURABILITY</c> given without <c>MEMORY_OPTIMIZED =
    /// ON</c>, its value (1 for <c>SCHEMA_ONLY</c>) spelled as real lower-cases it.
    /// </summary>
    internal static SimulatedSqlException DurabilityWithoutMemoryOptimized(byte durability) =>
        new($"The durability option '{(durability == 1 ? "schema_only" : "schema_and_data")}' is supported only with memory optimized tables.", 10779, 15, 1);

    /// <summary>Msg 10788: <c>DURABILITY</c> on a memory-optimized table type.</summary>
    internal static SimulatedSqlException DurabilityOnTableType() =>
        new("The option 'durability' is not supported with table types.", 10788, 15, 20);

    /// <summary>Msg 10789: a hash index or key without its <c>BUCKET_COUNT</c>.</summary>
    internal static SimulatedSqlException BucketCountRequired(string indexName, string tableName) =>
        new($"The option 'bucket_count' must be specified for index '{indexName}' on table '{tableName}'. It is required for hash indexes.", 10789, 15, 1);

    /// <summary>Msg 41303: a <c>BUCKET_COUNT</c> below 1 or above 2^30.</summary>
    internal static SimulatedSqlException BucketCountOutOfRange() =>
        new("The bucket count for a hash index must be a positive integer not exceeding 1073741824.", 41303, 15, 1);

    /// <summary>Msg 10791: a hash index or key on a disk-based table, then Msg 1750.</summary>
    internal static SimulatedSqlException HashIndexOnDiskTable() =>
        FollowedByConstraintNotCreated(new("Hash indexes are permitted only in memory optimized tables.", 10791, 16, 1));

    /// <summary>
    /// Msg 41337: a memory-optimized table in a database whose
    /// <c>MEMORY_OPTIMIZED_DATA</c> filegroup is missing (state 100) or has no
    /// container (state 1).
    /// </summary>
    internal static SimulatedSqlException MemoryOptimizedFilegroupMissing(byte state) =>
        new("Cannot create memory optimized tables. To create memory optimized tables, the database must have a MEMORY_OPTIMIZED_FILEGROUP that is online and has at least one container.", 41337, 16, state);

    /// <summary>Msg 41321: a durable memory-optimized table without a primary key, then Msg 1750.</summary>
    internal static SimulatedSqlException MemoryOptimizedRequiresPrimaryKey(string tableName) =>
        FollowedByConstraintNotCreated(new($"The memory optimized table '{tableName}' with DURABILITY=SCHEMA_AND_DATA must have a primary key.", 41321, 16, 7));

    /// <summary>Msg 41327: a memory-optimized table or table type without any index, then Msg 1750.</summary>
    internal static SimulatedSqlException MemoryOptimizedRequiresIndex(string tableName) =>
        FollowedByConstraintNotCreated(new($"The memory optimized table '{tableName}' must have at least one index or a primary key.", 41327, 16, 7));

    /// <summary>Msg 12317: a clustered rowstore key or index on a memory-optimized table.</summary>
    internal static SimulatedSqlException ClusteredIndexOnMemoryOptimized() =>
        new("Clustered indexes, which are the default for primary keys, are not supported with memory optimized tables. Specify a NONCLUSTERED index instead.", 12317, 16, 68);

    /// <summary>Msg 12322: a temporary table declared memory-optimized.</summary>
    internal static SimulatedSqlException TemporaryMemoryOptimizedTable() =>
        new("Temporary tables are not supported with memory optimized tables.", 12322, 16, 78);

    /// <summary>Msg 12339: an identity seed or increment other than 1 on a memory-optimized table.</summary>
    internal static SimulatedSqlException MemoryOptimizedIdentitySeed() =>
        new("The use of seed and increment values other than 1 is not supported with memory optimized tables.", 12339, 16, 17);

    /// <summary>Msg 10778: a foreign key between a memory-optimized and a disk-based table, then Msg 1750.</summary>
    internal static SimulatedSqlException ForeignKeyAcrossTableKinds() =>
        FollowedByConstraintNotCreated(new("Foreign key relationships between memory optimized tables and non-memory optimized tables are not supported.", 10778, 16, 1));

    /// <summary>Msg 10780: a memory-optimized table's foreign key referencing anything but the primary key, then Msg 1750.</summary>
    internal static SimulatedSqlException MemoryOptimizedForeignKeyNeedsPrimaryKey(string referencedTable, string foreignKeyName) =>
        FollowedByConstraintNotCreated(new($"There is no primary key in the referenced table '{referencedTable}' that matches the referencing column list in the foreign key '{foreignKeyName}'. Foreign keys in memory-optimized tables must reference primary keys.", 10780, 16, 1));

    /// <summary>Msg 12302: an <c>UPDATE</c> setting a memory-optimized table's primary key column, refused compiling it.</summary>
    internal static SimulatedSqlException MemoryOptimizedPrimaryKeyUpdate() =>
        new("Updating columns that are part of the PRIMARY KEY constraint is not supported with memory optimized tables.", 12302, 16, 6);

    /// <summary>Msg 10777: a trigger on a memory-optimized table that isn't natively compiled.</summary>
    internal static SimulatedSqlException MemoryOptimizedTriggerNotNative() =>
        new("Triggers on memory-optimized tables must use WITH NATIVE_COMPILATION.", 10777, 16, 1);

    /// <summary>Msg 367: the <c>SNAPSHOT</c> table hint on a disk-based table.</summary>
    internal static SimulatedSqlException SnapshotHintOnDiskTable() =>
        new("The hint 'SNAPSHOT' is valid only with memory optimized tables.", 367, 16, 1);

    /// <summary>
    /// Msg 41368: a memory-optimized table read at READ COMMITTED inside an
    /// explicit or implicit transaction, with neither a hint nor
    /// <c>MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT</c> to lift it. Ends the batch
    /// and rolls back, as under <c>XACT_ABORT</c>.
    /// </summary>
    internal static SimulatedSqlException MemoryOptimizedReadCommittedInTransaction() =>
        new("Accessing memory optimized tables using the READ COMMITTED isolation level is supported only for autocommit transactions. It is not supported for explicit or implicit transactions. Provide a supported isolation level for the memory optimized table using a table hint, such as WITH (SNAPSHOT).", 41368, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 41333: a memory-optimized table reached without the
    /// <c>SNAPSHOT</c> hint from a REPEATABLE READ or SERIALIZABLE session.
    /// Ends the batch and rolls back, as under <c>XACT_ABORT</c>.
    /// </summary>
    internal static SimulatedSqlException MemoryOptimizedNeedsSnapshot() =>
        new("The following transactions must access memory optimized tables and natively compiled modules under snapshot isolation: RepeatableRead transactions, Serializable transactions, and transactions that access tables that are not memory optimized in RepeatableRead or Serializable isolation.", 41333, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 41332: a memory-optimized table reached from a session at
    /// SNAPSHOT isolation. Ends the batch and rolls back, as under
    /// <c>XACT_ABORT</c>.
    /// </summary>
    internal static SimulatedSqlException MemoryOptimizedUnderSnapshotSession() =>
        new("Memory optimized tables and natively compiled modules cannot be accessed or created when the session TRANSACTION ISOLATION LEVEL is set to SNAPSHOT.", 41332, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 41302: a memory-optimized row another transaction has written —
    /// still in flight, or committed since this transaction's snapshot — which
    /// real refuses at once rather than waiting; state 110 for an update, 111
    /// for a delete. It ends the batch; an open transaction is doomed and rolls
    /// back at the batch's end (Msg 3998), or stays doomed for a caught error.
    /// </summary>
    internal static SimulatedSqlException MemoryOptimizedWriteConflict(bool delete) =>
        new("The current transaction attempted to update a record that has been updated since this transaction started. The transaction was aborted.", 41302, 16, delete ? (byte)111 : (byte)110) { AbortsAsUnderXactAbort = true, DoomsWhenUncaught = true };

    /// <summary>Msg 41361: <c>READ_ONLY</c> / <c>READ_WRITE</c> on the <c>MEMORY_OPTIMIZED_DATA</c> filegroup.</summary>
    internal static SimulatedSqlException MemoryOptimizedFilegroupAccessMode() =>
        new("The READ_ONLY property of a MEMORY_OPTIMIZED_DATA filegroup cannot be modified.", 41361, 16, 1);

    /// <summary>Msg 10797: a second <c>MEMORY_OPTIMIZED_DATA</c> filegroup.</summary>
    internal static SimulatedSqlException SecondMemoryOptimizedFilegroup() =>
        new("Only one MEMORY_OPTIMIZED_DATA filegroup is allowed per database.", 10797, 15, 2);

    /// <summary>Msg 5509: a <c>SIZE</c> or <c>FILEGROWTH</c> on a memory-optimized container.</summary>
    internal static SimulatedSqlException FilestreamFileSizeOption(string fileName) =>
        new($"The properties SIZE or FILEGROWTH cannot be specified for the FILESTREAM data file '{fileName}'.", 5509, 15, 2);

    /// <summary>Msg 41873: a memory-optimized container with a bounded <c>MAXSIZE</c>.</summary>
    internal static SimulatedSqlException MemoryOptimizedContainerMaxSize() =>
        new("File in memory-optimized filegroup must have MAXSIZE set to be UNLIMITED.", 41873, 16, 2);

    /// <summary>Msg 10783: a natively compiled module whose body isn't one <c>BEGIN ATOMIC</c> block.</summary>
    internal static SimulatedSqlException NativeModuleBodyNotAtomic() =>
        new("The body of a natively compiled module must be an ATOMIC block.", 10783, 15, 1);

    /// <summary>Msg 10784: a <c>BEGIN ATOMIC</c> block missing a required option.</summary>
    internal static SimulatedSqlException AtomicBlockOptionRequired(string option) =>
        new($"The WITH clause of BEGIN ATOMIC statement must specify a value for the option '{option}'.", 10784, 15, 1);

    /// <summary>Msg 10775: a natively compiled module reading a disk-based table.</summary>
    internal static SimulatedSqlException NativeModuleDiskTable(string objectName) =>
        new($"Object '{objectName}' is not a memory optimized table or a natively compiled inline table-valued function and cannot be accessed from a natively compiled module.", 10775, 16, 1);

    /// <summary>Msg 10794 state 130: an <c>INSTEAD OF</c> natively compiled trigger.</summary>
    internal static SimulatedSqlException InsteadOfNativeTrigger() =>
        new("The option 'INSTEAD OF' is not supported with natively compiled triggers.", 10794, 16, 130);
}
