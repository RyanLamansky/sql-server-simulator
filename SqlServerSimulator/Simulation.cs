using System;
using System.Collections.Generic;
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

    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
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

                                            var table = new Table(tableName.value);

                                            var columns = table.Columns;
                                            do
                                            {
                                                if (tokens.RequireNext() is not Name columnName)
                                                    throw new SimulatedSqlException("Simulated table creation requires named columns.");

                                                if (tokens.RequireNext() is not Name type)
                                                    throw new SimulatedSqlException("Simulated table creation requires columns to have a type.");

                                                columns.Add(new Column(columnName.value, type.value));
                                            } while ((token = tokens.RequireNext()) is Comma);

                                            if (token is not CloseParentheses)
                                                break;

                                            continue;
                                    }
                                    break;
                            }
                            break;

                        case Keyword.Select:
                            switch (token = tokens.RequireNext())
                            {
                                case DoubleAtPrefixedString selected:
                                    switch (selected.value)
                                    {
                                        case "VERSION":
                                            if (tokens.TryMoveNext(out token))
                                                break;

                                            yield return new SimulatedResultSet(new Dictionary<string, int>(), new object[] { this.Version });
                                            continue;
                                    }
                                    break;
                            }
                            break;

                        case Keyword.Insert:
                            if ((token = tokens.RequireNext()) is UnquotedString maybeInto && maybeInto.TryParse(out var keyword) && keyword == Keyword.Into)
                                token = tokens.RequireNext();

                            if (token is not StringToken desinationTable)
                                break;

                            if ((token = tokens.RequireNext()) is not OpenParentheses)
                                break;

                            var destinationColumns = new List<string>();
                            while ((token = tokens.RequireNext()) is StringToken column)
                            {
                                destinationColumns.Add(column.value);
                            }

                            if (token is not CloseParentheses)
                                break;

                            if ((token = tokens.RequireNext()) is not UnquotedString expectValues || expectValues.Parse() != Keyword.Values)
                                break;

                            if ((token = tokens.RequireNext()) is not OpenParentheses)
                                break;

                            var sourceValues = new List<object>();
                            while ((token = tokens.RequireNext()) is not CloseParentheses)
                            {
                                switch (token)
                                {
                                    case StringToken parsed:
                                        sourceValues.Add(parsed.value);
                                        break;
                                    case Numeric parsed:
                                        sourceValues.Add(parsed.Value);
                                        break;
                                    default:
                                        throw new NotSupportedException($"Simulated command processor doesn't know how to insert {token}.");
                                }
                            }

                            if (token is not CloseParentheses)
                                throw new NotSupportedException("Simulated command processor expected a closing parentheses.");

                            yield return new SimulatedNonQuery(sourceValues.Count);
                            continue;
                    }
                    break;
            }

            throw new NotSupportedException($"Simulated command processor doesn't know what to do with {token}.");
        }
    }
}
