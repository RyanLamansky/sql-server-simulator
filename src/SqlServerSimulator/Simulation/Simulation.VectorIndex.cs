using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// CREATE VECTOR INDEX, SQL Server 2025's DiskANN index over a vector column,
// probed 2026-09-29 against SQL Server 2025.
partial class Simulation
{
    /// <summary>The metric names <c>METRIC</c> takes, in <c>VECTOR_DISTANCE</c>'s order.</summary>
    internal static readonly string[] VectorMetrics = ["cosine", "dot", "euclidean"];

    /// <summary>
    /// Parses <c>CREATE VECTOR INDEX name ON table (col) WITH (METRIC = '…'
    /// [, TYPE = 'DiskANN'] [, MAXDOP = n]) [ON filegroup]</c>, the cursor
    /// entering on <c>VECTOR</c>.
    /// </summary>
    /// <remarks>
    /// The statement is a preview feature: with the database's
    /// <c>PREVIEW_FEATURES</c> off it is Msg 343 compiling the batch. The
    /// <c>WITH</c> list is mandatory and <c>METRIC</c> in it (Msg 153); a
    /// metric or type real doesn't know, or one not written as a string, is a
    /// syntax error at the value. Running, the refusals come in real's order:
    /// a temp table, a missing table, a missing column, a column that isn't
    /// <c>vector</c>, a name the table already uses, a second vector index on
    /// the column, and a table without a one-column <c>int</c> clustered
    /// primary key. Ahead of them all, inside a user transaction the
    /// statement is Msg 574, and under a SET option an index on a computed
    /// column refuses Msg 1934.
    /// </remarks>
    internal static bool TryParseCreateVectorIndex(ParserContext context)
    {
        if (!context.Batch.CurrentDatabase.ScopedConfiguration.PreviewFeatures)
            throw SimulatedSqlException.UnknownObjectTypeVector();
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
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var metric = ParseVectorIndexOptions(context);

        // The placement clause names a filegroup or "default"; neither is kept.
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            if (context.GetNextRequired() is not (Name or StringToken))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        if (context.Token is ReservedKeyword trailing)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(trailing);

        if (context.Batch.IsSkipping)
            return true;

        if (context.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.StatementInsideUserTransaction("CREATE VECTOR INDEX", state: 31);
        if (IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptions("CREATE VECTOR INDEX", setOptions);
        if (tableName.Leaf.StartsWith('#'))
            throw SimulatedSqlException.VectorIndexOnTempObject(tableName.Leaf);
        if (!context.Batch.TryResolveTable(tableName, out var table) || table.IsTableVariable)
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(tableName.ToString(), state: 1);

        var collation = context.Batch.CurrentDatabase.Collation;
        var ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, columnName));
        if (ordinal < 0)
            throw SimulatedSqlException.IndexColumnMissing(columnName, state: 201);
        var column = table.Columns[ordinal];
        if (column.Type is not VectorSqlType)
            throw SimulatedSqlException.VectorIndexColumnNotVector(column.Name, table.Name);

        if (table.Indexes.Exists(index => collation.Equals(index.Name, indexName))
            || table.KeyConstraints.Exists(key => collation.Equals(key.Name, indexName))
            || table.XmlIndexes.Exists(index => collation.Equals(index.Name, indexName))
            || table.SpatialIndexes.Exists(index => collation.Equals(index.Name, indexName))
            || table.JsonIndexes.Exists(index => collation.Equals(index.Name, indexName))
            || table.VectorIndexes.Exists(index => collation.Equals(index.Name, indexName)))
        {
            throw SimulatedSqlException.IndexAlreadyExists(indexName, tableName.ToString(), state: 203);
        }
        if (table.VectorIndexes.Exists(index => index.ColumnOrdinal == ordinal))
            throw SimulatedSqlException.VectorIndexAlreadyOnColumn(column.Name);
        var keyOrdinal = VectorIndexKeyOrdinal(table);

        table.OwningDatabase?.RejectWriteWhenReadOnly();
        var indexId = VectorIndex.IndexIdBase;
        foreach (var existing in table.VectorIndexes)
            indexId = Math.Max(indexId, existing.IndexId + 1);
        table.VectorIndexes.Add(new VectorIndex(indexName, ordinal, indexId, metric.ToUpperInvariant(), VectorIndexStartId(table, keyOrdinal, ordinal, metric)));
        // Real's build reports Msg 8625 and raises no CREATE_INDEX event,
        // where dropping the index does (probed 2026-09-29 against SQL Server
        // 2025).
        context.Batch.AppendInfoError(@class: 0, state: 0, SimulatedSqlException.JoinOrderEnforcedMessageNumber, SimulatedSqlException.JoinOrderEnforcedMessage);
        return true;
    }

