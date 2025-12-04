using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

internal sealed class Selection
{
    internal readonly SimulatedResultSet Results;

    private Selection(SimulatedResultSet results) => this.Results = results;

    public static Selection Parse(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue, uint depth)
    {
        token = tokens.RequireNext();

        int? topCount = null;

        if (token is ReservedKeyword { Keyword: Keyword.Top })
        {
            // SQL Server doesn't require outer parentheses.
            // When Expression.Parse supports them, the checks for them here should be removed.
            token = tokens.RequireNext<OpenParentheses>();
            token = tokens.RequireNext();

            var resolvedExpression = Expression.Parse(simulation, tokens, ref token, getVariableValue).Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            topCount = resolvedExpression is int unboxed ? unboxed : throw SimulatedSqlException.TopFetchRequiresInteger();

            if (token is not null and not CloseParentheses)
                throw SimulatedSqlException.SyntaxErrorNear(token);

            token = tokens.RequireNext();
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
            expressions.Add(Expression.Parse(simulation, tokens, ref token, getVariableValue));

            switch (token)
            {
                case Comma:
                    continue;

                case CloseParentheses:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(token);

                    goto case null;

                case null: // "Select" with no "From".
                    return new(new(
                        expressions,
                        ApplyClauses([[.. expressions.Select(x => x.Run(column => throw SimulatedSqlException.InvalidColumnName(column)))]])
                        ));

                case ReservedKeyword expectFrom:
                    if (expectFrom.Keyword != Keyword.From)
                        throw new NotSupportedException("Simulated selection processor expected a `from`.");

                    switch (token = tokens.RequireNext())
                    {
                        case StringToken tableName:
                            if (!simulation.Tables.TryGetValue(tableName.Value, out var table) && !simulation.SystemTables.Value.TryGetValue(tableName.Value, out table))
                                throw SimulatedSqlException.InvalidObjectName(tableName);

                            if (tokens.TryMoveNext(out token))
                            {
                                if (token is ReservedKeyword { Keyword: Keyword.As })
                                {
                                    if (token is not ReservedKeyword)
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
                            if ((token = tokens.RequireNext()) is not ReservedKeyword { Keyword: Keyword.Select })
                                throw SimulatedSqlException.SyntaxErrorNear(token);

                            {
                                var derived = Selection.Parse(simulation, tokens, ref token, getVariableValue, depth + 1).Results;

                                if ((token = tokens.RequireNext()) is ReservedKeyword { Keyword: Keyword.As })
                                {
                                    if (token is not ReservedKeyword)
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

                    throw new NotSupportedException($"Simulated selection processor expected a source table, found {token}.");
            }

            throw new NotSupportedException($"Simulated selection processor doesn't know what to do with {token}.");
        } while (tokens.TryMoveNext(out token));

        throw new NotSupportedException($"Simulated selection reached the end of the command before expected.");
    }
}
