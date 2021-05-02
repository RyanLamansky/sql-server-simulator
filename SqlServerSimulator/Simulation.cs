using System;
using System.Collections.Generic;
using System.Data.Common;

namespace SqlServerSimulator
{
    using Parser;
    using Parser.Tokens;

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

        internal IEnumerable<SimulatedResultSet> CreateResultSetsForCommand(SimulatedDbCommand command)
        {
            if (string.IsNullOrEmpty(command.CommandText))
                throw new InvalidOperationException("ExecuteReader: CommandText property has not been initialized");

            IEnumerable<SimulatedResultSet> ProduceResultSets()
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
                                    if ((token = tokens.RequireNext()) is UnquotedString maybeInto && maybeInto.value == "INTO")
                                        token = tokens.RequireNext();

                                    switch (token)
                                    {
                                        case StringToken destinationTable:
                                            if ((token = tokens.RequireNext()) is not OpenParentheses)
                                                break;

                                            var destinationColumns = new List<string>();
                                            while ((token = tokens.RequireNext()) is StringToken column)
                                            {
                                                destinationColumns.Add(column.value);
                                            }

                                            if (token is not CloseParentheses)
                                                break;

                                            if ((token = tokens.RequireNext()) is not UnquotedString expectValues || expectValues.value != "VALUES")
                                                break;

                                            if ((token = tokens.RequireNext()) is not OpenParentheses)
                                                break;

                                            var sourceValues = new List<string>();
                                            while ((token = tokens.RequireNext()) is StringToken column)
                                            {
                                                sourceValues.Add(column.value);
                                            }

                                            if (token is not CloseParentheses)
                                                break;
                                            continue;
                                    }

                                    break;
                            }
                            break;
                    }

                    throw new NotSupportedException($"Simulated command processor doesn't know what to do with {token}.");
                }
            }

            return ProduceResultSets();
        }
    }
}
