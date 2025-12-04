using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using System.Data.Common;
using System.Globalization;

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

    private SimulatedSqlException(string message, int number, byte @class, byte state)
        : this(message, new SimulatedSqlError(message, number, @class, state))
    {
    }

    private SimulatedSqlException(string? message, params ReadOnlySpan<SimulatedSqlError> errors)
        : base(message ?? "Simulated exception with no message.")
    {
        base.HResult = unchecked((int)0x80131904);
        base.Source = "Core Microsoft SqlClient Data Provider";

        if (errors.Length == 0)
        {
            this.Errors = [new SimulatedSqlError(base.Message, 0, 0, 0)];

            return;
        }

        this.Errors = [.. errors];

        var firstError = errors[0];

        this.Number = firstError.Number;
        this.Class = firstError.Class;
        this.State = firstError.State;

        var data = this.Data;

        data.Add("HelpLink.ProdName", "Microsoft SQL Server");
        data.Add("HelpLink.ProdVer", "99.00.1000");
        data.Add("HelpLink.EvtSrc", "MSSQLServer");
        data.Add("HelpLink.EvtID", firstError.Number.ToString(CultureInfo.InvariantCulture));
        data.Add("HelpLink.BaseHelpUrl", "https://go.microsoft.com/fwlink");
        data.Add("HelpLink.LinkId", "20476");
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

    /// <summary>
    /// Mimics the SqlException that occurs when an unknown type is referenced.
    /// </summary>
    /// <param name="name">The name of the type.</param>
    /// <param name="index">The 1-based index of the reference.</param>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException CannotFindDataType(string name, int index) => new($"Column, parameter, or variable #{index}: Cannot find data type {name}.", 2715, 16, 6);

    /// <summary>
    /// Mimics the SqlException that occurs then when a TOP/OFFSET/FETCH clause has an inappropriate column reference.
    /// </summary>
    /// <param name="name">The name of the column.</param>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException ColumnReferenceNotAllowed(IEnumerable<string> name)
        => new($"The reference to column \"{string.Join('.', name)}\" is not allowed in an argument to a TOP, OFFSET, or FETCH clause. Only references to columns at an outer scope or standalone expressions and subqueries are allowed here.", 4115, 15, 1);

    internal static SimulatedSqlException InvalidColumnName(string name) => new($"Invalid column name '{name}'.", 207, 16, 1);

    internal static SimulatedSqlException InvalidColumnName(IEnumerable<string> name) => InvalidColumnName(string.Join('.', name));

    internal static SimulatedSqlException InvalidObjectName(StringToken name) => new($"Invalid object name {name}.", 208, 16, 1);

    internal static SimulatedSqlException SyntaxErrorNearKeyword(ReservedKeyword token) => new($"Incorrect syntax near the keyword '{token}'.", 156, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(Token token) => new($"Incorrect syntax near '{token}'.", 102, 15, 1);

    internal static SimulatedSqlException ThereIsAlreadyAnObject(string name) => new($"There is already an object named '{name}' in the database.", 2714, 16, 6);

    /// <summary>
    /// Mimics the SqlException that occurs then when a TOP or FETCH clause returns something other than an integer.
    /// </summary>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException TopFetchRequiresInteger() => new("The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.", 1060, 15, 1);

    internal static SimulatedSqlException UnrecognizedBuiltInFunction(string name) => new($"'{name}' is not a recognized built-in function name.", 195, 15, 10);
}
