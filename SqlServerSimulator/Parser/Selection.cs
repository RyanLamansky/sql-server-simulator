namespace SqlServerSimulator.Parser;

using Tokens;

internal sealed class Selection
{
    internal readonly SimulatedResultSet Results;

    private Selection(SimulatedResultSet results) => this.Results = results;

    public static Selection Parse(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
    {
        token = tokens.RequireNext();

        Dictionary<string, int> columnIndexes = [];
        List<Expression> expressions = [];

        do
        {
            var expression = Expression.Parse(simulation, tokens, ref token, getVariableValue);
            if (expression.Name.Length > 0)
                columnIndexes.Add(expression.Name, columnIndexes.Count);
            expressions.Add(expression);

            switch (token)
            {
                case Comma:
                    continue;

                case null: // "Select" with no "From".
                    return new(new(
                        columnIndexes,
                        [[.. expressions.Select(x => x.Run(column => throw new SimulatedSqlException($"Invalid column name '{column}'.", 207, 16, 1)))]]
                        ));

                case UnquotedString unquotedString:
                    if (!unquotedString.TryParse(out var keyword) || keyword != Keyword.From)
                        throw new NotSupportedException("Simulated selection processor expected a `from`.");

                    switch (token = tokens.RequireNext())
                    {
                        case StringToken tableName:
                            if (!simulation.Tables.TryGetValue(tableName.Value, out var table) && !simulation.SystemTables.Value.TryGetValue(tableName.Value, out table))
                                throw new SimulatedSqlException($"Invalid object name {tableName}.", 208, 16, 1);

                            if (tokens.TryMoveNext(out token))
                            {
                                if (token is UnquotedString maybeAs && maybeAs.Parse() == Keyword.As)
                                {
                                    if (token is not UnquotedString)
                                        break;
                                }
                                else
                                    break;
                            }

                            return new(new(
                                columnIndexes,
                                table.Rows.Select<object?[], object?[]>(row => [..expressions.Select(x => x.Run(columnName =>
                                {
                                    var columnIndex = table.Columns.FindIndex(column => Collation.Default.Equals(column.Name, columnName.Last()));
                                    if (columnIndex == -1)
                                        throw new SimulatedSqlException($"Invalid column name '{columnName}'.", 207, 16, 1);
                                    
                                    return row[columnIndex];
                                }))])));
                    }

                    throw new NotSupportedException($"Simulated selection processor expected a source table, found {token}.");
            }

            throw new NotSupportedException($"Simulated selection processor doesn't know what to do with {token}.");
        } while (tokens.TryMoveNext(out token));

        throw new NotSupportedException($"Simulated selection reached the end of the command before expected.");
    }
}
