using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Where a joined <c>UPDATE</c> / <c>DELETE</c> writing through a view or
    /// CTE finds its target among the <c>FROM</c> clause's sources: the source
    /// the leading name aliases, else the one reading
    /// <paramref name="leadingView"/>, else — real binding a target the clause
    /// never introduced as an implicitly cross-joined source, as it does a
    /// table (probed 2026-10-01 against SQL Server 2025) — a source appended
    /// for it. -1 when the target the leading name aliases isn't a view or CTE,
    /// which the table paths then take.
    /// </summary>
    private static int JoinedViewTargetIndex(ParserContext context, Selection.PreParsedFrom from, MultiPartName leadingIdent, View? leadingView)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        var sources = from.Sources;
        for (var s = 0; s < sources.Count; s++)
        {
            if (sources[s].Qualifier is { } qualifier && collation.Equals(qualifier, leadingIdent.Leaf))
                return sources[s].WriteTargetView() is not null ? s : -1;
        }
        if (leadingView is null)
            return -1;
        for (var s = 0; s < sources.Count; s++)
        {
            if (ReferenceEquals(sources[s].BackingView, leadingView) || ReferenceEquals(sources[s].Cte?.DmlTarget, leadingView))
                return s;
        }

        var columns = ViewColumnsFor(context.Batch, leadingView, leadingIdent);
        var columnNames = new string[columns.Length];
        for (var c = 0; c < columns.Length; c++)
            columnNames[c] = columns[c].Name;
        from.Joins.Add(new JoinSpec(JoinKind.Cross, onPredicate: null));
        sources.Add(new FromSource(
            qualifier: leadingIdent.Leaf,
            columnNames: columnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            backingView: leadingView.UnstoredBody is null ? leadingView : null,
            writtenObjectName: leadingIdent.ToString(),
            cte: leadingView.UnstoredBody is null ? null : context.CteBindings![leadingIdent.Leaf]));
        return sources.Count - 1;
    }

    /// <summary>
    /// The table an alias-form joined <c>UPDATE</c> / <c>DELETE</c> writes, read
    /// off the <c>FROM</c> clause it read ahead, so its <c>OUTPUT</c> clause —
    /// written before that clause — binds against it; null without one.
    /// </summary>
    private static HeapTable? JoinedTargetTable(ParserContext context, Selection.PreParsedFrom? from, MultiPartName leadingIdent) =>
        from is not null && FindMutationTargetIndex(context.Batch.CurrentDatabase.Collation, from.Sources, leadingIdent.Leaf, leadingTable: null) is var index and >= 0
            ? from.Sources[index].BackingTable
            : null;

    /// <summary>
    /// A joined <c>UPDATE</c> whose target is a view or CTE: the leading name
    /// aliases one of its <c>FROM</c> sources or names the view itself, as in
    /// <c>UPDATE a SET … FROM v AS a JOIN u …</c> or <c>UPDATE v SET … FROM v
    /// JOIN u …</c>. Real writes the view's base table — the one the SET list
    /// lands in, for a join view — reading the target's rows as the view
    /// yields them, so its filter, row limit and window apply, and naming the
    /// target as the statement wrote it in its refusals (probed 2026-10-01
    /// against SQL Server 2025).
    /// </summary>
    private static SimulatedStatementOutcome ExecuteJoinedViewTargetUpdate(
        ParserContext context,
        MultiPartName leadingIdent,
        View? leadingView,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom from,
        List<SchemaObject>? partitionedReads = null)
    {
        try
        {
            return JoinedViewTargetUpdate(context, leadingIdent, leadingView, rawAssignments, top, from, partitionedReads);
        }
        catch (SimulatedSqlException) when (ResumePastFrom(context, from))
        {
            throw;
        }
    }

    /// <summary>
    /// Leaves the parser past a joined write's <c>FROM</c> clause when a
    /// refusal raised while it still sat ahead of it, the clause having been
    /// read already — so the batch resumes after the statement rather than
    /// inside a derived table's body. Answers false, letting the error go on.
    /// </summary>
    private static bool ResumePastFrom(ParserContext context, Selection.PreParsedFrom from)
    {
        if (context.SaveCheckpoint().Index < from.After.Index)
            context.RestoreCheckpoint(from.After);
        return false;
    }

    /// <summary>The body of <see cref="ExecuteJoinedViewTargetUpdate"/>.</summary>
    private static SimulatedStatementOutcome JoinedViewTargetUpdate(
        ParserContext context,
        MultiPartName leadingIdent,
        View? leadingView,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom from,
        List<SchemaObject>? partitionedReads)
    {
        var batch = context.Batch;
        var written = leadingIdent.ToString();
        var targetIndex = JoinedViewTargetIndex(context, from, leadingIdent, leadingView);
        var sources = from.Sources.ToArray();
        var joins = from.Joins.ToArray();
        var view = sources[targetIndex].WriteTargetView()!;
        if (sources.Length == 1 && (HasInsteadOfTrigger(batch, view, TriggerActions.Update) || InsteadOfTriggerViewUnder(batch, view, TriggerActions.Update) is not null))
            return ExecuteInsteadOfViewUpdate(context, InsteadOfTargetName(leadingIdent, sources[0], view), view, rawAssignments, top, serializableHint: false, from);
        if (view.PartitionedBase is not null && !HasInsteadOfTrigger(batch, view, TriggerActions.Update))
            return ExecutePartitionedViewUpdate(context, leadingIdent, view, rawAssignments, top, from, targetIndex, partitionedReads ?? []);
        RefuseJoinedViewWrite(context, view, TriggerActions.Update, written, [.. SetColumnNames(rawAssignments)]);
        if (view.BaseTable is not { } table)
            return ExecuteJoinedJoinViewUpdate(context, written, view, sources, joins, targetIndex, rawAssignments, top, from.After);

        BindDeferredXmlMutators(context, table, rawAssignments, written);
        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase, view, batch.BindErrors, derivedLabel: written);
        var setMasks = UpdateSetMasks(batch, assignments, name => Selection.SourceColumnMask(sources, name));
        FunctionBodyShape.NoteTableWrite(batch, "UPDATE", table);
        LockWriteTable(batch, table, "UPDATE", checkFilegroup: true);

        var tupleTypeResolver = Selection.ColumnTypeResolverFor(sources);
        BindSetValues(batch, table, assignments, tupleTypeResolver, _ => false);

        // The OUTPUT clause sits ahead of the FROM clause the statement has
        // already read; INSERTED / DELETED are the view's columns.
        var output = ParseJoinedViewTargetOutput(context, leadingIdent, view, table, TriggerActions.Update, new OutputPartnerScope(sources, targetIndex));
        var where = ParseJoinedViewTargetWhere(context, from.After, sources, joins, tupleTypeResolver);
        CheckJoinedViewTargetPermissions(batch, sources, targetIndex, view, table, TriggerActions.Update, rawAssignments);
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);
        if (where?.IsNeverTrue != true && !DmlTopIsZero(top, batch))
            RunUpdateStartupConstants(context, table, JoinedPredicates(joins, where), assignments);

        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var target = MaterializeViewTarget(batch, view, table, sources, targetIndex);
        sources = Selection.PrepareMutationJoinSources(sources, joins, where, targetIndex, batch);

        var seen = new HashSet<(int Page, int Slot)>();
        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();
        var oldSnapshotNeeded = output is not null
            || HasAfterTrigger(batch, table, TriggerActions.Update)
            || HasInsteadOfTrigger(batch, table, TriggerActions.Update)
            || table.SystemVersioning is not null
            || table.IncomingForeignKeys.Count > 0;
        var partners = output is { ReadsPartners: true } ? new OutputPartnerRows(sources) : null;

        // Hoisted per-row scaffolding: one mutable tuple slot, one cached
        // delegate and one runtime, so the per-row loop allocates none.
        byte[]?[] currentTuple = [];
        SqlValue resolveAcrossTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, currentTuple, name, batch);
        Func<MultiPartName, SqlValue> resolveTuple = resolveAcrossTuple;
        var runtime = new RuntimeContext(resolveTuple, batch);

        // An APPLY's correlated body yields its rows as the join runs it,
        // each carrying its base address into the statement's map.
        using (ParserScope.Enter(ref batch.RowAddressProbe, target.Correlated ? Selection.AddressCarryingWalk : batch.RowAddressProbe))
        {
            foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (where is not null && where.Run(runtime) != true)
                    continue;
                if (tuple[targetIndex] is not { } viewRow || !target.TryAddress(viewRow, out var address) || !seen.Add(address))
                    continue;

                // Judged as another session's write leaves it: the walk waits out,
                // in U, a row that session holds, and judges it again.
                if (target.ImageAt(address) is not { } baseImage)
                    continue;
                var rowBytes = baseImage;
                if (!batch.AwaitTargetRowWriters(table, address.Page, address.Slot, ref rowBytes))
                    continue;
                var judged = ReferenceEquals(rowBytes, baseImage) || rowBytes.AsSpan().SequenceEqual(baseImage)
                    ? JudgeTuple(baseImage)
                    : Rejudge(address, rowBytes);
                if (judged is not { } entry)
                    continue;
                affected.Add((address.Page, address.Slot, entry.NewValues, entry.OldSnapshot));
                judgedRows.Add((address.Page, address.Slot, rowBytes));
                partners?.Note(address, currentTuple);
            }

            ApplyDmlTopCap(top, affected, batch);
            HoldQualifyingRows(batch, table, affected, judgedRows, walkGeneration, RowLockPurpose.UpdatePreImage, (i, rowBytes) =>
            {
                if (Rejudge((affected[i].PageIndex, affected[i].SlotIndex), rowBytes) is not { } judged)
                    return false;
                affected[i] = (affected[i].PageIndex, affected[i].SlotIndex, judged.NewValues, judged.OldSnapshot);
                partners?.Note((affected[i].PageIndex, affected[i].SlotIndex), currentTuple);
                return true;
            });
        }

        return CommitUpdate(context, table, affected, output, [.. SetColumnOrdinals(assignments)], rowsLocked: true, partners: partners);

        // The current tuple's target row's new values, and its old image when
        // something reads that.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot) JudgeTuple(byte[] baseImage)
        {
            var fullValues = DecodeFullRow(table, baseImage);
            EvaluateComputedColumns(table, fullValues, batch);
            batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, resolveTuple, setMasks);
            if (view.CheckOptionCheck is { } checkOption && !checkOption(newValues, batch))
                throw SimulatedSqlException.ViewCheckOptionViolation();
            return (newValues, oldSnapshotNeeded ? fullValues : null);
        }

        // The row at address as rowBytes, judged again against its partners.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot)? Rejudge((int Page, int Slot) address, byte[] rowBytes) =>
            QualifyingTuple(address, rowBytes) is not null ? JudgeTuple(rowBytes) : null;

        // The first tuple showing the row at address as rowBytes that passes
        // the join and WHERE, made current; null when none does.
        byte[]?[]? QualifyingTuple((int Page, int Slot) address, byte[] rowBytes)
        {
            if (target.Correlated)
                return CorrelatedQualifyingTuple(target, sources, joins, targetIndex, where, address, ref currentTuple, runtime);
            if (target.ViewRowOf(address, rowBytes) is not { } viewRow)
                return null;
            foreach (var tuple in Selection.EnumerateJoinedRows(WithTargetNarrowedTo(sources, targetIndex, viewRow), joins, batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (tuple[targetIndex] is not null && (where is null || where.Run(runtime) == true))
                    return tuple;
            }
            return null;
        }
    }

    /// <summary>
    /// A joined <c>DELETE</c> whose target is a view or CTE, as
    /// <see cref="ExecuteJoinedViewTargetUpdate"/> reads it: it removes the
    /// base rows the view's rows the join and <c>WHERE</c> pass show, each
    /// once. A view reading several base tables is Msg 4405 naming the target
    /// as written (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static SimulatedStatementOutcome ExecuteJoinedViewTargetDelete(
        ParserContext context,
        MultiPartName leadingIdent,
        View? leadingView,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom from)
    {
        try
        {
            return JoinedViewTargetDelete(context, leadingIdent, leadingView, top, from);
        }
        catch (SimulatedSqlException) when (ResumePastFrom(context, from))
        {
            throw;
        }
    }

    /// <summary>The body of <see cref="ExecuteJoinedViewTargetDelete"/>.</summary>
    private static SimulatedStatementOutcome JoinedViewTargetDelete(
        ParserContext context,
        MultiPartName leadingIdent,
        View? leadingView,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom from)
    {
        var batch = context.Batch;
        var written = leadingIdent.ToString();
        var targetIndex = JoinedViewTargetIndex(context, from, leadingIdent, leadingView);
        var sources = from.Sources.ToArray();
        var joins = from.Joins.ToArray();
        var view = sources[targetIndex].WriteTargetView()!;
        if (sources.Length == 1 && (HasInsteadOfTrigger(batch, view, TriggerActions.Delete) || InsteadOfTriggerViewUnder(batch, view, TriggerActions.Delete) is not null))
            return ExecuteInsteadOfViewDelete(context, InsteadOfTargetName(leadingIdent, sources[0], view), view, top, serializableHint: false, from);
        if (view.PartitionedBase is not null && !HasInsteadOfTrigger(batch, view, TriggerActions.Delete))
            return ExecutePartitionedViewDelete(context, leadingIdent, view, top, from, targetIndex);
        RefuseJoinedViewWrite(context, view, TriggerActions.Delete, written, setColumns: null);
        var table = view.BaseTable!;
        FunctionBodyShape.NoteTableWrite(batch, "DELETE", table);
        LockWriteTable(batch, table, "DELETE", checkFilegroup: true);
        batch.RejectReferentialDeleteIntoVectorIndex(table);

        var output = ParseJoinedViewTargetOutput(context, leadingIdent, view, table, TriggerActions.Delete, new OutputPartnerScope(sources, targetIndex));
        var where = ParseJoinedViewTargetWhere(context, from.After, sources, joins, Selection.ColumnTypeResolverFor(sources));
        CheckJoinedViewTargetPermissions(batch, sources, targetIndex, view, table, TriggerActions.Delete, rawAssignments: null);
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);
        if (where?.IsNeverTrue != true && !DmlTopIsZero(top, batch))
            RunUpdateStartupConstants(context, table, JoinedPredicates(joins, where), []);

        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var target = MaterializeViewTarget(batch, view, table, sources, targetIndex);
        sources = Selection.PrepareMutationJoinSources(sources, joins, where, targetIndex, batch);

        var seen = new HashSet<(int Page, int Slot)>();
        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();
        // CommitDelete's foreign-key enforcement reads the decoded old rows,
        // as on the joined table path.
        var needsFull = output is not null
            || HasAfterTrigger(batch, table, TriggerActions.Delete)
            || HasInsteadOfTrigger(batch, table, TriggerActions.Delete)
            || table.SystemVersioning is not null
            || table.IncomingForeignKeys.Count > 0
            || table.GraphKind == GraphTableKind.Node;
        var partners = output is { ReadsPartners: true } ? new OutputPartnerRows(sources) : null;

        byte[]?[] currentTuple = [];
        SqlValue resolveAcrossTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, currentTuple, name, batch);
        var runtime = new RuntimeContext(resolveAcrossTuple, batch);

        // An APPLY's correlated body yields its rows as the join runs it,
        // each carrying its base address into the statement's map.
        using (ParserScope.Enter(ref batch.RowAddressProbe, target.Correlated ? Selection.AddressCarryingWalk : batch.RowAddressProbe))
        {
            foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (where is not null && where.Run(runtime) != true)
                    continue;
                if (tuple[targetIndex] is not { } viewRow || !target.TryAddress(viewRow, out var address) || !seen.Add(address))
                    continue;

                // Judged as another session's write leaves it, as the joined
                // UPDATE's are.
                if (target.ImageAt(address) is not { } baseImage)
                    continue;
                var rowBytes = baseImage;
                if (!batch.AwaitTargetRowWriters(table, address.Page, address.Slot, ref rowBytes))
                    continue;
                if (!ReferenceEquals(rowBytes, baseImage) && !rowBytes.AsSpan().SequenceEqual(baseImage) && !Qualifies(address, rowBytes))
                    continue;
                deleted.Add((address.Page, address.Slot, FullImage(rowBytes)));
                judgedRows.Add((address.Page, address.Slot, rowBytes));
                partners?.Note(address, currentTuple);
            }

            ApplyDmlTopCap(top, deleted, batch);
            HoldQualifyingRows(batch, table, deleted, judgedRows, walkGeneration, RowLockPurpose.Delete, (i, rowBytes) =>
            {
                var (page, slot, _) = deleted[i];
                if (!Qualifies((page, slot), rowBytes))
                    return false;
                deleted[i] = (page, slot, FullImage(rowBytes));
                partners?.Note((page, slot), currentTuple);
                return true;
            });
        }

        return CommitDelete(context, table, deleted, output, rowsLocked: true, partners: partners);

        // The base row's full image, when something reads it.
        SqlValue[]? FullImage(byte[] baseImage)
        {
            if (!needsFull)
                return null;
            var fullValues = DecodeFullRow(table, baseImage);
            EvaluateComputedColumns(table, fullValues, batch);
            return fullValues;
        }

        // Whether the row at address as rowBytes still shows through the view
        // and passes the join and WHERE with some partner.
        bool Qualifies((int Page, int Slot) address, byte[] rowBytes)
        {
            if (target.Correlated)
                return CorrelatedQualifyingTuple(target, sources, joins, targetIndex, where, address, ref currentTuple, runtime) is not null;
            if (target.ViewRowOf(address, rowBytes) is not { } viewRow)
                return false;
            foreach (var tuple in Selection.EnumerateJoinedRows(WithTargetNarrowedTo(sources, targetIndex, viewRow), joins, batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (tuple[targetIndex] is not null && (where is null || where.Run(runtime) == true))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The first tuple a joined write through an <c>APPLY</c>'s correlated
    /// body yields whose target row shows the base row at
    /// <paramref name="address"/> and passes the <c>WHERE</c>, made current;
    /// null when none does. The body's rows depend on the left side's, so the
    /// whole join runs again rather than one narrowed to the row.
    /// </summary>
    private static byte[]?[]? CorrelatedQualifyingTuple(
        ViewTargetRows target,
        FromSource[] sources,
        JoinSpec[] joins,
        int targetIndex,
        BooleanExpression? where,
        (int Page, int Slot) address,
        ref byte[]?[] currentTuple,
        RuntimeContext runtime)
    {
        foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, runtime.Batch, outerResolver: null))
        {
            currentTuple = tuple;
            if (tuple[targetIndex] is { } viewRow && target.TryAddress(viewRow, out var at) && at == address && (where is null || where.Run(runtime) == true))
                return tuple;
        }
        return null;
    }

    /// <summary>
    /// A joined <c>UPDATE</c> naming a join view in its <c>FROM</c> clause:
    /// the statement's own sources form a chain of no levels whose target
    /// source nests the view's level stack, and the SET list descends from
    /// that source to the one base table it lands in, Msg 4405 / 4406 naming
    /// the target as written otherwise (probed 2026-10-01 against SQL Server
    /// 2025). The walk is <see cref="RunJoinViewUpdate"/>'s.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteJoinedJoinViewUpdate(
        ParserContext context,
        string written,
        View view,
        FromSource[] sources,
        JoinSpec[] joins,
        int targetIndex,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top,
        ParserContext.Checkpoint afterFrom)
    {
        var batch = context.Batch;
        var chain = new JoinViewChain(sources, joins, written) { TargetIsDerivedTable = view.IsDerivedTable };
        var (path, assignments) = ResolveJoinViewSetTargets(batch, chain, rawAssignments, written, targetIndex);
        var table = chain.TableAt(path) ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(written, view.IsDerivedTable);
        var nested = chain.Nested[targetIndex]!;

        BindDeferredXmlMutators(context, table, rawAssignments, written);
        FunctionBodyShape.NoteTableWrite(batch, "UPDATE", table);
        LockWriteTable(batch, table, "UPDATE", checkFilegroup: true);
        var tupleTypeResolver = Selection.ColumnTypeResolverFor(sources);
        foreach (var (_, expr) in rawAssignments)
            UnresolvedCollation.RequireAssignable(expr.GetSqlType(batch, tupleTypeResolver));

        OutputProjection? output = null;
        Dictionary<SqlValue[], byte[]?[]>? tuplesByRow = null;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
        {
            tuplesByRow = new(ReferenceEqualityComparer.Instance);
            var shape = JoinViewOutputShape(batch, sources[targetIndex].Columns, nested, nested.Sources, path[1..], table, tuplesByRow);
            output = TryParseOutputClauseForMutation(context, table, allowInserted: true, allowDeleted: true, shape, partners: new OutputPartnerScope(sources, targetIndex));
            RejectClientOutputOnTriggeredTarget(batch, table, TriggerActions.Update, table.Name, output is { HasTarget: false });
        }
        var where = ParseJoinedViewTargetWhere(context, afterFrom, sources, joins, tupleTypeResolver);
        CheckJoinedViewTargetPermissions(batch, sources, targetIndex, view, table, TriggerActions.Update, rawAssignments);
        var readViewColumns = new List<string>();
        CollectTargetColumnReads(sources, targetIndex, where, rawAssignments, readViewColumns.Add);
        CheckJoinViewBrokenChains(batch, nested, table, readViewColumns, assignments);
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);
        if (where?.IsNeverTrue != true && !DmlTopIsZero(top, batch))
            RunUpdateStartupConstants(context, table, JoinedPredicates(joins, where), assignments);

        return RunJoinViewUpdate(context, chain, path, table, assignments, where, output, tuplesByRow, top, positionedCursor: null);
    }

    /// <summary>
    /// The name a joined write through a view whose <c>INSTEAD OF</c> trigger
    /// takes it reports the view by: as the leading name wrote it, or, when
    /// that is the <c>FROM</c> clause's alias for it, as the clause wrote it.
    /// </summary>
    private static MultiPartName InsteadOfTargetName(MultiPartName leadingIdent, FromSource source, View view) =>
        source.UnaliasedName is not null ? leadingIdent : new MultiPartName(view.Schema.Name).WithAddedPart(view.Name);

    /// <summary>
    /// Real's refusals of a joined write through a view, ahead of anything it
    /// reads: a view carrying an <c>INSTEAD OF</c> trigger for the action
    /// beside another source is Msg 414 / 415 naming the view — one the
    /// <c>FROM</c> clause names alone takes the trigger instead; a <c>DELETE</c>
    /// through one reading several base tables Msg 4405, and a view no write
    /// passes through its own refusal — an <c>UPDATE</c>'s unknown column Msg
    /// 207 and derived one Msg 4406 first — all naming the target as written
    /// (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static void RefuseJoinedViewWrite(ParserContext context, View view, TriggerActions action, string written, List<string>? setColumns)
    {
        if (HasInsteadOfTrigger(context.Batch, view, action))
            throw SimulatedSqlException.InsteadOfViewInJoin(view.Name, action == TriggerActions.Delete);
        if (view.BaseTable is not null || (view.IsJoinUpdatable && action != TriggerActions.Delete))
            return;
        if (action != TriggerActions.Delete && view.DerivedOutputColumns is { } derived)
        {
            foreach (var column in setColumns!)
            {
                var ordinal = IndexOfViewOutputColumn(context.CurrentDatabase.Collation, view, column);
                if (ordinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(column);
                if (derived[ordinal])
                    throw SimulatedSqlException.ViewDmlTouchesDerivedField(view.IsDerivedTable ? written : view.UnionOwnerName ?? written, view.IsDerivedTable);
            }
        }
        throw NonUpdatableViewError(view, written, action == TriggerActions.Delete);
    }

    /// <summary>
    /// The <c>OUTPUT</c> clause of a joined write through a single-base view
    /// or CTE, where the cursor sits: <c>INSERTED</c> / <c>DELETED</c> are the
    /// view's columns read off the base rows, the statement's other sources
    /// are read as <paramref name="partners"/> scopes them, and the base
    /// table's triggers refuse it to the client with Msg 334 naming that table.
    /// </summary>
    private static OutputProjection? ParseJoinedViewTargetOutput(ParserContext context, MultiPartName leadingIdent, View view, HeapTable table, TriggerActions action, OutputPartnerScope partners)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            return null;
        var output = TryParseOutputClauseForMutation(context, table, allowInserted: action != TriggerActions.Delete, allowDeleted: true, SingleBaseViewOutputShape(context.Batch, view, leadingIdent, table), partners: partners);
        RejectClientOutputOnTriggeredTarget(context.Batch, table, action, table.Name, output is { HasTarget: false });
        return output;
    }

    /// <summary>
    /// Resumes a joined write past the <c>FROM</c> clause it read ahead and
    /// binds its <c>WHERE</c>, with the join's <c>ON</c> predicates and the
    /// forced-seek checks, against the statement's sources.
    /// </summary>
    private static BooleanExpression? ParseJoinedViewTargetWhere(ParserContext context, ParserContext.Checkpoint afterFrom, FromSource[] sources, JoinSpec[] joins, Func<MultiPartName, SqlType> tupleTypeResolver)
    {
        context.RestoreCheckpoint(afterFrom);
        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
            context.MoveNextRequired();
            where = Selection.ParseAndBindPredicate(context, tupleTypeResolver, sources, joins);
        }
        BindJoinPredicatesWhileReporting(context.Batch, joins, tupleTypeResolver);
        Selection.ValidateForcedSeeks(context, sources, joins, where);
        return where;
    }

    /// <summary>
    /// The permission checks a joined write through a view makes: SELECT on the
    /// other sources it reads, then on the target the view's columns its
    /// <c>WHERE</c> and <c>SET</c> values read, and the write itself — column
    /// grain for an <c>UPDATE</c>, on the view, or, through a CTE, which is no
    /// securable, on the table it reaches — with a broken ownership chain
    /// checked as the single-target statement checks it.
    /// </summary>
    private static void CheckJoinedViewTargetPermissions(
        BatchContext batch,
        FromSource[] sources,
        int targetIndex,
        View view,
        HeapTable table,
        TriggerActions action,
        List<(string? ColumnName, Expression Expr)>? rawAssignments)
    {
        if (batch.IsSkipping)
            return;
        var verb = action == TriggerActions.Delete ? "DELETE" : "UPDATE";
        CheckJoinedReadSources(batch, sources, targetIndex);
        if (sources[targetIndex].ViaSynonym is { } synonym)
        {
            PermissionEnforcement.CheckSchemaObject(batch, verb, synonym);
            return;
        }
        if (view.UnstoredBody is not null)
        {
            PermissionEnforcement.CheckSchemaObject(batch, verb, table);
            return;
        }
        if (!PermissionEnforcement.Applies(batch, batch.DatabaseFor(view)))
        {
            CheckBrokenChainMutation(batch, view, action, where: null, rawAssignments);
            return;
        }
        var read = new ColumnReadTarget(view);
        CollectTargetColumnReads(sources, targetIndex, where: null, rawAssignments, read.Add);
        PermissionEnforcement.CheckColumns(batch, Permission.Select, read);
        if (rawAssignments is null)
        {
            PermissionEnforcement.CheckSchemaObject(batch, verb, view);
        }
        else
        {
            var assigned = new ColumnReadTarget(view);
            foreach (var columnName in SetColumnNames(rawAssignments))
                assigned.Add(columnName);
            PermissionEnforcement.CheckColumns(batch, Permission.Update, assigned);
        }
        CheckBrokenChainMutation(batch, view, action, where: null, rawAssignments);
    }

    /// <summary>
    /// Hands <paramref name="add"/> each column of the target source the
    /// <c>WHERE</c> and <c>SET</c> values of a joined write read.
    /// </summary>
    private static void CollectTargetColumnReads(
        FromSource[] sources,
        int targetIndex,
        BooleanExpression? where,
        List<(string? ColumnName, Expression Expr)>? rawAssignments,
        Action<string> add)
    {
        void Visit(MultiPartName name)
        {
            if (Selection.TryResolveSourceColumn(sources, name) is { } resolved && resolved.Source == targetIndex)
                add(sources[targetIndex].ColumnNames[resolved.Column]);
        }
        where?.VisitOperandExpressions(operand => operand.VisitColumnReferences(Visit));
        if (rawAssignments is null)
            return;
        foreach (var (_, expr) in rawAssignments)
            expr.VisitColumnReferences(Visit);
    }

    /// <summary>
    /// The rows a joined write's single-base view target yields, standing in
    /// the target source's slot, with the base row each shows and that row's
    /// image as it was read.
    /// </summary>
    private sealed class ViewTargetRows(BatchContext batch, View view, HeapTable table, FromSource source)
    {
        /// <summary>Each encoded view row's base address, by reference.</summary>
        public readonly Dictionary<byte[], (int Page, int Slot)> Addresses = new(ReferenceEqualityComparer.Instance);

        /// <summary>The base row's image each address was judged on.</summary>
        public readonly Dictionary<(int Page, int Slot), byte[]> Images = [];

        /// <summary>Each address's view row, for the derived columns a row limit or window computed.</summary>
        public readonly Dictionary<(int Page, int Slot), SqlValue[]> Rows = [];

        /// <summary>Reads a derived view column off a base row; built on first use.</summary>
        private Func<SqlValue[], int, SqlValue>? readDerived;

        /// <summary>
        /// An <c>APPLY</c>'s correlated body (<see cref="View.IsCorrelated"/>),
        /// whose rows the join yields as it runs the body against each left
        /// row, each recorded against its base address in the statement's
        /// <see cref="StatementContext.RowAddresses"/> map.
        /// </summary>
        public readonly bool Correlated = view.IsCorrelated;

        /// <summary>The base address the target slot's <paramref name="viewRow"/> shows.</summary>
        public bool TryAddress(byte[] viewRow, out (int Page, int Slot) address) =>
            this.Addresses.TryGetValue(viewRow, out address)
            || (this.Correlated && batch.CurrentStatement.RowAddresses is { } map && map.TryGet(viewRow, out address));

        /// <summary>The image the base row at <paramref name="address"/> is judged on, read when the join first reaches it.</summary>
        public byte[]? ImageAt((int Page, int Slot) address)
        {
            if (this.Images.TryGetValue(address, out var image))
                return image;
            if (!this.Correlated || table.Heap.ReadLiveRow(address.Page, address.Slot) is not { } live)
                return null;
            this.Images[address] = live;
            return live;
        }

        /// <summary>
        /// The view row the base row at <paramref name="address"/> shows as
        /// <paramref name="baseImage"/>, encoded for the target slot: each
        /// direct column read off the image, a derived one computed from it —
        /// a window's kept from the row the body yielded, since one row alone
        /// can't settle it. Null when the image no longer passes the view's
        /// filter.
        /// </summary>
        public byte[]? ViewRowOf((int Page, int Slot) address, byte[] baseImage)
        {
            var baseValues = DecodeFullRow(table, baseImage);
            EvaluateComputedColumns(table, baseValues, batch);
            if (view.VisibilityCheck is { } isVisible && !isVisible(baseValues, batch))
                return null;
            var values = new SqlValue[source.Columns.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = view.BaseColumnOrdinals[i] is var ordinal and >= 0 ? baseValues[ordinal]
                    : view.IsWindowed && this.Rows.TryGetValue(address, out var row) ? row[i]
                    : (this.readDerived ??= SingleBaseViewReader(batch, view, table))(baseValues, i);
            }
            return EncodeFor(source, values);
        }
    }

    /// <summary>
    /// Reads a joined write's single-base view target once, as the view yields
    /// its rows — its filter, row limit and window applied — each carrying the
    /// address of the base row it shows, and stands those rows in the target
    /// source's slot. A row whose base image no longer reads as the body read
    /// it (another session wrote it in between) is shown as the image reads.
    /// </summary>
    /// <remarks>
    /// Real reads the target under U, so its scan waits in U on a row another
    /// session is writing (probed 2026-10-01 against SQL Server 2025: a joined
    /// UPDATE or DELETE through a view waits <c>LCK_M_U</c> on the writer's
    /// key). The body's own read would wait in S, so the rows other sessions
    /// hold — and those their in-flight deletes and rewrites hid, whose prior
    /// image the view shows — are waited out in U first, the same outcome as
    /// real's wait at the row's turn, moved earlier within the statement.
    /// </remarks>
    private static ViewTargetRows MaterializeViewTarget(BatchContext batch, View view, HeapTable table, FromSource[] sources, int targetIndex)
    {
        if (!table.SupersededKeyImages.IsEmptyLockFree())
        {
            _ = AwaitSupersededTargetRows(batch, table, (_, prior) =>
            {
                var priorValues = DecodeFullRow(table, prior);
                EvaluateComputedColumns(table, priorValues, batch);
                return view.VisibilityCheck?.Invoke(priorValues, batch) != false;
            });
        }
        if (Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActiveUpdateLocks) != 0)
        {
            foreach (var (page, slot, bytes) in table.Heap.EnumerateRowsWithAddress())
            {
                var rowBytes = bytes;
                _ = batch.AwaitTargetRowWriters(table, page, slot, ref rowBytes);
            }
        }

        var source = sources[targetIndex];
        var target = new ViewTargetRows(batch, view, table, source);
        if (target.Correlated)
        {
            batch.CurrentStatement.RowAddresses ??= new();
            return target;
        }
        var rows = new List<byte[]>();
        foreach (var (row, address) in ViewRowsWithAddresses(batch, view))
        {
            if (address is not { } at || table.Heap.ReadLiveRow(at.Page, at.Slot) is not { } image)
                continue;
            target.Rows[at] = row;
            target.Images[at] = image;
            var baseValues = DecodeFullRow(table, image);
            var bytes = DirectColumnsMatch(view, row, baseValues) ? EncodeFor(source, row) : target.ViewRowOf(at, image);
            if (bytes is null)
                continue;
            target.Addresses[bytes] = at;
            rows.Add(bytes);
        }
        sources[targetIndex] = source.WithMaterializedRows(rows);
        return target;
    }

    /// <summary>Whether a view row's direct columns read as <paramref name="baseValues"/> does.</summary>
    private static bool DirectColumnsMatch(View view, SqlValue[] viewRow, SqlValue[] baseValues)
    {
        for (var i = 0; i < view.BaseColumnOrdinals.Length; i++)
        {
            if (view.BaseColumnOrdinals[i] is var ordinal and >= 0
                && (viewRow[i].IsNull != baseValues[ordinal].IsNull || (!viewRow[i].IsNull && !viewRow[i].Equals(baseValues[ordinal]))))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A view row's leading values encoded for <paramref name="source"/>'s slot, each converted to its column's type.</summary>
    private static byte[] EncodeFor(FromSource source, SqlValue[] values)
    {
        var columns = source.StoredSchema;
        var encoded = new SqlValue[columns.Length];
        for (var i = 0; i < encoded.Length; i++)
        {
            var value = values[i];
            encoded[i] = value.Type == columns[i].Type ? value
                : value.IsNull ? SqlValue.Null(columns[i].Type)
                : value.CoerceTo(columns[i].Type);
        }
        return RowEncoder.EncodeRow(columns, encoded);
    }
}
