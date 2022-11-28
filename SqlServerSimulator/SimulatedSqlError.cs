namespace SqlServerSimulator;

internal sealed class SimulatedSqlError
{
    internal SimulatedSqlError(string message, int number, byte @class, byte state)
    {
        this.Message = message;
        this.Number = number;
        this.Class = @class;
        this.State = state;
    }

    /// <summary>
    /// A description of the error.
    /// </summary>
    public string Message { get; }

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
}
