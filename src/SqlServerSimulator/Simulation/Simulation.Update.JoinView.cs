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
    /// <para>
    /// Each level's body is re-parsed here rather than captured at CREATE
    /// VIEW: its <see cref="ViewUpdatabilityProfile"/> carries live
    /// <see cref="FromSource"/> row enumerators, and the read path re-parses
    /// per reference for the same reason.
    /// </para>
    /// <para>
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
    /// </para>
    /// </remarks>
    private static SimulatedStatementOutcome ExecuteJoinViewUpdate(
        ParserContext context,
        MultiPartName targetName,
        View view,
        List<(string ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top)
    {
        var batch = context.Batch;
        var chain = BuildJoinViewChain(batch, view);
        var sources = chain.Sources;
        var (targetIndex, assignments) = ResolveJoinViewSetTargets(batch, chain, rawAssignments);
        var table = sources[targetIndex].BackingTable
            ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(chain.TargetName);

        FunctionBodyShape.NoteTableWrite(batch, "UPDATE", table);
        RejectDisabledClusteredIndex(table);
        RejectIncorrectSetOptionsForWrite(table, batch, "UPDATE");
        _ = batch.AcquireDataLockIfApplicable(table, default, isWrite: true);

        // Compile-time bind of the SET values and the predicate against the
        // view's own output columns — same contract as the single-base path,
        // so an unknown column or an unresolved collation reports before any
        // row is read.
        var typeResolver = Selection.ViewOutputColumnTypeResolver(batch, view);
        foreach (var (_, expr) in rawAssignments)
            UnresolvedCollation.RequireAssignable(expr.GetSqlType(batch, typeResolver));

        BooleanExpression? where = null;
        PositionedCursorTarget? positionedCursor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Current })
                positionedCursor = ParseWhereCurrentOf(context, table, [.. rawAssignments.Select(a => a.ColumnName)], view);
            else
                where = Selection.ParseAndBindPredicate(context, typeResolver);
        }

        CheckUpdatePermissions(context, targetName, table, view, rawAssignments, where);

        var targetAddresses = new Dictionary<byte[], (int Page, int Slot)>(ReferenceEqualityComparer.Instance);
        sources[targetIndex] = WrapSourceWithAddressTracking(sources[targetIndex], table, targetAddresses);

        var seen = new HashSet<(int Page, int Slot)>();
        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();

        // Hoisted scaffolding: one mutable tuple slot, one resolver per level,
        // one RuntimeContext each reused across the loop — see CLAUDE.md's
        // per-row resolver contract.
        byte[]?[] tuple = [];
        Func<MultiPartName, SqlValue> resolveTuple = null!;
        resolveTuple = name => ResolveAcrossMutationTuple(sources, tuple, name, batch, resolveTuple);
        var (resolvers, belowRuntimes) = BuildChainResolvers(batch, chain, resolveTuple);
        var resolveOutput = resolvers[^1];
        var runtime = new RuntimeContext(resolveOutput, batch);
        var topLevel = chain.Views.Length - 1;
        var checkLevel = HighestCheckOptionLevel(chain);

        // Skip mode commits nothing, so the walk is pure cost — and running
        // the body's WHERE / the SET list against live rows can raise on
        // behalf of a statement that never runs. Everything a CREATE-time
        // bind needs was resolved above.
        var tuples = batch.IsSkipping
            ? []
            : Selection.EnumerateJoinedRows(sources, chain.Joins, batch, outerResolver: null);
        foreach (var candidate in tuples)
        {
            tuple = candidate;

            // Every level's own WHERE decides which join tuples that level
            // shows, so together they gate candidacy exactly as the composed
            // VisibilityCheck does on the single-base path.
            if (!ChainLevelsPass(chain, belowRuntimes, topLevel))
                continue;

            var targetBytes = candidate[targetIndex];
            if (targetBytes is null)
                continue;
            if (!targetAddresses.TryGetValue(targetBytes, out var address))
                continue;
            if (positionedCursor is { } positioned && !CursorRowMatches(positioned, address))
                continue;
            if (where is not null && where.Run(runtime) != true)
                continue;
            if (!seen.Add(address))
                continue;

            var fullValues = DecodeFullRow(table, targetBytes);
            EvaluateComputedColumns(table, fullValues, batch);

            // Per-row stamp bump for NEXT VALUE FOR in the SET-list.
            batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, resolveOutput);

            if (checkLevel >= 0 && !ChainRowRemainsVisible(batch, chain, sources, targetIndex, table, newValues, checkLevel))
                throw SimulatedSqlException.ViewCheckOptionViolation();

            // The view's own INSTEAD OF triggers were refused up front, so
            // the only one that can claim the write is the base table's.
            var oldSnapshotNeeded = HasAfterTrigger(batch, table, TriggerActions.Update)
                || HasInsteadOfTrigger(batch, table, TriggerActions.Update)
                || table.SystemVersioning is not null
                || table.IncomingForeignKeys.Count > 0;
            affected.Add((address.Page, address.Slot, newValues, oldSnapshotNeeded ? fullValues : null));
        }

        ApplyDmlTopCap(top, affected, batch);

        return CommitUpdate(context, table, affected, output: null, [.. assignments.Select(a => a.Ordinal)]);
    }

    /// <summary>
    /// Binds an UPDATE's SET list to one of the chain's bottom base tables.
    /// Each column name descends the levels to the (source, column) it
    /// eventually reads: a level whose projection isn't a direct column
    /// reference is <strong>Msg 4406</strong> and targets landing in more than
    /// one source are <strong>Msg 4405</strong>, each raised as the walk meets
    /// it — so a list whose earlier pair already spans two base tables reports
    /// 4405 even when a later entry names a derived column (probe-confirmed).
    /// </summary>
    private static (int TargetIndex, List<(int Ordinal, Expression Expr)> Assignments) ResolveJoinViewSetTargets(
        BatchContext batch,
        JoinViewChain chain,
        List<(string ColumnName, Expression Expr)> rawAssignments)
    {
        var assignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
        var targetIndex = -1;

        foreach (var (columnName, expr) in rawAssignments)
        {
            var (sourceIndex, columnIndex) = DescendToBaseColumn(batch, chain, columnName);
            if (targetIndex >= 0 && targetIndex != sourceIndex)
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(chain.TargetName);
            targetIndex = sourceIndex;

            if (chain.Sources[sourceIndex].BackingTable is { } backing)
                RejectUnmodifiableSetTarget(backing, columnIndex, batch.DatabaseFor(backing));
            assignments.Add((columnIndex, expr));
        }

        return targetIndex < 0
            ? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(chain.TargetName)
            : (targetIndex, assignments);
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
