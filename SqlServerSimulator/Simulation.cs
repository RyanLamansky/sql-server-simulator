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
        var context = new ParserContext(command);

        while (context.GetNextOptional() is not null)
        {
            switch (context.Token)
            {
                case Comment:
                    continue;

                case Operator { Character: ';' }:
                    continue;

                case ReservedKeyword reserved:
                    switch (reserved.Keyword)
                    {
                        case Keyword.Set:
                            switch (context.GetNextRequired())
                            {
                                case UnquotedString setTarget:
                                    switch (setTarget.Value.ToUpperInvariant())
                                    {
                                        case "IMPLICIT_TRANSACTIONS":
                                        case "NOCOUNT":
                                            switch (context.GetNextRequired())
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
                            switch (context.GetNextRequired())
                            {
                                case ReservedKeyword whatToCreate:
                                    switch (whatToCreate.Keyword)
                                    {
                                        case Keyword.Table:
                                            if (context.GetNextRequired() is not Name tableName)
                                                break;

                                            if (context.GetNextRequired() is not Operator { Character: '(' })
                                                break;

                                            var table = new Table(tableName.Value);

                                            var columns = table.Columns;
                                            bool suppressAdvanceToken;
                                            do
                                            {
                                                suppressAdvanceToken = false;
                                                var columnName = context.GetNextRequired<Name>();
                                                var type = context.GetNextRequired<Name>();

                                                var nullable = true;

                                                context.MoveNextRequired();
                                                if (context.Token is ReservedKeyword next)
                                                {
                                                    switch (next.Keyword)
                                                    {
                                                        case Keyword.Not:
                                                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
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
                                            } while ((suppressAdvanceToken ? context.Token : context.GetNextRequired()) is Operator { Character: ',' });

                                            if (context.Token is not Operator { Character: ')' })
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
                            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Into })
                                context.MoveNextRequired();

                            if (context.Token is not StringToken destinationTableToken)
                                break;

                            if (!this.Tables.TryGetValue(destinationTableToken.Value, out var destinationTable))
                                throw SimulatedSqlException.InvalidObjectName(destinationTableToken);

                            Column[] destinationColumns;
                            if (context.GetNextRequired() is Operator { Character: '(' })
                            {
                                var usedColumns = new List<Column>();
                                while (context.GetNextRequired() is StringToken column)
                                {
                                    var columnName = column.Value;
                                    var tableColumn = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                                        ?? throw SimulatedSqlException.InvalidColumnName(columnName);
                                    usedColumns.Add(tableColumn);
                                }

                                if (context.Token is not Operator { Character: ')' })
                                    break;

                                destinationColumns = [.. usedColumns];

                                context.MoveNextRequired();
                            }
                            else
                            {
                                destinationColumns = [.. destinationTable.Columns];
                            }

                            if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
                                break;

                            var sourceRows = new List<Token[]>();

                            do
                            {
                                if (context.GetNextRequired<Operator>() is not { Character: '(' })
                                    throw SimulatedSqlException.SyntaxErrorNear(context.Token);

                                var sourceValues = new List<Token>();
                                while (context.GetNextRequired() is not Operator { Character: ')' })
                                    sourceValues.Add(context.Token);

                                sourceRows.Add([.. sourceValues]);

                            } while (context.GetNextOptional() is Operator { Character: ',' });

                            destinationTable.ReceiveData(destinationColumns, sourceRows, context.GetVariableValue);

                            yield return new SimulatedNonQuery(sourceRows.Count);
                            continue;
                    }
                    break;

                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context.Token);
            }
        } // while (tokens.TryMoveNext(out var token))
    }
}
