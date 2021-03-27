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
    }
}
