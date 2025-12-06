using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schema;
using System.Collections.Concurrent;
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

    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        using var context = new ParserContext(command);

        while (context.TryMoveNext(out context.Token))
        {
            switch (context.Token)
            {
                case Comment:
                    continue;

                case StatementTerminator:
                    continue;

                case ReservedKeyword reserved:
                    switch (reserved.Keyword)
                    {
                        case Keyword.Set:
                            switch (context.Token = context.RequireNext())
                            {
                                case UnquotedString setTarget:
                                    switch (setTarget.Value.ToUpperInvariant())
                                    {
                                        case "IMPLICIT_TRANSACTIONS":
                                        case "NOCOUNT":
                                            switch (context.Token = context.RequireNext())
                                            {
                                                case ReservedKeyword onOff:
                                                    switch (onOff.Keyword)
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
                            switch (context.Token = context.RequireNext())
                            {
                                case ReservedKeyword whatToCreate:
                                    switch (whatToCreate.Keyword)
                                    {
                                        case Keyword.Table:
                                            if (context.RequireNext() is not Name tableName)
                                                break;

                                            if ((context.Token = context.RequireNext()) is not OpenParentheses)
                                                break;

                                            var table = new Table(tableName.Value);

                                            var columns = table.Columns;
                                            bool suppressAdvanceToken;
                                            do
                                            {
                                                suppressAdvanceToken = false;
                                                if (context.RequireNext() is not Name columnName)
                                                    throw new SimulatedSqlException("Simulated table creation requires named columns.");

                                                if (context.RequireNext() is not Name type)
                                                    throw new SimulatedSqlException("Simulated table creation requires columns to have a type.");

                                                var nullable = true;

                                                context.Token = context.RequireNext();
                                                if (context.Token is ReservedKeyword next)
                                                {
                                                    switch (next.Keyword)
                                                    {
                                                        case Keyword.Not:
                                                            if ((context.Token = context.RequireNext()) is not ReservedKeyword { Keyword: Keyword.Null })
                                                                throw new NotSupportedException($"Simulated command processor doesn't know how to handle column definition token {context.Token}.");

                                                            nullable = false;
                                                            break;
                                                        case Keyword.Null:
                                                            nullable = true;
                                                            break;
                                                        default:
                                                            throw new NotSupportedException($"Simulated command processor doesn't know how handle column definition token {context.Token}.");
                                                    }
                                                }
                                                else
                                                {
                                                    suppressAdvanceToken = true;
                                                    nullable = true;
                                                }

                                                columns.Add(new(columnName.Value, DataType.GetByName(type, columns.Count + 1), nullable));
                                            } while ((suppressAdvanceToken ? context.Token : context.Token = context.RequireNext()) is Comma);

                                            if (context.Token is not CloseParentheses)
                                                break;

                                            if (!this.Tables.TryAdd(table.Name, table))
                                                throw SimulatedSqlException.ThereIsAlreadyAnObject(table.Name);

                                            continue;
                                    }
                                    break;
                            }
                            break;

                        case Keyword.Select:
                            yield return Selection.Parse(context, 0).Results;
                            break;

                        case Keyword.Insert:
                            if ((context.Token = context.RequireNext()) is ReservedKeyword { Keyword: Keyword.Into })
                                context.Token = context.RequireNext();

                            if (context.Token is not StringToken destinationTableToken)
                                break;

                            if (!this.Tables.TryGetValue(destinationTableToken.Value, out var destinationTable))
                                throw SimulatedSqlException.InvalidObjectName(destinationTableToken);

                            Column[] destinationColumns;
                            if ((context.Token = context.RequireNext()) is OpenParentheses)
                            {
                                var usedColumns = new List<Column>();
                                while ((context.Token = context.RequireNext()) is StringToken column)
                                {
                                    var columnName = column.Value;
                                    var tableColumn = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                                        ?? throw SimulatedSqlException.InvalidColumnName(columnName);
                                    usedColumns.Add(tableColumn);
                                }

                                if (context.Token is not CloseParentheses)
                                    break;

                                destinationColumns = [.. usedColumns];

                                context.Token = context.RequireNext();
                            }
                            else
                            {
                                destinationColumns = [.. destinationTable.Columns];
                            }

                            if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
                                break;

                            if ((context.Token = context.RequireNext()) is not OpenParentheses)
                                break;

                            var sourceValues = new List<Token>();
                            while ((context.Token = context.RequireNext()) is not CloseParentheses)
                            {
                                sourceValues.Add(context.Token);
                            }

                            if (context.Token is not CloseParentheses)
                                throw new NotSupportedException("Simulated command processor expected a closing parentheses.");

                            destinationTable.ReceiveData(destinationColumns, [[.. sourceValues]], context.GetVariableValue);

                            yield return new SimulatedNonQuery(sourceValues.Count);
                            continue;
                    }
                    break;

                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context.Token);
            }
        } // while (tokens.TryMoveNext(out var token))
    }
}
