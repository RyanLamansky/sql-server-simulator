using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Everything a table-scoped DDL statement — <c>ALTER TABLE</c>, <c>CREATE</c>
/// / <c>DROP</c> / <c>ALTER INDEX</c>, a column or index <c>sp_rename</c> —
/// can change about a <see cref="HeapTable"/>, captured before it runs so a
/// transaction rollback can put the table back as it stood.
/// </summary>
/// <remarks>
/// The capture leans on those statements never rewriting storage in place: a
/// column change builds a fresh <see cref="Heap"/> and column array and swaps
/// them in, so holding the old references is enough to restore the rows. What
/// they do mutate in place — names, the disabled / untrusted flags, a
/// column's default, the constraint and index lists — is copied value by
/// value.
/// </remarks>
internal sealed class HeapTableSnapshot
{
    private readonly HeapTable table;
    private readonly string name;
    private readonly DateTime modifyDate;
    private readonly byte lockEscalation;
    private readonly Schemas.PartitionPlacement? partitioning;
    private readonly int filegroupId;
    private readonly int lobFilegroupId;
    private readonly int maxColumnIdUsed;
    private readonly HeapColumn[] columns;
    private readonly (string Name, int ColumnId, bool IsRowGuidCol, bool IsSparse, Parser.Expression? Default, DefaultConstraint? DefaultConstraint, string? DefaultName)[] columnState;
    private readonly Heap heap;
    private readonly HeapColumn[]? heapLayout;
    private readonly HeapTable? systemVersioning;
    private readonly bool isHistoryTable;
    private readonly bool periodInheritedFromBase;
    private readonly int historyRetentionPeriod;
    private readonly HistoryRetentionUnit historyRetentionUnit;
    private readonly FullTextIndex? fullTextIndex;
    private readonly TableChangeTracking? changeTracking;
    private readonly KeyConstraint[] keyConstraints;
    private readonly (string Name, bool IsDisabled, StorageOptions Options)[] keyState;
    private readonly CheckConstraint[] checkConstraints;
    private readonly (string Name, bool IsNotTrusted, bool IsDisabled, string? Definition, bool IsSystemNamed)[] checkState;
    private readonly ForeignKey[] outgoingForeignKeys;
    private readonly ForeignKey[] incomingForeignKeys;
    private readonly (ForeignKey Key, string Name, bool IsNotTrusted, bool IsDisabled)[] foreignKeyState;
    private readonly Index[] indexes;
    private readonly (string Name, bool IgnoreDupKey, bool IsDisabled, StorageOptions Options, int CompressionDelay, bool ColumnstoreArchive)[] indexState;
    private readonly UserStatistic[] userStatistics;
    private readonly XmlIndex[] xmlIndexes;
    private readonly SpatialIndex[] spatialIndexes;
    private readonly JsonIndex[] jsonIndexes;
    private readonly (string Name, int ColumnOrdinal, bool IsDisabled)[] jsonIndexState;
    private readonly VectorIndex[] vectorIndexes;
    private readonly (string Name, int ColumnOrdinal)[] vectorIndexState;
    private readonly (Trigger Trigger, bool IsDisabled)[] triggers;
    private readonly EdgeConstraint[] edgeConstraints;
    private readonly (int StartOrdinal, int EndOrdinal)? periodColumns;
    private readonly StorageOptions heapOptions;
    private readonly bool pageCompressed;

