using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

// ALTER TABLE's index forms — ADD INDEX, DROP INDEX, ALTER INDEX … REBUILD —
// which only a memory-optimized table takes, CREATE / DROP / ALTER INDEX being
// refused on one (probed 2026-10-02 against SQL Server 2025).
partial class Simulation
{
    /// <summary>
    /// One <c>INDEX name [UNIQUE] [NONCLUSTERED] [HASH] (cols) [WITH (…)]</c>
    /// element of an <c>ALTER TABLE … ADD</c> list, entered on <c>INDEX</c>.
    /// A disk-based table refuses it with Msg 10785 then Msg 1750; a
    /// memory-optimized one refuses the shapes its indexes can't take, and a
    /// <c>UNIQUE</c> one checks the rows already there.
    /// </summary>
    private static bool ParseAddIndex(ParserContext context, MultiPartName tableName)
    {
        var pending = ParseTableLevelInlineIndex(context, tableName.Leaf, refusesColumnstore: false);
        if (context.Batch.IsSkipping)
            return true;
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        if (!table.IsMemoryOptimized)
            throw SimulatedSqlException.AlterTableIndexOnDiskTable(drop: false);
        RejectIndexShapeForTable(table, pending.Name, pending.IsClustered, pending.IsColumnstore, pending.Options, pending.Filter is not null, pending.IncludeColumnNames.Count > 0);
        var collation = context.Batch.CurrentDatabase.Collation;
        foreach (var (columnName, _) in pending.Columns)
        {
            if (!Array.Exists(table.Columns, column => collation.Equals(column.Name, columnName)))
                throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.IndexColumnMissing(columnName));
        }
        RecordTableDdlUndo(context, table);
        AddInlineIndexes(context.Batch, table, tableName.ToString(), [pending]);
        var added = table.Indexes[^1];
        if (added.IsUnique)
            ValidateExistingRowsForUniqueIndex(table, added, context.Batch, FormatQualifiedTableName(tableName, table));
        table.SettleIndexIds();
        return true;
    }

    /// <summary>
    /// <c>ALTER TABLE … DROP INDEX name [, INDEX name …]</c>, entered on the
    /// first <c>INDEX</c>: a disk-based table refuses it with Msg 10785 state
    /// 2 then Msg 1750, and a name the table's indexes don't hold is Msg 3701
    /// state 21.
    /// </summary>
    private static bool TryParseAlterTableDropIndexes(ParserContext context, MultiPartName tableName)
    {
        var names = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(name.Value);
            if (context.GetNextOptional() is not Operator { Character: ',' })
                break;
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Index })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        if (!table.IsMemoryOptimized)
            throw SimulatedSqlException.AlterTableIndexOnDiskTable(drop: true);
        var collation = context.Batch.CurrentDatabase.Collation;
        var dropped = new List<StoredIndex>(names.Count);
        foreach (var name in names)
            dropped.Add(table.Indexes.Find(index => collation.Equals(index.Name, name)) ?? throw SimulatedSqlException.MemoryOptimizedIndexMissing(name, alter: false));
        RecordTableDdlUndo(context, table);
        table.SettleIndexIds();
        foreach (var index in dropped)
            _ = table.Indexes.Remove(index);
        return true;
    }

    /// <summary>
    /// <c>ALTER TABLE … ALTER INDEX name REBUILD WITH (BUCKET_COUNT = n)</c>,
    /// entered on <c>INDEX</c>: the one rebuild a memory-optimized table takes,
    /// changing a hash index's bucket count. The <c>WITH</c> list is required
    /// (Msg 102 without it), a range index refuses <c>BUCKET_COUNT</c> (Msg
    /// 10790), and a name no index or key holds is Msg 3701 state 22.
    /// </summary>
    private static bool TryParseAlterTableAlterIndex(ParserContext context, MultiPartName tableName)
    {
        if (context.GetNextRequired() is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Rebuild })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var options = ParseOptionalIndexWithClause(context);
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        var collation = context.Batch.CurrentDatabase.Collation;
        var key = table.KeyConstraints.Find(constraint => collation.Equals(constraint.Name, name.Value));
        var index = key is null ? table.Indexes.Find(candidate => collation.Equals(candidate.Name, name.Value)) : null;
        if (key is null && index is null)
            throw SimulatedSqlException.MemoryOptimizedIndexMissing(name.Value, alter: true);
        if (!(key?.IsHash ?? index!.IsHash))
        {
            if (options.BucketCount is not null)
                throw SimulatedSqlException.BucketCountOnRangeIndex();
            return true;
        }
        if (options.BucketCount is not null)
        {
            var rounded = IndexOptions.BucketCountFor(options.AsHash());
            RecordTableDdlUndo(context, table);
            if (key is not null)
                key.BucketCount = rounded;
            else
                index!.BucketCount = rounded;
        }
        return true;
    }
}
