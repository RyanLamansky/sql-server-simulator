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
                using (var enumerator = command.CommandText.GetEnumerator())
                {
                    foreach (var _ in Parser.Tokenizer.Tokenize(enumerator))
                    {
                    }
                }

                yield break;
            }

            return ProduceResultSets();
        }
    }
}
