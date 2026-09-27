using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// CREATE JSON INDEX, SQL Server 2025's index over a json column, probed
// 2026-09-27 against SQL Server 2025.
partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE JSON INDEX name ON table (col) [FOR ('path' [, …])]
    /// [WITH (option = value [, …])]</c>, the cursor entering on <c>JSON</c>.
    /// The paths are kept as written and default to <c>$</c>.
    /// </summary>
    /// <remarks>
    /// The refusals, all real's: a filegroup, a <c>WHERE</c> or <c>FOR</c>
    /// after <c>WITH</c> is a syntax error; an option real's index grammar
    /// knows but this statement doesn't take is Msg 153 state 35, an unknown
    /// one Msg 155 then 153; a temp table Msg 13675; a column that isn't
    /// <c>json</c> Msg 13680; a table without a clustered primary key Msg
    /// 13672; a second JSON index on the column Msg 13681; a path with an
    /// advanced accessor Msg 13683 (state 2 for <c>[*]</c>, 3 for the rest)
    /// and two paths where one is the other or leads to it — names compared
    /// without case, the mode keyword ignored — Msg 13683 state 1.
    /// </remarks>
    internal static bool TryParseCreateJsonIndex(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Index })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var indexName = nameToken.Value;
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var tableName = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name columnToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = columnToken.Value;
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        string[] paths = ["$"];
        if (context.Token is ReservedKeyword { Keyword: Keyword.For })
            paths = ParseJsonIndexPaths(context);

        var options = context.Token is ReservedKeyword { Keyword: Keyword.With }
            ? ParseJsonIndexOptions(context)
            : new JsonIndexOptions();
        if (context.Token is ReservedKeyword trailing)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(trailing);

        if (context.Batch.IsSkipping)
            return true;

        if (tableName.Leaf.StartsWith('#'))
            throw SimulatedSqlException.JsonIndexOnTempObject(tableName.Leaf);
        if (!context.Batch.TryResolveTable(tableName, out var table) || table.IsTableVariable)
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(tableName.ToString(), state: 3);

        var collation = context.Batch.CurrentDatabase.Collation;
        var ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, columnName));
        if (ordinal < 0)
            throw SimulatedSqlException.IndexColumnMissing(columnName, state: 7);
        var column = table.Columns[ordinal];
        if (column.Type is not JsonSqlType)
            throw SimulatedSqlException.JsonIndexColumnNotJson(column.Name, table.Name);
        if (!table.KeyConstraints.Exists(key => key is { Kind: KeyConstraintKind.PrimaryKey, IsClustered: true } && key.FullOrdinals.Length < 32))
            throw SimulatedSqlException.JsonIndexNeedsClusteredPrimaryKey(table.Name);

        if (table.Indexes.Exists(index => collation.Equals(index.Name, indexName))
            || table.KeyConstraints.Exists(key => collation.Equals(key.Name, indexName))
            || table.XmlIndexes.Exists(index => collation.Equals(index.Name, indexName))
            || table.SpatialIndexes.Exists(index => collation.Equals(index.Name, indexName))
            || (!options.DropExisting && table.JsonIndexes.Exists(index => collation.Equals(index.Name, indexName))))
        {
            throw SimulatedSqlException.IndexAlreadyExists(indexName, tableName.ToString(), state: 3);
        }

        var onColumn = table.JsonIndexes.Find(index => index.ColumnOrdinal == ordinal);
        JsonIndex? replaced = null;
        if (options.DropExisting)
        {
            replaced = onColumn is not null && collation.Equals(onColumn.Name, indexName)
                ? onColumn
                : throw SimulatedSqlException.JsonIndexNotFound(indexName, column.Name, table.Name);
        }
        else if (onColumn is not null)
        {
            throw SimulatedSqlException.JsonIndexAlreadyOnColumn(onColumn.Name, column.Name, table.Name);
        }

        table.OwningDatabase?.RejectWriteWhenReadOnly();
        var indexId = replaced?.IndexId ?? NextJsonIndexId(table);
        if (replaced is not null)
            _ = table.JsonIndexes.Remove(replaced);
        table.JsonIndexes.Add(new JsonIndex(
            indexName,
            ordinal,
            indexId,
            paths,
            options.FillFactor,
            options.PadIndex,
            options.AllowRowLocks,
            options.AllowPageLocks,
            options.OptimizeForArraySearch));
        RecordDdlEvent(context, "CREATE_INDEX", EventSchemaName(tableName), indexName, "INDEX", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// JSON indexes take ids from real's dedicated 1216000+ range, one past
    /// the table's highest, so a dropped top id is reused and a dropped middle
    /// one isn't.
    /// </summary>
    private static int NextJsonIndexId(HeapTable table)
    {
        var next = JsonIndex.IndexIdBase;
        foreach (var existing in table.JsonIndexes)
            next = Math.Max(next, existing.IndexId + 1);
        return next;
    }

    /// <summary>
    /// Reads <c>FOR ('path' [, …])</c> from the <c>FOR</c>: string literals
    /// only, each a well-formed path (Msg 13607 as any reader reports it),
    /// then the accessor and overlap rules.
    /// </summary>
    private static string[] ParseJsonIndexPaths(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var written = new List<string>();
        while (true)
        {
            switch (context.GetNextRequired())
            {
                case Literal { Value: { IsNull: false } value } when SqlType.IsStringCategory(value.Type):
                    written.Add(value.AsString);
                    break;
                case ReservedKeyword keyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.GetNextRequired() is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            break;
        }

        var parsed = new JsonPath[written.Count];
        for (var i = 0; i < written.Count; i++)
        {
            parsed[i] = JsonPath.Parse(written[i]);
            foreach (var segment in parsed[i].Segments)
            {
                switch (segment.Kind)
                {
                    case JsonPath.SegmentKind.ArrayWildcard:
                        throw SimulatedSqlException.JsonIndexPathsInvalid(2);
                    case JsonPath.SegmentKind.PropertyWildcard or JsonPath.SegmentKind.ArraySelector:
                        throw SimulatedSqlException.JsonIndexPathsInvalid(3);
                }
            }
        }

        var collation = context.Batch.CurrentDatabase.Collation;
        for (var i = 0; i < parsed.Length; i++)
        {
            for (var j = i + 1; j < parsed.Length; j++)
            {
                if (LeadsTo(parsed[i], parsed[j], collation) || LeadsTo(parsed[j], parsed[i], collation))
                    throw SimulatedSqlException.JsonIndexPathsInvalid(1);
            }
        }
        return [.. written];

        static bool LeadsTo(in JsonPath prefix, in JsonPath path, Collation collation)
        {
            if (prefix.Segments.Length > path.Segments.Length)
                return false;
            for (var s = 0; s < prefix.Segments.Length; s++)
            {
                var (a, b) = (prefix.Segments[s], path.Segments[s]);
                if (a.IsIndex != b.IsIndex || (a.IsIndex ? a.Index != b.Index : !collation.Equals(a.Property, b.Property)))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Reads <c>WITH (option = value [, …])</c> from the <c>WITH</c>. The
    /// options kept are the ones <c>sys.json_indexes</c> reports; the others
    /// real takes (<c>DROP_EXISTING</c>, <c>MAXDOP</c>,
    /// <c>DATA_COMPRESSION</c>) are read and discarded, bar
    /// <c>DROP_EXISTING</c>'s replace.
    /// </summary>
    private static JsonIndexOptions ParseJsonIndexOptions(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var options = new JsonIndexOptions();
        while (true)
        {
            // FILLFACTOR arrives as a reserved keyword, so the name is read
            // off the source as written.
            if (context.GetNextRequired() is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var option = context.Token!.Source.ToString();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired();
            Span<char> folded = stackalloc char[32];
            var upper = option.Length <= folded.Length ? folded[..option.AsSpan().ToUpperInvariant(folded)] : [];
            switch (upper)
            {
                case "ALLOW_PAGE_LOCKS":
                    options.AllowPageLocks = OnOff(context);
                    break;
                case "ALLOW_ROW_LOCKS":
                    options.AllowRowLocks = OnOff(context);
                    break;
                case "DATA_COMPRESSION":
                    break;
                case "DROP_EXISTING":
                    options.DropExisting = OnOff(context);
                    break;
                case "FILLFACTOR":
                    var fill = value is Numeric { Value: { IsNull: false } number } ? number.AsInt32 : throw SimulatedSqlException.SyntaxErrorNear(context);
                    options.FillFactor = fill is >= 1 and <= 100 ? (byte)fill : throw SimulatedSqlException.FillFactorOutOfRange(fill);
                    break;
                case "IGNORE_DUP_KEY":
                    throw SimulatedSqlException.InvalidJsonIndexOption(option);
                case "MAXDOP":
                    break;
                case "ONLINE":
                    throw SimulatedSqlException.InvalidJsonIndexOption(option);
                case "OPTIMIZE_FOR_ARRAY_SEARCH":
                    options.OptimizeForArraySearch = OnOff(context);
                    break;
                case "OPTIMIZE_FOR_SEQUENTIAL_KEY":
                    throw SimulatedSqlException.InvalidJsonIndexOption(option);
                case "PAD_INDEX":
                    options.PadIndex = OnOff(context);
                    break;
                case "RESUMABLE":
                case "SORT_IN_TEMPDB":
                case "STATISTICS_INCREMENTAL":
                case "STATISTICS_NORECOMPUTE":
                case "XML_COMPRESSION":
                    throw SimulatedSqlException.InvalidJsonIndexOption(option);
                default:
                    throw SimulatedSqlException.UnrecognizedJsonIndexOption(option);
            }
            if (context.GetNextRequired() is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return options;
        }

        static bool OnOff(ParserContext context) => ReadOnOffOptionValue(context, context.Token!);
    }

    /// <summary>The <c>WITH</c> options of a <c>CREATE JSON INDEX</c>, at their defaults until set.</summary>
    private sealed class JsonIndexOptions
    {
        public byte FillFactor;
        public bool PadIndex;
        public bool AllowRowLocks = true;
        public bool AllowPageLocks = true;
        public bool OptimizeForArraySearch;
        public bool DropExisting;
    }
}