    public HeapTableSnapshot(HeapTable table, Database? database)
    {
        this.table = table;
        this.name = table.Name;
        this.modifyDate = table.ModifyDate;
        this.lockEscalation = table.LockEscalation;
        this.partitioning = table.Partitioning;
        this.filegroupId = table.FilegroupId;
        this.lobFilegroupId = table.LobFilegroupId;
        this.maxColumnIdUsed = table.MaxColumnIdUsed;
        this.columns = table.Columns;
        this.columnState = Array.ConvertAll(table.Columns, column => (
            column.Name, column.ColumnId, column.IsRowGuidCol, column.IsSparse, column.Default, column.DefaultConstraint,
            column.DefaultConstraint?.Name));
        this.heap = table.Heap;
        this.heapLayout = table.Heap.ReclaimColumns;
        this.systemVersioning = table.SystemVersioning;
        this.isHistoryTable = table.IsHistoryTable;
        this.periodInheritedFromBase = table.PeriodInheritedFromBase;
        this.historyRetentionPeriod = table.HistoryRetentionPeriod;
        this.historyRetentionUnit = table.HistoryRetentionUnit;
        this.fullTextIndex = table.FullTextIndex;
        this.changeTracking = table.ChangeTracking;
        this.keyConstraints = [.. table.KeyConstraints];
        this.keyState = Array.ConvertAll(this.keyConstraints, key => (key.Name, key.IsDisabled, StorageOptions.Of(key)));
        this.checkConstraints = [.. table.CheckConstraints];
        this.checkState = Array.ConvertAll(this.checkConstraints, check => (check.Name, check.IsNotTrusted, check.IsDisabled, check.Definition, check.IsSystemNamed));
        this.outgoingForeignKeys = [.. table.OutgoingForeignKeys];
        this.incomingForeignKeys = [.. table.IncomingForeignKeys];
        this.foreignKeyState = [.. this.outgoingForeignKeys.Concat(this.incomingForeignKeys).Distinct()
            .Select(key => (key, key.Name, key.IsNotTrusted, key.IsDisabled))];
        this.indexes = [.. table.Indexes];
        this.indexState = Array.ConvertAll(this.indexes, index => (index.Name, index.IgnoreDupKey, index.IsDisabled, StorageOptions.Of(index), index.CompressionDelay, index.ColumnstoreArchive));
        this.userStatistics = [.. table.UserStatistics];
        this.xmlIndexes = [.. table.XmlIndexes];
        this.spatialIndexes = [.. table.SpatialIndexes];
        this.jsonIndexes = [.. table.JsonIndexes];
        this.jsonIndexState = [.. table.JsonIndexes.Select(static index => (index.Name, index.ColumnOrdinal, index.IsDisabled))];
        this.vectorIndexes = [.. table.VectorIndexes];
        this.vectorIndexState = [.. table.VectorIndexes.Select(static index => (index.Name, index.ColumnOrdinal))];
        this.triggers = database is null
            ? []
            : [.. database.Schemas.EnumerateValues().SelectMany(schema => schema.Triggers.EnumerateValues())
                .Where(trigger => ReferenceEquals(trigger.Parent, table))
                .Select(trigger => (trigger, trigger.IsDisabled))];
        this.edgeConstraints = [.. table.EdgeConstraints];
        this.periodColumns = table.PeriodColumns;
        this.heapOptions = StorageOptions.OfHeap(table);
        this.pageCompressed = table.PageCompressed;
    }

