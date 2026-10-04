using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-batch runtime state. One <see cref="BatchContext"/> is constructed
/// per command execution by <see cref="Simulation.CreateResultSetsForCommand"/>;
/// it owns the <see cref="ParserContext"/> that walks the command's tokens
/// and the runtime state both parsing and execution mutate (variable slots,
/// undo log). Parsers see the parser context and reach runtime state via
/// <see cref="ParserContext.Batch"/>; the dispatch loop and writeback
/// helpers operate on the batch context directly.
/// </summary>
internal sealed partial class BatchContext
{
    /// <summary>The parser-side cursor / scratch state for this batch.</summary>
    public readonly ParserContext Parser;

    /// <summary>
    /// Heap-mutation undo log scoped to the current top-level statement. Set
    /// by <see cref="Simulation.CreateResultSetsForCommand"/>'s mutation
    /// dispatch around each INSERT / UPDATE / DELETE / MERGE; the
    /// <see cref="Heap.Insert"/> / <see cref="Heap.DeleteAt"/>
    /// call sites read it from here and append entries on success. A
    /// statement that throws mid-execution (e.g. a multi-row INSERT whose
    /// fourth row violates a constraint) walks the log backwards before the
    /// exception propagates, restoring the heap to its pre-statement state.
    /// Explicit transactions reuse the same log shape, lifetime extended
    /// across statements until COMMIT / ROLLBACK.
    /// </summary>
    public UndoLog? CurrentUndoLog;

    /// <summary>
    /// Per-statement undo log dedicated to <c>@t</c> table-variable mutations.
    /// Allocated fresh by <c>RunMutation</c> at the top of each DML statement
    /// and discarded on statement success; rolled back on statement failure.
    /// Probe-confirmed against SQL Server 2025: real SQL Server's table
    /// variables are non-transactional with respect to <c>BEGIN TRAN</c> /
    /// <c>ROLLBACK</c> (writes survive a tx-scoped rollback) but ARE
    /// statement-atomic — a multi-row INSERT into <c>@t</c> that fails on the
    /// third row leaves the first two rows undone. Routing @t mutations
    /// here instead of <see cref="CurrentUndoLog"/> preserves both invariants:
    /// the per-statement scope means tx-level rollback never sees these
    /// entries, and the replay-on-exception path inside <c>RunMutation</c>
    /// covers the statement-atomic case.
    /// </summary>
    public UndoLog? CurrentTableVarUndoLog;

    /// <summary>
    /// Statement-scoped version-store pending list for auto-commit DML.
    /// Allocated by <see cref="Simulation.RunMutation"/> at the top of each
    /// mutating statement when there is no active <see cref="SimulatedDbTransaction"/>
    /// — entries route to <see cref="SimulatedDbTransaction.PendingVersionEntries"/>
    /// instead when a tx is active. Drained on statement success via
    /// <see cref="VersionStore.FinalizePendingEntries"/> and on
    /// statement failure via <see cref="VersionStore.DiscardPendingEntries"/>.
    /// </summary>
    public List<PendingVersionEntry>? CurrentStatementVersionEntries;

    /// <summary>
    /// Per-statement snapshot Xid used by READ_COMMITTED_SNAPSHOT readers.
    /// Allocated lazily at the first user-table access inside the
    /// statement when the current database has
    /// <see cref="Database.ReadCommittedSnapshot"/> enabled and the
    /// session iso is <see cref="System.Data.IsolationLevel.ReadCommitted"/>.
    /// Cleared between statements by the dispatch loop.
    /// </summary>
    public long? RcsiStatementSnapshotXid;

    /// <summary>
    /// The list a captured version entry joins: the active transaction's
    /// <see cref="SimulatedDbTransaction.PendingVersionEntries"/> if any,
    /// otherwise the statement-scoped <see cref="CurrentStatementVersionEntries"/>,
    /// or null when neither is active.
    /// </summary>
    internal List<PendingVersionEntry>? ActivePendingVersionEntries() =>
        this.Connection.CurrentTransaction?.PendingVersionEntries ?? this.CurrentStatementVersionEntries;

    /// <summary>
    /// Per-statement scratch frame, allocated once per batch and overwritten
    /// in place by the dispatch loop at the top of each statement iteration.
    /// See <see cref="StatementContext"/> for the fields it carries.
    /// </summary>
    public readonly StatementContext CurrentStatement = new();

    /// <summary>
    /// The time a system-versioned table's period columns record a write at:
    /// the open transaction's begin time, as real stamps every write a
    /// transaction makes with the moment its <c>BEGIN TRANSACTION</c> ran,
    /// else the statement's frozen <see cref="StatementContext.UtcNow"/>
    /// (probed 2026-10-03 against SQL Server 2025).
    /// </summary>
    public DateTime SystemTimeUtc => this.Connection.CurrentTransaction?.BeginTimeUtc ?? this.FiringStatementSystemTimeUtc ?? this.CurrentStatement.UtcNow;

    /// <summary>
    /// In a trigger body fired outside a transaction, the firing statement's
    /// <see cref="SystemTimeUtc"/>, which every write of the body shares; null
    /// elsewhere.
    /// </summary>
    public DateTime? FiringStatementSystemTimeUtc;

    /// <summary>
    /// The line of the last top-level statement this batch's dispatch loop
    /// reached, -1 for a <c>BEGIN TRY</c>: where <c>STATISTICS TIME</c>
    /// reports the batch's compile (probed 2026-09-28 against SQL Server 2025).
    /// Kept only while the option is on.
    /// </summary>
    public int LastTopLevelStatementLine;

    /// <summary>
    /// Adopts <paramref name="outer"/>'s per-statement current-time freeze for
    /// a body that runs as part of the referencing statement rather than as
    /// statements of its own — a view body or an inline-TVF body, both of which
    /// real SQL Server inlines into the referencing statement's plan.
    /// Probe-confirmed against SQL Server 2025: a view whose projection is
    /// <c>SYSDATETIME()</c>, read once per row across a 300,000-row scan,
    /// yields one constant value equal to the referencing statement's own
    /// <c>SYSDATETIME()</c> — and an inline TVF applied per row does the same.
    /// <para>
    /// Module bodies that <em>do</em> dispatch statements of their own
    /// (procedure, trigger, scalar-UDF, multi-statement-TVF bodies) deliberately
    /// don't call this: the dispatch loop re-stamps each body statement, which
    /// is what real does — a UDF body that spins for 1.2 seconds between two
    /// <c>SYSDATETIME()</c> calls reads two values 1.2 seconds apart.
    /// </para>
    /// </summary>
    public void AdoptStatementFreezeFrom(BatchContext outer) =>
        this.CurrentStatement.UtcNow = outer.CurrentStatement.UtcNow;

    /// <summary>
    /// Per-execution results for the aggregate / window expressions of the
    /// SELECT currently projecting under this batch, keyed by expression
    /// instance (reference identity). The executor binds each group's / row's
    /// result here just before running the projection expressions, and
    /// <c>AggregateExpression.Run</c> / <c>WindowExpression.Run</c> read it
    /// back. Lives on the batch — NOT on the expression instance — because a
    /// plan-cached <c>Selection</c> is shared across concurrently-executing
    /// commands, and instance-bound results cross-contaminate them (measured
    /// as transiently wrong SUM/COUNT values under concurrent identical
    /// queries). Batch execution is single-threaded, so no lock; lazily
    /// allocated on the first bind so aggregate-free batches pay nothing.
    /// </summary>
    public Dictionary<Expression, SqlValue>? BoundProjectionResults;

    /// <summary>
    /// Binds one aggregate / window expression's result for the group / row
    /// about to be projected. Overwrites any earlier group's binding for the
    /// same instance — within one batch, groups project strictly after their
    /// bind, so only the latest binding is ever live.
    /// </summary>
    public void BindProjectionResult(Expression expression, SqlValue value) =>
        (this.BoundProjectionResults ??= new Dictionary<Expression, SqlValue>(ReferenceEqualityComparer.Instance))[expression] = value;

    /// <summary>
    /// Queues a <c>PRINT</c>-emitted string with <c>PRINT</c>'s standard
    /// fields (class 0, number 0, state 1 — matching <c>SqlError</c>'s fields
    /// for an inline <c>PRINT</c>). Caller has already formatted the operand
    /// value into its display string (NULL → single space per probe).
    /// Skipped-IF / loop-control suppression is decided by the caller
    /// (<see cref="IsSkipping"/>), not here.
    /// </summary>
    internal void AppendPrintMessage(string text) =>
        AppendInfoError(@class: 0, state: 1, number: 0, message: text);

    /// <summary>
    /// Queues an informational message for the statement being dispatched,
    /// attributed to its line and enclosing procedure; the dispatch loop
    /// places it in the outcome stream ahead of the statement's own outcomes.
    /// Severity 10 is delivered as class 0, as real sends it (probed
    /// 2026-09-23: <c>RAISERROR(…, 10, …)</c>, Msg 8153, Msg 15477 all arrive
    /// with class 0), while severities 1-9 keep their number.
    /// </summary>
    internal void AppendInfoError(byte @class, byte state, int number, string message) =>
        this.Connection.PendingMessages.Enqueue(this.InfoMessage(@class, state, number, message));

    /// <summary>
    /// An informational message attributed to the statement being dispatched,
    /// for a caller that places it itself rather than through
    /// <see cref="SimulatedDbConnection.PendingMessages"/>.
    /// </summary>
    internal SimulatedError InfoMessage(byte @class, byte state, int number, string message) =>
        new(
            @class: @class == 10 ? (byte)0 : @class,
            // A module body's lines count from its CREATE batch, as its errors'
            // do (probed 2026-09-26: a PRINT on a body's first line under a
            // CREATE two lines into its batch reports line 3).
            lineNumber: this.CurrentStatement.StartLine + this.LineOffset,
            message: message,
            number: number,
            procedure: this.ErrorProcedureName,
            server: this.Connection.DataSource,
            source: "SqlServerSimulator",
            state: state);

