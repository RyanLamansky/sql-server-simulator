using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [CLUSTERED | NONCLUSTERED] COLUMNSTORE INDEX name ON
    /// table [(col [, …])] [ORDER (col [, …])] [WHERE filter] [WITH (option
    /// [, …])] [ON filegroup]</c> — the nonclustered form the default, and the
    /// only one taking (and requiring) a column list. Cursor on entry: the
    /// <c>COLUMNSTORE</c> word. The index stores nothing of its own; it is
    /// catalog metadata whose declaration real's own refusals govern (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static bool ParseCreateColumnstoreIndex(ParserContext context, bool isUnique, bool isClustered)
    {
        if (isUnique)
            throw SimulatedSqlException.ColumnstoreIndexCannotBeUnique();
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Index })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var indexName = nameToken.Value;
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var targetTableName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        List<string>? columnNames = null;
        if (context.Token is Operator { Character: '(' })
        {
            if (isClustered)
                throw SimulatedSqlException.ClusteredColumnstoreKeyList();
            columnNames = ParseColumnstoreColumnList(context);
        }
        else if (!isClustered)
        {
            throw SimulatedSqlException.ColumnstoreKeyListMissing();
        }

        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Include })
            throw SimulatedSqlException.ColumnstoreIndexIncludedColumns();

        List<string> orderNames = [];
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            orderNames = ParseColumnstoreColumnList(context);
        }

        var (_, filter, filterDefinition, options) = ParseIndexTail(context, indexName, targetTableName.Leaf, acceptsInclude: false, IndexOptionStatement.CreateColumnstoreIndex, indexName);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(targetTableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        RecordTableDdlUndo(context, table);
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        var collation = context.Batch.CurrentDatabase.Collation;
        if (filter is not null)
            RejectComputedColumnInIndexFilter(context.Batch, table, indexName, FormatQualifiedTableName(targetTableName, table), filter);

        StoredIndex? replaced = null;
        foreach (var existing in table.Indexes)
        {
            if (collation.Equals(existing.Name, indexName))
                replaced = options.DropExisting ? existing : throw SimulatedSqlException.IndexAlreadyExists(indexName, targetTableName.ToString());
        }
        if (options.DropExisting && replaced is null)
            throw SimulatedSqlException.IndexNotFoundForDropExisting(indexName, table.Name);
        if (replaced is { IsClustered: true } && !isClustered)
            throw SimulatedSqlException.DropExistingClusteredToNonclustered();

        if (isClustered && replaced is null
            && (table.KeyConstraints.Exists(k => k.IsClustered) || table.Indexes.Exists(ix => ix.IsClustered)))
        {
            throw SimulatedSqlException.MoreThanOneClusteredIndexForColumnstore(table.Name);
        }
        if (table.Indexes.Exists(ix => ix.IsColumnstore && ix != replaced))
            throw SimulatedSqlException.MultipleColumnstoreIndexes();

        // The columns the index holds: the named ones, or every column of the
        // table for a clustered index — whose computed columns ride along.
        var ordinals = new List<int>();
        if (columnNames is not null)
        {
            RejectDuplicateIndexColumns(collation, [.. columnNames], [], inline: false);
            foreach (var columnName in columnNames)
            {
                var ordinal = ResolveColumnOrdinal(collation, table, columnName);
                if (table.Columns[ordinal].Computed is not null)
                    throw SimulatedSqlException.ColumnstoreIndexComputedColumn(table.Columns[ordinal].Name, table.Name);
                ordinals.Add(ordinal);
            }
        }
        else
        {
            ordinals.AddRange(Enumerable.Range(0, table.Columns.Length));
        }
        foreach (var ordinal in ordinals)
        {
            if (ColumnstoreRefusesType(table.Columns[ordinal], isClustered))
                throw SimulatedSqlException.ColumnstoreUnsupportedType(table.Columns[ordinal].Name);
        }
        var order = new int[orderNames.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = ResolveColumnOrdinal(collation, table, orderNames[i]);

        int[] storageOrdinals = [];
        int[] fullOrdinals = [];
        if (!isClustered)
        {
            fullOrdinals = [.. ordinals];
            storageOrdinals = [.. ordinals.Select(o => table.StorageOrdinals[o])];
        }

        var index = new StoredIndex(
            indexName,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            isUnique: false,
            isClustered,
            keyColumns: [],
            storageOrdinals,
            fullOrdinals,
            filter,
            filterDefinition,
            options,
            isColumnstore: true,
            columnstoreOrder: order);
        if (replaced is not null)
            table.Indexes[table.Indexes.IndexOf(replaced)] = index;
        else
            table.Indexes.Add(index);
        RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(targetTableName), indexName, "INDEX", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// A parenthesized column list, cursor on its <c>(</c>; leaves the cursor
    /// past the <c>)</c>. A sort order on a column is Msg 35302.
    /// </summary>
    private static List<string> ParseColumnstoreColumnList(ParserContext context)
    {
        List<string> names = [];
        do
        {
            if (context.GetNextRequired() is not Name column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(column.Value);
            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc })
                throw SimulatedSqlException.ColumnstoreIndexSortOrder();
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return names;
    }

    /// <summary>
    /// Whether a columnstore index of the given kind can't hold
    /// <paramref name="column"/> (Msg 35343): the legacy LOB types, <c>xml</c>,
    /// <c>sql_variant</c>, the CLR types and <c>rowversion</c> for either,
    /// and the MAX-length types too for a nonclustered one (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    internal static bool ColumnstoreRefusesType(HeapColumn column, bool clustered) =>
        column.Type is TextSqlType or NTextSqlType or ImageSqlType or XmlSqlType or SqlVariantSqlType
            or HierarchyIdSqlType or SpatialSqlType or RowVersionSqlType
        || (!clustered && column.MaxLength == SqlType.MaxLengthSentinel);
}
