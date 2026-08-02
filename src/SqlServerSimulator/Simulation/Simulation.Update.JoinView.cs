using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// UPDATE through a view whose body reads several sources. Real SQL
    /// Server accepts one as long as the SET list lands entirely in a single
    /// base table (probe-confirmed against SQL Server 2025); a SET list
    /// spanning two is <strong>Msg 4405</strong>, and a SET target that isn't
    /// a direct column projection is <strong>Msg 4406</strong>, which wins
    /// over 4405 whatever order the two appear in the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is re-parsed here rather than captured at CREATE VIEW: its
    /// <see cref="ViewUpdatabilityProfile"/> carries live
    /// <see cref="FromSource"/> row enumerators, and the read path re-parses
    /// per reference for the same reason.
    /// </para>
    /// <para>
    /// Execution mirrors <c>ExecuteJoinedUpdate</c>'s shape — join tuples,
    /// an address side-channel on the target source, dedupe by (page, slot)
    /// — with two differences that come from writing through a view: the
    /// statement's WHERE and SET expressions name the view's <em>output</em>
    /// columns, so they resolve by evaluating that column's projection
    /// against the tuple, and the body's own WHERE gates which tuples are
    /// candidates. Dedupe is what makes a base row that surfaces in several
    /// join tuples update once, off the first tuple that reaches it —
    /// probe-confirmed (a 1-side row joined to three rows takes the SET once
    /// and reports <c>@@ROWCOUNT</c> 1).
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
        var viewName = $"{view.Schema.Name}.{view.Name}";
        var body = batch.Connection.Simulation.ParseViewBodyPlan(batch, view);
        if (body.UpdatabilityProfile is not { } profile || profile.Sources.Length < 2)
            throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);

        var sources = (FromSource[])profile.Sources.Clone();
        var (targetIndex, assignments) = ResolveJoinViewSetTargets(batch, view, profile, sources, rawAssignments);
        var table = sources[targetIndex].BackingTable
            ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);

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

        // Hoisted scaffolding: one mutable tuple slot, one resolver pair, one
        // RuntimeContext reused across the loop — see CLAUDE.md's per-row
        // resolver contract.
        byte[]?[] tuple = [];
        SqlValue ResolveBody(MultiPartName name) => ResolveAcrossMutationTuple(sources, tuple, name);
        Func<MultiPartName, SqlValue> resolveBody = ResolveBody;
        SqlValue ResolveOutput(MultiPartName name) => ResolveViewOutputColumn(batch, view, profile, name, resolveBody);
        Func<MultiPartName, SqlValue> resolveOutput = ResolveOutput;
        var runtime = new RuntimeContext(resolveOutput, batch);
        var bodyRuntime = new RuntimeContext(resolveBody, batch);

        // Skip mode commits nothing, so the walk is pure cost — and running
        // the body's WHERE / the SET list against live rows can raise on
        // behalf of a statement that never runs. Everything a CREATE-time
        // bind needs was resolved above.
        var tuples = batch.IsSkipping
            ? []
            : Selection.EnumerateJoinedRows(sources, profile.Joins, batch, outerResolver: null);
        foreach (var candidate in tuples)
        {
            tuple = candidate;

            // The body's own WHERE decides which join tuples the view shows,
            // so it gates candidacy exactly as VisibilityCheck does on the
            // single-base path.
            if (!AllExcludersPass(profile.Excluders, bodyRuntime))
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

            if (view.WithCheckOption && !JoinViewRowRemainsVisible(profile, sources, targetIndex, table, newValues, batch))
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
    /// Binds an UPDATE's SET list to one of a multi-source view body's base
    /// tables. Each column name resolves to the view's output ordinal, then
    /// through that ordinal's projection to the (source, column) it reads:
    /// a projection that isn't a direct column reference is
    /// <strong>Msg 4406</strong> (raised as it is met, so it beats 4405
    /// whatever the SET order), and targets landing in more than one source
    /// are <strong>Msg 4405</strong>.
    /// </summary>
    private static (int TargetIndex, List<(int Ordinal, Expression Expr)> Assignments) ResolveJoinViewSetTargets(
        BatchContext batch,
        View view,
        ViewUpdatabilityProfile profile,
        FromSource[] sources,
        List<(string ColumnName, Expression Expr)> rawAssignments)
    {
        var viewName = $"{view.Schema.Name}.{view.Name}";
        var collation = batch.CurrentDatabase.Collation;
        var assignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
        var targetIndex = -1;

        foreach (var (columnName, expr) in rawAssignments)
        {
            var outputOrdinal = IndexOfViewOutputColumn(collation, view, columnName);
            if (outputOrdinal < 0)
                throw SimulatedSqlException.InvalidColumnName(columnName);
            if (UnwrapDirectRef(profile.Projections[outputOrdinal]) is not { ReferencedName: { } referenced })
                throw SimulatedSqlException.ViewDmlTouchesDerivedField(viewName);

            var (sourceIndex, columnIndex) = Selection.FindSourceColumn(sources, referenced);
            if (sourceIndex < 0)
                throw SimulatedSqlException.InvalidColumnName(referenced);
            if (targetIndex >= 0 && targetIndex != sourceIndex)
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);
            targetIndex = sourceIndex;

            if (sources[sourceIndex].BackingTable is { } backing)
                RejectUnmodifiableSetTarget(backing, columnIndex, batch.DatabaseFor(backing));
            assignments.Add((columnIndex, expr));
        }

        return targetIndex < 0
            ? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName)
            : (targetIndex, assignments);
    }

    /// <summary>
    /// Resolves a reference written against a multi-source view — the leaf
    /// names one of the view's output columns, whose value is that column's
    /// projection evaluated over the current join tuple. Evaluating the
    /// projection (rather than mapping to a base column) is what lets a
    /// derived output column be read in the statement's WHERE, which real
    /// allows even though writing one is Msg 4406.
    /// </summary>
    private static SqlValue ResolveViewOutputColumn(
        BatchContext batch,
        View view,
        ViewUpdatabilityProfile profile,
        MultiPartName name,
        Func<MultiPartName, SqlValue> resolveBody)
    {
        var ordinal = IndexOfViewOutputColumn(batch.CurrentDatabase.Collation, view, name.Leaf);
        return ordinal < 0
            ? throw SimulatedSqlException.InvalidColumnName(name)
            : profile.Projections[ordinal].Run(new RuntimeContext(resolveBody, batch));
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

    /// <summary>
    /// <c>WITH CHECK OPTION</c> for a multi-source view: whether the
    /// post-update base row still surfaces through the view — it must both
    /// find a join partner and pass the body's WHERE. Re-runs the join with
    /// the target source narrowed to that one row, so a row that changed
    /// which partner it matches is judged on its new partner (real accepts
    /// exactly that — probe-confirmed).
    /// </summary>
    /// <remarks>
    /// The probe row is encoded with no LOB store, which keeps every value
    /// inline and allocates no off-row chain for a row that may never be
    /// written; the decoder reads inline values without a store. Its ceiling
    /// is the encoder's 65535-byte var-offset cap rather than the heap's
    /// off-row spill.
    /// </remarks>
    private static bool JoinViewRowRemainsVisible(
        ViewUpdatabilityProfile profile,
        FromSource[] sources,
        int targetIndex,
        HeapTable table,
        SqlValue[] newValues,
        BatchContext batch)
    {
        var original = sources[targetIndex];
        var probeSources = (FromSource[])sources.Clone();
        probeSources[targetIndex] = new FromSource(
            qualifier: original.Qualifier,
            columnNames: original.ColumnNames,
            columns: original.Columns,
            storedSchema: original.StoredSchema,
            storageOrdinals: original.StorageOrdinals,
            lobStore: null,
            rows: [RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, newValues))],
            backingTable: original.BackingTable);

        byte[]?[] tuple = [];
        SqlValue Resolve(MultiPartName name) => ResolveAcrossMutationTuple(probeSources, tuple, name);
        var runtime = new RuntimeContext(Resolve, batch);
        foreach (var candidate in Selection.EnumerateJoinedRows(probeSources, profile.Joins, batch, outerResolver: null))
        {
            tuple = candidate;
            if (candidate[targetIndex] is not null && AllExcludersPass(profile.Excluders, runtime))
                return true;
        }
        return false;
    }
}
