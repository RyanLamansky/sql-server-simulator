using System;

namespace SqlServerSimulator
{
    /// <summary>
    /// Describes a simulated SQL exception.
    /// </summary>
    public sealed class SimulatedSqlException : Exception
    {
        internal SimulatedSqlException(string? message)
            : base(message ?? "Simulated exception with no message.")
        {
        }
    }
}