    /// <summary>
    /// Whether <paramref name="error"/> is a divide by zero or an arithmetic
    /// overflow the session answers with NULL instead: under <c>SET ARITHABORT
    /// OFF</c> with <c>ANSI_WARNINGS OFF</c> real yields NULL for the failing
    /// operation and follows the statement with the class-0 Msg 3607
    /// (<c>Division by zero occurred.</c>) or 3606 (<c>Arithmetic overflow
    /// occurred.</c>), each once, where either option on raises the error — and
    /// a fresh session's ARITHABORT is off, so <c>SET ANSI_WARNINGS OFF</c>
    /// alone gets there (probed 2026-09-25 against SQL Server 2025). An
    /// identity overflow and a failed conversion still raise. When true the
    /// notice has been noted, unless <c>SET ARITHIGNORE ON</c> suppresses it.
    /// </summary>
    internal bool AbsorbsArithmeticFault(SimulatedSqlException error)
    {
        if (this.Connection.Arithabort || this.Connection.AnsiWarnings || error.IsIdentityOverflow)
            return false;
        var noticed = !this.Connection.ArithIgnore;
        switch (error.Number)
        {
            case 220 or 232 or 8115:
                this.CurrentStatement.OwesOverflowNotice |= noticed;
                return true;
            case 8134:
                this.CurrentStatement.OwesDivideByZeroNotice |= noticed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Queues Msg 8153 now if an aggregate the statement ran so far skipped a
    /// NULL — for an <c>IF</c> / <c>WHILE</c> condition, whose warning real
    /// sends before the body's first message (probed 2026-09-23) rather than
    /// after the statement as a query's.
    /// </summary>
    internal void QueueNullEliminatedWarning()
    {
        if (!this.CurrentStatement.NullEliminated)
            return;
        this.CurrentStatement.NullEliminated = false;
        if (this.Connection.AnsiWarnings)
            this.Connection.PendingMessages.Enqueue(SimulatedSqlException.NullEliminatedMessage(this));
    }

    /// <summary>
    /// Queues real's severity-10 Msg 9927 once per statement. A full-text
    /// predicate evaluates per row, and real reports the ignored words once for
    /// the statement.
    /// </summary>
    internal void AppendFullTextNoiseWordMessage()
    {
        if (this.CurrentStatement.ReportedNoiseWords)
            return;
        this.CurrentStatement.ReportedNoiseWords = true;
        AppendInfoError(@class: 10, state: 1,
            SimulatedSqlException.FullTextNoiseWordMessageNumber,
            SimulatedSqlException.FullTextNoiseWordMessage);
    }

    /// <summary>
    /// Raw IF-skip flag: true while the dispatch loop is walking through an
    /// un-taken IF branch. The <see cref="IsSkipping"/> property OR's this
    /// with <see cref="LoopControl"/>-driven skipping (BREAK / CONTINUE in
    /// flight) so the statement-level gates can read one combined predicate
    /// regardless of why execution is short-circuited.
    /// </summary>
    public bool SkipModeFlag;

    /// <summary>
    /// True on the throwaway batch that binds a text without running it: a
    /// module body at <c>CREATE</c> / <c>ALTER</c> time
    /// (<c>Simulation.BindModuleBodyAtCreate</c>), or a batch compiling before
    /// it runs (<c>Simulation.CompileBatch</c>). The batch also runs with
    /// <see cref="SkipModeFlag"/> set, so every statement parses and resolves
    /// without mutating state; this flag adds the two behaviors that are
    /// specific to binding rather than to skipping:
    /// <list type="bullet">
    /// <item>permission enforcement is off (<see cref="EnforcesPermissions"/>)
    /// — a bind resolves names, it never reads or writes anything, and real
    /// binds a body under the module's own ownership chain;</item>
    /// <item>a swallowed deferred-name error raised mid-statement abandons the
    /// rest of the bind (<see cref="BatchAborted"/>), because anything the
    /// recovery scan reaches after it would be bound from an unreliable
    /// position; one raised by a statement read to its end
    /// (<see cref="StatementContext.DeferredReadToEnd"/>) doesn't.</item>
    /// </list>
    /// </summary>
    public bool CreateTimeBinding;

    /// <summary>
    /// The procedures a module body binding at <c>CREATE</c> calls that don't
    /// exist, as each <c>EXEC</c> wrote them, for the Msg 2007 notes the
    /// <c>CREATE</c> sends; null for every other batch.
    /// </summary>
    public List<string>? MissingProcedureReferences;

    /// <summary>
    /// Set on the throwaway batch <c>Simulation.CompileBatch</c> walks ahead of
    /// running a batch, as opposed to a module body binding at <c>CREATE</c>:
    /// real's optimizer meets the first but not the second, so its refusals
    /// (Msg 8622) are raised only here.
    /// </summary>
    public bool CompilingForRun;

    /// <summary>
    /// Set when the compile walk (<see cref="CompilingForRun"/>) meets a
    /// <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c>: real then optimizes each
    /// statement only as it runs, so a refusal its optimizer raises —
    /// <see cref="DeferredOptimizerError"/> — waits for its statement.
    /// </summary>
    public bool WalkMetDdl;

    /// <summary>
    /// The first refusal the compile walk met that real raises optimizing a
    /// statement rather than binding it (a write to a vector-indexed table),
    /// which ends the batch before it runs unless <see cref="WalkMetDdl"/>.
    /// </summary>
    public SimulatedSqlException? DeferredOptimizerError;

    /// <summary>
    /// Raises a write refusal settled while optimizing the writing statement:
    /// when it runs, or — recorded for the end of the walk — while the batch
    /// compiles; a statement skipped as it runs, or a module body binding at
    /// <c>CREATE</c>, raises nothing (probed 2026-09-29 against SQL Server
    /// 2025).
    /// </summary>
    private void RejectOptimizedWrite(SimulatedSqlException refusal)
    {
        if (!this.IsSkipping)
            throw refusal;
        if (this.CompilingForRun && this.DeferredOptimizerError is null)
        {
            refusal.ResolveDiagnostics(this.CurrentStatement.StartLine, this.LineOffset, this.ErrorProcedureName);
            this.DeferredOptimizerError = refusal;
        }
    }

    /// <summary>
    /// Set on the batch a scalar function's or multi-statement TVF's body runs
    /// in when called: like a procedure's, it is as far as a batch-aborting
    /// name-resolution error reaches, so the calling statement fails and its
    /// batch carries on (probed 2026-09-26 against SQL Server 2025). A view's
    /// or inline function's body binds with the calling statement instead,
    /// and its error ends that batch.
    /// </summary>
    public bool CalledFunctionBody;

    /// <summary>The <c>#</c> / <c>##</c> tables a create-time bind has seen a statement create.</summary>
    private HashSet<string>? tempTablesCreatedWhileBinding;

    /// <summary>
    /// Under <see cref="CreateTimeBinding"/>, notes that a statement — a
    /// <c>CREATE TABLE</c> or <c>SELECT … INTO</c> — creates the temp table
    /// <paramref name="name"/>, raising Msg 2714 state 1 for a second one: real
    /// refuses a batch or module body that creates one temp table twice while
    /// compiling it, even from opposite IF branches or with a <c>DROP</c>
    /// between (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public void NoteTempTableCreation(string name)
    {
        if (!this.CreateTimeBinding || !(IsLocalTempName(name) || IsGlobalTempName(name)))
            return;
        if (!(this.tempTablesCreatedWhileBinding ??= new(BuiltInToken.Comparer)).Add(name))
            throw SimulatedSqlException.NameTakenEndingOnlyStatement(name, state: 1);
    }

    /// <summary>
    /// Set while a <c>NATIVE_COMPILATION</c> procedure's body binds at
    /// <c>CREATE</c>, the one module body that may hold <c>BEGIN ATOMIC</c>.
    /// Any other module refuses the block there (Msg 10782), so a body that
    /// runs later already passed the check.
    /// </summary>
    public bool NativelyCompiledBody;

    /// <summary>
    /// The plan an emptiness probe (<see cref="Selection.HasAnyRow"/>) is
    /// draining, which answers without evaluating its projection; null
    /// otherwise. A nested plan the probe runs is never it.
    /// </summary>
    public Selection? ExistenceProbe;

    /// <summary>
    /// The plan <see cref="Selection.ExecuteWithRowAddresses"/> is draining,
    /// which appends the address of its first FROM source's row to each row
    /// it yields; null otherwise. A nested plan the run executes is never it.
    /// </summary>
    public Selection? RowAddressProbe;

    /// <summary>
    /// Set while a write through a level over a partitioned view reads that
    /// level's rows for one member, which real evaluates per member: a
    /// reference to the partitioned view then yields only that member's rows,
    /// and a window function numbers or frames them with its <c>ORDER BY</c>
    /// ignored (see <c>Simulation.PartitionedView.cs</c>). A view body the run
    /// reads inherits it. Null otherwise.
    /// </summary>
    public PartitionedMemberRun? PartitionedMemberRun;

    /// <summary>
    /// Binder errors gathered while a module body or a batch binds, non-null
    /// only on the bind batch (<see cref="CreateTimeBinding"/>). Real reports <em>every</em>
    /// binder error a body contains rather than stopping at the first
    /// (probe-confirmed: two statements with a bad column each report two
    /// Msg 207s, and a body <c>TRY</c> / <c>CATCH</c> shields neither — binding
    /// happens before any of it runs), so a statement's error is recorded here
    /// and the walk resumes at the next statement boundary. What lands in the
    /// list is a binder error (<c>Simulation.IsBinderError</c>, severity 16 and
    /// Msg 1087): real's parse-phase errors preempt the whole report, so they
    /// keep propagating on sight. A missing <c>DECLARE</c> type is gathered
    /// where it is raised, so the variable is declared anyway.
    /// </summary>
    public List<SimulatedSqlException>? CreateTimeBindErrors;

    /// <summary>
    /// The report one statement's binder errors gather into, non-null only
    /// while <c>Simulation.ReportEveryBindError</c> re-reads a statement whose
    /// bind failed: the sites that meet a name miss record it here and carry
    /// on rather than throw. A body the statement calls binds on a batch of its
    /// own, which never sees it.
    /// </summary>
    public BindErrorReport? BindErrors;

    /// <summary>
    /// Whether the last resume after a gathered binder error left the parse
    /// cursor on solid ground — a statement separator or the end of the body,
    /// rather than a keyword the recovery scan guessed at from inside the
    /// failed statement. Real's parse phase preempts its binder's report
    /// entirely (probe-confirmed: a bad column on one line and an undeclared
    /// variable on the next report only Msg 137), which a severity-15 error is
    /// free to reproduce from solid ground; raised from a guessed position it
    /// could just as well be a diagnostic against a fragment, so the bind keeps
    /// what it gathered instead.
    /// </summary>
    public bool BindResumedCleanly = true;

    /// <summary>
    /// Collector for real's function body-shape rules (Msg 455 / 444 / 443),
    /// non-null only while <c>Simulation.BindModuleBodyAtCreate</c> walks a
    /// scalar-UDF or multi-statement-TVF body — the two module kinds real
    /// applies those rules to. Null everywhere else, including on a procedure /
    /// trigger bind and on every invocation batch, so the recording sites cost
    /// one null check.
    /// </summary>
    public FunctionBodyShape? FunctionBodyShape;

    /// <summary>
    /// The scalar function calls the text being read would inline, non-null
    /// only while a batch compiles (<c>Simulation.CompileBatch</c>), a
    /// statement compiles as it runs, or a function body is read as real
    /// inlines it, and handed to the child batch a view's body binds on; null
    /// everywhere else, so a call site costs one null check.
    /// </summary>
    public InlinedScalarCalls? InlinedCalls;

    /// <summary>
    /// The statements of this batch's text that compile again as they run,
    /// by where each starts: set from <c>Simulation.CompileBatch</c>, and null
    /// when the batch has none, so a statement's dispatch costs one null check.
    /// </summary>
    public StatementsCompiledOnRun? StatementsCompiledOnRun;

    /// <summary>
    /// In-flight loop-flow signal. <see cref="LoopControl.Break"/> /
    /// <see cref="LoopControl.Continue"/> set by their dispatch sites;
    /// <see cref="LoopControl.None"/> the default. Only the
    /// immediately-enclosing WHILE consumes the value — IF / BEGIN…END /
    /// nested blocks pass it through unchanged (subsequent statements in
    /// their scope skip naturally via <see cref="IsSkipping"/>). The
    /// BREAK / CONTINUE parsers don't throw — flag-based control flow
    /// composes cleanly with iterator-based dispatch in a way exception-
    /// signaled control flow doesn't.
    /// </summary>
    public LoopControl LoopControl;

    /// <summary>
    /// Number of WHILE loops currently mid-iteration in this batch.
    /// Incremented unconditionally by WHILE on entry (even when the WHILE
    /// itself is in skip mode), decremented on exit. BREAK / CONTINUE check
    /// this at parse time: when zero, raise Msg 135 / 136 (matches real SQL
    /// Server's compile-time loop-scope check — fires even from un-taken IF
    /// branches, distinct from the un-taken-branch deferred-name-resolution
    /// gap).
    /// </summary>
    public int LoopDepth;

    /// <summary>
    /// Rows the row-level cancellation poll has let through since its last
    /// look (see <see cref="PollCancellation"/>). A field rather than a loop
    /// local so the stride carries across enumerations: a recursive CTE's
    /// member, a correlated inner plan or a cursor's fetch each enumerate a
    /// row or two at a time, and a per-enumeration count would never reach it.
    /// </summary>
    private int cancellationPollTick;

    /// <summary>
    /// The row loops' cancellation safe point: every 32nd call looks at the
    /// execution's cancellation and raises the attention when it has fired.
    /// The stride keeps a scan's per-row cost to an increment and a test —
    /// a 228k-row <c>COUNT(*)</c> and the index replay measured unchanged
    /// within noise — where a look on every row would read through the
    /// connection to the cancellation source's state. Thirty-two rows cheap
    /// enough for the stride to matter take microseconds, and a row that isn't
    /// cheap — a scalar function's loop — polls inside its own body.
    /// </summary>
    public void PollCancellation()
    {
        if ((++this.cancellationPollTick & 31) == 0)
            this.ThrowIfCancelled();
    }

    /// <summary>
    /// Raises the attention a <c>CommandTimeout</c> expiry or a caller's cancel
    /// becomes inside a running statement (Msg -2 or Msg 0, marked
    /// <see cref="SimulatedSqlException.IsAttention"/>), when it has fired.
    /// </summary>
    public void ThrowIfCancelled()
    {
        var connection = this.Connection;
        if (connection.ExecutionCancellationRequested)
            throw SimulatedSqlException.Attention(connection.ExecutionTimedOut);
    }

    /// <summary>
    /// The statement-boundary safe point — between two statements, and at the
    /// top of each <c>WHILE</c> iteration. At the top level a cancellation
    /// simply ends the batch, so this returns <see langword="true"/> for the
    /// dispatch loop to stop on. Inside a body that runs within a caller's
    /// statement it raises the attention instead, since stopping quietly
    /// would hand the caller a body that ended early — a function returning a
    /// half-computed value, a trigger letting its statement commit — and real
    /// ends the calling statement. No statement of the body's own was
    /// interrupted, so the body settles the attention as ending no write,
    /// except a function body, whose work is its calling statement's.
    /// </summary>
    public bool CancelledAtStatementBoundary()
    {
        var connection = this.Connection;
        if (!connection.ExecutionCancellationRequested)
            return false;
        if (!this.RunsInsideCallerStatement)
        {
            // Ends the batch: every block the statement sits in stops where it
            // is, as for a batch-aborting error, rather than demanding the END
            // the abandoned statements never reached — a cancel landing inside
            // BEGIN…END or TRY otherwise surfaced as a Msg 102 near it.
            this.BatchAborted = true;
            return true;
        }
        var attention = SimulatedSqlException.Attention(connection.ExecutionTimedOut);
        attention.AttentionSettled = !this.SuppressDiagnosticsResolution;
        throw attention;
    }

    /// <summary>
    /// True for a body that runs inside a statement of its caller — a
    /// procedure, trigger, function, view or dynamic-SQL body — where a
    /// cancellation seen between two of its own statements must end the
    /// calling statement too, rather than let the caller carry on with a
    /// body that stopped early.
    /// </summary>
    public bool RunsInsideCallerStatement =>
        this.UdfFrame is not null || this.ProcFrame is not null || this.TriggerFrame is not null
        || this.CalledFunctionBody || this.SuppressDiagnosticsResolution || this.IsContextConnectionCommand;

    /// <summary>
    /// The <c>QUOTED_IDENTIFIER</c> setting a top-level batch's last
    /// <c>SET</c> of it leaves, which <c>@@OPTIONS</c> reads anywhere in the
    /// batch; null for a batch holding none (see
    /// <c>Simulation.ScanParseTimeOptions</c>).
    /// </summary>
    public bool? QuotedIdentifiersAfterParse;

    /// <summary>
    /// Opens the transaction <c>SET IMPLICIT_TRANSACTIONS ON</c> gives a
    /// statement that reads or writes an object when none is open — called by
    /// the sites that meet one: a FROM source naming a table, view, table
    /// variable, catalog view or function, a DML target, object DDL and
    /// <c>GRANT</c> / <c>DENY</c> / <c>REVOKE</c>, a cursor's
    /// <c>DECLARE</c> / <c>OPEN</c> / <c>FETCH</c>, <c>NEXT VALUE FOR</c>, a
    /// user function a query calls, and <c>BEGIN TRANSACTION</c>, which then
    /// nests inside it. An <c>IF</c> / <c>WHILE</c> condition opens none, nor
    /// does a built-in rowset function, a CTE or <c>VALUES</c> by itself
    /// (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public void BeginImplicitTransaction()
    {
        var connection = this.Connection;
        if (!connection.ImplicitTransactions
            || connection.CurrentTransaction is not null
            || this.IsSkipping
            || this.Parser.ConditionDepth > 0
            || this.UdfFrame is not null
            || connection.TriggerStatementUndoLog is not null)
        {
            return;
        }
        connection.CurrentTransaction = new SimulatedDbTransaction(connection.Simulation, connection, System.Data.IsolationLevel.Unspecified) { BegunImplicitly = true };
        this.CurrentStatement.BeganImplicitTransaction = true;
    }

    /// <summary>
    /// Depth of nested IF / WHILE / BEGIN...END dispatches in this batch.
    /// Bumped by the body-dispatching parsers, decremented on exit. Used by
    /// the must-be-first-statement check on CREATE/ALTER
    /// PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA: zero depth + no prior
    /// statement = OK; anything else = Msg 111. Inner CommandText-equivalent
    /// contexts (proc / function / trigger / dynamic-SQL bodies) get a fresh
    /// <see cref="BatchContext"/> so this counter naturally resets at body
    /// entry, matching real SQL Server's batch boundary semantics.
    /// </summary>
    public int BlockDepth;

    /// <summary>
    /// Whether the batch's top-level dispatch has consumed at least one
    /// substantive statement (anything that isn't a bare <c>;</c>). The
    /// must-be-first-statement check on CREATE/ALTER
    /// PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA reads this together
    /// with <see cref="BlockDepth"/>: both zero / false = OK; either set =
    /// Msg 111.
    /// </summary>
    public bool HasDispatchedStatement;

    /// <summary>
    /// Marks the outermost (top-level) batch. When <see langword="true"/>, a
    /// statement-terminating error outside any TRY frame is emitted as a
    /// <see cref="SimulatedErrorOutcome"/> and the batch continues to the next
    /// statement, matching real SQL Server's default (non-XACT_ABORT) severity
    /// model. Both front doors — the in-process ADO surface and the TDS wire —
    /// set it (via <c>Simulation.CreateResultSetsForCommand</c>'s
    /// <c>continueOnError</c> argument, which defaults to
    /// <see langword="true"/>) and render the resulting shared outcome stream:
    /// the wire writes error tokens, the ADO reader converts outcomes to
    /// SqlClient-shaped exceptions. Not propagated into child batches (proc /
    /// trigger / UDF / dynamic-SQL bodies), which leave it
    /// <see langword="false"/> so their errors throw and surface at the
    /// invoking statement.
    /// </summary>
    public bool ContinueOnError;

    /// <summary>
    /// Set <see langword="true"/> when a batch-aborting error (a compile /
    /// bind-class name-resolution failure — see
    /// <c>Simulation.IsBatchAbortingNameResolution</c>) fires under
    /// <see cref="ContinueOnError"/>. Unlike an ordinary statement-terminating
    /// error, real SQL Server does not run the remaining statements after one
    /// of these (probe-confirmed against SQL Server 2025: a mid-batch
    /// <c>SELECT * FROM missing</c> streams the prior statements' results, the
    /// one Msg 208, then stops — the following statements never execute). The
    /// wire path emits the single error outcome and the dispatch loop breaks
    /// on this flag rather than resuming at the next statement, so the
    /// abandoned-mid-parse cascade of bogus Msg 319 / 102 syntax errors never
    /// happens. The in-process path aborts by throwing instead, so an error
    /// never sets it there; a cancellation seen between two top-level
    /// statements sets it on both paths (<see cref="CancelledAtStatementBoundary"/>).
    /// </summary>
    public bool BatchAborted;

    /// <summary>
    /// Set <see langword="true"/> when this batch's parse captures state
    /// that can't be reused across invocations:
    /// <list type="bullet">
    ///   <item><see cref="TryResolveTable"/> hands back a session- or
    ///   batch-local table — a local <c>#temp</c>, a global <c>##gtemp</c>,
    ///   or a <c>@t</c> table variable (the resolved <see cref="HeapTable"/>
    ///   instance is meaningful only to this session / batch).</item>
    ///   <item>The FROM-less SELECT path
    ///   (<c>BuildSynthesizedSqlRow</c>) evaluated the projection list at
    ///   parse time and baked the resulting <see cref="SqlValue"/>s into the
    ///   row source — replaying the cached plan would emit those stale
    ///   values instead of fresh <c>NEWID()</c> / <c>GETDATE()</c> /
    ///   <c>@@TRANCOUNT</c> / <c>NEXT VALUE FOR seq</c> results.</item>
    /// </list>
    /// The plan-cache promotion check reads this flag and skips caching
    /// when set. The name is left general because the underlying
    /// "non-cacheable" semantic is what matters at the promotion site —
    /// either condition disqualifies a batch identically.
    /// </summary>
    public bool HasSessionScopedReference;

    /// <summary>
    /// Whether a name in this batch was looked up as a local or global temp
    /// table, found or not. What it bound to is the session's, so a compile of
    /// the batch says nothing about the next session's run of the same text.
    /// </summary>
    public bool ResolvedTempTable;

    /// <summary>Plan-cache key component: the command text this batch was
    /// constructed for. Set by <c>CreateResultSetsForCommand</c> when the
    /// batch is plan-cache-eligible (non-empty text + live connection +
    /// current database), <see langword="null"/> otherwise. The SELECT arm
    /// reads all four components to build the lookup key for inline cache
    /// promotion (the iterator's post-foreach code is unreachable from a
    /// non-draining ExecuteReader consumer).</summary>
    public string? PlanCacheCommandText;

    /// <summary>Plan-cache key component: the current database name at
    /// batch start. See <see cref="PlanCacheCommandText"/>.</summary>
    public string? PlanCacheDatabaseName;

    /// <summary>Plan-cache key component: the canonical parameter type
    /// signature built from the command's <see cref="DbCommand.Parameters"/>
    /// collection. See <see cref="PlanCacheCommandText"/>.</summary>
    public string? PlanCacheParameterSignature;

    /// <summary>Plan-cache key component: the <see cref="Simulation.SchemaVersion"/>
    /// snapshot at batch start. Compared against the live value at promotion
    /// time so a DDL that fired mid-batch declines caching the entry.</summary>
    public long PlanCacheSchemaVersion;

    /// <summary>
    /// The row-returning <see cref="Selection"/>s this batch's top-level
    /// statements have parsed, in dispatch order — the candidate cache entry
    /// for a batch that turns out to be nothing but SELECTs. Appended by the
    /// SELECT arm; allocated on first append, so a batch that never reaches it
    /// costs nothing.
    /// </summary>
    public List<Selection>? PlanCacheSequence;

    /// <summary>
    /// The locks each <see cref="PlanCacheSequence"/> entry took while it
    /// parsed, index for index, which a replay takes again as its own session.
    /// </summary>
    public List<ReplayedLock[]>? PlanCacheSequenceLocks;

    /// <summary>
    /// Where each <see cref="PlanCacheSequence"/> entry's text starts and
    /// ends in the command, index for index — what a replay records the
    /// statement's Query Store capture under.
    /// </summary>
    public List<(int Start, int End)>? PlanCacheSequenceSpans;

    /// <summary>
    /// The plan-cache key this batch runs under, when it has one; the DML
    /// statements' plans are filed under it (<see cref="DmlPlans"/>).
    /// </summary>
    public Simulation.PlanCacheKey? PlanCacheKey;

    /// <summary>
    /// The DML statement plans cached for this batch's text, looked up once as
    /// the batch starts, or created by its first recording.
    /// </summary>
    public DmlPlanSet? DmlPlans;

    /// <summary>
    /// Armed around a top-level DML statement's parse when its plan may be
    /// cached; the statement's split point fills it (see
    /// <see cref="DmlPlanRecording"/>).
    /// </summary>
    public DmlPlanRecording? DmlPlanRecording;

    /// <summary>
    /// Records every lock acquisition, <c>NOWAIT</c> table and parse-time
    /// permission check while non-null — armed around a top-level statement's
    /// parse in a batch the plan cache may store. Taking schema-stability and
    /// table-level data locks is part of parsing a statement here, and so is
    /// the odd permission check, so a replay, which parses nothing, takes the
    /// recorded list again as the replaying session.
    /// </summary>
    public List<ReplayedLock>? ReplayLockLog;
#if DEBUG

    /// <summary>
    /// Where a SELECT this batch may cache first read its principal while
    /// parsing, if one did (see <see cref="PlanCacheCaptureAudit"/>).
    /// </summary>
    public string? PrincipalReadWhileParsing;
#endif

    /// <summary>
    /// Takes <paramref name="locks"/> — a cached statement's parse-time
    /// acquisitions and permission checks — as this batch's session, in the
    /// order the parse took them. Stops ahead of a permission check, answering
    /// false, once <see cref="Simulation.SchemaVersion"/> has moved off
    /// <paramref name="schemaVersion"/> (a lock waited out a definition
    /// change): the check would judge an object the statement may no longer
    /// name, and the caller parses the statement instead. A null
    /// <paramref name="schemaVersion"/> takes every step regardless.
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public bool TakeReplayedLocks(ReplayedLock[] locks, long? schemaVersion)
    {
        foreach (var taken in locks)
        {
            if (taken.Check is { } check)
            {
                if (schemaVersion is { } expected && Volatile.Read(ref this.Connection.Simulation.SchemaVersion) != expected)
                    return false;
                check.Run(this);
            }
            else if (taken.NoWaitTable is { } table)
            {
                _ = this.noWaitTables.Add(table);
            }
            else if (taken.TransactionScoped)
            {
                this.AcquireTransactionLock(taken.Resource!, taken.Mode, taken.NoWait);
            }
            else
            {
                this.AcquireStatementLock(taken.Resource!, taken.Mode, taken.NoWait);
            }
        }
        return true;
    }

    /// <summary>
    /// Top-level statements dispatched by this batch, counted by the dispatch
    /// loop. Compared against <see cref="PlanCacheSequence"/>'s length at the
    /// promotion site: equal counts mean every statement the batch ran was an
    /// admitted SELECT, and any other statement kind — a <c>SET</c>, a DML
    /// write, a <c>BEGIN…END</c> block, an <c>EXEC</c> — advances the counter
    /// without contributing a plan and so declines the whole batch. Counting
    /// is what keeps the eligibility rule in one place instead of a decline
    /// call in every one of the dispatch switch's forty arms.
    /// </summary>
    public int TopLevelStatementsDispatched;

    /// <summary>
    /// Active error context inside a <c>CATCH</c> block — set when the
    /// associated <c>TRY</c> body's dispatch caught a
    /// <see cref="SimulatedSqlException"/>, cleared when the enclosing
    /// <c>BEGIN CATCH ... END CATCH</c> exits. Drives
    /// <c>ERROR_NUMBER</c> / <c>ERROR_MESSAGE</c> / <c>ERROR_SEVERITY</c> /
    /// <c>ERROR_STATE</c> / <c>ERROR_LINE</c> / <c>ERROR_PROCEDURE</c>
    /// (which return NULL when this is null) and the no-arg
    /// <c>THROW;</c> re-raise. Nested <c>TRY/CATCH</c> saves+restores this
    /// around the inner CATCH so the outer CATCH (if reached via re-throw)
    /// sees the re-thrown error.
    /// </summary>
    public CaughtError? InFlightError;

    /// <summary>
    /// Set true when a <c>SimulatedSqlException</c> is caught at a
    /// <c>TRY/CATCH</c> boundary; <see cref="IsSkipping"/> OR's it in so the
    /// rest of the TRY body skip-dispatches until <c>END TRY</c>. Cleared
    /// when the matching CATCH begins running so its statements aren't
    /// themselves skipped.
    /// </summary>
    public bool ErrorSignaled;

    /// <summary>
    /// Number of <c>TRY</c> bodies currently being dispatched on the stack.
    /// Incremented at <c>BEGIN TRY</c>, decremented at <c>END TRY</c> — does
    /// <em>not</em> increment when the matching CATCH body runs (CATCH isn't
    /// inside its own TRY). The dispatch wrapper catches
    /// <see cref="SimulatedSqlException"/> only when this is positive;
    /// otherwise errors propagate out of the batch as before.
    /// </summary>
    public int TryFrameDepth;

    /// <summary>
    /// Number of <c>CATCH</c> bodies currently being dispatched on the stack.
    /// Incremented when a CATCH body starts running (i.e. the matching TRY
    /// caught an error), decremented when it ends. Gates <c>THROW;</c> (the
    /// no-arg re-raise — Msg 10704 when zero) and the in-CATCH detection for
    /// <c>ERROR_*()</c> functions.
    /// </summary>
    public int CatchDepth;

    /// <summary>
    /// Whether this batch has run a <c>SET DATEFIRST</c> of its own, which
    /// makes a later <c>SET LANGUAGE</c> in the same batch leave
    /// <c>@@DATEFIRST</c> alone. Real scopes that precedence to the batch
    /// rather than the session, so the same pair split across two batches ends
    /// on the language's value (probe-confirmed).
    /// </summary>
    public bool DateFirstSetExplicitly;

    /// <summary>
    /// Whether this batch has run a <c>SET DATEFORMAT</c> of its own, which a
    /// later <c>SET LANGUAGE</c> in the same batch then leaves alone — the same
    /// batch-scoped precedence as <see cref="DateFirstSetExplicitly"/>, and
    /// independent of it (probed 2026-09-24).
    /// </summary>
    public bool DateFormatSetExplicitly;

    /// <summary>
    /// Shared empty label map for the overwhelming majority of batches, which
    /// declare none.
    /// </summary>
    public static readonly System.Collections.Frozen.FrozenDictionary<string, LabelTarget> NoLabels =
        System.Collections.Frozen.FrozenDictionary<string, LabelTarget>.Empty;

    /// <summary>
    /// Every <c>label:</c> this batch or module body declares, mapped to the
    /// cursor position immediately after it — the point a <c>GOTO</c> resumes
    /// dispatch at. Filled once per batch root by
    /// <c>Simulation.ScanBatchLabels</c>, which also settles the compile-phase
    /// Msg 132 / 133 / 1026 refusals.
    /// </summary>
    public IReadOnlyDictionary<string, LabelTarget> Labels = NoLabels;

    /// <summary>
    /// The label a <c>GOTO</c> has asked for and the dispatch loop has yet to
    /// jump to. Non-null makes <see cref="IsSkipping"/> true, so the nested
    /// dispatch loops of any enclosing <c>BEGIN…END</c> / <c>WHILE</c> /
    /// <c>TRY</c> unwind the way they do for <c>RETURN</c>, and the batch root
    /// restores the label's checkpoint and clears this.
    /// </summary>
    public string? PendingGotoLabel;

    /// <summary>
    /// How many <c>END</c> tokens the dispatch loop still has to swallow after
    /// a <c>GOTO</c> landed on a label nested deeper in <c>BEGIN…END</c> blocks
    /// than the loop servicing the jump — the blocks execution entered
    /// mid-body and so never opened. Zero for every jump that stays at or
    /// climbs out of its own nesting.
    /// </summary>
    public int PendingBlockEnds;

    /// <summary>
    /// How many nested statement-dispatch loops are on the stack — one per
    /// open <c>BEGIN…END</c> block, <c>BEGIN TRY</c> or <c>BEGIN CATCH</c>,
    /// zero at the batch root. Distinct from <see cref="BlockDepth"/>, which
    /// also counts an <c>IF</c> / <c>WHILE</c> that dispatches a single
    /// statement rather than opening a loop of its own; a <c>GOTO</c> matches
    /// against this one because it is what
    /// <c>Simulation.ScanBatchLabels</c> can count from the tokens.
    /// </summary>
    public int DispatchLoopDepth;

    /// <summary>
    /// True after a <c>RETURN</c> statement has fired in this batch. Drives
    /// early-exit propagation: the dispatch loop (and every enclosing
    /// construct — WHILE, BEGIN…END block) checks this and stops as soon as
    /// the current statement's dispatch completes. <see cref="IsSkipping"/>
    /// also OR's this in so any statements still parsed after RETURN in the
    /// same scope no-op via the skip-mode gates.
    /// </summary>
    /// <remarks>
    /// RETURN propagates through WHILE (unlike BREAK / CONTINUE, which the
    /// innermost WHILE catches). Batch-level only for now; once stored
    /// procedures and functions land, the proc-call boundary will consume
    /// the signal (and the value-form <c>RETURN N</c> will start being legal
    /// inside those scopes, ungating the Msg 178 check).
    /// </remarks>
    public bool ReturnSignaled;

    /// <summary>
    /// Non-null when this batch is executing a scalar UDF body. Holds the
    /// declared return type (used to coerce <c>RETURN &lt;expr&gt;</c> values
    /// at the body's RETURN statement) and the return-value slot the call
    /// site reads after dispatch completes. The presence of this frame is
    /// also the "value-form RETURN is legal" gate — outside a UDF body,
    /// <c>RETURN &lt;expr&gt;</c> raises Msg 178 at parse time (except inside
    /// a procedure body, where <see cref="ProcFrame"/> takes over).
    /// </summary>
    public UdfFrame? UdfFrame;

    /// <summary>
    /// Non-null when this batch is executing a stored-procedure body. Holds
    /// the return-code slot (int) and the procedure name for diagnostic
    /// attribution. Like <see cref="UdfFrame"/>, the presence of this frame
    /// is one of the "value-form RETURN is legal" gates. Unlike a UDF, a
    /// procedure body's SELECT result sets propagate to the outer caller —
    /// the difference is enforced at the call site (the UDF invocation
    /// drains yielded outcomes; the procedure invocation yields them
    /// through).
    /// </summary>
    public ProcFrame? ProcFrame;

    /// <summary>
    /// Newline count preceding this batch's text within the definition it was
    /// carved from — added to a body error's line so the reported number is
    /// relative to the whole <c>CREATE</c> statement rather than the stored
    /// body span (probe-confirmed: a procedure whose body starts on the CREATE
    /// text's line 2 reports its errors at the CREATE-relative line). Zero for
    /// a top-level command and for dynamic-SQL (<c>EXEC('…')</c> /
    /// <c>sp_executesql</c>) batches, whose text the client sent verbatim.
    /// Read by <see cref="Simulation.DispatchOneStatement"/> when stamping a
    /// caught exception's diagnostics.
    /// </summary>
    public int LineOffset;

    /// <summary>
    /// The start line of the last statement this frame ran that real counts
    /// as run — not a bare <c>BEGIN</c>, nor a <c>DECLARE</c> that neither
    /// initializes a variable nor declares a cursor — or 0 before any, and -1
    /// right after a statement failed. <see cref="StatementContext.PriorStatementLine"/>
    /// takes it as each statement starts.
    /// </summary>
    public int CountedStatementLine;

    /// <summary>
    /// Schema-qualified name of the procedure whose body this batch executes
    /// (<c>dbo.p1</c>), stamped onto a caught exception's
    /// <see cref="SimulatedError.Procedure"/> and thence <c>ERROR_PROCEDURE()</c>
    /// to mirror real SqlClient (probe-confirmed). Empty for top-level and
    /// dynamic-SQL batches, which carry no procedure attribution.
    /// </summary>
    public string ErrorProcedureName = "";

    /// <summary>
    /// When set, a caught exception is <em>not</em> stamped at this batch's
    /// dispatch frame — it propagates unresolved so the enclosing invoking
    /// statement's frame attributes the line / procedure. Scalar-UDF,
    /// inline-TVF, multi-statement-TVF, and view bodies set this: real SQL
    /// Server inlines them for error attribution, reporting the outer invoking
    /// statement's line with no procedure name (probe-confirmed — a divide-by-
    /// zero inside any of these surfaces the <c>SELECT</c>/<c>INSERT</c> that
    /// invoked them, not a body-relative line). Procedures, triggers, and
    /// dynamic-SQL batches leave it false and resolve at their own frame, so a
    /// UDF error inside a procedure body attributes to the enclosing procedure.
    /// Read by <see cref="Simulation.DispatchOneStatement"/>.
    /// </summary>
    public bool SuppressDiagnosticsResolution;

    /// <summary>
    /// Set on the batch a security predicate's function body runs in, which
    /// reads its tables without applying row-level security
    /// (see <see cref="RowSecurity"/>).
    /// </summary>
    public bool SuppressesRowSecurity;

    /// <summary>
    /// The security predicates the batch's statements have applied, each
    /// compiled once (see <see cref="SecurityPredicateRunner"/>).
    /// </summary>
    public Dictionary<Schemas.SecurityPredicate, SecurityPredicateRunner>? RowSecurityRunners;

    /// <summary>
    /// Set on the batch a view's or inline function's body binds in where a
    /// statement references it: real binds the stored definition there as it
    /// does at <c>CREATE</c>, so a missing schema-qualified object reports
    /// line 12 (see <see cref="UnresolvableObjectName"/>; probed 2026-10-04
    /// against SQL Server 2025).
    /// </summary>
    public bool BindsModuleDefinition;

    /// <summary>
    /// Non-null when this batch is executing a trigger body. Holds the
    /// <c>INSERTED</c> / <c>DELETED</c> pseudo-tables (materialized from
    /// the firing DML's affected rows) so the trigger body's bare-name
    /// <c>FROM inserted</c> / <c>FROM deleted</c> references resolve to
    /// these instances via <see cref="TryResolveTable"/>'s 1-part
    /// fallback. Absent outside trigger bodies — a bare <c>FROM inserted</c>
    /// in a regular query surfaces Msg 208 through the standard path.
    /// </summary>
    public TriggerFrame? TriggerFrame;

    /// <summary>
    /// The result sets, messages and continued-past errors trigger bodies
    /// produced while the current statement ran, in the order they ran,
    /// waiting to be handed to the client ahead of that statement's own
    /// outcome. A trigger fires from inside the DML executor, which returns a
    /// single outcome, so the body's output can't be yielded in place — real
    /// surfaces it as the firing statement's, in trigger registration order
    /// (probe-confirmed).
    /// The body's rows-affected counts aren't buffered: forwarding them would
    /// inflate the statement's reported total.
    /// </summary>
    public List<SimulatedStatementOutcome>? PendingTriggerOutcomes;

    /// <summary>
    /// Whether execution-time permission checks apply to statements dispatched
    /// in this batch. False inside a static module body (procedure / view /
    /// TVF / scalar-UDF / trigger) — ownership chaining suppresses checks on
    /// the body's object references that share the module's owner
    /// (<see cref="OwnershipChainOwnerId"/>); a reference to an object with
    /// another owner breaks the chain and is checked anyway. Dynamic SQL (<c>EXEC('…')</c> / <c>sp_executesql</c>) breaks
    /// the chain: its <see cref="ProcFrame"/> carries
    /// <see cref="Parser.ProcFrame.IsDynamicSql"/>, so checks re-engage, as
    /// they do for a SQLCLR routine's context-connection command
    /// (<see cref="IsContextConnectionCommand"/>) though it carries a trigger's
    /// frame. The
    /// <c>dbo</c> bypass is a separate, cheaper short-circuit the enforcement
    /// helper applies on top of this.
    /// Also false while a module body is being bound at CREATE time
    /// (<see cref="CreateTimeBinding"/>) — the bind resolves names without
    /// reading or writing a row, and a multi-statement-TVF bind carries no
    /// frame of its own to suppress it.
    /// </summary>
    public bool EnforcesPermissions =>
        !this.CreateTimeBinding
        && !this.MultiStatementTvfBody
        && this.UdfFrame is null && (this.TriggerFrame is null || this.IsContextConnectionCommand)
        && (this.ProcFrame is null || this.ProcFrame.IsDynamicSql);

    /// <summary>
    /// Set on a multi-statement table-valued function's body batch, which
    /// carries neither frame but chains ownership like every other module
    /// body (probed 2026-10-04 against SQL Server 2025: a SELECT grant on the
    /// function reads the owner's table through it).
    /// </summary>
    public bool MultiStatementTvfBody;

    /// <summary>
    /// The effective owner of the module whose body this batch runs — a
    /// procedure's, a scalar function's, or a DML trigger's (its table's) —
    /// against which ownership chaining compares each object the body
    /// references: the same owner keeps the chain and skips the caller's check,
    /// any other owner breaks it (probed 2026-09-27 against SQL Server 2025).
    /// Null outside such a body, and in the module bodies that don't set it,
    /// whose references all stay chained.
    /// </summary>
    public int? OwnershipChainOwnerId;

    /// <summary>
    /// The object id of the procedure, scalar or multi-statement function, or
    /// trigger whose body this batch runs — what <c>@@PROCID</c> reads there
    /// (probed 2026-09-28 against SQL Server 2025, whose <c>OBJECT_NAME(@@PROCID)</c>
    /// names each of the four, and reads NULL in an inline function). Zero
    /// elsewhere.
    /// </summary>
    public int ModuleObjectId;

    /// <summary>
    /// True in the body of a function the optimizer doesn't inline — a
    /// multi-statement table-valued function, or a scalar one it can't or won't
    /// inline — whose statements a Query Store records under the function's
    /// object id, where an inlined body is part of its caller's statement.
    /// </summary>
    public bool CapturesQueryStore;

    /// <summary>
    /// Leaf names (<c>#foo</c>) of local temp tables created while this batch's
    /// body executed. Non-null only for a module body — a procedure, trigger,
    /// or dynamic-SQL (<c>EXEC</c> / <c>sp_executesql</c>) scope — where SQL
    /// Server drops a locally-created temp table when the module exits.
    /// <see cref="DropScopedTempTables"/> in the body-dispatch finally drops
    /// them; the session batch leaves this null and its temp tables live until
    /// the session ends (or an explicit <c>DROP</c>).
    /// </summary>
    public List<HeapTable>? ScopedTempTables;

    /// <summary>
    /// The scope a local temp table this batch creates belongs to: 0 for the
    /// session, whose batches share one scope, and a number of its own for
    /// each module body (<see cref="ScopesTempTables"/>), drawn when the body
    /// creates its first.
    /// </summary>
    public int TempTableScopeId()
    {
        if (this.tempTableScopeId == 0 && this.ScopesTempTables)
            this.tempTableScopeId = ++this.Connection.LastTempTableScopeId;
        return this.tempTableScopeId;
    }

    private int tempTableScopeId;

    /// <summary>
    /// Set on the top-level batch of an RPC <c>sp_executesql</c> / <c>sp_execute</c>
    /// / <c>sp_prepexec</c> statement (see
    /// <see cref="SimulatedDbCommand.ScopeTempTablesToBatch"/>): SQL Server runs
    /// that ad-hoc statement in a nested scope, so its temp tables are dropped
    /// when it finishes even though there's no proc / trigger / dynamic-SQL
    /// frame object.
    /// </summary>
    public bool ForceTempTableScope;

    /// <summary>
    /// Set on the batch the TDS endpoint synthesizes for the
    /// <c>sp_cursoropen</c> family (see
    /// <see cref="SimulatedDbCommand.ApiServerCursor"/>), which opens an API
    /// server cursor rather than a T-SQL one. The two origins resolve
    /// sensitivity identically bar one probed split: a KEYSET over a table with
    /// no unique index or clustered key converts to a read-only snapshot for
    /// T-SQL and stays Keyset for the API.
    /// </summary>
    public bool ApiServerCursor;

    /// <summary>
    /// True when this batch is a module body — a procedure, trigger, or
    /// dynamic-SQL scope (or an RPC ad-hoc-statement scope,
    /// <see cref="ForceTempTableScope"/>) — whose locally-created <c>#temp</c>
    /// tables are dropped at module exit (SQL Server's module-scoped temp-table
    /// lifetime).
    /// </summary>
    public bool ScopesTempTables => this.ProcFrame is not null || this.TriggerFrame is not null || this.ForceTempTableScope;

    /// <summary>
    /// Records a local temp table created during this module body so
    /// <see cref="DropScopedTempTables"/> drops it at module exit. A no-op for
    /// the session batch, whose temp tables persist for the session.
    /// </summary>
    public void RegisterScopedTempTable(HeapTable table)
    {
        if (this.ScopesTempTables)
            (this.ScopedTempTables ??= []).Add(table);
    }

    /// <summary>
    /// Drops the local temp tables this module body created, matching SQL
    /// Server's module-scoped lifetime: a statement after the module sees Msg
    /// 208, and a re-entrant call (the same proc invoked twice on one
    /// connection, or tedious's <c>execSql</c> re-running a
    /// <c>create table #t</c> through <c>sp_executesql</c>) re-creates the name
    /// without a Msg 2714 collision. Called from the proc / trigger /
    /// dynamic-SQL body-dispatch finally, so it runs on a body error too.
    /// </summary>
    public void DropScopedTempTables()
    {
        if (this.ScopedTempTables is not { } tables)
            return;
        // By table rather than name: once the body's own is gone, the name may
        // be a caller's again.
        for (var i = tables.Count - 1; i >= 0; i--)
            this.Connection.RemoveTempTable(tables[i]);
    }

    /// <summary>
    /// Scalar UDFs whose EXECUTE permission, and sequences whose UPDATE, has
    /// already been checked (and passed) in this batch, keyed with the
    /// principal it passed for — the once-per-statement memo shared
    /// by the query-context read-source check
    /// (<see cref="PermissionEnforcement.CheckReadSources"/>) and the
    /// non-query invocation-seam check (<c>Simulation.InvokeScalarFunction</c>),
    /// so a UDF invoked in a query isn't re-checked per row and a UDF invoked in
    /// a SET / IF operand is still checked once. Allocated lazily on first use.
    /// </summary>
    public HashSet<long>? ExecuteCheckedFunctionIds;

    /// <summary>
    /// Current grouping-set context — populated by the aggregate executor
    /// during projection of each group, restored to null between groups and
    /// between queries. Non-null surface exposes GROUPING() / GROUPING_ID()
    /// to the projection / HAVING expressions: <see cref="GroupingSetExpressions"/>
    /// is the set's column list (what's *not* grouped away for this row);
    /// <see cref="AllGroupingExpressions"/> is the union across all sets in
    /// the query (used to detect GROUPING(arg) where arg isn't in any
    /// grouping set — Msg 8161). Null outside an aggregate query — bare
    /// <c>SELECT GROUPING(x) FROM t</c> raises Msg 8161 via this null check.
    /// </summary>
    public Expression[]? GroupingSetExpressions;

    /// <summary>
    /// Companion to <see cref="GroupingSetExpressions"/> — the union of all
    /// grouping-set columns across the query. See that field's docs for the
    /// pair's role in GROUPING() validation.
    /// </summary>
    public IReadOnlyList<Expression>? AllGroupingExpressions;

    /// <summary>
    /// How many parallel grouped accumulations this batch has open
    /// (<c>Selection.Execution.AggregateParallel.cs</c>). The engagement gate
    /// requires zero, so an aggregate reached from inside another aggregate's
    /// row stream — a derived table over a grouped body is the shape — stays
    /// serial rather than forking a second worker set. Bumped and restored by
    /// the parallel path itself, always on the thread that owns the batch.
    /// </summary>
    public int ParallelAggregateDepth;

    /// <summary>
    /// Msg 42231 state 3: a <c>DELETE</c> whose foreign keys would cascade,
    /// or set NULL or a default, into a table that carries a vector index —
    /// settled compiling the statement, as its own target is (probed
    /// 2026-09-29 against SQL Server 2025).
    /// </summary>
    public void RejectReferentialDeleteIntoVectorIndex(HeapTable table)
    {
        foreach (var foreignKey in table.IncomingForeignKeys)
        {
            if (foreignKey.DeleteAction != ReferentialAction.NoAction && foreignKey.ChildTable.VectorIndexes.Count > 0)
                this.RejectOptimizedWrite(SimulatedSqlException.VectorIndexedTableIsReadOnly(foreignKey.ChildTable.Name, state: 3));
        }
    }

    /// <summary>
    /// True while the dispatch loop should treat each statement parser as
    /// "parse only" — advance the cursor and resolve names but skip the
    /// actual state mutation (heap inserts/updates/deletes, dict adds for
    /// CREATE TABLE / DECLARE, variable slot writes for SET, transaction
    /// state changes for BEGIN TRAN / COMMIT / ROLLBACK / SAVE, the existence
    /// check + drop for DROP TABLE, the create + bulk insert for SELECT INTO,
    /// the OBJECT_ID lookup for SET IDENTITY_INSERT, and so on). SELECT
    /// statements with this flag set don't yield result sets and don't
    /// update <see cref="SimulatedDbConnection.LastStatementRowCount"/>.
    /// Combines the raw IF skip flag with the in-flight loop-flow signal so
    /// statements after a BREAK / CONTINUE in the same block also skip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deferred name resolution (un-taken IF / WHILE): real SQL Server binds
    /// names lazily, so an un-taken <c>SELECT bad_col FROM bad_table</c> runs
    /// silently. The simulator parses both branches the same way, so its parse
    /// throws — but <c>Simulation.DispatchOneStatement</c> swallows a
    /// name-resolution error (<c>Msg 208</c> / <c>Msg 207</c>) caught while
    /// <see cref="IsSkipping"/> is true, dropping the discarded statement.
    /// BREAK / CONTINUE / RETURN / THROW scope checks (Msg 135 / 136 / 178 /
    /// 10704) are structural, not name resolution, so they explicitly don't
    /// defer — they fire even in skip mode, matching real SQL Server's
    /// compile-time check on those statements.
    /// </para>
    /// </remarks>
    public bool IsSkipping => this.SkipsForControlFlow || this.NoExecActive;

    /// <summary>
    /// <see cref="IsSkipping"/> short of <see cref="NoExecActive"/>: the
    /// statement sits where control flow doesn't reach — an un-taken branch,
    /// past a <c>BREAK</c> / <c>RETURN</c> / pending <c>GOTO</c>, or in the
    /// compile walk — which even <c>SET NOEXEC OFF</c> doesn't run from.
    /// </summary>
    public bool SkipsForControlFlow =>
        this.SkipModeFlag
        || this.LoopControl != LoopControl.None
        || this.ReturnSignaled
        || this.ErrorSignaled
        || this.PendingGotoLabel is not null;

    /// <summary>
    /// Mirrors <see cref="SimulatedDbConnection.NoExec"/> for this batch — set
    /// from the session as a top-level batch starts and by the batch's own
    /// <c>SET NOEXEC</c> — so every statement parses in skip mode without
    /// running while the option is on.
    /// </summary>
    public bool NoExecActive;

    /// <summary>The connection executing this batch.</summary>
    public SimulatedDbConnection Connection => this.Parser.Connection;

    /// <summary>
    /// The id of the transaction the running statement is in: the session's
    /// user transaction's, or else the statement's own autocommit one, drawn
    /// on first ask.
    /// </summary>
    internal long CurrentTransactionId()
    {
        if (this.Connection.CurrentTransaction is { } transaction)
            return transaction.TransactionId;
        var statement = this.CurrentStatement;
        if (statement.AutocommitTransactionId == 0)
            statement.AutocommitTransactionId = this.Connection.Simulation.AllocateTransactionId();
        return statement.AutocommitTransactionId;
    }

    /// <summary>The database this batch is executing against.</summary>
    public Database CurrentDatabase => this.Parser.CurrentDatabase;

    /// <summary>
    /// The database <paramref name="target"/> lives in — the one a write
    /// against it must charge its per-database state to (the rowversion
    /// counter, the version store, trigger dispatch), which is not the
    /// session's database when the statement names the target with a
    /// three-part name. Falls back to <see cref="CurrentDatabase"/> for the
    /// objects that belong to no database: temp tables, table variables,
    /// table-valued parameters, trigger pseudo-tables, and the shared system
    /// tables.
    /// </summary>
    public Database DatabaseFor(SchemaObject target) => target switch
    {
        Storage.HeapTable table => table.OwningDatabase ?? this.CurrentDatabase,
        View view => view.Schema.Database,
        Procedure procedure => procedure.Schema.Database,
        UserDefinedFunction function => function.Schema.Database,
        Synonym synonym => synonym.Schema.Database,
        Sequence sequence => sequence.Schema.Database,
        Trigger trigger => trigger.Schema.Database,
        _ => this.CurrentDatabase,
    };

    /// <summary>
    /// Where each declared variable's <c>DECLARE</c> was written, as the
    /// statement's 0-based offset in the batch text. A loop body re-dispatches
    /// its statements, so the same <c>DECLARE</c> executes once per pass and
    /// must not report Msg 134 the second time — T-SQL hoists the declaration
    /// and leaves only the assignment behind. The offset is what separates
    /// that from a genuine second <c>DECLARE</c> of the same name, which stays
    /// an error however unreachable it is.
    /// </summary>
    public readonly Dictionary<string, int> VariableDeclarationSites = new(VariableNameComparer);

    /// <summary>How <see cref="Variables"/> matches names: ignoring case, kana type and width.</summary>
    /// <remarks>
    /// Unlike object identifiers (which follow the database collation — see the
    /// name-comparison regimes in <c>docs/claude/collations.md</c>),
    /// variable names fold case, width, and kana type regardless of the
    /// database collation — probe-confirmed (2026-07-13) on a real
    /// <c>SQL_Latin1_General_CP1_CS_AS</c> database: <c>declare @vx int;
    /// set @VX = 5</c> succeeds, as does a fullwidth <c>@ｖx</c>
    /// declaration referenced as <c>@vx</c>.
    /// </remarks>
    internal static readonly MemoizedNameComparer VariableNameComparer = new(
        System.Globalization.CompareOptions.IgnoreCase
        | System.Globalization.CompareOptions.IgnoreKanaType
        | System.Globalization.CompareOptions.IgnoreWidth);

    /// <summary>How <see cref="TableVariables"/> and <see cref="CursorVariables"/> match names: ignoring case.</summary>
    internal static readonly MemoizedNameComparer TableVariableNameComparer = new(System.Globalization.CompareOptions.IgnoreCase);

    /// <summary>
    /// The documents behind this batch's <c>.nodes()</c> rows, allocated by the
    /// first one. A row carries its node's position and this registry's number
    /// for the document rather than a copy of the document, so shredding a
    /// large instance stays linear; the reference can't leave the statement
    /// that produced it (only the xml methods may read it), so the batch
    /// outlives every reader.
    /// </summary>
    internal Storage.XmlDocumentRegistry? XmlNodeDocuments;

    /// <summary>
    /// Per-batch variable store. Seeded with SqlClient parameters at
    /// construction; <c>DECLARE</c> adds entries; <c>SET</c> /
    /// <c>SELECT @v = expr</c> mutate them. Parameters and declared variables
    /// share a namespace — a <c>DECLARE</c> whose name collides with a
    /// parameter raises Msg 134 (probe-confirmed: real SQL Server treats
    /// SqlClient parameters as if they were already declared). End-of-batch
    /// write-back to <c>InputOutput</c> / <c>Output</c> direction parameters
    /// reads from this store.
    /// </summary>
    public readonly Dictionary<string, VariableSlot> Variables;

    /// <summary>
    /// Per-batch table-variable store keyed by name with the leading <c>@</c>
    /// stripped (mirrors <see cref="Variables"/>'s keying convention).
    /// <c>DECLARE @t TABLE (...)</c> adds an entry; the dict is discarded with
    /// the <see cref="BatchContext"/> at end of batch, providing the
    /// per-batch lifetime real SQL Server documents. Variable names live in
    /// a shared namespace with <see cref="Variables"/> — a <c>DECLARE @t int</c>
    /// followed by <c>DECLARE @t TABLE (...)</c> raises Msg 134
    /// (probe-confirmed: real SQL Server's name-uniqueness check is per-name,
    /// not per-kind).
    /// </summary>
    public readonly Dictionary<string, HeapTable> TableVariables = new(TableVariableNameComparer);

    /// <summary>
    /// LOCAL cursors declared in this batch / procedure / trigger frame
    /// (<c>DECLARE … CURSOR LOCAL FOR …</c>). Scoped to the frame: implicitly
    /// deallocated when the frame exits (probe-confirmed a LOCAL cursor is
    /// gone in the next GO-separated batch on the same connection), unless a
    /// cursor variable still references it (the refcount keeps the underlying
    /// <see cref="Cursor"/> object alive past its name-scope). GLOBAL cursors
    /// live on <see cref="SimulatedDbConnection.Cursors"/> instead and persist
    /// for the connection. Names are bare identifiers keyed like the global map.
    /// </summary>
    public readonly Dictionary<string, Cursor> LocalCursors = new(BuiltInToken.Comparer);

    /// <summary>
    /// Cursor variables (<c>DECLARE @c CURSOR</c>) declared or seeded in this
    /// frame, keyed by name with the leading <c>@</c> stripped (mirroring
    /// <see cref="Variables"/>). The value is the referenced <see cref="Cursor"/>
    /// object, or null for a declared-but-unallocated variable (Msg 16950 on
    /// use). Distinct namespace from scalar / table variables. Refcounted:
    /// binding increments <see cref="Cursor.VariableRefCount"/>, rebinding /
    /// <c>DEALLOCATE @c</c> / frame exit decrements and tears down at zero.
    /// </summary>
    public readonly Dictionary<string, Cursor?> CursorVariables = new(TableVariableNameComparer);

    /// <summary>
    /// Monotonically-increasing per-row stamp consumed by
    /// <c>NEXT VALUE FOR</c>. The per-row iterator at each DML / SELECT
    /// site bumps this just before evaluating row expressions, so multiple
    /// <c>NEXT VALUE FOR seq</c> calls within one row of one statement
    /// share a cache entry and emit the same value (probe-confirmed against
    /// SQL Server 2025: <c>INSERT VALUES (next, next)</c> writes the same
    /// value into both columns; <c>SELECT next, next FROM 3-row-table</c>
    /// advances per row but pairs columns per-row). Non-iterating expressions
    /// (one-shot <c>SET @v = next value for seq</c>, scalar <c>SELECT</c>)
    /// bump exactly once via the helper. Wraparound at <see cref="long.MaxValue"/>
    /// isn't a concern — 2^63 row iterations per batch is unreachable.
    /// </summary>
    public long CurrentRowStamp;

    /// <summary>
    /// Per-batch cache of last-emitted sequence values, keyed by sequence
    /// reference. <c>NEXT VALUE FOR seq</c> first consults this dict: if the
    /// stored stamp matches <see cref="CurrentRowStamp"/>, the cached value
    /// is reused (same-row dedup); otherwise the sequence is advanced and
    /// the cache slot updated. Cleared via dictionary turnover rather than
    /// per-statement reset because the stamp-equality check makes stale
    /// entries automatically invalid.
    /// </summary>
    public readonly Dictionary<Sequence, (long Stamp, SqlValue Value)> SequenceRowCache = [];

    /// <summary>
    /// Every value drawn by row stamp, kept while a multi-row <c>INSERT …
    /// VALUES</c> computes its tuples ahead of writing them, so a row's DEFAULT
    /// re-entering its tuple's stamp finds the value that tuple drew after
    /// later tuples have moved <see cref="SequenceRowCache"/> on. Null otherwise.
    /// </summary>
    public Dictionary<(Sequence Sequence, long Stamp), SqlValue>? SequenceValuesByRow;

    /// <summary>
    /// The <c>FOR SYSTEM_TIME</c> a view reference applies to the body this
    /// batch runs or binds — a nested view's body inherits it in turn — or
    /// null outside such a body.
    /// </summary>
    public InheritedSystemTime? InheritedSystemTime;

    /// <summary>
    /// Bumps <see cref="CurrentRowStamp"/> to start a new per-row evaluation
    /// scope. Called by per-row iterators (SELECT projection, INSERT VALUES,
    /// INSERT SELECT, UPDATE / DELETE, DEFAULT-clause evaluation during
    /// INSERT) and by one-shot expression sites (DECLARE @v initializer,
    /// SET @v assignment, RETURN expression) before evaluating any expression
    /// in the new scope. All <c>NEXT VALUE FOR</c> calls within the bump
    /// boundary that target the same sequence return the same value.
    /// </summary>
    public void BumpRowStamp() => this.CurrentRowStamp++;

    public BatchContext(SimulatedDbCommand command)
    {
        this.NoExecActive = command.Connection!.NoExec;
        this.Variables = SeedVariables(command);
        this.Parser = new ParserContext(command, this);
        SeedTableVariablesFromStructuredParameters(this, command);
    }

    /// <summary>
    /// Constructs a batch for scalar-UDF body re-dispatch. The
    /// <paramref name="udfBodyCommand"/> wraps the UDF's stored body source
    /// (its <c>CommandText</c>) and is constructed with the outer call site's
    /// <see cref="SimulatedDbConnection"/>, so the child batch sees the same
    /// connection / database / transaction state as the caller. Variables are
    /// pre-seeded with the function's argument values; the
    /// <paramref name="udfFrame"/> gates value-form <c>RETURN</c> inside the
    /// body and lands the return value for the caller to read. The call
    /// site drains yielded result sets (Msg 444 territory in real SQL
    /// Server — UDF bodies aren't allowed to surface result sets).
    /// </summary>
    public BatchContext(SimulatedDbCommand udfBodyCommand, Dictionary<string, VariableSlot> variables, UdfFrame udfFrame)
    {
        this.Variables = variables;
        this.UdfFrame = udfFrame;
        this.Parser = new ParserContext(udfBodyCommand, this);
    }

    /// <summary>
    /// Constructs a batch for stored-procedure-body re-dispatch. Like the
    /// UDF body constructor, the <paramref name="procBodyCommand"/> wraps
    /// the procedure's stored body source and shares the caller's connection
    /// / database / transaction state. Parameters pre-seed
    /// <paramref name="variables"/>; the <paramref name="procFrame"/> gates
    /// value-form <c>RETURN</c> and captures the return code. Result sets
    /// propagate to the outer caller — the call site yields them through
    /// (distinct from UDF bodies, where they're discarded).
    /// </summary>
    public BatchContext(SimulatedDbCommand procBodyCommand, Dictionary<string, VariableSlot> variables, ProcFrame procFrame, Dictionary<string, HeapTable>? tableVariables = null)
    {
        this.Variables = variables;
        this.ProcFrame = procFrame;
        this.Parser = new ParserContext(procBodyCommand, this);
        if (tableVariables is not null)
        {
            foreach (var kvp in tableVariables)
                this.TableVariables[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Constructs a batch for multi-statement-TVF body re-dispatch. Like the
    /// UDF / proc body constructors, the
    /// <paramref name="multiStatementTvfBodyCommand"/> wraps the function's
    /// stored body source and shares the caller's connection / database /
    /// transaction state via that command's connection. Parameters pre-seed
    /// <paramref name="variables"/>. **No frame is set**: MS-TVF bodies
    /// disallow value-form <c>RETURN N</c> (the existing RETURN-statement
    /// parser raises Msg 178 when both
    /// <see cref="UdfFrame"/> and <see cref="ProcFrame"/> are null,
    /// matching real SQL Server's probe-confirmed CREATE-time rejection).
    /// Bare <c>RETURN;</c> still sets <see cref="ReturnSignaled"/> the
    /// usual way. The caller (<see cref="Simulation.InvokeMultiStatementTvf"/>)
    /// pre-seeds the function's <c>@r</c> return-table variable into
    /// <see cref="TableVariables"/> after construction.
    /// </summary>
    public BatchContext(SimulatedDbCommand multiStatementTvfBodyCommand, Dictionary<string, VariableSlot> variables)
    {
        this.Variables = variables;
        this.Parser = new ParserContext(multiStatementTvfBodyCommand, this);
    }

    /// <summary>
    /// Constructs a batch for trigger-body re-dispatch. Mirrors the proc
    /// body constructor's shape (re-tokenize body source, share caller's
    /// connection / transaction / undo log via the outer batch's state)
    /// but seeds a fresh empty <see cref="Variables"/> dict and routes
    /// the <c>INSERTED</c> / <c>DELETED</c> pseudo-tables through the new
    /// <see cref="TriggerFrame"/>. Trigger bodies don't take parameters
    /// in the procedure sense and don't have a value-form RETURN, so no
    /// frame analogous to <see cref="ProcFrame"/> is needed for those —
    /// but result sets from SELECT statements in the body propagate to
    /// the outer caller (same as procedures; probe-confirmed:
    /// <c>create trigger ... as select 1</c> yields the result set).
    /// </summary>
    public BatchContext(SimulatedDbCommand triggerBodyCommand, TriggerFrame triggerFrame)
    {
        this.Variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        this.TriggerFrame = triggerFrame;
        this.Parser = new ParserContext(triggerBodyCommand, this);
    }

    /// <summary>
    /// Constructs a batch for one command a SQLCLR routine runs on its context
    /// connection. The command's parameters pre-seed
    /// <paramref name="variables"/>; a trigger's routine passes its
    /// <paramref name="triggerFrame"/>, so the command reads the firing
    /// statement's <c>INSERTED</c> / <c>DELETED</c> and <c>EVENTDATA()</c>
    /// as a T-SQL body does. No procedure frame is set: the text is a batch of
    /// its own, where a value-form <c>RETURN</c> is Msg 178 (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    public BatchContext(SimulatedDbCommand contextCommand, Dictionary<string, VariableSlot> variables, TriggerFrame? triggerFrame)
    {
        this.Variables = variables;
        this.TriggerFrame = triggerFrame;
        this.IsContextConnectionCommand = true;
        this.ForceTempTableScope = true;
        this.Parser = new ParserContext(contextCommand, this);
    }

    /// <summary>
    /// True for a command a SQLCLR routine runs on its context connection
    /// (see the constructor taking one), which carries a trigger's frame for
    /// its pseudo-tables but is checked for permissions as the caller's own
    /// statement is.
    /// </summary>
    public readonly bool IsContextConnectionCommand;

    /// <summary>
    /// Set on a context-connection command of a SQLCLR function marked
    /// <c>SystemDataAccessKind.Read</c> but not <c>DataAccessKind.Read</c>,
    /// whose reads of a user object are Msg 589.
    /// </summary>
    public bool RestrictsUserData;

    /// <summary>
    /// Joins the temp-table scope <paramref name="scopeId"/> rather than
    /// drawing one: every command one SQLCLR routine runs on its context
    /// connection shares the routine's scope, so a <c>#temp</c> one command
    /// creates is there for the next and goes when the routine returns
    /// (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public void JoinTempTableScope(int scopeId, List<HeapTable> scopedTempTables)
    {
        this.tempTableScopeId = scopeId;
        this.ScopedTempTables = scopedTempTables;
    }

    private static Dictionary<string, VariableSlot> SeedVariables(SimulatedDbCommand command)
    {
        var dict = new Dictionary<string, VariableSlot>(command.Parameters.Count, BatchContext.VariableNameComparer);
        foreach (SimulatedDbParameter parameter in command.Parameters)
        {
            // Skip structured / table-valued parameters here — they land in
            // TableVariables via SeedTableVariablesFromStructuredParameters.
            // Detection: a DataTable or IDataReader value combined with a
            // non-empty TypeName. SqlDbType.Structured itself isn't directly
            // observable on DbParameter (the simulator doesn't expose a
            // SqlDbType property), so the value-type + TypeName combination
            // is the signal.
            if (IsTableValuedParameterValue(parameter))
                continue;
            var name = parameter.ParameterName;
            if (name.StartsWith('@'))
                name = name[1..];
            // A parameter carrying a pre-built SqlValue binds it verbatim — the
            // path the TDS listener's CLR-UDT (hierarchyid / geography /
            // geometry) and sql_variant RPC parameters take, whose wire form
            // decodes straight to a typed SqlValue that no DbType can express.
            if (parameter.Value is SqlValue preBuilt)
            {
                dict[name] = new VariableSlot(preBuilt.Type, declaredMaxLength: null, preBuilt, parameter);
                continue;
            }

            var dbType = SqlType.GetByDbType(parameter.DbType);
            // SqlClient's Size = -1 convention declares a MAX-typed
            // parameter (varchar(max) / nvarchar(max) / varbinary(max)).
            // Honoring it matters beyond fidelity: the TDS listener projects
            // `select @p` with this declared type, and a MAX value over
            // 65535 bytes cannot be represented by the bounded wire form.
            var maxDeclared = parameter.Size == SqlType.MaxLengthSentinel ? AsMaxVariant(dbType) : null;
            if (maxDeclared is not null)
                dbType = maxDeclared;
            var seed = parameter.Value is null or DBNull
                ? SqlValue.Null(dbType)
                : dbType.ConvertParameter(parameter.Value);
            // For decimal / numeric parameters, ConvertParameter widens the
            // declared type to fit the value's natural scale (e.g. caller sends
            // 123.45m without an explicit scale → widens to decimal(28, 2)).
            // Track the post-widen type so VariableReference.GetSqlType returns
            // the right schema and downstream readers don't truncate.
            var declaredType = maxDeclared ?? (seed.IsNull ? dbType : seed.Type);
            dict[name] = new VariableSlot(declaredType, declaredMaxLength: null, seed, parameter);
        }
        return dict;
    }

    /// <summary>
    /// The MAX-typed variant of a variable-length parameter type, or null
    /// when the type has no MAX form. Applied when a parameter declares
    /// SqlClient's <c>Size = -1</c> MAX convention — both the batch
    /// variable-seed path and the direct stored-procedure binding.
    /// </summary>
    internal static SqlType? AsMaxVariant(SqlType dbType) => dbType switch
    {
        VarcharSqlType varchar => VarcharSqlType.Get(SqlType.MaxLengthSentinel, varchar.Collation ?? Collation.Baseline, varchar.Coercibility),
        NVarcharSqlType nvarchar => NVarcharSqlType.Get(SqlType.MaxLengthSentinel, nvarchar.Collation ?? Collation.Baseline, nvarchar.Coercibility),
        VarbinarySqlType => VarbinarySqlType.Get(SqlType.MaxLengthSentinel),
        _ => null,
    };

    /// <summary>
    /// True when <paramref name="parameter"/> looks like a table-valued
    /// parameter: a <see cref="System.Data.DataTable"/> or
    /// <see cref="System.Data.IDataReader"/>-typed <see cref="DbParameter.Value"/>.
    /// <c>TypeName</c> presence is required for a valid TVP but isn't
    /// gated here — a missing <c>TypeName</c> raises an explicit
    /// <see cref="ArgumentException"/> at materialization (mirroring
    /// <c>Microsoft.Data.SqlClient</c>'s client-side check).
    /// </summary>
    internal static bool IsTableValuedParameterValue(SimulatedDbParameter parameter) =>
        parameter.Value is System.Data.DataTable or System.Data.IDataReader or TableValuedParameterData;

    /// <summary>
    /// Materializes each TVP-shaped <see cref="SimulatedDbParameter"/> into the
    /// batch's <see cref="TableVariables"/> dict. Reads
    /// <see cref="SimulatedDbParameter.TypeName"/> off the parameter to look up
    /// the registered <see cref="TableType"/>;
    /// resolves the value source (<see cref="System.Data.DataTable"/> or
    /// <see cref="System.Data.IDataReader"/>) into rows via the type's
    /// <see cref="TableType.Clone"/> + per-row INSERT path. The clone is
    /// flagged as a TVP (<see cref="HeapTable.IsTableValuedParameter"/>)
    /// so any downstream DML attempt against the bound name raises Msg 10700.
    /// </summary>
    private static void SeedTableVariablesFromStructuredParameters(BatchContext batch, SimulatedDbCommand command)
    {
        foreach (SimulatedDbParameter parameter in command.Parameters)
        {
            if (!IsTableValuedParameterValue(parameter))
                continue;
            var typeName = parameter.TypeName;
            if (string.IsNullOrEmpty(typeName))
                throw new ArgumentException($"The table type parameter '{parameter.ParameterName}' must have a valid type name.", parameter.ParameterName);

            var parsedTypeName = ParseSimpleQualifiedName(typeName);
            if (!batch.TryResolveTableType(parsedTypeName, out var tableType))
                throw SimulatedSqlException.CannotFindDataType(parameterIndex: 1, typeName, parameter.ParameterName);

            var paramName = parameter.ParameterName;
            if (paramName.StartsWith('@'))
                paramName = paramName[1..];
            var clone = tableType.Clone("@" + paramName, batch, isTableValuedParameter: true);
            MaterializeTvpRows(parameter.Value!, tableType, clone, batch);
            batch.TableVariables[paramName] = clone;
        }
    }

    private static MultiPartName ParseSimpleQualifiedName(string typeName)
    {
        var trimmed = typeName.Trim();
        var firstDot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (firstDot < 0)
            return new MultiPartName(trimmed);
        var schema = trimmed[..firstDot].Trim();
        var leaf = trimmed[(firstDot + 1)..].Trim();
        return new MultiPartName(schema).WithAddedPart(leaf);
    }

    private static void MaterializeTvpRows(object source, TableType tableType, HeapTable destination, BatchContext batch)
    {
        switch (source)
        {
            case System.Data.DataTable dt:
                if (dt.Columns.Count != tableType.Columns.Length)
                    throw SimulatedSqlException.TableValuedParameterColumnCountMismatch(dt.Columns.Count, tableType.Columns.Length);
                foreach (System.Data.DataRow row in dt.Rows)
                    InsertOneRowFromValueArray(row.ItemArray, tableType, destination, batch);
                break;
            case System.Data.IDataReader reader:
                if (reader.FieldCount != tableType.Columns.Length)
                    throw SimulatedSqlException.TableValuedParameterColumnCountMismatch(reader.FieldCount, tableType.Columns.Length);
                var buffer = new object?[reader.FieldCount];
                while (reader.Read())
                {
                    for (var i = 0; i < buffer.Length; i++)
                        buffer[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    InsertOneRowFromValueArray(buffer, tableType, destination, batch);
                }
                break;
            case TableValuedParameterData wire:
                // A TVP decoded over the TDS wire. ColumnCount -1 marks a
                // TVP_NULL value (whole parameter NULL): it binds an empty table
                // variable and skips the arity check, matching an unsupplied
                // TVP. Otherwise the client-declared column count drives the
                // Msg 500 arity check even when zero rows were sent.
                if (wire.ColumnCount >= 0 && wire.ColumnCount != tableType.Columns.Length)
                    throw SimulatedSqlException.TableValuedParameterColumnCountMismatch(wire.ColumnCount, tableType.Columns.Length);
                foreach (var row in wire.Rows)
                    InsertOneRowFromSqlValues(row, tableType, destination, batch);
                break;
        }
    }

    private static void InsertOneRowFromValueArray(object?[] sourceValues, TableType tableType, HeapTable destination, BatchContext batch)
    {
        // Build a SqlValue[] matching the destination's stored column order.
        // Identity columns are not allowed to receive caller-supplied values
        // through a TVP (probe-confirmed: real SQL Server raises Msg 1077).
        // Position-based mapping: source column N → destination column N.
        // Real SQL Server ignores DataTable column names entirely (probe-
        // confirmed F2 / F2b — reversing the order with matching names
        // reverses the values).
        var fullValues = new SqlValue[tableType.Columns.Length];
        for (var i = 0; i < tableType.Columns.Length; i++)
        {
            var column = tableType.Columns[i];
            if (column.Identity is not null && sourceValues[i] is not null and not DBNull)
                throw SimulatedSqlException.InsertIntoIdentityColumnNotAllowedOnTableVariables();
            // Identity columns get the next auto-allocated value.
            if (column.Identity is not null)
            {
                fullValues[i] = Simulation.CoerceForIdentity(Simulation.GenerateIdentity(column), column);
                continue;
            }
            fullValues[i] = sourceValues[i] is null or DBNull
                ? SqlValue.Null(column.Type)
                : column.Type.ConvertParameter(sourceValues[i]!);
        }
        // Evaluate computed columns, enforce NOT NULL / CHECK / PK / UNIQUE, and
        // insert through the shared engine path so a TVP whose rows violate the
        // table type's constraints raises the same Msg 515 / 547 / 2627 / 2601
        // real SQL Server raises (probe-confirmed 2026-07-18 over the wire).
        Simulation.InsertTableValuedParameterRow(destination, fullValues, batch);
    }

    /// <summary>
    /// The <see cref="Storage.SqlValue"/>-typed sibling of
    /// <see cref="InsertOneRowFromValueArray"/> for TVP rows decoded off the TDS
    /// wire, where each cell already carries its wire type. Coercion to the
    /// destination column runs through <see cref="Storage.SqlValue.CoerceTo"/>,
    /// so a type mismatch (e.g. a reordered column putting an nvarchar value
    /// under an int column) raises Msg 245 with the correct source-type name,
    /// matching real SQL Server's wire behavior.
    /// </summary>
    private static void InsertOneRowFromSqlValues(SqlValue[] sourceValues, TableType tableType, HeapTable destination, BatchContext batch)
    {
        var fullValues = new SqlValue[tableType.Columns.Length];
        for (var i = 0; i < tableType.Columns.Length; i++)
        {
            var column = tableType.Columns[i];
            if (column.Identity is not null && !sourceValues[i].IsNull)
                throw SimulatedSqlException.InsertIntoIdentityColumnNotAllowedOnTableVariables();
            if (column.Identity is not null)
            {
                fullValues[i] = Simulation.CoerceForIdentity(Simulation.GenerateIdentity(column), column);
                continue;
            }
            fullValues[i] = sourceValues[i].IsNull ? SqlValue.Null(column.Type) : sourceValues[i].CoerceTo(column.Type);
        }
        Simulation.InsertTableValuedParameterRow(destination, fullValues, batch);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a live <see cref="VariableSlot"/>
    /// reference. Captured at parse time by <see cref="Expressions.VariableReference"/>
    /// so subsequent <c>SET</c> / <c>SELECT @v = expr</c> mutations are
    /// observable when the expression evaluates at runtime — the dictionary
    /// is append-only within a batch (re-DECLARE raises Msg 134), so a slot
    /// reference captured during parse stays valid.
    /// </summary>
    /// <exception cref="SimulatedSqlException">Must declare the scalar variable \"@{value of <paramref name="name"/>}\".</exception>
    public VariableSlot GetVariableSlot(string name) =>
        Variables.TryGetValue(name, out var slot)
        ? slot
        : throw (this.TableVariables.ContainsKey(name)
            ? SimulatedSqlException.TableVariableUsedAsScalar(name)
            : this.CursorVariables.ContainsKey(name)
                ? SimulatedSqlException.CursorVariableUsedAsScalar(name)
                : SimulatedSqlException.MustDeclareScalarVariable(name));

    /// <summary>
    /// While a cursor's query parses, the copies of the variables it reads,
    /// holding their values at the <c>DECLARE</c>: a cursor reads its
    /// variables as they were declared, whatever they hold when it opens or
    /// fetches (probed 2026-10-02 against SQL Server 2025, every cursor type).
    /// Null otherwise.
    /// </summary>
    public Dictionary<string, VariableSlot>? CursorDeclarationSnapshot;

    /// <summary>
    /// The copy of <paramref name="slot"/> a cursor declaration reads — see
    /// <see cref="CursorDeclarationSnapshot"/> — or null outside one.
    /// </summary>
    public VariableSlot? DeclarationSnapshotOf(string name, VariableSlot slot)
    {
        if (this.CursorDeclarationSnapshot is not { } snapshot)
            return null;
        if (!snapshot.TryGetValue(name, out var copy))
        {
            snapshot[name] = copy = new VariableSlot(slot.DeclaredType, slot.DeclaredMaxLength, slot.Value, parameter: null)
            {
                XmlSchemaCollection = slot.XmlSchemaCollection,
                SpelledNumeric = slot.SpelledNumeric,
                AliasType = slot.AliasType,
            };
        }
        return copy;
    }
}
