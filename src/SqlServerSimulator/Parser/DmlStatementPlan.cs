namespace SqlServerSimulator.Parser;

/// <summary>
/// The parsed half of a DML statement, re-executable against any batch: what
/// an <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c> parse
/// resolved (the target, its columns, the expressions, a source query or a
/// <c>FROM</c> clause's sources, the <c>OUTPUT</c> projection), handed to the
/// execution half that every run of the statement shares. A cached plan is
/// one object executed by many commands, possibly at once, so under the
/// shared-plan contract nothing an execution varies may live on it.
/// </summary>
internal abstract class DmlStatementPlan
{
    /// <summary>
    /// Runs the statement's execution half against <paramref name="context"/>'s
    /// batch, from where its parse stopped — the permission and lock checks
    /// real makes as the statement starts, then its rows.
    /// </summary>
    public abstract SimulatedStatementOutcome Run(ParserContext context);
}

/// <summary>
/// Armed on the batch around a statement's parse when the plan cache may keep
/// its plan: a DML statement's split point fills it with the plan, a
/// <c>SELECT</c>'s parse with its <see cref="Selection"/>, and either with what
/// else the parse did that a replay has to do again.
/// </summary>
internal sealed class StatementPlanRecording(int messagesAtStart)
{
    /// <summary>The DML plan, set when the statement's shape is one a replay reproduces.</summary>
    public DmlStatementPlan? Plan;

    /// <summary>The <c>SELECT</c> statement's plan, set when a replay reproduces it.</summary>
    public Selection? Query;

    /// <summary>Set when the statement's parse settled on a shape a replay can't reproduce.</summary>
    public bool Declined;

    /// <summary>
    /// How many informational messages the session had queued as the parse
    /// began: a parse that queues one — a warning real sends as it compiles the
    /// statement — sends nothing on a replay, so it records no plan.
    /// </summary>
    public readonly int MessagesAtStart = messagesAtStart;

    /// <summary>Where the parse left the cursor: the token after the statement.</summary>
    public ParserContext.Checkpoint End;

    /// <summary>The locks the parse took and the permission checks it made, in order.</summary>
    public ReplayedLock[] Locks = [];

    /// <summary>The statement-frame state the parse settled; see <see cref="StatementPlanEntry"/>.</summary>
    public bool OpensTransaction, CallsUserFunction, ReadsPermanentObject, ReadsTemporaryObject, ReadsTableVariable;

    /// <summary>The client-bound <c>OUTPUT</c> shape the parse noted, if any.</summary>
    public (Storage.SqlType[] Schema, string[] ColumnNames)? ClientOutputShape;
#if DEBUG

    /// <summary>The watch over the parse's principal reads, until the split point ends it.</summary>
    public PlanCacheCaptureAudit.PrincipalReadWatch? PrincipalWatch;

    /// <summary>Where the parse first read its principal, if it did (see <see cref="PlanCacheCaptureAudit"/>).</summary>
    public string? PrincipalRead;
#endif

    /// <summary>
    /// Takes what the parse ending at the cursor did besides building its plan
    /// — where it stopped, the locks it took (<paramref name="locks"/>) and the
    /// flags it set on <paramref name="statement"/> — or declines when that
    /// includes something a replay wouldn't repeat. True when taken.
    /// </summary>
    public bool TakeParse(ParserContext context, List<ReplayedLock>? locks, StatementContext statement)
    {
        var batch = context.Batch;
        this.Declined = true;
        if (locks is null
            || batch.HasSessionScopedReference
            || batch.Connection.PendingMessages.Count != this.MessagesAtStart
            || statement.RemoteWrite is not null
            || statement.TransactionMark is not null
            || statement.BindsDeferredSource
            || statement.Recompiles)
        {
            return false;
        }

        this.Declined = false;
        this.End = context.SaveCheckpoint();
        this.Locks = [.. locks];
        this.OpensTransaction = statement.OpensTransaction;
        this.CallsUserFunction = statement.CallsUserFunction;
        this.ReadsPermanentObject = statement.ReadsPermanentObject;
        this.ReadsTemporaryObject = statement.ReadsTemporaryObject;
        this.ReadsTableVariable = statement.ReadsTableVariable;
        this.ClientOutputShape = statement.ClientOutputShape;
        return true;
    }
}

