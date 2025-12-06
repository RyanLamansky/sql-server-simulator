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
        context.Token = context.RequireNext();

        int? topCount = null;

        if (context.Token is ReservedKeyword { Keyword: Keyword.Top })
        {
            // SQL Server doesn't require outer parentheses.
            // When Expression.Parse supports them, the checks for them here should be removed.
            context.Token = context.RequireNext<OpenParentheses>();
            context.Token = context.RequireNext();

            var resolvedExpression = Expression.Parse(context).Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            topCount = resolvedExpression is int unboxed ? unboxed : throw SimulatedSqlException.TopFetchRequiresInteger();

            if (context.Token is not null and not CloseParentheses)
                throw SimulatedSqlException.SyntaxErrorNear(context.Token);

            context.Token = context.RequireNext();
        }

        List<Expression> expressions = [];

        IEnumerable<object?[]> ApplyClauses(IEnumerable<object?[]> records)
        {
            if (topCount is not null)
                records = records.Take(topCount.GetValueOrDefault());

            return records;
        }

        do
        {
            expressions.Add(Expression.Parse(context));

            switch (context.Token)
            {
                case Comma:
                    continue;

                case CloseParentheses:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context.Token);

                    goto case null;

                case null: // "Select" with no "From".
                    return new(new(
                        expressions,
                        ApplyClauses([[.. expressions.Select(x => x.Run(column => throw SimulatedSqlException.InvalidColumnName(column)))]])
                        ));

                case ReservedKeyword expectFrom:
                    if (expectFrom.Keyword != Keyword.From)
                        throw new NotSupportedException("Simulated selection processor expected a `from`.");

                    switch (context.Token = context.RequireNext())
                    {
                        case StringToken tableName:
                            if (!context.Simulation.Tables.TryGetValue(tableName.Value, out var table) && !context.Simulation.SystemTables.Value.TryGetValue(tableName.Value, out table))
                                throw SimulatedSqlException.InvalidObjectName(tableName);

                            if (context.TryMoveNext(out context.Token))
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
                                ApplyClauses(table.Rows.Select<object?[], object?[]>(row => [..expressions.Select(x => x.Run(columnName =>
                                {
                                    var columnIndex = table.Columns.FindIndex(column => Collation.Default.Equals(column.Name, columnName.Last()));
                                    return columnIndex == -1 ? throw SimulatedSqlException.InvalidColumnName(columnName) : row[columnIndex];
                                }))]))
                                ));

                        case OpenParentheses:
                            if ((context.Token = context.RequireNext()) is not ReservedKeyword { Keyword: Keyword.Select })
                                throw SimulatedSqlException.SyntaxErrorNear(context.Token);

                            {
                                var derived = Selection.Parse(context, depth + 1).Results;

                                if ((context.Token = context.RequireNext()) is ReservedKeyword { Keyword: Keyword.As })
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
                                    ApplyClauses(derived.Select<object?[], object?[]>(row => [..expressions.Select(x => x.Run(columnName =>
                                    {
                                        var columnIndex = Array.FindIndex(derived.columnNames, name => Collation.Default.Equals(name, columnName.Last()));
                                        return columnIndex == -1 ? throw SimulatedSqlException.InvalidColumnName(columnName) : row[columnIndex];
                                    }))]))
                                    ));
                            }
                    }

                    throw new NotSupportedException($"Simulated selection processor expected a source table, found {context.Token}.");
            }

            throw new NotSupportedException($"Simulated selection processor doesn't know what to do with {context.Token}.");
        } while (context.TryMoveNext(out context.Token));

        throw new NotSupportedException($"Simulated selection reached the end of the command before expected.");
    }
}
