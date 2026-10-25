using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The statement plans cached per command text or module body, beside
    /// <see cref="planCache"/>'s SELECT sequences and keyed the same way. A set
    /// holds one plan per <c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c>,
    /// <c>DELETE</c> or <c>MERGE</c> in the text, so a batch mixing them with
    /// statements that have no plan (<c>SET NOCOUNT ON</c>, a <c>DECLARE</c>,
    /// an <c>IF</c>) still skips parsing the ones that do. A module body's
    /// statements are filed under <see cref="ModuleBodyKey"/>. Bounded and
    /// cleared as the plan cache is.
    /// </summary>
    private readonly BoundedCache<PlanCacheKey, StatementPlanSet> statementPlanSets = new(PlanCacheCapacity);

    /// <summary>Test-observable: DML statements that ran from a cached plan.</summary>
    internal long DmlPlanHits;

    /// <summary>Test-observable: DML statements that recorded a plan as they parsed.</summary>
    internal long DmlPlanRecordings;

    /// <summary>Test-observable: <c>SELECT</c> statements that ran from a cached statement plan.</summary>
    internal long SelectPlanHits;

    /// <summary>Test-observable: <c>SELECT</c> statements that recorded a statement plan as they parsed.</summary>
    internal long SelectPlanRecordings;

    /// <summary>Test-observable: statements whose run replayed the compile walk's parse of them rather than parsing again.</summary>
    internal long WalkPlanReplays;

    /// <summary>
    /// Runs one DML statement, from its cached plan when one is
    /// current, otherwise by parsing it — recording the plan the parse builds
    /// when the statement qualifies. A replay moves the cursor to where the
    /// statement's parse would have left it, then takes the parse's locks and
    /// runs the plan's execution half, all inside <see cref="RunMutation"/>
    /// exactly where the parse would have run, so the statement's atomicity,
    /// its errors and their order are the parsed statement's. A statement under
    /// a <c>WITH</c> prefix files its plan where the prefix starts, the
    /// prefix's parse part of its recording (see <see cref="PrepareCtePrefixPlan"/>).
    /// </summary>
    private SimulatedStatementOutcome RunDmlStatement(ParserContext context, Func<ParserContext, SimulatedStatementOutcome> parse)
    {
        var batch = context.Batch;
        // A prefix the cache holds is what admits the statement it leads.
        var cachesPlan = MayCacheStatementPlan(context);
        var prefix = TakeCtePrefixPlan(context);
        if (!cachesPlan)
        {
            DisarmCtePrefixPlan(context, prefix);
            return RunMutation(context, parse);
        }

        var statement = context.SaveCheckpoint();
        var start = prefix?.Start ?? statement;
        var entry = prefix is null ? this.CurrentStatementPlan(context, start) : prefix.Entry;
        if (entry is { IsDeclined: true })
            return RunMutation(context, parse);
        if (entry is { Plan: not null } && context.CanJumpTo(entry.End))
        {
            _ = Interlocked.Increment(ref this.DmlPlanHits);
            return RunMutation(context, replaying =>
            {
                replaying.JumpTo(entry.End);
                if (entry.Replay(replaying) is { } replayed)
                    return replayed;
                // The schema locks the replay took waited out a definition
                // change, so the statement parses as it would have.
                if (prefix is not null)
                    ParseSkippedCtePrefix(replaying, prefix);
                else
                    replaying.RestoreCheckpoint(start);
                return parse(replaying);
            });
        }
        if (prefix is { Recording: null })
        {
            // A plan the prefix found that can't replay here parses as a
            // statement the cache keeps nothing of.
            ParseSkippedCtePrefix(context, prefix);
            return RunMutation(context, parse);
        }

        // The statement-scoped flag is judged for this statement alone; what
        // earlier statements set still stands for the batch afterwards.
        bool enteredSessionScoped;
        StatementPlanRecording recording;
        long schemaVersion;
        Database database;
        if (prefix is { Recording: { } armed })
        {
            (recording, enteredSessionScoped, schemaVersion, database) = (armed, prefix.EnteredSessionScoped, prefix.SchemaVersion, prefix.Database);
        }
        else
        {
            enteredSessionScoped = batch.HasSessionScopedReference;
            batch.HasSessionScopedReference = false;
            recording = BeginStatementRecording(batch);
            schemaVersion = Volatile.Read(ref this.SchemaVersion);
            database = context.CurrentDatabase;
        }
        batch.DmlPlanRecording = recording;
        recording.Statement = statement;
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
            batch.HasSessionScopedReference |= enteredSessionScoped;
            // A statement whose execution half failed still parsed completely,
            // so its plan is as good as a successful one's.
            this.EndStatementRecording(batch, recording, start, schemaVersion, database);
        }
    }

    /// <summary>
    /// At a statement's <c>WITH</c> prefix, the cursor on the keyword: when
    /// the plan cache may keep the statement, finds the plan it filed where the
    /// prefix starts — and then jumps the cursor past the unparsed prefix to the
    /// statement's keyword, for the dispatch loop to route it to its arm, which
    /// replays the plan — or, with none recorded yet, arms the statement's
    /// recording ahead of the prefix, so the locks, checks and flags its common
    /// table expressions' parse settles are recorded with the statement's own.
    /// Either way the arm takes what this left on
    /// <see cref="ParserContext.CtePrefixPlan"/>. True when the prefix is to be
    /// parsed.
    /// </summary>
    private bool PrepareCtePrefixPlan(ParserContext context)
    {
        if (!MayCacheStatementPlan(context))
            return true;
        var start = context.SaveCheckpoint();
        var entry = this.CurrentStatementPlan(context, start);
        if (entry is { IsDeclined: true })
            return true;
        if (entry is not null)
        {
            if (!context.CanJumpTo(entry.Statement) || !context.CanJumpTo(entry.End))
                return true;
            context.JumpTo(entry.Statement);
            context.CtePrefixPlan = CtePrefixPlan.Replaying(start, entry);
            return false;
        }
        var batch = context.Batch;
        var enteredSessionScoped = batch.HasSessionScopedReference;
        batch.HasSessionScopedReference = false;
        context.CtePrefixPlan = CtePrefixPlan.Recorded(start, BeginStatementRecording(batch), enteredSessionScoped, Volatile.Read(ref this.SchemaVersion), context.CurrentDatabase);
        return true;
    }

    /// <summary>Takes what <see cref="PrepareCtePrefixPlan"/> left for the statement's arm, if anything.</summary>
    private static CtePrefixPlan? TakeCtePrefixPlan(ParserContext context)
    {
        var prefix = context.CtePrefixPlan;
        context.CtePrefixPlan = null;
        return prefix;
    }

    /// <summary>
    /// Lets go of a recording <see cref="PrepareCtePrefixPlan"/> armed that no
    /// statement plan will take — a prefix that failed to parse, one tagging
    /// the statement's change-tracking context as it parses, a statement the
    /// cache keeps nothing of after all — keeping what its parse noted of
    /// session-scoped references.
    /// </summary>
    private static void DisarmCtePrefixPlan(ParserContext context, CtePrefixPlan? prefix)
    {
        if (prefix?.Recording is null)
            return;
        var batch = context.Batch;
        batch.ReplayLockLog = null;
        batch.HasSessionScopedReference |= prefix.EnteredSessionScoped;
#if DEBUG
        EndPrincipalWatch(prefix.Recording);
#endif
    }

    /// <summary>
    /// Parses the <c>WITH</c> prefix a replay jumped past, for a statement that
    /// parses after all, leaving the cursor on the statement's keyword.
    /// </summary>
    private static void ParseSkippedCtePrefix(ParserContext context, CtePrefixPlan prefix)
    {
        context.RestoreCheckpoint(prefix.Start);
        ParseCteBindings(context);
        context.CtePrefixLeadsSelectStatement = context.Token is ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' };
    }

    /// <summary>
    /// The plan cached for the statement starting at <paramref name="start"/>,
    /// when it was recorded under the current schema version, in the database
    /// the batch is in and — for a trigger's statement over <c>inserted</c> /
    /// <c>deleted</c> — over the pair the firing holds; null when there is none
    /// to replay or decline by, and for a statement compiling as it runs this time (see
    /// <see cref="StatementsCompiledOnRun"/>), whose compile sends what a
    /// replay wouldn't.
    /// </summary>
    private StatementPlanEntry? CurrentStatementPlan(ParserContext context, ParserContext.Checkpoint start) =>
        context.Batch.InlinedCalls is not null ? null
            : this.Current(context, context.Batch.WalkPlans?.GetValueOrDefault(start.MemoPosition))
                ?? this.Current(context, context.Batch.StatementPlans?.Find(start.MemoPosition));

    /// <summary>
    /// <paramref name="entry"/> when it was recorded under the current schema
    /// version, in the database the batch is in and over the pseudo-tables its
    /// firing holds (see <see cref="CurrentStatementPlan"/>); otherwise null.
    /// </summary>
    private StatementPlanEntry? Current(ParserContext context, StatementPlanEntry? entry) =>
        entry is not null
            && entry.SchemaVersion == Volatile.Read(ref this.SchemaVersion)
            && ReferenceEquals(entry.Database, context.CurrentDatabase)
            && (entry.PseudoTables is null || ReferenceEquals(entry.PseudoTables, context.Batch.TriggerFrame?.PseudoTables))
                ? entry
                : null;

    /// <summary>
    /// Arms the lock log, and in a Debug build the principal-read watch, for a
    /// statement's parse whose plan may be cached, answering the recording its
    /// parse fills.
    /// </summary>
    private static StatementPlanRecording BeginStatementRecording(BatchContext batch)
    {
        var recording = new StatementPlanRecording(batch.Connection.PendingMessages.Count);
        batch.ReplayLockLog = [];
        batch.StatementPseudoTables = null;
        batch.StatementBindsFiringPseudoTables = false;
#if DEBUG
        recording.PrincipalWatch = PlanCacheCaptureAudit.WatchPrincipalReads(batch.Connection.Security);
#endif
        return recording;
    }

    /// <summary>
    /// Files what a statement's parse recorded — a plan, or that its shape
    /// has none — unless the schema changed while it ran, or the statement
    /// bound pseudo-tables its firing materialized for itself, whose shape
    /// says nothing about the kept pair's statements.
    /// </summary>
    private void EndStatementRecording(BatchContext batch, StatementPlanRecording recording, ParserContext.Checkpoint start, long schemaVersion, Database database)
    {
#if DEBUG
        EndPrincipalWatch(recording);
        if (recording.Plan is not null || recording.Query is not null)
            PlanCacheCaptureAudit.VerifyPrincipalIndependent(recording.PrincipalRead, batch.PlanCacheKey!.Value.CommandText);
#endif
        if (batch.StatementBindsFiringPseudoTables)
            return;
        if ((recording.Plan is not null || recording.Query is not null || recording.Declined) && Volatile.Read(ref this.SchemaVersion) == schemaVersion)
            this.PublishStatementPlan(batch, start.MemoPosition, new StatementPlanEntry(recording, schemaVersion, database));
    }

    /// <summary>
    /// Whether the statement at the cursor may run from, or record, a cached
    /// plan: a statement of a batch or module body the plan cache keys — under
    /// a <c>WITH</c> prefix only when the prefix's parse is recorded with it
    /// (see <see cref="PrepareCtePrefixPlan"/>) — under the settings its key was
    /// taken with — a statement earlier in the batch may have changed one —
    /// and none of the settings the cache stays out of. A statement inside a
    /// block, an <c>IF</c>, a <c>WHILE</c> or a <c>TRY</c> qualifies as one at
    /// the top does: it parses the same text the same way, and a branch not
    /// taken is skipped rather than parsed for a plan.
    /// Any principal qualifies: a plan holds nothing that depends on who parsed
    /// it, since the permission checks and the masks run in the execution half
    /// or replay from the recording as the executing principal (see
    /// <see cref="PermissionEnforcement.CheckWhileParsing(BatchContext, CompiledPermissionCheck)"/>), and the one
    /// principal-dependent binding — the default schema an unqualified name
    /// searches — is a key component.
    /// </summary>
    private static bool MayCacheStatementPlan(ParserContext context)
    {
        var batch = context.Batch;
        if (batch.IsSkipping
            || batch.BindErrors is not null
            || batch.CreateTimeBinding
            || batch.DependencySink is not null
            || batch.FunctionBodyShape is not null
            || (context.CteBindings is not null && context.CtePrefixPlan is null)
            || !context.HoldsTokenSequence)
        {
            return false;
        }

        var connection = batch.Connection;
        if (batch.StatementPlanModule is { } module)
        {
            batch.StatementPlanModule = null;
            connection.Simulation.AttachStatementPlans(batch, ModuleBodyKey(connection, module, context.Command.CommandText));
        }
        return batch.PlanCacheKey is { } key
            && connection.SessionIsolationLevel == System.Data.IsolationLevel.ReadCommitted
            && !connection.ImplicitTransactions && !connection.NoExec && !connection.ParseOnly && !connection.FmtOnly
            && !connection.StatisticsIo && !connection.StatisticsTime && !connection.NoBrowseTable
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
        recording.Declined = true;
        if (admitted && recording.TakeParse(context, locks, batch.CurrentStatement))
            recording.Plan = plan;
    }

