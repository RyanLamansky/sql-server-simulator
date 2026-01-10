using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Manages the higher-level logic to convert a sequence of command tokens into tabular results.
/// </summary>
internal sealed class Selection
{
    internal readonly SimulatedResultSet Results;

    private Selection(SimulatedResultSet results) => this.Results = results;

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
        context.MoveNextRequired();

        int? topCount = null;

        if (context.Token is ReservedKeyword { Keyword: Keyword.Top })
        {
            context.MoveNextRequired();

            var resolvedExpression = Expression.Parse(context).Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            topCount = resolvedExpression.Value is int unboxed ? unboxed : throw SimulatedSqlException.TopFetchRequiresInteger();
        }

        List<Expression> expressions = [];
        List<BooleanExpression> excluders = [];

        IEnumerable<DataValue[]> ApplyClauses(IEnumerable<DataValue[]> records, Func<DataValue[], List<string>, DataValue> getColumnValueFromRow)
        {
            records = records.Where(row =>
            {
                foreach (var excluder in excluders)
                {
                    if (!excluder.Run(columnName => getColumnValueFromRow(row, columnName)))
                        return false;
                }

                return true;
            }).Select<DataValue[], DataValue[]>(row =>
                [.. expressions.Select(x => x.Run(columnName => getColumnValueFromRow(row, columnName)))]
            );

            if (topCount is not null)
                records = records.Take(topCount.GetValueOrDefault());

            return records;
        }

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
                    expressions[^1] = Expression.AssignName(expressions.Last(), name);
                    continue;

                case ReservedKeyword { Keyword: Keyword.As }:
                    expressions[^1] = Expression.AssignName(expressions.Last(), context.GetNextRequired<Name>());
                    continue;

                case ReservedKeyword { Keyword: Keyword.From }:
                    switch (context.GetNextRequired())
                    {
                        case Name tableName:
                            if (!context.Simulation.Tables.TryGetValue(tableName.Value, out var table) && !context.Simulation.SystemTables.Value.TryGetValue(tableName.Value, out table))
                                throw SimulatedSqlException.InvalidObjectName(tableName);

                            if (context.GetNextOptional() is not null)
                            {
                                if (context.Token is ReservedKeyword { Keyword: Keyword.As })
                                {
                                    if (context.Token is not ReservedKeyword)
                                        break;
                                }
                                else
                                {
                                    break;
                                }
                            }

                            return new(new(
                                expressions,
                                ApplyClauses(table.Rows, (row, columnName) =>
                                {
                                    var columnIndex = table.Columns.FindIndex(column => Collation.Default.Equals(column.Name, columnName.Last()));
                                    return columnIndex == -1 ? throw SimulatedSqlException.InvalidColumnName(columnName) : row[columnIndex];
                                })
                                ));

                        case Operator { Character: '(' }:
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                                throw SimulatedSqlException.SyntaxErrorNear(context);

                            {
                                var derived = Selection.Parse(context, depth + 1).Results;

                                if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.As })
                                {
                                    if (context.Token is not ReservedKeyword)
                                        break;
                                }
                                else
                                {
                                    break;
                                }

                                return new(new(
                                    expressions,
                                    ApplyClauses(derived.records, (row, columnName) =>
                                    {
                                        var columnIndex = Array.FindIndex(derived.columnNames, name => Collation.Default.Equals(name, columnName.Last()));
                                        return columnIndex == -1 ? throw SimulatedSqlException.InvalidColumnName(columnName) : row[columnIndex];
                                    })
                                    ));
                            }
                    }

                    throw SimulatedSqlException.SyntaxErrorNear(context);

                case ReservedKeyword { Keyword: Keyword.Where }:
                    context.MoveNextRequired();
                    excluders.Add(BooleanExpression.Parse(context));
                    continue;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        return new(new(
            expressions,
            ApplyClauses([[.. expressions.Select(x => x.Run(column => throw SimulatedSqlException.InvalidColumnName(column)))]], (row, columnName) => throw new NotSupportedException())
            ));
    }
}
