using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

internal sealed class Selection
{
    internal readonly SimulatedResultSet Results;

    private Selection(SimulatedResultSet results) => this.Results = results;

    public static Selection Parse(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue, uint depth)
    {
        token = tokens.RequireNext();

        List<Expression> expressions = [];

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
                        [[.. expressions.Select(x => x.Run(column => throw SimulatedSqlException.InvalidColumnName(column)))]]
                        ));

                case UnquotedString unquotedString:
                    if (!unquotedString.TryParse(out var keyword) || keyword != Keyword.From)
                        throw new NotSupportedException("Simulated selection processor expected a `from`.");

                    switch (token = tokens.RequireNext())
                    {
                        case StringToken tableName:
                            if (!simulation.Tables.TryGetValue(tableName.Value, out var table) && !simulation.SystemTables.Value.TryGetValue(tableName.Value, out table))
                                throw SimulatedSqlException.InvalidObjectName(tableName);

                            if (tokens.TryMoveNext(out token))
                            {
                                if (token is UnquotedString maybeAs && maybeAs.Parse() == Keyword.As)
                                {
                                    if (token is not UnquotedString)
                                        break;
                                }
                                else
                                {
                                    break;
                                }
                            }

                            return new(new(
                                expressions,
                                table.Rows.Select<object?[], object?[]>(row => [..expressions.Select(x => x.Run(columnName =>
                                {
                                    var columnIndex = table.Columns.FindIndex(column => Collation.Default.Equals(column.Name, columnName.Last()));
                                    return columnIndex == -1 ? throw SimulatedSqlException.InvalidColumnName(columnName) : row[columnIndex];
                                }))])
                                ));

                        case OpenParentheses:
                            if ((token = tokens.RequireNext()) is not UnquotedString maybeSelect || maybeSelect.Parse() != Keyword.Select)
                                throw SimulatedSqlException.SyntaxErrorNear(token);

                            {
                                var derived = Selection.Parse(simulation, tokens, ref token, getVariableValue, depth + 1).Results;

                                if ((token = tokens.RequireNext()) is UnquotedString maybeAs && maybeAs.Parse() == Keyword.As)
                                {
                                    if (token is not UnquotedString)
                                        break;
                                }
                                else
                                {
                                    break;
                                }

                                return new(new(
                                    expressions,
                                    derived.Select<object?[], object?[]>(row => [..expressions.Select(x => x.Run(columnName =>
                                    {
                                        var columnIndex = Array.FindIndex(derived.columnNames, name => Collation.Default.Equals(name, columnName.Last()));
                                        return columnIndex == -1 ? throw SimulatedSqlException.InvalidColumnName(columnName) : row[columnIndex];
                                    }))])
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
