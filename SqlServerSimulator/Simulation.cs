using System.Collections.Concurrent;
using System.Data.Common;

namespace SqlServerSimulator;

using Parser;
using Parser.Tokens;
using Schema;

/// <summary>
/// Defines and controls the simulated scenario.
/// </summary>
public sealed class Simulation
{
    /// <summary>
    /// Creates a new <see cref="Simulation"/> instance with default behaviors.
    /// </summary>
    public Simulation()
    {
    }

    /// <summary>
    /// The value for the `@@VERSION` system-defined value, defaults to "SQL Server Simulator".
    /// </summary>
    public string Version { get; set; } = "SQL Server Simulator";

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    private readonly ConcurrentDictionary<string, Table> tables = new(Collation.Default);

    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var variables = command
            .Parameters
            .Cast<DbParameter>()
            .Select(parameter =>
            {
                var name = parameter.ParameterName;
                var type = DataType.GetByDbType(parameter.DbType);
                return (Name: name.StartsWith("@") ? name[1..] : name, TypeValue: (DataType: type, Value: parameter.Value is null ? null : type.ConvertFrom(parameter.Value)));
            })
            .ToDictionary(tuple => tuple.Name, StringComparer.InvariantCultureIgnoreCase);

        object? ValidatingGetVariableValue(string name)
        {
            if (variables.TryGetValue(name, out var value))
                return value.TypeValue.Value;

            throw new SimulatedSqlException($"Must declare the scalar variable \"@{name}\".");
        };

        using var tokens = Tokenizer.Tokenize(command.CommandText).GetEnumerator();

        while (tokens.TryMoveNext(out var token))
        {
            switch (token)
            {
                case Comment:
                    continue;

                case StatementTerminator:
                    continue;

                case UnquotedString unquotedString:
                    switch (unquotedString.Parse())
                    {
                        case Keyword.Set:
                            switch (token = tokens.RequireNext())
                            {
                                case UnquotedString setTarget:
                                    switch (setTarget.Parse())
                                    {
                                        case Keyword.NoCount:
                                            switch (token = tokens.RequireNext())
                                            {
                                                case UnquotedString noCountMode:
                                                    switch (noCountMode.Parse())
                                                    {
                                                        case Keyword.On:
                                                        case Keyword.Off:
                                                            continue;
                                                    }
                                                    break;
                                            }
                                            break;
                                    }
                                    break;
                            }
                            break;

                        case Keyword.Create:
                            switch (token = tokens.RequireNext())
                            {
                                case UnquotedString whatToCreate:
                                    switch (whatToCreate.Parse())
                                    {
                                        case Keyword.Table:
                                            if (tokens.RequireNext() is not Name tableName)
                                                break;

                                            if ((token = tokens.RequireNext()) is not OpenParentheses)
                                                break;

                                            var table = new Table(tableName.Value);

                                            var columns = table.Columns;
                                            bool dontAdvanceToken;
                                            do
                                            {
                                                dontAdvanceToken = false;
                                                if (tokens.RequireNext() is not Name columnName)
                                                    throw new SimulatedSqlException("Simulated table creation requires named columns.");

                                                if (tokens.RequireNext() is not Name type)
                                                    throw new SimulatedSqlException("Simulated table creation requires columns to have a type.");

                                                var nullable = true;

                                                token = tokens.RequireNext();
                                                if (token is UnquotedString next)
                                                {
                                                    switch (next.Parse())
                                                    {
                                                        case Keyword.Not:
                                                            if ((token = tokens.RequireNext()) is not UnquotedString mustBeNull || mustBeNull.Parse() != Keyword.Null)
                                                                throw new NotSupportedException($"Simulated command processor doesn't know how to handle column definition token {token}.");

                                                            nullable = false;
                                                            break;
                                                        case Keyword.Null:
                                                            nullable = true;
                                                            break;
                                                        default:
                                                            throw new NotSupportedException($"Simulated command processor doesn't know how handle column definition token {token}.");
                                                    }
                                                }
                                                else
                                                {
                                                    dontAdvanceToken = true;
                                                    nullable = true;
                                                }

                                                columns.Add(new Column(columnName.Value, DataType.GetByName(type), nullable));
                                            } while ((dontAdvanceToken ? token : token = tokens.RequireNext()) is Comma);

                                            if (token is not CloseParentheses)
                                                break;

                                            if (!this.tables.TryAdd(table.Name, table))
                                                // TODO Msg 2714, Level 16, State 6, Line x
                                                throw new SimulatedSqlException($"There is already an object named '{table.Name}' in the database.");

                                            continue;
                                    }
                                    break;
                            }
                            break;

                        case Keyword.Select:
                            switch (token = tokens.RequireNext())
                            {
                                case DoubleAtPrefixedString selected:
                                    switch (selected.Parse())
                                    {
                                        case AtAtKeyword.Version:
                                            if (tokens.TryMoveNext(out token))
                                                break;

                                            yield return new SimulatedResultSet(new Dictionary<string, int>(), new object[] { this.Version });
                                            continue;
                                    }
                                    break;
                                case AtPrefixedString atPrefixed:
                                    yield return new SimulatedResultSet(new Dictionary<string, int>(), new object?[] { ValidatingGetVariableValue(atPrefixed.Value) });
                                    continue;
                                case Numeric selected:
                                    yield return new SimulatedResultSet(new Dictionary<string, int>(), new object[] { selected.Value });
                                    continue;
                            }
                            break;

                        case Keyword.Insert:
                            if ((token = tokens.RequireNext()) is UnquotedString maybeInto && maybeInto.TryParse(out var keyword) && keyword == Keyword.Into)
                                token = tokens.RequireNext();

                            if (token is not StringToken desinationTableToken)
                                break;

                            if (!this.tables.TryGetValue(desinationTableToken.Value, out var desinationTable))
                                // TODO Msg 208, Level 16, State 0, Line x
                                throw new SimulatedSqlException($"Invalid object name '{desinationTableToken.Value}'.");

                            Column[] destinationColumns;
                            if ((token = tokens.RequireNext()) is OpenParentheses)
                            {
                                var usedColumns = new List<Column>();
                                while ((token = tokens.RequireNext()) is StringToken column)
                                {
                                    var columnName = column.Value;
                                    var tableColumn = desinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName));
                                    if (tableColumn is null)
                                        // TODO Msg 207, Level 16, State 1, Line x
                                        throw new SimulatedSqlException($"Invalid column name '{columnName}'.");

                                    usedColumns.Add(tableColumn);
                                }

                                if (token is not CloseParentheses)
                                    break;

                                destinationColumns = usedColumns.ToArray();

                                token = tokens.RequireNext();
                            }
                            else
                            {
                                destinationColumns = desinationTable.Columns.ToArray();
                            }

                            if (token is not UnquotedString expectValues || expectValues.Parse() != Keyword.Values)
                                break;

                            if ((token = tokens.RequireNext()) is not OpenParentheses)
                                break;

                            var sourceValues = new List<Token>();
                            while ((token = tokens.RequireNext()) is not CloseParentheses)
                            {
                                sourceValues.Add(token);
                            }

                            if (token is not CloseParentheses)
                                throw new NotSupportedException("Simulated command processor expected a closing parentheses.");

                            desinationTable.ReceiveData(destinationColumns.ToArray(), new[] { sourceValues.ToArray() }, ValidatingGetVariableValue);

                            yield return new SimulatedNonQuery(sourceValues.Count);
                            continue;
                    }
                    break;
            }

            throw new NotSupportedException($"Simulated command processor doesn't know what to do with {token}.");
        }
    }
}
