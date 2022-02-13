using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Describes a simulated SQL exception.
/// </summary>
internal sealed class SimulatedSqlException : DbException
{
    internal SimulatedSqlException(string? message)
        : base(message ?? "Simulated exception with no message.")
    {
        base.HResult = unchecked((int)0x80131904);
    }

    /// <inheritdoc/>
    public sealed override int ErrorCode => this.HResult;

    /// <inheritdoc/>
    public sealed override bool IsTransient => false;
}
