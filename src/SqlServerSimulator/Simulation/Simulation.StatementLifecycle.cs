using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// One statement's dispatch lifecycle — begin, enter, run, settle and route an
// error, leave, then send what the statement's ending owes — which
// DispatchFramedStatement drives.
partial class Simulation
{
    /// <summary>
    /// How a dispatched statement ended, which decides what
    /// <see cref="DispatchFramedStatement"/> sends after it.
    /// </summary>
    private enum StatementEnding : byte
    {
        /// <summary>The statement ran without an error.</summary>
        Completed,

        /// <summary>
        /// The error leaves this dispatch frame: an enclosing frame, a module's
        /// caller or the front door handles it.
        /// </summary>
        Propagated,

        /// <summary>
        /// A statement walked in skip mode named something missing, which real
        /// binds only once the statement runs, so the statement is dropped.
        /// </summary>
        Deferred,

        /// <summary>
        /// A binder error joined the report of the module body or batch being
        /// bound (<see cref="BatchContext.CreateTimeBindErrors"/>).
        /// </summary>
        GatheredBindError,

        /// <summary>An enclosing <c>TRY</c> frame caught the error.</summary>
        Caught,

        /// <summary>
        /// The error goes to the client among the batch's outcomes, and the
        /// batch carries on to its next statement unless the error ended it.
        /// </summary>
        Continued,
    }

    /// <summary>
    /// One dispatch of one statement, from its first token to what its ending
    /// sends, in phases: the constructor resets the statement frame and reads
    /// off the leading tokens what the statement's DONE, its statistics and its
    /// Query Store capture need; <see cref="Enter"/> takes what the statement
    /// holds while it runs and <see cref="Leave"/> gives it back;
    /// <see cref="Run"/> runs the statement, with the work real does just
    /// before and after it, and settles and routes the error it raised; then
    /// <see cref="DispatchFramedStatement"/> ends the Query Store capture and
    /// sends what the <see cref="Ending"/> owes, in the order real sends it.
    /// A cross-cutting concern joins the phase whose timing it shares.
    /// </summary>
    /// <remarks>
    /// A local of <see cref="DispatchFramedStatement"/> rather than state on
    /// <see cref="StatementContext"/>: statements nest — an <c>IF</c>'s branch,
    /// a block's body, a procedure's — and every nested statement overwrites
    /// the batch's one statement frame, while what this dispatch reads once
    /// they ran must be its own. A struct, so a statement's dispatch
    /// allocates nothing for it.
    /// </remarks>
    private struct StatementLifecycle
    {
        /// <summary>Where the statement's text starts, for re-reading it.</summary>
        public readonly ParserContext.Checkpoint StatementStart;

        /// <summary>
        /// How many errors a bind had gathered before this statement: a
        /// statement read for its whole report binds its nested ones first,
        /// whose errors follow.
        /// </summary>
        private readonly int gatheredBefore;

        /// <summary>
        /// Whether statements send a DONE of their own on this connection —
        /// only one serving a TDS session renders it — and this one runs.
        /// </summary>
        public readonly bool FramesStatement;

        /// <summary>
        /// The kind the statement names in its own DONE; null when not
        /// <see cref="FramesStatement"/> and for a compound statement, whose
        /// parts send their own.
        /// </summary>
        public readonly ushort? StartDoneKind;

        /// <summary>Whether the statement calls a procedure, which closes with a DONEPROC.</summary>
        public readonly bool IsCall;

        /// <summary>
        /// Whether the statement is a block, an <c>IF</c> or a <c>WHILE</c>,
        /// whose parts own what a failing write reports.
        /// </summary>
        private readonly bool compound;

        /// <summary>
        /// Whether the statement reports its own statistics, which a function
        /// or view body's inline into its caller's.
        /// </summary>
        public readonly bool ReportsStatistics;

        /// <summary>
        /// The DONE kind <c>STATISTICS TIME</c> times the statement as; null
        /// when the option is off or the statement doesn't report.
        /// </summary>
        public readonly ushort? TimedKind;

        /// <summary>
        /// Whether <c>STATISTICS TIME</c> times a procedure call, which reports
        /// after its body.
        /// </summary>
        public readonly bool TimedCall;

        /// <summary>When the statement's <c>STATISTICS TIME</c> clock started.</summary>
        public readonly StatementClock Clock;

        /// <summary>The module a <c>CREATE</c> or <c>ALTER</c> makes, which its time is attributed to.</summary>
        public readonly string? CreatedModule;

        /// <summary>The Query Store capture timing the statement; null when the store won't record it.</summary>
        public QueryStoreCapture? QueryStore;

        /// <summary>The reads <c>STATISTICS IO</c> reports for the statement.</summary>
        public IoStatistics? StatementIo;

        /// <summary>The reads the Query Store capture counts.</summary>
        public IoStatistics? QueryStoreIo;

        /// <summary>How the statement ended; set by <see cref="Run"/>.</summary>
        public StatementEnding Ending;

        /// <summary>The error the statement ended with; null when it completed.</summary>
        public SimulatedSqlException? Error;

        private int? savedThreadId;
        private bool announcedReader;
        private IoStatistics? enclosingIo;
        private FunctionBodyShape? shape;
        private bool opensConditional;
        private bool resumedAtStatementEnd;

        /// <summary>The statement is walked without running under <c>SET NOEXEC ON</c> (<see cref="BatchContext.FramesUnderNoExec"/>).</summary>
        private readonly bool walksUnderNoExec;

        /// <summary>The statement's error closes with a DONE of its own (<see cref="StatementDoneKind.SecurityDdlFailed"/>).</summary>
        private readonly bool isSecurityDdl;

        /// <summary>A bind or compile walk deferred the statement partway through it, so its resume needs checking.</summary>
        private bool deferredMidStatement;

        /// <summary>
        /// The session's <see cref="SimulatedDbConnection.RowSecurityMarks"/> as
        /// the statement began: a count past it when an error settles means the
        /// statement, or one it ran, applied a security predicate.
        /// </summary>
        private readonly int rowSecurityMarks;

        /// <summary>
        /// What the enclosing statement let its scalar function calls inline
        /// into, and how many calls the compile had gathered, when this one
        /// began (see <see cref="InlinedScalarCalls"/>).
        /// </summary>
        private InliningStatement enclosingInlining;
        private int inlinedCallsBefore;

        /// <summary>
        /// The calls gathered as this statement compiles while it runs — one
        /// its batch's compile deferred, or one carrying <c>OPTION
        /// (RECOMPILE)</c> — whose failures go out ahead of what it sends.
        /// </summary>
        public InlinedScalarCalls? CompiledOnRun;

