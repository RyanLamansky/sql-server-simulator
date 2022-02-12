using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Describes a simulated SQL exception.
/// </summary>
public sealed class SimulatedSqlException : DbException
{
    internal SimulatedSqlException(string? message)
        : base(message ?? "Simulated exception with no message.")
    {
    }
}
