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

    /// <summary>A copy of a freshly made exception that ends its batch, as <see cref="TerminatesBatch"/> describes.</summary>
    internal SimulatedSqlException EndingBatch() => new(this.Message, [.. this.Errors]) { TerminatesBatch = true };

    /// <summary>
    /// What <c>@@ERROR</c> reads after this error, where that isn't its
    /// number: a <c>RAISERROR</c> of an unregistered id at severity 11 and up,
    /// or <c>WITH SETERROR</c>, reports Msg 18054 and leaves <c>@@ERROR</c>
    /// at the id it asked for, while <c>ERROR_NUMBER()</c> reads 18054
    /// (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    internal int AtAtErrorNumber => this.atAtErrorOverride ?? this.Number;

    private int? atAtErrorOverride;

    /// <summary>This exception, with <c>@@ERROR</c> reading its last entry's number rather than its first.</summary>
    internal SimulatedSqlException ReportingLastToAtAtError()
    {
        this.atAtErrorOverride = this.Errors[^1].Number;
        return this;
    }

    /// <summary>
    /// An informational message that goes out right after this error when it
    /// ends only its statement — a procedure's <c>RETURN</c> whose value
    /// failed sends Msg 282 for the NULL status it returns instead.
    /// </summary>
    internal SimulatedError? FollowingMessage { get; set; }

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
    /// Refines <see cref="AbortsAsUnderXactAbort"/>: uncaught, the error still
    /// ends the batch, but the transaction is left doomed rather than rolled
    /// back, so the batch's end rolls it back with Msg 3998. Probe-confirmed
    /// against SQL Server 2025 (2026-10-02) for a memory-optimized write
    /// conflict (Msg 41302).
    /// </summary>
    internal bool DoomsWhenUncaught { get; private init; }

    /// <summary>
    /// When <see langword="true"/>, this is a cancellation (a
    /// <c>CommandTimeout</c> expiry or a caller's cancel) observed inside a
    /// running statement rather than between two. It ends the batch and no
    /// <c>BEGIN TRY</c> frame catches it, as an attention does on real, while
    /// the statement it interrupted rolls back like any failed statement.
    /// Internal — never part of the public <c>SqlException</c>-shaped surface.
    /// </summary>
    internal bool IsAttention { get; private init; }

    /// <summary>
    /// Set once the innermost statement-level frame an <see cref="IsAttention"/>
    /// error reached has decided whether the attention ended a write (see
    /// <see cref="SimulatedDbConnection.AttentionEndedWrite"/>), so a frame
    /// further out re-raising it doesn't decide again. A function body leaves
    /// it to the statement that called it, as real attributes the function's
    /// work to that statement.
    /// </summary>
    internal bool AttentionSettled;

    /// <summary>
    /// An identity value past its column's type, which real follows with the
    /// class-0 Msg 3606 (<c>Arithmetic overflow occurred.</c>) where another
    /// error ending a write is followed by Msg 3621 (probed 2026-09-25 against
    /// SQL Server 2025).
    /// </summary>
    internal bool IsIdentityOverflow { get; private init; }

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
    /// Set when the error was raised computing a row's select-list value in a
    /// pipelined projection — where real computes it after an <c>INSERT</c>
    /// has drawn that row's identity value, so the failing row uses one up —
    /// and cleared again by any operator real evaluates ahead of the draw: a
    /// filter, a sort key, a <c>DISTINCT</c>, an aggregate, a constant scan
    /// (probed 2026-10-04 against SQL Server 2025). Read by
    /// <c>INSERT … SELECT</c> alone.
    /// </summary>
    internal bool RaisedInRowProjection;

    /// <summary>
    /// The batch <see cref="EndedCalledBatch"/> was set in: within that batch
    /// the error still ends everything it passes through — a <c>BEGIN … END</c>
    /// block around the failing statement included — and only its caller
    /// carries on.
    /// </summary>
    internal object? EndedCalledBatchIn;

    /// <summary>
    /// Set on the refusal of a result set an <c>INSERT … EXEC</c> target's
    /// columns can't take, which ends every batch between it and the
    /// <c>INSERT</c>, as an error that ends only its statement doesn't.
    /// </summary>
    internal bool EndsInsertExec;

    /// <summary>
    /// Set when a write in a function body raised this error — a
    /// multi-statement function filling its return table — which ends the
    /// calling statement as a write of its own would, Msg 3621 following it.
    /// </summary>
    internal bool EndedFunctionWrite;

    /// <summary>
    /// Set when a scalar function's body raised this error as it ran, which
    /// makes a missing object there (Msg 208) an execution error of the
    /// calling statement rather than its compile's: a write it ends earns
    /// Msg 3621 (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal bool RaisedRunningFunctionBody;

    /// <summary>
    /// Set on a <c>DECLARE</c>'s missing or oversized type, which real meets
    /// parsing the batch rather than binding it: a batch or module body
    /// carrying one reports every such error and none of its binder errors
    /// (probed 2026-09-30 against SQL Server 2025). See
    /// <see cref="DropBinderErrorsBehindDeclarations"/>.
    /// </summary>
    internal bool PreemptsBinderErrors;

    /// <summary>
    /// Leaves <paramref name="errors"/> as real reports them: when one preempts
    /// the binder's (<see cref="PreemptsBinderErrors"/>), only those and the
    /// Msg 1087 of a table variable used without a declaration, which real
    /// meets parsing too (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal static void DropBinderErrorsBehindDeclarations(List<SimulatedSqlException> errors)
    {
        if (errors.Exists(static error => error.PreemptsBinderErrors))
            _ = errors.RemoveAll(static error => !error.PreemptsBinderErrors && error.Number != 1087);
    }

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
    /// Set with <see cref="RaisingScopeRecorded"/>: whether the statement that
    /// raised this error is a <c>SELECT</c> sending rows to the client and
    /// writing none. An error escaping a trigger body from one is followed by
    /// a Msg 3621 at that statement's line and module; from any other — a
    /// write, an assignment, <c>SET</c>, <c>DECLARE</c>'s initializer, an
    /// <c>IF</c> condition — at line 1 outside any (probed 2026-10-06 against
    /// SQL Server 2025).
    /// </summary>
    internal bool RaisedByClientSelect;

    /// <summary>
    /// Set on a refusal real settles compiling a statement — a DML
    /// <c>TOP</c>'s of its written constant, a nested <c>NEXT VALUE FOR</c> —
    /// raised as a statement the batch's compile deferred runs: it ends the
    /// batch uncaught by a TRY in its scope, and with no Msg 3621 where a
    /// <c>TOP</c> value read while running sends it (probed 2026-10-06 against
    /// SQL Server 2025).
    /// </summary>
    internal bool RefusedRecompilingDeferred;

    /// <summary>
    /// Set on a conversion error raised while <c>ALTER TABLE … ALTER COLUMN</c>
    /// rewrites the column's values, which real follows with Msg 3621 though
    /// the error ends the batch, where the same error from an INSERT or UPDATE
    /// gets none (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal bool EndedColumnRewrite;

    /// <summary>
    /// Set once the innermost dispatch frame has asked whether this error's
    /// statement carries more binder errors (<c>Simulation.ReportEveryBindError</c>),
    /// so an enclosing frame it propagates through doesn't ask again.
    /// </summary>
    internal bool BindReportSettled;

    /// <summary>
    /// Set on a compile-time report — a statement's binder errors, a batch's
    /// — whose <em>first</em> entry is what a <c>CATCH</c> reads through
    /// <c>ERROR_NUMBER()</c> and its siblings, where an error raised as several
    /// at run time shows its last (probed 2026-09-27 against SQL Server 2025:
    /// <c>EXEC('SELECT x1, x2 FROM t')</c> inside <c>TRY</c> reports x1, and
    /// so does a two-statement dynamic batch).
    /// </summary>
    internal bool CatchReadsFirstEntry;

    /// <summary>
    /// Guards <see cref="ResolveDiagnostics"/> against re-stamping. An error
    /// born inside a nested body (procedure / dynamic-SQL batch) is resolved at
    /// its own dispatch frame's catch boundary; as it propagates outward each
    /// enclosing frame must leave the already-resolved line / procedure alone.
    /// </summary>
    private bool diagnosticsResolved;

    /// <summary>
    /// Set on the Msg 6522 a CLR type's <c>Parse</c> raises converting a
    /// string, which a compile-time fold restates (see
    /// <see cref="ClrTypeParseFoldedAtCompile"/>).
    /// </summary>
    internal bool IsClrTypeParseFailure;

    /// <summary>
    /// Set on the error that ends the session (a <c>RAISERROR … WITH LOG</c> at
    /// severity 20 or more), whose last entry is the severity-20 Msg 0 SqlClient
    /// adds on its own side when the server severs the connection — on the
    /// wire, real sends the entries before it, Msg 596 at line 0 and in no
    /// procedure, and a DONE carrying <c>DONE_SRVERROR</c> (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal bool EndsSession;

    /// <summary>
    /// Set on an error whose only entry is the class-11 Msg 0 SqlClient raises
    /// on its own side when a request's DONE carries <c>DONE_ERROR</c> with no
    /// error ahead of it: on the wire real sends that DONE alone (probed
    /// 2026-10-06 against SQL Server 2025, a refused <c>SHUTDOWN</c>).
    /// </summary>
    internal bool RaisedByClient;

    /// <summary>
    /// Set on a non-schema-bound security predicate's binding failure met
    /// while the batch compiles: its leading Msg 208 doesn't defer with its
    /// statement as a missing object's does, and it ends the compile's report
    /// (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal bool BindsWithBatch;

    /// <summary>A copy of this error as the batch's compile meets it, marked <see cref="BindsWithBatch"/> and ending no batch of its own.</summary>
    internal SimulatedSqlException BindingWithBatch() => new(this.Message, [.. this.Errors]) { BindsWithBatch = true };

    /// <summary>
    /// This error as a linked server raised it evaluating a remote
    /// <c>UPDATE</c>'s <c>SET</c> list and the provider relayed it: at line 1
    /// of the server's statement, ending the caller's batch, behind the Msg
    /// 3621 the server sent first when the error ended only its statement
    /// there (probed 2026-10-05 and 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal SimulatedSqlException RelayedFromRemoteUpdate(Parser.BatchContext batch)
    {
        if (this.Number is 220 or 232 or 515 or 517 or 8115 or 8134)
        {
            var terminated = batch.InfoMessage(@class: 0, state: 1, number: 3621, "The statement has been terminated.");
            terminated.LineNumber = 1;
            batch.Connection.PendingMessages.Enqueue(terminated);
        }
        return new SimulatedSqlException(this.Message, [.. this.Errors]) { TerminatesBatch = true }.PinLine(1);
    }

    /// <summary>
    /// Set on an error a system procedure raised from its own body, which real
    /// reports as the procedure's <c>RAISERROR</c>: a DONEINPROC carrying the
    /// error, then the procedure returning 1 with no error on its DONEPROC
    /// (probed 2026-09-28 against SQL Server 2025, <c>sp_help</c> of a missing
    /// object).
    /// </summary>
    internal bool RaisedBySystemProcedure;

    /// <summary>
    /// The return code a system procedure gives the <c>EXEC @rc = …</c> caller,
    /// and sends as its RETURNSTATUS, when this error ends it: 1 for most, the
    /// error number itself for the <c>sys.sp_*</c> option procedures and
    /// <c>sp_recompile</c> (probed 2026-09-30 and 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    internal int SystemProcedureReturnCode = 1;

    /// <summary>Set on a refusal binding a system procedure's arguments, which leaves the caller's return code unset.</summary>
    internal bool SystemProcedureBindingError;

    /// <summary>
    /// The line of a system procedure's own source a refusal is raised at, set
    /// where one procedure raises the same number from several lines.
    /// </summary>
    internal int? SystemProcedureLine;

    /// <summary>Sets <see cref="SystemProcedureLine"/> and returns this exception.</summary>
    internal SimulatedSqlException AtSystemProcedureLine(int line)
    {
        this.SystemProcedureLine = line;
        return this;
    }

    /// <summary>Set by <see cref="PinLine"/>: the line holds wherever the error is caught.</summary>
    private bool linePinned;

    /// <summary>
    /// Gives this exception a fixed line that no enclosing frame's statement
    /// line or body offset replaces — for an error real reports at the same
    /// line wherever it is raised — while the procedure is still attributed.
    /// With <paramref name="keepStamped"/>, an entry already stamped with a
    /// line of its own keeps it.
    /// </summary>
    internal SimulatedSqlException PinLine(int line, bool keepStamped = false)
    {
        this.linePinned = true;
        foreach (var error in this.Errors)
        {
            if (!keepStamped || error.LineNumber == 0)
                error.LineNumber = line;
        }
        return this;
    }

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
            if (!this.linePinned)
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

    /// <summary>
    /// The exception a client reads for a stretch of a batch: as
    /// <see cref="Aggregate"/>, except that an informational entry riding with
    /// an error (Msg 2724 after a parameter's Msg 2715) moves behind every
    /// error, because SqlClient collects the errors first and the warnings
    /// after them. The TDS stream keeps the order real sends.
    /// </summary>
    internal static SimulatedSqlException ForClient(List<SimulatedSqlException> errors, List<SimulatedError>? messages)
    {
        var sawInfo = false;
        var misordered = false;
        foreach (var error in errors)
        {
            foreach (var entry in error.Errors)
            {
                if (entry.Class <= 10)
                    sawInfo = true;
                else
                    misordered |= sawInfo;
            }
        }
        if (!misordered)
            return Aggregate(errors, messages);

        var entries = new List<SimulatedError>();
        var resolved = true;
        foreach (var error in errors)
        {
            resolved &= error.diagnosticsResolved;
            foreach (var entry in error.Errors)
            {
                if (entry.Class > 10)
                    entries.Add(entry);
            }
        }
        foreach (var error in errors)
        {
            foreach (var entry in error.Errors)
            {
                if (entry.Class <= 10)
                    entries.Add(entry);
            }
        }
        if (messages is not null)
            entries.AddRange(messages);
        var aggregate = FromErrors(entries);
        aggregate.diagnosticsResolved = resolved;
        return aggregate;
    }

    /// <summary>
    /// A fresh copy of this single-entry error, for a report that sends the
    /// same error twice — each entry takes its own line.
    /// </summary>
    internal SimulatedSqlException CopyOfError() => new(this.Errors[0].Message, this.Number, this.Class, this.State);

    /// <summary>1-based line number of the first error. Shortcut for <c>Errors[0].LineNumber</c>; mirrors <c>SqlException.LineNumber</c>.</summary>
    public int LineNumber => this.Errors[0].LineNumber;

    /// <summary>Name of the procedure or trigger generating the first error, or empty string. Shortcut for <c>Errors[0].Procedure</c>; mirrors <c>SqlException.Procedure</c>.</summary>
    public string Procedure => this.Errors[0].Procedure;

    /// <summary>Server name carried by the first error. Shortcut for <c>Errors[0].Server</c>; mirrors <c>SqlException.Server</c>.</summary>
    public string Server => this.Errors[0].Server;
}
