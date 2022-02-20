using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Describes a simulated SQL exception.
/// </summary>
internal sealed class SimulatedSqlException : DbException
{
    internal SimulatedSqlException(string? message)
        : this(message, Array.Empty<SimulatedSqlError>())
    {
    }

    internal SimulatedSqlException(string message, int number, byte @class, byte state)
        : this(message, new[] { new SimulatedSqlError(message, number, @class, state)})
    {
    }

    internal SimulatedSqlException(string? message, IReadOnlyList<SimulatedSqlError>? errors)
        : base(message ?? "Simulated exception with no message.")
    {
        base.HResult = unchecked((int)0x80131904);
        
        errors = this.Errors = errors ?? Array.Empty<SimulatedSqlError>();

        if (errors.Count == 0)
        {
            this.Errors = new [] { new SimulatedSqlError(base.Message, 0, 0, 0)};

            return;
        }

        var firstError = errors[0];

        this.Number = firstError.Number;
        this.Class = firstError.Class;
        this.State = firstError.State;
    }

    /// <inheritdoc/>
    public sealed override int ErrorCode => this.HResult;

    /// <inheritdoc/>
    public sealed override bool IsTransient => false;

    public int Number { get; }

    public byte Class { get; }

    public byte State { get; }

    public IReadOnlyList<SimulatedSqlError> Errors { get; }
}