    public void Restore()
    {
        var table = this.table;
        table.Name = this.name;
        table.ModifyDate = this.modifyDate;
        table.LockEscalation = this.lockEscalation;
        table.Partitioning = this.partitioning;
        table.FilegroupId = this.filegroupId;
        table.LobFilegroupId = this.lobFilegroupId;
        table.MaxColumnIdUsed = this.maxColumnIdUsed;
        table.Columns = this.columns;
        for (var i = 0; i < this.columns.Length; i++)
        {
            var column = this.columns[i];
            var state = this.columnState[i];
            column.Name = state.Name;
            column.ColumnId = state.ColumnId;
            column.IsRowGuidCol = state.IsRowGuidCol;
            column.IsSparse = state.IsSparse;
            column.Default = state.Default;
            column.DefaultConstraint = state.DefaultConstraint;
            if (state.DefaultConstraint is { } defaultConstraint)
                defaultConstraint.Name = state.DefaultName!;
        }
        table.Heap = this.heap;
        table.RecomputeStorageProjections();
        // A heap the change kept, being empty, took the new layout, and rows
        // the transaction wrote into it since leave their slots behind, so it
        // takes back its own by hand.
        this.heap.ReclaimColumns = this.heapLayout;
        table.Heap.InvalidateSeekJournal();
        table.SystemVersioning = this.systemVersioning;
        table.IsHistoryTable = this.isHistoryTable;
        table.PeriodInheritedFromBase = this.periodInheritedFromBase;
        table.HistoryRetentionPeriod = this.historyRetentionPeriod;
        table.HistoryRetentionUnit = this.historyRetentionUnit;
        table.FullTextIndex = this.fullTextIndex;
        table.ChangeTracking = this.changeTracking;

        Refill(table.KeyConstraints, this.keyConstraints);
        for (var i = 0; i < this.keyConstraints.Length; i++)
        {
            var key = this.keyConstraints[i];
            (key.Name, key.IsDisabled, var options) = this.keyState[i];
            options.RestoreTo(key);
        }
        Refill(table.CheckConstraints, this.checkConstraints);
        for (var i = 0; i < this.checkConstraints.Length; i++)
        {
            var check = this.checkConstraints[i];
            (check.Name, check.IsNotTrusted, check.IsDisabled, check.Definition, check.IsSystemNamed) = this.checkState[i];
        }

        // A foreign key lives on both of its tables' lists, so one the
        // statement added or dropped here has to leave or rejoin the other
        // table's list too.
        foreach (var added in table.OutgoingForeignKeys.Except(this.outgoingForeignKeys))
            _ = added.ReferencedTable.IncomingForeignKeys.Remove(added);
        foreach (var dropped in this.outgoingForeignKeys.Except(table.OutgoingForeignKeys))
        {
            if (!dropped.ReferencedTable.IncomingForeignKeys.Contains(dropped))
                dropped.ReferencedTable.IncomingForeignKeys.Add(dropped);
        }
        Refill(table.OutgoingForeignKeys, this.outgoingForeignKeys);
        foreach (var dropped in this.incomingForeignKeys.Except(table.IncomingForeignKeys))
        {
            if (!dropped.ChildTable.OutgoingForeignKeys.Contains(dropped))
                dropped.ChildTable.OutgoingForeignKeys.Add(dropped);
        }
        Refill(table.IncomingForeignKeys, this.incomingForeignKeys);
        foreach (var (key, keyName, isNotTrusted, isDisabled) in this.foreignKeyState)
            (key.Name, key.IsNotTrusted, key.IsDisabled) = (keyName, isNotTrusted, isDisabled);

        table.Indexes = [.. this.indexes];
        for (var i = 0; i < this.indexes.Length; i++)
        {
            var index = this.indexes[i];
            (index.Name, index.IgnoreDupKey, index.IsDisabled, var options, index.CompressionDelay, index.ColumnstoreArchive) = this.indexState[i];
            options.RestoreTo(index);
        }
        table.UserStatistics = [.. this.userStatistics];
        Refill(table.XmlIndexes, this.xmlIndexes);
        Refill(table.SpatialIndexes, this.spatialIndexes);
        Refill(table.JsonIndexes, this.jsonIndexes);
        for (var i = 0; i < this.jsonIndexes.Length; i++)
            (this.jsonIndexes[i].Name, this.jsonIndexes[i].ColumnOrdinal, this.jsonIndexes[i].IsDisabled) = this.jsonIndexState[i];
        Refill(table.VectorIndexes, this.vectorIndexes);
        for (var i = 0; i < this.vectorIndexes.Length; i++)
            (this.vectorIndexes[i].Name, this.vectorIndexes[i].ColumnOrdinal) = this.vectorIndexState[i];
        foreach (var (trigger, isDisabled) in this.triggers)
            trigger.IsDisabled = isDisabled;
        Refill(table.EdgeConstraints, this.edgeConstraints);
        table.PeriodColumns = this.periodColumns;
        this.heapOptions.RestoreToHeap(table);
        table.PageCompressed = this.pageCompressed;
        // What the storage views lay out follows the options put back.
        _ = Interlocked.Increment(ref table.LayoutVersion);
    }

