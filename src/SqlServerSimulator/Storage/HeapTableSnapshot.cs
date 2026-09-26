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
    private readonly int maxColumnIdUsed;
    private readonly HeapColumn[] columns;
    private readonly (string Name, int ColumnId, bool IsRowGuidCol, bool IsSparse, Parser.Expression? Default, DefaultConstraint? DefaultConstraint, string? DefaultName)[] columnState;
    private readonly Heap heap;
    private readonly HeapTable? systemVersioning;
    private readonly bool isHistoryTable;
    private readonly bool periodInheritedFromBase;
    private readonly int historyRetentionPeriod;
    private readonly HistoryRetentionUnit historyRetentionUnit;
    private readonly FullTextIndex? fullTextIndex;
    private readonly KeyConstraint[] keyConstraints;
    private readonly (string Name, bool IsDisabled)[] keyState;
    private readonly CheckConstraint[] checkConstraints;
    private readonly (string Name, bool IsNotTrusted, bool IsDisabled, string? Definition, bool IsSystemNamed)[] checkState;
    private readonly ForeignKey[] outgoingForeignKeys;
    private readonly ForeignKey[] incomingForeignKeys;
    private readonly (ForeignKey Key, string Name, bool IsNotTrusted, bool IsDisabled)[] foreignKeyState;
    private readonly Index[] indexes;
    private readonly (string Name, bool IgnoreDupKey, bool IsDisabled)[] indexState;
    private readonly UserStatistic[] userStatistics;
    private readonly XmlIndex[] xmlIndexes;
    private readonly SpatialIndex[] spatialIndexes;
    private readonly (Trigger Trigger, bool IsDisabled)[] triggers;

    public HeapTableSnapshot(HeapTable table, Database? database)
    {
        this.table = table;
        this.name = table.Name;
        this.modifyDate = table.ModifyDate;
        this.lockEscalation = table.LockEscalation;
        this.maxColumnIdUsed = table.MaxColumnIdUsed;
        this.columns = table.Columns;
        this.columnState = Array.ConvertAll(table.Columns, column => (
            column.Name, column.ColumnId, column.IsRowGuidCol, column.IsSparse, column.Default, column.DefaultConstraint,
            column.DefaultConstraint?.Name));
        this.heap = table.Heap;
        this.systemVersioning = table.SystemVersioning;
        this.isHistoryTable = table.IsHistoryTable;
        this.periodInheritedFromBase = table.PeriodInheritedFromBase;
        this.historyRetentionPeriod = table.HistoryRetentionPeriod;
        this.historyRetentionUnit = table.HistoryRetentionUnit;
        this.fullTextIndex = table.FullTextIndex;
        this.keyConstraints = [.. table.KeyConstraints];
        this.keyState = Array.ConvertAll(this.keyConstraints, key => (key.Name, key.IsDisabled));
        this.checkConstraints = [.. table.CheckConstraints];
        this.checkState = Array.ConvertAll(this.checkConstraints, check => (check.Name, check.IsNotTrusted, check.IsDisabled, check.Definition, check.IsSystemNamed));
        this.outgoingForeignKeys = [.. table.OutgoingForeignKeys];
        this.incomingForeignKeys = [.. table.IncomingForeignKeys];
        this.foreignKeyState = [.. this.outgoingForeignKeys.Concat(this.incomingForeignKeys).Distinct()
            .Select(key => (key, key.Name, key.IsNotTrusted, key.IsDisabled))];
        this.indexes = [.. table.Indexes];
        this.indexState = Array.ConvertAll(this.indexes, index => (index.Name, index.IgnoreDupKey, index.IsDisabled));
        this.userStatistics = [.. table.UserStatistics];
        this.xmlIndexes = [.. table.XmlIndexes];
        this.spatialIndexes = [.. table.SpatialIndexes];
        this.triggers = database is null
            ? []
            : [.. database.Schemas.Values.SelectMany(schema => schema.Triggers.Values)
                .Where(trigger => ReferenceEquals(trigger.Parent, table))
                .Select(trigger => (trigger, trigger.IsDisabled))];
    }

    public void Restore()
    {
        var table = this.table;
        table.Name = this.name;
        table.ModifyDate = this.modifyDate;
        table.LockEscalation = this.lockEscalation;
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
        table.RecomputeStorageProjections();
        table.Heap = this.heap;
        table.Heap.InvalidateSeekJournal();
        table.SystemVersioning = this.systemVersioning;
        table.IsHistoryTable = this.isHistoryTable;
        table.PeriodInheritedFromBase = this.periodInheritedFromBase;
        table.HistoryRetentionPeriod = this.historyRetentionPeriod;
        table.HistoryRetentionUnit = this.historyRetentionUnit;
        table.FullTextIndex = this.fullTextIndex;

        Refill(table.KeyConstraints, this.keyConstraints);
        for (var i = 0; i < this.keyConstraints.Length; i++)
            (this.keyConstraints[i].Name, this.keyConstraints[i].IsDisabled) = this.keyState[i];
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

        Refill(table.Indexes, this.indexes);
        for (var i = 0; i < this.indexes.Length; i++)
            (this.indexes[i].Name, this.indexes[i].IgnoreDupKey, this.indexes[i].IsDisabled) = this.indexState[i];
        Refill(table.UserStatistics, this.userStatistics);
        Refill(table.XmlIndexes, this.xmlIndexes);
        Refill(table.SpatialIndexes, this.spatialIndexes);
        foreach (var (trigger, isDisabled) in this.triggers)
            trigger.IsDisabled = isDisabled;
    }

    private static void Refill<T>(List<T> list, T[] contents)
    {
        list.Clear();
        list.AddRange(contents);
    }
}
