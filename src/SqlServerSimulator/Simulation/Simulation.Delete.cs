using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and executes <c>DELETE [FROM] &lt;table&gt; [WHERE pred]</c>
    /// (single-table form), <c>DELETE [FROM] &lt;alias&gt; FROM &lt;table&gt; AS &lt;alias&gt; [WHERE]</c>
    /// (single-source EF7+ <c>ExecuteDelete</c> form), and the joined-source
    /// form (<c>DELETE [FROM] &lt;alias&gt; FROM t AS &lt;alias&gt; JOIN u AS b ON ... [WHERE]</c>)
    /// that EF Core emits for <c>ExecuteDelete</c> over collection navigations.
    /// Rows matching the predicate are tombstoned at the page level; their
    /// payload bytes and any LOB chains are not reclaimed (CLAUDE.md flags
    /// this as a leak quirk pending the LOB-lifecycle bundle).
    /// </summary>
    /// <remarks>
    /// In the joined-source form, the same target row may surface in
    /// multiple join tuples; SQL Server deletes each unique target exactly
    /// once (probe-confirmed). The simulator dedupes by (page, slot)
    /// during enumeration to match.
    /// </remarks>
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
        if (leadingView is not null)
        {
            switch (RouteViewWrite(context.Batch, leadingView, TriggerActions.Delete))
            {
                case DmlViewRoute.InsteadOf:
                    {
                        // An INSTEAD OF DELETE trigger takes the write whatever the
                        // view's shape, reading the view's own rows.
                        context.MoveNextOptional();
                        var insteadOfHints = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
                        Selection.ValidateDmlTargetHints(insteadOfHints);
                        return ExecuteInsteadOfViewDelete(context, leadingIdent, leadingView, top, insteadOfHints.Serializable);
                    }
                case DmlViewRoute.Refused:
                    throw NonUpdatableViewError(leadingView, leadingIdent.ToString());
            }
        }
        context.MoveNextOptional();
        var targetHints = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
        Selection.ValidateDmlTargetHints(targetHints);
        // Phase 1a: lock the resolved DELETE target. Skipped when
        // leadingTable is null (multi-source alias form — target determined
        // post-FROM, deferred to 1b).
        if (leadingTable is not null)
        {
            LockWriteTable(context.Batch, leadingTable, "DELETE");
            context.Batch.RejectReferentialDeleteIntoVectorIndex(leadingTable);
        }

        if (remoteWrite is not null)
            SettleRemoteMutation(context, remoteWrite, remoteWrite);
        // INSERTED isn't a valid qualifier in DELETE OUTPUT (probe-confirmed
        // Msg 4104).
        var output = ParseMutationOutput(context, leadingIdent, leadingTable, leadingView, TriggerActions.Delete);

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            return leadingView is not null
                ? throw new NotSupportedException($"Multi-source DELETE through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled — target the underlying table directly.")
                : ExecuteJoinedDelete(context, leadingIdent, leadingTable, output, top);
        }

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
                positionedCursor = ParseWhereCurrentOf(context, table, assignedColumns: null, sourceView);
            else
                where = Selection.ParseAndBindPredicate(context, Selection.TargetColumnTypeResolver(context.Batch, targetName, table, sourceView));
        }

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
            if (where is not null && securable is not Synonym)
            {
                var read = sourceView is not null ? new ColumnReadTarget(sourceView) : new ColumnReadTarget(table);
                where.VisitOperandExpressions(op => op.VisitColumnReferences(read.Add));
                PermissionEnforcement.CheckColumns(context.Batch, Permission.Select, read);
            }
            else if (where is not null)
            {
                PermissionEnforcement.CheckSchemaObject(context.Batch, "SELECT", securable);
            }
            PermissionEnforcement.CheckSchemaObject(context.Batch, "DELETE", securable);
        }
        // Checked even from a module body whose reference to the view is
        // chained.
        if (deleteSecurable is not null)
            CheckBrokenChainMutation(context.Batch, sourceView, TriggerActions.Delete, where, rawAssignments: null);

        if (positionedCursor is null)
            Selection.SettleSerializableWriteFence(table, where, serializableHint, context.Batch);

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();
        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var hasDeleteTriggers = HasAfterTrigger(context.Batch, table, TriggerActions.Delete);
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Delete);
        var needsFullForTriggers = hasDeleteTriggers || insteadOfActive;
        var needsFullForHistory = table.SystemVersioning is not null;
        var needsFullForFk = table.IncomingForeignKeys.Count > 0 || table.GraphKind == GraphTableKind.Node;

        // Seek the target when WHERE carries an indexable equality / range
        // (positioned DELETE leaves where null, so it keeps the full scan — the
        // cursor already fixed one row). The loop re-runs WHERE below, so the
        // seek only narrows the rows considered.
        var rowSource = where is not null
            ? Selection.SeekMutationTarget(table, where, context.Batch) ?? ClusteredScan.RowsWithAddress(table, context.Connection.StatementIo)
            : ClusteredScan.RowsWithAddress(table, context.Connection.StatementIo);
        // Skip mode commits nothing (CommitDelete returns early) — same reason
        // the UPDATE path drops its row source, including the runtime errors a
        // never-run statement's WHERE would otherwise raise while a module body
        // binds at CREATE time.
        if (context.Batch.IsSkipping || DmlTopIsZero(top, context.Batch))
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
        var viewRows = MaterializeRowSelectiveViewRows(context, sourceView, table, positionedCursor is not null);
        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var judgedRows = new List<(int PageIndex, int SlotIndex, TargetRowHold Hold, byte[] Bytes)>();
        foreach (var (pageIndex, slotIndex, scannedBytes) in rowSource)
        {
            context.Batch.PollCancellation();
            // Positioned DELETE (WHERE CURRENT OF): only the cursor's row.
            if (positionedCursor is { } positioned && !CursorRowMatches(positioned, (pageIndex, slotIndex)))
                continue;

            // Judged under the U a writer reads its target with, as UPDATE's are.
            var rowBytes = scannedBytes;
            var hold = context.Batch.AwaitTargetRow(table, pageIndex, slotIndex, ref rowBytes);
            if (hold == TargetRowHold.Gone)
                continue;
            if (!JudgeRow(pageIndex, slotIndex, rowBytes, out var fullOld))
            {
                context.Batch.ReleaseTargetRow(table, pageIndex, slotIndex, hold);
                continue;
            }
            deleted.Add((pageIndex, slotIndex, fullOld));
            judgedRows.Add((pageIndex, slotIndex, hold, rowBytes));
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
            if (where is not null || output is not null || sourceView is not null || needsFullForTriggers || needsFullForHistory || needsFullForFk)
            {
                fullValues = DecodeFullRow(table, rowBytes);
                EvaluateComputedColumns(table, fullValues, context.Batch);
            }

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

            fullOld = (output is null && !needsFullForTriggers && !needsFullForHistory && !needsFullForFk) ? null : fullValues;
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
        Selection.DmlTopLimit? top)
    {
        var sourcesList = new List<FromSource>();
        var joinsList = new List<JoinSpec>();
        // Real leaves NEXT VALUE FOR legal in a joined UPDATE / DELETE's own
        // FROM-clause derived table, where every other derived table refuses it
        // (probe-confirmed 2026-08-05, both spellings, against the Msg 11719
        // the SELECT / INSERT … SELECT / MERGE … USING forms take).
        using (ParserScope.Enter(ref context.AllowNextValueForInFromClause, true))
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.From);
            Selection.ParseSourcesAndJoins(context, QueryScope.Statement, sourcesList, joinsList);
        }
        if (ReadJoinedTailPastMissingTarget(context, sourcesList, joinsList, leadingIdent, leadingTable))
            return new SimulatedNonQuery(0);
        var targetIndex = FindOrAppendMutationTarget(context, sourcesList, joinsList, leadingIdent, leadingTable);
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
        BindJoinPredicatesWhileReporting(context.Batch, joins, Selection.ColumnTypeResolverFor(sources));
        Selection.ValidateForcedSeeks(context, sources, joins, where);

        // Skip mode has bound everything it needs; enumerating the join would
        // run its sources, a NEXT VALUE FOR among them.
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);
        if (where?.IsNeverTrue != true && !DmlTopIsZero(top, context.Batch))
            RunUpdateStartupConstants(context, table, JoinedPredicates(joins, where), []);

        Selection.SettleSerializableWriteFence(table, where, serializableHint: false, context.Batch, sources[targetIndex].Qualifier);
        sources = Selection.PrepareMutationJoinSources(sources, joins, where, targetIndex, context.Batch);

        var targetAddresses = new Dictionary<byte[], (int Page, int Slot)>(ReferenceEqualityComparer.Instance);
        sources[targetIndex] = WrapSourceWithAddressTracking(sources[targetIndex], table, targetAddresses, context.Connection.StatementIo);

        var seen = new HashSet<(int Page, int Slot)>();
        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();

        // Hoisted per-row scaffolding — see ExecuteJoinedUpdate for why the
        // delegate is cached rather than a per-call local-function conversion.
        byte[]?[] currentTuple = [];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, currentTuple, name, context.Batch);

        foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, context.Batch, outerResolver: null))
        {
            currentTuple = tuple;
            var ResolveTuple = resolveTuple;

            if (where is not null && where.Run(new RuntimeContext(ResolveTuple, context.Batch)) != true)
                continue;

            var targetBytes = tuple[targetIndex];
            if (targetBytes is null)
                continue;
            if (!targetAddresses.TryGetValue(targetBytes, out var addr))
                continue;
            if (!seen.Add(addr))
                continue;

            SqlValue[]? fullValues = null;
            // The incoming-FK term is load-bearing, not an optimization:
            // CommitDelete's parent-side enforcement reads the decoded old
            // rows, and skips silently when every one of them is null. Without
            // it a joined DELETE tombstones a referenced parent row and leaves
            // the child orphaned — where the no-FROM path raises Msg 547 — so
            // the two forms have to agree on when the full row is needed.
            var needsFull = output is not null
                || HasAfterTrigger(context.Batch, table, TriggerActions.Delete)
                || HasInsteadOfTrigger(context.Batch, table, TriggerActions.Delete)
                || table.SystemVersioning is not null
                || table.IncomingForeignKeys.Count > 0
                || table.GraphKind == GraphTableKind.Node;
            if (needsFull)
            {
                fullValues = DecodeFullRow(table, targetBytes);
                EvaluateComputedColumns(table, fullValues, context.Batch);
            }
            deleted.Add((addr.Page, addr.Slot, fullValues));
        }

        ApplyDmlTopCap(top, deleted, context.Batch);

        return CommitDelete(context, table, deleted, output, sourceView: null);
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
        bool rowsLocked = false)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Delete);
        if (!insteadOfActive)
            RejectWriteToUnwritableFilegroup(table, context.Batch, "DELETE");

        // SNAPSHOT isolation write-conflict: a DELETE on a row modified
        // since this SI tx's snapshot raises Msg 3960 and auto-rolls-back.
        if (context.Batch.Connection.SessionIsolationLevel == System.Data.IsolationLevel.Snapshot)
        {
            foreach (var (pageIndex, slotIndex, _) in deleted)
                Storage.VersionStore.CheckSnapshotUpdateConflict(context.Batch, table, (pageIndex, slotIndex));
        }

        if (insteadOfActive)
        {
            // OUTPUT INTO's rows land before the body runs (probed 2026-09-27
            // against SQL Server 2025); to the client it is Msg 334.
            var outputRows = output is null ? null : ProjectDeleteOutput(deleted, output, context.Batch);
            FireInsteadOfDeleteTrigger(context, table, sourceView, deleted);
            return output is null || output.HasTarget
                ? new SimulatedNonQuery(deleted.Count)
                : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!, deleted.Count);
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
            var rows = ProjectDeleteOutput(deleted, output, context.Batch);
            // OUTPUT INTO @t suppresses the result set (probe-confirmed).
            if (!output.HasTarget)
            {
                FireAfterDeleteTriggers(context, table, deleted);
                return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows, deleted.Count);
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
            if (VersionStore.WillCaptureVersions(context.Batch.DatabaseFor(table), table) && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } oldBytes)
                VersionStore.CaptureWrite(context.Batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytes, VersionWriteKind.Delete);
        }
        table.Heap.DeleteAt(pageIndex, slotIndex, undoLog, ReclaimSuperseded(table, context));
        // The slot is tombstoned and slot ids are never reused, so the row's
        // lock entry has no future lookup; the hold in the session's lock list
        // keeps the resource alive until release, and what still has to find
        // it — the waits on an uncommitted delete, the lock DMVs — reaches it
        // through HeapTable.SupersededKeyImages.
        if (lockable)
            _ = table.RowLocks.TryRemove((pageIndex, slotIndex), out _);
    }

    /// <summary>
    /// Rewrites one row a foreign key's referential action changes, as an
    /// UPDATE rewrites its own: under the row's X with its pre-image noted,
    /// the new image tested against the key ranges a SERIALIZABLE reader
    /// holds, and the pre-update version captured ahead of the write.
    /// </summary>
    internal static void RewriteRowAt(ParserContext context, HeapTable table, int pageIndex, int slotIndex, byte[] rewritten, UndoLog? undoLog)
    {
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
        BatchContext batch)
    {
        var rows = new List<byte[]>(deleted.Count);
        foreach (var (_, _, fullOld) in deleted)
        {
            var projectedBytes = output.ProjectRow(batch, insertedValues: null, deletedValues: fullOld);
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
                    : ProjectThroughView(sourceView, fullOld)));
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
