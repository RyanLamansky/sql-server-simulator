using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schema;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Simulates a SQL Server instance.
/// </summary>
public sealed class Simulation
{
    /// <summary>
    /// Creates a new <see cref="Simulation"/> instance.
    /// </summary>
    public Simulation()
    {
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    internal readonly ConcurrentDictionary<string, Table> Tables = new(Collation.Default);

    internal readonly Lazy<Dictionary<string, Table>> SystemTables = new(() => BuiltInResources.SystemTables.ToDictionary(table => table.Name, Collation.Default));

#if DEBUG
    private sealed class TokenArrayEnumerator(string? command) : IEnumerator<Token>
    {
        private readonly Token[] source = [.. Tokenizer.Tokenize(command)];

        public int Index { get; private set; } = -1;

        public Token Current => source[Index];

        object System.Collections.IEnumerator.Current => Current;

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            var newIndex = checked(Index + 1);
            if (newIndex >= source.Length)
                return false;

            Index = newIndex;
            return true;
        }

        public void Reset() => Index = -1;

        public override string ToString()
        {
            var result = new System.Text.StringBuilder();
            var source = this.source;

            for (var i = 0; i < source.Length; i++)
            {
                var token = source[i];
                if (i != Index)
                {
                    _ = result.Append(token).Append('·');
                    continue;
                }

                _ = result.Append('»').Append(token).Append('«').Append('·');
            }

            return result.ToString(0, result.Length - 1);

        }
    }
#endif

    private sealed class VariableFinder(SimulatedDbCommand command)
    {
        private readonly FrozenDictionary<string, (string Name, (DataType type, object? Value) TypeValue)> variables = command
            .Parameters
            .Cast<DbParameter>()
            .Select(parameter =>
            {
                var name = parameter.ParameterName;
                var type = DataType.GetByDbType(parameter.DbType);
                return (Name: name.StartsWith('@') ? name[1..] : name, TypeValue: (DataType: type, Value: parameter.Value is null ? null : type.ConvertFrom(parameter.Value)));
            })
            .ToFrozenDictionary(tuple => tuple.Name, StringComparer.InvariantCultureIgnoreCase);

        private object? ValidatingGetVariableValue(string name)
        {
            return variables.TryGetValue(name, out var value)
                ? value.TypeValue.Value
                : throw new SimulatedSqlException($"Must declare the scalar variable \"@{name}\".");
        }

        public static Func<string, object?> FromCommand(SimulatedDbCommand command) => new VariableFinder(command).ValidatingGetVariableValue;
    }

    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var getVariableValue = VariableFinder.FromCommand(command);

#if DEBUG
        using var tokens = new TokenArrayEnumerator(command.CommandText);
#else
        using var tokens = Tokenizer.Tokenize(command.CommandText).GetEnumerator();
#endif

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
                                        case Keyword.Implicit_Transactions:
                                        case Keyword.NoCount:
                                            switch (token = tokens.RequireNext())
                                            {
                                                case UnquotedString onOff:
                                                    switch (onOff.Parse())
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
                                            bool suppressAdvanceToken;
                                            do
                                            {
                                                suppressAdvanceToken = false;
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
                                                    suppressAdvanceToken = true;
                                                    nullable = true;
                                                }

                                                columns.Add(new Column(columnName.Value, DataType.GetByName(type), nullable));
                                            } while ((suppressAdvanceToken ? token : token = tokens.RequireNext()) is Comma);

                                            if (token is not CloseParentheses)
                                                break;

                                            if (!this.Tables.TryAdd(table.Name, table))
                                                throw new SimulatedSqlException($"There is already an object named '{table.Name}' in the database.", 2714, 16, 6);

                                            continue;
                                    }
                                    break;
                            }
                            break;

                        case Keyword.Select:
                            yield return Selection.Parse(this, tokens, ref token, getVariableValue).Results;
                            break;

                        case Keyword.Insert:
                            if ((token = tokens.RequireNext()) is UnquotedString maybeInto && maybeInto.TryParse(out var keyword) && keyword == Keyword.Into)
                                token = tokens.RequireNext();

                            if (token is not StringToken destinationTableToken)
                                break;

                            if (!this.Tables.TryGetValue(destinationTableToken.Value, out var destinationTable))
                                throw new SimulatedSqlException($"Invalid object name '{destinationTableToken.Value}'.", 208, 16, 0);

                            Column[] destinationColumns;
                            if ((token = tokens.RequireNext()) is OpenParentheses)
                            {
                                var usedColumns = new List<Column>();
                                while ((token = tokens.RequireNext()) is StringToken column)
                                {
                                    var columnName = column.Value;
                                    var tableColumn = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                                        ?? throw new SimulatedSqlException($"Invalid column name '{columnName}'.", 207, 16, 1);
                                    usedColumns.Add(tableColumn);
                                }

                                if (token is not CloseParentheses)
                                    break;

                                destinationColumns = [.. usedColumns];

                                token = tokens.RequireNext();
                            }
                            else
                            {
                                destinationColumns = [.. destinationTable.Columns];
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

                            destinationTable.ReceiveData(destinationColumns, [[.. sourceValues]], getVariableValue);

                            yield return new SimulatedNonQuery(sourceValues.Count);
                            continue;
                    }
                    break;
            }

            throw new NotSupportedException($"Simulated command processor doesn't know what to do with {token}.");
        }
    }
}
