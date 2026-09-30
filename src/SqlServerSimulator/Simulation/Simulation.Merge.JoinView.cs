using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// How a <c>MERGE</c> into a join view reaches its one base table: the
    /// statement matches and acts on the view's own rows, and each action is
    /// carried back to the base row the view row shows.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-28 against SQL Server 2025. Every action must land in one
    /// base table — the <c>UPDATE SET</c> targets and the <c>INSERT</c> column
    /// list — and a <c>DELETE</c> removes the row from that table, or with no
    /// other action from the first table in the bottom view's <c>FROM</c>.
    /// </remarks>
    private sealed class JoinViewMergePlan(JoinViewChain chain, int[] path, HeapTable table, int[] viewToBase, bool[] assigned)
    {
        public readonly JoinViewChain Chain = chain;

        /// <summary>The bottom source path the write takes, as <see cref="DescendToBaseColumn"/> returns it.</summary>
        public readonly int[] Path = path;

        /// <summary>The base table every action writes.</summary>
        public readonly HeapTable Table = table;

        /// <summary>Per view column, the <see cref="Table"/> column it projects directly, or -1.</summary>
        public readonly int[] ViewToBase = viewToBase;

        /// <summary>Per view column, whether some <c>UPDATE SET</c> assigns it.</summary>
        public readonly bool[] Assigned = assigned;

        /// <summary>
        /// Per row of the view-shaped target heap, the <see cref="Table"/> row
        /// it shows; null where the view row is NULL-extended on that side.
        /// </summary>
        public readonly Dictionary<(int Page, int Slot), (int Page, int Slot)?> Addresses = [];

        public readonly bool ChecksOption = chain.HasCheckOptionAlong(path);
    }

    /// <summary>
    /// Settles which base table a <c>MERGE</c> into a join view writes, walking
    /// the <c>WHEN</c> clauses in order: a target naming another table than
    /// one met earlier is <strong>Msg 4405</strong>, a derived one
    /// <strong>Msg 4406</strong>, and so is an <c>INSERT</c> with no column
    /// list or <c>DEFAULT VALUES</c> (4405), each naming the view as written.
    /// </summary>
    private static JoinViewMergePlan PlanJoinViewMerge(BatchContext batch, View view, HeapTable viewRows, List<WhenClause> whenClauses, MultiPartName writtenName)
    {
        var written = writtenName.ToString();
        JoinViewChain chain;
        try
        {
            chain = BuildJoinViewChain(batch, view, written);
        }
        catch (SimulatedSqlException refused) when (refused.Number == 4405)
        {
            throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(written);
        }

        (int[] Path, int ColumnIndex) Descend(string columnName)
        {
            try
            {
                return DescendToBaseColumn(batch, chain, columnName);
            }
            catch (SimulatedSqlException derived) when (derived.Number == 4406)
            {
                throw SimulatedSqlException.ViewDmlTouchesDerivedField(written);
            }
        }

        int[]? path = null;
        void Route(int[] columnPath)
        {
            if (path is not null && !path.AsSpan().SequenceEqual(columnPath))
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(written);
            path = columnPath;
        }

        var assigned = new bool[viewRows.Columns.Length];
        foreach (var clause in whenClauses)
        {
            switch (clause.Action)
            {
                case MergeActionKind.Update:
                    foreach (var (ordinal, _) in clause.Assignments!)
                    {
                        if (ordinal < 0)
                            continue;
                        var (columnPath, columnIndex) = Descend(viewRows.Columns[ordinal].Name);
                        Route(columnPath);
                        if (chain.TableAt(columnPath) is { } backing)
                            RejectUnmodifiableSetTarget(backing, columnIndex, batch.DatabaseFor(backing));
                        assigned[ordinal] = true;
                    }
                    break;
                case MergeActionKind.Insert:
                    if (clause.InsertColumnsImplied)
                        throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(written);
                    foreach (var column in clause.InsertColumns!)
                    {
                        var (columnPath, columnIndex) = Descend(column.Name);
                        Route(columnPath);
                        if (chain.TableAt(columnPath)?.Columns[columnIndex] is { Computed: not null } computed)
                            throw SimulatedSqlException.ColumnCannotBeModified(computed.Name);
                    }
                    break;
            }
        }

        // A DELETE alone removes the row from the first table the bottom view
        // reads, down through a first source that is itself a join view.
        if (path is null)
        {
            var first = new List<int>();
            var current = chain;
            while (true)
            {
                first.Add(0);
                if (current.Sources[0] is not { BackingTable: null, BackingView: { } inner } || !ReadsThroughChain(inner))
                    break;
                current = current.Nested[0] ??= BuildJoinViewChain(batch, inner, nested: true);
            }
            path = [.. first];
        }

        var table = chain.TableAt(path) ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(written);
        var viewToBase = new int[viewRows.Columns.Length];
        for (var i = 0; i < viewToBase.Length; i++)
        {
            viewToBase[i] = -1;
            try
            {
                var (columnPath, columnIndex) = DescendToBaseColumn(batch, chain, viewRows.Columns[i].Name);
                if (columnPath.AsSpan().SequenceEqual(path))
                    viewToBase[i] = columnIndex;
            }
            catch (SimulatedSqlException)
            {
                // A derived column or one of another table reads nothing back.
            }
        }

        _ = batch.AcquireDataLockIfApplicable(table, default, isWrite: true);
        return new JoinViewMergePlan(chain, path, table, viewToBase, assigned);
    }

    /// <summary>
    /// Fills the view-shaped target heap with the view's rows, recording for
    /// each the written table's row it shows. The rows come from the view's
    /// own join tuples rather than a read of the view, which is what ties each
    /// one to a base-row address.
    /// </summary>
    private static void LoadJoinViewMergeRows(BatchContext batch, JoinViewMergePlan plan, HeapTable viewRows)
    {
        var chain = plan.Chain;
        var path = plan.Path;
        var addresses = new Dictionary<byte[], (int Page, int Slot)>(ReferenceEqualityComparer.Instance);
        var rowMaps = new Dictionary<byte[], byte[]?[]>[path.Length - 1];
        var sources = SourcesAlongPath(batch, chain, path, 0, original => WrapSourceWithAddressTracking(original, plan.Table, addresses, batch.Connection.StatementIo), rowMaps);

        // Hoisted scaffolding: one mutable tuple slot and one resolver per
        // level, reused across the walk.
        byte[]?[] tuple = [];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, tuple, name, batch);
        var (resolvers, belowRuntimes) = BuildChainResolvers(batch, chain, resolveTuple);
        var topLevel = chain.Views.Length - 1;
        var columns = viewRows.Columns;
        var names = new MultiPartName[columns.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = new MultiPartName(chain.Views[topLevel].OutputColumns[i].Name);

        foreach (var candidate in Selection.EnumerateJoinedRows(sources, chain.Joins, batch, outerResolver: null))
        {
            tuple = candidate;
            if (!ChainLevelsPass(chain, belowRuntimes, topLevel))
                continue;
            var values = new SqlValue[columns.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var value = resolvers[topLevel](names[i]);
                values[i] = value.Type == columns[i].Type ? value
                    : value.IsNull ? SqlValue.Null(columns[i].Type)
                    : value.CoerceTo(columns[i].Type);
            }
            (int Page, int Slot)? address = TargetBytesAlongPath(candidate, path, rowMaps) is { } target && addresses.TryGetValue(target, out var found)
                ? found
                : null;
            var slot = viewRows.Heap.Insert(RowEncoder.EncodeRow(viewRows.StoredColumns, ProjectStoredValues(viewRows, values), viewRows.Heap), undoLog: null);
            plan.Addresses[slot] = address;
        }
    }

    /// <summary>
    /// Commits a <c>MERGE</c> into a join view: carries each view-row action to
    /// the written table's row and commits those as a <c>MERGE</c> into that
    /// table would, its constraints, foreign keys and triggers included.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-28 against SQL Server 2025. A base row two view rows show
    /// takes one <c>DELETE</c> but not two <c>UPDATE</c>s (<strong>Msg
    /// 8672</strong>, even for the same value), and <c>@@ROWCOUNT</c> counts
    /// base rows. An <c>UPDATE</c> landing on a NULL-extended row of an outer
    /// join is real's <strong>Msg 8705</strong>, with nothing written. The
    /// <c>OUTPUT</c> rows are the view's, with <c>INSERTED</c> reading the
    /// written row back — its identity and computed values included.
    /// </remarks>
    private static SimulatedStatementOutcome CommitJoinViewMerge(
        ParserContext context,
        HeapTable viewRows,
        JoinViewMergePlan plan,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes,
        OutputProjection? output,
        List<(int Key, MergeActionKind Kind, int Index)>? outputOrder,
        List<WhenClause> whenClauses)
    {
        var batch = context.Batch;
        var table = plan.Table;
        var baseInserts = new List<(SqlValue[] NewValues, SqlValue[]? SourceValues)>(pendingInserts.Count);
        var baseUpdates = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)>(pendingUpdates.Count);
        var baseDeletes = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)>(pendingDeletes.Count);
        var touched = new HashSet<(int Page, int Slot)>();

        SqlValue[] ReadBaseRow((int Page, int Slot) address)
        {
            var values = DecodeFullRow(table, table.Heap.ReadSlotBytes(address.Page, address.Slot)!);
            EvaluateComputedColumns(table, values, batch);
            return values;
        }

        // The written row read back into the view's columns: a direct one off
        // the row, and for OUTPUT a derived one reading only the written table
        // computed from it through the chain, so INSERTED sees its new value
        // (probed 2026-09-30 against SQL Server 2025).
        var derivedReader = output is null ? null : JoinViewDerivedReader(batch, plan);
        void MapBack(SqlValue[] viewValues, SqlValue[] baseValues)
        {
            for (var i = 0; i < viewValues.Length; i++)
            {
                if (plan.ViewToBase[i] >= 0)
                    viewValues[i] = baseValues[plan.ViewToBase[i]];
                else if (derivedReader?.Invoke(baseValues, i) is { } derived)
                    viewValues[i] = derived.Type == viewRows.Columns[i].Type || derived.IsNull ? derived : derived.CoerceTo(viewRows.Columns[i].Type);
            }
        }

        var updatedColumnOrdinals = new List<int>();
        for (var i = 0; i < plan.Assigned.Length; i++)
        {
            if (plan.Assigned[i] && plan.ViewToBase[i] >= 0 && !updatedColumnOrdinals.Contains(plan.ViewToBase[i]))
                updatedColumnOrdinals.Add(plan.ViewToBase[i]);
        }

        foreach (var (page, slot, _, newView, sourceValues) in pendingUpdates)
        {
            if (plan.Addresses[(page, slot)] is not { } address)
                throw SimulatedSqlException.MissingIndexEntryInDml(table.IndexIdentities()[0].IndexId, table.ObjectId, batch.DatabaseFor(table).Name);
            if (!touched.Add(address))
                throw SimulatedSqlException.MergeMultiMatch();
            var oldValues = ReadBaseRow(address);
            var newValues = (SqlValue[])oldValues.Clone();
            for (var i = 0; i < plan.Assigned.Length; i++)
            {
                var ordinal = plan.ViewToBase[i];
                if (!plan.Assigned[i] || ordinal < 0)
                    continue;
                var column = table.Columns[ordinal];
                newValues[ordinal] = CoerceForWrite(EnforceMaxLength(newView[i], column, table, context.Connection), column, batch);
                EnforceRule(table, newValues, ordinal, batch);
            }
            for (var ci = 0; ci < table.Columns.Length; ci++)
            {
                if (table.Columns[ci].Type == SqlType.RowVersion)
                    newValues[ci] = SqlValue.FromRowVersion(batch.DatabaseFor(table).AllocateRowVersion());
            }
            EvaluateComputedColumns(table, newValues, batch);
            EnforceNotNull(table, newValues, "UPDATE");
            EnforceCheckConstraints(table, newValues, batch, "UPDATE");
            if (plan.ChecksOption && !PathRowRemainsVisible(batch, plan.Chain, plan.Path, table, newValues))
                throw SimulatedSqlException.ViewCheckOptionViolation();
            baseUpdates.Add((address.Page, address.Slot, oldValues, newValues, sourceValues));
            MapBack(newView, newValues);
        }

        var deleteKept = new bool[pendingDeletes.Count];
        for (var i = 0; i < pendingDeletes.Count; i++)
        {
            var (page, slot, _, sourceValues) = pendingDeletes[i];
            if (plan.Addresses[(page, slot)] is not { } address)
                continue;
            if (!touched.Add(address))
            {
                if (baseUpdates.Exists(update => update.Page == address.Page && update.Slot == address.Slot))
                    throw SimulatedSqlException.MergeMultiMatch();
                continue;
            }
            baseDeletes.Add((address.Page, address.Slot, ReadBaseRow(address), sourceValues));
            deleteKept[i] = true;
        }

        if (pendingInserts.Count > 0)
        {
            // The one WHEN NOT MATCHED clause's list, carried to the base
            // columns it names; each row's values go through the table's own
            // INSERT path, which supplies defaults, identity and rowversion
            // and enforces its constraints.
            var insertClause = whenClauses.Find(clause => clause.Kind == WhenClauseKind.NotMatchedByTarget)!;
            var viewOrdinals = new int[insertClause.InsertColumns!.Length];
            var baseColumns = new HeapColumn[viewOrdinals.Length];
            for (var i = 0; i < viewOrdinals.Length; i++)
            {
                viewOrdinals[i] = Array.IndexOf(viewRows.Columns, insertClause.InsertColumns[i]);
                baseColumns[i] = table.Columns[plan.ViewToBase[viewOrdinals[i]]];
            }
            var insteadOfInsert = HasInsteadOfTrigger(batch, table, TriggerActions.Insert);
            foreach (var (newView, sourceValues) in pendingInserts)
            {
                var values = new Expression[viewOrdinals.Length];
                for (var i = 0; i < values.Length; i++)
                    values[i] = new Value(newView[viewOrdinals[i]]);
                var clause = new WhenClause(WhenClauseKind.NotMatchedByTarget, MergeActionKind.Insert, searchCondition: null, assignments: null, baseColumns, values);
                ApplyInsert(context, table, sourceView: null, clause, sourceValues ?? [], static (_, _, name) => throw SimulatedSqlException.InvalidColumnName(name), baseInserts, insteadOfInsert);
                var inserted = baseInserts[^1].NewValues;
                if (!insteadOfInsert && plan.ChecksOption && !PathRowRemainsVisible(batch, plan.Chain, plan.Path, table, inserted))
                    throw SimulatedSqlException.ViewCheckOptionViolation();
                MapBack(newView, inserted);
            }
        }

        // OUTPUT reads the view's rows, landing an INTO target's before the
        // base table's triggers run, as a MERGE into the table itself does.
        var outputRows = output is null ? null : ProjectMergeOutput(context.Batch, output, outputOrder!, viewRows.Columns, pendingInserts, pendingUpdates, pendingDeletes, deleteKept);

        _ = CommitMerge(context, table, sourceView: null, baseInserts, baseUpdates, baseDeletes, output: null, outputOrder: null, whenClauses, viewRowsTarget: null, updatedColumnOrdinals: updatedColumnOrdinals);

        var totalAffected = baseInserts.Count + baseUpdates.Count + baseDeletes.Count;
        context.Connection.LastStatementRowCount = totalAffected;
        return output is { HasTarget: false }
            ? new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!, totalAffected)
            : new SimulatedNonQuery(totalAffected);
    }

    /// <summary>
    /// Reads a join view's derived column off a written row of the plan's
    /// table, evaluated down the chain with that row alone in its slot; null
    /// for a column that also reads another source, which <c>INSERTED</c> may
    /// not name (Msg 404) and so is never asked for.
    /// </summary>
    private static Func<SqlValue[], int, SqlValue?> JoinViewDerivedReader(BatchContext batch, JoinViewMergePlan plan)
    {
        var chain = plan.Chain;
        var top = chain.Views.Length - 1;
        var encode = JoinViewTargetSlotEncoder(batch, chain, plan.Path, chain.Sources, plan.Table);
        var tuple = new byte[]?[chain.Sources.Length];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(chain.Sources, tuple, name, batch);
        var resolveOutput = BuildChainResolvers(batch, chain, resolveTuple).Resolvers[^1];
        return (row, ordinal) =>
        {
            if (JoinViewColumnReadsOtherSource(batch, chain, top, ordinal, plan.Path[0]))
                return null;
            tuple[plan.Path[0]] = encode(row);
            return resolveOutput(new MultiPartName(chain.Views[top].OutputColumns[ordinal].Name));
        };
    }

    /// <summary>
    /// The <c>OUTPUT</c> rows of a <c>MERGE</c>'s actions, in the order the
    /// match phase keyed them; a delete <paramref name="deleteKept"/> marks
    /// as not taken is left out.
    /// </summary>
    private static List<byte[]> ProjectMergeOutput(
        BatchContext batch,
        OutputProjection output,
        List<(int Key, MergeActionKind Kind, int Index)> outputOrder,
        HeapColumn[] columns,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes,
        bool[]? deleteKept = null)
    {
        var nullTarget = new SqlValue[columns.Length];
        for (var i = 0; i < nullTarget.Length; i++)
            nullTarget[i] = SqlValue.Null(columns[i].Type);

        var rows = new List<byte[]>();
        foreach (var (_, kind, index) in outputOrder.OrderBy(action => action.Key))
        {
            if (kind == MergeActionKind.Delete && deleteKept is not null && !deleteKept[index])
                continue;
            var bytes = kind switch
            {
                MergeActionKind.Insert => output.ProjectRow(batch, insertedValues: pendingInserts[index].NewValues, deletedValues: nullTarget, sourceValues: pendingInserts[index].SourceValues, action: "INSERT"),
                MergeActionKind.Update => output.ProjectRow(batch, insertedValues: pendingUpdates[index].NewValues, deletedValues: pendingUpdates[index].OldValues, sourceValues: pendingUpdates[index].SourceValues, action: "UPDATE"),
                _ => output.ProjectRow(batch, insertedValues: nullTarget, deletedValues: pendingDeletes[index].OldValues, sourceValues: pendingDeletes[index].SourceValues, action: "DELETE"),
            };
            if (bytes is not null)
                rows.Add(bytes);
        }
        return rows;
    }
}
