using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// What <c>INSERTED</c> / <c>DELETED</c> are when a DML statement's
    /// <c>OUTPUT</c> writes through a view: the view's own columns, in its
    /// order and under its names (<c>INSERTED.*</c> expands to them, and a base
    /// column the view doesn't project is Msg 207), each read as the view
    /// projects it — a derived column computed from the written row, a masked
    /// one masked as the view reads it (probed 2026-09-27 against SQL Server
    /// 2025).
    /// </summary>
    /// <remarks>
    /// A column is read only when the clause names it, so a derived column
    /// that fails for the row (<c>10 / a</c> over a zero) raises only when
    /// OUTPUT reads it, as on real.
    /// </remarks>
    private sealed class ViewOutputShape(HeapColumn[] columns, Func<SqlValue[], int, SqlValue>? read, Func<int, bool>? insertedRefused)
    {
        public readonly HeapColumn[] Columns = columns;

        /// <summary>
        /// Reads view column <c>ordinal</c> off the row a DML site hands
        /// OUTPUT — a base-table row, evaluated through the view — or null
        /// when the site hands rows already shaped to the view (an
        /// <c>INSTEAD OF</c> trigger's pseudo-table rows).
        /// </summary>
        public readonly Func<SqlValue[], int, SqlValue>? Read = read;

        /// <summary>
        /// Which view columns <c>INSERTED</c> may not name — <strong>Msg
        /// 404</strong>: a join view's columns that read a base table the
        /// statement isn't writing, and every column under an <c>INSTEAD OF
        /// UPDATE</c> trigger, whose would-be row real never forms. Null when
        /// all may be named.
        /// </summary>
        private readonly Func<int, bool>? insertedRefused = insertedRefused;

        /// <summary>
        /// The Msg 404s met while binding the clause: real reports each one
        /// and carries on binding, so a later Msg 207 follows them.
        /// </summary>
        public readonly List<SimulatedSqlException> Refusals = [];

        /// <summary>
        /// The type of view column <paramref name="ordinal"/> as the clause
        /// binds it, recording a Msg 404 when <c>INSERTED</c> may not name it.
        /// </summary>
        public SqlType Admit(int ordinal, bool inserted)
        {
            if (inserted && this.insertedRefused?.Invoke(ordinal) == true)
                this.Refusals.Add(SimulatedSqlException.OutputColumnOfUnmodifiedBaseTable(this.Columns[ordinal].Name));
            return this.Columns[ordinal].Type;
        }

        /// <summary>Raises the gathered refusals, in order, as one error.</summary>
        public void ThrowRefusals()
        {
            if (this.Refusals.Count > 0)
                throw SimulatedSqlException.Aggregate(this.Refusals);
        }
    }

    /// <summary>
    /// The OUTPUT shape of a write through a view whose chain reaches one base
    /// table: rows arrive as <paramref name="table"/> rows and each named view
    /// column is read off one through the chain's projections.
    /// </summary>
    private static ViewOutputShape SingleBaseViewOutputShape(BatchContext batch, View view, MultiPartName writtenName, HeapTable table) =>
        new(ViewColumnsFor(batch, view, writtenName), SingleBaseViewReader(batch, view, table), insertedRefused: null);

    /// <summary>
    /// A view's columns as a write through it reads them — re-bound, so a
    /// masked column carries the mask the body projects now; a CTE target's
    /// are the ones its definition recorded.
    /// </summary>
    private static HeapColumn[] ViewColumnsFor(BatchContext batch, View view, MultiPartName writtenName) =>
        view.UnstoredBody is null ? batch.Connection.Simulation.BindViewColumns(batch, view, writtenName) : view.OutputColumns;

    /// <summary>
    /// Reads a view column off a row of the one base table the view's chain
    /// reaches. A column the chain passes straight through comes off the row
    /// by its base ordinal; a derived one runs its level's projection, whose
    /// names resolve against the level below the same way, down to the row.
    /// </summary>
    /// <remarks>
    /// Each level's body is re-parsed here, as the other writes through a view
    /// do. A window function has no meaning over one row, so a windowed
    /// level's derived column is refused rather than evaluated.
    /// </remarks>
    private static Func<SqlValue[], int, SqlValue> SingleBaseViewReader(BatchContext batch, View view, HeapTable table)
    {
        var collation = batch.CurrentDatabase.Collation;
        var levels = new List<(View View, Expression[]? Projections)>();
        var level = view;
        while (true)
        {
            var profile = (level.UnstoredBody ?? batch.Connection.Simulation.ParseViewBodyPlan(batch, level)).UpdatabilityProfile;
            levels.Add((level, profile?.Projections));
            if (profile?.Sources is not [{ BackingView: { } lower }])
                break;
            level = lower;
        }

        SqlValue ReadBase(SqlValue[] row, int ordinal) =>
            table.Columns[ordinal] is { Computed: not null, IsPersisted: false }
                ? EvaluateComputedColumn(table, row, ordinal, batch)
                : row[ordinal];

        SqlValue ReadLevel(int depth, int ordinal, SqlValue[] row)
        {
            var (levelView, projections) = levels[depth];
            if (levelView.BaseColumnOrdinals[ordinal] is var baseOrdinal and >= 0)
                return ReadBase(row, baseOrdinal);
            if (projections is null || levelView.IsWindowed)
                throw new NotSupportedException($"OUTPUT reading the derived column '{levelView.OutputColumns[ordinal].Name}' of '{levelView.Name}' isn't modeled for this view shape.");

            var below = depth + 1;
            return projections[ordinal].Run(new RuntimeContext(
                name =>
                {
                    if (below == levels.Count)
                    {
                        var baseOrdinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, name.Leaf));
                        return baseOrdinal < 0 ? throw SimulatedSqlException.InvalidColumnName(name) : ReadBase(row, baseOrdinal);
                    }
                    var lowerOrdinal = IndexOfViewOutputColumn(collation, levels[below].View, name.Leaf);
                    return lowerOrdinal < 0 ? throw SimulatedSqlException.InvalidColumnName(name) : ReadLevel(below, lowerOrdinal, row);
                },
                batch));
        }

        return (row, ordinal) => ReadLevel(0, ordinal, row);
    }

    /// <summary>
    /// The OUTPUT shape of a write through a join view: rows arrive as rows of
    /// the one base table the statement writes, and a view column is read by
    /// evaluating it down the chain against a join tuple — the row's own
    /// tuple when <paramref name="tuplesByRow"/> recorded one (an UPDATE's
    /// pre-image, which reads the other sources as they joined), else a tuple
    /// holding only the written row. <c>INSERTED</c> may name only columns
    /// reading nothing but the written table (Msg 404), which is what makes
    /// the second form enough for it.
    /// </summary>
    private static ViewOutputShape JoinViewOutputShape(
        BatchContext batch,
        HeapColumn[] columns,
        JoinViewChain chain,
        FromSource[] sources,
        int targetIndex,
        HeapTable table,
        Dictionary<SqlValue[], byte[]?[]>? tuplesByRow)
    {
        var top = chain.Views[^1];
        byte[]?[] tuple = [];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, tuple, name, batch);
        var (resolvers, _) = BuildChainResolvers(batch, chain, resolveTuple);
        var resolveOutput = resolvers[^1];

        SqlValue Read(SqlValue[] row, int ordinal)
        {
            if (tuplesByRow is null || !tuplesByRow.TryGetValue(row, out var recorded))
            {
                recorded = new byte[]?[sources.Length];
                recorded[targetIndex] = RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, row));
            }
            tuple = recorded;
            return resolveOutput(new MultiPartName(top.OutputColumns[ordinal].Name));
        }

        return new ViewOutputShape(columns, Read, ordinal => JoinViewColumnReadsOtherSource(batch, chain, chain.Views.Length - 1, ordinal, targetIndex));
    }

    /// <summary>
    /// Whether column <paramref name="ordinal"/> of the chain's level
    /// <paramref name="level"/> reads, anywhere down the chain, a column of a
    /// bottom source other than <paramref name="targetIndex"/>.
    /// </summary>
    private static bool JoinViewColumnReadsOtherSource(BatchContext batch, JoinViewChain chain, int level, int ordinal, int targetIndex)
    {
        var collation = batch.CurrentDatabase.Collation;
        var readsOther = false;
        chain.Profiles[level].Projections[ordinal].VisitColumnReferences(name =>
        {
            if (readsOther)
                return;
            if (level == 0)
            {
                readsOther = Selection.FindSourceColumn(chain.Sources, name).SourceIndex != targetIndex;
                return;
            }
            var lower = IndexOfViewOutputColumn(collation, chain.Views[level - 1], name.Leaf);
            readsOther = lower < 0 || JoinViewColumnReadsOtherSource(batch, chain, level - 1, lower, targetIndex);
        });
        return readsOther;
    }
}
