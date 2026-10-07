using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// MERGE execution: the permission checks, the match against the target, the actions and their commit.
partial class Simulation
{

    /// <summary>
    /// The execution half of <c>MERGE</c>: the permission checks real makes as
    /// the statement starts, a view target's rows, then the match and the
    /// actions. Reads no tokens.
    /// </summary>
    private static SimulatedStatementOutcome RunMerge(ParserContext context, MergePlan plan)
    {
        var (destinationTable, viewRowsTarget, joinWrite) = (plan.DestinationTable, plan.ViewRowsTarget, plan.JoinWrite);
        RowSecurity.NoteWrite(context.Batch, destinationTable);
        if (!context.Batch.IsSkipping)
            CheckMergePermissions(context.Batch, plan.DestinationName, plan.TriggerTarget, plan.WhenClauses, joinWrite, plan.OnPredicate, plan.TargetAlias);
        if (joinWrite is not null && !context.Batch.IsSkipping)
        {
            // The written table's rows the load holds in U go once the
            // statement has written, its writes holding X.
            try
            {
                LoadJoinViewMergeRows(context.Batch, joinWrite, destinationTable);
                return Execute();
            }
            finally
            {
                foreach (var (page, slot) in joinWrite.HeldRows)
                    context.Batch.ReleaseTargetRow(joinWrite.Table, page, slot, TargetRowHold.Update);
                joinWrite.HeldRows.Clear();
            }
        }
        if (viewRowsTarget is not null && !context.Batch.IsSkipping)
        {
            foreach (var row in ReadViewRows(context.Batch, viewRowsTarget, destinationTable.Columns))
                _ = destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, ProjectStoredValues(destinationTable, row), destinationTable.Heap), undoLog: null);
        }
        return Execute();

        SimulatedStatementOutcome Execute() => ExecuteMerge(
            context, destinationTable, plan.SourceView, plan.TargetAlias, plan.MaterializeSource, plan.SourceAlias, plan.SourceColumnNames, plan.SourceSchema,
            plan.OnPredicate, plan.WhenClauses, DataMasking.Applying(context.Batch, plan.WriteMasks), plan.Output, plan.SerializableHint, viewRowsTarget, joinWrite, plan.Top);
    }

    /// <summary>
    /// Checks MERGE permissions on the target: SELECT (the ON predicate reads
    /// it) plus the write permission of each action kind present (INSERT /
    /// UPDATE / DELETE). Denials surface as Msg 229. A single-table view
    /// target whose owner differs from its base table's breaks the ownership
    /// chain, so the same permissions are then checked on the base table,
    /// after the view's (probed 2026-09-27 against SQL Server 2025). The
    /// source read is not separately checked — a documented gap.
    /// </summary>
    private static void CheckMergePermissions(BatchContext batch, MultiPartName destinationName, SchemaObject destination, List<WhenClause> whenClauses, JoinViewMergePlan? joinWrite = null, BooleanExpression? onPredicate = null, string? targetAlias = null)
    {
        var insert = false;
        var update = false;
        var delete = false;
        foreach (var clause in whenClauses)
        {
            switch (clause.Action)
            {
                case MergeActionKind.Insert:
                    insert = true;
                    break;
                case MergeActionKind.Update:
                    update = true;
                    break;
                case MergeActionKind.Delete:
                    delete = true;
                    break;
            }
        }

        var target = PermissionEnforcement.SecurableFor(batch, destinationName, destination);
        if (PermissionEnforcement.Applies(batch, batch.DatabaseFor(target)))
        {
            var denied = PermissionEnforcement.SchemaObjectDenial(batch, "SELECT", target);
            if (insert)
                denied = PermissionEnforcement.Combine(denied, PermissionEnforcement.SchemaObjectDenial(batch, "INSERT", target));
            if (update)
                denied = PermissionEnforcement.Combine(denied, PermissionEnforcement.SchemaObjectDenial(batch, "UPDATE", target));
            if (delete)
                denied = PermissionEnforcement.Combine(denied, PermissionEnforcement.SchemaObjectDenial(batch, "DELETE", target));
            if (denied is not null)
                throw denied;
        }

        if (destination is View && joinWrite is not null)
        {
            CheckJoinViewMergeChain(batch, joinWrite, whenClauses, onPredicate!, targetAlias!, insert, update, delete);
            return;
        }
        if (destination is not View view || view.BaseTable is not { } baseTable)
            return;
        void CheckBase(string permission) => PermissionEnforcement.CheckBrokenChainWrite(batch, permission, view, baseTable);
        CheckBase("SELECT");
        if (insert)
            CheckBase("INSERT");
        if (update)
            CheckBase("UPDATE");
        if (delete)
            CheckBase("DELETE");
    }

    /// <summary>
    /// The broken ownership chain a <c>MERGE</c> through a join view crosses:
    /// SELECT on the columns the statement reads of every other-owner table
    /// and view below it, then each action's write permission on the table it
    /// lands in — the UPDATE path's rules with INSERT and DELETE at object
    /// grain (probed 2026-09-30 against SQL Server 2025: the join view's tables
    /// for all three actions, and a view over another owner's join view
    /// refused at the join view for SELECT then the action's permission).
    /// A statement with several action kinds checks each in INSERT, UPDATE,
    /// DELETE order, which is unprobed.
    /// </summary>
    private static void CheckJoinViewMergeChain(BatchContext batch, JoinViewMergePlan joinWrite, List<WhenClause> whenClauses, BooleanExpression onPredicate, string targetAlias, bool insert, bool update, bool delete)
    {
        var collation = batch.CurrentDatabase.Collation;
        var reads = new List<string>();
        void Read(MultiPartName name)
        {
            if (name.ImmediateQualifier is null || collation.Equals(name.ImmediateQualifier, targetAlias))
                reads.Add(name.Leaf);
        }
        onPredicate.VisitOperandExpressions(operand => operand.VisitColumnReferences(Read));
        var assignments = new List<(int Ordinal, Expression Expr)>();
        foreach (var clause in whenClauses)
        {
            clause.SearchCondition?.VisitOperandExpressions(operand => operand.VisitColumnReferences(Read));
            if (clause.Assignments is not { } assigned)
                continue;
            foreach (var (ordinal, expr) in assigned)
            {
                expr.VisitColumnReferences(Read);
                assignments.Add((ordinal < 0 ? -1 : joinWrite.ViewToBase[ordinal], expr));
            }
        }
        var writes = new List<string>(3);
        if (insert)
            writes.Add("INSERT");
        if (update)
            writes.Add("UPDATE");
        if (delete)
            writes.Add("DELETE");
        CheckJoinViewBrokenChains(batch, joinWrite.Chain, joinWrite.Table, reads, assignments, [.. writes]);
    }

    /// <summary>
    /// Runs the prepared MERGE plan against the live target heap. The
    /// source <see cref="Selection"/> materializes into a list once;
    /// each target row is scanned, its action chosen via the first
    /// applicable WHEN clause, and queued. Unmatched source rows fall
    /// into the <c>WHEN NOT MATCHED [BY TARGET]</c> clause if present.
    /// All queued mutations apply atomically before triggers fire.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteMerge(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        string targetAlias,
        Func<BatchContext, List<SqlValue[]>> materializeSource,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        BooleanExpression onPredicate,
        List<WhenClause> whenClauses,
        MaskingFunction?[]? writeMasks,
        OutputProjection? output,
        bool serializableHint,
        View? viewRowsTarget,
        JoinViewMergePlan? joinWrite,
        Selection.DmlTopLimit? top)
    {
        // Skip mode commits nothing (CommitMerge returns early), so the match
        // walk is pure cost — and running the ON predicate / WHEN actions
        // against live rows can raise a runtime error on behalf of a statement
        // that never ran, which at CREATE-time module binding would refuse a
        // body real accepts. The target, source, ON predicate and WHEN clauses
        // are all parsed by the caller, so binding is already complete here.
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var sourceRows = materializeSource(context.Batch);
        var sourceMatched = new bool[sourceRows.Count];
        var defaultTargetName = sourceView?.Name ?? destinationTable.Name;

        // A non-persisted computed column of the target is evaluated only where
        // the statement reads it for a row — the ON, a WHEN condition, an action
        // — so an expression failing for some row raises when one of them reads
        // it there, and not when nothing does (probed 2026-10-01 against SQL
        // Server 2025). The row decode leaves a failing one NULL, so a read of
        // a NULL evaluates it again, raising if it failed.
        var lazilyComputed = LazilyComputedColumns(destinationTable);
        SqlValue ReadTargetColumn(SqlValue[] targetValues, int ordinal) =>
            lazilyComputed?[ordinal] == true && targetValues[ordinal].IsNull
                ? EvaluateComputedColumn(destinationTable, targetValues, ordinal, context.Batch)
                : targetValues[ordinal];

        // A MERGE through a row-limited or windowed view matches against the
        // rows the view yields, so its row limit or window applies to every
        // action, and the body's derived columns (a ROW_NUMBER()'s rn) read off
        // the view row each base row showed as (probed 2026-09-30 against SQL
        // Server 2025).
        var viewRows = MaterializeRowSelectiveViewRows(context, sourceView, positioned: false);
        var viewRowOf = viewRows is null ? null : new Dictionary<SqlValue[], SqlValue[]>(ReferenceEqualityComparer.Instance);
        bool TryReadDerivedTargetColumn(SqlValue[]? targetValues, string leaf, out SqlValue value)
        {
            value = default;
            if (viewRowOf is null)
                return false;
            for (var i = 0; i < sourceView!.OutputColumns.Length; i++)
            {
                if (!context.Batch.CurrentDatabase.Collation.Equals(sourceView.OutputColumns[i].Name, leaf))
                    continue;
                value = targetValues is not null && viewRowOf.TryGetValue(targetValues, out var viewRow)
                    ? viewRow[i]
                    : SqlValue.Null(sourceView.OutputColumns[i].Type);
                return true;
            }
            return false;
        }

        // Target-side column lookup: user-facing names match view OutputColumns
        // when a view target is in scope, otherwise base table columns. View
        // path translates the matched user-name to a base ordinal via
        // BaseColumnOrdinals so the heap-decoded targetValues array indexes
        // correctly. Derived view columns (BaseColumnOrdinals[i] == -1)
        // can be read at runtime by re-evaluating the projection's
        // expression — but writes to them were rejected at parse time.
        // Resolve target columns by qualifier; null-source means BY-SOURCE branch (everything in source resolver returns NULL).
        SqlValue ResolveCombined(SqlValue[]? targetValues, SqlValue[]? sourceValues, MultiPartName name)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, targetAlias)
                || context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, defaultTargetName))
            {
                if (TryLookupTargetColumn(context.Batch.CurrentDatabase.Collation, name.Leaf, destinationTable, sourceView, out var targetOrdinal, out var targetType))
                    return targetValues is null ? SqlValue.Null(targetType) : ReadTargetColumn(targetValues, targetOrdinal);
                if (TryReadDerivedTargetColumn(targetValues, name.Leaf, out var derived))
                    return derived;
            }
            if (context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceValues is null ? SqlValue.Null(sourceSchema[i]) : sourceValues[i];
                }
            }
            if (name.Count == 1)
            {
                if (TryLookupTargetColumn(context.Batch.CurrentDatabase.Collation, name.Leaf, destinationTable, sourceView, out var targetOrdinal, out var targetType))
                    return targetValues is null ? SqlValue.Null(targetType) : ReadTargetColumn(targetValues, targetOrdinal);
                if (TryReadDerivedTargetColumn(targetValues, name.Leaf, out var derived))
                    return derived;
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceValues is null ? SqlValue.Null(sourceSchema[i]) : sourceValues[i];
                }
            }
            throw RowLocator.IsLocatorName(name)
                ? new NotSupportedException("TEXTPTR in a MERGE isn't modeled: its target and source rows don't carry the address a text pointer names.")
                : SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

        Selection.SettleSerializableMergeFence(
            destinationTable, targetAlias, onPredicate, whenClauses.Any(c => c.Kind == WhenClauseKind.NotMatchedBySource),
            sourceRows, sourceValues => name => ResolveCombined(null, sourceValues, name), serializableHint, context.Batch);

        var pendingInserts = new List<(SqlValue[] NewValues, SqlValue[]? SourceValues)>();
        var pendingUpdates = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)>();
        var pendingDeletes = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)>();

        var nmbtClause = whenClauses.FirstOrDefault(c => c.Kind == WhenClauseKind.NotMatchedByTarget);
        // INSTEAD OF INSERT fires against the view when applicable; otherwise
        // the action targets the base table directly.
        var insteadOfInsertTarget = (SchemaObject?)viewRowsTarget ?? (SchemaObject?)sourceView ?? destinationTable;

        // OUTPUT lists the actions in source-row order, a NOT MATCHED BY
        // SOURCE delete after them all — the order real's usual plan, driven
        // from the source side, produces for an upsert (probed 2026-09-26
        // against SQL Server 2025; a plan real sorts for a merge join lists
        // them in key order instead). Each action queued since the last tag
        // takes the key given.
        var outputOrder = output is null ? null : new List<(int Key, MergeActionKind Kind, int Index)>();
        int taggedInserts = 0, taggedUpdates = 0, taggedDeletes = 0;
        void Tag(int key)
        {
            if (outputOrder is null)
                return;
            while (taggedInserts < pendingInserts.Count)
                outputOrder.Add((key, MergeActionKind.Insert, taggedInserts++));
            while (taggedUpdates < pendingUpdates.Count)
                outputOrder.Add((key, MergeActionKind.Update, taggedUpdates++));
            while (taggedDeletes < pendingDeletes.Count)
                outputOrder.Add((key, MergeActionKind.Delete, taggedDeletes++));
        }

        // SET ROWCOUNT and TOP cap the actions a MERGE takes, composing as a
        // minimum: a row every WHEN clause declines consumes nothing, a NOT
        // MATCHED BY SOURCE delete counts like any other action, and nothing
        // past the cap is evaluated, so its multi-match Msg 8672 or conversion
        // error never raises (probed 2026-09-25 and 2026-09-28 against SQL
        // Server 2025). Which actions come first is the plan's to say — a TOP
        // (1) and a TOP (2) over the same rows can pick disjoint ones — so a
        // capped statement takes them in the order OUTPUT lists them, the order
        // real's usual source-driven plan takes them in. TOP (n) PERCENT is a
        // share, rounded up, of every action the statement would take (34.5
        // PERCENT of three takes two).
        var capped = top is not null || context.Connection.RowCountLimit > 0;
        var deferredSteps = capped ? new List<MergeStep>() : null;
        void Step(MergeStep step)
        {
            if (deferredSteps is not null)
                deferredSteps.Add(step);
            else
                RunStep(step);
        }
        void RunStep(MergeStep step)
        {
            if (step.MatchedSources is { } matchedSources)
            {
                ApplyMergeMatched(context, destinationTable, sourceView, whenClauses, writeMasks, step.Page, step.Slot, step.TargetValues!, sourceRows, matchedSources, ResolveCombined, pendingUpdates, pendingDeletes);
            }
            else if (step.TargetValues is { } targetValues)
            {
                if (PickClause(whenClauses, WhenClauseKind.NotMatchedBySource, targetValues, sourceValues: null, context.Batch, ResolveCombined) is { } chosen)
                    ApplyChosenMatchedAction(context, destinationTable, sourceView, chosen, writeMasks, step.Page, step.Slot, targetValues, sourceValues: null, ResolveCombined, pendingUpdates, pendingDeletes);
            }
            else if (NotMatchedByTargetApplies(step.Key))
            {
                // A join view's row is formed only to be carried to its base
                // table, whose own INSERT path validates it.
                ApplyInsert(context, destinationTable, sourceView, nmbtClause!, writeMasks, sourceRows[step.Key], ResolveCombined, pendingInserts, insteadOfInsert: joinWrite is not null || HasInsteadOfTrigger(context.Batch, insteadOfInsertTarget, TriggerActions.Insert));
            }
            Tag(step.Key);
        }
        bool NotMatchedByTargetApplies(int sourceIndex) =>
            nmbtClause!.SearchCondition is not { } cond
            || cond.Run(new RuntimeContext(name => ResolveCombined(null, sourceRows[sourceIndex], name), context.Batch)) == true;
        bool StepQualifies(MergeStep step) => step.MatchedSources is { } matchedSources
            ? PickClause(whenClauses, WhenClauseKind.Matched, step.TargetValues, sourceRows[matchedSources[0]], context.Batch, ResolveCombined) is not null
            : step.TargetValues is not null
            ? PickClause(whenClauses, WhenClauseKind.NotMatchedBySource, step.TargetValues, sourceValues: null, context.Batch, ResolveCombined) is not null
            : NotMatchedByTargetApplies(step.Key);

        // Phase A finds, per matched target row, the source rows it matches, then
        // applies the WHEN MATCHED / WHEN NOT MATCHED BY SOURCE action. When the
        // ON carries a seekable target equality and the target isn't a view
        // (whose column names don't map to the base heap), the inverted path
        // seeks matching targets per source row — turning the match phase into
        // O(source × log target). With no NOT MATCHED BY SOURCE clause it then
        // touches only the matched targets; with one (which must visit every
        // target to find the unmatched ones) it walks the heap once applying the
        // precomputed matches, dropping the inner source loop either way. An
        // unindexed target keeps the heap walk, hashing the source by the ON's
        // equality keys instead (below) so neither shape is O(target × source).
        //
        // An ON settled non-TRUE while compiling — EF Core's multi-row insert
        // writes ON 1=0 — matches no target row, so with no NOT MATCHED BY
        // SOURCE clause to visit the unmatched ones the target isn't read at
        // all and every source row falls to Phase B: real's plan for it reports
        // the target at scan count 0 and lists no work table (probed 2026-10-01
        // against SQL Server 2025), where walking it here cost a decode per
        // target row and an ON per target × source pair.
        var hasNotMatchedBySource = whenClauses.Any(c => c.Kind == WhenClauseKind.NotMatchedBySource);
        var readsTarget = hasNotMatchedBySource
            || !(onPredicate.IsNeverTrue || (ConstantFolding.TryFoldPredicate(onPredicate, context, out var onConstant) && onConstant != true));
        // The heap's generation before the match reads any target row: a row
        // held with the heap unwritten since is the row as it was read.
        var walkGeneration = Volatile.Read(ref destinationTable.Heap.MutationGeneration);
        // Noted before the target is read, as an UPDATE's walk notes it.
        var keysPutBack = Volatile.Read(ref destinationTable.KeysPutBack);
        var targetSeek = readsTarget && sourceView is null
            ? Selection.TryPrepareMergeTargetSeek(destinationTable, targetAlias, onPredicate, context.Batch)
            : null;
        // Real's MERGE lists a work table between its source and its target.
        if (readsTarget)
            context.Connection.StatementIo?.UseWorktable();

        // Real filters by every other ON conjunct before it computes a
        // computed column for one (probed 2026-10-01: `ON p.c = 1 AND s.x = 0`
        // and `ON p.c = 1 AND p.price = 5` raise nothing for a row the other
        // conjunct turns away, while `ON p.c = 1 OR p.id = s.x` does), so a
        // conjunct reading one is tried last.
        var sides = readsTarget
            ? new MergeSides(context.Batch.CurrentDatabase.Collation, destinationTable, sourceView, targetAlias, defaultTargetName, sourceAlias, sourceColumnNames, sourceSchema)
            : null;
        BooleanExpression[]? onConjuncts = null;
        if (sides is not null && lazilyComputed is not null)
        {
            var conjuncts = new List<BooleanExpression>();
            onPredicate.CollectConjuncts(conjuncts);
            onConjuncts = ComputedReadsLast([.. conjuncts], sides, lazilyComputed);
        }
        bool OnMatches(RuntimeContext runtime) => onConjuncts is null ? onPredicate.Run(runtime) == true : MergeResidualMatches(onConjuncts, runtime);

        // A target row another session's write in flight hid from the match —
        // deleted, or rewritten off the seek or the ON — is waited out first
        // when its prior image matches a source row, or whatever it held when
        // NOT MATCHED BY SOURCE visits every target row.
        if (readsTarget && !destinationTable.SupersededKeyImages.IsEmptyLockFree())
            _ = AwaitSupersededTargetRows(context.Batch, destinationTable, (_, prior) => hasNotMatchedBySource || PriorImageMatches(prior));
        bool PriorImageMatches(byte[] prior)
        {
            var priorValues = DecodeFullRow(destinationTable, prior);
            EvaluateComputedColumns(destinationTable, priorValues, context.Batch);
            foreach (var sourceValues in sourceRows)
            {
                if (OnMatches(new RuntimeContext(name => ResolveCombined(priorValues, sourceValues, name), context.Batch)))
                    return true;
            }
            return false;
        }

        if (!readsTarget)
        {
            JoinDiagnostics.Sink?.Add("Merge:NoTargetRead");
        }
        else if (targetSeek is not null)
        {
            JoinDiagnostics.Sink?.Add("Merge:TargetSeek");

            // Match phase: per source row, seek the matching target rows and group
            // them by target address — first-source-wins via source-index order
            // (sources iterate ascending). The seek matched the equality prefix
            // only, so the full ON is re-run per candidate (residual filter — a
            // term like … AND t.active = 1, or a stale cache entry, is dropped).
            var matchedByTarget = new Dictionary<(int Page, int Slot), List<int>>();
            for (var si = 0; si < sourceRows.Count; si++)
            {
                var sourceValues = sourceRows[si];
                foreach (var (page, slot, candidateBytes) in targetSeek(name => ResolveCombined(null, sourceValues, name)))
                {
                    // Matched under the U a writer reads its target with (see
                    // BatchContext.AwaitTargetRow), so a row another session
                    // is writing matches as that write leaves it.
                    var rowBytes = candidateBytes;
                    var hold = context.Batch.AwaitTargetRow(destinationTable, page, slot, ref rowBytes);
                    while (hold != TargetRowHold.Gone)
                    {
                        var candidateValues = DecodeFullRow(destinationTable, rowBytes);
                        EvaluateComputedColumns(destinationTable, candidateValues, context.Batch);
                        if (!RowSecurity.Admits(context.Batch, destinationTable, candidateValues)
                            || !OnMatches(new RuntimeContext(name => ResolveCombined(candidateValues, sourceValues, name), context.Batch)))
                        {
                            context.Batch.ReleaseTargetRow(destinationTable, page, slot, hold);
                            break;
                        }
                        if (!context.Batch.HoldQualifyingTargetRow(destinationTable, page, slot, ref hold, ref rowBytes, walkGeneration))
                            continue;

                        sourceMatched[si] = true;
                        if (!matchedByTarget.TryGetValue((page, slot), out var sources))
                            matchedByTarget[(page, slot)] = sources = [];
                        sources.Add(si);
                        break;
                    }
                }
            }

            if (hasNotMatchedBySource)
            {
                // Complement pass: every target row in heap order — matched rows take
                // their precomputed source list, unmatched rows fall to WHEN NOT
                // MATCHED BY SOURCE. Heap-order interleaving matches the scan path's
                // discovery order, but with no per-target source loop.
                foreach (var (pageIndex, slotIndex, scannedBytes) in ClusteredScan.RowsWithAddress(destinationTable, context.Connection.StatementIo))
                {
                    context.Batch.PollCancellation();
                    // A matched row holds its X already; an unmatched one takes
                    // it for its NOT MATCHED BY SOURCE action, read again when
                    // another session changed it first.
                    var rowBytes = scannedBytes;
                    var matched = matchedByTarget.TryGetValue((pageIndex, slotIndex), out var matchedSources);
                    // A row the filter predicate hides is no target row at all.
                    if (!matched && !RowSecurity.Admits(context.Batch, destinationTable, FullImage(destinationTable, rowBytes, context.Batch)))
                        continue;
                    if (!matched)
                    {
                        var hold = context.Batch.AwaitTargetRow(destinationTable, pageIndex, slotIndex, ref rowBytes);
                        if (hold != TargetRowHold.Gone)
                            _ = context.Batch.HoldQualifyingTargetRow(destinationTable, pageIndex, slotIndex, ref hold, ref rowBytes, walkGeneration);
                        if (hold == TargetRowHold.Gone)
                            continue;
                    }
                    var targetValues = DecodeFullRow(destinationTable, rowBytes);
                    EvaluateComputedColumns(destinationTable, targetValues, context.Batch);
                    Step(matched
                        ? new MergeStep(matchedSources![0], pageIndex, slotIndex, targetValues, matchedSources)
                        : new MergeStep(sourceRows.Count, pageIndex, slotIndex, targetValues, null));
                }
            }
            else
            {
                // No NOT MATCHED BY SOURCE: visit only matched targets, restoring
                // heap order via the (page, slot) sort so the apply order matches
                // the scan path's.
                foreach (var address in matchedByTarget.Keys.OrderBy(a => a.Page).ThenBy(a => a.Slot))
                {
                    var targetValues = DecodeFullRow(destinationTable, destinationTable.Heap.ReadSlotBytes(address.Page, address.Slot)!);
                    EvaluateComputedColumns(destinationTable, targetValues, context.Batch);
                    var matchedSources = matchedByTarget[address];
                    Step(new MergeStep(matchedSources[0], address.Page, address.Slot, targetValues, matchedSources));
                }
            }
        }
        else
        {
            // Phase A: per target row, the source rows it matches. The ON's
            // `target.col = source.col` conjuncts hash the source once and probe
            // per target row — O(target + source) — leaving the conjuncts that
            // aren't such an equality as a residual re-checked per probed pair.
            // With no such conjunct the match stays the target × source scan,
            // running the whole ON per pair.
            var matchPlan = TryPlanMergeMatch(onPredicate, sides!);
            var residual = matchPlan is null ? [] : lazilyComputed is null ? matchPlan.Residual : ComputedReadsLast(matchPlan.Residual, sides!, lazilyComputed);
            JoinDiagnostics.Sink?.Add(matchPlan is null
                ? "Merge:Scan"
                : $"Merge:HashMatch(keys={matchPlan.Keys.Length},residual={matchPlan.Residual.Length})");
            // Built at the first target row that probes it, never ahead of one:
            // an empty target (or one the view hides entirely) evaluates no ON
            // at all on the scan path, so nothing the build could raise on a
            // source row may fire before a target row asks.
            MergeSourceHash? sourceHash = null;

            foreach (var (pageIndex, slotIndex, scannedBytes) in ClusteredScan.RowsWithAddress(destinationTable, context.Connection.StatementIo))
            {
                context.Batch.PollCancellation();
                // Matched under the U a writer reads its target with (see
                // BatchContext.AwaitTargetRow); a row an action may write takes
                // its X, and is matched again when it changed first.
                var rowBytes = scannedBytes;
                var hold = context.Batch.AwaitTargetRow(destinationTable, pageIndex, slotIndex, ref rowBytes);
                while (hold != TargetRowHold.Gone && !MatchTargetRow(pageIndex, slotIndex, ref hold, ref rowBytes))
                    context.Batch.PollCancellation();
            }

            // Matches one target row against the source and queues its step;
            // false when the row changed before it could be held, to match again.
            bool MatchTargetRow(int pageIndex, int slotIndex, ref TargetRowHold hold, ref byte[] rowBytes)
            {
                var targetValues = DecodeFullRow(destinationTable, rowBytes);
                EvaluateComputedColumns(destinationTable, targetValues, context.Batch);

                // A row the filter predicate hides is no target row at all.
                if (!RowSecurity.Admits(context.Batch, destinationTable, targetValues))
                {
                    context.Batch.ReleaseTargetRow(destinationTable, pageIndex, slotIndex, hold);
                    return true;
                }

                // View visibility filter: a base row not visible through the
                // view participates in neither the ON-predicate match nor the
                // BY-SOURCE enumeration. Mirrors UPDATE / DELETE through view
                // semantics.
                if (sourceView?.VisibilityCheck is { } vis && !vis(targetValues, context.Batch))
                {
                    context.Batch.ReleaseTargetRow(destinationTable, pageIndex, slotIndex, hold);
                    return true;
                }
                if (viewRows is not null)
                {
                    if (!viewRows.TryGetValue((pageIndex, slotIndex), out var viewRow))
                    {
                        context.Batch.ReleaseTargetRow(destinationTable, pageIndex, slotIndex, hold);
                        return true;
                    }
                    viewRowOf![targetValues] = viewRow;
                }

                // Find all matching source rows, ascending source index either
                // way — the bucket chain links in build order.
                var matchedSources = new List<int>();
                if (matchPlan is not null && sourceRows.Count > 0)
                {
                    sourceHash ??= new MergeSourceHash(matchPlan.Keys, sourceRows);
                    // A key the target computes is read for every row probed.
                    if (lazilyComputed is not null)
                    {
                        foreach (var key in matchPlan.Keys)
                            targetValues[key.TargetOrdinal] = ReadTargetColumn(targetValues, key.TargetOrdinal);
                    }
                    for (var si = sourceHash.FirstCandidate(targetValues); si >= 0; si = sourceHash.NextCandidate(si))
                    {
                        if (residual.Length > 0
                            && !MergeResidualMatches(residual, new RuntimeContext(name => ResolveCombined(targetValues, sourceRows[si], name), context.Batch)))
                        {
                            continue;
                        }
                        matchedSources.Add(si);
                    }
                }
                else
                {
                    for (var si = 0; si < sourceRows.Count; si++)
                    {
                        if (OnMatches(new RuntimeContext(name => ResolveCombined(targetValues, sourceRows[si], name), context.Batch)))
                            matchedSources.Add(si);
                    }
                }

                // A row no action can reach is let go; any other takes its X
                // before it counts as matched.
                if (matchedSources.Count == 0 && !hasNotMatchedBySource)
                {
                    context.Batch.ReleaseTargetRow(destinationTable, pageIndex, slotIndex, hold);
                    return true;
                }
                if (!context.Batch.HoldQualifyingTargetRow(destinationTable, pageIndex, slotIndex, ref hold, ref rowBytes, walkGeneration))
                    return false;
                foreach (var si in matchedSources)
                    sourceMatched[si] = true;
                Step(matchedSources.Count > 0
                    ? new MergeStep(matchedSources[0], pageIndex, slotIndex, targetValues, matchedSources)
                    : new MergeStep(sourceRows.Count, pageIndex, slotIndex, targetValues, null));
                return true;
            }
        }

        // A target row whose key another session deleted and put back
        // elsewhere during the match was never matched: the statement runs
        // again (BatchContext.TargetKeyReinserted) before a source row it
        // missed falls to NOT MATCHED and inserts a key that stands.
        if (readsTarget)
        {
            if (!destinationTable.SupersededKeyImages.IsEmptyLockFree())
                _ = AwaitSupersededTargetRows(context.Batch, destinationTable, (_, prior) => hasNotMatchedBySource || PriorImageMatches(prior));
            if (Volatile.Read(ref destinationTable.KeysPutBack) != keysPutBack)
            {
                context.Batch.TargetKeyReinserted = true;
                if (context.Batch.TargetWalkMayRunAgain)
                    return new SimulatedNonQuery(0);
            }
        }

        // Phase B: unmatched source rows → WHEN NOT MATCHED BY TARGET.
        if (nmbtClause is not null)
        {
            for (var si = 0; si < sourceRows.Count; si++)
            {
                if (!sourceMatched[si])
                    Step(new MergeStep(si, 0, 0, null, null));
            }
        }

        if (deferredSteps is not null)
        {
            // Stable by key: a NOT MATCHED BY SOURCE delete, keyed past every
            // source row, keeps its heap order.
            var ordered = deferredSteps.OrderBy(step => step.Key).ToList();
            var cap = top is { } limit
                ? Selection.ResolveDmlTopCap(limit, limit.Percent ? ordered.Count(StepQualifies) : int.MaxValue, context.Batch)
                : long.MaxValue;
            if (context.Connection.RowCountLimit is > 0 and var rowCountLimit && rowCountLimit < cap)
                cap = rowCountLimit;
            foreach (var step in ordered)
            {
                if (pendingInserts.Count + pendingUpdates.Count + pendingDeletes.Count >= cap)
                    break;
                RunStep(step);
            }
        }

        // Phase C: commit mutations.
        return CommitMerge(context, destinationTable, sourceView, pendingInserts, pendingUpdates, pendingDeletes, output, outputOrder, whenClauses, viewRowsTarget, joinWrite);
    }

    /// <summary>
    /// Per column of <paramref name="table"/>, whether it is a non-persisted
    /// computed column no key or index holds — the kind a row decode evaluates
    /// without raising (see <see cref="EvaluateComputedColumns"/>); null when
    /// the table has none.
    /// </summary>
    private static bool[]? LazilyComputedColumns(HeapTable table)
    {
        bool[]? lazy = null;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (table.Columns[i] is { Computed: not null, IsPersisted: false } && !IsKeyedColumn(table, i))
                (lazy ??= new bool[table.Columns.Length])[i] = true;
        }
        return lazy;
    }

    /// <summary>
    /// <paramref name="conjuncts"/> with those reading one of the target's
    /// <paramref name="lazilyComputed"/> columns moved after the rest, each
    /// group in written order.
    /// </summary>
    private static BooleanExpression[] ComputedReadsLast(BooleanExpression[] conjuncts, MergeSides sides, bool[] lazilyComputed)
    {
        var readsComputed = new bool[conjuncts.Length];
        var any = false;
        for (var i = 0; i < conjuncts.Length; i++)
        {
            var reads = false;
            conjuncts[i].VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
                reads |= sides.TryClassify(name, out var isTarget, out var ordinal, out _) && isTarget && lazilyComputed[ordinal]));
            any |= readsComputed[i] = reads;
        }
        if (!any)
            return conjuncts;
        var ordered = new List<BooleanExpression>(conjuncts.Length);
        for (var i = 0; i < conjuncts.Length; i++)
        {
            if (!readsComputed[i])
                ordered.Add(conjuncts[i]);
        }
        for (var i = 0; i < conjuncts.Length; i++)
        {
            if (readsComputed[i])
                ordered.Add(conjuncts[i]);
        }
        return [.. ordered];
    }

    /// <summary>
    /// Walks the WHEN clauses of a given kind in declaration order and
    /// returns the first one whose <c>AND</c> search condition is
    /// satisfied (or absent). Returns null when no clause of that kind
    /// applies.
    /// </summary>
    private static WhenClause? PickClause(
        List<WhenClause> clauses,
        WhenClauseKind kind,
        SqlValue[]? targetValues,
        SqlValue[]? sourceValues,
        BatchContext batch,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined)
    {
        foreach (var clause in clauses)
        {
            if (clause.Kind != kind)
                continue;
            if (clause.SearchCondition is { } cond)
            {
                var result = cond.Run(new RuntimeContext(name => resolveCombined(targetValues, sourceValues, name), batch));
                if (result != true)
                    continue;
            }
            return clause;
        }
        return null;
    }

    // Applies the WHEN MATCHED action to one matched target row, given the source
    // rows it matched (in source-index order, first wins). Picks the first
    // applicable MATCHED clause, enforces the multiple-source-match guard
    // (Msg 8672 for UPDATE), and queues the action. Shared by both inverted-seek
    // apply passes (matched-only and the NOT MATCHED BY SOURCE complement scan).
    private static void ApplyMergeMatched(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        List<WhenClause> whenClauses,
        MaskingFunction?[]? writeMasks,
        int pageIndex,
        int slotIndex,
        SqlValue[] targetValues,
        List<SqlValue[]> sourceRows,
        List<int> matchedSources,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes)
    {
        var sourceValues = sourceRows[matchedSources[0]];
        var chosen = PickClause(whenClauses, WhenClauseKind.Matched, targetValues, sourceValues, context.Batch, resolveCombined);
        if (chosen is null)
            return;
        if (chosen.Action == MergeActionKind.Update && matchedSources.Count > 1)
            throw SimulatedSqlException.MergeMultiMatch();

        ApplyChosenMatchedAction(context, destinationTable, sourceView, chosen, writeMasks, pageIndex, slotIndex, targetValues, sourceValues, resolveCombined, pendingUpdates, pendingDeletes);
    }

    /// <summary>The logical column values of a stored row of <paramref name="table"/>, computed columns included.</summary>
    private static SqlValue[] FullImage(HeapTable table, byte[] rowBytes, BatchContext batch)
    {
        var fullValues = DecodeFullRow(table, rowBytes);
        EvaluateComputedColumns(table, fullValues, batch);
        return fullValues;
    }

    private static void ApplyChosenMatchedAction(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        WhenClause clause,
        MaskingFunction?[]? writeMasks,
        int pageIndex,
        int slotIndex,
        SqlValue[] targetValues,
        SqlValue[]? sourceValues,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes)
    {
        if (clause.Action == MergeActionKind.Delete)
        {
            RowSecurity.EnforceBlock(context.Batch, destinationTable, BlockOperation.BeforeDelete, targetValues);
            pendingDeletes.Add((pageIndex, slotIndex, targetValues, sourceValues));
            return;
        }
        RowSecurity.EnforceBlock(context.Batch, destinationTable, BlockOperation.BeforeUpdate, targetValues);
        // UPDATE: compute new row using assignments evaluated against the same pre-update snapshot.
        context.Batch.BumpRowStamp();
        var newValues = new SqlValue[destinationTable.Columns.Length];
        Array.Copy(targetValues, newValues, targetValues.Length);

        foreach (var (ord, expr) in clause.Assignments!)
        {
            var raw = expr.Run(new RuntimeContext(name => resolveCombined(targetValues, sourceValues, name), context.Batch));
            if (writeMasks?[ord] is { } mask)
                raw = DataMasking.ForStorage(mask.Apply(raw, destinationTable.Columns[ord].Type));
            raw = EnforceMaxLength(raw, destinationTable.Columns[ord], destinationTable, context.Connection);
            newValues[ord] = SqlValue.NameVariantBase(raw, CoerceForWrite(raw, destinationTable.Columns[ord], context.Batch), expr.ResultReportsNumeric);
            EnforceRule(destinationTable, newValues, ord, context.Batch);
        }
        WriteAssignedColumnSet(destinationTable, clause.Assignments!, newValues);

        StampUpdatedRow(destinationTable, newValues, context.Batch);
        EnforceNotNull(destinationTable, newValues, "UPDATE");
        EnforceCheckConstraints(destinationTable, newValues, context.Batch, "UPDATE", reportedVerb: "MERGE", deferFunctionChecks: true);
        RowSecurity.EnforceBlock(context.Batch, destinationTable, BlockOperation.AfterUpdate, newValues);

        // WITH CHECK OPTION: post-update row must still satisfy the view's
        // visibility chain. Raised before commit so a violating row leaves
        // the heap unchanged.
        if (sourceView?.CheckOptionCheck is { } co && !co(newValues, context.Batch))
            throw SimulatedSqlException.ViewCheckOptionViolation();

        pendingUpdates.Add((pageIndex, slotIndex, targetValues, newValues, sourceValues));
    }

    private static void ApplyInsert(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        WhenClause clause,
        MaskingFunction?[]? writeMasks,
        SqlValue[] sourceValues,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        bool insteadOfInsert)
    {
        context.Batch.BumpRowStamp();
        var rowValues = new SqlValue[destinationTable.Columns.Length];
        for (var i = 0; i < rowValues.Length; i++)
            rowValues[i] = SqlValue.Null(destinationTable.Columns[i].Type);

        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Connection.IdentityInsertTable is string activeTable
            && context.Batch.CurrentDatabase.Collation.Equals(activeTable, destinationTable.Name);

        var identityListed = false;
        for (var i = 0; i < clause.InsertColumns!.Length; i++)
        {
            if (ReferenceEquals(clause.InsertColumns[i], identityColumn))
            {
                identityListed = true;
                break;
            }
        }
        if (identityColumn is not null)
        {
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        // The row draws its identity value as it reaches the insert, ahead of
        // its defaults and conversions, and never gives it back: a row failing
        // in them uses one up, as an INSERT's does (probed 2026-10-04 against
        // SQL Server 2025). INSTEAD OF INSERT draws none.
        Int128? drawnIdentity = identityColumn is not null && !identityListed && !insteadOfInsert ? GenerateIdentity(identityColumn) : null;

        // Defaults for columns absent from the INSERT branch's list.
        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            var column = destinationTable.Columns[i];
            if (column.Default is null) continue;
            var listed = false;
            for (var j = 0; j < clause.InsertColumns.Length; j++)
            {
                if (ReferenceEquals(clause.InsertColumns[j], column))
                {
                    listed = true;
                    break;
                }
            }
            if (listed) continue;
            var defaultValue = column.Default.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch));
            rowValues[i] = CoerceForInsert(EnforceMaxLength(defaultValue, column, destinationTable, context.Connection), column);
        }

        var sourceRuntime = new RuntimeContext(name => resolveCombined(null, sourceValues, name), context.Batch);
        for (var i = 0; i < clause.InsertColumns.Length; i++)
        {
            var targetColumn = clause.InsertColumns[i];
            var ordinal = -1;
            for (var j = 0; j < destinationTable.Columns.Length; j++)
            {
                if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                {
                    ordinal = j;
                    break;
                }
            }
            var source = clause.InsertValues![i].Run(sourceRuntime);
            if (writeMasks?[ordinal] is { } mask)
                source = DataMasking.ForStorage(mask.Apply(source, targetColumn.Type));
            source = EnforceMaxLength(source, targetColumn, destinationTable, context.Connection);
            var coerced = SqlValue.NameVariantBase(source, CoerceForWrite(source, targetColumn, context.Batch), clause.InsertValues[i].ResultReportsNumeric);
            rowValues[ordinal] = coerced;
            if (targetColumn.IsColumnSet)
            {
                if (Array.Exists(clause.InsertColumns, static column => column.IsSparse))
                    throw SimulatedSqlException.ColumnSetAndSparseColumnWritten();
                Parser.Expressions.ColumnSetValue.Write(destinationTable, targetColumn, rowValues, coerced);
            }

            if (ReferenceEquals(targetColumn, identityColumn))
            {
                if (coerced.IsNull)
                {
                    throw IsConstantNull(clause.InsertValues[i], context.Batch)
                        ? SimulatedSqlException.DefaultOrNullNotAllowedForIdentity()
                        : SimulatedSqlException.CannotInsertNull(targetColumn.Name, QualifyForNullMessage(destinationTable), "UPDATE");
                }
                identityColumn.Identity!.ObserveExplicit(IdentityState.FromSqlValue(coerced));
            }
        }

        if (identityColumn is not null && !identityListed)
        {
            if (insteadOfInsert)
            {
                // INSTEAD OF INSERT: typed default for identity, matching
                // the table-target INSERT path (probe-confirmed).
                rowValues[identityOrdinal] = CoerceForIdentity(0L, identityColumn);
            }
            else
            {
                rowValues[identityOrdinal] = CoerceForIdentity(drawnIdentity!.Value, identityColumn);
            }
        }

        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            if (destinationTable.Columns[i].Type == SqlType.RowVersion)
                rowValues[i] = SqlValue.FromRowVersion(context.Batch.DatabaseFor(destinationTable).AllocateRowVersion());
        }

        if (destinationTable.GraphKind != GraphTableKind.None)
            SettleGraphColumns(destinationTable, rowValues, clause.InsertColumns, context.Batch);
        StampInsertedPeriod(destinationTable, rowValues, context.Batch);
        EvaluateComputedColumns(destinationTable, rowValues, context.Batch);
        if (!insteadOfInsert)
        {
            // A MERGE's insert reports Msg 515 as "UPDATE fails." and Msg 547
            // as the MERGE statement's (probed 2026-10-01 against SQL Server 2025).
            EnforceNotNull(destinationTable, rowValues, "UPDATE");
            EnforceCheckConstraints(destinationTable, rowValues, context.Batch, reportedVerb: "MERGE", deferFunctionChecks: true);
            EnforceEdgeConstraints(destinationTable, rowValues, context, "MERGE");
            RowSecurity.EnforceBlock(context.Batch, destinationTable, BlockOperation.AfterInsert, rowValues);
        }

        // WITH CHECK OPTION on the post-insert row, matching INSERT-through-view.
        // Skipped for INSTEAD OF INSERT because the trigger body's own DML is
        // what actually lands a row; the view's CheckOption only gates direct
        // heap writes through it.
        if (!insteadOfInsert && sourceView?.CheckOptionCheck is { } co && !co(rowValues, context.Batch))
            throw SimulatedSqlException.ViewCheckOptionViolation();

        pendingInserts.Add((rowValues, sourceValues));
    }

    private static SimulatedStatementOutcome CommitMerge(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes,
        OutputProjection? output,
        List<(int Key, MergeActionKind Kind, int Index)>? outputOrder,
        List<WhenClause> whenClauses,
        View? viewRowsTarget,
        JoinViewMergePlan? joinWrite = null,
        List<int>? updatedColumnOrdinals = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);
        if (joinWrite is not null)
            return CommitJoinViewMerge(context, destinationTable, joinWrite, pendingInserts, pendingUpdates, pendingDeletes, output, outputOrder, whenClauses);

        // UPDATE(col) / COLUMNS_UPDATED() report the statement's SET-clause
        // membership rather than what any row actually changed, so the mask
        // is the union of every WHEN MATCHED THEN UPDATE clause's targets
        // whether or not that clause fired. A join view's write hands in the
        // base-table ordinals its SET lists reach.
        if (updatedColumnOrdinals is null)
        {
            updatedColumnOrdinals = [];
            foreach (var clause in whenClauses)
            {
                if (clause.Action != MergeActionKind.Update || clause.Assignments is null)
                    continue;
                foreach (var (ordinal, _) in clause.Assignments)
                {
                    if (!updatedColumnOrdinals.Contains(ordinal))
                        updatedColumnOrdinals.Add(ordinal);
                }
            }
        }

        // Per-action INSTEAD OF detection. INSTEAD OF triggers live on the
        // view when the target is a view, otherwise on the base table.
        // When set, the corresponding pending list bypasses the heap-write +
        // AFTER-trigger path and fires its INSTEAD OF trigger with would-be
        // values. Real SQL Server allows a mixed MERGE where, say, INSERT
        // routes through INSTEAD OF while UPDATE writes to the heap normally
        // — each action is decided independently.
        var insteadOfTarget = (SchemaObject?)viewRowsTarget ?? (SchemaObject?)sourceView ?? destinationTable;
        var insteadOfInsert = pendingInserts.Count > 0 && HasInsteadOfTrigger(context.Batch, insteadOfTarget, TriggerActions.Insert);
        var insteadOfUpdate = pendingUpdates.Count > 0 && HasInsteadOfTrigger(context.Batch, insteadOfTarget, TriggerActions.Update);
        var insteadOfDelete = pendingDeletes.Count > 0 && HasInsteadOfTrigger(context.Batch, insteadOfTarget, TriggerActions.Delete);

        // SNAPSHOT isolation write-conflict, as CommitUpdate and CommitDelete
        // judge it: a row another transaction changed since the snapshot is
        // Msg 3960.
        foreach (var (page, slot, _, _, _) in pendingUpdates)
            VersionStore.CheckSnapshotUpdateConflict(context.Batch, destinationTable, (page, slot));
        foreach (var (page, slot, _, _) in pendingDeletes)
            VersionStore.CheckSnapshotUpdateConflict(context.Batch, destinationTable, (page, slot), delete: true);

        // The guard's second check, for a write another session raced, reads
        // the same list the check below does: the updates first, then the inserts.
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>? pseudoAffected = null;
        UniqueKeyWriteGuard? keyGuard = null;
        // Validate key constraints across only the actions that actually
        // hit the heap. INSTEAD OF action lists bypass key checks since
        // they never reach the heap — the trigger body's own DML lands
        // with its own validation.
        if ((!insteadOfInsert && pendingInserts.Count > 0) || (!insteadOfUpdate && pendingUpdates.Count > 0))
        {
            pseudoAffected = [];
            if (!insteadOfUpdate)
            {
                foreach (var (page, slot, oldValues, newValues, _) in pendingUpdates)
                    pseudoAffected.Add((page, slot, newValues, oldValues));
            }
            if (!insteadOfInsert)
            {
                // For inserts, the "address" of the new row doesn't exist yet;
                // (-1, i) is sentinel — never collides with a real heap address.
                for (var i = 0; i < pendingInserts.Count; i++)
                    pseudoAffected.Add((-1, i, pendingInserts[i].NewValues, FullOld: null));
            }
            if (pseudoAffected.Count > 0)
            {
                keyGuard = BeginUniqueKeyGuard(context.Batch, destinationTable);
                EnforceKeysForUpdate(destinationTable, pseudoAffected, context.Batch);
            }
        }

        var undoLog = destinationTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;

        // Outgoing FK validation on inserts + updates (the post-image rows
        // that are about to land in the heap). Fires before mutation so a
        // violation rolls back cleanly via the statement-atomic exception —
        // except a key referencing the table itself, checked once the rows
        // are written so a row may reference itself or another row of the
        // same MERGE, as an INSERT's may (probed 2026-10-02 against SQL Server
        // 2025).
        var selfReferencing = ReferencesItself(destinationTable);
        List<SqlValue[]>? outgoingRows = null;
        if (destinationTable.OutgoingForeignKeys.Count > 0)
        {
            var newRows = new List<SqlValue[]>(pendingInserts.Count + pendingUpdates.Count);
            if (!insteadOfInsert)
            {
                foreach (var (newValues, _) in pendingInserts)
                    newRows.Add(newValues);
            }
            if (!insteadOfUpdate)
            {
                foreach (var (_, _, _, newValues, _) in pendingUpdates)
                    newRows.Add(newValues);
            }
            if (newRows.Count > 0)
            {
                EnforceOutgoingForeignKeys(destinationTable, newRows, context, "MERGE", selfReferencing: selfReferencing ? false : null);
                outgoingRows = newRows;
            }
        }

        // Apply heap operations only for non-INSTEAD-OF actions.
        var lockableTable = IsLockableTable(destinationTable);

        // A read-only database refuses the write, but only once one is actually
        // due: a MERGE whose actions all decline completes quietly on real.
        if ((!insteadOfDelete && pendingDeletes.Count > 0)
            || (!insteadOfUpdate && pendingUpdates.Count > 0)
            || (!insteadOfInsert && pendingInserts.Count > 0))
        {
            destinationTable.OwningDatabase?.RejectWriteWhenReadOnly();
        }

        // System-versioned: each row the MERGE deletes or rewrites moves to
        // history first, its ROW END the statement's UtcNow, as a lone
        // DELETE or UPDATE would.
        if (destinationTable.SystemVersioning is { } historyTable && destinationTable.PeriodColumns is { } period)
        {
            if (!insteadOfDelete)
            {
                foreach (var (_, _, oldValues, _) in pendingDeletes)
                    WriteHistoryRow(destinationTable, historyTable, period, oldValues, context, undoLog);
            }
            if (!insteadOfUpdate)
            {
                foreach (var (_, _, oldValues, newValues, _) in pendingUpdates)
                    WriteHistoryRow(destinationTable, historyTable, period, oldValues, context, undoLog, newValues);
            }
        }

        if (!insteadOfDelete)
        {
            foreach (var (page, slot, oldValues, _) in pendingDeletes)
                DeleteRowAt(context, destinationTable, page, slot, oldValues, undoLog);
        }
        var tracking = destinationTable.ChangeTracking;
        if (!insteadOfUpdate)
        {
            var keyOrdinals = tracking is null ? [] : TableChangeTracking.KeyOrdinals(destinationTable);
            var trackedColumns = tracking?.UpdatedColumns(destinationTable, keyOrdinals, updatedColumnOrdinals);
            var setsKey = tracking is not null && TableChangeTracking.SetsKey(keyOrdinals, updatedColumnOrdinals);
            List<(SqlValue[] OldKey, SqlValue[] NewKey)>? keyMoves = null;
            var lobColumns = LegacyLobColumnsAmong(destinationTable, updatedColumnOrdinals);
            for (var u = 0; u < pendingUpdates.Count; u++)
            {
                var (page, slot, oldValues, newValues, _) = pendingUpdates[u];
                if (lobColumns is not null)
                    NoteRootedLobNulls(destinationTable, lobColumns, page, slot, oldValues, newValues);
                tracking?.RecordUpdate(context.Batch, destinationTable, keyOrdinals, oldValues, newValues, trackedColumns, setsKey, ref keyMoves);
                var storedNew = ProjectStoredValues(destinationTable, newValues);
                var rewritten = RowEncoder.EncodeRow(destinationTable.StoredColumns, storedNew, destinationTable.Heap);
                if (lockableTable)
                {
                    context.Batch.AcquireRowLockTxScoped(destinationTable, page, slot, LockMode.Exclusive, RowLockPurpose.UpdatePreImage);
                    context.Batch.NoteSupersededRow(destinationTable, page, slot);
                    context.Batch.ProbeKeyLocksForUpdate(destinationTable, page, slot, rewritten);
                    CaptureMergeVersion(context.Batch, destinationTable, page, slot, VersionWriteKind.Update);
                }
                UpdateCheckedRow(context.Batch, destinationTable, pseudoAffected!, u, rewritten, storedNew, undoLog, ReclaimSuperseded(destinationTable, context), keyGuard);
                ClusteredScan.NoteKeyAssignment(destinationTable, updatedColumnOrdinals, (page, slot), undoLog);
                EnforceLandedRowChecks(destinationTable, newValues, context.Batch, "MERGE");
            }
            tracking?.RecordKeyMoves(context.Batch, destinationTable, keyMoves);
            destinationTable.NoteColumnsUpdated(updatedColumnOrdinals, pendingUpdates.Count);
        }
        if (!insteadOfInsert)
        {
            var firstInsert = insteadOfUpdate ? 0 : pendingUpdates.Count;
            for (var n = 0; n < pendingInserts.Count; n++)
            {
                var newValues = pendingInserts[n].NewValues;
                tracking?.RecordRow(context.Batch, destinationTable, newValues, ChangeTrackingOperation.Insert);
                var storedNew = ProjectStoredValues(destinationTable, newValues);
                var image = RowEncoder.EncodeRow(destinationTable.StoredColumns, storedNew, destinationTable.Heap);
                var guard = keyGuard;
                while (InsertRow(context.Batch, destinationTable, image, undoLog, guard: guard, storedValues: storedNew).PageIndex < 0)
                    guard = RecheckAffectedRow(context.Batch, destinationTable, pseudoAffected!, firstInsert + n);
                EnforceLandedRowChecks(destinationTable, newValues, context.Batch, "MERGE");
                context.Connection.StatementIo?.CountWrite(destinationTable);
            }
        }

        if (selfReferencing && outgoingRows is not null)
            EnforceOutgoingForeignKeys(destinationTable, outgoingRows, context, "MERGE", selfReferencing: true);

        // The written rows judged against any unique-indexed view over the
        // table, as an INSERT's and an UPDATE's are (Msg 2601).
        if ((!insteadOfUpdate && pendingUpdates.Count > 0) || (!insteadOfInsert && pendingInserts.Count > 0))
            context.Batch.Connection.Simulation.EnforceIndexedViews(destinationTable, context.Batch);

        // Incoming-FK cascade for MERGE's DELETE/UPDATE actions on the
        // destination. INSTEAD OF paths bypass (the trigger handles its own
        // DML). A deleted graph node's edge constraints are judged as a
        // DELETE's: real's Msg 547 names the DELETE statement even under a
        // MERGE (probed 2026-10-04 against SQL Server 2025).
        if (!insteadOfDelete && pendingDeletes.Count > 0 && (destinationTable.IncomingForeignKeys.Count > 0 || destinationTable.GraphKind == GraphTableKind.Node))
        {
            var oldRows = new List<SqlValue[]>(pendingDeletes.Count);
            foreach (var (_, _, oldValues, _) in pendingDeletes)
                oldRows.Add(oldValues);
            EnforceIncomingForeignKeysOnDelete(destinationTable, oldRows, context, "MERGE", depth: 0);
            if (destinationTable.GraphKind == GraphTableKind.Node)
                EnforceEdgeConstraintsOnNodeDelete(destinationTable, oldRows, context);
        }
        if (!insteadOfUpdate && pendingUpdates.Count > 0 && destinationTable.IncomingForeignKeys.Count > 0)
        {
            var pairs = new List<(SqlValue[] OldFull, SqlValue[] NewFull)>(pendingUpdates.Count);
            foreach (var (_, _, oldValues, newValues, _) in pendingUpdates)
                pairs.Add((oldValues, newValues));
            EnforceIncomingFkOnUpdate(destinationTable, pairs, context, depth: 0, verb: "MERGE");
        }

        // Identity counter: only advances when the inserts actually hit the
        // heap. INSTEAD OF INSERT doesn't allocate identity, so no update
        // to LastIdentity is needed.
        if (!insteadOfInsert && destinationTable.IdentityOrdinal >= 0 && pendingInserts.Count > 0)
        {
            var lastId = pendingInserts[^1].NewValues[destinationTable.IdentityOrdinal];
            context.Connection.RecordInsertIdentity(lastId.IsNull ? null : IdentityState.FromSqlValue(lastId));
        }

        // Build OUTPUT result, in the order the match phase keyed the actions.
        var outputRows = output is null ? null : ProjectMergeOutput(context.Batch, output, outputOrder!, destinationTable.Columns, pendingInserts, pendingUpdates, pendingDeletes);

        // Fire triggers in INSERT → UPDATE → DELETE order (probe-confirmed).
        // For each action, route to INSTEAD OF if attached, else AFTER (if
        // attached). The branch-presence checks short-circuit when there's
        // no trigger of either timing for the action.
        // INSTEAD OF triggers see view-shaped INSERTED / DELETED columns
        // when the target is a view; AFTER triggers continue to fire against
        // the base table with base-shaped values (AFTER triggers on views
        // aren't a thing in SQL Server).
        var pseudoColumns = sourceView?.OutputColumns ?? destinationTable.Columns;

        var totalAffected = pendingInserts.Count + pendingUpdates.Count + pendingDeletes.Count;
        if (pendingInserts.Count > 0)
        {
            var insertedRows = new List<SqlValue[]>(pendingInserts.Count);
            foreach (var (newValues, _) in pendingInserts)
                insertedRows.Add(newValues);
            if (insteadOfInsert)
            {
                var insertedViewRows = sourceView is null
                    ? insertedRows
                    : insertedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                context.Connection.LastStatementRowCount = pendingInserts.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, insteadOfTarget, TriggerActions.Insert,
                    pseudoColumns, insertedViewRows, deletedRows: null,
                    affectedRowCount: pendingInserts.Count);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Insert))
            {
                context.Connection.LastStatementRowCount = pendingInserts.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Insert,
                    insertedRows: insertedRows, deletedRows: null,
                    affectedRowCount: pendingInserts.Count);
            }
        }
        if (pendingUpdates.Count > 0)
        {
            var insertedRows = new List<SqlValue[]>(pendingUpdates.Count);
            var deletedRows = new List<SqlValue[]>(pendingUpdates.Count);
            foreach (var (_, _, oldValues, newValues, _) in pendingUpdates)
            {
                insertedRows.Add(newValues);
                deletedRows.Add(oldValues);
            }
            if (insteadOfUpdate)
            {
                var insertedViewRows = sourceView is null
                    ? insertedRows
                    : insertedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                var deletedViewRows = sourceView is null
                    ? deletedRows
                    : deletedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                context.Connection.LastStatementRowCount = pendingUpdates.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, insteadOfTarget, TriggerActions.Update,
                    pseudoColumns, insertedViewRows, deletedViewRows,
                    affectedRowCount: pendingUpdates.Count, updatedColumnOrdinals);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Update))
            {
                context.Connection.LastStatementRowCount = pendingUpdates.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Update,
                    insertedRows: insertedRows, deletedRows: deletedRows,
                    affectedRowCount: pendingUpdates.Count, updatedColumnOrdinals);
            }
        }
        if (pendingDeletes.Count > 0)
        {
            var deletedRows = new List<SqlValue[]>(pendingDeletes.Count);
            foreach (var (_, _, oldValues, _) in pendingDeletes)
                deletedRows.Add(oldValues);
            if (insteadOfDelete)
            {
                var deletedViewRows = sourceView is null
                    ? deletedRows
                    : deletedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                context.Connection.LastStatementRowCount = pendingDeletes.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, insteadOfTarget, TriggerActions.Delete,
                    pseudoColumns, insertedRows: null, deletedRows: deletedViewRows,
                    affectedRowCount: pendingDeletes.Count);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Delete))
            {
                context.Connection.LastStatementRowCount = pendingDeletes.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Delete,
                    insertedRows: null, deletedRows: deletedRows,
                    affectedRowCount: pendingDeletes.Count);
            }
        }

        context.Connection.LastStatementRowCount = totalAffected;
        // An INTO target consumed the rows, so the statement is a non-query —
        // the same suppression INSERT / UPDATE / DELETE apply.
        return output is { HasTarget: false }
            ? new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!, totalAffected) { ColumnNullability = output.Nullability }
            : new SimulatedNonQuery(totalAffected);
    }

    /// <summary>
    /// One row a MERGE may act on, keyed by the source row that drives it (a
    /// NOT MATCHED BY SOURCE target is keyed past every source row): a matched
    /// target carries its matching source rows, an unmatched target only its
    /// values, and an unmatched source row neither.
    /// </summary>
    private readonly struct MergeStep(int key, int page, int slot, SqlValue[]? targetValues, List<int>? matchedSources)
    {
        public readonly int Key = key;
        public readonly int Page = page;
        public readonly int Slot = slot;
        public readonly SqlValue[]? TargetValues = targetValues;
        public readonly List<int>? MatchedSources = matchedSources;
    }

    /// <summary>
    /// One <c>ON</c> conjunct of the shape <c>&lt;target column&gt; =
    /// &lt;source column&gt;</c>, reduced to the two row ordinals the match
    /// phase reads plus the type both sides coerce to before hashing — the
    /// promote-then-compare target the <c>=</c> operator itself reaches, so a
    /// bucket hit means exactly what evaluating the conjunct would have said.
    /// </summary>
    private sealed class MergeEquiKey(int targetOrdinal, int sourceOrdinal, SqlType common)
    {
        public readonly int TargetOrdinal = targetOrdinal;
        public readonly int SourceOrdinal = sourceOrdinal;
        public readonly SqlType Common = common;
    }

    /// <summary>
    /// The hashable shape of a MERGE's <c>ON</c>: one or more
    /// <see cref="MergeEquiKey"/>s plus the conjuncts that aren't one, which are
    /// re-checked per probed candidate pair.
    /// </summary>
    private sealed class MergeMatchPlan(MergeEquiKey[] keys, BooleanExpression[] residual)
    {
        public readonly MergeEquiKey[] Keys = keys;
        public readonly BooleanExpression[] Residual = residual;
    }

    /// <summary>
    /// The two name spaces a MERGE's <c>ON</c> binds against, and where each
    /// side's columns are read from: an ordinal into the target's decoded heap
    /// row or into the source's materialized value array. Consulted only while
    /// planning the match phase — the runtime resolver keeps its own copy of
    /// these rules, and <see cref="TryClassify"/> is written to agree with it.
    /// </summary>
    private sealed class MergeSides(
        Collation collation,
        HeapTable destinationTable,
        View? sourceView,
        string targetAlias,
        string defaultTargetName,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema)
    {
        /// <summary>
        /// Decides which side of the MERGE a column reference reads and where in
        /// that side's value array it sits, following exactly the rules the
        /// runtime resolver applies: the target alias or the target's own name as
        /// qualifier reads the target, the source alias reads the source, and an
        /// unqualified name tries the target first. False for a name neither side
        /// answers to (which the runtime resolver would reject) and for a view
        /// target's derived projection (no base ordinal to read).
        /// </summary>
        public bool TryClassify(MultiPartName name, out bool isTarget, out int ordinal, out SqlType type)
        {
            if (collation.Equals(name.ImmediateQualifier, targetAlias) || collation.Equals(name.ImmediateQualifier, defaultTargetName))
            {
                if (TryLookupTargetColumn(collation, name.Leaf, destinationTable, sourceView, out ordinal, out type))
                {
                    isTarget = true;
                    return true;
                }
            }
            if (collation.Equals(name.ImmediateQualifier, sourceAlias) && this.TrySourceOrdinal(name.Leaf, out ordinal, out type))
            {
                isTarget = false;
                return true;
            }
            if (name.Count == 1)
            {
                if (TryLookupTargetColumn(collation, name.Leaf, destinationTable, sourceView, out ordinal, out type))
                {
                    isTarget = true;
                    return true;
                }
                if (this.TrySourceOrdinal(name.Leaf, out ordinal, out type))
                {
                    isTarget = false;
                    return true;
                }
            }

            isTarget = false;
            ordinal = 0;
            type = SqlType.Int32;
            return false;
        }

        private bool TrySourceOrdinal(string columnName, out int ordinal, out SqlType type)
        {
            for (var i = 0; i < sourceColumnNames.Length; i++)
            {
                if (collation.Equals(sourceColumnNames[i], columnName))
                {
                    ordinal = i;
                    type = sourceSchema[i];
                    return true;
                }
            }
            ordinal = 0;
            type = SqlType.Int32;
            return false;
        }
    }

    /// <summary>
    /// Splits a MERGE's <c>ON</c> predicate into <c>target = source</c> equality
    /// conjuncts (hashable keys) and a residual of everything else. Returns null
    /// — signalling the caller's target × source scan — when no conjunct splits
    /// cleanly across the two sides.
    /// </summary>
    private static MergeMatchPlan? TryPlanMergeMatch(BooleanExpression on, MergeSides sides)
    {
        var conjuncts = new List<BooleanExpression>();
        on.CollectConjuncts(conjuncts);

        var keys = new List<MergeEquiKey>();
        var residual = new List<BooleanExpression>();
        foreach (var conjunct in conjuncts)
        {
            if (TryExtractMergeEquiKey(conjunct, sides, out var key))
                keys.Add(key);
            else
                residual.Add(conjunct);
        }

        return keys.Count == 0 ? null : new MergeMatchPlan([.. keys], [.. residual]);
    }

    /// <summary>
    /// Recognizes one <c>ON</c> conjunct as an equality between a bare target
    /// column and a bare source column. Declines — leaving the conjunct in the
    /// residual, where it evaluates exactly as it always did — when either
    /// operand is anything but a column reference (which keeps side
    /// classification one exact name lookup, so a more complex operand can never
    /// be misattributed), when both land on the same side, or when the pair's
    /// types don't promote the way the runtime <c>=</c> would.
    /// </summary>
    private static bool TryExtractMergeEquiKey(BooleanExpression conjunct, MergeSides sides, out MergeEquiKey key)
    {
        key = null!;
        if (!conjunct.TryGetEqualityOperands(out var a, out var b)
            || a is not Parser.Expressions.Reference refA
            || b is not Parser.Expressions.Reference refB)
        {
            return false;
        }

        if (!sides.TryClassify(refA.ReferencedName, out var aIsTarget, out var aOrdinal, out var aType)
            || !sides.TryClassify(refB.ReferencedName, out var bIsTarget, out var bOrdinal, out var bType)
            || aIsTarget == bIsTarget)
        {
            return false;
        }

        var (targetOrdinal, targetType, sourceOrdinal, sourceType) = aIsTarget
            ? (aOrdinal, aType, bOrdinal, bType)
            : (bOrdinal, bType, aOrdinal, aType);
        if (!Selection.TryPromoteComparableKeyTypes(targetType, sourceType, out var common))
            return false;

        key = new MergeEquiKey(targetOrdinal, sourceOrdinal, common);
        return true;
    }

    /// <summary>
    /// True when every residual (non-equality) <c>ON</c> conjunct evaluates to
    /// <c>true</c> for a probed pair — UNKNOWN and false both fail, matching the
    /// <c>== true</c> gate the whole-predicate scan applies.
    /// </summary>
    private static bool MergeResidualMatches(BooleanExpression[] residual, RuntimeContext runtime)
    {
        foreach (var conjunct in residual)
        {
            if (conjunct.Run(runtime) != true)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The MERGE source hashed by the <c>ON</c>'s equality keys: a bucket per
    /// distinct key value holding the head and tail of a forward-linked chain of
    /// source indexes, so a probe walks its candidates in ascending source order
    /// — the order the target × source scan discovers them in, which is what
    /// first-source-wins and the Msg 8672 multi-match guard read. A source row
    /// with a NULL in any key component joins no bucket (<c>NULL = NULL</c> is
    /// UNKNOWN) but keeps its index, so it still reaches
    /// <c>WHEN NOT MATCHED BY TARGET</c>.
    /// </summary>
    private sealed class MergeSourceHash
    {
        private readonly MergeEquiKey[] keys;
        private readonly Dictionary<SqlValueKey, (int Head, int Tail)> buckets = [];
        private readonly int[] next;
        private readonly SqlValue[] scratch;

        public MergeSourceHash(MergeEquiKey[] keys, List<SqlValue[]> sourceRows)
        {
            this.keys = keys;
            this.next = new int[sourceRows.Count];
            this.scratch = new SqlValue[keys.Length];
            for (var si = 0; si < sourceRows.Count; si++)
            {
                this.next[si] = -1;
                if (!this.TryComputeKey(sourceRows[si], targetSide: false))
                    continue;

                var probe = new SqlValueKey(this.scratch);
                if (this.buckets.TryGetValue(probe, out var chain))
                {
                    this.next[chain.Tail] = si;
                    this.buckets[probe] = (chain.Head, si);
                }
                else
                {
                    // Only a first occurrence stores the key, so only that path
                    // pays for a stable copy of the scratch buffer.
                    this.buckets[new SqlValueKey((SqlValue[])this.scratch.Clone())] = (si, si);
                }
            }
        }

        /// <summary>
        /// The lowest-numbered source row whose key equals this target row's, or
        /// -1 when a key component is NULL or no source row carries that key.
        /// </summary>
        public int FirstCandidate(SqlValue[] targetValues) =>
            this.TryComputeKey(targetValues, targetSide: true) && this.buckets.TryGetValue(new SqlValueKey(this.scratch), out var chain)
                ? chain.Head
                : -1;

        /// <summary>The next source row sharing <paramref name="sourceIndex"/>'s key, or -1.</summary>
        public int NextCandidate(int sourceIndex) => this.next[sourceIndex];

        // Fills the scratch buffer with one side's key values, each coerced to
        // the key's promotion target so bucket equality is the `=` operator's.
        // False the moment a component is NULL — that row equi-matches nothing.
        private bool TryComputeKey(SqlValue[] row, bool targetSide)
        {
            for (var i = 0; i < this.keys.Length; i++)
            {
                var raw = row[targetSide ? this.keys[i].TargetOrdinal : this.keys[i].SourceOrdinal];
                if (raw.IsNull)
                    return false;
                this.scratch[i] = raw.Type == this.keys[i].Common ? raw : raw.CoerceTo(this.keys[i].Common);
            }
            return true;
        }
    }
}
