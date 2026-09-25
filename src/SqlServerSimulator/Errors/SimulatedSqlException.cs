using System.Collections;
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
        : this(Printable(message), new SimulatedError(@class, lineNumber: 0, Printable(message), number, procedure: "", server: SimulatedDbConnection.DataSourceName, source: SourceName, state))
    {
    }

    /// <summary>
    /// A value a message quotes can't carry a NUL, which real prints as a
    /// period (<c>'a.b'</c> for <c>'a' + CHAR(0) + 'b'</c>; probed 2026-09-25
    /// against SQL Server 2025) where every other control character passes
    /// through.
    /// </summary>
    private static string Printable(string message) => message.Replace('\0', '.');

    private SimulatedSqlException(string message, params ReadOnlySpan<SimulatedError> errors)
        : base(message)
    {
        base.HResult = unchecked((int)0x80131904);
        base.Source = SourceName;

        SimulatedError first;
        if (errors.Length == 0)
        {
            first = new SimulatedError(@class: 0, lineNumber: 0, base.Message, number: 0, procedure: "", server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 0);
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
    }

    private bool dataFilled;

    /// <summary>
    /// The <c>HelpLink.*</c> entries <c>SqlException</c> carries, then anything a caller adds.
    /// </summary>
    /// <remarks>
    /// Filled on first read rather than at construction, since most catches never read it.
    /// </remarks>
    public override IDictionary Data
    {
        get
        {
            var data = base.Data;
            if (this.dataFilled)
                return data;

            data["HelpLink.ProdName"] = "Microsoft SQL Server";
            data["HelpLink.ProdVer"] = "99.00.1000";
            data["HelpLink.EvtSrc"] = "MSSQLServer";
            data["HelpLink.EvtID"] = this.Number.ToString(CultureInfo.InvariantCulture);
            data["HelpLink.BaseHelpUrl"] = "https://go.microsoft.com/fwlink";
            data["HelpLink.LinkId"] = "20476";
            this.dataFilled = true;
            return data;
        }
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

    /// <summary>
    /// When <see langword="true"/>, this error terminates the whole batch
    /// rather than merely its own statement — the statement-continuation
    /// engine emits it, then stops (real SQL Server's <c>THROW</c> semantics:
    /// an uncaught <c>THROW</c> ends the batch, unlike a severity-16
    /// <c>RAISERROR</c> which lets the batch proceed). Set by the
    /// <c>THROW</c> factories; every other factory leaves it
    /// <see langword="false"/>. Internal — never part of the public
    /// <c>SqlException</c>-shaped surface.
    /// </summary>
    internal bool TerminatesBatch { get; private init; }

    /// <summary>
    /// When <see langword="true"/>, raising this error rolls the session's
    /// whole transaction stack back and cannot be intercepted by a
    /// <c>BEGIN TRY</c> frame — SQL Server's transaction-aborting error class,
    /// distinct from the far commoner statement-aborting one that leaves
    /// <c>@@TRANCOUNT</c> alone. Probe-confirmed against SQL Server 2025
    /// (2026-08-05) for the RANGE-frame ORDER BY diagnostic (Msg 8728): a
    /// transaction opened in an earlier batch reads <c>@@TRANCOUNT</c> 0 and
    /// <c>XACT_STATE()</c> 0 afterwards, its writes are undone, a surrounding
    /// <c>BEGIN TRY</c> never reaches its <c>CATCH</c>, and the statements
    /// after it in the batch never run — while the neighbouring bind and
    /// runtime errors (Msg 207 / 208 / 306 / 4104 / 4194 / 8120 / 8134) all
    /// leave the transaction standing. Internal — never part of the public
    /// <c>SqlException</c>-shaped surface.
    /// </summary>
    internal bool AbortsTransaction { get; private init; }

    /// <summary>
    /// When <see langword="true"/>, this error behaves as every run-time error
    /// does under <c>SET XACT_ABORT ON</c>, whatever the option says:
    /// uncaught, it ends the batch and rolls the whole transaction stack back;
    /// caught by a <c>TRY</c> frame, it dooms the transaction. Probe-confirmed
    /// against SQL Server 2025 (2026-09-23) for the XML parsing family.
    /// Internal — never part of the public <c>SqlException</c>-shaped surface.
    /// </summary>
    internal bool AbortsAsUnderXactAbort { get; private init; }

    /// <summary>
    /// When <see langword="true"/>, this error came from a <c>RAISERROR</c>
    /// statement, the one raising construct <c>SET XACT_ABORT ON</c> does not
    /// promote: probed against SQL Server 2025, a severity-16 or severity-19
    /// <c>RAISERROR</c> under <c>XACT_ABORT ON</c> leaves the batch running and
    /// the transaction committable, where the same session's <c>THROW</c> ends
    /// the batch and rolls back. (Caught by a <c>TRY</c> frame it still dooms
    /// the transaction, like any other error under the option.)
    /// </summary>
    internal bool RaisedByRaiserror { get; private init; }

    /// <summary>
    /// Set once, at the innermost dispatch frame, when
    /// <c>SET XACT_ABORT ON</c> promoted this error out of the
    /// statement-terminating class: the transaction has already been rolled
    /// back or doomed, and the frame that finally handles the error ends the
    /// batch rather than continuing to the next statement. Mutable rather than
    /// <c>init</c> because the promotion is a property of the session the error
    /// was raised in, not of the factory that built it.
    /// </summary>
    internal bool XactAbortPromoted;

    /// <summary>
    /// Set when this error ended the batch of the procedure or dynamic SQL it
    /// was raised in, which is as far as real lets it reach: in the caller the
    /// <c>EXEC</c> fails like any other statement and the caller's batch goes on
    /// (probed 2026-09-24 against SQL Server 2025, for a missing table, a
    /// missing column, and a compile error in <c>EXEC('…')</c> /
    /// <c>sp_executesql</c>).
    /// </summary>
    internal bool EndedCalledBatch;

    /// <summary>
    /// Set by the first dispatch frame that sees this error, which is the
    /// scope whose statement raised it. Only that scope's procedure counts the
    /// error toward the status it returns without a <c>RETURN</c> value
    /// (<see cref="Parser.ProcFrame.MaxErrorSeverity"/>): one propagating out of
    /// a nested procedure or dynamic SQL arrives already recorded, and real
    /// leaves the caller's status at 0 for it (probed 2026-09-24 against
    /// SQL Server 2025).
    /// </summary>
    internal bool RaisingScopeRecorded;

    /// <summary>
    /// Set when this error escaped a trigger body. The firing statement then
    /// sends Msg 3621 after it even when the error ends the batch, where an
    /// error ending the batch from the statement itself sends none (probed
    /// 2026-09-24 against SQL Server 2025).
    /// </summary>
    internal bool EndedTriggerBody;

    /// <summary>
    /// Guards <see cref="ResolveDiagnostics"/> against re-stamping. An error
    /// born inside a nested body (procedure / dynamic-SQL batch) is resolved at
    /// its own dispatch frame's catch boundary; as it propagates outward each
    /// enclosing frame must leave the already-resolved line / procedure alone.
    /// </summary>
    private bool diagnosticsResolved;

    /// <summary>
    /// Pre-stamps a known line / procedure and marks this exception resolved so
    /// the enclosing dispatch frame's <see cref="ResolveDiagnostics"/> leaves
    /// it untouched. Used by the <c>THROW;</c> re-raise, which carries the
    /// original error's captured line rather than the re-raising statement's.
    /// </summary>
    internal void PreserveDiagnostics(int line, string? procedure)
    {
        this.diagnosticsResolved = true;
        foreach (var error in this.Errors)
        {
            error.LineNumber = line;
            if (procedure is { Length: > 0 } && error.Procedure.Length == 0)
                error.Procedure = procedure;
        }
    }

    /// <summary>
    /// Stamps the batch-relative line, server, and enclosing-procedure context
    /// onto this exception's <see cref="Errors"/> the first time an enclosing
    /// dispatch frame catches it — the ambient-capture point the static error
    /// factories can't reach. Runs once (subsequent enclosing frames no-op via
    /// <see cref="diagnosticsResolved"/>), so the innermost frame — where the
    /// error was actually born — wins, matching SQL Server's innermost-frame
    /// attribution for nested procedure calls (probe-confirmed).
    /// </summary>
    /// <param name="baseLine">
    /// The line to attribute when an error carries none of its own: the failing
    /// statement's start line for runtime / bind errors, or the parser's
    /// current-token line for syntax errors (severity 15). An error that
    /// already carries a line (a re-raised <c>THROW;</c> preserving the
    /// original) keeps it.
    /// </param>
    /// <param name="lineOffset">
    /// Newline count preceding a procedure body's start within its
    /// <c>CREATE</c> text, added so a body error reports the line relative to
    /// the whole definition (probe-confirmed). Zero for top-level and
    /// dynamic-SQL batches.
    /// </param>
    /// <param name="procedure">
    /// Schema-qualified name of the enclosing procedure body, or empty for
    /// top-level / dynamic-SQL batches.
    /// </param>
    internal void ResolveDiagnostics(int baseLine, int lineOffset, string procedure)
    {
        if (this.diagnosticsResolved)
            return;
        this.diagnosticsResolved = true;
        foreach (var error in this.Errors)
        {
            error.LineNumber = (error.LineNumber == 0 ? baseLine : error.LineNumber) + lineOffset;
            if (procedure.Length != 0 && error.Procedure.Length == 0)
                error.Procedure = procedure;
        }
    }

    /// <summary>
    /// Aggregates the entries gathered while draining a stretch of a batch into
    /// a single exception, mirroring how real SqlClient surfaces every
    /// statement-terminating error of that stretch through one
    /// <c>SqlException.Errors</c> collection. The first entry supplies the
    /// top-level <see cref="Number"/> / <see cref="Class"/> / <see cref="State"/>,
    /// and <c>Message</c> joins every entry's text with
    /// <see cref="Environment.NewLine"/>, as SqlClient builds it
    /// (probed 2026-09-23).
    /// </summary>
    internal static SimulatedSqlException FromErrors(List<SimulatedError> errors)
        => new(string.Join(Environment.NewLine, errors.Select(error => error.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(errors));

    /// <summary>
    /// Collapses several exceptions into one carrying all their entries in
    /// order, followed by <paramref name="messages"/> — the informational
    /// messages the same stretch of the batch sent, which SqlClient appends
    /// after the errors rather than firing (Msg 3621 among them). A lone
    /// exception with no messages is handed back as-is; otherwise the entries
    /// flatten through <see cref="FromErrors"/>. The aggregate inherits its
    /// inputs' diagnostics state, so entries already stamped with a line /
    /// procedure (a module body's, say) aren't re-stamped by an enclosing frame.
    /// </summary>
    internal static SimulatedSqlException Aggregate(List<SimulatedSqlException> errors, List<SimulatedError>? messages = null)
    {
        if (errors.Count == 1 && messages is not { Count: > 0 })
            return errors[0];

        var entries = new List<SimulatedError>(errors.Count + (messages?.Count ?? 0));
        var resolved = true;
        foreach (var error in errors)
        {
            resolved &= error.diagnosticsResolved;
            foreach (var entry in error.Errors)
                entries.Add(entry);
        }
        if (messages is not null)
            entries.AddRange(messages);

        var aggregate = FromErrors(entries);
        aggregate.diagnosticsResolved = resolved;
        return aggregate;
    }

    /// <summary>1-based line number of the first error. Shortcut for <c>Errors[0].LineNumber</c>; mirrors <c>SqlException.LineNumber</c>.</summary>
    public int LineNumber => this.Errors[0].LineNumber;

    /// <summary>Name of the procedure or trigger generating the first error, or empty string. Shortcut for <c>Errors[0].Procedure</c>; mirrors <c>SqlException.Procedure</c>.</summary>
    public string Procedure => this.Errors[0].Procedure;

    /// <summary>Server name carried by the first error. Shortcut for <c>Errors[0].Server</c>; mirrors <c>SqlException.Server</c>.</summary>
    public string Server => this.Errors[0].Server;
}
