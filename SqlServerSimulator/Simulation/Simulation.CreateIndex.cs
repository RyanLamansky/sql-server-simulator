using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX name ON
    /// table (col [ASC | DESC] [, …]) [INCLUDE (col [, …])] [WHERE filter]
    /// [WITH (option [, …])]</c>. Cursor on entry: any of <c>UNIQUE</c> /
    /// <c>CLUSTERED</c> / <c>NONCLUSTERED</c> / <c>INDEX</c> (the keyword
    /// the dispatcher hit just after <c>CREATE</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The simulator has no B-tree storage; non-UNIQUE indexes are catalog
    /// metadata only — they're visible through <c>sys.indexes</c> /
    /// <c>sys.index_columns</c> but don't accelerate queries or constrain
    /// inserts. UNIQUE indexes use the existing key-uniqueness mechanism
    /// (the same NULL-handling rule that <see cref="KeyConstraint"/>
    /// applies — first NULL allowed, second raises Msg 2601). When a
    /// WHERE filter is present, only rows for which the filter evaluates
    /// true participate in the uniqueness check, mirroring SQL Server's
    /// filtered-unique-index semantic.
    /// </para>
    /// <para>
    /// The <c>WITH (option = value, …)</c> clause is parsed and discarded:
    /// <c>FILLFACTOR</c>, <c>PAD_INDEX</c>, <c>IGNORE_DUP_KEY</c>,
    /// <c>ONLINE</c>, etc. are all valid SQL Server options that don't
    /// alter behavior in the simulator. The <c>CLUSTERED</c> keyword is
    /// likewise accepted but doesn't change storage — every table is a
    /// flat heap regardless of declared clustering.
    /// </para>
    /// </remarks>
    internal static bool TryParseCreateIndex(ParserContext context)
    {
        var isUnique = false;
        var isClustered = false;
        while (true)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Unique } when !isUnique:
                    isUnique = true;
                    context.MoveNextRequired();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Clustered } when !isClustered:
                    isClustered = true;
                    context.MoveNextRequired();
                    continue;
                case ReservedKeyword { Keyword: Keyword.NonClustered }:
                    context.MoveNextRequired();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Index }:
                    break;
                default:
                    return false;
            }
            break;
        }

        // Cursor on INDEX. Index name follows.
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var indexName = nameToken.Value;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var targetTableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var keyColumns = new List<(string Name, bool IsDescending)>();
        do
        {
            if (context.GetNextRequired() is not Name keyCol)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var isDescending = false;
            context.MoveNextRequired();
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Asc }:
                    context.MoveNextRequired();
                    break;
                case ReservedKeyword { Keyword: Keyword.Desc }:
                    isDescending = true;
                    context.MoveNextRequired();
                    break;
            }
            keyColumns.Add((keyCol.Value, isDescending));
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var includeColumnNames = new List<string>();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Include })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            do
            {
                if (context.GetNextRequired() is not Name incCol)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                includeColumnNames.Add(incCol.Value);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

        BooleanExpression? filter = null;
        string? filterDefinition = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            filter = BooleanExpression.Parse(context);
            // filter_definition text capture isn't supported by the parser
            // surface — the catalog view reports NULL for filtered
            // indexes' definition until ParserContext gains span tracking.
        }

        // WITH (option = value, …) followed by an optional ON <filegroup>.
        // Both are parsed and discarded — the simulator has no B-tree
        // storage and no filegroup model, so none of the options are
        // semantically meaningful.
        SkipOptionalIndexWithClause(context);
        SkipOptionalFilegroupClause(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(targetTableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        var qualifiedTableName = FormatQualifiedTableName(targetTableName, table);

        foreach (var existing in table.Indexes)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(existing.Name, indexName))
                throw SimulatedSqlException.IndexAlreadyExists(indexName, qualifiedTableName);
        }
        foreach (var kc in table.KeyConstraints)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(kc.Name, indexName))
                throw SimulatedSqlException.IndexAlreadyExists(indexName, qualifiedTableName);
        }

        var resolvedKeyColumns = new IndexKeyColumn[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var fullOrdinal = ResolveColumnOrdinal(context.Batch.CurrentDatabase.Collation, table, keyColumns[i].Name);
            resolvedKeyColumns[i] = new IndexKeyColumn(table.StorageOrdinals[fullOrdinal], keyColumns[i].IsDescending);
        }
        var resolvedIncludeColumns = new int[includeColumnNames.Count];
        for (var i = 0; i < includeColumnNames.Count; i++)
        {
            var fullOrdinal = ResolveColumnOrdinal(context.Batch.CurrentDatabase.Collation, table, includeColumnNames[i]);
            resolvedIncludeColumns[i] = table.StorageOrdinals[fullOrdinal];
        }

        var index = new StoredIndex(
            indexName,
            context.CurrentDatabase.AllocateObjectId(),
            isUnique,
            isClustered,
            resolvedKeyColumns,
            resolvedIncludeColumns,
            filter,
            filterDefinition);

        if (isUnique)
            ValidateExistingRowsForUniqueIndex(table, index, context.Batch, qualifiedTableName);

        table.Indexes.Add(index);
        return true;
    }

    private static int ResolveColumnOrdinal(Collation collation, HeapTable table, string columnName)
    {
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
                return i;
        }
        throw SimulatedSqlException.IndexColumnMissing(columnName);
    }

    /// <summary>
    /// Schema-qualified table name in the form <c>dbo.Table</c> — used in
    /// index-related error messages where SQL Server's wording always
    /// includes the schema (verbatim against probed Msg 1913 / 3701 / 3723).
    /// </summary>
    private static string FormatQualifiedTableName(MultiPartName written, HeapTable table) =>
        written.Count >= 2 ? $"{written.ImmediateQualifier}.{table.Name}" : $"{Database.DefaultSchemaName}.{table.Name}";

    /// <summary>
    /// Linear-scan validation of existing rows for a new UNIQUE index.
    /// Decodes each row's key tuple (and evaluates the WHERE filter when
    /// present, skipping rows whose filter doesn't evaluate true), raising
    /// Msg 1505 on the first duplicate. Filter-aware: rows excluded by the
    /// filter aren't checked, mirroring SQL Server's filtered-unique-index
    /// semantic.
    /// </summary>
    private static void ValidateExistingRowsForUniqueIndex(HeapTable table, StoredIndex index, BatchContext batch, string qualifiedTableName)
    {
        var seen = new List<SqlValue[]>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;
        var rowValues = index.Filter is null ? null : new SqlValue[table.Columns.Length];

        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            if (index.Filter is { } filter && rowValues is not null)
            {
                for (var c = 0; c < table.Columns.Length; c++)
                {
                    var storageOrd = table.StorageOrdinals[c];
                    rowValues[c] = storageOrd >= 0
                        ? RowDecoder.DecodeColumn(storedColumns, rowBytes, storageOrd, lobStore)
                        : SqlValue.Null(table.Columns[c].Type);
                }
                if (EvaluateIndexFilter(filter, table, rowValues, batch) != true)
                    continue;
            }

            var key = new SqlValue[index.KeyColumns.Length];
            for (var k = 0; k < index.KeyColumns.Length; k++)
                key[k] = RowDecoder.DecodeColumn(storedColumns, rowBytes, index.KeyColumns[k].StorageOrdinal, lobStore);

            foreach (var prior in seen)
            {
                var match = true;
                for (var k = 0; k < key.Length; k++)
                {
                    if (!prior[k].Equals(key[k]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    throw SimulatedSqlException.DuplicateKeyOnCreate(qualifiedTableName, index.Name, key);
            }
            seen.Add(key);
        }
    }

    /// <summary>
    /// Evaluates a filtered-index <c>WHERE</c> predicate against a single
    /// row. <paramref name="rowValues"/> is indexed in full-column order
    /// (matching <see cref="HeapTable.Columns"/>); the resolver maps a
    /// referenced column name to its slot via case-insensitive name
    /// compare, the same shape <c>EnforceCheckConstraints</c> uses.
    /// </summary>
    internal static bool? EvaluateIndexFilter(BooleanExpression filter, HeapTable table, SqlValue[] rowValues, BatchContext batch)
    {
        SqlValue ResolveByName(MultiPartName reference)
        {
            for (var k = 0; k < table.Columns.Length; k++)
            {
                if (batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, reference.Leaf))
                    return rowValues[k];
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }
        var runtime = new RuntimeContext(ResolveByName, batch);
        return filter.Run(runtime);
    }

    /// <summary>
    /// Renders an index-violation key tuple for Msg 2601 the same way
    /// <c>FormatKeyValue</c> does for Msg 2627 (NULL as <c>&lt;NULL&gt;</c>,
    /// strings raw, numerics in invariant culture). Reuses the existing
    /// helper.
    /// </summary>
    internal static string FormatIndexKeyValues(SqlValue[] keyValues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < keyValues.Length; i++)
        {
            if (i > 0)
                _ = sb.Append(", ");
            _ = sb.Append(FormatKeyValue(keyValues[i]));
        }
        return sb.ToString();
    }
}
