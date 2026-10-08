using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>VECTOR_SEARCH(TABLE = t [AS a], COLUMN = c, SIMILAR_TO = q, METRIC =
/// 'm', TOP_N = n) [AS s]</c>, SQL Server 2025's vector-index search, probed
/// 2026-09-29 against SQL Server 2025.
/// </summary>
/// <remarks>
/// <para>
/// Real exposes the result as two FROM members: the table's columns under the
/// table's own name or alias and a <c>distance</c> column under the function's
/// alias, <c>distance</c> first in a <c>SELECT *</c>. The simulator builds
/// exactly that pair — a rowset of <c>(distance, key)</c> joined on the
/// clustered key to an ordinary source over the table, as a parenthesized join
/// group — so every JOIN, APPLY and name rule the FROM clause knows applies
/// unchanged. The key column is hidden, which keeps it out of a star.
/// </para>
/// <para>
/// Real's DiskANN search is approximate; the simulator answers the exact
/// <c>TOP_N</c> nearest by the metric, ties in key order, which is what real
/// returns at small sizes (see <c>docs/claude/vector.md</c>).
/// </para>
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>The two sources a <c>VECTOR_SEARCH</c> exposes and the join that pairs them.</summary>
    private readonly struct VectorSearchSources(FromSource distance, FromSource table, BooleanExpression? keyJoin)
    {
        public readonly FromSource Distance = distance;
        public readonly FromSource Table = table;

        /// <summary>The clustered-key equality, or null when a skipped statement named a missing table.</summary>
        public readonly BooleanExpression? KeyJoin = keyJoin;
    }

    /// <summary>
    /// Whether the source after the cursor is <c>VECTOR_SEARCH ( TABLE</c> —
    /// the named-argument form. Anything else spelled <c>VECTOR_SEARCH</c> is
    /// an ordinary object name to real (Msg 208 for the positional form).
    /// </summary>
    private static bool NextSourceIsVectorSearch(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            if (context.GetNextOptional() is not Name { Value: var name } || !BuiltInToken.Equals(name, "VECTOR_SEARCH")
                || context.GetNextOptional() is not Operator { Character: '(' })
            {
                return false;
            }
            if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Table })
                return true;
            // A named argument other than TABLE first isn't a function
            // argument either: real stops at its '='.
            return context.Token is Name or ReservedKeyword && context.GetNextOptional() is Operator { Character: '=' }
                ? throw SimulatedSqlException.SyntaxErrorNear(context)
                : false;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Appends a <c>VECTOR_SEARCH</c>'s two sources and their key join to the
    /// flat lists, the cursor on the token before the function name.
    /// </summary>
    private static void AddVectorSearch(ParserContext context, List<FromSource> sources, List<JoinSpec> joins, Func<MultiPartName, SqlType>? argumentScope)
    {
        var search = ParseVectorSearch(context, argumentScope);
        AddVectorSearchSources(context, sources, search);
        joins.Add(search.KeyJoin is { } keyJoin ? new JoinSpec(JoinKind.Inner, keyJoin) : new JoinSpec(JoinKind.Cross, null));
    }

    /// <summary>
    /// Appends a <c>VECTOR_SEARCH</c> written as an <c>APPLY</c>'s right
    /// side: the distance rowset is the lateral member, re-run per left row,
    /// and the table joins it on the key — inner under <c>CROSS APPLY</c>,
    /// left under <c>OUTER APPLY</c> so a left row with no match keeps its
    /// NULLs. Its arguments see <paramref name="left"/>, the APPLY's chain to
    /// its left, as any APPLY right side does.
    /// </summary>
    private static void AddAppliedVectorSearch(ParserContext context, List<FromSource> sources, List<JoinSpec> joins, JoinKind applyKind, QueryScope scope, FromSource[] left)
    {
        var binding = context.FromBinding!;
        var resolver = binding.ResolverOver(left, scope.OuterTypeResolver);
        VectorSearchSources search;
        using (ParserScope.Enter(ref context.PartialScopeReferences, binding.References()))
            search = AcrossApplyBoundary(context, left, () => ParseVectorSearch(context, resolver));
        AddVectorSearchSources(context, sources, search);
        joins.Add(new JoinSpec(applyKind, onPredicate: null));
        joins.Add(search.KeyJoin is { } keyJoin
            ? new JoinSpec(applyKind == JoinKind.CrossApply ? JoinKind.Inner : JoinKind.Left, keyJoin)
            : new JoinSpec(JoinKind.Cross, null));
    }

    /// <summary>
    /// Adds the two members, a repeated exposed name among them or against an
    /// earlier source ending the statement at once: real reports that
    /// collision alone, where an ordinary source's joins the statement's
    /// whole binder report.
    /// </summary>
    private static void AddVectorSearchSources(ParserContext context, List<FromSource> sources, VectorSearchSources search)
    {
        var report = context.Batch.BindErrors;
        context.Batch.BindErrors = null;
        try
        {
            AddSource(context, sources, search.Distance);
            AddSource(context, sources, search.Table);
        }
        finally
        {
            context.Batch.BindErrors = report;
        }
    }

    /// <summary>
    /// Parses the call, the cursor on the token before <c>VECTOR_SEARCH</c>,
    /// leaving it past the result alias. The argument grammar is fixed and
    /// ordered; <c>SIMILAR_TO</c> takes a variable, a column of an enclosing
    /// scope or a literal, and <c>TOP_N</c> an integer literal, a variable or
    /// such a column — anything else a syntax error where real reports it.
    /// </summary>
    private static VectorSearchSources ParseVectorSearch(ParserContext context, Func<MultiPartName, SqlType>? argumentScope)
    {
        context.Batch.CurrentStatement.MarkOpensTransaction();
        // The hidden key column is named by the call's position, which keeps
        // two searches in one FROM apart and a replayed plan's names stable.
        var keyName = "\u0001vector_search_key_" + context.GetNextRequired().StartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = context.GetNextRequired();
        var tableKeyword = (ReservedKeyword)context.GetNextRequired();
        // Without the preview switch the name is an ordinary function call,
        // whose argument list can't open with TABLE.
        if (!context.Batch.CurrentDatabase.ScopedConfiguration.PreviewFeatures)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(tableKeyword);
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not (Name or Operator { Character: '.' }))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var tableName = BatchContext.ParseObjectName(context);
        var tableAlias = ConsumeOptionalAlias(context);

        ExpectVectorSearchArgument(context, "COLUMN");
        if (context.GetNextRequired() is not Name columnToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = columnToken.Value;
        context.MoveNextRequired();

        ExpectVectorSearchArgument(context, "SIMILAR_TO");
        context.MoveNextRequired();
        var similarTo = ParseVectorSearchOperand(context, acceptLiterals: true);

        ExpectVectorSearchArgument(context, "METRIC");
        if (context.GetNextRequired() is not Literal { Value: { IsNull: false } metricValue } || !SqlType.IsStringCategory(metricValue.Type))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var metricIndex = VectorArguments.Choice(metricValue.AsString, Simulation.VectorMetrics);
        if (metricIndex < 0)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var metric = metricValue.AsString.TrimEnd(' ');

        if (context.GetNextRequired() is Operator { Character: ')' })
            throw SimulatedSqlException.VectorSearchTopNMissing();
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name { Value: var topWord } || !BuiltInToken.Equals(topWord, "TOP_N"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        // A literal count must be an int; anything else written there is Msg
        // 1060, a sign or an expression a syntax error.
        switch (context.Token)
        {
            case Numeric { Value: var count } when count.Type != SqlType.Int32:
            case Literal or ReservedKeyword { Keyword: Keyword.Null }:
                throw SimulatedSqlException.TopFetchRequiresInteger();
        }
        var topN = ParseVectorSearchOperand(context, acceptLiterals: false);
        // A comma after TOP_N looks for another argument, and there is none.
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var resultAlias = ConsumeOptionalAlias(context);
        // The result takes no column list: `AS s (d)` stops at the first name.
        if (context.Token is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (context.Batch.TryResolveView(tableName, out _))
            throw SimulatedSqlException.VectorIndexNeedsIntClusteredKey("", 4);
        if (!context.Batch.TryResolveTable(tableName, out var table))
        {
            // A table created earlier in the batch binds when its statement
            // runs, as any other source's does.
            if (context.Batch.IsSkipping)
            {
                context.Batch.CurrentStatement.BindsDeferredSource = true;
                return new VectorSearchSources(FromSource.DeferredPlaceholder(resultAlias), FromSource.DeferredPlaceholder(tableAlias ?? tableName.Leaf), null);
            }
            throw SimulatedSqlException.InvalidObjectName(tableName, 240);
        }
        context.Batch.BeginImplicitTransaction();

        var collation = context.Batch.CurrentDatabase.Collation;
        var ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, columnName));
        if (ordinal < 0)
            throw SimulatedSqlException.VectorSearchColumnMissing(columnName);
        if (table.Columns[ordinal].Type is not VectorSqlType vectorType)
            throw SimulatedSqlException.VectorSearchColumnNotVector(columnName);
        if (!table.VectorIndexes.Exists(index => index.ColumnOrdinal == ordinal && index.Metric.Equals(metric, StringComparison.OrdinalIgnoreCase)))
            throw SimulatedSqlException.VectorSearchIndexMissing(metricValue.AsString, columnName);
        var keyOrdinal = Simulation.VectorIndexKeyOrdinal(table);

        // The operands bind in the scope a function argument binds in: an
        // APPLY's left side and the enclosing queries, never this FROM's own
        // sources.
        SqlType ResolveArgument(MultiPartName name) =>
            argumentScope is not null ? argumentScope(name)
            : name.ImmediateQualifier is null ? throw SimulatedSqlException.InvalidColumnName(name)
            : throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        var queryType = similarTo.GetSqlType(context.Batch, ResolveArgument);
        if (queryType is not VectorSqlType && !Expression.IsUntypedNullLiteral(similarTo))
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(queryType, similarTo), 3, "vector_distance");
        if (topN.GetSqlType(context.Batch, ResolveArgument).Category != SqlTypeCategory.Integer)
            throw SimulatedSqlException.TopFetchRequiresInteger();

        HeapColumn[] distanceColumns =
        [
            new("distance", SqlType.Float, maxLength: null, nullable: false),
            new(keyName, SqlType.Int32, maxLength: null, nullable: false, isHidden: true),
        ];
        SqlType[] schema = [SqlType.Float, SqlType.Int32];
        var plan = new Selection(schema, ["distance", keyName],
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateVectorSearchRows(table, table.StorageOrdinals[ordinal], table.StorageOrdinals[keyOrdinal], vectorType.dimensions, vectorType.IsFloat16, metricIndex, similarTo, topN, schema, batch, outerResolver))
        {
            ColumnNullability = [false, false],
        };
        var distance = new FromSource(
            qualifier: resultAlias,
            columnNames: ["distance", keyName],
            columns: distanceColumns,
            storedSchema: distanceColumns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);

        var heapPlan = context.Batch.AcquireDataLockIfApplicable(table, default, isWrite: false);
        var synonym = RecordSecurableRead(context, table, tableName);
        var tableQualifier = tableAlias ?? tableName.Leaf;
        var tableSource = new FromSource(
            qualifier: tableQualifier,
            columnNames: [.. table.Columns.Select(static column => column.Name)],
            columns: table.Columns,
            storedSchema: table.StoredColumns,
            storageOrdinals: table.StorageOrdinals,
            lobStore: table.Heap,
            rows: heapPlan.NoLockReader ? new UnlockedScanRows(table) : new LockCheckedScanRows(table, heapPlan),
            backingTable: table,
            heapPlan: heapPlan,
            viaSynonym: synonym,
            autoElementName: tableAlias ?? tableName.ToString(),
            writtenObjectName: tableName.ToString(),
            unaliasedName: tableAlias is null ? FromSource.Resolved(tableName, context.Batch.CurrentDatabase) : null);

        var keyJoin = BooleanExpression.Equality(
            resultAlias is null ? new Reference(keyName) : new Reference(resultAlias, keyName),
            new Reference(tableQualifier, table.Columns[keyOrdinal].Name));
        return new VectorSearchSources(distance, tableSource, keyJoin);
    }

    /// <summary>
    /// Requires <c>, name =</c> for the argument <paramref name="name"/>, the
    /// cursor on the token before the comma and left on the <c>=</c>.
    /// </summary>
    private static void ExpectVectorSearchArgument(ParserContext context, string name)
    {
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // COLUMN is a reserved word, the others ordinary names.
        if (context.GetNextRequired() is not (Name or ReservedKeyword) || !BuiltInToken.Equals(context.Token.Source.ToString(), name))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Reads a <c>SIMILAR_TO</c> or <c>TOP_N</c> operand at the cursor: a
    /// variable, a column name, or (for <c>SIMILAR_TO</c>) a literal, nothing
    /// around it — real's grammar takes no expression there, so the token
    /// after the operand must end the argument.
    /// </summary>
    private static Expression ParseVectorSearchOperand(ParserContext context, bool acceptLiterals)
    {
        var start = context.SaveCheckpoint();
        switch (context.Token)
        {
            case AtPrefixedString:
                context.MoveNextRequired();
                break;
            case Name:
                context.MoveNextRequired();
                while (context.Token is Operator { Character: '.' })
                {
                    if (context.GetNextRequired() is not Name)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                }
                break;
            case Numeric:
            case Literal or ReservedKeyword { Keyword: Keyword.Null } when acceptLiterals:
                context.MoveNextRequired();
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.Token is not Operator { Character: ',' or ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.RestoreCheckpoint(start);
        return Expression.Parse(context);
    }

    /// <summary>
    /// The search: every row's distance to the query vector by the metric,
    /// computed exactly as <c>VECTOR_DISTANCE</c> does, the nearest
    /// <c>TOP_N</c> kept in distance order and ties in key order. A NULL
    /// query vector or a NULL-vector row contributes nothing; a NULL count is
    /// Msg 1014, and a query vector of another dimension count Msg 42204.
    /// </summary>
    private static IEnumerable<byte[]> EnumerateVectorSearchRows(
        Storage.HeapTable table,
        int vectorStorage,
        int keyStorage,
        int dimensions,
        bool vectorIsFloat16,
        int metricIndex,
        Expression similarTo,
        Expression topN,
        SqlType[] schema,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (name => throw SimulatedSqlException.InvalidColumnName(name));
        var runtime = new RuntimeContext(resolver, batch);
        var count = topN.Run(runtime);
        if (count.IsNull)
            throw SimulatedSqlException.TopClauseInvalidValue();
        var limit = count.CoerceTo(SqlType.BigInt).AsInt64;
        if (limit < 0)
            throw SimulatedSqlException.TopRowCountMustNotBeNegative();
        var query = similarTo.Run(runtime);
        if (query.IsNull || limit == 0)
            yield break;
        if (VectorSqlType.IsFloat16Bytes(query.AsVectorBytes) != vectorIsFloat16)
            throw SimulatedSqlException.VectorDistanceBaseTypesDiffer();
        var queryElements = VectorSqlType.Elements(query.AsVectorBytes);
        if (queryElements.Length != dimensions)
            throw SimulatedSqlException.VectorDimensionsMismatch(dimensions, queryElements.Length, 3);

        var candidates = new List<(float Distance, int Key)>();
        foreach (var bytes in table.Rows)
        {
            var vector = RowDecoder.DecodeColumn(table.StoredColumns, bytes, vectorStorage, table.Heap);
            if (vector.IsNull)
                continue;
            var distance = VectorDistance.Compute(metricIndex, VectorSqlType.Elements(vector.AsVectorBytes), queryElements);
            candidates.Add((distance, RowDecoder.DecodeColumn(table.StoredColumns, bytes, keyStorage, table.Heap).AsInt32));
        }
        candidates.Sort(static (left, right) => left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : left.Key.CompareTo(right.Key));

        var taken = 0L;
        foreach (var (distance, key) in candidates)
        {
            if (taken++ == limit)
                yield break;
            yield return RowEncoder.EncodeRow(schema, [SqlValue.FromDouble(distance), SqlValue.FromInt32(key)]);
        }
    }
}
