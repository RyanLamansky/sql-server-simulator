namespace SqlServerSimulator.Parser;

/// <summary>
/// The parsed half of a DML statement, re-executable against any batch: what
/// an <c>INSERT … VALUES</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c>
/// parse resolved (the target, its columns, the expressions, the
/// <c>OUTPUT</c> projection), handed to the execution half that every run of
/// the statement shares. A cached plan is one object executed by many
/// commands, possibly at once, so under the shared-plan contract nothing an
/// execution varies may live on it.
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
/// Armed on the batch around a top-level DML statement's parse when the plan
/// cache may keep its plan: the parse's split point fills it with the plan and
/// what else the parse did that a replay has to do again.
/// </summary>
internal sealed class DmlPlanRecording
{
    /// <summary>
    /// <see cref="ParserContext.QueriesParsed"/> as the statement began, so its
    /// split point can tell whether it parsed a nested query.
    /// </summary>
    public int QueriesParsedAtStart;

    /// <summary>The plan, set when the statement's shape is one a replay reproduces.</summary>
    public DmlStatementPlan? Plan;

    /// <summary>Set when the statement's parse settled on a shape a replay can't reproduce.</summary>
    public bool Declined;

    /// <summary>Where the parse left the cursor: the token after the statement.</summary>
    public ParserContext.Checkpoint End;

    /// <summary>The locks the parse took, in order.</summary>
    public ReplayedLock[] Locks = [];

    /// <summary>The statement-frame state the parse settled; see <see cref="DmlPlanEntry"/>.</summary>
    public bool OpensTransaction, CallsUserFunction, ReadsPermanentObject, ReadsTemporaryObject, ReadsTableVariable;

    /// <summary>The client-bound <c>OUTPUT</c> shape the parse noted, if any.</summary>
    public (Storage.SqlType[] Schema, string[] ColumnNames)? ClientOutputShape;
}

/// <summary>
/// One cached DML statement: its <see cref="Plan"/>, where its text ends, and
/// what its parse did besides building the plan — the locks it took and the
/// statement-frame flags it set — which a replay repeats in that order before
/// running the plan. An entry with no plan records that the statement parsed
/// to a shape a replay can't reproduce, so later runs under the same schema
/// parse it without recording again.
/// </summary>
internal sealed class DmlPlanEntry(DmlPlanRecording recording, long schemaVersion, Database database)
{
    public readonly DmlStatementPlan? Plan = recording.Plan;
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

    /// <summary>
    /// Replays the statement against <paramref name="context"/>'s batch, whose
    /// cursor already sits at <see cref="End"/>: the parse's locks as this
    /// session, then the flags its parse set on the statement frame, then the
    /// execution half. The locks come first because a parse takes them before
    /// it reaches anything that sets a flag — a lock wait that ends the
    /// statement ends it with the frame the parse had at that point.
    /// </summary>
    public SimulatedStatementOutcome Replay(ParserContext context)
    {
        var batch = context.Batch;
        batch.TakeReplayedLocks(this.Locks);
        var statement = batch.CurrentStatement;
        if (this.opensTransaction)
            statement.MarkOpensTransaction();
        statement.CallsUserFunction |= this.callsUserFunction;
        statement.ReadsPermanentObject |= this.readsPermanentObject;
        statement.ReadsTemporaryObject |= this.readsTemporaryObject;
        statement.ReadsTableVariable |= this.readsTableVariable;
        if (this.clientOutputShape is { } shape)
            statement.ClientOutputShape = shape;
        return this.Plan!.Run(context);
    }
}

/// <summary>
/// The DML plans cached for one command text, keyed by where each statement
/// starts in the text's memoized token sequence. Copy-on-write: a lookup reads
/// whichever dictionary is published without locking, and a recording
/// publishes a new one.
/// </summary>
internal sealed class DmlPlanSet
{
    private volatile Dictionary<int, DmlPlanEntry> entries = [];
    private readonly Lock publishing = new();

    /// <summary>The plan cached for the statement starting at token ordinal <paramref name="start"/>, if any.</summary>
    public DmlPlanEntry? Find(int start) => this.entries.TryGetValue(start, out var entry) ? entry : null;

    /// <summary>Files <paramref name="entry"/> for the statement starting at <paramref name="start"/>, replacing any stale one.</summary>
    public void Publish(int start, DmlPlanEntry entry)
    {
        lock (this.publishing)
            this.entries = new Dictionary<int, DmlPlanEntry>(this.entries) { [start] = entry };
    }
}