    /// <summary>
    /// The storage options a rebuild or an <c>ALTER INDEX … SET</c> changes in
    /// place on an index, a key constraint's index or the heap, the
    /// per-partition lists copied, since a rebuild edits them.
    /// </summary>
    private readonly struct StorageOptions
    {
        private readonly byte fillFactor;
        private readonly bool isPadded;
        private readonly bool allowRowLocks;
        private readonly bool allowPageLocks;
        private readonly bool optimizeForSequentialKey;
        private readonly bool statisticsNoRecompute;
        private readonly byte dataCompression;
        private readonly List<byte>? partitionDataCompression;
        private readonly bool xmlCompression;
        private readonly List<byte>? partitionXmlCompression;

        private StorageOptions(byte fillFactor, bool isPadded, bool allowRowLocks, bool allowPageLocks, bool optimizeForSequentialKey, bool statisticsNoRecompute,
            byte dataCompression, List<byte>? partitionDataCompression, bool xmlCompression, List<byte>? partitionXmlCompression)
        {
            this.fillFactor = fillFactor;
            this.isPadded = isPadded;
            this.allowRowLocks = allowRowLocks;
            this.allowPageLocks = allowPageLocks;
            this.optimizeForSequentialKey = optimizeForSequentialKey;
            this.statisticsNoRecompute = statisticsNoRecompute;
            this.dataCompression = dataCompression;
            this.partitionDataCompression = Copy(partitionDataCompression);
            this.xmlCompression = xmlCompression;
            this.partitionXmlCompression = Copy(partitionXmlCompression);
        }

        public static StorageOptions Of(KeyConstraint key) => new(key.FillFactor, key.IsPadded, key.AllowRowLocks, key.AllowPageLocks, key.OptimizeForSequentialKey,
            key.StatisticsNoRecompute, key.DataCompression, key.PartitionDataCompression, key.XmlCompression, key.PartitionXmlCompression);

        public static StorageOptions Of(Index index) => new(index.FillFactor, index.IsPadded, index.AllowRowLocks, index.AllowPageLocks, index.OptimizeForSequentialKey,
            index.StatisticsNoRecompute, index.DataCompression, index.PartitionDataCompression, index.XmlCompression, index.PartitionXmlCompression);

        public static StorageOptions OfHeap(HeapTable table) => new(0, false, table.HeapAllowRowLocks, table.HeapAllowPageLocks, false,
            false, table.HeapDataCompression, table.HeapPartitionDataCompression, table.HeapXmlCompression, table.HeapPartitionXmlCompression);

        public void RestoreTo(KeyConstraint key)
        {
            key.FillFactor = this.fillFactor;
            key.IsPadded = this.isPadded;
            key.AllowRowLocks = this.allowRowLocks;
            key.AllowPageLocks = this.allowPageLocks;
            key.OptimizeForSequentialKey = this.optimizeForSequentialKey;
            key.StatisticsNoRecompute = this.statisticsNoRecompute;
            key.DataCompression = this.dataCompression;
            key.PartitionDataCompression = Copy(this.partitionDataCompression);
            key.XmlCompression = this.xmlCompression;
            key.PartitionXmlCompression = Copy(this.partitionXmlCompression);
        }

        public void RestoreTo(Index index)
        {
            index.FillFactor = this.fillFactor;
            index.IsPadded = this.isPadded;
            index.AllowRowLocks = this.allowRowLocks;
            index.AllowPageLocks = this.allowPageLocks;
            index.OptimizeForSequentialKey = this.optimizeForSequentialKey;
            index.StatisticsNoRecompute = this.statisticsNoRecompute;
            index.DataCompression = this.dataCompression;
            index.PartitionDataCompression = Copy(this.partitionDataCompression);
            index.XmlCompression = this.xmlCompression;
            index.PartitionXmlCompression = Copy(this.partitionXmlCompression);
        }

        public void RestoreToHeap(HeapTable table)
        {
            table.HeapAllowRowLocks = this.allowRowLocks;
            table.HeapAllowPageLocks = this.allowPageLocks;
            table.HeapDataCompression = this.dataCompression;
            table.HeapPartitionDataCompression = Copy(this.partitionDataCompression);
            table.HeapXmlCompression = this.xmlCompression;
            table.HeapPartitionXmlCompression = Copy(this.partitionXmlCompression);
        }

        private static List<byte>? Copy(List<byte>? list) => list is null ? null : [.. list];
    }

    private static void Refill<T>(List<T> list, T[] contents)
    {
        list.Clear();
        list.AddRange(contents);
    }
}
