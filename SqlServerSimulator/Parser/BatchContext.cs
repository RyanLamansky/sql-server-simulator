using SqlServerSimulator.Parser.Tokens;
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
    /// <see cref="Storage.VersionStore.FinalizePendingEntries"/> and on
    /// statement failure via <see cref="Storage.VersionStore.DiscardPendingEntries"/>.
    /// </summary>
    public List<Storage.PendingVersionEntry>? CurrentStatementVersionEntries;

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
    /// Routes a captured version entry to the active transaction's
    /// <see cref="SimulatedDbTransaction.PendingVersionEntries"/> list if
    /// any, otherwise to the statement-scoped
    /// <see cref="CurrentStatementVersionEntries"/>. No-op when no list is
    /// active (the caller already short-circuited via the
    /// <see cref="Storage.VersionStore.IsVersioningEnabled"/> guard).
    /// </summary>
    internal void AppendPendingVersionEntry(Storage.PendingVersionEntry entry)
    {
        var tx = this.Connection.CurrentTransaction;
        if (tx is not null)
            tx.PendingVersionEntries.Add(entry);
        else
            this.CurrentStatementVersionEntries?.Add(entry);
    }

    /// <summary>
    /// Per-statement scratch frame, allocated once per batch and overwritten
    /// in place by the dispatch loop at the top of each statement iteration.
    /// See <see cref="StatementContext"/> for the fields it carries.
    /// </summary>
    public readonly StatementContext CurrentStatement = new();

    /// <summary>
    /// Buffer of <c>PRINT</c>-emitted strings collected across this batch.
    /// Probe-confirmed coalescing semantic: multiple <c>PRINT</c> statements
    /// in one command join with <c>\n</c> separators into a single
    /// <see cref="SimulatedDbConnection.InfoMessage"/> firing at end of
    /// dispatch. Null when no PRINT has fired yet (avoids the per-batch
    /// allocation for the typical PRINT-less batch).
    /// </summary>
    private List<string>? pendingPrintMessages;

    /// <summary>
    /// 1-based line of the first <c>PRINT</c> statement in this batch —
    /// captured at the moment the first message is buffered. SqlClient
    /// probe-confirmed: the coalesced <c>InfoMessage</c> event carries
    /// the first contributing statement's line, even when later <c>PRINT</c>s
    /// in the same batch live on different lines.
    /// </summary>
    private int firstPrintLine;

    /// <summary>
    /// Buffers a <c>PRINT</c>-emitted string against this batch's pending
    /// output list. Caller has already formatted the operand value into its
    /// display string (NULL → single space per probe). Skipped-IF / loop-
    /// control suppression is decided by the caller (<see cref="IsSkipping"/>),
    /// not here.
    /// </summary>
    internal void AppendPrintMessage(string text)
    {
        if (this.pendingPrintMessages is null)
        {
            this.pendingPrintMessages = [];
            this.firstPrintLine = this.CurrentStatement.StartLine;
        }
        this.pendingPrintMessages.Add(text);
    }

    /// <summary>
    /// If any <c>PRINT</c> statements buffered output during this batch,
    /// delivers them to <see cref="SimulatedDbConnection.InfoMessage"/>
    /// subscribers as a single event (messages joined with <c>\n</c>,
    /// <see cref="SimulatedInfoMessageEventArgs.LineNumber"/> set to the
    /// first contributing PRINT's line). No-op when the buffer is empty.
    /// Called by <see cref="Simulation.CreateResultSetsForCommand"/> after
    /// dispatch completes.
    /// </summary>
    internal void FlushPrintMessages()
    {
        if (this.pendingPrintMessages is not { Count: > 0 } list)
            return;
        var joined = string.Join('\n', list);
        this.Connection.RaiseInfoMessage(new SimulatedInfoMessageEventArgs(
            joined,
            this.firstPrintLine,
            source: "SqlServerSimulator"));
        list.Clear();
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
    /// Total WHILE iterations executed in this batch. Counted across all
    /// loops; the cap is global per batch. Real SQL Server has no such cap
    /// (timeouts handle runaway loops in production); the simulator caps
    /// at <see cref="LoopIterationLimit"/> so a buggy test doesn't hang CI.
    /// </summary>
    public long LoopIterations;

    /// <summary>Per-batch ceiling on total WHILE iterations.</summary>
    public const long LoopIterationLimit = 100_000;

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
    /// Fidelity gap vs SQL Server (un-taken IF): real SQL Server defers name
    /// resolution for un-taken IF branches (an un-taken
    /// <c>SELECT bad_col FROM bad_table</c> runs silently). The simulator
    /// parses both branches the same way, so invalid table/column references
    /// in un-taken branches still raise <c>Msg 208</c> / <c>Msg 207</c> here.
    /// Common patterns (<c>IF NOT EXISTS (…) CREATE TABLE foo (…)</c>,
    /// <c>IF OBJECT_ID('foo','U') IS NOT NULL DROP TABLE foo</c>) reference
    /// names that exist at parse time when the branch is skipped, so they
    /// work end-to-end; only synthetic patterns that name nothing-tables hit
    /// the gap. BREAK / CONTINUE scope checks (Msg 135 / 136) explicitly
    /// don't defer — they fire even in skip mode, matching real SQL Server's
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
    /// Acquires <paramref name="mode"/> on <paramref name="resource"/> for
    /// the current connection, honoring the connection's
    /// <see cref="SimulatedDbConnection.LockTimeoutMillis"/>, and records the
    /// acquisition in <see cref="StatementSchemaLocks"/> so the dispatch
    /// loop releases it at statement end. The two-phase split (acquire then
    /// record) is fine because <see cref="LockManager.Acquire"/> can only
    /// fail by throwing — on success the lock IS held, and we always reach
    /// the append. On throw the lock isn't held, no cleanup needed.
    /// </summary>
    public void AcquireStatementLock(LockResource resource, LockMode mode)
    {
        var connection = this.Connection;
        connection.Simulation.LockManager.Acquire(resource, mode, connection, connection.LockTimeoutMillis);
        this.StatementSchemaLocks.Add((resource, mode));
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
    public void AcquireTransactionLock(LockResource resource, LockMode mode)
    {
        var connection = this.Connection;
        connection.Simulation.LockManager.Acquire(resource, mode, connection, connection.LockTimeoutMillis);
        if (connection.CurrentTransaction is { } tx)
            tx.HeldLocks.Add((resource, mode));
        else
            this.StatementSchemaLocks.Add((resource, mode));
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
    /// <item>Read session <c>SERIALIZABLE</c> (no TABLOCK*) → table-S tx-scoped
    /// for phantom-prevention at table granularity (the simulator has no
    /// indexes to range-lock through; locking the whole table is the
    /// closest faithful approximation).</item>
    /// <item>Read session <c>READ UNCOMMITTED</c> / hint <c>NOLOCK</c> → no
    /// table-level lock acquired (dirty read).</item>
    /// <item>Read default (RC / RR / HOLDLOCK hint) → table-IS.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Per-row plan:
    /// <list type="bullet">
    /// <item>NOLOCK / RU isolation → no per-row lock; reader doesn't probe.</item>
    /// <item>RC reader → no row-S acquisition; reader probes each row for
    /// an incompatible row-X holder and waits (or skips with READPAST).</item>
    /// <item>RR / HOLDLOCK reader → row-S tx-scoped per touched row.</item>
    /// <item>SERIALIZABLE reader → table-S tx-scoped already covers the
    /// whole scan; no per-row row-S needed.</item>
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
        if (table.IsTableVariable || IsLocalTempName(table.Name))
            return DataLockPlan.Bypass;
        if (Simulation.SystemHeapTables.ContainsValue(table))
            return DataLockPlan.Bypass;

        var connection = this.Connection;
        var isolation = connection.SessionIsolationLevel;

        // SNAPSHOT isolation reaching a user table in a database where
        // ALLOW_SNAPSHOT_ISOLATION is OFF raises Msg 3952. Probe-confirmed
        // the rejection point is the first user-table access, not the SET
        // statement and not BeginTransaction. The bypass paths above
        // (table-variable / local-temp / system table) are the same ones
        // real SQL Server doesn't apply Msg 3952 to — system catalogs work
        // fine inside an SI session regardless of the database flag.
        if (isolation == System.Data.IsolationLevel.Snapshot && !this.CurrentDatabase.AllowSnapshotIsolation)
            throw SimulatedSqlException.SnapshotIsolationNotAllowed(this.CurrentDatabase.Name);

        // Read uncommitted / NOLOCK: skip everything. Dirty-read semantics.
        if (!isWrite && (hints.NoLock || isolation == System.Data.IsolationLevel.ReadUncommitted))
            return DataLockPlan.NoLock;

        // TABLOCKX: table-X tx-scoped; no per-row work.
        if (hints.TabLockX)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.Exclusive);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }

        if (hints.TabLock)
        {
            if (isWrite)
            {
                this.AcquireTransactionLock(table.TableDataLock, LockMode.Exclusive);
                return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
            }
            // Reader TABLOCK: table-S. Tx-scoped iff HOLDLOCK/SER/REPEATABLE or session RR/SER.
            var tabLockTxScoped = hints.Serializable
                || hints.Repeatable
                || isolation is System.Data.IsolationLevel.RepeatableRead or System.Data.IsolationLevel.Serializable;
            if (tabLockTxScoped)
                this.AcquireTransactionLock(table.TableDataLock, LockMode.Shared);
            else
                this.AcquireStatementLock(table.TableDataLock, LockMode.Shared);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: false, noLockReader: false);
        }

        if (isWrite)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive);
            return new DataLockPlan(rowMode: LockMode.Exclusive, rowTxScoped: true, skipBlockedRows: false, noLockReader: false);
        }

        // Reader path (no TABLOCK*).
        if (hints.XLock)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive);
            return new DataLockPlan(rowMode: LockMode.Exclusive, rowTxScoped: true, skipBlockedRows: hints.ReadPast, noLockReader: false);
        }
        if (hints.UpdLock)
        {
            this.AcquireTransactionLock(table.TableDataLock, LockMode.IntentExclusive);
            return new DataLockPlan(rowMode: LockMode.Update, rowTxScoped: true, skipBlockedRows: hints.ReadPast, noLockReader: false);
        }
        if (hints.Serializable || isolation == System.Data.IsolationLevel.Serializable)
        {
            // SERIALIZABLE / HOLDLOCK hint: take table-S tx-scoped. Real
            // SQL Server uses key-range locks here; the simulator has no
            // index range structure so we degenerate to table-level for
            // phantom prevention. Conservative — blocks more than real SQL
            // Server but never incorrectly allows a phantom-creating insert.
            this.AcquireTransactionLock(table.TableDataLock, LockMode.Shared);
            return new DataLockPlan(rowMode: null, rowTxScoped: false, skipBlockedRows: hints.ReadPast, noLockReader: false);
        }
        // RC / RR reader.
        this.AcquireStatementLock(table.TableDataLock, LockMode.IntentShared);
        var rowTxScoped = hints.Repeatable || isolation == System.Data.IsolationLevel.RepeatableRead;
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
    /// against the active transaction (tx-scoped — every per-row data
    /// lock in phase 1b is tx-scoped). Bumps the per-tx per-table row-lock
    /// count; if the count crosses
    /// <see cref="SimulatedDbTransaction.RowLockEscalationThreshold"/>,
    /// the row lock is released and a single table-X is acquired in its
    /// place (escalation). Subsequent per-row acquires on the same table
    /// in the same tx short-circuit (the table-X already covers).
    /// </summary>
    public void AcquireRowLockTxScoped(HeapTable table, int pageIndex, int slotIndex, LockMode mode)
    {
        var connection = this.Connection;
        if (connection.CurrentTransaction is { } tx && tx.EscalatedTables.Contains(table))
            return;
        var resource = table.GetOrCreateRowLock(pageIndex, slotIndex);
        connection.Simulation.LockManager.Acquire(resource, mode, connection, connection.LockTimeoutMillis);
        if (connection.CurrentTransaction is { } activeTx)
        {
            activeTx.HeldLocks.Add((resource, mode));
            var counts = activeTx.RowLockCountsByTable;
            _ = counts.TryGetValue(table, out var prev);
            counts[table] = prev + 1;
            if (counts[table] > SimulatedDbTransaction.RowLockEscalationThreshold && !activeTx.EscalatedTables.Contains(table))
                EscalateToTableX(table, activeTx);
        }
        else
        {
            this.StatementSchemaLocks.Add((resource, mode));
        }
    }

    /// <summary>
    /// Promotes a transaction's accumulated per-row tx-scoped locks on
    /// <paramref name="table"/> into a single table-X. Releases every
    /// row-lock entry the transaction holds on this table; acquires
    /// table-X tx-scoped; marks the table as escalated so future row-lock
    /// requests short-circuit. Matches real SQL Server's escalation
    /// behavior at ~5000 row-locks-per-table.
    /// </summary>
    private void EscalateToTableX(HeapTable table, SimulatedDbTransaction tx)
    {
        var connection = this.Connection;
        var manager = connection.Simulation.LockManager;
        // Acquire table-X first; if this throws (timeout / deadlock), the
        // partial state stays consistent — escalation didn't happen, the
        // already-held row locks remain.
        manager.Acquire(table.TableDataLock, LockMode.Exclusive, connection, connection.LockTimeoutMillis);
        tx.HeldLocks.Add((table.TableDataLock, LockMode.Exclusive));
        _ = tx.EscalatedTables.Add(table);
        // Now release every row-lock entry on this table.
        for (var i = tx.HeldLocks.Count - 1; i >= 0; i--)
        {
            var (resource, mode) = tx.HeldLocks[i];
            // Skip the table-X we just appended; release row-level locks
            // owned by this table. Row locks live in table.RowLocks dict;
            // identify by reference.
            if (ReferenceEquals(resource, table.TableDataLock))
                continue;
            if (!IsRowLockOf(table, resource))
                continue;
            manager.Release(resource, mode, connection);
            tx.HeldLocks.RemoveAt(i);
        }
        tx.RowLockCountsByTable[table] = 0;
    }

    private static bool IsRowLockOf(HeapTable table, LockResource resource)
    {
        foreach (var kv in table.RowLocks)
        {
            if (ReferenceEquals(kv.Value, resource))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Wraps <paramref name="table"/>'s row enumeration with per-row
    /// conflict checks driven by <paramref name="plan"/>. Each yielded
    /// row's RID flows through <see cref="TouchRowForRead"/>; READPAST-
    /// blocked rows are silently skipped. The wrapper captures
    /// <paramref name="batch"/> at FROM-source parse time and reuses it
    /// when the SELECT plan iterates (same batch instance — by-reference
    /// capture is safe even for correlated subqueries, which iterate the
    /// same source repeatedly).
    /// </summary>
    public static IEnumerable<byte[]> WrapWithRowConflictChecks(HeapTable table, BatchContext batch, DataLockPlan plan)
    {
        var snapshotXid = batch.ResolveSnapshotXidForRead(table);
        foreach (var (pageIndex, slotIndex, bytes) in table.Heap.EnumerateRowsWithAddress())
        {
            if (snapshotXid is { } sx)
            {
                var resolved = Storage.VersionStore.ResolveVisibleVersion(table, (pageIndex, slotIndex), bytes, sx, batch.Connection.CurrentTransaction);
                if (resolved is null)
                    continue;
                yield return resolved;
                continue;
            }
            if (batch.TouchRowForRead(table, pageIndex, slotIndex, plan))
                yield return bytes;
        }
        // Second pass: under snapshot, surface tombstoned slots whose chain
        // carries a still-visible historical version. The live-heap pass
        // skips these (heap iteration skips tombstoned slots), but the
        // SI / RCSI snapshot may pre-date the delete, in which case the
        // pre-delete payload is the visible version. Walks the per-table
        // version dict directly; live slots are filtered out by the
        // tombstone check so we don't double-yield.
        if (snapshotXid is { } sx2)
        {
            foreach (var kv in table.RowVersions)
            {
                if (!table.Heap.IsSlotTombstoned(kv.Key.PageIndex, kv.Key.SlotIndex))
                    continue;
                var resolved = Storage.VersionStore.ResolveTombstonedSlotForSnapshot(kv.Value, sx2, batch.Connection.CurrentTransaction);
                if (resolved is null)
                    continue;
                yield return resolved;
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
        if (Simulation.SystemHeapTables.ContainsValue(table))
            return null;

        var connection = this.Connection;
        var isolation = connection.SessionIsolationLevel;
        var database = this.CurrentDatabase;

        if (isolation == System.Data.IsolationLevel.Snapshot)
        {
            if (connection.CurrentTransaction is { } tx)
            {
                if (tx.SnapshotXid is null)
                {
                    tx.SnapshotXid = database.CurrentTransactionCommitId;
                    database.ActiveSnapshotTxs[tx] = 0;
                }
                return tx.SnapshotXid;
            }
            // Auto-commit SI session — each read gets the latest commit
            // stamp (effectively current state). Rare path, mostly a
            // grammar-level use.
            return database.CurrentTransactionCommitId;
        }

        if (isolation == System.Data.IsolationLevel.ReadCommitted && database.ReadCommittedSnapshot)
        {
            this.RcsiStatementSnapshotXid ??= database.CurrentTransactionCommitId;
            return this.RcsiStatementSnapshotXid;
        }

        return null;
    }

    /// <summary>
    /// Reader-side row-touch helper called per row during enumeration.
    /// Based on <paramref name="plan"/>:
    /// <list type="bullet">
    /// <item><see cref="DataLockPlan.NoLockReader"/> — no probe, no acquire (dirty read).</item>
    /// <item><see cref="DataLockPlan.RowMode"/> non-null — acquire that mode tx-scoped.</item>
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
            this.AcquireRowLockTxScoped(table, pageIndex, slotIndex, mode);
            return true;
        }
        // RC probe path: only act if a conflict exists.
        var connection = this.Connection;
        if (connection.CurrentTransaction is { } tx && tx.EscalatedTables.Contains(table))
            return true;
        var resource = table.GetOrCreateRowLock(pageIndex, slotIndex);
        var manager = connection.Simulation.LockManager;
        if (!manager.HasIncompatibleHolderOtherThan(resource, LockMode.Shared, connection))
            return true;
        if (plan.SkipBlockedRows)
            return false;
        // Wait for the row's writers to drain. Transient acquire-release
        // matches real SQL Server's RC pattern: "block until committed,
        // then release immediately."
        manager.Acquire(resource, LockMode.Shared, connection, connection.LockTimeoutMillis);
        manager.Release(resource, LockMode.Shared, connection);
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
            manager.Release(resource, mode, connection);
        }
        this.StatementSchemaLocks.Clear();
    }

    public bool IsSkipping =>
        this.SkipModeFlag
        || this.LoopControl != LoopControl.None
        || this.ReturnSignaled
        || this.ErrorSignaled;

    /// <summary>The connection executing this batch.</summary>
    public SimulatedDbConnection Connection => this.Parser.Connection;

    /// <summary>The database this batch is executing against.</summary>
    public Database CurrentDatabase => this.Parser.CurrentDatabase;

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
    public readonly Dictionary<string, HeapTable> TableVariables = new(StringComparer.InvariantCultureIgnoreCase);

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
        this.Variables = new Dictionary<string, VariableSlot>(StringComparer.InvariantCultureIgnoreCase);
        this.TriggerFrame = triggerFrame;
        this.Parser = new ParserContext(triggerBodyCommand, this);
    }

    private static Dictionary<string, VariableSlot> SeedVariables(SimulatedDbCommand command)
    {
        var dict = new Dictionary<string, VariableSlot>(StringComparer.InvariantCultureIgnoreCase);
        foreach (DbParameter parameter in command.Parameters)
        {
            // Skip structured / table-valued parameters here — they land in
            // TableVariables via SeedTableVariablesFromStructuredParameters.
            // Detection: a DataTable or IDataReader value combined with a
            // non-empty TypeName extension property. SqlDbType.Structured
            // itself isn't directly observable on DbParameter (the simulator
            // doesn't expose a SqlDbType property), so the value-type +
            // TypeName combination is the signal.
            if (IsTableValuedParameterValue(parameter))
                continue;
            var name = parameter.ParameterName;
            if (name.StartsWith('@'))
                name = name[1..];
            var dbType = SqlType.GetByDbType(parameter.DbType);
            var seed = parameter.Value is null or DBNull
                ? SqlValue.Null(dbType)
                : dbType.ConvertParameter(parameter.Value);
            // For decimal / numeric parameters, ConvertParameter widens the
            // declared type to fit the value's natural scale (e.g. caller sends
            // 123.45m without an explicit scale → widens to decimal(28, 2)).
            // Track the post-widen type so VariableReference.GetSqlType returns
            // the right schema and downstream readers don't truncate.
            var declaredType = seed.IsNull ? dbType : seed.Type;
            dict[name] = new VariableSlot(declaredType, declaredMaxLength: null, seed, parameter);
        }
        return dict;
    }

    /// <summary>
    /// True when <paramref name="parameter"/> looks like a table-valued
    /// parameter: a <see cref="System.Data.DataTable"/> or
    /// <see cref="System.Data.IDataReader"/>-typed <see cref="DbParameter.Value"/>.
    /// <c>TypeName</c> presence is required for a valid TVP but isn't
    /// gated here — a missing <c>TypeName</c> raises an explicit
    /// <see cref="ArgumentException"/> at materialization (mirroring
    /// <c>Microsoft.Data.SqlClient</c>'s client-side check).
    /// </summary>
    private static bool IsTableValuedParameterValue(DbParameter parameter) =>
        parameter.Value is System.Data.DataTable or System.Data.IDataReader;

    /// <summary>
    /// Materializes each TVP-shaped <see cref="DbParameter"/> into the
    /// batch's <see cref="TableVariables"/> dict. Reads the
    /// <c>TypeName</c> extension property off the parameter to look up the
    /// registered <see cref="TableType"/>;
    /// resolves the value source (<see cref="System.Data.DataTable"/> or
    /// <see cref="System.Data.IDataReader"/>) into rows via the type's
    /// <see cref="TableType.Clone"/> + per-row INSERT path. The clone is
    /// flagged as a TVP (<see cref="HeapTable.IsTableValuedParameter"/>)
    /// so any downstream DML attempt against the bound name raises Msg 10700.
    /// </summary>
    private static void SeedTableVariablesFromStructuredParameters(BatchContext batch, SimulatedDbCommand command)
    {
        foreach (DbParameter parameter in command.Parameters)
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
            MaterializeTvpRows(parameter.Value!, tableType, clone);
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

    private static void MaterializeTvpRows(object source, TableType tableType, HeapTable destination)
    {
        switch (source)
        {
            case System.Data.DataTable dt:
                if (dt.Columns.Count != tableType.Columns.Length)
                    throw SimulatedSqlException.TableValuedParameterColumnCountMismatch(dt.Columns.Count, tableType.Columns.Length);
                foreach (System.Data.DataRow row in dt.Rows)
                    InsertOneRowFromValueArray(row.ItemArray, tableType, destination);
                break;
            case System.Data.IDataReader reader:
                if (reader.FieldCount != tableType.Columns.Length)
                    throw SimulatedSqlException.TableValuedParameterColumnCountMismatch(reader.FieldCount, tableType.Columns.Length);
                var buffer = new object?[reader.FieldCount];
                while (reader.Read())
                {
                    for (var i = 0; i < buffer.Length; i++)
                        buffer[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    InsertOneRowFromValueArray(buffer, tableType, destination);
                }
                break;
        }
    }

    private static void InsertOneRowFromValueArray(object?[] sourceValues, TableType tableType, HeapTable destination)
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
                fullValues[i] = Simulation.CoerceForIdentity(column.Identity.GenerateNext(), column);
                continue;
            }
            fullValues[i] = sourceValues[i] is null or DBNull
                ? SqlValue.Null(column.Type)
                : column.Type.ConvertParameter(sourceValues[i]!);
        }
        // Encode via the destination's StoredColumns (skipping non-stored
        // computed-without-PERSISTED slots, since the encoder works against
        // stored cells only).
        var storedValues = new SqlValue[destination.StoredColumns.Length];
        var s = 0;
        for (var i = 0; i < tableType.Columns.Length; i++)
        {
            if (tableType.Columns[i].IsStored)
                storedValues[s++] = fullValues[i];
        }
        _ = destination.Heap.Insert(Storage.RowEncoder.EncodeRow(destination.Schema, storedValues));
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
        : throw SimulatedSqlException.MustDeclareScalarVariable(name);

    /// <summary>
    /// Recognizes a local temp-table name (<c>#foo</c>, including bare
    /// <c>#</c>). Global temps (<c>##foo</c>) aren't modeled and return
    /// false. The rule: leading <c>#</c>, second char is not <c>#</c> (so
    /// <c>##</c>-prefixed names fall out as not-local).
    /// </summary>
    public static bool IsLocalTempName(string name) =>
        name.Length >= 1 && name[0] == '#' && (name.Length == 1 || name[1] != '#');

    /// <summary>
    /// Recognizes a table-variable name (<c>@foo</c>). Used by DML / FROM
    /// resolution to route 1-part references with a leading <c>@</c> to
    /// <see cref="TableVariables"/> instead of the regular schema/temp lookup.
    /// </summary>
    public static bool IsTableVariableName(string name) =>
        name.Length >= 2 && name[0] == '@';

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
            if (Collation.Default.Equals(name.Leaf, "inserted") && triggerFrame.Inserted is { } ins)
            {
                table = ins;
                return true;
            }
            if (Collation.Default.Equals(name.Leaf, "deleted") && triggerFrame.Deleted is { } del)
            {
                table = del;
                return true;
            }
        }

        if (IsLocalTempName(name.Leaf))
        {
            // Temp tables are session-local; no other connection can DROP
            // them, so Sch-S acquisition is unnecessary (and would be a
            // self-conflict-free no-op anyway).
            return this.Connection.TempTables.TryGetValue(name.Leaf, out table);
        }

        // Table-variable routing: @-prefixed leaves are per-batch, 1-part-only
        // (probe-confirmed: dbo.@t raises Msg 102 at parse). Dict key is the
        // @-stripped name (matches Variables dict convention). Table variables
        // are per-batch — no concurrency, no Sch-S.
        if (IsTableVariableName(name.Leaf))
        {
            if (name.Count > 1)
            {
                table = null;
                return false;
            }
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
            this.AcquireStatementLock(table.SchemaLock, LockMode.SchemaStability);
            return true;
        }

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
    /// DROP / TRUNCATE / SELECT-INTO target lives in. Returns false when the
    /// schema (the segment to the left of the leaf) doesn't exist, when a
    /// 3-part name's db segment doesn't match <see cref="CurrentDatabase"/>,
    /// or when the name is 4-part (linked-server names aren't modeled — the
    /// simulator returns false rather than silently ignoring the server
    /// segment). A 1-part name resolves to <see cref="Database.DefaultSchemaName"/>
    /// (always present, so this branch never returns false).
    /// </summary>
    public bool TryResolveSchema(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema)
    {
        if (name.Count >= 4)
        {
            schema = null;
            return false;
        }
        if (name.Count == 3 && !Collation.Default.Equals(name[0], this.CurrentDatabase.Name))
        {
            schema = null;
            return false;
        }
        var schemaName = name.Count >= 2 ? name.ImmediateQualifier! : Database.DefaultSchemaName;
        return this.CurrentDatabase.Schemas.TryGetValue(schemaName, out schema);
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
        if (name.Count < 2
            || !this.TryResolveSchema(name, out var schema)
            || !schema.Functions.TryGetValue(name.Leaf, out function))
        {
            return false;
        }
        this.AcquireStatementLock(function.SchemaLock, LockMode.SchemaStability);
        return true;
    }

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
        if (!this.TryResolveSchema(name, out var schema)
            || !schema.Views.TryGetValue(name.Leaf, out view))
        {
            return false;
        }
        this.AcquireStatementLock(view.SchemaLock, LockMode.SchemaStability);
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
    /// Resolves <paramref name="name"/> to a <see cref="CatalogView"/> in
    /// either the <c>sys</c> or <c>INFORMATION_SCHEMA</c> schema. Returns
    /// true for 2-part names <c>{sys|INFORMATION_SCHEMA}.&lt;view&gt;</c>
    /// (case-insensitive) whose leaf matches a registered view, or for
    /// 3-part names whose db segment matches <see cref="CurrentDatabase"/>.
    /// Used by the FROM parser to route catalog-view references to virtual
    /// projections before falling through to the regular
    /// <see cref="TryResolveTable"/> path. The registry is keyed by the
    /// fully-qualified name (e.g. <c>"sys.tables"</c>,
    /// <c>"INFORMATION_SCHEMA.COLUMNS"</c>) so one resolver can serve both
    /// schemas without per-namespace dispatch.
    /// </summary>
    public bool TryResolveCatalogView(MultiPartName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogView? view)
    {
        view = null;
        if (name.Count is not (2 or 3))
            return false;
        if (name.Count == 3 && !Collation.Default.Equals(name[0], this.CurrentDatabase.Name))
            return false;
        var key = $"{name.ImmediateQualifier}.{name.Leaf}";
        return Simulation.CatalogViews.TryGetValue(key, out view);
    }

    /// <summary>
    /// Parses an object name (1–4 dotted segments) at the current token,
    /// leaving the cursor on the <em>last</em> consumed name segment (matching
    /// the standard parser-context contract that every parser leaves Token on
    /// its last consumed token). Empty segments (<c>tempdb..#foo</c>, db with
    /// omitted schema) are tolerated — they're silently compressed out, so
    /// <c>tempdb..#foo</c> returns a 2-part name (<c>tempdb</c> + <c>#foo</c>).
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
                return name;
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
                name = name.WithAddedPart(afterEmpty.Value);
                continue;
            }
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