/// <summary>
/// One cached statement: its plan — a DML statement's <see cref="Plan"/> or a
/// <c>SELECT</c>'s <see cref="Query"/> — where its text ends, and what its
/// parse did besides building the plan — the locks it took and the
/// statement-frame flags it set — which a replay repeats in that order before
/// running the plan. An entry with neither plan records that the statement
/// parsed to a shape a replay can't reproduce, so later runs under the same
/// schema parse it without recording again.
/// </summary>
internal sealed class StatementPlanEntry(StatementPlanRecording recording, long schemaVersion, Database database)
{
    public readonly DmlStatementPlan? Plan = recording.Plan;
    public readonly Selection? Query = recording.Query;
    public readonly ParserContext.Checkpoint End = recording.End;
    public readonly ReplayedLock[] Locks = recording.Locks;
    private readonly bool opensTransaction = recording.OpensTransaction, callsUserFunction = recording.CallsUserFunction;
    private readonly bool readsPermanentObject = recording.ReadsPermanentObject, readsTemporaryObject = recording.ReadsTemporaryObject;
    private readonly bool readsTableVariable = recording.ReadsTableVariable;
    private readonly (Storage.SqlType[] Schema, string[] ColumnNames)? clientOutputShape = recording.ClientOutputShape;

    /// <summary>The <see cref="Simulation.SchemaVersion"/> the statement parsed under.</summary>
    public readonly long SchemaVersion = schemaVersion;

    /// <summary>The database the statement parsed in.</summary>
    public readonly Database Database = database;

    /// <summary>Whether the statement recorded no plan, its shape being one a replay can't reproduce.</summary>
    public bool IsDeclined => this.Plan is null && this.Query is null;

    /// <summary>
    /// Replays the DML statement against <paramref name="context"/>'s batch,
    /// whose cursor already sits at <see cref="End"/>: what its parse did (see
    /// <see cref="ReplayParse"/>), then the execution half.
    /// </summary>
    /// <remarks>
    /// Null, nothing run, when a definition change the locks waited out made
    /// the plan stale: the caller parses the statement instead.
    /// </remarks>
    public SimulatedStatementOutcome? Replay(ParserContext context) =>
        this.ReplayParse(context) ? this.Plan!.Run(context) : null;

    /// <summary>
    /// Repeats what the statement's parse did against <paramref name="context"/>'s
    /// batch: the parse's locks and permission checks as this session, then the
    /// flags its parse set on the statement frame. The locks come first because
    /// a parse takes them before it reaches anything that sets one — a lock
    /// wait that ends the statement ends it with the frame the parse had at
    /// that point. False, with no flag set, when a definition change the locks
    /// waited out made the plan stale.
    /// </summary>
    public bool ReplayParse(ParserContext context)
    {
        var batch = context.Batch;
        if (!batch.TakeReplayedLocks(this.Locks, this.SchemaVersion) || Volatile.Read(ref context.Connection.Simulation.SchemaVersion) != this.SchemaVersion)
            return false;
        var statement = batch.CurrentStatement;
        if (this.opensTransaction)
            statement.MarkOpensTransaction();
        statement.CallsUserFunction |= this.callsUserFunction;
        statement.ReadsPermanentObject |= this.readsPermanentObject;
        statement.ReadsTemporaryObject |= this.readsTemporaryObject;
        statement.ReadsTableVariable |= this.readsTableVariable;
        if (this.clientOutputShape is { } shape)
            statement.ClientOutputShape = shape;
        return true;
    }
}

/// <summary>
/// The statement plans cached for one command text or module body, keyed by
/// where each statement starts in the text's memoized token sequence.
/// Copy-on-write: a lookup reads whichever dictionary is published without
/// locking, and a recording publishes a new one.
/// </summary>
internal sealed class StatementPlanSet
{
    private volatile Dictionary<int, StatementPlanEntry> entries = [];
    private readonly Lock publishing = new();

    /// <summary>The plan cached for the statement starting at token ordinal <paramref name="start"/>, if any.</summary>
    public StatementPlanEntry? Find(int start) => this.entries.TryGetValue(start, out var entry) ? entry : null;

    /// <summary>Files <paramref name="entry"/> for the statement starting at <paramref name="start"/>, replacing any stale one.</summary>
    public void Publish(int start, StatementPlanEntry entry)
    {
        lock (this.publishing)
            this.entries = new Dictionary<int, StatementPlanEntry>(this.entries) { [start] = entry };
    }
}
