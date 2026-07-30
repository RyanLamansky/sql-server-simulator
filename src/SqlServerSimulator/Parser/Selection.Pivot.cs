using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// PIVOT / UNPIVOT table operators. Both attach as a postfix to a parsed
/// FROM source (<see cref="ApplyOptionalPivotUnpivot"/>) and produce a new
/// <see cref="FromSource"/> whose deferred <see cref="FromSource.LateralPlan"/>
/// computes the rotated rowset — so the enclosing query, JOIN driver, and
/// correlation plumbing treat a pivoted source exactly like a derived table.
/// </summary>
/// <remarks>
/// <para>
/// PIVOT desugars to grouped conditional aggregation: the grouping key is
/// every column of the inner source except the FOR column and the
/// aggregate's argument column (SQL Server's implicit-grouping rule — stray
/// columns split the groups), and each <c>IN</c> value becomes a projection
/// of <c>&lt;agg&gt;(CASE forCol WHEN value THEN argCol END)</c>. The plan is
/// built through <see cref="BuildSqlProjection"/>, so decimal promotion,
/// empty-group semantics (SUM→NULL, COUNT→0), and three-valued handling all
/// come from the shared aggregate path.
/// </para>
/// <para>
/// UNPIVOT is an unfold, not an aggregation: each inner row emits one output
/// row per <c>IN</c> column whose value is non-NULL, carrying the
/// passthrough columns, the value, and the source column's name. It's built
/// as a <see cref="Selection"/> with a custom row producer so it rides the
/// same <see cref="FromSource.LateralPlan"/> seam as PIVOT.
/// </para>
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// If the cursor sits on a <c>PIVOT</c> / <c>UNPIVOT</c> keyword, parses
    /// the operator (and its required alias) and returns the rotated source;
    /// otherwise returns <paramref name="source"/> unchanged. Loops so a
    /// chain (<c>... PIVOT (...) p UNPIVOT (...) u</c>) composes left to right.
    /// </summary>
    private static FromSource ApplyOptionalPivotUnpivot(
        ParserContext context, FromSource source, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        while (true)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Pivot }:
                    source = ParsePivot(context, source, outerTypeResolver);
                    break;
                case ReservedKeyword { Keyword: Keyword.Unpivot }:
                    source = ParseUnpivot(context, source);
                    break;
                default:
                    return source;
            }
        }
    }

    /// <summary>
    /// Parses <c>PIVOT ( agg(argCol) FOR forCol IN ([v1], [v2], ...) ) AS alias</c>
    /// entered with the cursor on the <c>PIVOT</c> keyword, and returns the
    /// desugared grouped-aggregation source. Leaves the cursor on the first
    /// token after the alias.
    /// </summary>
    private static FromSource ParsePivot(
        ParserContext context, FromSource source, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        ExpectOperator(context.GetNextRequired(), '(');

        context.MoveNextRequired();
        var aggregateName = (context.Token as Name)?.Value ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        var kind = MapPivotAggregate(aggregateName) ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        ExpectOperator(context.GetNextRequired(), '(');
        context.MoveNextRequired();
        // COUNT(*) reaches here with `*` (an Operator), so the Name cast fails
        // → Msg 102, matching SQL Server's rejection of COUNT(*) in PIVOT.
        var argColName = (context.Token as Name)?.Value ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        ExpectOperator(context.GetNextRequired(), ')');

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var forColName = (context.Token as Name)?.Value ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        var pivotValues = ParseInValueIdentifiers(context);

        ExpectOperator(context.GetNextRequired(), ')');
        var alias = ConsumeOptionalAlias(context)
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        // Resolve the FOR and argument columns against the inner source. The
        // CASE input (FOR column) isn't type-resolved by BuildSqlProjection,
        // so an unknown name would otherwise escape to a runtime failure;
        // validate both up front to surface Msg 207 at parse time.
        var (forSource, forCol) = FindSourceColumn([source], new MultiPartName(forColName));
        if (forSource == -1)
            throw SimulatedSqlException.InvalidColumnName(forColName);
        var forColType = source.Columns[forCol].Type;
        if (FindSourceColumn([source], new MultiPartName(argColName)).SourceIndex == -1)
            throw SimulatedSqlException.InvalidColumnName(argColName);

        // Grouping key = every inner column except the FOR column and the
        // aggregate argument column.
        var groupingRefs = new List<Expression>();
        foreach (var colName in source.ColumnNames)
        {
            if (BuiltInToken.Equals(colName, forColName) || BuiltInToken.Equals(colName, argColName))
                continue;
            groupingRefs.Add(new Reference(colName));
        }

        var projection = new List<Expression>(groupingRefs);
        var aggregates = new List<AggregateExpression>();
        var seenValues = new List<string>();
        foreach (var pivotValue in pivotValues)
        {
            foreach (var prior in seenValues)
            {
                if (BuiltInToken.Equals(prior, pivotValue))
                    throw SimulatedSqlException.ColumnSpecifiedMultipleTimes(pivotValue, alias);
            }
            seenValues.Add(pivotValue);

            var literal = new Value(SqlValue.FromNVarchar(pivotValue).CoerceTo(forColType));
            var when = CaseExpression.CreateSimple(
                new Reference(forColName), [literal], [new Reference(argColName)], elseBranch: null);
            var aggregate = AggregateExpression.CreatePivotAggregate(kind, when);
            aggregates.Add(aggregate);
            projection.Add(new NamedExpression(aggregate, pivotValue));
        }

        var fromClause = new FromClause();
        if (groupingRefs.Count > 0)
        {
            fromClause.GroupingSets.Add([.. groupingRefs]);
            fromClause.AllGroupingExpressions.AddRange(groupingRefs);
        }

        var plan = BuildSqlProjection(
            context.Batch, [source], joins: [], projection, fromClause,
            distinct: false, topExpression: null, topPercent: false, topWithTies: false, aggregates, windows: [],
            outerTypeResolver, isAssignmentOnly: false, intoTarget: null, context.ReadColumnSink);

        return WrapRotatedPlan(alias, plan);
    }

    /// <summary>
    /// Parses <c>UNPIVOT ( valueCol FOR nameCol IN (col1, col2, ...) ) AS alias</c>
    /// entered with the cursor on the <c>UNPIVOT</c> keyword, and returns the
    /// unfold source. Leaves the cursor on the first token after the alias.
    /// </summary>
    private static FromSource ParseUnpivot(ParserContext context, FromSource source)
    {
        ExpectOperator(context.GetNextRequired(), '(');
        context.MoveNextRequired();
        var valueColName = (context.Token as Name)?.Value ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var nameColName = (context.Token as Name)?.Value ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        var unpivotColumns = ParseInValueIdentifiers(context);

        ExpectOperator(context.GetNextRequired(), ')');
        var alias = ConsumeOptionalAlias(context)
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        // The unpivoted columns fold into one value column, so they must all
        // share a type (Msg 8167 otherwise — SQL Server doesn't promote, e.g.
        // int + bigint conflicts). The first column's type is the value type.
        SqlType? valueType = null;
        foreach (var col in unpivotColumns)
        {
            var (s, c) = FindSourceColumn([source], new MultiPartName(col));
            if (s == -1)
                throw SimulatedSqlException.InvalidColumnName(col);
            var colType = source.Columns[c].Type;
            if (valueType is null)
                valueType = colType;
            else if (!valueType.Equals(colType))
                throw SimulatedSqlException.UnpivotColumnTypeConflict(col);
        }

        // Passthrough columns = inner columns not folded by the IN list.
        var passthroughNames = new List<string>();
        foreach (var colName in source.ColumnNames)
        {
            var folded = false;
            foreach (var col in unpivotColumns)
            {
                if (BuiltInToken.Equals(colName, col))
                {
                    folded = true;
                    break;
                }
            }
            if (!folded)
                passthroughNames.Add(colName);
        }

        // Output shape: passthrough columns, then the value column, then the
        // name column (SQL Server's SELECT * order for UNPIVOT). The name
        // column carries source column names — nvarchar(128), like sysname.
        var nameColType = NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.CoercibleDefault);
        var columnNames = new string[passthroughNames.Count + 2];
        var schema = new SqlType[columnNames.Length];
        var passthroughTypes = new SqlType[passthroughNames.Count];
        for (var i = 0; i < passthroughNames.Count; i++)
        {
            var (s, c) = FindSourceColumn([source], new MultiPartName(passthroughNames[i]));
            passthroughTypes[i] = source.Columns[c].Type;
            columnNames[i] = passthroughNames[i];
            schema[i] = passthroughTypes[i];
        }
        columnNames[^2] = valueColName;
        schema[^2] = valueType!;
        columnNames[^1] = nameColName;
        schema[^1] = nameColType;

        var capturedPassthrough = passthroughNames.ToArray();
        var capturedUnpivot = unpivotColumns.ToArray();
        var capturedValueType = valueType!;
        var plan = new Selection(schema, columnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => UnpivotRows(
                source, capturedPassthrough, capturedUnpivot, schema, capturedValueType, nameColType, batch, outerResolver));

        return WrapRotatedPlan(alias, plan);
    }

    /// <summary>
    /// Streams the UNPIVOT unfold: one output row per (inner row × non-NULL
    /// IN column), carrying the passthrough values, the column value coerced
    /// to the shared value type, and the source column's name.
    /// </summary>
    private static IEnumerable<byte[]> UnpivotRows(
        FromSource source, string[] passthroughNames, string[] unpivotColumns,
        SqlType[] schema, SqlType valueType, NVarcharSqlType nameColType,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        FromSource[] sources = [source];
        var memo = new SourceColumnMemo();
        foreach (var tuple in EnumerateJoinedRows(sources, [], batch, outerResolver))
        {
            var localTuple = tuple;
            SqlValue Resolve(MultiPartName name) => ResolveAcrossTuple(sources, localTuple, name, batch, outerResolver, Resolve, memo);

            foreach (var col in unpivotColumns)
            {
                var value = Resolve(new MultiPartName(col));
                if (value.IsNull)
                    continue;

                var values = new SqlValue[schema.Length];
                for (var i = 0; i < passthroughNames.Length; i++)
                    values[i] = Resolve(new MultiPartName(passthroughNames[i]));
                values[^2] = value.CoerceTo(valueType);
                values[^1] = SqlValue.FromNVarchar(nameColType, col);
                yield return RowEncoder.EncodeRow(schema, values);
            }
        }
    }

    /// <summary>
    /// Parses the <c>IN ( id [, id]... )</c> identifier list shared by PIVOT
    /// and UNPIVOT. Entered with the cursor before the <c>IN</c> keyword;
    /// leaves it on the closing <c>)</c> of the list. Entries must be
    /// identifiers (<c>[2020]</c> / bare names) — string / numeric literals
    /// raise Msg 102, matching SQL Server.
    /// </summary>
    private static List<string> ParseInValueIdentifiers(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.In })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        ExpectOperator(context.GetNextRequired(), '(');

        var values = new List<string>();
        while (true)
        {
            context.MoveNextRequired();
            values.Add((context.Token as Name)?.Value ?? throw SimulatedSqlException.SyntaxErrorNear(context));
            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    continue;
                case Operator { Character: ')' }:
                    return values;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
    }

    /// <summary>
    /// Wraps a rotated <see cref="Selection"/> as a deferred FROM source,
    /// mirroring the derived-table construction (schema-only HeapColumns, no
    /// LOB store, rows deferred to <see cref="FromSource.LateralPlan"/>).
    /// </summary>
    private static FromSource WrapRotatedPlan(string alias, Selection plan)
    {
        var columns = new HeapColumn[plan.Schema.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = new HeapColumn(string.Empty, plan.Schema[i], maxLength: null, nullable: true);

        return new FromSource(
            qualifier: alias,
            columnNames: plan.ColumnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
    }

    private static void ExpectOperator(Token? token, char character)
    {
        if (token is not Operator op || op.Character != character)
            throw SimulatedSqlException.SyntaxErrorNear(token);
    }

    /// <summary>
    /// Maps a PIVOT aggregate function name to its <see cref="AggregateKind"/>,
    /// or null when the name isn't a single-argument aggregate the simulator
    /// models (STRING_AGG needs a separator, so it has no PIVOT form).
    /// </summary>
    private static AggregateKind? MapPivotAggregate(string name)
    {
        Span<char> upper = stackalloc char[name.Length];
        var length = name.AsSpan().ToUpperInvariant(upper);
        return upper[..length] switch
        {
            "APPROX_COUNT_DISTINCT" => AggregateKind.ApproxCountDistinct,
            "AVG" => AggregateKind.Avg,
            "CHECKSUM_AGG" => AggregateKind.ChecksumAgg,
            "COUNT" => AggregateKind.Count,
            "COUNT_BIG" => AggregateKind.CountBig,
            "MAX" => AggregateKind.Max,
            "MIN" => AggregateKind.Min,
            "STDEV" => AggregateKind.Stdev,
            "STDEVP" => AggregateKind.StdevP,
            "SUM" => AggregateKind.Sum,
            "VAR" => AggregateKind.Var,
            "VARP" => AggregateKind.VarP,
            _ => null,
        };
    }
}
