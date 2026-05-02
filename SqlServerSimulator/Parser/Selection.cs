using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Manages the higher-level logic to convert a sequence of command tokens into tabular results.
/// </summary>
internal sealed class Selection
{
    internal readonly SimulatedQueryResult Results;

    private Selection(SimulatedQueryResult results) => this.Results = results;

    /// <summary>
    /// Creates a <see cref="Selection"/> from a series of tokens.
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <param name="depth">The current depth of recursed selection, such as with derived tables. 0 for the top-level SELECT.</param>
    /// <returns>The prepared command.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Selection Parse(ParserContext context, uint depth)
    {
        int? topCount = null;

        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Top })
        {
            var resolved = Expression
                .Parse(context.MoveNextRequiredReturnSelf())
                .Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            topCount = !resolved.IsNull && resolved.Type == SqlType.Int32
                ? resolved.AsInt32
                : throw SimulatedSqlException.TopFetchRequiresInteger();
        }

        List<Expression> expressions = [];
        List<BooleanExpression> excluders = [];

        do
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.From }:
                    break;

                case ReservedKeyword { Keyword: not Keyword.Null } keyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);

                case Operator { Character: ',' }:
                    if (expressions.Count == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    continue;
                case Operator { Character: ')' }:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                default:
                    expressions.Add(Expression.Parse(context));
                    break;
            }

            switch (context.Token)
            {
                case null:
                    goto ExitWhileTokenLoop;

                case Operator { Character: ',' }:
                    continue;

                case Name name:
                    expressions[^1] = Expression.AssignName(expressions[^1], name);
                    continue;

                case ReservedKeyword { Keyword: Keyword.As }:
                    expressions[^1] = Expression.AssignName(expressions[^1], context.GetNextRequired<Name>());
                    continue;

                case ReservedKeyword { Keyword: Keyword.From }:
                    switch (context.GetNextRequired())
                    {
                        case Name tableName:
                            if (!context.Simulation.HeapTables.TryGetValue(tableName.Value, out var heapTable)
                                && !Simulation.SystemHeapTables.TryGetValue(tableName.Value, out heapTable))
                            {
                                throw SimulatedSqlException.InvalidObjectName(tableName);
                            }

                            ConsumeOptionalAliasAndWhere(context, excluders);

                            var heapColumnNames = new string[heapTable.Columns.Length];
                            for (var ci = 0; ci < heapColumnNames.Length; ci++)
                                heapColumnNames[ci] = heapTable.Columns[ci].Name;

                            return new(BuildSqlProjection(heapColumnNames, heapTable.Schema, heapTable.Rows, expressions, excluders, topCount));

                        case Operator { Character: '(' }:
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                                throw SimulatedSqlException.SyntaxErrorNear(context);

                            if (Selection.Parse(context, depth + 1).Results is not SimulatedSqlResultSet derived)
                                throw new InvalidOperationException("Inner SELECT produced a non-Pages result set; this should be unreachable.");

                            ConsumeOptionalAliasAndWhere(context, excluders);

                            return new(BuildSqlProjection(derived.ColumnNames, derived.Schema, derived.RowBytes, expressions, excluders, topCount));
                    }

                    throw SimulatedSqlException.SyntaxErrorNear(context);

                case ReservedKeyword { Keyword: Keyword.Where }:
                    excluders.Add(BooleanExpression.Parse(Expression.Parse(context.MoveNextRequiredReturnSelf()), context));
                    continue;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        return new(BuildSynthesizedSqlRow(expressions, excluders, topCount));
    }

    /// <summary>
    /// After a FROM source (table name or derived table), parses an optional
    /// AS alias (the alias name is discarded — column resolution today is by
    /// last-component match, so the alias is informational), then any number
    /// of WHERE clauses.
    /// </summary>
    private static void ConsumeOptionalAliasAndWhere(ParserContext context, List<BooleanExpression> excluders)
    {
        var nextToken = context.GetNextOptional();
        if (nextToken is ReservedKeyword { Keyword: Keyword.As })
        {
            _ = context.GetNextRequired<Name>();
            nextToken = context.GetNextOptional();
        }

        while (nextToken is ReservedKeyword { Keyword: Keyword.Where })
        {
            excluders.Add(BooleanExpression.Parse(Expression.Parse(context.MoveNextRequiredReturnSelf()), context));
            nextToken = context.GetNextOptional();
        }
    }

    /// <summary>
    /// Builds the result for a tableless SELECT (synthesized constant-row
    /// branch). The row is encoded into the page-row format and the bytes are
    /// handed to the result set; decoding happens lazily per column when the
    /// reader navigates it. Any <paramref name="excluders"/> are evaluated
    /// against the synthesized row; if any returns false the result is empty.
    /// <paramref name="topCount"/>, if set to zero, also suppresses the row.
    /// </summary>
    private static SimulatedSqlResultSet BuildSynthesizedSqlRow(List<Expression> expressions, List<BooleanExpression> excluders, int? topCount)
    {
        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        for (var i = 0; i < expressions.Count; i++)
        {
            values[i] = expressions[i].Run(column => throw SimulatedSqlException.InvalidColumnName(column));
            schema[i] = values[i].Type;
            columnNames[i] = expressions[i].Name;
        }

        if (topCount == 0)
            return new SimulatedSqlResultSet(schema, columnNames, []);

        foreach (var excluder in excluders)
        {
            if (!excluder.Run(column => throw SimulatedSqlException.InvalidColumnName(column)))
                return new SimulatedSqlResultSet(schema, columnNames, []);
        }

        return new SimulatedSqlResultSet(schema, columnNames, [RowEncoder.EncodeRow(schema, values)]);
    }

    /// <summary>
    /// Builds the result for a SELECT-FROM-source query (a heap table or a
    /// derived table). Each input row is decoded column-by-column on demand
    /// via <see cref="RowDecoder.DecodeColumn"/>; each projection expression
    /// is evaluated against that row through <see cref="Expression.Run"/>;
    /// the resulting values are re-encoded into a fresh output row.
    /// </summary>
    /// <remarks>
    /// Output schema is determined statically from the projection list using
    /// <see cref="Expression.GetSqlType"/>. Constants, references, and
    /// composite expressions all participate, as long as they have a static
    /// type-of resolution.
    /// </remarks>
    private static SimulatedSqlResultSet BuildSqlProjection(
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        IEnumerable<byte[]> sourceRows,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        int? topCount)
    {
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        int FindSourceColumn(List<string> name)
        {
            for (var j = 0; j < sourceColumnNames.Length; j++)
            {
                if (Collation.Default.Equals(sourceColumnNames[j], name[^1]))
                    return j;
            }
            return -1;
        }

        SqlType ResolveColumnType(List<string> name)
        {
            var idx = FindSourceColumn(name);
            return idx == -1 ? throw SimulatedSqlException.InvalidColumnName(name) : sourceSchema[idx];
        }

        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].GetSqlType(ResolveColumnType);
            outputColumnNames[i] = expressions[i].Name;
        }

        return new SimulatedSqlResultSet(outputSchema, outputColumnNames, ProjectSqlRows(sourceSchema, sourceRows, FindSourceColumn, expressions, excluders, outputSchema, topCount));
    }

    private static IEnumerable<byte[]> ProjectSqlRows(
        SqlType[] sourceSchema,
        IEnumerable<byte[]> sourceRows,
        Func<List<string>, int> findSourceColumn,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        int? topCount)
    {
        var remaining = topCount;
        foreach (var rowBytes in sourceRows)
        {
            if (remaining == 0)
                yield break;

            var bytes = rowBytes;

            SqlValue ResolveColumn(List<string> name)
            {
                var columnIndex = findSourceColumn(name);
                return columnIndex == -1
                    ? throw SimulatedSqlException.InvalidColumnName(name)
                    : RowDecoder.DecodeColumn(sourceSchema, bytes, columnIndex);
            }

            var include = true;
            foreach (var excluder in excluders)
            {
                if (!excluder.Run(ResolveColumn))
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(ResolveColumn);

            yield return RowEncoder.EncodeRow(outputSchema, projected);

            if (remaining is not null)
                remaining--;
        }
    }
}
