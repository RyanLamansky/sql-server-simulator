using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// A parenthesized list directly after a <c>DELETE</c> target is a legacy
    /// hint list real refuses, so a query there is the syntax error at its
    /// <c>SELECT</c> rather than the next statement (probed 2026-10-01 against
    /// SQL Server 2025: <c>DELETE t (SELECT 2)</c> is Msg 156). Leaves the
    /// cursor on the target's name.
    /// </summary>
    private static void RejectQueryAfterDeleteTarget(ParserContext context)
    {
        var onTarget = context.SaveCheckpoint();
        if (context.GetNextOptional() is Operator { Character: '(' })
        {
            if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Select })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.RestoreCheckpoint(onTarget);
    }

    /// <summary>
    /// Parses and executes <c>DELETE [FROM] &lt;table&gt; [WHERE pred]</c>
    /// (single-table form), <c>DELETE [FROM] &lt;alias&gt; FROM &lt;table&gt; AS &lt;alias&gt; [WHERE]</c>
    /// (single-source EF7+ <c>ExecuteDelete</c> form), and the joined-source
    /// form (<c>DELETE [FROM] &lt;alias&gt; FROM t AS &lt;alias&gt; JOIN u AS b ON ... [WHERE]</c>)
    /// that EF Core emits for <c>ExecuteDelete</c> over collection navigations.
    /// Rows matching the predicate are tombstoned at the page level.
    /// </summary>
    /// <remarks>
    /// In the joined-source form, the same target row may surface in
    /// multiple join tuples; SQL Server deletes each unique target exactly
    /// once (probe-confirmed). The simulator dedupes by (page, slot)
    /// during enumeration to match.
    /// </remarks>
    private static SimulatedStatementOutcome ParseDelete(ParserContext context)
    {
        // Real binds FROM, then WHERE, then OUTPUT (probed 2026-09-27).
        context.Batch.BindErrors?.OpenScope(context.Token);
        context.MoveNextRequired();
        var top = Selection.ParseDmlTopClause(context);
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            context.MoveNextRequired();

        var target = ParseDmlTarget(context, RemoteWriteKind.Delete);
        var (leadingIdent, remoteWrite, leadingView, leadingTable) = (target.Name, target.Remote, target.View, target.Table);
        RejectQueryAfterDeleteTarget(context);
        if (leadingView is not null && RouteViewWrite(context.Batch, leadingView, TriggerActions.Delete) == DmlViewRoute.Refused)
            throw NonUpdatableViewError(leadingView, leadingIdent.ToString(), delete: true);
        context.MoveNextOptional();
        var targetHints = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
        Selection.ValidateDmlTargetHints(targetHints, leadingIdent.ToString(), "DELETE");
        // A write's target takes no NOEXPAND, an indexed view's included
        // (probed 2026-10-04 against SQL Server 2025).
        if (targetHints.NoExpand)
            throw SimulatedSqlException.NoExpandHintInvalid(leadingIdent.ToString(), state: 1);
        // A target the FROM clause names — an alias, or a view written through
        // in a join — is read from that clause ahead of the OUTPUT clause,
        // which binds against the table it reaches.
        var preParsedFrom = remoteWrite is null && (leadingTable is null || leadingView is not null || context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            ? Selection.PreParseMutationFrom(context, fromCursor: true)
            : null;
        if (preParsedFrom is not null && (leadingView is not null || (leadingTable is null && JoinedViewTargetIndex(context, preParsedFrom, leadingIdent, leadingView: null) >= 0)))
            return ExecuteJoinedViewTargetDelete(context, leadingIdent, leadingView, top, preParsedFrom);
        // An INSTEAD OF DELETE trigger on a view takes the write whatever the
        // view's shape, reading the view's own rows.
        if (leadingView is not null && RouteViewWrite(context.Batch, leadingView, TriggerActions.Delete) == DmlViewRoute.InsteadOf)
            return ExecuteInsteadOfViewDelete(context, leadingIdent, leadingView, top, targetHints.Serializable);
        if (leadingView is { PartitionedBase: not null })
            return ExecutePartitionedViewDelete(context, leadingIdent, leadingView, top, from: null, targetIndex: 0);
        // Phase 1a: lock the resolved DELETE target. Skipped when
        // leadingTable is null (multi-source alias form — target determined
        // post-FROM, deferred to 1b).
        if (leadingTable is not null)
        {
            LockWriteTable(context.Batch, leadingTable, "DELETE", hints: targetHints);
            context.Batch.RejectReferentialDeleteIntoVectorIndex(leadingTable);
        }

        if (remoteWrite is not null)
            SettleRemoteMutation(context, remoteWrite, remoteWrite);
        // INSERTED isn't a valid qualifier in DELETE OUTPUT (probe-confirmed
        // Msg 4104).
        var output = ParseMutationOutput(context, leadingIdent, leadingTable ?? JoinedTargetTable(context, preParsedFrom, leadingIdent), leadingView, TriggerActions.Delete, preParsedFrom);

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            return ExecuteJoinedDelete(context, leadingIdent, leadingTable, output, top, preParsedFrom);

        var table = RequireMutationTable(context, leadingIdent, leadingTable, "DELETE");
        return ExecuteDeleteAgainstTable(context, leadingIdent, table, output, top, targetHints.Serializable, leadingView);
    }

    /// <summary>
    /// Single-table no-FROM execution path: iterates the target heap directly,
    /// tombstones matching rows, and projects OUTPUT.DELETED if requested.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteDeleteAgainstTable(
        ParserContext context,
        MultiPartName targetName,
        HeapTable table,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        bool serializableHint,
        View? sourceView = null)
    {
        BooleanExpression? where = null;
        PositionedCursorTarget? positionedCursor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Current })
            {
                positionedCursor = ParseWhereCurrentOf(context, table, assignedColumns: null, sourceView);
            }
            else
            {
                using var scope = EnterTargetScope(context, targetName, table, sourceView);
                where = Selection.ParseAndBindPredicate(context, Selection.TargetColumnTypeResolver(context.CurrentDatabase, targetName, table, sourceView));
            }
        }
        Selection.ParseOptionalDmlOptionClause(context);
        if (sourceView is null)
            LoadPredicateStatistics(context.Batch, table, where);

        var plan = new DeletePlan(targetName, table, where, positionedCursor, output, top, serializableHint, sourceView);
        NoteDmlPlan(
            context,
            plan,
            admitted: positionedCursor is null && AdmitsDmlPlan(context.Batch, table, sourceView, output));
        return RunDelete(context, plan);
    }

    /// <summary>
    /// A single-table <c>DELETE</c>'s parse, which <see cref="RunDelete"/>
    /// executes — once as the statement parses, and again for each replay of
    /// a cached plan.
    /// </summary>
    private sealed class DeletePlan(
        MultiPartName targetName,
        HeapTable table,
        BooleanExpression? where,
        PositionedCursorTarget? positionedCursor,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        bool serializableHint,
        View? sourceView) : DmlStatementPlan
    {
        public readonly MultiPartName TargetName = targetName;
        public readonly HeapTable Table = table;
        public readonly BooleanExpression? Where = where;
        public readonly PositionedCursorTarget? PositionedCursor = positionedCursor;
        public readonly OutputProjection? Output = output;
        public readonly Selection.DmlTopLimit? Top = top;
        public readonly bool SerializableHint = serializableHint;
        public readonly View? SourceView = sourceView;

        public override SimulatedStatementOutcome Run(ParserContext context) => RunDelete(context, this);
    }

    /// <summary>
    /// The execution half of a single-table <c>DELETE</c>: the permission
    /// checks, the row walk evaluating WHERE, and the commit. Reads no tokens.
    /// </summary>
    private static SimulatedStatementOutcome RunDelete(ParserContext context, DeletePlan plan)
    {
        var (targetName, table, where, positionedCursor) = (plan.TargetName, plan.Table, plan.Where, plan.PositionedCursor);
        var (output, top, serializableHint, sourceView) = (plan.Output, plan.Top, plan.SerializableHint, plan.SourceView);

        // DELETE reads the target when it has a WHERE clause — real then
        // also requires SELECT, checked first so the SELECT denial surfaces
        // when both SELECT and DELETE are missing (probe M1d). A bare DELETE
        // with no WHERE reads nothing and needs only DELETE (M1e). DELETE
        // itself is not column-grantable, so it stays object-grain; only the
        // read-implies-SELECT is column-grain — on a base table or a view,
        // but not through a synonym, which takes no column grants at all.
        var deleteSecurable = context.Batch.IsSkipping
            ? null
            : PermissionEnforcement.SecurableFor(context.Batch, targetName, (SchemaObject?)sourceView ?? table);
        if (deleteSecurable is { } securable && PermissionEnforcement.Applies(context.Batch, context.Batch.DatabaseFor(securable)))
        {
            SimulatedSqlException? readDenied = null;
            if (where is not null && securable is not Synonym)
            {
                var read = sourceView is not null ? new ColumnReadTarget(sourceView) : new ColumnReadTarget(table);
                where.VisitOperandExpressions(op => op.VisitColumnReferences(read.Add));
                readDenied = PermissionEnforcement.ColumnsDenial(context.Batch, Permission.Select, read);
            }
            else if (where is not null)
            {
                readDenied = PermissionEnforcement.SchemaObjectDenial(context.Batch, "SELECT", securable);
            }
            if (PermissionEnforcement.Combine(readDenied, PermissionEnforcement.SchemaObjectDenial(context.Batch, "DELETE", securable)) is { } refusal)
                throw refusal;
        }
        // Checked even from a module body whose reference to the view is
        // chained.
        if (deleteSecurable is not null)
            CheckBrokenChainMutation(context.Batch, sourceView, TriggerActions.Delete, where, rawAssignments: null);

        if (positionedCursor is null)
            Selection.SettleSerializableWriteFence(table, where, serializableHint, context.Batch);

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        RowSecurity.NoteWrite(context.Batch, table);
        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();
        var needsFull = DeleteNeedsOldRows(context.Batch, table, (SchemaObject?)sourceView ?? table, output);
        var filtered = context.Batch.IsSkipping ? null : RowSecurity.For(context.Batch, table)?.Filter;

        // The walk reads the rows as it goes and never meets a row another
        // session deleted meanwhile; real's read meets the deleted key and
        // waits on it. Those still in flight are waited out once the walk is
        // done, before the statement holds anything, and a key one of them —
        // or any delete since the seek below chose the rows — put back sends
        // the statement to run again (BatchContext.TargetKeyReinserted).
        // Noted before the seek: a delete and reinsert settling between the
        // seek and the note left the key in neither.
        var keysPutBack = Volatile.Read(ref table.KeysPutBack);

        // Seek the target when WHERE carries an indexable equality / range
        // (positioned DELETE leaves where null, so it keeps the full scan — the
        // cursor already fixed one row). The loop re-runs WHERE below, so the
        // seek only narrows the rows considered.
        // The compile walk holds no schema lock, so it doesn't read the
        // target's rows or seek cache at all: another session redefining the
        // table meanwhile left them in a layout the walk decoded wrongly.
        var rowSource = context.Batch.IsSkipping ? [] : MutationRowSource(table, where, context.Batch);
        // Skip mode commits nothing (CommitDelete returns early) — same reason
        // the UPDATE path drops its row source, including the runtime errors a
        // never-run statement's WHERE would otherwise raise while a module body
        // binds at CREATE time.
        var readsNoRow = context.Batch.IsSkipping || DmlTopIsZero(top, context.Batch);
        if (readsNoRow)
        {
            // TOP (0) reads no row at all, so nothing per row can raise either.
            rowSource = [];
        }
        else if (where is not null && Selection.MutationPlanStarts(table, where))
        {
            // Real evaluates the WHERE's runtime constants as its plan starts,
            // so `DELETE t WHERE id = 1/0` raises over an empty table.
            RunUpdateStartupConstants(context, table, [where], []);
        }
        var viewRows = MaterializeRowSelectiveViewRows(context, sourceView, positionedCursor is not null);
        // A write wholly through a seek of the clustered key waits in X, as
        // real's plan writes through that seek without reading ahead. Only
        // another session's write in flight makes the walk wait at all; a
        // shared reader is waited out by the X the row is then written under.
        var contended = positionedCursor is null && sourceView is null && where is not null && !readsNoRow
            && (Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActiveUpdateLocks) != 0 || !table.SupersededKeyImages.IsEmptyLockFree());
        var targetWait = contended && Selection.WhereIsClusteredKeySeek(table, where!) ? LockMode.Exclusive : LockMode.Update;
        var throughIndex = contended && targetWait == LockMode.Update ? Selection.MutationSeekIndex(table, where!) : null;
        // A seek chose its rows from the images they carried; one a wait here
        // let settle may carry another.
        if (positionedCursor is null && !readsNoRow && !table.SupersededKeyImages.IsEmptyLockFree()
            && AwaitSupersededTargetRows(context.Batch, table, (address, prior) => JudgeRow(address.Page, address.Slot, prior, out _), targetWait, throughIndex)
            && where is not null)
        {
            rowSource = MutationRowSource(table, where, context.Batch);
        }
        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();
        foreach (var (pageIndex, slotIndex, scannedBytes) in rowSource)
        {
            context.Batch.PollCancellation();
            // Positioned DELETE (WHERE CURRENT OF): only the cursor's row.
            if (positionedCursor is { } positioned && !CursorRowMatches(positioned, (pageIndex, slotIndex)))
                continue;

            // Judged as another session's write leaves it, as UPDATE's are.
            var rowBytes = scannedBytes;
            if (!context.Batch.AwaitTargetRowWriters(table, pageIndex, slotIndex, ref rowBytes, targetWait, throughIndex)
                || !JudgeRow(pageIndex, slotIndex, rowBytes, out var fullOld))
            {
                continue;
            }
            deleted.Add((pageIndex, slotIndex, fullOld));
            judgedRows.Add((pageIndex, slotIndex, rowBytes));
        }

        if (positionedCursor is null && !readsNoRow)
        {
            if (!table.SupersededKeyImages.IsEmptyLockFree())
                _ = AwaitSupersededTargetRows(context.Batch, table, (address, prior) => JudgeRow(address.Page, address.Slot, prior, out _), targetWait, throughIndex);
            if (Volatile.Read(ref table.KeysPutBack) != keysPutBack)
                context.Batch.TargetKeyReinserted = true;
        }
        ApplyDmlTopCap(top, deleted, context.Batch);
        HoldQualifyingRows(context.Batch, table, deleted, judgedRows, walkGeneration, RowLockPurpose.Delete, (i, rowBytes) =>
        {
            var (pageIndex, slotIndex, _) = deleted[i];
            if (!JudgeRow(pageIndex, slotIndex, rowBytes, out var fullOld))
                return false;
            deleted[i] = (pageIndex, slotIndex, fullOld);
            return true;
        });

        // SI writer pre-flight: scan the version chain for snapshot-visible
        // tombstoned rows. A pre-delete payload matching WHERE means
        // another tx already removed a row our snapshot still sees —
        // Msg 3960 with auto-rollback (probe-confirmed against SQL Server
        // 2025; mirrors the SI UPDATE-on-RC-deleted case). Helper lives
        // in Simulation.Update.cs and the partial-class scope shares it.
        // Skipped for positioned deletes — the cursor fixed a single live row.
        if (positionedCursor is null)
            CheckSnapshotConflictOnTombstonedRows(context, table, where, sourceView);

        return CommitDelete(context, table, deleted, output, sourceView, rowsLocked: true);

        // Whether the statement deletes the row, with the full image the
        // statement keeps of it when something reads that.
        bool JudgeRow(int pageIndex, int slotIndex, byte[] rowBytes, out SqlValue[]? fullOld)
        {
            fullOld = null;
            SqlValue[]? fullValues = null;
            if (where is not null || sourceView is not null || needsFull || filtered is not null)
            {
                fullValues = DecodeFullRow(table, rowBytes);
                EvaluateComputedColumns(table, fullValues, context.Batch);
            }

            // A row the table's filter predicate hides is no candidate, and is
            // judged before anything the statement itself evaluates.
            if (filtered is not null && !RowSecurity.Admits(context.Batch, table, fullValues!))
                return false;

            // View visibility filter: rows not visible in the view aren't
            // candidates for DELETE through it. AND-of-WHEREs up the chain.
            if (sourceView?.VisibilityCheck is { } vis && !vis(fullValues!, context.Batch))
                return false;

            // A windowed or row-limited target writes only to the rows its body yields.
            SqlValue[]? viewRow = null;
            if (viewRows is not null && !viewRows.TryGetValue((pageIndex, slotIndex), out viewRow))
                return false;

            if (where is not null)
            {
                var localValues = fullValues!;
                SqlValue Resolve(MultiPartName name) => ReadTargetRowColumn(context.Batch, table, sourceView, localValues, viewRow, (pageIndex, slotIndex), name);

                if (where.Run(new RuntimeContext(Resolve, context.Batch)) != true)
                    return false;
            }

            fullOld = needsFull ? fullValues : null;
            return true;
        }
    }

    /// <summary>
    /// Joined-source DELETE execution. Mirrors
    /// <see cref="ExecuteJoinedUpdate"/>'s shape: parses multi-source FROM,
    /// identifies target via <see cref="FindMutationTargetIndex"/>, builds
    /// the byte[]-to-(page,slot) address map, then iterates join tuples
    /// applying WHERE per tuple and deduping target deletes by address.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteJoinedDelete(
        ParserContext context,
        MultiPartName leadingIdent,
        HeapTable? leadingTable,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom? preParsedFrom)
    {
        var sourcesList = preParsedFrom?.Sources ?? [];
        var joinsList = preParsedFrom?.Joins ?? [];
        var binding = preParsedFrom?.Binding;
        if (preParsedFrom is not null)
        {
            // The OUTPUT clause's binding already read the sources; resume past them.
            context.RestoreCheckpoint(preParsedFrom.After);
        }
        else
        {
            // Real leaves NEXT VALUE FOR legal in a joined UPDATE / DELETE's own
            // FROM-clause derived table, where every other derived table refuses it
            // (probe-confirmed 2026-08-05, both spellings, against the Msg 11719
            // the SELECT / INSERT … SELECT / MERGE … USING forms take).
            using (ParserScope.Enter(ref context.AllowNextValueForInFromClause, true))
            {
                context.Batch.BindErrors?.EnterClause(context.Token, BindClause.From);
                binding = Selection.ParseSourcesAndJoins(context, QueryScope.Statement, sourcesList, joinsList);
            }
        }
        if (ReadJoinedTailPastMissingTarget(context, sourcesList, joinsList, leadingIdent, leadingTable))
            return new SimulatedNonQuery(0);
        var targetIndex = FindOrAppendMutationTarget(context, sourcesList, joinsList, leadingIdent, leadingTable, binding);
        // A leading name that is a table's but aliases a view, CTE or derived
        // table in the FROM clause writes through that source.
        if (output is null && sourcesList[targetIndex] is { BackingTable: null } aliased && aliased.WriteTargetView() is not null)
            return ExecuteJoinedViewTargetDelete(context, leadingIdent, leadingView: null, top, new Selection.PreParsedFrom(sourcesList, joinsList, context.SaveCheckpoint()));
        var sources = sourcesList.ToArray();
        var joins = joinsList.ToArray();

        var table = BindJoinedMutationTable(context, sources, targetIndex, "DELETE");
        context.Batch.RejectReferentialDeleteIntoVectorIndex(table);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
            context.MoveNextRequired();
            where = Selection.ParseAndBindPredicate(context, Selection.ColumnTypeResolverFor(sources), sources, joins);
        }
        Selection.ParseOptionalDmlOptionClause(context);
        BindJoinPredicatesWhileReporting(context.Batch, joins, Selection.ColumnTypeResolverFor(sources));
        Selection.ValidateForcedSeeks(context, sources, joins, where);
        LoadJoinedPredicateStatistics(context.Batch, sources, joins, where);

        var plan = new JoinedDeletePlan(table, sources, joins, targetIndex, where, output, top);
        NoteDmlPlan(context, plan, admitted: AdmitsDmlPlan(context.Batch, table, view: null, output));
        return RunJoinedDelete(context, plan);
    }

    /// <summary>
    /// A joined <c>DELETE</c>'s parse, which <see cref="RunJoinedDelete"/>
    /// executes — once as the statement parses, and again for each replay of
    /// a cached plan.
    /// </summary>
    private sealed class JoinedDeletePlan(
        HeapTable table,
        FromSource[] sources,
        JoinSpec[] joins,
        int targetIndex,
        BooleanExpression? where,
        OutputProjection? output,
        Selection.DmlTopLimit? top) : DmlStatementPlan
    {
        public readonly HeapTable Table = table;

        /// <summary>The sources as parsed; each execution narrows and reorders a copy.</summary>
        public readonly FromSource[] Sources = sources;
        public readonly JoinSpec[] Joins = joins;
        public readonly int TargetIndex = targetIndex;
        public readonly BooleanExpression? Where = where;
        public readonly OutputProjection? Output = output;
        public readonly Selection.DmlTopLimit? Top = top;

        public override SimulatedStatementOutcome Run(ParserContext context) => RunJoinedDelete(context, this);
    }

    /// <summary>
    /// The execution half of a joined <c>DELETE</c>: the join walk applying
    /// WHERE per tuple and deduping its target rows by address, and the
    /// commit. Reads no tokens.
    /// </summary>
    private static SimulatedStatementOutcome RunJoinedDelete(ParserContext context, JoinedDeletePlan plan)
    {
        // Skip mode has bound everything it needs; enumerating the join would
        // run its sources, a NEXT VALUE FOR among them.
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);
        var (table, where, output, top) = (plan.Table, plan.Where, plan.Output, plan.Top);
        var sources = (FromSource[])plan.Sources.Clone();
        var joins = plan.Joins;
        var targetIndex = plan.TargetIndex;
        if (where?.IsNeverTrue != true && !DmlTopIsZero(top, context.Batch))
            RunUpdateStartupConstants(context, table, JoinedPredicates(joins, where), []);

        Selection.SettleSerializableWriteFence(table, where, serializableHint: false, context.Batch, sources[targetIndex].Qualifier);
        // Noted before the target is read, as the plain walk notes it.
        var keysPutBack = Volatile.Read(ref table.KeysPutBack);
        var targetAddresses = new RowAddressMap();
        sources[targetIndex] = sources[targetIndex].AsWriteTarget(targetAddresses);
        // Settled over the FROM clause as written, ahead of the reordering.
        var targetWait = JoinedTargetWait(table, sources, targetIndex, joins, where);
        sources = Selection.PrepareMutationJoinSources(sources, ref joins, where is null ? [] : [where], ref targetIndex, MutationMayReorder(top, context.Batch), context.Batch);

        var seen = new HashSet<(int Page, int Slot)>();
        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();
        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();
        var needsFull = DeleteNeedsOldRows(context.Batch, table, table, output);
        var partners = output is { ReadsPartners: true } ? new OutputPartnerRows(sources) : null;

        // Hoisted per-row scaffolding — see ExecuteJoinedUpdate.
        byte[]?[] currentTuple = [];
        SqlValue resolveAcrossTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, currentTuple, name, context.Batch);
        var runtime = new RuntimeContext(resolveAcrossTuple, context.Batch);

        if (!table.SupersededKeyImages.IsEmptyLockFree())
            _ = AwaitSupersededTargetRows(context.Batch, table, (_, prior) => Rejudge(prior, out var _), targetWait);
        foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, context.Batch, outerResolver: null))
        {
            currentTuple = tuple;

            if (where is not null && where.Run(runtime) != true)
                continue;

            var targetBytes = tuple[targetIndex];
            if (targetBytes is null)
                continue;
            if (!targetAddresses.TryGet(targetBytes, out var addr))
                continue;
            if (!seen.Add(addr))
                continue;

            // Judged as another session's write leaves it, as the joined
            // UPDATE's are.
            var rowBytes = targetBytes;
            if (!context.Batch.AwaitTargetRowWriters(table, addr.Page, addr.Slot, ref rowBytes, targetWait))
                continue;
            SqlValue[]? fullOld;
            if (ReferenceEquals(rowBytes, targetBytes) || rowBytes.AsSpan().SequenceEqual(targetBytes))
                fullOld = FullImage(targetBytes);
            else if (!Rejudge(rowBytes, out fullOld))
                continue;
            deleted.Add((addr.Page, addr.Slot, fullOld));
            judgedRows.Add((addr.Page, addr.Slot, rowBytes));
            partners?.Note(addr, currentTuple);
        }

        // A target row whose key another session deleted and put back
        // elsewhere during the walk was never paired: the statement runs
        // again, as the plain walk's does.
        if (!table.SupersededKeyImages.IsEmptyLockFree())
            _ = AwaitSupersededTargetRows(context.Batch, table, (_, prior) => Rejudge(prior, out var _), targetWait);
        if (Volatile.Read(ref table.KeysPutBack) != keysPutBack)
            context.Batch.TargetKeyReinserted = true;

        ApplyDmlTopCap(top, deleted, context.Batch);
        HoldQualifyingRows(context.Batch, table, deleted, judgedRows, walkGeneration, RowLockPurpose.Delete, (i, rowBytes) =>
        {
            if (!Rejudge(rowBytes, out var fullOld))
                return false;
            deleted[i] = (deleted[i].PageIndex, deleted[i].SlotIndex, fullOld);
            partners?.Note((deleted[i].PageIndex, deleted[i].SlotIndex), currentTuple);
            return true;
        });

        return CommitDelete(context, table, deleted, output, sourceView: null, rowsLocked: true, partners);

        // The target row's full image, when something reads it.
        SqlValue[]? FullImage(byte[] targetBytes)
        {
            if (!needsFull)
                return null;
            var fullValues = DecodeFullRow(table, targetBytes);
            EvaluateComputedColumns(table, fullValues, context.Batch);
            return fullValues;
        }

        // Whether the target row as rowBytes still qualifies, judged again
        // against its partners, with its full image.
        bool Rejudge(byte[] rowBytes, out SqlValue[]? fullOld)
        {
            foreach (var tuple in Selection.EnumerateJoinedRows(WithTargetNarrowedTo(sources, targetIndex, rowBytes), joins, context.Batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (tuple[targetIndex] is { } targetBytes && (where is null || where.Run(runtime) == true))
                {
                    fullOld = FullImage(targetBytes);
                    return true;
                }
            }
            fullOld = null;
            return false;
        }
    }

    /// <summary>
    /// Whether a DELETE walk keeps each row's decoded old image for
    /// <see cref="CommitDelete"/>, which reads it for OUTPUT's and the
    /// triggers' DELETED, the history row, and the parent-side checks of
    /// incoming foreign keys and edge constraints. Those skip a row whose
    /// image is null, so every DELETE walk asks here, since one answering
    /// without the foreign-key term would tombstone a referenced row and
    /// leave its child orphaned.
    /// </summary>
    private static bool DeleteNeedsOldRows(BatchContext batch, HeapTable table, SchemaObject insteadOfParent, OutputProjection? output) =>
        output is not null
        || HasAfterTrigger(batch, table, TriggerActions.Delete)
        || HasInsteadOfTrigger(batch, insteadOfParent, TriggerActions.Delete)
        || table.SystemVersioning is not null
        || table.IncomingForeignKeys.Count > 0
        || table.GraphKind == GraphTableKind.Node;

    /// <summary>The logical column values of the live row at an address, computed columns included.</summary>
    private static SqlValue[] FullImageAt(HeapTable table, int pageIndex, int slotIndex, BatchContext batch)
    {
        var fullValues = DecodeFullRow(table, table.Heap.ReadSlotBytes(pageIndex, slotIndex)!);
        EvaluateComputedColumns(table, fullValues, batch);
        return fullValues;
    }

    /// <summary>
    /// Tombstones the deleted rows and emits OUTPUT.DELETED projection rows
    /// when requested. Shared between the no-FROM and joined-source paths.
    /// When an INSTEAD OF DELETE trigger is attached to the target (heap
    /// table directly, or the view passed in <paramref name="sourceView"/>),
    /// the heap-delete / AFTER-trigger path is skipped and the INSTEAD OF
    /// body fires with DELETED carrying the would-be deleted rows.
    /// </summary>
    private static SimulatedStatementOutcome CommitDelete(
        ParserContext context,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted,
        OutputProjection? output,
        View? sourceView = null,
        bool rowsLocked = false,
        OutputPartnerRows? partners = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Delete);
        if (!insteadOfActive)
            RejectWriteToUnwritableFilegroup(table, context.Batch, "DELETE");

        // SNAPSHOT isolation write-conflict: a DELETE on a row modified
        // since this SI tx's snapshot raises Msg 3960 and auto-rolls-back.
        foreach (var (pageIndex, slotIndex, _) in deleted)
            Storage.VersionStore.CheckSnapshotUpdateConflict(context.Batch, table, (pageIndex, slotIndex), delete: true);

        if (insteadOfActive)
        {
            // OUTPUT INTO's rows land before the body runs (probed 2026-09-27
            // against SQL Server 2025); to the client it is Msg 334.
            var outputRows = output is null ? null : ProjectDeleteOutput(deleted, output, context.Batch, partners);
            FireInsteadOfDeleteTrigger(context, table, sourceView, deleted);
            return output is null || output.HasTarget
                ? new SimulatedNonQuery(deleted.Count)
                : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!, deleted.Count) { ColumnNullability = output.Nullability };
        }

        // A BEFORE DELETE block predicate judges each row as it stands, ahead
        // of any change.
        if (RowSecurity.For(context.Batch, table)?.BlockFor(BlockOperation.BeforeDelete) is not null)
        {
            foreach (var (pageIndex, slotIndex, fullOld) in deleted)
                RowSecurity.EnforceBlock(context.Batch, table, BlockOperation.BeforeDelete, fullOld ?? FullImageAt(table, pageIndex, slotIndex, context.Batch));
        }

        var undoLog = table.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;
        // System-versioned DELETE: copy each row's pre-delete state to
        // history with ROW END = UtcNow before tombstoning the current row.
        if (table.SystemVersioning is { } historyTable && table.PeriodColumns is { } pc)
        {
            foreach (var (_, _, oldFull) in deleted)
            {
                if (oldFull is not null)
                    WriteHistoryRow(table, historyTable, pc, oldFull, context, undoLog);
            }
        }
        foreach (var (pageIndex, slotIndex, fullOld) in deleted)
            DeleteRowAt(context, table, pageIndex, slotIndex, fullOld, undoLog, rowsLocked);

        // Incoming-FK cascade: parent-side DELETE fires the matching FK's
        // DELETE action on every child table whose FK columns reference one
        // of the deleted rows. NO ACTION raises Msg 547; CASCADE recurses;
        // SET NULL / SET DEFAULT rewrite the child's FK columns.
        if (table.IncomingForeignKeys.Count > 0 || table.GraphKind == GraphTableKind.Node)
        {
            var oldRows = new List<SqlValue[]>(deleted.Count);
            foreach (var (_, _, oldFull) in deleted)
            {
                if (oldFull is not null)
                    oldRows.Add(oldFull);
            }
            if (oldRows.Count > 0)
                EnforceIncomingForeignKeysOnDelete(table, oldRows, context, "DELETE", depth: 0);
            if (table.GraphKind == GraphTableKind.Node)
                EnforceEdgeConstraintsOnNodeDelete(table, oldRows, context);
        }

        if (output is not null)
        {
            var rows = ProjectDeleteOutput(deleted, output, context.Batch, partners);
            // OUTPUT INTO @t suppresses the result set (probe-confirmed).
            if (!output.HasTarget)
            {
                FireAfterDeleteTriggers(context, table, deleted);
                return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows, deleted.Count) { ColumnNullability = output.Nullability };
            }
        }
        FireAfterDeleteTriggers(context, table, deleted);
        return new SimulatedNonQuery(deleted.Count);
    }

    /// <summary>
    /// Deletes one row a statement removes — a DELETE's own, or one a foreign
    /// key's or an edge constraint's cascade takes with it — as every other
    /// session must see it: change-tracked, under the row's X with its
    /// pre-image noted for the uniqueness checks and scans that wait on an
    /// uncommitted delete, and with its pre-delete version captured ahead of
    /// the tombstone, so a snapshot never misses the row before the chain
    /// carries it. <paramref name="fullOld"/> is the row's full image when the
    /// caller decoded one; <paramref name="rowLocked"/> says the caller holds
    /// the row's X already.
    /// </summary>
    internal static void DeleteRowAt(ParserContext context, HeapTable table, int pageIndex, int slotIndex, SqlValue[]? fullOld, UndoLog? undoLog, bool rowLocked = false)
    {
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        table.ChangeTracking?.RecordRow(context.Batch, table, fullOld ?? DecodeFullRow(table, table.Heap.ReadSlotBytes(pageIndex, slotIndex)!), ChangeTrackingOperation.Delete);
        var lockable = IsLockableTable(table);
        if (lockable)
        {
            if (!rowLocked)
                context.Batch.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive, RowLockPurpose.Delete);
            context.Batch.NoteSupersededRow(table, pageIndex, slotIndex);
            if (table.RowLocks.TryGetValue((pageIndex, slotIndex), out var rowLock))
                rowLock.DeletedBy = context.Connection.LockOwner;
            if (VersionStore.WillCaptureVersions(context.Batch.DatabaseFor(table), table) && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } oldBytes)
                VersionStore.CaptureWrite(context.Batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytes, VersionWriteKind.Delete);
        }
        // The row's lock entry stays until its X goes (HeapTable.RetireRowLock):
        // a rollback restores the row at this address, and a session already
        // waiting on the lock then holds the row it waited for.
        table.Heap.DeleteAt(pageIndex, slotIndex, undoLog, ReclaimSuperseded(table, context));
    }

    /// <summary>
    /// Rewrites one row a foreign key's referential action changes, as an
    /// UPDATE rewrites its own: under the row's X with its pre-image noted,
    /// the new image tested against the key ranges a SERIALIZABLE reader
    /// holds, and the pre-update version captured ahead of the write.
    /// </summary>
    internal static void RewriteRowAt(ParserContext context, HeapTable table, int pageIndex, int slotIndex, byte[] rewritten, UndoLog? undoLog)
    {
        RejectLobOnEmptyFilegroup(context.Batch, table, rewritten);
        if (IsLockableTable(table))
        {
            context.Batch.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive, RowLockPurpose.UpdatePreImage);
            context.Batch.NoteSupersededRow(table, pageIndex, slotIndex);
            context.Batch.ProbeKeyLocksForUpdate(table, pageIndex, slotIndex, rewritten);
            if (VersionStore.WillCaptureVersions(context.Batch.DatabaseFor(table), table) && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } oldBytes)
                VersionStore.CaptureWrite(context.Batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytes, VersionWriteKind.Update);
        }
        table.Heap.UpdateAt(pageIndex, slotIndex, rewritten, undoLog, ReclaimSuperseded(table, context));
    }

    private static List<byte[]> ProjectDeleteOutput(
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted,
        OutputProjection output,
        BatchContext batch,
        OutputPartnerRows? partners = null)
    {
        var rows = new List<byte[]>(deleted.Count);
        foreach (var (page, slot, fullOld) in deleted)
        {
            var projectedBytes = output.ProjectRow(batch, insertedValues: null, deletedValues: fullOld, partners: partners?.For(page, slot));
            if (projectedBytes is not null)
                rows.Add(projectedBytes);
        }
        return rows;
    }

    private static void FireAfterDeleteTriggers(
        ParserContext context,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted)
    {
        if (!HasAfterTrigger(context.Batch, table, TriggerActions.Delete))
            return;
        var deletedRows = new List<SqlValue[]>(deleted.Count);
        foreach (var (_, _, fullOld) in deleted)
            deletedRows.Add(fullOld ?? new SqlValue[table.Columns.Length]);
        context.Connection.LastStatementRowCount = deleted.Count;
        context.Batch.Connection.Simulation.FireTriggers(
            context.Batch, table, TriggerActions.Delete,
            insertedRows: null, deletedRows: deletedRows,
            affectedRowCount: deleted.Count);
    }

    /// <summary>
    /// Fires the INSTEAD OF DELETE trigger attached to
    /// <paramref name="sourceView"/> (when non-null) or
    /// <paramref name="table"/>. DELETED is projected through the view's
    /// <see cref="View.BaseColumnOrdinals"/> for a view target; INSERTED
    /// is empty for DELETE.
    /// </summary>
    private static void FireInsteadOfDeleteTrigger(
        ParserContext context,
        HeapTable table,
        View? sourceView,
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted)
    {
        var deletedRows = new List<SqlValue[]>(deleted.Count);
        foreach (var (_, _, fullOld) in deleted)
        {
            deletedRows.Add(sourceView is null
                ? (fullOld ?? new SqlValue[table.Columns.Length])
                : (fullOld is null
                    ? new SqlValue[sourceView.OutputColumns.Length]
                    : ProjectThroughView(context.Batch, table, sourceView, fullOld)));
        }
        context.Connection.LastStatementRowCount = deleted.Count;
        var pseudoColumns = sourceView?.OutputColumns ?? table.Columns;
        var parent = (SchemaObject?)sourceView ?? table;
        _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
            context.Batch, parent, TriggerActions.Delete,
            pseudoColumns, insertedRows: null, deletedRows: deletedRows,
            affectedRowCount: deleted.Count);
    }
}
