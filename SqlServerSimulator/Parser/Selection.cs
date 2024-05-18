namespace SqlServerSimulator.Parser;

using Tokens;

internal sealed class Selection
{
    internal SimulatedResultSet Results;

    private Selection(SimulatedResultSet results)
    {
        this.Results = results;
    }


    public static Selection Parse(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
    {
        token = tokens.RequireNext();

        var expression = Expression.Parse(simulation, tokens, ref token, getVariableValue);

        if (token is not UnquotedString unquoted || unquoted.Parse() != Keyword.From)
        {
            return new Selection(new SimulatedResultSet(
                new Dictionary<string, int> { { expression.Name, 0 } },
                new object?[] { expression.Run(column => throw new SimulatedSqlException($"Invalid column name '{column}'.", 207, 16, 1)) }
                ));
        }

        token = tokens.RequireNext();

        switch (token)
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

                var columnIndexes = new Dictionary<string, int>();

                return new Selection(new SimulatedResultSet(
                    new Dictionary<string, int> { { expression.Name, 0 } },
                    table.Rows.Select(row =>
                    {
                        return new object?[] {
                    expression.Run(columnName =>
                    {
                        var columnIndex = table.Columns.FindIndex(column => Collation.Default.Equals(column.Name, columnName.Last()));
                        if (columnIndex == -1)
                            throw new SimulatedSqlException($"Invalid column name '{columnName}'.", 207, 16, 1);

                        return row[columnIndex];
                    })
                        };
                    })));
        }

        throw new NotSupportedException($"Simulated command processor doesn't know what to do with {token}.");
    }
}
