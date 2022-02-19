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

    internal SimulatedSqlException(string? message, int number, byte @class, byte state)
        : this(message)
    {
        this.Number = number;
        this.Class = @class;
        this.State = state;
    }

    /// <inheritdoc/>
    public sealed override int ErrorCode => this.HResult;

    /// <inheritdoc/>
    public sealed override bool IsTransient => false;

    public int Number { get; }

    public byte Class { get; }

    public byte State { get; }
}
