using SqlServerSimulator.Parser.FullText;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>CONTAINSTABLE</c> / <c>FREETEXTTABLE</c> — the rowset forms of the two
/// full-text predicates, projecting <c>KEY</c> and <c>RANK</c>. Built as a
/// <see cref="Selection"/> factory alongside <c>OPENJSON</c> and
/// <c>STRING_SPLIT</c> so the FROM-source machinery (alias, JOIN, APPLY,
/// lateral re-execution) needs nothing new.
/// </summary>
/// <remarks>
/// <para>
/// <c>KEY</c> carries the type of the column the index's <c>KEY INDEX</c> names
/// — <c>int</c> for the usual identity primary key, <c>varchar(20)</c> for a
/// string key (probe-confirmed through
/// <c>sys.dm_exec_describe_first_result_set</c>). <c>RANK</c> is always
/// <c>int</c>.
/// </para>
/// <para>
/// <c>RANK</c> <b>values</b> are the simulator's own. Real's come from the
/// engine's relevance scorer, which probing showed to be quantized and
/// corpus-dependent in ways no published formula reproduces — the same term at
/// the same frequency in a same-length document scored 32 in one table and 112
/// in another. The simulator computes a deterministic BM25-shaped score
/// instead: monotone in term frequency, falling with document length, rising
/// with term rarity, and stable across runs, which is what a consumer ordering
/// by <c>RANK</c> depends on. <c>docs/claude/full-text.md</c> records it as a
/// divergence.
/// </para>
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// Parses <c>CONTAINSTABLE(table, column_spec, condition [, LANGUAGE n]
    /// [, top_n_by_rank])</c> and its <c>FREETEXTTABLE</c> sibling with the
    /// cursor on the function keyword; on return the cursor sits one past the
    /// closing <c>)</c>.
    /// </summary>
    public static Selection ParseFullTextTable(ParserContext context, bool freeText)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableName);

        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var spec = FullTextColumnSpec.Parse(context);

        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var condition = Expression.Parse(context.MoveNextRequiredReturnSelf());

        Expression? topByRank = null;
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            // `LANGUAGE n` picks the word breaker; the simulator models English
            // only, so the argument is parsed for shape and discarded.
            if (context.Token is Name languageToken
                && context.Batch.CurrentDatabase.Collation.Equals(languageToken.Value, "LANGUAGE"))
            {
                _ = Expression.Parse(context.MoveNextRequiredReturnSelf());
                continue;
            }
            topByRank = Expression.Parse(context);
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var binding = FullTextColumnSpec.Bind(
            spec, table, tableName.Leaf, context.Batch.CurrentDatabase, context.Batch.CurrentDatabase.Collation, qualifier: null);
        var keyStorageOrdinal = ResolveFullTextKeyOrdinal(table);
        var keyType = table.StoredColumns[keyStorageOrdinal].Type;

        SqlType[] schema = [keyType, SqlType.Int32];
        string[] columnNames = ["KEY", "RANK"];
        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateFullTextTableRows(
                binding, condition, freeText, topByRank, keyStorageOrdinal, schema, batch, outerResolver));
    }

    /// <summary>
    /// Finds the storage ordinal of the single column the index's
    /// <c>KEY INDEX</c> names, falling back to the table's primary key when the
    /// index was created without one (the BACPAC path can leave the name
    /// empty).
    /// </summary>
    private static int ResolveFullTextKeyOrdinal(HeapTable table)
    {
        var index = table.FullTextIndex!;
        foreach (var keyConstraint in table.KeyConstraints)
        {
            if (keyConstraint.StorageOrdinals.Length > 0
                && string.Equals(keyConstraint.Name, index.KeyIndexName, StringComparison.OrdinalIgnoreCase))
            {
                return keyConstraint.StorageOrdinals[0];
            }
        }
        foreach (var tableIndex in table.Indexes)
        {
            if (tableIndex.KeyStorageOrdinals.Length > 0
                && string.Equals(tableIndex.Name, index.KeyIndexName, StringComparison.OrdinalIgnoreCase))
            {
                return tableIndex.KeyStorageOrdinals[0];
            }
        }
        foreach (var keyConstraint in table.KeyConstraints)
        {
            if (keyConstraint.Kind == KeyConstraintKind.PrimaryKey && keyConstraint.StorageOrdinals.Length > 0)
                return keyConstraint.StorageOrdinals[0];
        }
        return 0;
    }

    /// <summary>
    /// Scans the table once, word-breaking each row's searched columns, and
    /// emits <c>(KEY, RANK)</c> for every match. The corpus statistics the rank
    /// needs — how many rows hold each term, and the average document length —
    /// come from the same pass, so the whole scan happens before the first row
    /// is yielded.
    /// </summary>
    private static IEnumerable<byte[]> EnumerateFullTextTableRows(
        FullTextBinding binding,
        Expression condition,
        bool freeText,
        Expression? topByRank,
        int keyStorageOrdinal,
        SqlType[] schema,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);

        var conditionValue = condition.Run(runtime);
        if (conditionValue.IsNull)
            throw SimulatedSqlException.FullTextNullOrEmptyPredicate();
        var conditionText = conditionValue.AsString;
        if (string.IsNullOrWhiteSpace(conditionText))
            throw SimulatedSqlException.FullTextNullOrEmptyPredicate();

        var compiled = freeText
            ? FullTextSearchCondition.ParseFreeText(conditionText, binding.AccentSensitive)
            : FullTextSearchCondition.ParseContains(conditionText, binding.AccentSensitive);
        if (compiled.SawStopword)
            batch.AppendFullTextNoiseWordMessage();

        var limit = ResolveTopByRank(topByRank, runtime);
        if (limit == 0)
            yield break;

        List<(FullTextTermNode Leaf, double Weight)> leaves = [];
        compiled.Root.CollectLeaves(leaves, 1.0);

        var table = binding.Table;
        var storedSchema = table.StoredColumns;
        var searchedStorageOrdinals = new int[binding.ColumnOrdinals.Length];
        for (var i = 0; i < searchedStorageOrdinals.Length; i++)
            searchedStorageOrdinals[i] = table.StorageOrdinals[binding.ColumnOrdinals[i]];

        var documentFrequencies = new int[leaves.Count];
        List<(SqlValue Key, int[] Frequencies, int Length)> matches = [];
        var rowCount = 0;
        var totalLength = 0L;

        foreach (var bytes in table.Rows)
        {
            rowCount++;
            var document = new FullTextDocument();
            foreach (var storageOrdinal in searchedStorageOrdinals)
            {
                var value = RowDecoder.DecodeColumn(storedSchema, bytes, storageOrdinal, table.Heap);
                document.AddColumn(FullTextBinding.TextOf(value), binding.AccentSensitive);
            }
            totalLength += document.Length;

            var frequencies = new int[leaves.Count];
            for (var i = 0; i < leaves.Count; i++)
            {
                frequencies[i] = leaves[i].Leaf.TermFrequency(document);
                if (frequencies[i] > 0)
                    documentFrequencies[i]++;
            }
            if (compiled.Matches(document))
                matches.Add((RowDecoder.DecodeColumn(storedSchema, bytes, keyStorageOrdinal, table.Heap), frequencies, document.Length));
        }

        var averageLength = rowCount == 0 ? 1.0 : Math.Max(1.0, (double)totalLength / rowCount);
        List<(SqlValue Key, int Rank)> ranked = new(matches.Count);
        foreach (var (key, frequencies, length) in matches)
        {
            ranked.Add((key, ComputeRank(leaves, frequencies, documentFrequencies, rowCount, length, averageLength)));
        }
        ranked.Sort(static (left, right) => right.Rank.CompareTo(left.Rank));

        var emitted = 0;
        foreach (var (key, rank) in ranked)
        {
            if (limit is { } cap && emitted >= cap)
                yield break;
            emitted++;
            yield return RowEncoder.EncodeRow(schema, [key, SqlValue.FromInt32(rank)]);
        }
    }

    /// <summary>
    /// Reads the optional <c>top_n_by_rank</c> argument. Real answers 0 with an
    /// empty rowset and refuses a negative literal at the grammar level
    /// (Msg 102 near the minus sign), which the expression parser already does.
    /// </summary>
    private static int? ResolveTopByRank(Expression? topByRank, RuntimeContext runtime)
    {
        if (topByRank is null)
            return null;
        var value = topByRank.Run(runtime);
        return value.IsNull ? null : (int)value.CoerceTo(SqlType.BigInt).AsInt64;
    }

    /// <summary>
    /// The modeled relevance score: a BM25 sum over the condition's leaf terms,
    /// scaled into real's 0–1000 band. Deterministic for a given corpus, and
    /// ordered the way a reader expects; the values themselves do not match
    /// real's.
    /// </summary>
    private static int ComputeRank(
        List<(FullTextTermNode Leaf, double Weight)> leaves,
        int[] frequencies,
        int[] documentFrequencies,
        int rowCount,
        int documentLength,
        double averageLength)
    {
        const double SaturationK1 = 1.2;
        const double LengthB = 0.75;
        var score = 0.0;
        for (var i = 0; i < leaves.Count; i++)
        {
            var termFrequency = frequencies[i];
            if (termFrequency == 0)
                continue;
            var documentFrequency = Math.Max(1, documentFrequencies[i]);
            var inverseFrequency = Math.Log(1.0 + ((rowCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));
            var normalized = termFrequency * (SaturationK1 + 1.0)
                / (termFrequency + (SaturationK1 * (1.0 - LengthB + (LengthB * documentLength / averageLength))));
            score += leaves[i].Weight * inverseFrequency * normalized;
        }
        return Math.Clamp((int)Math.Round(score * 100.0), 1, 1000);
    }
}
