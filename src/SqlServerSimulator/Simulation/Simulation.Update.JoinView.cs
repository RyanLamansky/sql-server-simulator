using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// UPDATE through a view whose chain bottoms out in a body reading several
    /// sources. Real SQL Server accepts one as long as the SET list lands
    /// entirely in a single base table (probe-confirmed against SQL Server
    /// 2025); a SET list spanning two is <strong>Msg 4405</strong> and a SET
    /// target that isn't a direct column projection is
    /// <strong>Msg 4406</strong>, each reported as the left-to-right walk of
    /// the list meets it.
    /// </summary>
    /// <remarks>
    /// Each level's body is re-parsed here rather than captured at CREATE
    /// VIEW: its <see cref="ViewUpdatabilityProfile"/> carries live
    /// <see cref="FromSource"/> row enumerators, and the read path re-parses
    /// per reference for the same reason. The walk is
    /// <see cref="RunJoinViewUpdate"/>'s.
    /// </remarks>
    private static SimulatedStatementOutcome ExecuteJoinViewUpdate(
        ParserContext context,
        MultiPartName targetName,
        View view,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top)
    {
        var batch = context.Batch;
        var chain = BuildJoinViewChain(batch, view, targetName.ToString());
        var (path, assignments) = ResolveJoinViewSetTargets(batch, chain, rawAssignments, targetName.ToString());
        var table = chain.TableAt(path)
            ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(targetName.ToString());

        BindDeferredXmlMutators(context, table, rawAssignments, targetName.ToString());
        FunctionBodyShape.NoteTableWrite(batch, "UPDATE", table);
        LockWriteTable(batch, table, "UPDATE", checkFilegroup: true);

        // Compile-time bind of the SET values and the predicate against the
        // view's own output columns — same contract as the single-base path,
        // so an unknown column or an unresolved collation reports before any
        // row is read.
        var typeResolver = Selection.ViewOutputColumnTypeResolver(batch.CurrentDatabase, view);
        foreach (var (_, expr) in rawAssignments)
            UnresolvedCollation.RequireAssignable(expr.GetSqlType(batch, typeResolver));

        // OUTPUT binds against the view's columns now that the SET list has
        // named the table written; INSERTED may name only the columns reading
        // nothing else (Msg 404), while DELETED reads each row's own join
        // tuple as it stood.
        OutputProjection? output = null;
        Dictionary<SqlValue[], byte[]?[]>? tuplesByRow = null;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
        {
            tuplesByRow = new(ReferenceEqualityComparer.Instance);
            var shape = JoinViewOutputShape(batch, ViewColumnsFor(batch, view, targetName), chain, chain.Sources, path, table, tuplesByRow);
            output = TryParseOutputClauseForMutation(context, table, allowInserted: true, allowDeleted: true, shape);
            RejectClientOutputOnTriggeredTarget(batch, table, TriggerActions.Update, table.Name, output is { HasTarget: false });
        }
        // A FROM clause here is one the statement couldn't read ahead, which
        // raises its own error read in place.
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            Selection.ParseSourcesAndJoins(context, QueryScope.Statement, [], []);
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        BooleanExpression? where = null;
        PositionedCursorTarget? positionedCursor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Current })
                positionedCursor = ParseWhereCurrentOf(context, table, [.. SetColumnNames(rawAssignments)], view);
            else
                where = Selection.ParseAndBindPredicate(context, typeResolver);
        }

        CheckUpdatePermissions(context, targetName, table, view, rawAssignments, where);
        var readViewColumns = new List<string>();
        where?.VisitOperandExpressions(operand => operand.VisitColumnReferences(name => readViewColumns.Add(name.Leaf)));
        foreach (var (_, expr) in rawAssignments)
            expr.VisitColumnReferences(name => readViewColumns.Add(name.Leaf));
        CheckJoinViewBrokenChains(batch, chain, table, readViewColumns, assignments);

        return RunJoinViewUpdate(context, chain, path, table, assignments, where, output, tuplesByRow, top, positionedCursor);
    }

    /// <summary>
    /// The row walk and commit of an UPDATE through a join view, shared by the
    /// statement naming the view and the joined statement naming it in its
    /// <c>FROM</c> clause (<paramref name="chain"/> a chain of no levels, whose
    /// <c>WHERE</c> and <c>SET</c> read the tuple directly).
    /// </summary>
    /// <remarks>
    /// Execution mirrors <c>ExecuteJoinedUpdate</c>'s shape — join tuples,
    /// an address side-channel on the target source, dedupe by (page, slot)
    /// — with two differences that come from writing through a view: the
    /// statement's WHERE and SET expressions name the view's <em>output</em>
    /// columns, so they resolve by evaluating that column's projection
    /// against the tuple (through as many levels as the chain has), and every
    /// level's own WHERE gates which tuples are candidates. Dedupe is what
    /// makes a base row that surfaces in several join tuples update once, off
    /// the first tuple that reaches it — probe-confirmed (a 1-side row joined
    /// to three rows takes the SET once and reports <c>@@ROWCOUNT</c> 1).
    /// </remarks>
    private static SimulatedStatementOutcome RunJoinViewUpdate(
        ParserContext context,
        JoinViewChain chain,
        int[] path,
        HeapTable table,
        List<(int Ordinal, Expression Expr)> assignments,
        BooleanExpression? where,
        OutputProjection? output,
        Dictionary<SqlValue[], byte[]?[]>? tuplesByRow,
        Selection.DmlTopLimit? top,
        PositionedCursorTarget? positionedCursor)
    {
        var batch = context.Batch;
        var seen = new HashSet<(int Page, int Slot)>();
        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();

        // The view's own INSTEAD OF triggers took their own path, so the only
        // one that can claim the write is the base table's.
        var oldSnapshotNeeded = UpdateNeedsOldRows(batch, table, table, output);

        // Hoisted scaffolding: one mutable tuple slot, one resolver per level,
        // one RuntimeContext each reused across the loop — see CLAUDE.md's
        // per-row resolver contract. A chain of no levels reads the tuple.
        byte[]?[] tuple = [];
        var sources = chain.Sources;
        Dictionary<byte[], byte[]?[]>[] tupleMaps = [];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, tuple, name, batch);
        var (resolvers, belowRuntimes) = BuildChainResolvers(batch, chain, resolveTuple);
        var resolveOutput = resolvers.Length == 0 ? resolveTuple : resolvers[^1];
        var runtime = new RuntimeContext(resolveOutput, batch);
        var topLevel = chain.Views.Length - 1;
        var checksOption = chain.HasCheckOptionAlong(path);

        // Skip mode commits nothing, so the walk is pure cost — and running
        // the body's WHERE / the SET list against live rows can raise on
        // behalf of a statement that never runs. Everything a CREATE-time
        // bind needs was resolved by the caller.
        if (batch.IsSkipping)
            return CommitUpdate(context, table, affected, output, [.. SetColumnOrdinals(assignments)], rowsLocked: true);
        if (positionedCursor is null && !table.SupersededKeyImages.IsEmptyLockFree())
            _ = AwaitSupersededTargetRows(batch, table, (_, prior) => QualifyingTuple(prior) is not null);

        // The target source records each row's heap address as it reads it; a
        // target under a nested join view is reached through that view's
        // rows, computed here, after the waits above.
        var targetAddresses = new RowAddressMap();
        var rowMaps = new Dictionary<byte[], byte[]?[]>[path.Length - 1];
        sources = SourcesAlongPath(batch, chain, path, 0, original => original.AsWriteTarget(targetAddresses), rowMaps);
        // A target among the chain's own sources narrows, seeks and reorders
        // as a joined UPDATE's does, by the filters every level and the
        // statement apply, rebound onto the join's sources.
        var joins = chain.Joins;
        var walkPath = path;
        if (path.Length == 1 && chain.Views.Length != 0)
        {
            var targetSlot = path[0];
            sources = Selection.PrepareMutationJoinSources(
                sources, ref joins, JoinViewNarrowing(batch, chain, where), ref targetSlot,
                MutationMayReorder(top, batch) && tuplesByRow is null, batch);
            walkPath = [targetSlot];
        }
        // A joined statement's OUTPUT may read its own other sources.
        var partners = output is { ReadsPartners: true } ? new OutputPartnerRows(sources) : null;
        foreach (var candidate in Selection.EnumerateJoinedRows(sources, joins, batch, outerResolver: null))
        {
            tuple = candidate;
            tupleMaps = rowMaps;

            if (TuplePasses(walkPath) is not { } targetBytes)
                continue;
            if (!targetAddresses.TryGet(targetBytes, out var address))
                continue;
            if (positionedCursor is { } positioned && !CursorRowMatches(positioned, address))
                continue;
            if (where is not null && where.Run(runtime) != true)
                continue;
            if (!seen.Add(address))
                continue;

            // Judged as another session's write leaves it, as a joined
            // UPDATE's are.
            var rowBytes = targetBytes;
            if (!batch.AwaitTargetRowWriters(table, address.Page, address.Slot, ref rowBytes))
                continue;
            var judged = ReferenceEquals(rowBytes, targetBytes) || rowBytes.AsSpan().SequenceEqual(targetBytes)
                ? JudgeTuple(targetBytes)
                : Rejudge(rowBytes);
            if (judged is not { } entry)
                continue;
            affected.Add((address.Page, address.Slot, entry.NewValues, entry.OldSnapshot));
            judgedRows.Add((address.Page, address.Slot, rowBytes));
            partners?.Note(address, tuple);
        }

        ApplyDmlTopCap(top, affected, batch);
        HoldQualifyingRows(batch, table, affected, judgedRows, walkGeneration, RowLockPurpose.UpdatePreImage, (i, rowBytes) =>
        {
            if (Rejudge(rowBytes) is not { } judged)
                return false;
            affected[i] = (affected[i].PageIndex, affected[i].SlotIndex, judged.NewValues, judged.OldSnapshot);
            partners?.Note((affected[i].PageIndex, affected[i].SlotIndex), tuple);
            return true;
        });

        return CommitUpdate(context, table, affected, output, [.. SetColumnOrdinals(assignments)], rowsLocked: true, partners: partners);

        // The target row the current tuple shows when every level's own WHERE
        // passes it — together they gate candidacy exactly as the composed
        // VisibilityCheck does on the single-base path — else null.
        byte[]? TuplePasses(int[] tuplePath) =>
            ChainLevelsPass(chain, belowRuntimes, topLevel) ? TargetBytesAlongPath(tuple, tuplePath, tupleMaps) : null;

        // The target row of the current tuple's new values, and its old image
        // when something reads that.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot) JudgeTuple(byte[] targetBytes)
        {
            var fullValues = DecodeFullRow(table, targetBytes);
            EvaluateComputedColumns(table, fullValues, batch);

            // Per-row stamp bump for NEXT VALUE FOR in the SET-list.
            batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, resolveOutput);

            if (checksOption && !PathRowRemainsVisible(batch, chain, path, table, newValues))
                throw SimulatedSqlException.ViewCheckOptionViolation();

            // DELETED reads the row's join as it stood: the view's own tuple,
            // or under a statement's FROM clause the tuple of the view its
            // target source shows.
            if (tuplesByRow is { } recordedTuples)
                recordedTuples[fullValues] = (byte[]?[])(topLevel < 0 ? tupleMaps[0][tuple[path[0]]!] : tuple).Clone();
            return (newValues, oldSnapshotNeeded ? fullValues : null);
        }

        // The target row as rowBytes, judged again against its partners.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot)? Rejudge(byte[] rowBytes) =>
            QualifyingTuple(rowBytes) is { } targetBytes ? JudgeTuple(targetBytes) : null;

        // The target row as rowBytes when every level and the WHERE still show
        // it with some partner, the first such tuple current; else null.
        byte[]? QualifyingTuple(byte[] rowBytes)
        {
            var narrowedMaps = new Dictionary<byte[], byte[]?[]>[path.Length - 1];
            var narrowed = SourcesAlongPath(batch, chain, path, 0, original => SingleRowSource(original, rowBytes, original.LobStore), narrowedMaps);
            foreach (var candidate in Selection.EnumerateJoinedRows(narrowed, chain.Joins, batch, outerResolver: null))
            {
                tuple = candidate;
                tupleMaps = narrowedMaps;
                if (TuplePasses(path) is { } targetBytes && (where is null || where.Run(runtime) == true))
                    return targetBytes;
            }
            return null;
        }
    }

    /// <summary>
    /// Binds an UPDATE's SET list to one of the chain's bottom base tables —
    /// for a chain of no levels, descending from its source
    /// <paramref name="targetSource"/>.
    /// Each column name descends the levels to the (source, column) it
    /// eventually reads: a level whose projection isn't a direct column
    /// reference is <strong>Msg 4406</strong> and targets landing in more than
    /// one source are <strong>Msg 4405</strong>, each raised as the walk meets
    /// it — so a list whose earlier pair already spans two base tables reports
    /// 4405 even when a later entry names a derived column (probe-confirmed).
    /// </summary>
    private static (int[] Path, List<(int Ordinal, Expression Expr)> Assignments) ResolveJoinViewSetTargets(
        BatchContext batch,
        JoinViewChain chain,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        string writtenName,
        int targetSource = -1)
    {
        var assignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
        int[]? path = null;

        foreach (var (columnName, expr) in rawAssignments)
        {
            if (columnName is null)
            {
                assignments.Add((-1, expr));
                continue;
            }
            var (columnPath, columnIndex) = DescendToBaseColumn(batch, chain, columnName, targetSource);
            if (path is not null && !path.AsSpan().SequenceEqual(columnPath))
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(writtenName, chain.TargetIsDerivedTable);
            path = columnPath;

            if (chain.TableAt(columnPath) is { } backing)
                RejectUnmodifiableSetTarget(backing, columnIndex, batch.DatabaseFor(backing));
            assignments.Add((columnIndex, expr));
        }

        return path is null
            ? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(writtenName, chain.TargetIsDerivedTable)
            : (path, assignments);
    }

    private static int IndexOfViewOutputColumn(Collation collation, View view, string columnName)
    {
        for (var i = 0; i < view.OutputColumns.Length; i++)
        {
            if (collation.Equals(view.OutputColumns[i].Name, columnName))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Whether every excluder holds for the tuple <paramref name="runtime"/>
    /// resolves against, under WHERE's three-valued rule (UNKNOWN excludes).
    /// </summary>
    private static bool AllExcludersPass(BooleanExpression[] excluders, RuntimeContext runtime)
    {
        foreach (var excluder in excluders)
        {
            if (excluder.Run(runtime) != true)
                return false;
        }
        return true;
    }
}
