using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Describes a simulated SQL exception. Mirrors enough of
/// <c>Microsoft.Data.SqlClient.SqlException</c> that consumers who catch
/// <see cref="DbException"/> can downcast and read
/// <see cref="Number"/> / <see cref="Class"/> / <see cref="State"/> /
/// <see cref="Errors"/> the same way they would against a real
/// <c>SqlException</c>.
/// </summary>
/// <remarks>
/// The Msg-specific factory methods live in topical partial files in the same
/// directory:
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
/// </remarks>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Constructors are private — instances are only built via the topical factory methods on this partial class.")]
public sealed partial class SimulatedSqlException : DbException
{
    private const string SourceName = "Core Microsoft SqlClient Data Provider";

    private SimulatedSqlException(string message, int number, byte @class, byte state)
        : this(message, new SimulatedError(@class, lineNumber: 0, message, number, procedure: "", server: "", source: SourceName, state))
    {
    }

    private SimulatedSqlException(string message, params ReadOnlySpan<SimulatedError> errors)
        : base(message)
    {
        base.HResult = unchecked((int)0x80131904);
        base.Source = SourceName;

        SimulatedError first;
        if (errors.Length == 0)
        {
            first = new SimulatedError(@class: 0, lineNumber: 0, base.Message, number: 0, procedure: "", server: "", source: SourceName, state: 0);
            this.Errors = new SimulatedErrorCollection([first]);
        }
        else
        {
            first = errors[0];
            this.Errors = new SimulatedErrorCollection([.. errors]);
        }

        this.Number = first.Number;
        this.Class = first.Class;
        this.State = first.State;

        var data = this.Data;

        data.Add("HelpLink.ProdName", "Microsoft SQL Server");
        data.Add("HelpLink.ProdVer", "99.00.1000");
        data.Add("HelpLink.EvtSrc", "MSSQLServer");
        data.Add("HelpLink.EvtID", first.Number.ToString(CultureInfo.InvariantCulture));
        data.Add("HelpLink.BaseHelpUrl", "https://go.microsoft.com/fwlink");
        data.Add("HelpLink.LinkId", "20476");
    }

    /// <inheritdoc/>
    public sealed override int ErrorCode => this.HResult;

    /// <inheritdoc/>
    public sealed override bool IsTransient => false;

    /// <summary>
    /// An error number as described by https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors .
    /// Mirrors <c>SqlException.Number</c>.
    /// </summary>
    public int Number { get; }

    /// <summary>
    /// A value from 1 to 25 that indicates the severity level of the error. The default is 0.
    /// Mirrors <c>SqlException.Class</c>.
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
    /// Mirrors <c>SqlException.State</c>.
    /// </summary>
    public byte State { get; }

    /// <summary>Collection of one or more <see cref="SimulatedError"/> entries. Mirrors <c>SqlException.Errors</c>.</summary>
    public SimulatedErrorCollection Errors { get; }

    /// <summary>1-based line number of the first error. Shortcut for <c>Errors[0].LineNumber</c>; mirrors <c>SqlException.LineNumber</c>.</summary>
    public int LineNumber => this.Errors[0].LineNumber;

    /// <summary>Name of the procedure or trigger generating the first error, or empty string. Shortcut for <c>Errors[0].Procedure</c>; mirrors <c>SqlException.Procedure</c>.</summary>
    public string Procedure => this.Errors[0].Procedure;

    /// <summary>Server name carried by the first error. Shortcut for <c>Errors[0].Server</c>; mirrors <c>SqlException.Server</c>.</summary>
    public string Server => this.Errors[0].Server;
}
