using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Data.Common;

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
internal sealed class BatchContext
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
            return true;
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
    /// happens. The in-process path aborts by throwing instead, so it never
    /// sets this.
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
    /// Records every lock acquisition and <c>NOWAIT</c> table while non-null —
    /// armed by the SELECT arm around a top-level statement's parse in a batch
    /// the plan cache may store. Taking schema-stability and table-level data
    /// locks is part of parsing a SELECT here, so a replay, which parses
    /// nothing, takes the recorded list again as the replaying session.
    /// </summary>
    public List<ReplayedLock>? ReplayLockLog;

    /// <summary>
    /// Takes <paramref name="locks"/> — a cached statement's parse-time
    /// acquisitions — as this batch's session, in the order the parse took them.
    /// </summary>
    public void TakeReplayedLocks(ReplayedLock[] locks)
    {
        foreach (var taken in locks)
        {
            if (taken.NoWaitTable is { } table)
                _ = this.noWaitTables.Add(table);
            else if (taken.TransactionScoped)
                this.AcquireTransactionLock(taken.Resource!, taken.Mode, taken.NoWait);
            else
                this.AcquireStatementLock(taken.Resource!, taken.Mode, taken.NoWait);
        }
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
        && this.UdfFrame is null && (this.TriggerFrame is null || this.IsContextConnectionCommand)
        && (this.ProcFrame is null || this.ProcFrame.IsDynamicSql);

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
    /// Object ids of scalar UDFs whose EXECUTE permission has already been
    /// checked (and passed) in this batch — the once-per-statement memo shared
    /// by the query-context read-source check
    /// (<see cref="PermissionEnforcement.CheckReadSources"/>) and the
    /// non-query invocation-seam check (<c>Simulation.InvokeScalarFunction</c>),
    /// so a UDF invoked in a query isn't re-checked per row and a UDF invoked in
    /// a SET / IF operand is still checked once. Allocated lazily on first use.
    /// </summary>
    public HashSet<int>? ExecuteCheckedFunctionIds;

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
    /// Schema-stability and schema-modification locks acquired during the
    /// current statement's dispatch. Each successful TryResolve*-side
    /// acquisition (Sch-S on the resolved schema object) and each DDL-side
    /// acquisition (Sch-M on the target before mutation) appends here; the
    /// dispatch loop releases every entry in a <c>finally</c> at statement
    /// end so locks are returned regardless of success / error / TRY-catch
    /// outcome. Re-entrance (same object resolved twice in one statement —
    /// e.g. <c>FROM t a JOIN t b</c>) is handled inside
    /// <see cref="LockResource"/> via per-owner counting; this list just
    /// tracks every acquisition by reference so Release runs the matching
    /// number of times.
    /// </summary>
    public readonly List<(LockResource Resource, LockMode Mode)> StatementSchemaLocks = [];

    /// <summary>
    /// Tables the current statement resolved with a <c>NOWAIT</c> table hint.
    /// Real scopes the hint to the table it sits on, but the per-row lock
    /// acquisitions happen far from the hint list — every DML path reaches
    /// <see cref="AcquireRowLockTxScoped"/> with a table and a RID and nothing
    /// else — so the hinted tables are recorded here at
    /// <see cref="AcquireDataLockIfApplicable"/> and looked up there. Cleared
    /// with the statement's locks, so a hint doesn't leak into the batch's
    /// next statement.
    /// </summary>
    private readonly HashSet<HeapTable> noWaitTables = [];

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/> for
    /// the current connection, honoring the connection's
    /// <see cref="SimulatedDbConnection.LockTimeoutMillis"/>, and records the
    /// acquisition in <see cref="StatementSchemaLocks"/> so the dispatch
    /// loop releases it at statement end. The two-phase split (acquire then
    /// record) is fine because <see cref="LockManager.Acquire"/> can only
    /// fail by throwing — on success the lock IS held, and we always reach
    /// the append. On throw the lock isn't held, no cleanup needed.
    /// </summary>
    public void AcquireStatementLock(LockResource resource, LockMode mode, bool noWait = false)
    {
        this.ReplayLockLog?.Add(new ReplayedLock(resource, mode, noWait, transactionScoped: false));
        var connection = this.Connection;
        connection.Simulation.LockManager.Acquire(resource, mode, connection.Session, noWait ? 0 : connection.LockTimeoutMillis);
        this.StatementSchemaLocks.Add((resource, mode));
    }

    /// <summary>
    /// The Sch-M a statement that redefines <paramref name="table"/> or
    /// rewrites its rows wholesale — <c>ALTER TABLE</c>, <c>TRUNCATE</c>,
    /// <c>SWITCH</c> — takes: on its schema lock for the statement, and on its
    /// data lock to the transaction's end, which waits out every transaction
    /// still holding the table's intent lock and holds new readers and writers
    /// off until this one settles, as real's object Sch-M does (probed
    /// 2026-10-01 against SQL Server 2025: a <c>TRUNCATE</c> behind an open
    /// insert waits <c>LCK_M_SCH_M</c> on the object until it commits).
    /// Without the second, the statement swapped the rows out from under an
    /// open writer, whose rollback then wrote into pages that were gone.
    /// </summary>
    public void AcquireTableRedefinitionLock(HeapTable table)
    {
        this.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);
        if (Simulation.IsLockableTable(table))
            this.AcquireTransactionLock(table.TableDataLock, LockMode.SchemaModification);
    }

    /// <summary>
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/> for
    /// the current connection and records the acquisition against the
    /// active <see cref="SimulatedDbTransaction"/>, so the lock releases at
    /// COMMIT / ROLLBACK instead of statement end. Used for X data locks
    /// (which must span the transaction under READ COMMITTED) and HOLDLOCK-
    /// upgraded S locks (held until tx end matching SERIALIZABLE).
    /// </summary>
    /// <remarks>
    /// When no transaction is active, the lock falls back to statement-end
    /// release (recorded in <see cref="StatementSchemaLocks"/>) — auto-
    /// commit semantics, matching real SQL Server's implicit-commit-after-
    /// statement behavior for DML outside <c>BEGIN TRAN</c>.
    /// </remarks>
    public void AcquireTransactionLock(LockResource resource, LockMode mode, bool noWait = false)
    {
        this.ReplayLockLog?.Add(new ReplayedLock(resource, mode, noWait, transactionScoped: true));
        var connection = this.Connection;
        connection.Simulation.LockManager.Acquire(resource, mode, connection.Session, noWait ? 0 : connection.LockTimeoutMillis);
        if (connection.CurrentTransaction is { } tx)
            tx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
    }

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
    /// Phase-1b entry point: acquire the appropriate table-level data lock
    /// (IS / IX / SIX / S / U / X) on <paramref name="table"/> and return a
    /// <see cref="DataLockPlan"/> describing what per-row lock the caller
    /// should acquire / probe as it enumerates or mutates rows. Routing
    /// depends on direction (<paramref name="isWrite"/>), hints
    /// (<paramref name="hints"/>), and the session's
    /// <see cref="SimulatedDbConnection.SessionIsolationLevel"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Table-level mode selection:
    /// <list type="bullet">
    /// <item><c>TABLOCKX</c> on read or write → table-X (skips row-level).</item>
    /// <item><c>TABLOCK</c> on read → table-S; on write → table-X.</item>
    /// <item>Write (no TABLOCK*) → table-IX.</item>
    /// <item>Read <c>XLOCK</c> / <c>UPDLOCK</c> → table-IX (intent to write).</item>
    /// <item>Read session <c>SERIALIZABLE</c> (no TABLOCK*) → table-IS
    /// tx-scoped, with phantom protection deferred to whoever consumes the
    /// source: key-range locks on the keys the predicate reaches when it is
    /// sargable on an indexed leading column, on every key otherwise, and a
    /// table-S over a heap. See <see cref="EnsureSerializableTableLock"/>.</item>
    /// <item>Read session <c>READ UNCOMMITTED</c> / hint <c>NOLOCK</c> → no
    /// table-level lock acquired (dirty read).</item>
    /// <item>Read default (RC / RR / HOLDLOCK hint) → table-IS, tx-scoped
    /// under RR, whose row locks outlive the statement.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Per-row plan:
    /// <list type="bullet">
    /// <item>NOLOCK / RU isolation → no per-row lock; reader doesn't probe.</item>
    /// <item>RC reader → no row-S acquisition; reader probes each row for
    /// an incompatible row-X holder and waits (or skips with READPAST).</item>
    /// <item>RR reader → row-S tx-scoped per row it returns.</item>
    /// <item>SERIALIZABLE reader → no per-row acquire; the key locks (or a
    /// heap's table-S) cover both the rows read and the gaps between them,
    /// since a writer tests its row's keys against every held key lock
    /// whatever its own isolation level.</item>
    /// <item>UPDLOCK reader → row-U tx-scoped per touched row.</item>
    /// <item>XLOCK reader → row-X tx-scoped per touched row.</item>
    /// <item>Writer (table-IX) → row-X tx-scoped per mutated row.</item>
    /// <item>TABLOCK* → no per-row lock (the table-level lock covers).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Skips data-lock acquisition entirely for tables that aren't shared
    /// across connections (table variables, local temp tables, system
    /// tables) — same set that <see cref="TryResolveTable"/> bypasses for
    /// Sch-S acquisition. Returns <see cref="DataLockPlan.Bypass"/> in
    /// that case so the caller's per-row logic naturally short-circuits.
    /// </para>
    /// </remarks>
    public DataLockPlan AcquireDataLockIfApplicable(HeapTable table, Selection.TableHintInfo hints, bool isWrite)
    {
        // A vector index makes its table read-only, which real settles
        // optimizing the writing statement — an un-taken branch included
        // when the batch compiles before it runs — and which ends the batch.
        if (isWrite && table.VectorIndexes.Count > 0)
            this.RejectOptimizedWrite(SimulatedSqlException.VectorIndexedTableIsReadOnly(table.Name));

        // A skipped statement — an un-taken branch, or a batch compiling before
        // it runs — touches no rows, and a transaction-scoped lock taken for it
        // would outlive it.
        if (this.IsSkipping)
            return DataLockPlan.Bypass;
        if (table.IsTableVariable || IsLocalTempName(table.Name))
            return DataLockPlan.Bypass;
        if (Simulation.SystemHeapTables.Values.Contains(table))
            return DataLockPlan.Bypass;

        if (hints.NoWait)
        {
            _ = this.noWaitTables.Add(table);
            this.ReplayLockLog?.Add(new ReplayedLock(table));
        }

        var connection = this.Connection;
        var isolation = connection.SessionIsolationLevel;

        // SNAPSHOT isolation reaching a user table in a database where
        // ALLOW_SNAPSHOT_ISOLATION is OFF raises Msg 3952. Probe-confirmed
        // the rejection point is the first user-table access, not the SET
        // statement and not BeginTransaction. The bypass paths above
        // (table-variable / local-temp / system table) are the same ones
        // real SQL Server doesn't apply Msg 3952 to — system catalogs work
        // fine inside an SI session regardless of the database flag. The flag
        // that governs is the *table's* database, not the session's: an SI
        // session in a snapshot-enabled database reading a three-part name in
        // one that isn't raises 3952 naming the target (probe-confirmed).
        if (isolation == System.Data.IsolationLevel.Snapshot && this.DatabaseFor(table) is { AllowSnapshotIsolation: false } snapshotDisabled)
            throw SimulatedSqlException.SnapshotIsolationNotAllowed(snapshotDisabled.Name);

        // Read uncommitted / NOLOCK: skip everything. Dirty-read semantics.
        if (!isWrite && (hints.NoLock || isolation == System.Data.IsolationLevel.ReadUncommitted))
            return DataLockPlan.NoLock;

        // A snapshot older than the table's definition can't reach it, since
        // metadata isn't versioned (see VersionStore.NoteDefinitionChange); a
        // NOLOCK read reads no snapshot and goes ahead (probed 2026-10-01).
        if (isolation == System.Data.IsolationLevel.Snapshot
            && connection.CurrentTransaction is { SnapshotXid: { } snapshotXid }
            && Volatile.Read(ref table.DefinitionXid) > snapshotXid)
        {
            throw SimulatedSqlException.SnapshotTableDefinitionChanged(this.DatabaseFor(table).Name);
        }

        // TABLOCKX: table-X tx-scoped; no per-row work.
        if (hints.TabLockX)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.Exclusive, hints.NoWait);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }

        if (hints.TabLock)
        {
            if (isWrite)
            {
                this.AcquireTransactionLock(table.TableDataLock, LockMode.Exclusive, hints.NoWait);
                return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
            }
            // Reader TABLOCK: table-S. Tx-scoped iff HOLDLOCK/SER/REPEATABLE or session RR/SER.
            var tabLockTxScoped = hints.Serializable
                || hints.Repeatable
                || isolation is System.Data.IsolationLevel.RepeatableRead or System.Data.IsolationLevel.Serializable;
            if (tabLockTxScoped)
                this.AcquireTransactionLock(table.TableDataLock, LockMode.Shared, hints.NoWait);
            else
                this.AcquireStatementLock(table.TableDataLock, LockMode.Shared, hints.NoWait);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }

        if (isWrite)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive, hints.NoWait);
            return new DataLockPlan(rowMode: LockMode.Exclusive, rowTxScoped: true, skipBlockedRows: false, noLockReader: false);
        }

        // Reader path (no TABLOCK*).
        var serializable = hints.Serializable || isolation == System.Data.IsolationLevel.Serializable;
        if (hints.XLock || hints.UpdLock)
        {
            // Table-IX either way — probed, real reports IX at the object for
            // an UPDLOCK / XLOCK read whatever the isolation level.
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive, hints.NoWait);
            var hintedRowMode = hints.XLock ? LockMode.Exclusive : LockMode.Update;
            if (!serializable)
                return new DataLockPlan(rowMode: hintedRowMode, rowTxScoped: true, skipBlockedRows: hints.ReadPast, noLockReader: false);
            // SERIALIZABLE on top of the hint: real fences the same interval a
            // plain SERIALIZABLE read would, in the mode the hint names —
            // RangeS-U for UPDLOCK, RangeX-X for XLOCK (probe-confirmed, and
            // neither takes a plain key lock beside it). The per-row hold stays
            // on top: range modes live on resources of their own here, so
            // dropping the row-U / row-X would stop blocking the readers and
            // writers that take one without ever probing a range.
            this.HasSessionScopedReference = true;
            return new DataLockPlan(
                rowMode: hintedRowMode, rowTxScoped: true, skipBlockedRows: hints.ReadPast, noLockReader: false,
                serializableRangeMode: hints.XLock ? LockMode.RangeExclusiveExclusive : LockMode.RangeSharedUpdate,
                fence: new PhantomFenceState());
        }
        if (serializable)
        {
            // SERIALIZABLE / HOLDLOCK hint. Only table-IS is settled here —
            // the predicate that decides between key-range locks and the
            // whole-table fallback isn't known at FROM-source resolution, so
            // the plan carries the obligation forward (see
            // DataLockPlan.SerializableRangeMode). The IS is tx-scoped, not
            // statement-scoped, so a concurrent TABLOCKX still conflicts for
            // as long as the ranges are held — that writer takes no per-row
            // lock, so the range probe would never see it.
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentShared, hints.NoWait);
            // Not plan-cacheable: the plan's PhantomFenceState records
            // whether the fence is settled, and a replay sharing it would find
            // the first execution's fence already taken and take none.
            this.HasSessionScopedReference = true;
            return new DataLockPlan(
                rowMode: null, rowTxScoped: false, skipBlockedRows: hints.ReadPast, noLockReader: false,
                serializableRangeMode: LockMode.RangeSharedShared,
                fence: new PhantomFenceState());
        }
        // RC / RR reader. A REPEATABLE READ keeps its IS as long as the row S
        // locks under it, as real reports — it is what a TABLOCKX writer, which
        // takes no row lock, meets.
        var rowTxScoped = hints.Repeatable || isolation == System.Data.IsolationLevel.RepeatableRead;
        if (rowTxScoped)
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentShared, hints.NoWait);
        else
            this.AcquireStatementLock(table.TableDataLock, LockMode.IntentShared, hints.NoWait);
        // RR: acquire row-S tx-scoped per row.
        // RC default: probe-only (no acquire). Encoded as rowMode = null + noLockReader = false;
        // the row-touch helper distinguishes "null + noLockReader=false" (probe) from
        // "null + noLockReader=true" (skip even probe — that's the NoLock path).
        var rowMode = rowTxScoped ? (LockMode?)LockMode.Shared : null;
        return new DataLockPlan(rowMode: rowMode, rowTxScoped: rowTxScoped, skipBlockedRows: hints.ReadPast, noLockReader: false);
    }

    /// <summary>
    /// Acquires <paramref name="mode"/> on the row at
    /// <c>(pageIndex, slotIndex)</c> in <paramref name="table"/>, recording
    /// it against the active transaction (the statement, outside one), and
    /// counts it toward the statement's lock escalation on the table. A row
    /// the transaction or statement has already escalated past takes no lock
    /// of its own. Before the row lock, a held key lock on the row's keys is
    /// tested as <paramref name="purpose"/> says it must be.
    /// </summary>
    public void AcquireRowLockTxScoped(HeapTable table, int pageIndex, int slotIndex, LockMode mode, RowLockPurpose purpose = RowLockPurpose.Read)
    {
        // Every UPDATE / DELETE path passes through here with a RID in hand,
        // so it is where their key-lock tests hang: the lock comes before the
        // write, so the slot holds the row it is about to supersede. An
        // UPDATE's new image is tested separately at the rewrite site — a row
        // moving into a fenced gap is a phantom the old image can't reveal —
        // and an INSERT tests its image before the heap write and takes its
        // X through AcquireInsertedRowLock.
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0
            && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } liveImage)
        {
            if (purpose == RowLockPurpose.Read)
                _ = this.TestRowKeyLock(table, liveImage, mode, skipIfBlocked: false);
            else
                this.TestKeyLocksForWrite(table, liveImage, purpose);
        }
        this.AcquireRowLock(table, pageIndex, slotIndex, mode);
    }

    /// <summary>
    /// The X on a row an insert has just placed, taken under the heap's latch
    /// before the row is visible (see <c>Simulation.InsertRow</c>), so it
    /// skips the key-range tests, which can wait and which the insert ran
    /// before taking the latch. No session can hold a lock on an address
    /// that didn't exist, so the acquisition never waits.
    /// </summary>
    public void AcquireInsertedRowLock(HeapTable table, int pageIndex, int slotIndex)
    {
        this.AcquireRowLock(table, pageIndex, slotIndex, LockMode.Exclusive, underLatch: true);
        if (table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource))
            resource.InsertedBy = this.Connection.Session;
    }

    private void AcquireRowLock(HeapTable table, int pageIndex, int slotIndex, LockMode mode, bool underLatch = false, bool countForEscalation = true)
    {
        if (this.EscalatedModeOf(table) is { } escalated && (escalated == LockMode.Exclusive || mode == LockMode.Shared))
            return;
        var connection = this.Connection;
        var resource = table.GetOrCreateRowLock(pageIndex, slotIndex);
        connection.Simulation.LockManager.Acquire(resource, mode, connection.Session, this.LockTimeoutFor(table), sweepAbandoned: !underLatch);
        if (connection.CurrentTransaction is { } activeTx)
            activeTx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
        if (countForEscalation)
            this.CountLocksForEscalation(table, 1, exclusive: mode != LockMode.Shared, rowLock: true);
    }

    /// <summary>
    /// Readies a row a writer's target read is about to judge — an UPDATE's,
    /// a DELETE's or a MERGE's. Real reads its target under U, so a row
    /// another session is writing is waited out in U and judged as that write
    /// left it (probed 2026-10-01 against SQL Server 2025: a MERGE, seeking or
    /// scanning, waits <c>LCK_M_U</c> on the writer's key, and an UPDATE whose
    /// row was being rewritten by a transaction that then rolled back writes
    /// from the restored row). Here the wait happens only when the row holds
    /// a lock U conflicts with; otherwise nothing is taken yet, and the lock
    /// a qualifying row needs comes from <see cref="HoldQualifyingTargetRow"/>.
    /// <paramref name="rowBytes"/> becomes the row as it stands after a wait.
    /// </summary>
    public TargetRowHold AwaitTargetRow(HeapTable table, int pageIndex, int slotIndex, ref byte[] rowBytes)
    {
        if ((Volatile.Read(ref table.ActiveDataWriters) == 0 && Volatile.Read(ref table.ActiveUpdateLocks) == 0)
            || !table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource)
            || !Simulation.IsLockableTable(table)
            || !this.Connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(resource, LockMode.Update, this.Connection.Session))
        {
            return TargetRowHold.None;
        }
        this.AcquireRowLock(table, pageIndex, slotIndex, LockMode.Update, countForEscalation: false);
        if (table.Heap.ReadLiveRow(pageIndex, slotIndex) is not { } current)
        {
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Update);
            return TargetRowHold.Gone;
        }
        rowBytes = current;
        return TargetRowHold.Update;
    }

    /// <summary>
    /// <see cref="AwaitTargetRow"/> for a statement that takes its rows' X
    /// only once its walk is done, in walk order
    /// (<see cref="HoldQualifyingTargetRow"/>): the U a wait took goes as soon
    /// as the row is read, so the walk holds nothing. Holding it, the walk
    /// could keep a later row while its X waits on an earlier one another
    /// session holds, that session's X waiting on the later — a deadlock real
    /// never meets, since its walk takes U on every row in order (probed
    /// 2026-10-01 against SQL Server 2025: eight sessions each updating the
    /// same two rows forty times, single-table or joined, meet none). False
    /// when the row was deleted while the walk waited.
    /// </summary>
    public bool AwaitTargetRowWriters(HeapTable table, int pageIndex, int slotIndex, ref byte[] rowBytes)
    {
        var hold = this.AwaitTargetRow(table, pageIndex, slotIndex, ref rowBytes);
        if (hold == TargetRowHold.Update)
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Update, countedForEscalation: false);
        return hold != TargetRowHold.Gone;
    }

    /// <summary>
    /// Takes and keeps U on a row a <c>MERGE</c> into a join view reads its
    /// written table through, as real's target read does (probed 2026-10-01
    /// against SQL Server 2025: it waits <c>LCK_M_U</c> on the writer's key),
    /// so the row stays as read until the statement's write converts the U to
    /// X; the caller gives it back with <see cref="ReleaseTargetRow"/> once
    /// the statement has written. Answers the row as it stands once taken —
    /// null when another session's write deleted it.
    /// </summary>
    public byte[]? HoldTargetRowForUpdate(HeapTable table, int pageIndex, int slotIndex)
    {
        if (Simulation.IsLockableTable(table))
            this.AcquireRowLock(table, pageIndex, slotIndex, LockMode.Update, countForEscalation: false);
        return table.Heap.ReadLiveRow(pageIndex, slotIndex);
    }

    /// <summary>
    /// The key lock an INSERT's check of a nonclustered PRIMARY KEY / UNIQUE
    /// constraint or unique index with <c>IGNORE_DUP_KEY</c> takes: real reads
    /// that index for the key under a SERIALIZABLE U — U on the key when a row
    /// carries it, <c>RangeS-U</c> on the next key past it when none does —
    /// and keeps it to the transaction's end, the insert then splitting the
    /// range so its own key takes <c>RangeX-X</c> (probed 2026-10-01 against
    /// SQL Server 2025; a clustered key with the option takes none). So a
    /// second writer of the key waits in U, and one writing another key into
    /// the same gap waits in <c>RangeS-U</c>, until the first settles.
    /// </summary>
    public void LockIgnoreDupKeyProbe(HeapTable table, object keyOwner, SqlValueKey probe)
    {
        if (this.IsSkipping
            || !Simulation.IsLockableTable(table)
            || this.EscalatedModeOf(table) == LockMode.Exclusive
            || KeyLockGroup.For(table, keyOwner) is not { } group)
        {
            return;
        }
        var heap = table.Heap;
        var cache = HeapSeekCache.For(heap);
        var (resource, mode) = cache.AnyRowMatches(heap, table.StoredColumns, group.Ordinals, group.Commons, probe)
            ? (group.GetOrCreate(probe), LockMode.Update)
            : (group.GetOrCreate(cache.NextKeyAbove(heap, table.StoredColumns, heap, group.Ordinals, group.Commons, probe)), LockMode.RangeSharedUpdate);
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (manager.IsHeldBy(resource, mode, connection.Session))
            return;
        manager.Acquire(resource, mode, connection.Session, this.LockTimeoutFor(table));
        if (connection.CurrentTransaction is { } tx)
            tx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
    }

    /// <summary>
    /// The rows of <paramref name="table"/> other sessions have rewritten or
    /// deleted and still hold X on, each with the image it carried before
    /// that write and the lock (<see cref="HeapTable.SupersededKeyImages"/>);
    /// null when there are none, which costs one lock-free read.
    /// </summary>
    public List<((int Page, int Slot) Address, byte[] PriorImage, LockResource Lock)>? SupersededTargetRows(HeapTable table)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree() || !Simulation.IsLockableTable(table))
            return null;
        var session = this.Connection.Session;
        List<((int Page, int Slot) Address, byte[] PriorImage, LockResource Lock)>? rows = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, session))
                continue;
            foreach (var (address, (image, resource)) in images)
                (rows ??= []).Add((address, image, resource));
        }
        return rows;
    }

    /// <summary>
    /// Waits in U for the session holding <paramref name="resource"/>, the X
    /// on a row it rewrote or deleted, as real's target read meets that row;
    /// true when there was someone to wait for.
    /// </summary>
    public bool AwaitSupersededTargetRow(HeapTable table, LockResource resource) =>
        this.AwaitRowWriters(table, resource, LockMode.Update);

    /// <summary>Gives back what <paramref name="hold"/> took on a row the statement then turned away.</summary>
    public void ReleaseTargetRow(HeapTable table, int pageIndex, int slotIndex, TargetRowHold hold)
    {
        if ((hold & TargetRowHold.Exclusive) != 0)
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Exclusive);
        if ((hold & TargetRowHold.Update) != 0)
            this.ReleaseRowLockAcquisition(table, pageIndex, slotIndex, LockMode.Update, countedForEscalation: false);
    }

    /// <summary>
    /// Takes the X a target row the statement judged qualifying is written
    /// under, as real converts its U to X writing the row (probed 2026-10-01
    /// against SQL Server 2025: an UPDATE scanning past another session's row
    /// already holds X on the rows before it), so no other writer changes the
    /// row between the judgement and the write — two sessions updating one
    /// row from its own value each see the other's write — and the write
    /// itself takes nothing more. <paramref name="purpose"/> is the write's,
    /// for the key-range tests. False when the row changed since
    /// <paramref name="rowBytes"/> was read, the X having waited out the
    /// session that changed it: <paramref name="rowBytes"/> is then the row as
    /// it stands, to judge again, and <paramref name="hold"/> is
    /// <see cref="TargetRowHold.Gone"/> when that write deleted it. A heap
    /// whose <see cref="Heap.MutationGeneration"/> still reads
    /// <paramref name="walkGeneration"/>, noted before the walk read any row,
    /// has had no row written since — a rollback only restores an image some
    /// write moved the generation past — so the row isn't read again.
    /// </summary>
    public bool HoldQualifyingTargetRow(
        HeapTable table, int pageIndex, int slotIndex, ref TargetRowHold hold, ref byte[] rowBytes, long walkGeneration, RowLockPurpose purpose = RowLockPurpose.UpdatePreImage)
    {
        if ((hold & TargetRowHold.Exclusive) != 0 || !Simulation.IsLockableTable(table))
            return true;
        this.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive, purpose);
        hold |= TargetRowHold.Exclusive;
        if (Volatile.Read(ref table.Heap.MutationGeneration) == walkGeneration)
            return true;
        var current = table.Heap.ReadLiveRow(pageIndex, slotIndex);
        if (current is not null && current.AsSpan().SequenceEqual(rowBytes))
            return true;
        if (current is null)
        {
            this.ReleaseTargetRow(table, pageIndex, slotIndex, hold);
            hold = TargetRowHold.Gone;
            return false;
        }
        rowBytes = current;
        return false;
    }

    /// <summary>
    /// Gives back the most recent acquisition of <paramref name="mode"/> on the
    /// row at <paramref name="pageIndex"/> / <paramref name="slotIndex"/> — the
    /// lock a tx-scoped read took on a row that turned out not to qualify,
    /// which real releases rather than keeping to the transaction's end. A
    /// row the read took no lock on (the table escalated) gives back nothing.
    /// <paramref name="countedForEscalation"/> false for an acquisition that
    /// didn't count toward escalation, as a target read's U doesn't.
    /// </summary>
    public void ReleaseRowLockAcquisition(HeapTable table, int pageIndex, int slotIndex, LockMode mode, bool countedForEscalation = true)
    {
        if (!table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource))
            return;
        var connection = this.Connection;
        var held = connection.CurrentTransaction?.HeldLocks ?? this.StatementSchemaLocks;
        var index = held.LastIndexOf((resource, mode));
        if (index < 0)
            return;
        held.RemoveAt(index);
        connection.Simulation.LockManager.Release(resource, mode, connection.Session);
        if (countedForEscalation && this.CurrentStatement.LockTallies is { } tallies && tallies.TryGetValue(table, out var tally) && !tally.RowsKeyLocked)
            tally.Count--;
    }

    /// <summary>
    /// Records the image of the row at <paramref name="pageIndex"/> /
    /// <paramref name="slotIndex"/> — which this session has just taken X on
    /// and is about to delete or rewrite — in
    /// <see cref="HeapTable.SupersededKeyImages"/>, so another session's
    /// uniqueness check can wait on the key the write takes away, and its
    /// scan on the row a delete hides, until this session's transaction
    /// settles. An escalated table's X covers every row already.
    /// </summary>
    public void NoteSupersededRow(HeapTable table, int pageIndex, int slotIndex)
    {
        var connection = this.Connection;
        if (this.EscalatedModeOf(table) == LockMode.Exclusive)
            return;
        // Every caller has just taken the row X through AcquireRowLockTxScoped,
        // whose only way out without it is the escalation checked above; the
        // entry retires with that hold's release.
        if (!table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource)
            || table.Heap.ReadSlotBytes(pageIndex, slotIndex) is not { } image)
        {
            return;
        }
        table.SupersededKeyImages.GetOrAdd(connection.Session, static _ => new())[(pageIndex, slotIndex)] = (image, resource);
    }

    /// <summary>
    /// Waits out every other session's uncommitted delete on
    /// <paramref name="table"/> before a locking scan reads it. The scan's
    /// heap walk never reaches a tombstoned slot, where real's scan meets the
    /// deleted row's X-locked key and waits on it (probed 2026-09-26 against
    /// SQL Server 2025) — a read that skipped it would report the delete
    /// before it committed. Waiting up front rather than at the row's turn
    /// only moves the wait earlier within the same statement.
    /// </summary>
    public void AwaitUncommittedDeletes(HeapTable table)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree())
            return;
        var connection = this.Connection;
        List<LockResource>? holders = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, connection.Session))
                continue;
            foreach (var (address, (_, resource)) in images)
            {
                if (table.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex))
                    (holders ??= []).Add(resource);
            }
        }

        if (holders is not null)
        {
            foreach (var resource in holders)
                _ = this.AwaitRowWriters(table, resource);
        }
    }

    /// <summary>
    /// Waits out every other session's uncommitted delete or rewrite of a row
    /// whose <paramref name="storageOrdinals"/> tuple was
    /// <paramref name="probe"/> (see <see cref="HeapTable.SupersededKeyImages"/>),
    /// the way real's insert of a key waits on that key's lock — in
    /// <paramref name="mode"/>, X for a uniqueness check and S for a foreign
    /// key's. Cheap when no other session has one pending on the table.
    /// <paramref name="reportedKey"/>, when the tuple is a unique key, is
    /// where the lock DMVs report the wait (<see cref="SessionToken.WaitingOnKey"/>).
    /// </summary>
    public void AwaitSupersededKeyHolders(HeapTable table, int[] storageOrdinals, SqlType[] commons, SqlValueKey probe, LockMode mode, bool reportedKey = false)
    {
        if (table.SupersededKeyImages.IsEmptyLockFree())
            return;
        var connection = this.Connection;
        List<LockResource>? holders = null;
        foreach (var (owner, images) in table.SupersededKeyImages)
        {
            if (ReferenceEquals(owner, connection.Session))
                continue;
            foreach (var (_, (image, resource)) in images)
            {
                if (HeapSeekCache.TryComputeKey(image, storageOrdinals, commons, table.StoredColumns, table.Heap, out var key) && key.Equals(probe))
                    (holders ??= []).Add(resource);
            }
        }

        if (holders is not null)
        {
            foreach (var resource in holders)
                _ = this.AwaitRowWriters(table, resource, mode, reportedKey ? probe : null);
        }
    }

    /// <summary>
    /// Waits out another session's uncommitted write to a live row carrying
    /// <paramref name="probe"/> — the duplicate a uniqueness check just
    /// found, which is only a duplicate once that write commits (real's
    /// second insert of a key waits on the first's lock rather than failing
    /// at once, requesting X on it; probed 2026-10-01 against SQL Server
    /// 2025). <paramref name="mode"/> is
    /// <see cref="AwaitSupersededKeyHolders"/>'s, as is <paramref name="reportedKey"/>.
    /// True when it waited, so the caller checks again.
    /// </summary>
    public bool AwaitLiveKeyHolders(HeapTable table, int[] storageOrdinals, SqlType[] commons, SqlValueKey probe, LockMode mode, bool reportedKey = false)
    {
        if (Volatile.Read(ref table.ActiveDataWriters) == 0)
            return false;
        var waited = false;
        foreach (var (page, slot, _) in HeapSeekCache.For(table.Heap).MatchingRows(table.Heap, table.StoredColumns, storageOrdinals, commons, probe))
        {
            if (table.RowLocks.TryGetValue((page, slot), out var resource))
                waited |= this.AwaitRowWriters(table, resource, mode, reportedKey ? probe : null);
        }
        return waited;
    }

    /// <summary>
    /// <see cref="AwaitLiveKeyHolders"/> for one row a uniqueness check's
    /// scan found carrying the key — the scan a NULL key component sends it
    /// to. True when it waited, so the caller scans again.
    /// </summary>
    public bool AwaitKeyHolderAt(HeapTable table, int pageIndex, int slotIndex) =>
        Volatile.Read(ref table.ActiveDataWriters) != 0
        && table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource)
        && this.AwaitRowWriters(table, resource, LockMode.Exclusive);

    /// <summary>
    /// Blocks until no other session holds the row lock
    /// <paramref name="resource"/> incompatibly with S — the transient
    /// acquire-and-release real's "wait for the committed row" amounts to.
    /// </summary>
    public void AwaitRowWritersOf(HeapTable table, LockResource resource) => _ = this.AwaitRowWriters(table, resource);

    // True when there was someone to wait for. A wait for a key some row
    // carries names the key, for the lock DMVs.
    private bool AwaitRowWriters(HeapTable table, LockResource resource, LockMode mode = LockMode.Shared, SqlValueKey? waitingOnKey = null)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, mode, connection.Session))
            return false;
        var session = connection.Session;
        session.WaitingOnKey = waitingOnKey is { } key ? KeyLockGroup.Describe(key) : null;
        try
        {
            manager.Acquire(resource, mode, session, this.LockTimeoutFor(table));
        }
        finally
        {
            session.WaitingOnKey = null;
        }
        manager.Release(resource, mode, session);
        return true;
    }

    /// <summary>
    /// The table lock this transaction — or, outside one, this statement —
    /// escalated <paramref name="table"/>'s row and key locks to, or null.
    /// </summary>
    private LockMode? EscalatedModeOf(HeapTable table)
    {
        if (this.Connection.CurrentTransaction is { } tx)
        {
            if (tx.EscalatedTables.Count != 0 && tx.EscalatedTables.Contains(table))
                return LockMode.Exclusive;
            return tx.SharedEscalatedTables.Count != 0 && tx.SharedEscalatedTables.Contains(table) ? LockMode.Shared : null;
        }
        return this.CurrentStatement.EscalatedTables is { } escalated && escalated.TryGetValue(table, out var mode) ? mode : null;
    }

    /// <summary>
    /// Counts <paramref name="added"/> more row or key locks this statement
    /// took on <paramref name="table"/>, and escalates them to one table lock
    /// once the estimated total reaches the next attempt point — S when every
    /// lock counted is S-family, X otherwise, as real's escalation of a
    /// REPEATABLE READ scan, a SERIALIZABLE scan, an <c>UPDLOCK</c> scan and
    /// an UPDATE shows (probed 2026-09-28 against SQL Server 2025). A table
    /// set <c>LOCK_ESCALATION = DISABLE</c> keeps its locks however many.
    /// </summary>
    private void CountLocksForEscalation(HeapTable table, int added, bool exclusive, bool rowLock = false, bool rowsKeyLocked = false)
    {
        if (table.LockEscalation == 1)
            return;
        var tallies = this.CurrentStatement.LockTallies ??= new(ReferenceEqualityComparer.Instance);
        if (!tallies.TryGetValue(table, out var tally))
            tallies[table] = tally = new LockEscalationTally();
        tally.Exclusive |= exclusive;
        tally.RowsKeyLocked |= rowsKeyLocked;
        if (added == 0 || (rowLock && tally.RowsKeyLocked))
            return;
        tally.Count += added;
        var estimated = EstimatedLockTotal(table, tally.Count);
        if (estimated >= tally.NextAttempt && !this.TryEscalate(table, tally.Exclusive))
            tally.NextAttempt = estimated + LockEscalationTally.RetryInterval;
    }

    /// <summary>
    /// Real's escalation counts every lock the statement holds on the table:
    /// its row or key locks, the table's own intent lock, and the intent lock
    /// on each page those rows sit on. Pages aren't locked here, so their
    /// share is estimated from the heap's rows per page — which is what puts
    /// a SERIALIZABLE scan of a narrow table over real's threshold at about
    /// 6 235 keys rather than 6 250 (probed 2026-09-28 against SQL Server
    /// 2025).
    /// </summary>
    private static int EstimatedLockTotal(HeapTable table, int locks)
    {
        var heap = table.Heap;
        var rows = heap.RowCount;
        var pages = heap.Pages.Count;
        var pageLocks = rows <= 0 ? 0 : Math.Min(pages, (((long)locks * pages) + rows - 1) / rows);
        return (int)Math.Min(int.MaxValue, locks + 1 + pageLocks);
    }

    /// <summary>
    /// Replaces the row and key locks this transaction (or statement) holds on
    /// <paramref name="table"/> with one table S or X. Real escalates only when
    /// the table lock is grantable at once, and otherwise keeps the fine-grained
    /// locks and tries again later, so this never waits: false means refused.
    /// The table's intent lock folds into the escalated mode, as real reports
    /// a single OBJECT S / X afterwards.
    /// </summary>
    private bool TryEscalate(HeapTable table, bool exclusive)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        var mode = exclusive ? LockMode.Exclusive : LockMode.Shared;
        if (manager.TryAcquire(table.TableDataLock, mode, connection.Session, 0) is not (LockAcquireOutcome.Granted or LockAcquireOutcome.GrantedAfterWait))
            return false;

        var tx = connection.CurrentTransaction;
        var held = tx?.HeldLocks ?? this.StatementSchemaLocks;
        held.Add((table.TableDataLock, mode));
        ReleaseEscalated(held, table, mode, manager, connection.Session);
        if (tx is not null)
            ReleaseEscalated(this.StatementSchemaLocks, table, mode, manager, connection.Session);

        if (tx is null)
            (this.CurrentStatement.EscalatedTables ??= new(ReferenceEqualityComparer.Instance))[table] = mode;
        else if (exclusive)
            _ = tx.EscalatedTables.Add(table);
        else
            _ = tx.SharedEscalatedTables.Add(table);
        return true;
    }

    // Drops the holds a table lock in `mode` covers from `held`: every row and
    // key lock on the table an X covers, only the S-family ones an S does, and
    // the table's intent lock the escalated mode subsumes.
    private static void ReleaseEscalated(List<(LockResource Resource, LockMode Mode)> held, HeapTable table, LockMode mode, LockManager manager, SessionToken session)
    {
        var exclusive = mode == LockMode.Exclusive;
        for (var i = held.Count - 1; i >= 0; i--)
        {
            var (resource, heldMode) = held[i];
            var covered = ReferenceEquals(resource, table.TableDataLock)
                ? heldMode == LockMode.IntentShared || (exclusive && heldMode == LockMode.IntentExclusive)
                : ReferenceEquals(resource.OwningTable, table)
                    && (exclusive || heldMode is LockMode.Shared or LockMode.RangeSharedShared);
            if (!covered)
                continue;
            manager.Release(resource, heldMode, session);
            held.RemoveAt(i);
        }
    }

    /// <summary>
    /// Tables this batch has already fenced whole for SERIALIZABLE phantom
    /// protection. Purely an idempotency guard: the fallback is decided per
    /// materialization, and a source can be re-enumerated many times (a
    /// correlated subquery's inner side), so without this the whole key space
    /// would be walked and re-covered per pass.
    /// </summary>
    private readonly HashSet<HeapTable> serializableTableFallbacks = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Discharges a SERIALIZABLE / <c>HOLDLOCK</c> reader's outstanding
    /// phantom protection for a read whose shape offers no narrower interval
    /// (a whole-table scan, a non-sargable predicate, a predicate on an
    /// unindexed or non-leading column, a cross-column <c>OR</c>): the whole
    /// key space. Over a clustered table that is every key of the clustered
    /// index plus the infinity anchor in the plan's range mode, as real's scan
    /// takes; over a heap it is a table S, real's OBJECT S for a heap scan
    /// (probed 2026-09-28 against SQL Server 2025).
    /// <para>
    /// A no-op for every other plan, for a source whose fence is already
    /// settled (the keys the seek path locked cover the same obligation more
    /// narrowly), and for a table this batch already fenced whole.
    /// </para>
    /// </summary>
    public void EnsureSerializableTableLock(HeapTable table, in DataLockPlan plan)
    {
        if (plan.SerializableRangeMode is not { } mode || plan.Fence is not { Settled: false } fence)
            return;
        fence.Settled = true;
        if (!this.serializableTableFallbacks.Add(table))
            return;
        if (KeyLockGroup.RowGroupOf(table) is { } group)
            this.AcquireKeyFence(table, group, group.Commons, [KeyFenceInterval.Everything], mode, KeyFenceKind.Read, lookupRows: false);
        else
            this.AcquireSerializableTableS(table);
    }

    /// <summary>
    /// A heap's SERIALIZABLE fence: table S, folding in the IS the read took
    /// when its source resolved, which real reports converted to the one
    /// OBJECT S.
    /// </summary>
    private void AcquireSerializableTableS(HeapTable table)
    {
        this.AcquireTransactionLock(table.TableDataLock, LockMode.Shared, this.noWaitTables.Contains(table));
        var connection = this.Connection;
        var held = connection.CurrentTransaction?.HeldLocks ?? this.StatementSchemaLocks;
        var index = held.IndexOf((table.TableDataLock, LockMode.IntentShared));
        if (index >= 0)
        {
            connection.Simulation.LockManager.Release(table.TableDataLock, LockMode.IntentShared, connection.Session);
            held.RemoveAt(index);
        }
    }

    /// <summary>
    /// Takes the key locks a SERIALIZABLE access of <paramref name="group"/>'s
    /// index over <paramref name="intervals"/> takes, in <paramref name="mode"/>:
    /// every key inside each interval and the first key past it, the infinity
    /// anchor when none follows — real's next-key locking, which is what fences
    /// the gap below each key and past the read's end (probed 2026-09-28
    /// against SQL Server 2025). An interval naming one full key of a unique
    /// index is the exception real makes when it finds that key: a reader
    /// takes a plain key lock there (a row S for the clustered key), a writer
    /// nothing past its own row X; a miss locks the next key like any range.
    /// <para>
    /// Bounds are compared in <paramref name="commons"/>, the types the
    /// predicate promoted the key columns to. A clustered key's anchors are
    /// also tested against another session's lock on the anchored row, which
    /// real meets as the key lock itself; <paramref name="lookupRows"/> takes
    /// the row S real's lookup from a nonclustered index takes on each row it
    /// reads. Past the escalation threshold the whole read escalates instead.
    /// </para>
    /// </summary>
    public void AcquireKeyFence(
        HeapTable table, KeyLockGroup group, SqlType[] commons, List<KeyFenceInterval> intervals, LockMode mode, KeyFenceKind kind, bool lookupRows)
    {
        var keyPart = LockManager.KeyPartOf(mode);
        if (this.EscalatedModeOf(table) is { } escalated && (escalated == LockMode.Exclusive || keyPart == LockMode.Shared))
            return;

        var heap = table.Heap;
        var cache = HeapSeekCache.For(heap);
        var requests = new List<(SqlValueKey? Key, LockMode Mode, (int Page, int Slot)[] Rids, bool Lookup)>();
        var cap = LockEscalationTally.FirstAttempt;
        var capped = false;
        foreach (var interval in intervals)
        {
            var anchors = cache.KeyLockAnchors(heap, table.StoredColumns, heap, group.Ordinals, commons,
                interval.Lower, interval.LowerInclusive, interval.Upper, interval.UpperInclusive, capped ? int.MaxValue : cap);
            if (anchors is null)
            {
                // Too many keys to lock one by one: escalate if that can be
                // granted, else lock them all after all.
                if (table.LockEscalation != 1 && this.TryEscalate(table, keyPart != LockMode.Shared))
                    return;
                capped = true;
                anchors = cache.KeyLockAnchors(heap, table.StoredColumns, heap, group.Ordinals, commons,
                    interval.Lower, interval.LowerInclusive, interval.Upper, interval.UpperInclusive, int.MaxValue)!;
            }

            if (interval.UniquePoint && anchors.Count == 2)
            {
                // The one key a unique equality can find.
                var (_, rids) = anchors[0];
                if (kind == KeyFenceKind.Read && mode == LockMode.RangeSharedShared && group.IsRowGroup)
                {
                    foreach (var (page, slot) in rids)
                        this.AcquireRowLockTxScoped(table, page, slot, LockMode.Shared);
                }
                else if (kind == KeyFenceKind.Read && !group.IsRowGroup)
                {
                    requests.Add((anchors[0].Key, keyPart, rids, lookupRows));
                }
                continue;
            }

            for (var i = 0; i < anchors.Count; i++)
                requests.Add((anchors[i].Key, mode, anchors[i].Rids, lookupRows && !group.IsRowGroup && i < anchors.Count - 1));
        }

        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        var session = connection.Session;
        var noWait = this.noWaitTables.Count != 0 && this.noWaitTables.Contains(table);
        var acquired = 0;
        foreach (var (key, requestMode, rids, lookup) in requests)
        {
            var resource = group.GetOrCreate(key is { } k ? Normalize(group, k) : null);
            if (!manager.IsHeldBy(resource, requestMode, session))
            {
                this.AcquireTransactionLock(resource, requestMode, noWait);
                acquired++;
            }

            if (group.IsRowGroup)
            {
                var rowMode = LockManager.KeyPartOf(requestMode);
                foreach (var rid in rids)
                {
                    if (table.RowLocks.TryGetValue(rid, out var rowLock) && manager.HasIncompatibleHolderOtherThan(rowLock, rowMode, session))
                    {
                        manager.Acquire(rowLock, rowMode, session, this.LockTimeoutFor(table));
                        manager.Release(rowLock, rowMode, session);
                    }
                }
            }
            else if (lookup)
            {
                foreach (var (page, slot) in rids)
                    this.AcquireRowLockTxScoped(table, page, slot, LockMode.Shared);
            }
        }

        this.CountLocksForEscalation(table, acquired, exclusive: keyPart != LockMode.Shared, rowsKeyLocked: group.IsRowGroup);
    }

    // An anchor found through a seek-cache entry keyed in the predicate's
    // promoted types, restated in the column types every writer's test reads
    // its row in — a widening, so narrowing back is exact.
    private static SqlValueKey Normalize(KeyLockGroup group, SqlValueKey key)
    {
        var restated = false;
        for (var i = 0; i < key.ComponentCount && !restated; i++)
            restated = !key.ComponentAt(i).Type.Equals(group.Commons[i]);
        if (!restated)
            return key;
        var components = new SqlValue[key.ComponentCount];
        for (var i = 0; i < components.Length; i++)
            components[i] = key.ComponentAt(i).CoerceTo(group.Commons[i]);
        return new SqlValueKey(components);
    }

    /// <summary>
    /// Tests the key lock another session may hold on the clustered key of
    /// the row <paramref name="image"/> is, against a row lock in
    /// <paramref name="mode"/> — a reader's S meeting a writer's
    /// <c>RangeX-X</c>, an <c>UPDLOCK</c> reader's U meeting another's
    /// <c>RangeS-U</c> — waiting it out, or reporting false for a
    /// <c>READPAST</c> reader to skip the row.
    /// </summary>
    private bool TestRowKeyLock(HeapTable table, byte[] image, LockMode mode, bool skipIfBlocked, bool unlockedWhenClean = false)
    {
        if (KeyLockGroup.ClusteredOwner(table) is not { } owner
            || !table.KeyLockGroups.TryGetValue(owner, out var group)
            || Volatile.Read(ref group.Holds) == 0
            || !group.TryReadKey(image, out var key)
            || group.Find(key) is not { } resource)
        {
            return true;
        }
        if (unlockedWhenClean
            && !this.Connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(
                resource, mode, this.Connection.Session, holder => ChangedWhileOpen(table, holder)))
        {
            return true;
        }
        return this.TestKeyLock(table, resource, mode, skipIfBlocked);
    }

    // Whether the table changed since the holder's transaction began — or the
    // holder is a statement running outside one — which is when real's READ
    // COMMITTED read takes the S that meets its lock.
    private static bool ChangedWhileOpen(HeapTable table, SessionToken holder) =>
        holder.TryResolveOwner()?.CurrentTransaction is not { } transaction
        || Volatile.Read(ref table.Heap.LastModifiedEpoch) >= transaction.BeginEpoch;

    // Waits until no other session holds `resource` incompatibly with `mode`
    // — an instant-duration acquire, never held — or, for a READPAST reader,
    // reports that it would have to.
    private bool TestKeyLock(HeapTable table, LockResource resource, LockMode mode, bool skipIfBlocked)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, mode, connection.Session))
            return true;
        if (skipIfBlocked)
            return false;
        manager.Acquire(resource, mode, connection.Session, this.LockTimeoutFor(table));
        manager.Release(resource, mode, connection.Session);
        return true;
    }

    // A write's tests against every index somebody holds a key lock in: an
    // insert tests the gap its key lands in, a delete the lock on its key,
    // and an update's pre-image only the clustered key's (the rewrite site
    // tests a nonclustered index once it knows the update touches it).
    private void TestKeyLocksForWrite(HeapTable table, byte[] image, RowLockPurpose purpose)
    {
        foreach (var (_, group) in table.KeyLockGroups)
        {
            if (Volatile.Read(ref group.Holds) == 0
                || (purpose == RowLockPurpose.UpdatePreImage && !group.IsRowGroup)
                || !group.TryReadKey(image, out var key))
            {
                continue;
            }
            if (purpose == RowLockPurpose.Insert)
                this.TestGapLock(table, group, key);
            else if (group.Find(key) is { } resource)
                _ = this.TestKeyLock(table, resource, LockMode.Exclusive, skipIfBlocked: false);
        }
    }

    // Real's insert-range test: RangeI-N, instant, on the first key above the
    // one being written — the anchor whose range the new key lands in — or on
    // the infinity anchor past the last key. A key written into a gap this
    // session itself range-locks splits that gap, so the new key takes
    // RangeX-X to keep the lower half fenced, as real's does (probed
    // 2026-09-28 against SQL Server 2025: a HOLDLOCK MERGE's insert).
    private void TestGapLock(HeapTable table, KeyLockGroup group, SqlValueKey key)
    {
        var next = HeapSeekCache.For(table.Heap).NextKeyAbove(table.Heap, table.StoredColumns, table.Heap, group.Ordinals, group.Commons, key);
        if (group.Find(next) is not { } resource)
            return;
        _ = this.TestKeyLock(table, resource, LockMode.RangeInsertNull, skipIfBlocked: false);
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        if (!manager.HoldsRangeMode(resource, connection.Session))
            return;
        var split = group.GetOrCreate(key);
        if (!manager.IsHeldBy(split, LockMode.RangeExclusiveExclusive, connection.Session))
            this.AcquireTransactionLock(split, LockMode.RangeExclusiveExclusive);
    }

    /// <summary>
    /// Blocks the caller until no other session's key lock fences the gap a
    /// new row <paramref name="image"/> lands in, on any index. Runs on every
    /// writer whatever its own isolation level — a range lock's whole purpose
    /// is to fence writers that know nothing about it — and mirrors real's
    /// RangeI-N: an instant-duration mode on the next key, taken only to test
    /// the gap and released the moment it is granted, so it never shows up in
    /// a lock snapshot taken after the write.
    /// <para>
    /// Costs nothing when no key lock is held anywhere on the table (the
    /// <see cref="HeapTable.ActiveKeyRangeLocks"/> read).
    /// </para>
    /// </summary>
    /// <exception cref="SimulatedSqlException">
    /// Msg 1222 on lock timeout, Msg 1205 when waiting would close a cycle.
    /// </exception>
    public void ProbeKeyLocksForInsert(HeapTable table, ReadOnlySpan<byte> image)
    {
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0)
            this.TestKeyLocksForWrite(table, image.ToArray(), RowLockPurpose.Insert);
    }

    /// <summary>
    /// The rewrite site's half of an UPDATE's key-lock tests, called with the
    /// row at <paramref name="pageIndex"/> / <paramref name="slotIndex"/> still
    /// holding its old image, once the new one is known: a nonclustered index whose row the update touches — its key
    /// moves, or a column it carries changes — has its old key's lock tested,
    /// and any index whose key moves has the gap the new key lands in tested,
    /// since a row moving into a fenced gap is a phantom its old image can't
    /// reveal. An update touching no column of an index takes nothing there,
    /// as on real (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public void ProbeKeyLocksForUpdate(HeapTable table, int pageIndex, int slotIndex, byte[] newImage)
    {
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) == 0 || table.Heap.ReadSlotBytes(pageIndex, slotIndex) is not { } oldImage)
            return;
        foreach (var (_, group) in table.KeyLockGroups)
        {
            if (Volatile.Read(ref group.Holds) == 0)
                continue;
            var hadOld = group.TryReadKey(oldImage, out var oldKey);
            var hasNew = group.TryReadKey(newImage, out var newKey);
            var moved = hadOld != hasNew || (hadOld && !oldKey.Equals(newKey));
            if (!group.IsRowGroup && hadOld && (moved || group.RowChanges(oldImage, newImage)) && group.Find(oldKey) is { } held)
                _ = this.TestKeyLock(table, held, LockMode.Exclusive, skipIfBlocked: false);
            if (moved && hasNew)
                this.TestGapLock(table, group, newKey);
        }
    }

    /// <summary>
    /// Wraps <paramref name="table"/>'s row enumeration with per-row
    /// conflict checks driven by <paramref name="plan"/>. Each yielded
    /// row's RID flows through <see cref="TouchRowForRead"/>; READPAST-
    /// blocked rows are silently skipped. <paramref name="batch"/> is the
    /// executing one: a SELECT's FROM source holds a
    /// <see cref="LockCheckedScanRows"/> that calls here per execution, since
    /// a cached plan is replayed by other sessions.
    /// </summary>
    public static IEnumerable<byte[]> WrapWithRowConflictChecks(HeapTable table, BatchContext batch, DataLockPlan plan)
    {
        // Reaching here means nothing narrowed the source to an index seek, so
        // a SERIALIZABLE reader is about to scan the whole table and its
        // phantom fence has to be the whole key space. Deliberately inside the
        // iterator body: the seek decision is made after the FROM source is
        // built, and a seeked source is a different enumerable that never runs
        // this one.
        batch.EnsureSerializableTableLock(table, plan);
        var io = batch.Connection.StatementIo?.Touch(table);
        _ = io?.ScanCount += 1;
        var lastPage = -1;
        var snapshotXid = batch.ResolveSnapshotXidForRead(table);
        if (snapshotXid is null && !plan.NoLockReader && !plan.SkipBlockedRows)
            batch.AwaitUncommittedDeletes(table);
        // Null unless the statement reads a row locator (see RowLocator).
        var addresses = batch.CurrentStatement.RowAddresses;
        // A clustered table scans in its key's order (see ClusteredScan); a
        // snapshot read sweeps the heap and its version chains as before.
        if (snapshotXid is null && ClusteredScan.Order(table) is { } clusteredOrder)
        {
            var seen = new HashSet<(int, int)>();
            foreach (var (pageIndex, slotIndex) in clusteredOrder)
            {
                if (!seen.Add((pageIndex, slotIndex)) || table.Heap.IsSlotTombstoned(pageIndex, slotIndex))
                    continue;
                if (batch.TouchRowForRead(table, pageIndex, slotIndex, plan) && table.Heap.ReadLiveRow(pageIndex, slotIndex) is { } bytes)
                {
                    io?.Enter(pageIndex, ref lastPage);
                    addresses?.Record(bytes, pageIndex, slotIndex);
                    yield return bytes;
                }
            }
            yield break;
        }
        var heap = table.Heap;
        if (snapshotXid is { } sx)
        {
            // A deleted slot comes through too: the snapshot may predate the
            // delete. Each slot resolves once, against the chain as it stood
            // with the slot, so a write landing mid-scan neither hides a row
            // nor shows it twice.
            foreach (var (pageIndex, slotIndex, read, sequence) in heap.EnumerateSlots())
            {
                io?.Enter(pageIndex, ref lastPage);
                var resolved = Storage.VersionStore.ReadSnapshotSlot(table, (pageIndex, slotIndex), read, sequence, sx, batch.Connection.Session);
                if (resolved is null)
                    continue;
                addresses?.Record(resolved, pageIndex, slotIndex);
                yield return resolved;
            }
            // A chain whose address the heap no longer has — its page trimmed
            // or truncated away — may still hold a version the snapshot
            // predates.
            foreach (var (address, chain) in table.RowVersions)
            {
                if (heap.TryReadSlot(address.PageIndex, address.SlotIndex, out _, out _))
                    continue;
                var resolved = Storage.VersionStore.ResolveTombstonedSlotForSnapshot(chain, sx, batch.Connection.Session);
                if (resolved is null)
                    continue;
                addresses?.Record(resolved, address.PageIndex, address.SlotIndex);
                yield return resolved;
            }
        }
        else
        {
            using var rows = heap.EnumerateRowsWithAddress().GetEnumerator();
            while (true)
            {
                // Read just ahead of the row, so a write since — one a wait in
                // the probe below outlasted — shows as a moved sequence.
                var sequence = heap.WriteSequence;
                if (!rows.MoveNext())
                    break;
                var (pageIndex, slotIndex, bytes) = rows.Current;
                io?.Enter(pageIndex, ref lastPage);
                if (!batch.TouchRowForRead(table, pageIndex, slotIndex, plan))
                    continue;
                // A wait on the row's writer can outlast the image read before
                // it: when anything wrote the heap since, read the row as it
                // stands — gone if the write deleted it — as real's read after
                // the wait does.
                if (heap.WriteSequence != sequence)
                {
                    if (heap.ReadLiveRow(pageIndex, slotIndex) is not { } current)
                        continue;
                    bytes = current;
                }
                addresses?.Record(bytes, pageIndex, slotIndex);
                yield return bytes;
            }
        }
    }

    /// <summary>
    /// Returns the snapshot Xid governing this read, or <c>null</c> when
    /// the read should use the standard lock-based path (default RC without
    /// RCSI, RR, SERIALIZABLE, etc). Allocates the per-transaction SI Xid
    /// lazily on first call; allocates the per-statement RCSI Xid lazily
    /// on first user-table read inside the statement.
    /// </summary>
    internal long? ResolveSnapshotXidForRead(HeapTable table)
    {
        if (table.IsTableVariable || IsLocalTempName(table.Name))
            return null;
        if (Simulation.SystemHeapTables.Values.Contains(table))
            return null;

        var connection = this.Connection;
        var isolation = connection.SessionIsolationLevel;
        var simulation = connection.Simulation;

        if (isolation == System.Data.IsolationLevel.Snapshot)
        {
            if (connection.CurrentTransaction is { } tx)
            {
                if (tx.SnapshotXid is null)
                {
                    // Registered before the stamp is read, under one no later
                    // than it: a version sweep running meanwhile either sees
                    // the registration or read its cutoff before this stamp
                    // existed, so it can't drop a version the snapshot reads.
                    var transactionId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tx);
                    simulation.ActiveSnapshotTxs[connection.Session] = new ActiveSnapshotRegistration(transactionId, simulation.CurrentTransactionCommitId, connection.Spid);
                    var snapshotXid = simulation.CurrentTransactionCommitId;
                    tx.SnapshotXid = snapshotXid;
                    simulation.ActiveSnapshotTxs[connection.Session] = new ActiveSnapshotRegistration(transactionId, snapshotXid, connection.Spid);
                }
                return tx.SnapshotXid;
            }
            // Auto-commit SI session — each read gets the latest commit
            // stamp (effectively current state). Rare path, mostly a
            // grammar-level use.
            return simulation.CurrentTransactionCommitId;
        }

        // RCSI is the *table's* database's flag, not the session's — a session
        // in a non-RCSI database reading a three-part name into an RCSI one
        // reads versioned, and the reverse blocks (probe-confirmed).
        if (isolation == System.Data.IsolationLevel.ReadCommitted && this.DatabaseFor(table).ReadCommittedSnapshot)
        {
            this.RcsiStatementSnapshotXid ??= simulation.CurrentTransactionCommitId;
            return this.RcsiStatementSnapshotXid;
        }

        return null;
    }

    /// <summary>
    /// Reader-side row-touch helper called per row during enumeration.
    /// Based on <paramref name="plan"/>:
    /// <list type="bullet">
    /// <item><see cref="DataLockPlan.NoLockReader"/> — no probe, no acquire (dirty read).</item>
    /// <item><see cref="DataLockPlan.RowMode"/> non-null — acquire that mode
    /// tx-scoped, unless <see cref="DataLockPlan.SkipBlockedRows"/> is set and
    /// another connection already holds the row incompatibly, which is the
    /// <c>UPDLOCK, READPAST</c> / <c>XLOCK, READPAST</c> pair real answers by
    /// leaving the row out of the result (probe-confirmed).</item>
    /// <item>Else (RC probe path) — check for a row-X holder by another
    /// connection. If found and <see cref="DataLockPlan.SkipBlockedRows"/>
    /// is true, return false so the caller skips this row (READPAST).
    /// Otherwise wait for the row by transiently acquiring + releasing
    /// row-S (matches real SQL Server's "wait for committed row" semantic).</item>
    /// </list>
    /// Returns true when the row should be yielded; false on READPAST skip.
    /// </summary>
    public bool TouchRowForRead(HeapTable table, int pageIndex, int slotIndex, in DataLockPlan plan)
    {
        if (plan.NoLockReader)
            return true;
        if (plan.RowMode is { } mode)
        {
            // READPAST beside UPDLOCK / XLOCK: probe before acquiring, since
            // the acquire would block. The RC path's ActiveDataWriters gate
            // doesn't serve here — it counts row-X grants only, and the holder
            // this pair most often meets is another UPDLOCK reader's row-U.
            if (plan.SkipBlockedRows
                && ((table.RowLocks.TryGetValue((pageIndex, slotIndex), out var held)
                        && this.Connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(held, mode, this.Connection.Session))
                    || (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0
                        && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } image
                        && !this.TestRowKeyLock(table, image, mode, skipIfBlocked: true))))
            {
                return false;
            }
            this.AcquireRowLockTxScoped(table, pageIndex, slotIndex, mode);
            return true;
        }
        // RC probe path: only act if a conflict exists.
        var connection = this.Connection;
        if (connection.CurrentTransaction is { } tx && tx.EscalatedTables.Contains(table))
            return true;
        // Lock-free table-level gate: with no data-X held anywhere on the
        // table, every row is committed-readable, so skip the per-row lock-
        // resource intern and the manager gate entirely. This is the
        // read-mostly common path — under concurrency it keeps readers off
        // the single LockManager gate, which a per-row probe would otherwise
        // serialize on. A row-X grant increments ActiveDataWriters under the
        // gate before the writer mutates the heap, so a zero read here means
        // no conflicting writer had started.
        if (Volatile.Read(ref table.ActiveDataWriters) == 0)
            return true;
        // A RangeX-X counts as a writer too: a SERIALIZABLE UPDATE's lock on
        // the key past its range refuses this read though no row X is there —
        // when the table changed while the holder's transaction was open,
        // which is when real's READ COMMITTED takes its S at all (probed
        // 2026-09-28 against SQL Server 2025: the same lock behind an XLOCK
        // read, or a DELETE that removed nothing, lets the read through until
        // a write lands).
        if (Volatile.Read(ref table.ActiveKeyRangeLocks) != 0
            && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } keyedImage
            && !this.TestRowKeyLock(table, keyedImage, LockMode.Shared, plan.SkipBlockedRows, unlockedWhenClean: true))
        {
            return false;
        }
        // A writer is somewhere on the table; check this specific row. Use a
        // non-interning lookup — a row with no holder interned can't be in
        // conflict, so reading it through costs no allocation and no gate.
        if (!table.RowLocks.TryGetValue((pageIndex, slotIndex), out var resource))
            return true;
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, LockMode.Shared, connection.Session))
            return true;
        if (plan.SkipBlockedRows)
            return false;
        // Wait for the row's writers to drain. Transient acquire-release
        // matches real SQL Server's RC pattern: "block until committed,
        // then release immediately."
        manager.Acquire(resource, LockMode.Shared, connection.Session, this.LockTimeoutFor(table));
        manager.Release(resource, LockMode.Shared, connection.Session);
        return true;
    }

    /// <summary>
    /// Releases every lock acquired during the current statement. Called by
    /// the dispatch loop in a <c>finally</c> at statement end. Safe to call
    /// even when the list is empty; safe to call multiple times (the list
    /// clears between calls so the second is a no-op).
    /// </summary>
    public void ReleaseStatementSchemaLocks()
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        // Release in reverse acquisition order — symmetric to a stack of
        // acquires. Phase 0 has no order-dependent semantics in release
        // (every Sch-S / Sch-M release pulses the gate independently), but
        // the LIFO discipline matches structured-locking convention.
        for (var i = this.StatementSchemaLocks.Count - 1; i >= 0; i--)
        {
            var (resource, mode) = this.StatementSchemaLocks[i];
            manager.Release(resource, mode, connection.Session);
        }
        this.StatementSchemaLocks.Clear();
        this.noWaitTables.Clear();
    }

    /// <summary>
    /// The lock timeout to use for <paramref name="table"/>: zero when the
    /// current statement named it with a <c>NOWAIT</c> table hint, else the
    /// session's own <see cref="SimulatedDbConnection.LockTimeoutMillis"/>.
    /// </summary>
    private int LockTimeoutFor(HeapTable table)
        => this.noWaitTables.Count != 0 && this.noWaitTables.Contains(table) ? 0 : this.Connection.LockTimeoutMillis;

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
    /// Comparer for <c>@</c>-variable and table-variable names. Unlike
    /// object identifiers (which follow the database collation — see the
    /// name-comparison regimes in <c>docs/claude/collations.md</c>),
    /// variable names fold case, width, and kana type regardless of the
    /// database collation — probe-confirmed (2026-07-13) on a real
    /// <c>SQL_Latin1_General_CP1_CS_AS</c> database: <c>declare @vx int;
    /// set @VX = 5</c> succeeds, as does a fullwidth <c>@ｖx</c>
    /// declaration referenced as <c>@vx</c>.
    /// </summary>
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
            : SimulatedSqlException.MustDeclareScalarVariable(name));

    /// <summary>
    /// Recognizes a local temp-table name (<c>#foo</c>, including bare
    /// <c>#</c>). Global temps (<c>##foo</c>) are recognized by
    /// <see cref="IsGlobalTempName(string)"/> instead. The rule: leading
    /// <c>#</c>, second char is not <c>#</c>.
    /// </summary>
    public static bool IsLocalTempName(string name) =>
        name.Length >= 1 && name[0] == '#' && (name.Length == 1 || name[1] != '#');

    /// <summary>
    /// Recognizes a global temp-table name (<c>##foo</c>, including bare
    /// <c>##</c> — probe-confirmed against SQL Server 2025 that two-char
    /// <c>##</c> is a valid table name). Rule: at least two characters,
    /// both <c>#</c>.
    /// </summary>
    public static bool IsGlobalTempName(string name) =>
        name.Length >= 2 && name[0] == '#' && name[1] == '#';

    /// <summary>
    /// Recognizes a table-variable name (<c>@foo</c>). Used by DML / FROM
    /// resolution to route 1-part references with a leading <c>@</c> to
    /// <see cref="TableVariables"/> instead of the regular schema/temp lookup.
    /// </summary>
    public static bool IsTableVariableName(string name) =>
        name.Length >= 2 && name[0] == '@';

    /// <summary>
    /// When non-null, every base <see cref="HeapTable"/> and every
    /// <see cref="View"/> resolved during parsing is recorded here. Set only
    /// while collecting an indexed view's base-table dependencies at CREATE
    /// INDEX-on-view time (<c>Simulation.IndexedViews.cs</c>); null on the hot
    /// path, so normal parsing pays a single null check.
    /// </summary>
    public (HashSet<HeapTable> Tables, HashSet<View> Views)? DependencySink;

    /// <summary>
    /// Resolves <paramref name="name"/> against the right table dictionary —
    /// the connection's <see cref="SimulatedDbConnection.TempTables"/> for
    /// <c>#foo</c> names, otherwise the named schema (or
    /// <see cref="Database.DefaultSchemaName"/> for an unqualified reference)
    /// plus the simulation's flat system-table dict. Centralizes the routing
    /// rule so callsites (SELECT/INSERT/UPDATE/DELETE/MERGE name lookups,
    /// <c>IDENT_CURRENT</c>, <c>SET IDENTITY_INSERT</c>) stay uniform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution by <see cref="MultiPartName.Count"/>:
    /// </para>
    /// <list type="bullet">
    /// <item>1-part <c>t</c> — temp dict (if <c>#</c>-prefixed); else default
    /// schema then system tables.</item>
    /// <item>2-part <c>schema.t</c> — named schema; falls through to false
    /// when the schema doesn't exist or doesn't hold a table by that name.
    /// System tables are <em>not</em> reachable through a schema qualifier
    /// (real SQL Server's <c>sys.&lt;table&gt;</c> isn't modeled).</item>
    /// <item>3-part <c>db.schema.t</c> — same as 2-part after validating the
    /// db segment matches <see cref="CurrentDatabase"/>'s name; mismatched db
    /// returns false.</item>
    /// <item>4-part <c>server.db.schema.t</c> — false (linked servers not
    /// modeled; the callsite raises Msg 208 via the standard path).</item>
    /// </list>
    /// <para>
    /// For <c>#</c>-prefixed leaves a qualifier is cosmetic and ignored —
    /// matches probe-confirmed behavior for <c>tempdb..#foo</c> /
    /// <c>tempdb.dbo.#foo</c> in DROP TABLE; the connection's temp-table dict
    /// is the routing key regardless of preceding segments.
    /// </para>
    /// </remarks>
    public bool TryResolveTable(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapTable? table)
    {
        // Trigger pseudo-tables INSERTED / DELETED resolve first when a
        // trigger body is in flight. 1-part names only (probe-confirmed:
        // qualified `dbo.inserted` raises Msg 208 in real SQL Server).
        // Pseudo-tables are batch-local materializations — no Sch-S needed
        // (no DDL can target them).
        if (this.TriggerFrame is { } triggerFrame && name.Count == 1)
        {
            if (BuiltInToken.Equals(name.Leaf, "inserted") && triggerFrame.Inserted is { } ins)
            {
                table = ins;
                return true;
            }
            if (BuiltInToken.Equals(name.Leaf, "deleted") && triggerFrame.Deleted is { } del)
            {
                table = del;
                return true;
            }
        }

        // A view's or function's body can't read a temp table (probed
        // 2026-10-01 against SQL Server 2025).
        if (IsLocalTempName(name.Leaf) || IsGlobalTempName(name.Leaf))
        {
            if (this.Parser.BindingViewDefinition)
                throw SimulatedSqlException.ViewOnTemporaryTable();
            if (this.UdfFrame is not null)
                throw SimulatedSqlException.TemporaryTableInFunction();
        }

        if (IsLocalTempName(name.Leaf))
        {
            // Temp tables are session-local; no other connection can DROP
            // them, so Sch-S acquisition is unnecessary (and would be a
            // self-conflict-free no-op anyway). Session-scoped: the parsed
            // Selection binds the specific HeapTable instance from THIS
            // connection's TempTables dict, so a cross-session plan-cache
            // replay would project the wrong table.
            this.HasSessionScopedReference = true;
            this.ResolvedTempTable = true;
            this.CurrentStatement.ReadsTemporaryObject = true;
            return this.Connection.TempTables.TryGetValue(name.Leaf, out table);
        }

        if (IsGlobalTempName(name.Leaf))
        {
            // Global temp tables live on the simulation; the qualifier is
            // cosmetic and ignored (probe-confirmed `tempdb..##q` works the
            // same as `##q`). Sch-S acquisition is skipped — the connection-
            // dispose cleanup is the only DDL that races with reads, and it
            // walks the dict under TryRemove which is atomic against
            // resolution lookups. The binding still captures a specific
            // HeapTable instance whose drop/recreate cycle isn't
            // SchemaVersion-tracked, so the plan-cache treats it as
            // session-scoped and declines.
            this.HasSessionScopedReference = true;
            this.ResolvedTempTable = true;
            this.CurrentStatement.ReadsTemporaryObject = true;
            return this.Connection.Simulation.GlobalTempTables.TryGetValue(name.Leaf, out table);
        }

        // Table-variable routing: @-prefixed leaves are per-batch, 1-part-only
        // (probe-confirmed: dbo.@t raises Msg 102 at parse). Dict key is the
        // @-stripped name (matches Variables dict convention). Table variables
        // are per-batch — no concurrency, no Sch-S. Strictly batch-scoped, so
        // any cached plan referencing one is invalid the moment it leaves the
        // owning batch.
        if (IsTableVariableName(name.Leaf))
        {
            if (name.Count > 1)
            {
                table = null;
                return false;
            }
            this.HasSessionScopedReference = true;
            this.CurrentStatement.ReadsTemporaryObject = true;
            this.CurrentStatement.ReadsTableVariable = true;
            return this.TableVariables.TryGetValue(name.Leaf[1..], out table);
        }

        if (!this.TryResolveSchema(name, out var schema))
        {
            // 1-part fallback to system tables when the default schema lookup
            // misses; matches the legacy bare-`systypes` access path. System
            // tables are immutable and SHARED across Simulations (the dict
            // lives in BuiltInResources as a static Value), so per-instance
            // lock acquisition would race across simulations — skip the
            // schema-stability acquire; nothing can DDL them anyway.
            if (name.Count == 1)
                return Simulation.SystemHeapTables.TryGetValue(name.Leaf, out table);
            table = null;
            return false;
        }

        if (schema.HeapTables.TryGetValue(name.Leaf, out table))
        {
            this.CurrentStatement.ReadsPermanentObject = true;
            this.AcquireStatementLock(table.SchemaLock, LockMode.SchemaStability);
            _ = this.DependencySink?.Tables.Add(table);
            return true;
        }

        // Synonym redirect: `FROM syn` where `syn FOR t` resolves through to
        // the base table (recursing so a schema-qualified base routes too).
        // A synonym whose base is a view returns false here and is picked up
        // by the caller's TryResolveView, which applies the same redirect.
        if (this.TryRedirectThroughSynonym(schema, name, out var tableBase))
            return this.TryResolveTable(tableBase, out table);

        // Bare 1-part also falls through to system tables when the default
        // schema doesn't hold the table — same shared-instance reasoning,
        // no Sch-S acquire.
        if (name.Count == 1)
            return Simulation.SystemHeapTables.TryGetValue(name.Leaf, out table);

        table = null;
        return false;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to the <see cref="Schema"/> a CREATE /
    /// DROP / TRUNCATE / SELECT-INTO / FROM target lives in. Returns false
    /// when the schema doesn't exist, when a 3-part name's db segment
    /// doesn't match any database in <see cref="Simulation.Databases"/>, or
    /// when the name is 4-part (linked-server names aren't modeled — the
    /// simulator returns false rather than silently ignoring the server
    /// segment). 3-part names route to the named database, enabling
    /// cross-database SELECT / JOIN / catalog-view inspection and DML; the
    /// returned <see cref="Schema"/> carries its owning <see cref="Database"/>
    /// via <see cref="Schema.Database"/> so callers (catalog-view enumerators,
    /// constraint-violation error messages, etc.) can scope correctly.
    /// A 1-part name resolves to the connection's <see cref="CurrentDatabase"/>
    /// + <see cref="Database.DefaultSchemaName"/> (always present, so the
    /// 1-part branch never returns false).
    /// </summary>
    /// <summary>
    /// The database a written object name targets — the one a three-part
    /// name's leading segment resolves to, else <see cref="CurrentDatabase"/>.
    /// The pre-resolution counterpart to <see cref="DatabaseFor(SchemaObject)"/>:
    /// the DDL permission gates need the target database before the schema
    /// lookup that would produce an object to read it off. An unrecognized
    /// database segment falls back to the session's, leaving the miss for the
    /// caller's own resolution step to report.
    /// </summary>
    public Database DatabaseForName(MultiPartName name) =>
        name.Count == 3 && this.Connection.Simulation.Databases.TryGetValue(name[0], out var database)
            ? database
            : this.CurrentDatabase;

    /// <summary>
    /// Schema an unqualified name resolves to while a <c>CREATE SCHEMA
    /// &lt;name&gt; &lt;element&gt; …</c> element list is being bound — the
    /// schema being created rather than
    /// <see cref="Database.DefaultSchemaName"/>. Real scopes the elements that
    /// way (probe-confirmed: an element's <c>CREATE TABLE t</c> lands in the new
    /// schema and a sibling <c>GRANT SELECT ON t</c> grants on it), and routing
    /// it through the one place unqualified names bind is what makes creation
    /// sites and reference sites agree. Null everywhere else.
    /// </summary>
    internal string? CreateSchemaElementScope;

    /// <summary>
    /// The schema a <c>CREATE SCHEMA</c> statement creates, while the compile
    /// pass walks its element list without creating it, so an element naming
    /// it as a qualifier defers to the run. Null everywhere else.
    /// </summary>
    internal string? SchemaCompiledUncreated;

    public bool TryResolveSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema)
    {
        if (name.Count >= 4)
        {
            schema = null;
            return false;
        }
        var database = this.CurrentDatabase;
        if (name.Count == 3)
        {
            // 3-part name: route to the named database (which may be the
            // current one or any other instance-hosted database). Missing
            // database returns false — the caller surfaces Msg 208 the same
            // way a missing table does, matching real SQL Server.
            if (!this.Connection.Simulation.Databases.TryGetValue(name[0], out database))
            {
                schema = null;
                return false;
            }
        }
        var schemaName = name.Count >= 2
            ? name.ImmediateQualifier!
            : this.CreateSchemaElementScope ?? Database.DefaultSchemaName;
        return database.Schemas.TryGetValue(schemaName, out schema);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered scalar
    /// <see cref="UserDefinedFunction"/>. Schema-qualified (2- or 3-part)
    /// references route through <see cref="TryResolveSchema"/>; 1-part names
    /// fall through to <see langword="false"/> (real SQL Server treats
    /// unqualified UDF calls as built-in function lookups, raising Msg 195
    /// when nothing matches — the call site enforces that 2-part minimum by
    /// only invoking this resolver when <see cref="MultiPartName.Count"/>
    /// is &gt;= 2).
    /// </summary>
    public bool TryResolveFunction(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function)
    {
        function = null;
        if (name.Count < 2 || !this.TryResolveSchema(name, out var schema))
            return false;
        if (!schema.Functions.TryGetValue(name.Leaf, out function))
        {
            // Synonym redirect: `SELECT dbo.syn(1)` where `syn FOR f` calls the
            // base scalar function, and `FROM dbo.syn(1)` the base TVF
            // (probe-confirmed both work on real). A base written unqualified
            // needs the default schema attached, since a 1-part name never
            // reaches function resolution at a call site (Msg 195).
            if (!this.TryRedirectThroughSynonym(schema, name, out var functionBase))
                return false;
            if (functionBase.Count == 1)
                functionBase = new MultiPartName(Database.DefaultSchemaName).WithAddedPart(functionBase.Leaf);
            return this.TryResolveFunction(functionBase, out function);
        }
        this.AcquireStatementLock(function.SchemaLock, LockMode.SchemaStability);
        this.CurrentStatement.MarkOpensTransaction();
        this.CurrentStatement.CallsUserFunction = true;
        return true;
    }

    /// <summary>
    /// <see cref="TryResolveFunction"/> for a FROM or APPLY source, which
    /// answers only a table-valued function and which a one-part name reaches
    /// through the default schema, where a scalar call's never does (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    public bool TryResolveTableValuedFunction(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UserDefinedFunction? function) =>
        (this.TryResolveFunction(name, out function)
            || (name.Count == 1 && this.TryResolveFunction(new MultiPartName(Database.DefaultSchemaName).WithAddedPart(name.Leaf), out function)))
        && function is InlineTableValuedFunction or MultiStatementTableValuedFunction or ClrTableValuedFunction;

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="View"/>.
    /// Unlike scalar UDFs, views accept 1-part names too (probe-confirmed:
    /// <c>FROM v1</c> works the same as <c>FROM dbo.v1</c>) — the lookup
    /// falls back to <see cref="Database.DefaultSchemaName"/> for the
    /// unqualified case. Schema-qualified misses return false; the caller
    /// is responsible for routing those to Msg 208.
    /// </summary>
    public bool TryResolveView(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out View? view)
    {
        view = null;
        if (!this.TryResolveSchema(name, out var schema))
            return false;
        if (!schema.Views.TryGetValue(name.Leaf, out view))
        {
            // Synonym redirect for a synonym whose base is a view (see
            // TryResolveTable for the table-base case).
            return this.TryRedirectThroughSynonym(schema, name, out var viewBase)
                && this.TryResolveView(viewBase, out view);
        }
        this.CurrentStatement.ReadsPermanentObject = true;
        this.AcquireStatementLock(view.SchemaLock, LockMode.SchemaStability);
        _ = this.DependencySink?.Views.Add(view);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="Procedure"/>.
    /// Like views (and unlike scalar UDFs), procedures accept 1-part names —
    /// probe-confirmed: <c>EXEC p1</c> finds <c>dbo.p1</c>. The lookup falls
    /// back to <see cref="Database.DefaultSchemaName"/> for the unqualified
    /// case; schema-qualified misses return false (caller routes to Msg 2812).
    /// </summary>
    public bool TryResolveProcedure(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Procedure? procedure)
    {
        procedure = null;
        if (!this.TryResolveSchema(name, out var schema)
            || !schema.Procedures.TryGetValue(name.Leaf, out procedure))
        {
            return false;
        }
        this.AcquireStatementLock(procedure.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered DML <see cref="Trigger"/>.
    /// Triggers share the schema's object-name namespace; the lookup falls back
    /// to <see cref="Database.DefaultSchemaName"/> for the unqualified case.
    /// Used by <c>OBJECT_ID</c> so the canonical
    /// <c>OBJECT_DEFINITION(OBJECT_ID('trg'))</c> idiom resolves. DDL triggers
    /// (database-scoped, not schema-resident) aren't covered here.
    /// </summary>
    public bool TryResolveTrigger(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Trigger? trigger)
    {
        trigger = null;
        if (!this.TryResolveSchema(name, out var schema)
            || !schema.Triggers.TryGetValue(name.Leaf, out trigger))
        {
            return false;
        }
        this.AcquireStatementLock(trigger.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered user-defined
    /// <see cref="TableType"/>. Like views / procedures (and unlike scalar
    /// UDFs), table types accept 1-part names: probe-confirmed against SQL
    /// Server 2025 that <c>DECLARE @t MyType</c> finds <c>dbo.MyType</c>.
    /// The lookup falls back to <see cref="Database.DefaultSchemaName"/> for
    /// the unqualified case.
    /// </summary>
    public bool TryResolveTableType(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TableType? tableType)
    {
        tableType = null;
        if (!this.TryResolveSchema(name, out var schema)
            || !schema.TableTypes.TryGetValue(name.Leaf, out tableType))
        {
            return false;
        }
        this.AcquireStatementLock(tableType.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered scalar
    /// <see cref="AliasType"/> (UDDT) in the per-database schema dictionary.
    /// Like table types, alias types accept 1-part names with fallback to
    /// <see cref="Database.DefaultSchemaName"/>; 2-part qualified references
    /// route through <see cref="TryResolveSchema"/>. Used by every type-
    /// reference parser site (CREATE TABLE column, DECLARE @v, procedure /
    /// function / sequence param, ALTER TABLE ALTER COLUMN, OPENJSON, EXEC
    /// dynamic-SQL parameter) to determine whether a parsed type reference
    /// expands to a built-in or to an alias's underlying type.
    /// </summary>
    public bool TryResolveAliasType(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AliasType? aliasType)
    {
        aliasType = null;
        return this.TryResolveSchema(name, out var schema)
            && schema.AliasTypes.TryGetValue(name.Leaf, out aliasType);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a registered <see cref="Sequence"/>.
    /// Accepts 1-part names (probe-confirmed: <c>NEXT VALUE FOR seq1</c> finds
    /// <c>dbo.seq1</c>) with fallback to <see cref="Database.DefaultSchemaName"/>;
    /// 2-part / 3-part qualified routes through the named schema. Returns false
    /// on miss (caller routes to Msg 208 for unknown name or Msg 11726 if the
    /// name resolves to a non-sequence object).
    /// </summary>
    public bool TryResolveSequence(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Sequence? sequence)
    {
        sequence = null;
        if (!this.TryResolveSchema(name, out var schema)
            || !schema.Sequences.TryGetValue(name.Leaf, out sequence))
        {
            return false;
        }
        this.AcquireStatementLock(sequence.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a <see cref="Synonym"/> — the
    /// synonym object itself, without following it to its base. Accepts 1-part
    /// names with the usual <see cref="Database.DefaultSchemaName"/> fallback.
    /// Used by <c>OBJECT_ID</c> (which reports a synonym's own id and never
    /// follows it — probe-confirmed <c>OBJECT_ID('syn', 'U')</c> is NULL) and
    /// by the DROP / error paths that need to know a name is a synonym.
    /// </summary>
    public bool TryResolveSynonym(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Synonym? synonym)
    {
        synonym = null;
        if (!this.TryResolveSchema(name, out var schema) || !schema.Synonyms.TryGetValue(name.Leaf, out synonym))
            return false;
        this.AcquireStatementLock(synonym.SchemaLock, LockMode.SchemaStability);
        return true;
    }

    /// <summary>
    /// Resolves the object a <paramref name="synonym"/> points at, by direct
    /// dictionary lookup rather than through the kind-specific resolvers (so
    /// no second redirect is applied). Returns false when the base name has no
    /// object behind it — the deferred-resolution case real reports as
    /// Msg 5313 at first use.
    /// </summary>
    public bool TryResolveSynonymBase(Synonym synonym, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SchemaObject? baseObject)
    {
        baseObject = null;
        if (!this.TryResolveSchema(synonym.BaseObject, out var schema))
            return false;
        var collation = schema.Database.Collation;
        foreach (var candidate in schema.SchemaObjects())
        {
            if (collation.Equals(candidate.Name, synonym.BaseObject.Leaf))
            {
                baseObject = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Rewrites <paramref name="name"/> to the base object it names when it is
    /// a synonym, leaving any other name untouched. The EXEC path expands
    /// before resolving so a synonym over a missing procedure reports Msg 2812
    /// naming the base, matching real.
    /// </summary>
    public MultiPartName ExpandSynonym(MultiPartName name) =>
        this.TryResolveSchema(name, out var schema) && this.TryRedirectThroughSynonym(schema, name, out var baseName)
            ? baseName
            : name;

    /// <summary>
    /// The error a reference to <paramref name="name"/> raises when nothing
    /// resolved: Msg 208 for an unknown name, or Msg 5313 when the name is a
    /// synonym (whose base binds lazily, so the failure belongs to the base,
    /// not the synonym). Real distinguishes the two 5313 states — 1 when the
    /// base names nothing, 224 when it names an object the reference can't use
    /// (a procedure or sequence in a FROM clause).
    /// </summary>
    /// <remarks>
    /// Binding a module definition, real reports a missing schema-qualified
    /// object at line 12 whatever the definition's layout, and an unqualified
    /// one at the line of its name (probed 2026-09-26 against SQL Server 2025).
    /// </remarks>
    public SimulatedSqlException UnresolvableObjectName(MultiPartName name)
    {
        if (this.TryResolveSynonym(name, out var synonym))
            return SimulatedSqlException.SynonymRefersToInvalidObject(name.ToString(), this.TryResolveSynonymBase(synonym, out _) ? (byte)224 : (byte)1);
        var error = SimulatedSqlException.InvalidObjectName(name);
        if (!this.CreateTimeBinding && !this.Parser.BindingViewDefinition)
            return error;
        if (name.Count >= 2)
            return error.PinLine(12);
        return this.Parser.Token is { } leaf ? error.PinLine(leaf.LineNumber + this.LineOffset) : error;
    }

    /// <summary>
    /// The shared synonym-redirect step behind <see cref="TryResolveTable"/> /
    /// <see cref="TryResolveView"/> / <see cref="TryResolveFunction"/>: hands
    /// back the base name when <paramref name="name"/>'s leaf is a synonym in
    /// <paramref name="schema"/>. A base that is itself a synonym raises
    /// Msg 470 — real accepts the <c>CREATE SYNONYM</c> that builds the chain
    /// and rejects it at first use, so the check belongs here rather than at
    /// creation.
    /// </summary>
    private bool TryRedirectThroughSynonym(Schema schema, MultiPartName name, out MultiPartName baseName)
    {
        if (!schema.Synonyms.TryGetValue(name.Leaf, out var synonym))
        {
            baseName = default;
            return false;
        }
        this.AcquireStatementLock(synonym.SchemaLock, LockMode.SchemaStability);
        baseName = synonym.BaseObject;
        return this.TryResolveSchema(baseName, out var baseSchema) && baseSchema.Synonyms.ContainsKey(baseName.Leaf)
            ? throw SimulatedSqlException.SynonymChainingNotAllowed(name.ToString(), baseName.ToString())
            : true;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a <see cref="CatalogView"/> in
    /// either the <c>sys</c> or <c>INFORMATION_SCHEMA</c> schema. Returns
    /// true for 2-part names <c>{sys|INFORMATION_SCHEMA}.&lt;view&gt;</c>
    /// (case-insensitive) whose leaf matches a registered view, or for
    /// 3-part names whose db segment names a database in
    /// <see cref="Simulation.Databases"/>. <paramref name="targetDatabase"/>
    /// receives the database the view is scoped to — the connection's
    /// <see cref="CurrentDatabase"/> for 2-part references, the resolved
    /// instance-hosted database for 3-part references. The catalog-view row
    /// generator reads from this rather than <c>batch.CurrentDatabase</c>
    /// so <c>WideWorldImporters.sys.tables</c> projects WWI's tables even
    /// when the connection is pointed at a different DB.
    /// Used by the FROM parser to route catalog-view references to virtual
    /// projections before falling through to the regular
    /// <see cref="TryResolveTable"/> path. The registry is keyed by the
    /// fully-qualified name (e.g. <c>"sys.tables"</c>,
    /// <c>"INFORMATION_SCHEMA.COLUMNS"</c>) so one resolver can serve both
    /// schemas without per-namespace dispatch.
    /// </summary>
    public bool TryResolveCatalogView(
        MultiPartName name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogView? view,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Database? targetDatabase)
    {
        view = null;
        targetDatabase = null;
        if (name.Count is not (1 or 2 or 3))
            return false;
        targetDatabase = this.CurrentDatabase;
        if (name.Count == 3 && !this.Connection.Simulation.Databases.TryGetValue(name[0], out targetDatabase))
            return false;
        // A 1-part name resolves only the legacy compatibility views registered
        // under a bare (dot-less) key — sysobjects / sysusers, which live in the
        // sys schema but resolve unqualified (probe-confirmed: bare `sysobjects`
        // works, bare `objects` / `tables` raise Msg 208). Every modern catalog
        // view is keyed `sys.<name>` / `INFORMATION_SCHEMA.<name>`, so a bare
        // user-table name never collides.
        var key = name.Count == 1 ? name.Leaf : $"{name.ImmediateQualifier}.{name.Leaf}";
        if (!Simulation.CatalogViews.TryGetValue(key, out view)
            && !TryResolveCompatibilityViewThroughDbo(name, targetDatabase, out view))
        {
            return false;
        }
        // master.dbo.spt_values (and its 1-/2-part forms) resolve only when the
        // reference lands in master — the compatibility table exists nowhere else.
        if (view.MasterScoped && !Collation.Baseline.Equals(targetDatabase.Name, Simulation.MasterDatabaseName))
        {
            view = null;
            targetDatabase = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// A compatibility view (<c>sysobjects</c>, <c>sysprocesses</c>, …) also
    /// resolves under <c>dbo</c> — <c>dbo.sysobjects</c>,
    /// <c>master.dbo.sysprocesses</c>, <c>master..sysobjects</c> — where a
    /// modern catalog view doesn't (<c>dbo.tables</c> is Msg 208), unless a
    /// <c>dbo</c> object of that name is there to take it (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    private static bool TryResolveCompatibilityViewThroughDbo(MultiPartName name, Database targetDatabase, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogView? view)
    {
        view = null;
        return name.Count > 1
            && Collation.Baseline.Equals(name.ImmediateQualifier, Database.DefaultSchemaName)
            && Simulation.CatalogViews.TryGetValue(name.Leaf, out view)
            && !(targetDatabase.Schemas.TryGetValue(Database.DefaultSchemaName, out var dbo)
                && (dbo.HeapTables.ContainsKey(name.Leaf) || dbo.Views.ContainsKey(name.Leaf) || dbo.Synonyms.ContainsKey(name.Leaf)));
    }

    /// <summary>
    /// Routes a four-part name (<c>server.db.schema.t</c>) through the
    /// connection's <see cref="Simulation.ActiveLinkedServers"/> dict to
    /// the matching <see cref="LinkedServer"/> + remote <see cref="HeapTable"/>.
    /// Returns false for non-4-part names (so callers can fall through to
    /// regular table resolution); returns false for 4-part names whose
    /// leading segment isn't an active linked server (caller surfaces
    /// Msg 208 via the standard <c>InvalidObjectName</c> path). On a
    /// successful match, <paramref name="remoteDatabaseName"/> /
    /// <paramref name="remoteSchemaName"/> are the resolved literal
    /// segments (with empty-middle <c>srv..t</c> substitution applied via
    /// <see cref="Database.DefaultSchemaName"/>) so the caller can plumb
    /// them into the remote-query SQL string.
    /// </summary>
    /// <remarks>
    /// Looks up the remote table or view via direct access to
    /// <see cref="LinkedServer.Target"/>'s <see cref="Database.Schemas"/>
    /// dict — parse-time metadata stays in-process even though execution
    /// round-trips through the remote's public ADO.NET surface. Matches
    /// real SQL Server's "metadata at compile, data at execute" linked-
    /// server contract; the in-process shortcut avoids a no-row query on
    /// every parse without changing observable behavior.
    /// </remarks>
    public bool TryResolveLinkedServerTable(
        MultiPartName name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LinkedServer? linkedServer,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapColumn[]? remoteColumns,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteDatabaseName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteSchemaName)
    {
        linkedServer = null;
        remoteName = null;
        remoteColumns = null;
        remoteDatabaseName = null;
        remoteSchemaName = null;
        return name.Count == 4
            && this.Connection.Simulation.ActiveLinkedServers.TryGetValue(name[0], out linkedServer)
            && TryResolveRemoteTable(linkedServer, name, out remoteName, out remoteColumns, out remoteDatabaseName, out remoteSchemaName);
    }

    /// <summary>
    /// The table or view the last three segments of the four-part
    /// <paramref name="name"/> name on <paramref name="linkedServer"/> — a
    /// linked server, or the one an ad hoc <c>OPENROWSET</c> connects to —
    /// as <see cref="TryResolveLinkedServerTable"/> describes.
    /// </summary>
    public static bool TryResolveRemoteTable(
        LinkedServer linkedServer,
        MultiPartName name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapColumn[]? remoteColumns,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteDatabaseName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? remoteSchemaName)
    {
        remoteName = null;
        remoteColumns = null;
        remoteDatabaseName = null;
        remoteSchemaName = null;

        // Empty middle segments fall back to defaults: missing db → the
        // database a fresh session of the server starts in; missing schema →
        // the remote's per-database DefaultSchemaName.
        var dbSegment = string.IsNullOrEmpty(name[1]) ? linkedServer.SessionDatabaseName : name[1];
        if (!linkedServer.Target.Databases.TryGetValue(dbSegment, out var remoteDatabase))
            return false;
        var schemaSegment = string.IsNullOrEmpty(name[2]) ? Database.DefaultSchemaName : name[2];
        if (!remoteDatabase.Schemas.TryGetValue(schemaSegment, out var remoteSchema))
            return false;
        // A view reads as its projection does.
        if (remoteSchema.HeapTables.TryGetValue(name.Leaf, out var remoteTable))
        {
            remoteName = remoteTable.Name;
            remoteColumns = Array.FindAll(remoteTable.Columns, column => !column.IsHidden);
        }
        else if (remoteSchema.Views.TryGetValue(name.Leaf, out var remoteView))
        {
            remoteName = remoteView.Name;
            remoteColumns = remoteView.OutputColumns;
        }
        else
        {
            return false;
        }
        remoteColumns = RemoteWrite.ProviderColumns(linkedServer, name, remoteColumns);
        remoteDatabaseName = dbSegment;
        remoteSchemaName = schemaSegment;
        return true;
    }

    /// <summary>
    /// Parses an object name (1–4 dotted segments) at the current token,
    /// leaving the cursor on the <em>last</em> consumed name segment (matching
    /// the standard parser-context contract that every parser leaves Token on
    /// its last consumed token). An empty middle segment (<c>db..table</c>,
    /// <c>tempdb..#foo</c>) substitutes <see cref="Database.DefaultSchemaName"/>
    /// so <c>db..table</c> resolves identically to <c>db.dbo.table</c> —
    /// real SQL Server uses the login's default schema (probe-confirmed); the
    /// simulator has no per-login schema and routes everything through
    /// <c>dbo</c>, so the substitution is exact for the modeled case.
    /// Used everywhere a table-shaped name appears (CREATE / DROP / TRUNCATE
    /// / SELECT-FROM / INSERT / UPDATE / DELETE / MERGE / SET IDENTITY_INSERT)
    /// so the multi-part-name grammar lives in one place. The 5th segment
    /// raises Msg 4104 via <see cref="MultiPartName.WithAddedPart"/>.
    /// </summary>
    public static MultiPartName ParseObjectName(ParserContext context, bool acceptTableVariable = false)
    {
        // Table-variable references (@t in DML target / FROM-source position
        // when <paramref name="acceptTableVariable"/> is true): accept as a
        // 1-part name with the @ kept in the leaf so downstream routing
        // (TryResolveTable's IsTableVariableName check) can identify it. A
        // trailing `.` raises a syntax error matching the probe-confirmed
        // Msg 102 for `dbo.@t` (real SQL Server rejects any dotted form
        // involving an @-prefixed segment at parse time). Contexts where @t
        // isn't legal (CREATE TABLE / ALTER TABLE / DROP TABLE / TRUNCATE
        // TABLE / SELECT INTO) leave <paramref name="acceptTableVariable"/>
        // false so the @ token falls through to a syntax error — matches
        // probe-confirmed Msg 102 for those statement shapes.
        if (acceptTableVariable && context.Token is AtPrefixedString atVar)
        {
            var leaf = "@" + atVar.Value;
            var atCheckpoint = context.SaveCheckpoint();
            if (context.MoveNext() && context.Token is Operator { Character: '.' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.RestoreCheckpoint(atCheckpoint);
            return new MultiPartName(leaf);
        }
        // Leading empty segment(s): `..name` (db + schema omitted) or
        // `.name` (schema omitted) resolve to the current database / default
        // schema, matching real SQL Server (`SELECT * FROM ..t` reads dbo.t
        // in the current db; `EXEC ..sp_foo` runs the system/dbo proc; the
        // form SqlClient's SqlBulkCopy metadata batch sends is
        // `exec ..sp_tablecollations_100`). Drop the omitted leading
        // positions — an empty db means the current database anyway, and the
        // trailing segments carry the schema/object identity — so the name
        // parses as if the empties weren't written.
        var omittedLeading = 0;
        while (context.Token is Operator { Character: '.' })
        {
            omittedLeading++;
            if (!context.MoveNext())
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var schemaOmitted = false;

        if (context.Token is not Name first)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = new MultiPartName(first.Value);
        while (true)
        {
            // Peek for a `.` continuation without permanently advancing — if
            // the next token isn't a dot, restore so the cursor sits on the
            // last consumed name segment.
            var checkpoint = context.SaveCheckpoint();
            if (!context.MoveNext() || context.Token is not Operator { Character: '.' })
            {
                context.RestoreCheckpoint(checkpoint);
                return name.WithOmissions(omittedLeading, schemaOmitted);
            }

            // Advanced past the dot. Read the next segment — a Name extends
            // the dotted name; a second `.` is an empty segment that we skip
            // and read one more time.
            if (!context.MoveNext())
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is Name next)
            {
                name = name.WithAddedPart(next.Value);
                continue;
            }
            if (context.Token is Operator { Character: '.' } && context.MoveNext() && context.Token is Name afterEmpty)
            {
                // Empty middle segment (`db..table`). Substitute the default
                // schema name so the resolver sees `db.dbo.table` — real
                // SQL Server uses the login's default schema; the simulator
                // has no per-login schema and routes everything through dbo.
                // The pattern is required for cross-database short-form
                // queries to land in the correct database (the leading
                // segment routes to that DB rather than being interpreted
                // as a schema in the current DB).
                name = name.WithAddedPart(Database.DefaultSchemaName).WithAddedPart(afterEmpty.Value);
                schemaOmitted = true;
                continue;
            }
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