#if DEBUG
    /// <summary>Ends <paramref name="recording"/>'s principal-read watch, if it still runs.</summary>
    private static void EndPrincipalWatch(StatementPlanRecording recording)
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
    /// text's set on its first plan; a set the cache refuses serves the batch's
    /// own execution alone, so its later statements don't ask again.
    /// </summary>
    private void PublishStatementPlan(BatchContext batch, int start, StatementPlanEntry entry)
    {
#if DEBUG
        if ((entry.Plan ?? (object?)entry.Query) is { } plan)
            PlanCacheCaptureAudit.Verify([plan], batch.PlanCacheKey!.Value.CommandText);
#endif
        if (batch.StatementPlans is not { } set)
        {
            var key = batch.PlanCacheKey!.Value;
            if (!this.statementPlanSets.TryGetValue(key, out set))
            {
                set = new StatementPlanSet();
                if (!this.statementPlanSets.TryAdd(key, set) && this.statementPlanSets.TryGetValue(key, out var raced))
                    set = raced;
            }
            batch.StatementPlans = set;
        }
        set.Publish(start, entry);
        if (entry.Plan is not null)
            _ = Interlocked.Increment(ref this.DmlPlanRecordings);
        else if (entry.Query is not null)
            _ = Interlocked.Increment(ref this.SelectPlanRecordings);
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