        /// <summary>
        /// Begins the statement at the cursor: resets the statement frame and
        /// reads off its leading tokens what the rest of the lifecycle needs.
        /// </summary>
        public StatementLifecycle(BatchContext batch, bool atBatchStart)
        {
            // An inline join hint in a statement whose own query doesn't
            // settle its hints — an IF's condition, a SET's subquery — sends
            // its Msg 8625 as the next statement begins, still on its own line.
            if (batch.Parser.JoinOrderEnforced)
            {
                batch.Parser.JoinOrderEnforced = false;
                Selection.SendJoinOrderEnforced(batch);
            }

            // Snapshot the statement-start line before parser advance — used as
            // ERROR_LINE() default when an error fires inside this statement.
            batch.CurrentStatement.StartLine = batch.Parser.Token?.LineNumber ?? 1;
            batch.CurrentStatement.StartIndex = batch.Parser.Token?.StartIndex ?? 0;
            if (!batch.IsSkipping)
            {
                batch.CurrentStatement.PriorStatementLine = batch.CountedStatementLine;
                if (batch.Parser.Token is not ReservedKeyword { Keyword: Keyword.Declare } && !AtBareBeginBlock(batch.Parser))
                    batch.CountedStatementLine = batch.CurrentStatement.StartLine;
            }
            this.StatementStart = batch.Parser.SaveCheckpoint();
            this.rowSecurityMarks = batch.Connection.RowSecurityMarks;
            this.gatheredBefore = batch.CreateTimeBindErrors?.Count ?? 0;
            // The string → date-time conversion reads the session's order from
            // here, having no session of its own; an unchanged order republishes
            // for free.
            DateOrder.Current = batch.Connection.DateFormat;
            Language.Current = batch.Connection.Language;
            batch.CurrentStatement.BeginDispatch();
            batch.Parser.JoinHintSites = null;
            batch.CurrentStatement.ChangesTableStructure = ChangesTableStructure(batch.Parser);
            this.FramesStatement = batch.Connection.FramesEveryStatement && (!batch.IsSkipping || batch.FramesUnderNoExec);
            this.walksUnderNoExec = this.FramesStatement && batch.FramesUnderNoExec;
            this.StartDoneKind = this.FramesStatement ? StatementDoneKindOf(batch.Parser) : null;
            this.isSecurityDdl = (this.StartDoneKind is null or StatementDoneKind.NoDone) && IsSecurityDdl(batch.Parser);
            this.IsCall = this.FramesStatement && IsProcedureCall(batch.Parser, atBatchStart);
            this.compound = batch.Parser.Token is ReservedKeyword { Keyword: Keyword.If or Keyword.While }
                || (batch.Parser.Token is ReservedKeyword { Keyword: Keyword.Begin } && StatementDoneKindOf(batch.Parser) is null);
            // SET STATISTICS TIME reports each statement that closes with a DONE,
            // and a procedure call after its body (probed 2026-09-28 against SQL
            // Server 2025); a function or view body inlines into its caller's.
            this.ReportsStatistics = Simulation.ReportsStatistics(batch);
            this.TimedKind = this.ReportsStatistics && batch.Connection.StatisticsTime ? this.StartDoneKind ?? StatementDoneKindOf(batch.Parser) : null;
            this.TimedCall = this.ReportsStatistics && batch.Connection.StatisticsTime && (this.IsCall || IsProcedureCall(batch.Parser, atBatchStart));
            this.Clock = this.TimedKind is not null || this.TimedCall ? StatementClock.Start(batch.Connection) : default;
            this.CreatedModule = this.TimedKind is not null ? CreatedModuleName(batch.Parser) : null;
            batch.CurrentStatement.DoneKind = this.StartDoneKind ?? this.TimedKind ?? StatementDoneKind.NoDone;
            batch.CurrentStatement.DoneCount = -1;
            batch.CurrentStatement.StatementVerb = batch.Parser.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Insert } => "INSERT",
                ReservedKeyword { Keyword: Keyword.Update } => "UPDATE",
                ReservedKeyword { Keyword: Keyword.Delete } => "DELETE",
                ReservedKeyword { Keyword: Keyword.Merge } => "MERGE",
                _ => "SELECT",
            };
            if (!batch.IsSkipping)
            {
                var session = batch.Connection.Session;
                session.CurrentCommand = batch.Parser.Token is ReservedKeyword { Keyword: Keyword.WaitFor }
                    ? "WAITFOR"
                    : batch.CurrentStatement.StatementVerb;
                // Offsets are into the command's own text, so only a top-level
                // statement moves them; a module or dynamic-SQL body runs inside
                // the statement that called it.
                if (batch.Connection.NestingLevel == 0)
                    session.StatementStartIndex = batch.Parser.Token!.StartIndex;
            }
            // READ_COMMITTED_SNAPSHOT readers take a fresh snapshot per statement;
            // clearing here ensures the next statement allocates a new Xid on its
            // first user-table read.
            batch.RcsiStatementSnapshotXid = null;
            // Per-statement stamp bump — establishes a fresh "row" context for
            // NEXT VALUE FOR caching at the statement boundary. Multi-row DML
            // and SELECT iterators bump again per-row, but one-shot statements
            // (SET, DECLARE init, RETURN, scalar SELECT) inherit this baseline
            // bump and don't need to advance the stamp themselves.
            batch.BumpRowStamp();
        }

        /// <summary>
        /// Takes what the statement holds while it runs, which
        /// <see cref="Leave"/> gives back.
        /// </summary>
        /// <remarks>
        /// The connection's <c>CurrentExecutingThreadId</c> is set to the
        /// current managed thread for the statement's duration so concurrent
        /// acquirers on the same thread can short-circuit to Msg 1205 (no
        /// progress is possible while this thread is the executor). Save and
        /// restore handles the nested-body case (proc / trigger / UDF dispatch
        /// enters this lifecycle recursively under the same connection).
        /// </remarks>
        public void Enter(BatchContext batch)
        {
            var connection = batch.Connection;
            this.savedThreadId = connection.CurrentExecutingThreadId;
            connection.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
            // The outermost statement holds row images for its whole run, a
            // nested one's included, so it alone pins the LOB chains retired
            // meanwhile.
            this.announcedReader = connection.Simulation.LobReclamation.Enter(connection.Session);
            // SET STATISTICS IO: the statement gathers its own reads, its caller's
            // put aside until it completes.
            // Query Store times the statement and counts its reads the same way,
            // for a database whose store is recording.
            this.QueryStore = IsQueryStoreCandidate(batch.Parser.Token) ? BeginQueryStoreCapture(batch, io: null) : null;
            this.enclosingIo = connection.StatementIo;
            if (this.ReportsStatistics)
                connection.StatementIo = connection.StatisticsIo || this.QueryStore is not null ? new IoStatistics() : null;
            this.StatementIo = this.ReportsStatistics && connection.StatisticsIo ? connection.StatementIo : null;
            this.QueryStoreIo = this.QueryStore is not null ? connection.StatementIo : null;
            // Function body-shape recording (Msg 455 / 444 / 443) — active only
            // while a scalar UDF's / multi-statement TVF's body binds at CREATE.
            // An IF / WHILE brackets its contained statements so none of them can
            // satisfy the last-statement rule; the nested dispatch has completed by
            // Leave, since Run materializes every outcome.
            this.shape = batch.FunctionBodyShape;
            this.opensConditional = this.shape is not null && NoteFunctionBodyStatement(batch, this.shape);
            var inlining = batch.Parser.Token switch
            {
                ReservedKeyword { Keyword: Keyword.With or Keyword.Create or Keyword.Alter } => InliningStatement.Barred,
                ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge } or Operator { Character: '(' } => InliningStatement.Query,
                _ => InliningStatement.NonQuery,
            };
            if (inlining == InliningStatement.Query && !batch.IsSkipping && batch.InlinedCalls is null && batch.StatementsCompiledOnRun is { } compiledOnRun
                && this.StatementStart.Token is { } first && compiledOnRun.CompilesNow(first.StartIndex))
            {
                batch.InlinedCalls = this.CompiledOnRun = new InlinedScalarCalls(errors: null, body: false);
            }
            if (batch.InlinedCalls is { Body: false } inlined)
            {
                this.enclosingInlining = inlined.Statement;
                this.inlinedCallsBefore = inlined.Calls.Count;
                inlined.Statement = inlining;
            }
            if (this.opensConditional)
                this.shape!.ConditionalDepth++;
        }

        /// <summary>
        /// Gives back what <see cref="Enter"/> took, and the statement-scoped
        /// Sch-S / Sch-M locks, on success, error or a caught exception alike.
        /// </summary>
        private readonly void Leave(BatchContext batch)
        {
            var connection = batch.Connection;
            batch.ReleaseStatementSchemaLocks();
            connection.CurrentExecutingThreadId = this.savedThreadId;
            if (this.announcedReader)
            {
                LobReclamation.Leave(connection.Session);
                Volatile.Write(ref connection.Session.StatementSnapshotXid, long.MaxValue);
            }
            if (this.ReportsStatistics)
                connection.StatementIo = this.enclosingIo;
            if (this.opensConditional)
                this.shape!.ConditionalDepth--;
            if (batch.InlinedCalls is { Body: false } inlined)
            {
                // A query real binds only once it runs, or one told not to,
                // inlines nothing as the batch compiles.
                if (inlined.Statement != InliningStatement.NonQuery)
                {
                    var deferred = this.Ending == StatementEnding.Deferred || batch.CurrentStatement.BindsDeferredSource;
                    if (deferred || batch.CurrentStatement.DisablesScalarUdfInlining)
                        inlined.DropFrom(this.inlinedCallsBefore);
                    // A statement the compile deferred compiles once it runs, as
                    // does one reading a table variable from compatibility
                    // level 150 on (deferred compilation, probed 2026-09-30
                    // against SQL Server 2025: its calls fail twice), and one
                    // carrying OPTION (RECOMPILE) every time it runs.
                    if (inlined.CompilesBatch && this.StatementStart.Token is { } first)
                    {
                        if (deferred)
                        {
                            inlined.CompileOnRun(first.StartIndex, everyRun: false);
                        }
                        else if (inlined.KeepsAnyFrom(this.inlinedCallsBefore))
                        {
                            if (batch.CurrentStatement.Recompiles)
                                inlined.CompileOnRun(first.StartIndex, everyRun: true);
                            else if (batch.CurrentStatement.ReadsTableVariable && batch.CurrentDatabase.CompatibilityLevel >= CompatibilityLevel.Sql150)
                                inlined.CompileOnRun(first.StartIndex, everyRun: false);
                        }
                    }
                }
                inlined.Statement = this.enclosingInlining;
                if (ReferenceEquals(inlined, this.CompiledOnRun))
                    batch.InlinedCalls = null;
            }
        }

        /// <summary>
        /// Runs the statement into <paramref name="outcomes"/>, then
        /// <see cref="Leave"/>s. An error it raises is settled and routed
        /// (<see cref="Ending"/>); anything else propagates.
        /// </summary>
        /// <remarks>
        /// The statement's outcomes are materialized, not yielded, because an
        /// iterator can't catch around a <c>yield</c>. Materialization is cheap
        /// — every statement produces few outcomes and a SELECT already
        /// materializes its rows before yielding its result set — and it fills
        /// <paramref name="outcomes"/> as the statement produces them, so what
        /// a failing statement sent before its error — a body's messages and
        /// result sets ahead of the error that ended it — still reaches the
        /// client first, as real streams it.
        /// </remarks>
        public void Run(Simulation simulation, BatchContext batch, List<SimulatedStatementOutcome> outcomes, bool requireSemicolonBeforeCte, bool atBatchStart)
        {
            var connection = batch.Connection;
            try
            {
                try
                {
                    if (!batch.IsSkipping && batch.Connection.CurrentTransaction is not null && DatabaseDdlInTransaction(batch.Parser) is { } refusal)
                    {
                        // Parsed without running, so the refusal leaves the cursor
                        // past the statement for the recovery scan.
                        batch.SkipModeFlag = true;
                        try
                        {
                            foreach (var _ in simulation.DispatchOneStatementCore(batch, requireSemicolonBeforeCte, atBatchStart))
                            {
                            }
                        }
                        finally
                        {
                            batch.SkipModeFlag = false;
                        }
                        throw refusal;
                    }
                    if (connection.ImplicitTransactions && connection.CurrentTransaction is null && OpensImplicitTransactionAsDdl(batch.Parser))
                        batch.BeginImplicitTransaction();
                    foreach (var outcome in simulation.DispatchOneStatementCore(batch, requireSemicolonBeforeCte, atBatchStart))
                        outcomes.Add(outcome);
                    if (batch.CurrentStatement.PendingCompileRefusal is { } heldRefusal && !batch.CurrentStatement.BindsDeferredSource)
                        throw heldRefusal;
                    // Database-scope DDL triggers fire after the statement's own
                    // work completed but inside its error handling, so a body-side
                    // error surfaces as the statement's (and reaches an enclosing
                    // TRY / CATCH, and trips the Msg 3616 swallowed-error rule).
                    simulation.FireDdlTriggers(batch);
                }
                catch (SimulatedSqlException thrown)
                {
                    // A refusal the walk held back for the statement's sources
                    // was raised ahead of whatever the statement met after it,
                    // which wins unless it defers the statement.
                    if (batch.CurrentStatement.PendingCompileRefusal is { } pending && batch.IsSkipping && !DefersWithItsStatement(batch, thrown))
                        thrown = pending;
                    this.RouteError(batch, this.SettleError(simulation, batch, thrown, requireSemicolonBeforeCte, atBatchStart));
                }
            }
            finally
            {
                this.Leave(batch);
            }
        }

        /// <summary>
        /// Completes an error the statement raised before anything judges where
        /// it goes: the statement's whole binder report, the line and procedure
        /// the error is attributed to, and the rollbacks real performs before
        /// the error reaches anyone.
        /// </summary>
        private SimulatedSqlException SettleError(Simulation simulation, BatchContext batch, SimulatedSqlException thrown, bool requireSemicolonBeforeCte, bool atBatchStart)
        {
            var connection = batch.Connection;

            // After a failed statement, the next OPEN / FETCH to miss its
            // cursor reports its own line (probed 2026-09-29 against SQL
            // Server 2025).
            batch.CountedStatementLine = -1;

            // A binder error is the first of however many the statement
            // carries; real reports them all, so the statement is read
            // again for the whole report before anything below judges it.
            var ex = thrown;
            // The report reads the statement again, gathering its calls afresh.
            if (batch.InlinedCalls is { Body: false } inlined && !thrown.BindReportSettled && BindErrorReport.StartsReport(thrown))
                inlined.TruncateTo(this.inlinedCallsBefore);
            if (!thrown.BindReportSettled)
                (ex, this.resumedAtStatementEnd) = simulation.ReportEveryBindError(batch, thrown, this.StatementStart, requireSemicolonBeforeCte, atBatchStart);

            // A statement that applied a security predicate quotes no value,
            // type or name in a conversion or truncation error, whichever row
            // raised it (probed 2026-10-04 against SQL Server 2025).
            if (connection.RowSecurityMarks != this.rowSecurityMarks && ex.RedactedForRowSecurity() is { } redacted)
                ex = redacted;

            // Stamp the batch-relative line / server / procedure the static
            // factories couldn't know at throw time — the ambient-capture
            // point. Syntax errors (severity 15) report the parser's
            // current-token line; runtime / bind errors report the failing
            // statement's start line. The innermost dispatch frame wins:
            // ResolveDiagnostics no-ops on an already-resolved error as it
            // propagates outward (matching SQL Server's innermost-frame
            // attribution for nested calls).
            // Scalar-UDF / TVF / view bodies inline for attribution: they
            // leave the error unresolved so the enclosing invoking
            // statement's frame stamps it (probe-confirmed — real reports
            // the outer statement's line, no procedure). All other frames
            // (top-level, procedure, trigger, dynamic-SQL) resolve here.
            // A function body's failing write ends the calling statement,
            // which real follows with the Msg 3621 a write earns (probed
            // 2026-09-28 against SQL Server 2025).
            if (batch.SuppressDiagnosticsResolution && batch.CurrentStatement.WritesRows && !batch.IsSkipping)
                ex.EndedFunctionWrite = true;
            if (batch.CalledFunctionBody && batch.UdfFrame is not null && !batch.IsSkipping)
                ex.RaisedRunningFunctionBody = true;
            if (!batch.SuppressDiagnosticsResolution)
            {
                var diagnosticLine = ex.Class == 15 && !ex.RaisedByRaiserror
                    ? batch.Parser.Token?.LineNumber ?? batch.CurrentStatement.StartLine
                    : batch.CurrentStatement.StartLine;
                ex.ResolveDiagnostics(diagnosticLine, batch.LineOffset, batch.ErrorProcedureName);
                if (!ex.RaisingScopeRecorded)
                {
                    ex.RaisingScopeRecorded = true;
                    ex.RaisedByClientSelect = batch.CurrentStatement.SendsRows && !batch.CurrentStatement.WritesRows && !ex.EndedFunctionWrite;
                    // A call of a procedure that doesn't exist (Msg 2812) is
                    // a called procedure's error as far as the status goes,
                    // and counts for nothing (probed 2026-10-02 against SQL
                    // Server 2025).
                    if (!batch.IsSkipping && batch.ProcFrame is { } procFrame && ex.Class > procFrame.MaxErrorSeverity && ex.Number != 2812)
                        procFrame.MaxErrorSeverity = ex.Class;
                }
            }
            // A cancellation is decided by the innermost statement it
            // interrupted outside any function body: whether that statement
            // ended a write decides the Msg 3621 ahead of the attention's
            // acknowledgment.
            if (ex.IsAttention && !ex.AttentionSettled && !batch.SuppressDiagnosticsResolution)
            {
                ex.AttentionSettled = true;
                connection.AttentionEndedWrite = batch.CurrentStatement.WritesRows && !batch.IsSkipping
                    && batch.TriggerFrame is null && !connection.XactAbort;
            }
            // Class 13 = deadlock victim. Real undoes the victim's work and
            // releases its locks before the error reaches anyone; a TRY that
            // will catch it finds the transaction open and doomed, while
            // uncaught it ends the transaction (probed 2026-10-03 against
            // SQL Server 2025).
            if (ex.Class == 13 && connection.CurrentTransaction is { } victim)
            {
                if (connection.OpenTryFrames > 0 && !victim.Doomed)
                    victim.UndoAsDeadlockVictim();
                else
                    victim.EndRollback();
            }
            // The transaction-aborting error class does the same, for the
            // same reason and at the same point: real rolls the whole
            // stack back (@@TRANCOUNT 2 reads 0 afterwards, not 1) before
            // the error reaches anyone. Unlike the deadlock victim it also
            // refuses to be caught, so the TRY-frame arm below skips it.
            if (ex.AbortsTransaction)
                connection.CurrentTransaction?.EndRollback();
            // Real opens an implicit transaction only once its statement
            // compiled, so a compile error met here takes back the one the
            // statement opened.
            if (batch.CurrentStatement.BeganImplicitTransaction && (IsDeferredCompileError(ex) || ex.Number == 201) && connection.CurrentTransaction is { TranCount: 1 } implicitTransaction)
                implicitTransaction.EndRollback();
            // SET XACT_ABORT ON generalizes that class conditionally: while
            // the option is on, a run-time error that would ordinarily end
            // only its own statement ends the batch and rolls the whole
            // stack back instead. It is NOT the same shape as the
            // unconditional class above — a TRY frame anywhere on the
            // session still catches it, and what the transaction gets is
            // the doomed state rather than a rollback. Applied at the
            // innermost frame and marked, so an outer frame re-raising the
            // same exception doesn't ask twice.
            ApplyXactAbortPromotion(connection, ex, batch.CurrentStatement.ChangesTableStructure, IsDeferredCompileError(ex) && !ex.EndedCalledBatch ? batch.TryFrameDepth : 0);
            return ex;
        }

        /// <summary>
        /// Decides where a settled error goes (<see cref="Ending"/>), with what
        /// that decision does at once: a batch it ends, a report it joins,
        /// the <c>@@ROWCOUNT</c> a failed statement leaves.
        /// </summary>
        private void RouteError(BatchContext batch, SimulatedSqlException ex)
        {
            var connection = batch.Connection;
            this.Error = ex;
            // Deferred name resolution: real SQL Server binds object /
            // column names lazily, so an un-taken IF / WHILE branch (or a
            // block skipped after BREAK / CONTINUE / RETURN) that names a
            // nonexistent table or column compiles fine and is discarded.
            // The simulator resolves names inline with parsing, so in skip
            // mode such a failure just means the discarded statement
            // referenced something absent — drop the statement instead of
            // surfacing the error. Checked ahead of the TRY-frame path: a
            // skipped BEGIN TRY body must not activate its CATCH. Only
            // name resolution defers, along with any binder error in a
            // statement that parsed over a missing FROM source — syntax /
            // structural errors carry other numbers and still propagate.
            if (batch.IsSkipping
                && DefersWithItsStatement(batch, ex))
            {
                this.Ending = StatementEnding.Deferred;
                // Real parses the whole batch and binds every statement it
                // doesn't defer, so the walk goes on past a write to a missing
                // target, which the compile pass reads to its end. Any other
                // deferral is raised mid-statement, where EndSkipped's
                // recovery scan may only guess at the statement's end; the
                // walk goes on from a resume it can trust — the statement's
                // separator or the end of the text — and stops on a guess,
                // since walking on from inside the statement would report
                // errors against fragments.
                this.deferredMidStatement = batch.CreateTimeBinding && !batch.CurrentStatement.DeferredReadToEnd;
            }
            else if (batch.CreateTimeBindErrors is { } bindErrors && IsBinderError(ex))
            {
                // A module body reports every binder error it contains, so
                // this one is gathered and the bind resumes at the next
                // statement boundary. Checked ahead of the TRY-frame path
                // because a body's own TRY / CATCH doesn't shield a binder
                // error — binding precedes any of it running
                // (probe-confirmed). Severity 15 is real's parse phase,
                // which preempts the whole report rather than joining it,
                // so those keep propagating from the arm below.
                bindErrors.Insert(Math.Min(this.gatheredBefore, bindErrors.Count), ex);
                this.Ending = StatementEnding.GatheredBindError;

                // An illegal explicit conversion ends the report where it
                // is: real gathers name-resolution errors across the whole
                // body but stops at a Msg 529, so a body whose first
                // statement carries one reports it alone even when a later
                // statement names a missing column (probed 2026-08-05). A
                // MERGE's Msg 5324 is its parser's, so nothing follows it
                // (probed 2026-10-01), and neither does an inline function's
                // Msg 1090 for a table-valued DEFAULT (probed 2026-10-04).
                // A FOR SYSTEM_TIME over a view reading no versioned table
                // (Msg 13544) ends it the same way (probed 2026-10-04), as
                // does a linked server's provider refusing an object's
                // metadata — Msg 7314, 7325, 7357, 9514 (probed 2026-10-05).
                if (ex.Number is 529 or 1090 or 5324 or 7314 or 7325 or 7357 or 8622 or 9514 or 13544 || ex.BindsWithBatch)
                    batch.BatchAborted = true;
            }
            else if (CaughtByTryFrame(batch, ex))
            {
                // Only a batch that runs raises into a TRY frame: an error
                // met while the batch compiles — a syntax error in the TRY
                // body — refuses the whole batch on real rather than
                // reaching the CATCH (probed 2026-09-25 against SQL Server
                // 2025). At run time this arm also absorbs the tail of a
                // statement that failed mid-parse, which the rest of the
                // TRY body skips.
                this.Ending = StatementEnding.Caught;
                // A failed statement leaves @@ROWCOUNT at 0, whatever ran
                // before it (probed 2026-09-27 against SQL Server 2025).
                connection.LastStatementRowCount = 0;
                // Under XACT_ABORT a caught error dooms the transaction, a
                // called batch's syntax error included, and a caught Msg 266
                // — a procedure's or dynamic batch's transaction count left
                // changed — does whatever the option says, where uncaught
                // both leave it committable (probed 2026-10-02 against SQL
                // Server 2025).
                if ((ex.Number == 266 || connection.XactAbort) && connection.CurrentTransaction is { } changed)
                    changed.Doomed = true;
            }
            else if (batch.ContinueOnError && batch.ProcFrame is null && batch.TriggerFrame is null && EndsBatch(ex))
            {
                // Batch-aborting error: a bind-class name-resolution
                // failure (missing object / column / ambiguous / could-not-
                // be-bound), an uncaught THROW (ex.TerminatesBatch), or an
                // error XACT_ABORT ON promoted with no TRY frame to catch
                // it (the transaction has already been rolled back). Real
                // SQL Server ends the batch rather than continuing to the
                // next statement — probe-confirmed that a mid-batch THROW
                // leaves the following statement unrun (contrast a
                // severity-16 RAISERROR, which continues). Emit the one
                // error, then set the flag the dispatch loop breaks on — no
                // cursor recovery scan, so the OPTION (USE HINT(...)) tail's
                // `USE` token is never mis-dispatched as a `USE <database>`
                // statement.
                this.Ending = StatementEnding.Continued;
                batch.BatchAborted = true;
            }
            else if (batch.ContinueOnError && !EndsBatch(ex) && IsStatementTerminating(ex) && !(ex.EndedCalledBatch && ReferenceEquals(ex.EndedCalledBatchIn, batch)) && !ex.EndsInsertExec)
            {
                // A continuing procedure, trigger or dynamic-SQL body takes this arm
                // too, and its error travels up among the body's outcomes;
                // one that ends the batch propagates below instead, so it
                // unwinds every caller it reaches.
                this.Ending = StatementEnding.Continued;
                // A called batch a compile or name-resolution error ended
                // leaves @@ROWCOUNT where its last statement put it (probed
                // 2026-10-02 against SQL Server 2025).
                if (!ex.EndedCalledBatch)
                    connection.LastStatementRowCount = 0;
            }
            else
            {
                // A procedure's, dynamic SQL's, trigger's or called
                // function's batch is as far as a batch-aborting
                // name-resolution error reaches, so a TRY around the call
                // or the firing statement catches it (probed 2026-10-01
                // against SQL Server 2025 for a trigger).
                if ((batch.ProcFrame is not null || batch.TriggerFrame is not null || batch.CalledFunctionBody) && (IsBatchAbortingNameResolution(ex) || IsBulkRefusal(ex)) && !ex.EndedCalledBatch)
                {
                    ex.EndedCalledBatch = true;
                    ex.EndedCalledBatchIn = batch;
                }
                this.Ending = StatementEnding.Propagated;
            }
        }

        /// <summary>
        /// What goes out ahead of a <see cref="StatementEnding.Propagated"/>
        /// error, which the caller then throws.
        /// </summary>
        public readonly IEnumerable<SimulatedStatementOutcome> EndPropagated(BatchContext batch, List<SimulatedStatementOutcome> outcomes)
        {
            var connection = batch.Connection;
            var propagated = this.Error!;
            // A module or dynamic batch's failing statement closes with its
            // DONE at its own level when a TRY frame further out is what
            // catches the error — a write with its count — as a caught error's
            // statement does (probed 2026-09-28 against SQL Server 2025).
            var caughtFurtherOut = !batch.IsSkipping && !batch.CreateTimeBinding && connection.OpenTryFrames > 0
                && !propagated.AbortsTransaction && !propagated.IsAttention;
            var count = caughtFurtherOut && !this.compound ? CaughtWriteCount(batch) : null;
            if (this.StartDoneKind is not null)
                _ = FrameStatement(batch, outcomes, standInForNone: this.FramesStatement && caughtFurtherOut && count is null && !this.IsCall);
            foreach (var outcome in ProducedOutcomes(batch, outcomes))
                yield return outcome;
            if (count is not null)
            {
                count.DoneKind = batch.CurrentStatement.DoneKind;
                yield return count;
            }
        }

        /// <summary>
        /// Moves <paramref name="parser"/>, left wherever the statement's error
        /// interrupted it, to the next statement boundary — skipping the
        /// boundary-like words that are the statement's own: those a
        /// parenthesized expression or a <c>CASE</c> holds (its <c>ELSE</c> and
        /// <c>END</c>, a subquery's <c>SELECT</c>), an <c>UPDATE</c>'s
        /// <c>SET</c>, an <c>INSERT</c>'s source, the statement a common table
        /// expression introduces, a set operator's next <c>SELECT</c>, a
        /// cursor's query, and everything up to a <c>MERGE</c>'s closing
        /// semicolon. The words before the interrupted token are read from the
        /// statement's start for that: a lock wait timing out at an
        /// <c>UPDATE</c>'s target left the cursor before its <c>SET</c> list,
        /// where the scan once stopped, and the rest of the batch parsed from
        /// there.
        /// </summary>
        private readonly void SkipToNextStatement(ParserContext parser)
        {
            if (parser.Token is not { } interrupted)
                return;
            var resumeAt = interrupted.StartIndex;
            var parens = 0;
            var cases = 0;
            parser.RestoreCheckpoint(this.StatementStart);
            var kind = parser.Token is ReservedKeyword { Keyword: var first } ? first : (Keyword?)null;
            var clauseTaken = false;
            Token? previous = null;
            while (parser.Token is { } token)
            {
                if (previous is not null && parens <= 0 && cases <= 0 && token is ReservedKeyword { Keyword: var word } && IsStatementBoundary(token))
                {
                    var owned = kind switch
                    {
                        Keyword.Merge => true,
                        Keyword.With when word is Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge => true,
                        Keyword.Update => word == Keyword.Set && !clauseTaken,
                        Keyword.Insert => word is Keyword.Select or Keyword.Exec or Keyword.Execute or Keyword.With && !clauseTaken,
                        _ => false,
                    } || (word == Keyword.Select && previous is ReservedKeyword { Keyword: Keyword.Union or Keyword.All or Keyword.Except or Keyword.Intersect or Keyword.For });
                    if (owned)
                    {
                        if (kind == Keyword.With)
                            kind = word;
                        else
                            clauseTaken = true;
                    }
                    else if (token.StartIndex >= resumeAt)
                    {
                        return;
                    }
                }
                else if (token.StartIndex >= resumeAt && parens <= 0 && cases <= 0 && IsStatementBoundary(token) && (previous is not null || token is not ReservedKeyword))
                {
                    return;
                }
                switch (token)
                {
                    case Operator { Character: '(' }:
                        parens++;
                        break;
                    case Operator { Character: ')' }:
                        parens--;
                        break;
                    case ReservedKeyword { Keyword: Keyword.Case } when parens <= 0:
                        cases++;
                        break;
                    case ReservedKeyword { Keyword: Keyword.End } when parens <= 0 && cases > 0:
                        cases--;
                        break;
                    case ReservedKeyword { Keyword: Keyword.Values } when kind == Keyword.Insert && parens <= 0:
                        // A VALUES list is the INSERT's source; a SELECT after
                        // it starts the next statement.
                        clauseTaken = true;
                        break;
                    default:
                        break;
                }
                previous = token;
                parser.MoveNextOptional();
            }
        }

        /// <summary>
        /// Ends a <see cref="StatementEnding.Deferred"/> statement or one whose
        /// error a bind gathered: resumes at the next statement boundary and
        /// sends what the statement produced before it failed.
        /// </summary>
        public readonly IEnumerable<SimulatedStatementOutcome> EndSkipped(BatchContext batch, List<SimulatedStatementOutcome> outcomes)
        {
            // The parser threw mid-statement; advance to the next statement
            // boundary so the outer dispatch loop resumes cleanly (same
            // cursor-recovery scan the TRY-caught path uses). No
            // @@ERROR / InFlightError mutation — a skipped statement is
            // conceptually never compiled, not run-and-failed, and a gathered
            // bind error belongs to the CREATE the bind serves.
            // A bind's re-read that reached the statement's end, or a deferred
            // statement read to its end, left the cursor at the next
            // statement, which may open with a `(`.
            var parser = batch.Parser;
            if (!(parser.Token is Operator { Character: '(' } paren && (this.resumedAtStatementEnd || parser.StatementEndedOnParen == paren.StartIndex)))
                this.SkipToNextStatement(parser);
            // A scan that stopped on a separator (or ran out of body) resumed
            // where a statement really begins; one that stopped on a keyword
            // guessed, and the bind reads a later severity-15 error from a
            // guessed position as recovery noise.
            var resumedCleanly = this.resumedAtStatementEnd || parser.Token is null or Operator { Character: ';' };
            if (this.Ending == StatementEnding.GatheredBindError)
                batch.BindResumedCleanly = resumedCleanly;
            else if (this.deferredMidStatement && !resumedCleanly)
                batch.BatchAborted = true;
            foreach (var outcome in ProducedOutcomes(batch, outcomes))
                yield return outcome;
        }

        /// <summary>
        /// Sends a <see cref="StatementEnding.Continued"/> error to the client
        /// with what the statement produced ahead of it and the statistics,
        /// notices and closed scopes that follow it.
        /// </summary>
        public readonly IEnumerable<SimulatedStatementOutcome> EndContinued(BatchContext batch, List<SimulatedStatementOutcome> outcomes)
        {
            var connection = batch.Connection;
            var continuedError = this.Error!;
            // Top-level statement-terminating continuation: the statement
            // failed but the batch proceeds to the next one (real SQL Server's
            // default, non-XACT_ABORT severity model). Set @@ERROR so a
            // following statement observes it, but do NOT touch InFlightError /
            // ErrorSignaled — those are TRY/CATCH-only state, and this error is
            // bound for the client, not a CATCH block. Same cursor-recovery
            // scan the deferred-name and TRY-caught paths use, then emit the
            // error into the shared outcome stream so both front doors render
            // it (the wire writes error token(s); the in-process reader
            // converts it to a throw); the outer dispatch loop resumes at the
            // next statement.
            connection.LastErrorNumber = continuedError.AtAtErrorNumber;
            // A batch-aborting error skips the cursor-recovery scan: the outer
            // dispatch loop breaks on BatchAborted, so the cursor position no
            // longer matters and scanning could only mis-stop on a keyword-like
            // token inside the failed statement's own tail.
            if (!batch.BatchAborted)
                this.SkipToNextStatement(batch.Parser);
            // The error closes with its statement's DONE — or, when it ended
            // the batch, with the batch's closing one, and when it ended a
            // procedure call, with that call's DONEPROC (probed 2026-09-28
            // against SQL Server 2025).
            var errorOutcome = new SimulatedErrorOutcome(continuedError);
            List<SimulatedStatementOutcome>? closedScopes = null;
            if (this.FramesStatement)
            {
                if (this.StartDoneKind is not null)
                    _ = FrameStatement(batch, outcomes, standInForNone: false);
                if (batch.BatchAborted)
                {
                    errorOutcome.DoneKind = StatementDoneKind.Batch;
                }
                else if (this.IsCall && continuedError.RaisedBySystemProcedure && OpenProcScopes(outcomes) == 1)
                {
                    errorOutcome.DoneKind = StatementDoneKind.RaisError;
                    closedScopes = [ScopeExit(batch, continuedError.SystemProcedureReturnCode)];
                }
                else if (this.IsCall || OpenProcScopes(outcomes) > 0)
                {
                    errorOutcome.DoneKind = StatementDoneKind.ClosedByScope;
                    closedScopes = [];
                    CloseAbandonedProcScopes(batch, [.. outcomes], closedScopes, this.IsCall, endedByError: true);
                }
                else
                {
                    errorOutcome.DoneKind = batch.CurrentStatement.DoneKind is not StatementDoneKind.NoDone ? batch.CurrentStatement.DoneKind
                        : this.isSecurityDdl ? StatementDoneKind.SecurityDdlFailed
                        : StatementDoneKind.Batch;
                }
                errorOutcome.InModule = batch.ProcFrame is not null || batch.TriggerFrame is not null;
                errorOutcome.TransactionEventMark = connection.TransactionEventsRecorded;
            }
            // A statement compiled before its error ran into it.
            if (this.TimedKind is not null && !batch.BatchAborted && connection.StatisticsTime && (batch.CurrentStatement.CallsUserFunction || CompilesParameterized(batch, this.StatementStart)))
                yield return new SimulatedInfoOutcome(CompileTime(batch, clock: null, batch.CurrentStatement.StartLine + batch.LineOffset, batch.ErrorProcedureName));
            foreach (var outcome in ProducedOutcomes(batch, outcomes))
                yield return outcome;
            yield return errorOutcome;
            if (continuedError.FollowingMessage is { } following)
                yield return new SimulatedInfoOutcome(following, followsRows: true);
            // A statement its error ended still reports its time, after the
            // error and ahead of Msg 3621; one that ended the batch doesn't
            // (probed 2026-09-28 against SQL Server 2025).
            if ((this.TimedKind is not null || this.TimedCall) && !batch.BatchAborted && connection.StatisticsTime)
                yield return new SimulatedInfoOutcome(ExecutionTimes(batch, this.Clock, this.CreatedModule), followsRows: true);
            // Msg 3621 goes out ahead of the statement's DONE, as real sends it.
            if (IsStatementTerminationNoticed(batch, continuedError))
            {
                yield return new SimulatedInfoOutcome(
                    continuedError.IsIdentityOverflow
                        ? SimulatedSqlException.ArithmeticOverflowOccurredMessage(batch)
                        : SimulatedSqlException.StatementTerminatedMessage(batch, continuedError),
                    followsRows: true);
            }
            else if (continuedError.IsIdentityOverflow && batch.BatchAborted)
            {
                // The overflow ends the batch and rolls back, and its Msg 3606
                // then names no statement: line 1, outside any module (probed
                // 2026-09-28 against SQL Server 2025).
                var notice = SimulatedSqlException.ArithmeticOverflowOccurredMessage(batch);
                notice.LineNumber = 1;
                notice.Procedure = string.Empty;
                yield return new SimulatedInfoOutcome(notice);
            }
            if (closedScopes is not null)
            {
                foreach (var closed in closedScopes)
                    yield return closed;
            }
        }

        /// <summary>
        /// Hands a <see cref="StatementEnding.Caught"/> error to its
        /// <c>CATCH</c> block and sends what the failed statement still owes
        /// the client: its outcomes, its DONE, the count a write reports and
        /// its time.
        /// </summary>
        public readonly IEnumerable<SimulatedStatementOutcome> EndCaught(BatchContext batch, List<SimulatedStatementOutcome> outcomes)
        {
            var connection = batch.Connection;
            var caught = this.Error!;
            var caughtCount = batch.IsSkipping ? null : CaughtWriteCount(batch);
            // First error in this TRY body wins; subsequent throws while
            // already-signaled (from skip-mode parsers that still hit
            // runtime errors) silently swallow — the captured first error
            // is what CATCH sees.
            if (!batch.ErrorSignaled)
            {
                // The exception's diagnostics were resolved by SettleError,
                // so ERROR_LINE() / ERROR_PROCEDURE() report the same
                // values the exception carries (probe-confirmed parity).
                // An error raised as several — a constraint failure and its
                // Msg 1750, a CREATE SCHEMA failure and its Msg 2759 — is the
                // last of them to ERROR_NUMBER() and its siblings, as on real
                // (probed 2026-09-24 against SQL Server 2025); a compile-time
                // report is its first.
                var shown = caught.CatchReadsFirstEntry ? caught.Errors[0] : caught.Errors[^1];
                batch.InFlightError = new CaughtError(
                    shown.Number,
                    shown.Message,
                    shown.Class,
                    shown.State,
                    shown.LineNumber,
                    shown.Procedure.Length == 0 ? null : shown.Procedure,
                    caught.CatchReadsFirstEntry || caught.Errors.Count < 2 ? null : [.. caught.Errors.SkipLast(1)]);
                batch.ErrorSignaled = true;
            }
            connection.LastErrorNumber = caught.AtAtErrorNumber;
            // Any error of severity >= 11 raised while a trigger body runs
            // aborts the firing statement at trigger exit (Msg 3616) even
            // though this CATCH swallowed it — real doesn't let a body's own
            // TRY / CATCH rescue the statement that fired it (probe-confirmed,
            // including for a module the body called; severity <= 10 is
            // informational and leaves the unit intact). A body that turned
            // XACT_ABORT off first keeps the error from dooming anything, and
            // the statement stands (probed 2026-09-28 against SQL Server 2025).
            if (connection.TriggerNestLevel > 0 && caught.Class >= 11 && connection.XactAbort)
                connection.TriggerBodyErrorRaised = true;

            // The parser threw mid-statement, so the cursor is at an
            // unpredictable position. Advance to the next statement boundary
            // (a `;`, statement-starting keyword like `END`, or EOB) so the
            // outer DispatchStatementsUntil loop can resume cleanly — without
            // this scan it'd re-dispatch the same partially-parsed statement
            // and infinite-loop. IsStatementBoundary treats `END` as a stop,
            // so we land at `END TRY` for the typical case.
            this.SkipToNextStatement(batch.Parser);
            // A caught error's statement still closes with its DONE, the error
            // bit clear — a procedure call with its DONEPROC, no return status
            // (probed 2026-09-28 against SQL Server 2025).
            if (this.FramesStatement)
            {
                if (this.IsCall || OpenProcScopes(outcomes) > 0)
                {
                    CloseAbandonedProcScopes(batch, [.. outcomes], outcomes, this.IsCall, endedByError: false);
                }
                else if (this.StartDoneKind is not null)
                {
                    _ = caughtCount?.DoneKind = batch.CurrentStatement.DoneKind;
                    _ = FrameStatement(batch, outcomes, standInForNone: caughtCount is null);
                }
            }
            // The statement's NOCOUNT governs its own count, as a completed
            // statement's does — a trigger body's, though the body's SET reverts
            // before the firing statement sends what it buffered (probed
            // 2026-10-06 against SQL Server 2025).
            foreach (var outcome in outcomes)
                outcome.CountSuppressed ??= connection.NoCount;
            foreach (var outcome in ProducedOutcomes(batch, outcomes))
                yield return outcome;
            if (caughtCount is not null)
                yield return caughtCount;
            if ((this.TimedKind is not null || this.TimedCall) && connection.StatisticsTime)
                yield return new SimulatedInfoOutcome(ExecutionTimes(batch, this.Clock, this.CreatedModule));
        }

        /// <summary>
        /// Settles a <see cref="StatementEnding.Completed"/> statement's
        /// <c>@@ERROR</c>, gives its outcomes their DONE, and stamps on each
        /// what the session was when the statement produced it.
        /// </summary>
        public readonly void Complete(BatchContext batch, List<SimulatedStatementOutcome> outcomes)
        {
            var connection = batch.Connection;
            // Skip-mode statements don't count toward @@ERROR reset (skip-mode
            // dispatch is conceptually "didn't run" — the surrounding scope owns
            // @@ERROR). Successful real statements clear @@ERROR to 0 unless the
            // statement explicitly opted out (RAISERROR sev ≤ 10 WITH SETERROR
            // wrote its own number and asked us not to clobber it).
            if (!batch.IsSkipping && !batch.CurrentStatement.SuppressErrorReset)
                connection.LastErrorNumber = 0;

            // Stamp the session TEXTSIZE in effect when this statement produced
            // its rows: client-boundary cursors truncate under the producing
            // statement's cap even when the session value changes before the
            // (lazily-read) result is drained — notably a proc body's SET
            // TEXTSIZE, which reverts at proc exit while its result sets keep it.
            // Stamped in the same walk: the producing statement's line and
            // enclosing procedure, which a downstream projection (EXEC … WITH
            // RESULT SETS) attributes its errors to. Already-stamped results pass
            // through untouched so the innermost producing frame wins.
            if (this.walksUnderNoExec)
                FrameUnranStatement(batch, outcomes, this.StatementStart, this.IsCall);
            else if (this.StartDoneKind is not null)
                _ = FrameStatement(batch, outcomes, standInForNone: true);
            foreach (var o in outcomes)
            {
                // SET NOCOUNT ON suppresses the statement's count wherever a client
                // reads one. Recorded per outcome rather than read at consumption
                // time because a procedure body's SET NOCOUNT reverts when the body
                // exits, which is before the caller pulls the outcomes the body
                // produced; the null-coalescing assignment keeps the innermost
                // producing frame's setting for the same reason the stamps below
                // do.
                o.CountSuppressed ??= connection.NoCount;
                if (o is not SimulatedQueryResult query)
                    continue;
                if (connection.TextSize >= 0)
                    query.ClientTextSize = connection.TextSize;
                if (query.OriginLine == 0)
                {
                    query.OriginLine = batch.CurrentStatement.StartLine + batch.LineOffset;
                    query.OriginProcedure = batch.ErrorProcedureName;
                }
            }
        }
    }
}