    /// <summary>
    /// The full ordinal of the table's clustered primary key column, which a
    /// vector index requires to be one <c>int</c> column: Msg 42217 state 1
    /// when there is no one-column clustered primary key, state 2 when its
    /// column has another type.
    /// </summary>
    internal static int VectorIndexKeyOrdinal(HeapTable table)
    {
        var key = table.KeyConstraints.Find(static key => key is { Kind: KeyConstraintKind.PrimaryKey, IsClustered: true });
        if (key is null || key.FullOrdinals.Length != 1)
            throw SimulatedSqlException.VectorIndexNeedsIntClusteredKey(table.Name, 1);
        return table.Columns[key.FullOrdinals[0]].Type == SqlType.Int32
            ? key.FullOrdinals[0]
            : throw SimulatedSqlException.VectorIndexNeedsIntClusteredKey(table.Name, 2);
    }

    /// <summary>
    /// Reads <c>WITH (option = value [, …])</c> from the <c>WITH</c>, leaving
    /// the cursor past its <c>)</c>, and returns the metric as written.
    /// </summary>
    private static string ParseVectorIndexOptions(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        string? metric = null;
        while (true)
        {
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
                case "MAXDOP":
                    var negative = value is Operator { Character: '-' };
                    if (negative)
                        _ = context.GetNextRequired();
                    if (context.Token is not Numeric { Value: { IsNull: false } degree })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (negative || degree.CoerceTo(SqlType.BigInt).AsInt64 > 32767)
                        throw SimulatedSqlException.IndexMaxDopOutOfRange((negative ? "-" : "") + context.Token.Source.ToString());
                    break;
                case "METRIC":
                    metric = value is Literal { Value: { IsNull: false } text } && SqlType.IsStringCategory(text.Type) && VectorArguments.Choice(text.AsString, VectorMetrics) >= 0
                        ? text.AsString.TrimEnd(' ')
                        : throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case "TYPE":
                    if (value is not Literal { Value: { IsNull: false } type } || !SqlType.IsStringCategory(type.Type)
                        || !type.AsString.TrimEnd(' ').Equals("DiskANN", StringComparison.OrdinalIgnoreCase))
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    break;
                default:
                    if (IndexOptionNames.Contains(option))
                        throw SimulatedSqlException.VectorIndexOptionNotTaken(option);
                    throw value is Numeric
                        ? SimulatedSqlException.UnrecognizedVectorIndexOption(option)
                        : SimulatedSqlException.UnrecognizedIndexOption(option, "CREATE VECTOR INDEX");
            }
            if (context.GetNextRequired() is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return metric ?? throw SimulatedSqlException.VectorIndexMetricMissing();
        }
    }

    /// <summary>
    /// The <c>StartId</c> real's build reports: the clustered key of the
    /// vector nearest, by the index's metric, to the mean of the table's
    /// vectors. Rows are visited in key order except that the second comes
    /// ahead of the first, and only a strictly nearer vector displaces the
    /// one held — the order that reproduces which of several equally near
    /// rows real names (probed 2026-09-29 against SQL Server 2025). A float16
    /// column reports its lowest key, and a table with no vectors 0.
    /// </summary>
    private static string VectorIndexStartId(HeapTable table, int keyOrdinal, int vectorOrdinal, string metric)
    {
        var keyStorage = table.StorageOrdinals[keyOrdinal];
        var vectorStorage = table.StorageOrdinals[vectorOrdinal];
        var rows = new List<(int Key, float[] Vector)>();
        foreach (var bytes in table.Rows)
        {
            var vector = RowDecoder.DecodeColumn(table.StoredColumns, bytes, vectorStorage, table.Heap);
            if (!vector.IsNull)
                rows.Add((RowDecoder.DecodeColumn(table.StoredColumns, bytes, keyStorage, table.Heap).AsInt32, VectorSqlType.Elements(vector.AsVectorBytes)));
        }
        if (rows.Count == 0)
            return "0";
        rows.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        // Over float16 vectors real names the lowest key.
        if (((VectorSqlType)table.Columns[vectorOrdinal].Type).IsFloat16)
            return rows[0].Key.ToString(CultureInfo.InvariantCulture);
        if (rows.Count > 1)
            (rows[0], rows[1]) = (rows[1], rows[0]);

        var mean = new float[rows[0].Vector.Length];
        foreach (var (_, vector) in rows)
        {
            for (var i = 0; i < mean.Length; i++)
                mean[i] += vector[i];
        }
        for (var i = 0; i < mean.Length; i++)
            mean[i] /= rows.Count;

        var metricIndex = VectorArguments.Choice(metric, VectorMetrics);
        var best = rows[0].Key;
        var bestDistance = VectorDistance.Compute(metricIndex, rows[0].Vector, mean);
        for (var r = 1; r < rows.Count; r++)
        {
            var distance = VectorDistance.Compute(metricIndex, rows[r].Vector, mean);
            if (distance < bestDistance)
                (best, bestDistance) = (rows[r].Key, distance);
        }
        return best.ToString(CultureInfo.InvariantCulture);
    }
}
