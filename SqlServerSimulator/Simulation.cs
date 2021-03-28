using System;
using System.Collections.Generic;
using System.Data.Common;

namespace SqlServerSimulator
{
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
        /// The value for the `@@VERSION` system-defined value.
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
                var selectVersion = false;
                using (var enumerator = command.CommandText.GetEnumerator())
                {
#if DEBUG
                    var tokens = new List<Parser.Token>();
#endif
                    foreach (var token in Parser.Tokenizer.Tokenize(enumerator))
                    {
#if DEBUG
                        tokens.Add(token);
#endif

                        if (token is Parser.Tokens.Comment)
                            continue;

                        if (token is Parser.Tokens.UnquotedString unquotedString)
                        {
                            if (unquotedString.value != "SELECT")
                                throw new NotSupportedException($"Simulated command processor doesn't know what to do with {unquotedString}.");
                        }
                        else if (token is Parser.Tokens.DoubleAtPrefixedString doubleAtPrefixedString)
                        {
                            if (doubleAtPrefixedString.value != "VERSION")
                                throw new NotSupportedException($"Simulated command processor doesn't know what to do with {doubleAtPrefixedString}.");

                            selectVersion = true;
                        }
                    }
                }

                if (selectVersion)
                    yield return new SimulatedResultSet(new object[][] { new object[] { this.Version } }, new Dictionary<string, int>());
            }

            return ProduceResultSets();
        }
    }
}
