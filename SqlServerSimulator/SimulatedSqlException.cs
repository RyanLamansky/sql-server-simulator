using System.Collections.Immutable;
using System.Data.Common;

namespace SqlServerSimulator;

#pragma warning disable CA1032 // Implement standard exception constructors
// This is only thrown internally so standard constructors aren't needed.

/// <summary>
/// Describes a simulated SQL exception.
/// </summary>
internal sealed class SimulatedSqlException : DbException
{
    internal SimulatedSqlException(string? message)
        : this(message, [])
    {
    }

    internal SimulatedSqlException(string message, int number, byte @class, byte state)
        : this(message, new SimulatedSqlError(message, number, @class, state))
    {
    }

    internal SimulatedSqlException(string? message, params SimulatedSqlError[] errors)
        : base(message ?? "Simulated exception with no message.")
    {
        base.HResult = unchecked((int)0x80131904);

        if (errors.Length == 0)
        {
            this.Errors = ImmutableArray.Create([new SimulatedSqlError(base.Message, 0, 0, 0)]);

            return;
        }

        this.Errors = ImmutableArray.Create(errors);

        var firstError = errors[0];

        this.Number = firstError.Number;
        this.Class = firstError.Class;
        this.State = firstError.State;
    }

    /// <inheritdoc/>
    public sealed override int ErrorCode => this.HResult;

    /// <inheritdoc/>
    public sealed override bool IsTransient => false;

    /// <summary>
    /// An error number as described by https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors .
    /// </summary>
    public int Number { get; }

    /// <summary>
    /// A value from 1 to 25 that indicates the severity level of the error. The default is 0.
    /// </summary>
    /// <remarks>
    /// The severity indicates how serious the error is.
    /// Errors that have a low severity, such as 1 or 2, are information messages or low-level warnings.
    /// Errors that have a high severity indicate problems that should be addressed as soon as possible.
    /// </remarks>
    public byte Class { get; }

    /// <summary>
    /// Some error messages can be raised at multiple points in the code for the Database Engine.
    /// For example, an 1105 error can be raised for several different conditions.
    /// Each specific condition that raises an error assigns a unique state code.
    /// </summary>
    public byte State { get; }

    public IReadOnlyList<SimulatedSqlError> Errors { get; }
}
