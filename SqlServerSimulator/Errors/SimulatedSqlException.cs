using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Describes a simulated SQL exception. The Msg-specific factory methods
/// live in topical partial files in the same directory:
/// <list type="bullet">
/// <item><c>SimulatedSqlException.TypeErrors.cs</c> — type lookup, size,
/// CAST / CONVERT, conversion, arithmetic.</item>
/// <item><c>SimulatedSqlException.SchemaErrors.cs</c> — DDL rules (identity,
/// rowversion, computed columns, table-level invariants, compatibility).</item>
/// <item><c>SimulatedSqlException.ConstraintErrors.cs</c> — per-row write
/// violations (NOT NULL, CHECK, PK / UNIQUE, truncation, row size).</item>
/// <item><c>SimulatedSqlException.ResolutionErrors.cs</c> — column / object /
/// identifier resolution.</item>
/// <item><c>SimulatedSqlException.QueryErrors.cs</c> — set ops, ORDER BY,
/// aggregates, subqueries, pagination, function lookup.</item>
/// <item><c>SimulatedSqlException.SyntaxErrors.cs</c> — generic parse-time
/// errors.</item>
/// </list>
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only thrown internally; the public-API surface doesn't need standard exception constructors.")]
internal sealed partial class SimulatedSqlException : DbException
{
    private SimulatedSqlException(string message, int number, byte @class, byte state)
        : this(message, new SimulatedSqlError(message, number, @class, state))
    {
    }

    private SimulatedSqlException(string message, params ReadOnlySpan<SimulatedSqlError> errors)
        : base(message)
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
    public readonly int Number;

    /// <summary>
    /// A value from 1 to 25 that indicates the severity level of the error. The default is 0.
    /// </summary>
    /// <remarks>
    /// The severity indicates how serious the error is.
    /// Errors that have a low severity, such as 1 or 2, are information messages or low-level warnings.
    /// Errors that have a high severity indicate problems that should be addressed as soon as possible.
    /// </remarks>
    public readonly byte Class;

    /// <summary>
    /// Some error messages can be raised at multiple points in the code for the Database Engine.
    /// For example, an 1105 error can be raised for several different conditions.
    /// Each specific condition that raises an error assigns a unique state code.
    /// </summary>
    public readonly byte State;

    public readonly SimulatedSqlError[] Errors;
}
