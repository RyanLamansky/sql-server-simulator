using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Concurrent;
using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Simulates a SQL Server instance.
/// </summary>
public sealed class Simulation
{
    /// <summary>
    /// Creates a new simulated SQL Server instance with no tables or data.
    /// </summary>
    public Simulation()
    {
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    /// <summary>User tables, keyed by name.</summary>
    internal readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static Dictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var context = new ParserContext(command);

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case Operator { Character: ';' }:
                    continue;

                case ReservedKeyword { Keyword: Keyword.Set }:
                    if (TryParseSet(context))
                        continue;
                    break;

                case ReservedKeyword { Keyword: Keyword.Create }:
                    switch (context.GetNextRequired())
                    {
                        case ReservedKeyword { Keyword: Keyword.Table }:
                            if (context.GetNextRequired() is not Name tableName)
                                break;

                            if (context.GetNextRequired() is not Operator { Character: '(' })
                                break;

                            var rawColumns = new List<(Name Name, Name TypeName, bool Nullable)>();
                            bool suppressAdvanceToken;
                            do
                            {
                                suppressAdvanceToken = false;
                                var columnName = context.GetNextRequired<Name>();
                                var type = context.GetNextRequired<Name>();

                                bool nullable;

                                if (context.GetNextRequired() is ReservedKeyword next)
                                {
                                    switch (next.Keyword)
                                    {
                                        case Keyword.Not:
                                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                                                throw SimulatedSqlException.SyntaxErrorNear(context);

                                            nullable = false;
                                            break;
                                        case Keyword.Null:
                                            nullable = true;
                                            break;
                                        default:
                                            throw SimulatedSqlException.SyntaxErrorNear(context);
                                    }
                                }
                                else
                                {
                                    suppressAdvanceToken = true;
                                    nullable = true;
                                }

                                rawColumns.Add((columnName, type, nullable));
                            } while ((suppressAdvanceToken ? context.Token : context.GetNextRequired()) is Operator { Character: ',' });

                            if (context.Token is not Operator { Character: ')' })
                                break;

                            var heapColumns = new HeapColumn[rawColumns.Count];
                            for (var i = 0; i < rawColumns.Count; i++)
                                heapColumns[i] = new(rawColumns[i].Name.Value, SqlType.GetByName(rawColumns[i].TypeName, i + 1), rawColumns[i].Nullable);

                            var heapTable = new HeapTable(tableName.Value, heapColumns);
                            if (!this.HeapTables.TryAdd(heapTable.Name, heapTable))
                                throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);

                            continue;
                    }
                    break;

                case ReservedKeyword { Keyword: Keyword.Select }:
                    yield return Selection.Parse(context, 0).Results;
                    continue;

                case ReservedKeyword { Keyword: Keyword.Insert }:
                    if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Into })
                        context.MoveNextRequired();

                    if (context.Token is not StringToken destinationTableToken)
                        break;

                    yield return this.HeapTables.TryGetValue(destinationTableToken.Value, out var destinationTable)
                        ? ProcessHeapInsert(destinationTable, context)
                        : throw SimulatedSqlException.InvalidObjectName(destinationTableToken);
                    continue;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// INSERT processor. Parses the column subset and VALUES tuples, converts
    /// each value token to a <see cref="SqlValue"/> typed to its target column,
    /// encodes each row via <see cref="RowEncoder.EncodeRow"/>, and appends
    /// the bytes to <paramref name="destinationTable"/>'s heap.
    /// </summary>
    private static SimulatedNonQuery ProcessHeapInsert(HeapTable destinationTable, ParserContext context)
    {
        HeapColumn[] destinationColumns;
        if (context.GetNextRequired() is Operator { Character: '(' })
        {
            var usedColumns = new List<HeapColumn>();
            while (context.GetNextRequired() is StringToken column)
            {
                var columnName = column.Value;
                var tableColumn = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                    ?? throw SimulatedSqlException.InvalidColumnName(columnName);
                usedColumns.Add(tableColumn);
            }

            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            destinationColumns = [.. usedColumns];

            context.MoveNextRequired();
        }
        else
        {
            destinationColumns = [.. destinationTable.Columns];
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var sourceRows = new List<Token[]>();

        do
        {
            if (context.GetNextRequired<Operator>() is not { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var sourceValues = new List<Token>();
            while (true)
            {
                var valueToken = context.GetNextRequired();
                if (valueToken is Operator { Character: ',' or ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                sourceValues.Add(valueToken);

                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            sourceRows.Add([.. sourceValues]);

        } while (context.GetNextOptional() is Operator { Character: ',' });

        foreach (var sourceRow in sourceRows)
        {
            var rowValues = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(destinationTable.Schema[i]);

            for (var i = 0; i < destinationColumns.Length; i++)
            {
                var targetColumn = destinationColumns[i];
                var ordinal = -1;
                for (var j = 0; j < destinationTable.Columns.Length; j++)
                {
                    if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }

                rowValues[ordinal] = TokenToSqlValue(sourceRow[i], targetColumn.Type, context.GetVariableValue);
            }

            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.Schema, rowValues));
        }

        return new SimulatedNonQuery(sourceRows.Count);
    }

    private static SqlValue TokenToSqlValue(Token token, SqlType targetType, Func<string, SqlValue> getVariableValue)
    {
        var source = token switch
        {
            Numeric numeric => numeric.Value,
            AtPrefixedString atPrefixed => getVariableValue(atPrefixed.Value),
            _ => throw new NotSupportedException($"INSERT doesn't know how to handle input of type {token.GetType().Name}."),
        };

        try
        {
            return source.CoerceTo(targetType);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(targetType.ToString()!);
        }
    }

    private static bool TryParseSet(ParserContext context)
    {
        var setTarget = context.GetNextRequired<UnquotedString>().Value;
        Span<char> upper = stackalloc char[setTarget.Length];
        return setTarget.ToUpperInvariant(upper) switch
        {
            7 => upper switch
            {
                "NOCOUNT" => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
                _ => false
            },
            21 => upper switch
            {
                "IMPLICIT_TRANSACTIONS" => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
                _ => false
            },
            _ => false
        };
    }
}
