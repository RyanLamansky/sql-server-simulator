using System.Collections.Concurrent;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The DML statement plans cached per command text, beside
    /// <see cref="planCache"/>'s SELECT sequences and keyed the same way. A set
    /// holds one plan per top-level <c>INSERT</c>, <c>UPDATE</c>,
    /// <c>DELETE</c> or <c>MERGE</c> in the text, so a batch mixing them with statements that
    /// have no plan (<c>SET NOCOUNT ON</c>, a <c>DECLARE</c>) still skips
    /// parsing the ones that do. Capped and cleared as the plan cache is.
    /// </summary>
    private readonly ConcurrentDictionary<PlanCacheKey, DmlPlanSet> dmlPlanSets = new();

    /// <summary>How many keys <see cref="dmlPlanSets"/> holds, counted as <see cref="planCacheCount"/> is.</summary>
    private int dmlPlanSetCount;

    /// <summary>Test-observable: DML statements that ran from a cached plan.</summary>
    internal long DmlPlanHits;

    /// <summary>Test-observable: DML statements that recorded a plan as they parsed.</summary>
    internal long DmlPlanRecordings;

    /// <summary>
    /// Runs one top-level DML statement, from its cached plan when one is
    /// current, otherwise by parsing it — recording the plan the parse builds
    /// when the statement qualifies. A replay moves the cursor to where the
    /// statement's parse would have left it, then takes the parse's locks and
    /// runs the plan's execution half, all inside <see cref="RunMutation"/>
    /// exactly where the parse would have run, so the statement's atomicity,
    /// its errors and their order are the parsed statement's.
    /// </summary>
    private SimulatedStatementOutcome RunDmlStatement(ParserContext context, Func<ParserContext, SimulatedStatementOutcome> parse)
    {
        var batch = context.Batch;
        if (!MayCacheDmlPlan(context))
            return RunMutation(context, parse);

        var start = context.SaveCheckpoint();
        var entry = batch.DmlPlans?.Find(start.MemoPosition);
        if (entry is not null && (entry.SchemaVersion != Volatile.Read(ref this.SchemaVersion) || !ReferenceEquals(entry.Database, context.CurrentDatabase)))
            entry = null;
        if (entry is { Plan: null })
            return RunMutation(context, parse);
        if (entry is not null && context.CanJumpTo(entry.End))
        {
            _ = Interlocked.Increment(ref this.DmlPlanHits);
            return RunMutation(context, replaying =>
            {
                replaying.JumpTo(entry.End);
                if (entry.Replay(replaying) is { } replayed)
                    return replayed;
                // The schema locks the replay took waited out a definition
                // change, so the statement parses as it would have.
                replaying.RestoreCheckpoint(start);
                return parse(replaying);
            });
        }

        // The statement-scoped flag is judged for this statement alone; what
        // earlier statements set still stands for the batch afterwards.
        var enteredSessionScoped = batch.HasSessionScopedReference;
        batch.HasSessionScopedReference = false;
        var recording = batch.DmlPlanRecording = new DmlPlanRecording();
        batch.ReplayLockLog = [];
#if DEBUG
        recording.PrincipalWatch = PlanCacheCaptureAudit.WatchPrincipalReads(batch.Connection.Security);
#endif
        var schemaVersion = Volatile.Read(ref this.SchemaVersion);
        var database = context.CurrentDatabase;
        try
        {
            var outcome = RunMutation(context, parse);
            // A statement that ran without reaching an admitting split point
            // is a shape with no replay; so noted, its later runs don't record.
            recording.Declined |= recording.Plan is null;
            if (recording.Plan is not null)
            {
                // The execution half reads no tokens, so the cursor is still
                // where the split point recorded it; a mismatch means one did,
                // and a replay would skip what it read.
                var position = context.SaveCheckpoint();
                if (position.MemoPosition != recording.End.MemoPosition || !ReferenceEquals(position.Token, recording.End.Token))
                {
#if DEBUG
                    throw new InvalidOperationException("A DML statement's execution half moved the parser past its recorded end.");
#else
                    recording.Plan = null;
#endif
                }
            }
            return outcome;
        }
        finally
        {
            batch.DmlPlanRecording = null;
            batch.ReplayLockLog = null;
#if DEBUG
            EndPrincipalWatch(recording);
            if (recording.Plan is not null)
                PlanCacheCaptureAudit.VerifyPrincipalIndependent(recording.PrincipalRead, batch.PlanCacheKey!.Value.CommandText);
#endif
            batch.HasSessionScopedReference |= enteredSessionScoped;
            // A statement whose execution half failed still parsed completely,
            // so its plan is as good as a successful one's.
            if ((recording.Plan is not null || recording.Declined) && Volatile.Read(ref this.SchemaVersion) == schemaVersion)
                this.PublishDmlPlan(batch, start.MemoPosition, new DmlPlanEntry(recording, schemaVersion, database));
        }
    }

    /// <summary>
    /// Whether the statement at the cursor may run from, or record, a cached
    /// plan: a top-level statement of a batch the plan cache keys — not under a
    /// <c>WITH</c> prefix, whose common table expressions parse ahead of the
    /// statement's recording — under the
    /// settings its key was taken with — a statement earlier in the batch may
    /// have changed one — and none of the settings the cache stays out of.
    /// Any principal qualifies: a plan holds nothing that depends on who parsed
    /// it, since the permission checks and the masks run in the execution half
    /// or replay from the recording as the executing principal (see
    /// <see cref="PermissionEnforcement.CheckWhileParsing(BatchContext, CompiledPermissionCheck)"/>), and the one
    /// principal-dependent binding — the default schema an unqualified name
    /// searches — is a key component.
    /// </summary>
    private static bool MayCacheDmlPlan(ParserContext context)
    {
        var batch = context.Batch;
        if (batch.PlanCacheKey is not { } key
            || batch.BlockDepth != 0
            || batch.IsSkipping
            || batch.BindErrors is not null
            || batch.CreateTimeBinding
            || batch.DependencySink is not null
            || batch.FunctionBodyShape is not null
            || batch.UdfFrame is not null
            || batch.ProcFrame is not null
            || batch.TriggerFrame is not null
            || context.CteBindings is not null
            || !context.HoldsTokenSequence)
        {
            return false;
        }

        var connection = batch.Connection;
        return connection.SessionIsolationLevel == System.Data.IsolationLevel.ReadCommitted
            && !connection.ImplicitTransactions && !connection.NoExec && !connection.ParseOnly && !connection.FmtOnly
            && !connection.StatisticsIo && !connection.StatisticsTime && !connection.NoBrowseTable
            && connection.InsertExecTargetTypes is null
            && key.QuotedIdentifiers == context.QuotedIdentifiers
            && key.DateFormat == connection.DateFormat
            && key.AnsiNulls == connection.AnsiNulls
            && key.ConcatNullYieldsNull == connection.ConcatNullYieldsNull
            && connection.CurrentDatabase is { } database
            && string.Equals(key.DatabaseName, database.Name, StringComparison.Ordinal)
            // An EXECUTE AS earlier in the batch changes the default schema an
            // unqualified name searches.
            && string.Equals(key.DefaultSchemaName, batch.DefaultSchemaName, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DML statement's split point: its parse is complete and
    /// <paramref name="plan"/> holds everything its execution half needs. When
    /// the statement is recording (see <see cref="RunDmlStatement"/>) and
    /// <paramref name="admitted"/> says its shape is one a replay reproduces,
    /// the recording takes the plan, the cursor, the locks taken so far and
    /// the statement-frame flags, and otherwise marks the statement declined;
    /// either way the lock log disarms here, so the execution half's row locks
    /// — which it takes again every run — stay out.
    /// </summary>
    private static void NoteDmlPlan(ParserContext context, DmlStatementPlan plan, bool admitted)
    {
        var batch = context.Batch;
        if (batch.DmlPlanRecording is not { } recording)
            return;
        batch.DmlPlanRecording = null;
        var locks = batch.ReplayLockLog;
        batch.ReplayLockLog = null;
#if DEBUG
        EndPrincipalWatch(recording);
#endif
        var statement = batch.CurrentStatement;
        recording.Declined = true;
        if (!admitted
            || locks is null
            || batch.HasSessionScopedReference
            || statement.RemoteWrite is not null
            || statement.TransactionMark is not null
            || statement.BindsDeferredSource)
        {
            return;
        }

        recording.Declined = false;
        recording.Plan = plan;
        recording.End = context.SaveCheckpoint();
        recording.Locks = [.. locks];
        recording.OpensTransaction = statement.OpensTransaction;
        recording.CallsUserFunction = statement.CallsUserFunction;
        recording.ReadsPermanentObject = statement.ReadsPermanentObject;
        recording.ReadsTemporaryObject = statement.ReadsTemporaryObject;
        recording.ReadsTableVariable = statement.ReadsTableVariable;
        recording.ClientOutputShape = statement.ClientOutputShape;
    }

#if DEBUG
    /// <summary>Ends <paramref name="recording"/>'s principal-read watch, if it still runs.</summary>
    private static void EndPrincipalWatch(DmlPlanRecording recording)
    {
        if (recording.PrincipalWatch is { } principalWatch)
        {
            recording.PrincipalRead = PlanCacheCaptureAudit.EndPrincipalWatch(principalWatch);
            recording.PrincipalWatch = null;
        }
    }

#endif
    /// <summary>
    /// Files a recorded statement plan under the batch's key, creating the
    /// text's set on its first plan while the cap allows.
    /// </summary>
    private void PublishDmlPlan(BatchContext batch, int start, DmlPlanEntry entry)
    {
#if DEBUG
        if (entry.Plan is { } plan)
            PlanCacheCaptureAudit.Verify([plan], batch.PlanCacheKey!.Value.CommandText);
#endif
        if (batch.DmlPlans is not { } set)
        {
            var key = batch.PlanCacheKey!.Value;
            if (!this.dmlPlanSets.TryGetValue(key, out set))
            {
                if (Volatile.Read(ref this.dmlPlanSetCount) >= PlanCacheCapacity)
                    return;
                set = new DmlPlanSet();
                if (this.dmlPlanSets.TryAdd(key, set))
                    _ = Interlocked.Increment(ref this.dmlPlanSetCount);
                else
                    set = this.dmlPlanSets[key];
            }
            batch.DmlPlans = set;
        }
        set.Publish(start, entry);
        if (entry.Plan is not null)
            _ = Interlocked.Increment(ref this.DmlPlanRecordings);
    }

    /// <summary>
    /// Whether a table's write-time behavior can change without a schema
    /// change in a way a cached DML plan wouldn't see: another database's
    /// table, whose write resolves the session's identity in that database —
    /// a path the replay differential doesn't cover; a trigger that could be
    /// enabled or disabled under it while its <c>OUTPUT</c> returns rows to the
    /// client (Msg 334 is settled while parsing), or a column or index that
    /// makes the write check the session's SET options.
    /// </summary>
    private static bool BlocksDmlPlan(BatchContext batch, Storage.HeapTable table, bool clientOutput) =>
        !ReferenceEquals(batch.DatabaseFor(table), batch.CurrentDatabase)
        || table.IsTableVariable
        || table.IsTableValuedParameter
        || RequiresCorrectSetOptions(table)
        || (clientOutput && TableHasAnyTrigger(batch, table));

    /// <summary>Whether any DML trigger, enabled or not, is attached to <paramref name="table"/>.</summary>
    private static bool TableHasAnyTrigger(BatchContext batch, Storage.HeapTable table) =>
        TriggersAttachedTo(batch, table).Length != 0;
}
