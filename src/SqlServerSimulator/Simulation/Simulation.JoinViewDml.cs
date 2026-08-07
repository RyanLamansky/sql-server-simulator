using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The stack of view levels a write passes through when the bottom of the
    /// chain reads several sources. Index 0 is that multi-source (join) view,
    /// each higher index a single-source view reading the one below it, and
    /// the last is the view the statement names.
    /// </summary>
    /// <remarks>
    /// A join view has no single base table, so the
    /// <see cref="View.BaseColumnOrdinals"/> map every single-source level
    /// composes through stops there. Keeping the levels as levels — and
    /// chaining a resolver per level — is what lets a write reach the base
    /// heap anyway: each level's projections are expressions over the level
    /// below, and the bottom's are expressions over the join tuple.
    /// </remarks>
    private sealed class JoinViewChain(View[] views, ViewUpdatabilityProfile[] profiles)
    {
        public readonly View[] Views = views;

        /// <summary>Body profile of <see cref="Views"/> at the same index.</summary>
        public readonly ViewUpdatabilityProfile[] Profiles = profiles;

        /// <summary>
        /// The bottom level's FROM sources, cloned so the UPDATE path can swap
        /// the target slot for an address-tracking wrapper without disturbing
        /// the parsed profile.
        /// </summary>
        public readonly FromSource[] Sources = (FromSource[])profiles[0].Sources.Clone();

        public readonly JoinSpec[] Joins = profiles[0].Joins;

        /// <summary>Name the DML errors report — the view the statement named.</summary>
        public readonly string TargetName = $"{views[^1].Schema.Name}.{views[^1].Name}";
    }

    /// <summary>
    /// The routing a join-view INSERT hands <see cref="ProcessHeapInsert"/>:
    /// the listed view column names pre-resolved to base-table columns (the
    /// column list has to be read before the target table is known, so it is
    /// scanned once and replayed here), plus the chained
    /// <c>WITH CHECK OPTION</c> predicate when some level carries one.
    /// </summary>
    private sealed class JoinViewInsertPlan(Dictionary<string, HeapColumn> columns, Func<SqlValue[], BatchContext, bool>? checkOption)
    {
        public readonly Dictionary<string, HeapColumn> Columns = columns;
        public readonly Func<SqlValue[], BatchContext, bool>? CheckOption = checkOption;
    }

    /// <summary>
    /// Walks a view down to the multi-source body underneath it, re-parsing
    /// each level's body: the profiles carry live <see cref="FromSource"/> row
    /// enumerators, the same reason the read path re-parses per reference.
    /// A level that isn't DML-eligible, or one whose single source is neither
    /// a view nor part of a multi-source body, is <strong>Msg 4405</strong>.
    /// </summary>
    private static JoinViewChain BuildJoinViewChain(BatchContext batch, View view)
    {
        var viewName = $"{view.Schema.Name}.{view.Name}";
        var views = new List<View>();
        var profiles = new List<ViewUpdatabilityProfile>();
        var level = view;
        while (true)
        {
            if (batch.Connection.Simulation.ParseViewBodyPlan(batch, level).UpdatabilityProfile is not { } profile)
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);
            views.Add(level);
            profiles.Add(profile);
            if (profile.Sources.Length > 1)
                break;
            if (profile.Sources is not [{ BackingView: { } lower }])
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);
            level = lower;
        }

        views.Reverse();
        profiles.Reverse();
        return new JoinViewChain([.. views], [.. profiles]);
    }

    /// <summary>
    /// Builds the per-level resolver stack over a join tuple. Entry
    /// <c>level</c> of the returned resolver array answers a name written
    /// against <c>Views[level]</c>'s output columns by evaluating that
    /// column's projection; entry <c>level</c> of the runtime array is the
    /// context that level's own projections and WHERE excluders evaluate in —
    /// the level below for every level but the bottom, whose projections read
    /// the tuple directly.
    /// </summary>
    private static (Func<MultiPartName, SqlValue>[] Resolvers, RuntimeContext[] BelowRuntimes) BuildChainResolvers(
        BatchContext batch,
        JoinViewChain chain,
        Func<MultiPartName, SqlValue> tupleResolver)
    {
        var collation = batch.CurrentDatabase.Collation;
        var resolvers = new Func<MultiPartName, SqlValue>[chain.Views.Length];
        var belowRuntimes = new RuntimeContext[chain.Views.Length];
        var below = tupleResolver;
        for (var level = 0; level < chain.Views.Length; level++)
        {
            var view = chain.Views[level];
            var projections = chain.Profiles[level].Projections;
            var belowRuntime = new RuntimeContext(below, batch);
            belowRuntimes[level] = belowRuntime;
            resolvers[level] = name =>
            {
                var ordinal = IndexOfViewOutputColumn(collation, view, name.Leaf);
                return ordinal < 0
                    ? throw SimulatedSqlException.InvalidColumnName(name)
                    : projections[ordinal].Run(belowRuntime);
            };
            below = resolvers[level];
        }
        return (resolvers, belowRuntimes);
    }

    /// <summary>
    /// Whether the current join tuple survives every level's WHERE from the
    /// bottom up through <paramref name="throughLevel"/> — which is what
    /// "visible through that view" means, since a level shows only rows the
    /// levels below it already showed.
    /// </summary>
    private static bool ChainLevelsPass(JoinViewChain chain, RuntimeContext[] belowRuntimes, int throughLevel)
    {
        for (var level = 0; level <= throughLevel; level++)
        {
            if (!AllExcludersPass(chain.Profiles[level].Excluders, belowRuntimes[level]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The highest level carrying <c>WITH CHECK OPTION</c>, or -1 when none
    /// does. Visibility at a level implies visibility at every level below,
    /// so enforcing the highest one enforces the whole chain's.
    /// </summary>
    private static int HighestCheckOptionLevel(JoinViewChain chain)
    {
        for (var level = chain.Views.Length - 1; level >= 0; level--)
        {
            if (chain.Views[level].WithCheckOption)
                return level;
        }
        return -1;
    }

    /// <summary>
    /// Resolves a column name written against the statement's view down to the
    /// <c>(source, column)</c> of the bottom body it reads, one level at a
    /// time. A level whose projection isn't a direct column reference is
    /// <strong>Msg 4406</strong> naming the statement's view (probe-confirmed
    /// — the derived column may sit at any level and real still reports the
    /// one written).
    /// </summary>
    private static (int SourceIndex, int ColumnIndex) DescendToBaseColumn(BatchContext batch, JoinViewChain chain, string columnName)
    {
        var collation = batch.CurrentDatabase.Collation;
        var name = columnName;
        var level = chain.Views.Length - 1;
        while (true)
        {
            var ordinal = IndexOfViewOutputColumn(collation, chain.Views[level], name);
            if (ordinal < 0)
                throw SimulatedSqlException.InvalidColumnName(name);
            if (UnwrapDirectRef(chain.Profiles[level].Projections[ordinal]) is not { ReferencedName: { } referenced })
                throw SimulatedSqlException.ViewDmlTouchesDerivedField(chain.TargetName);
            if (level == 0)
            {
                var found = Selection.FindSourceColumn(chain.Sources, referenced);
                return found.SourceIndex < 0
                    ? throw SimulatedSqlException.InvalidColumnName(referenced)
                    : found;
            }
            name = referenced.Leaf;
            level--;
        }
    }

    /// <summary>
    /// <c>WITH CHECK OPTION</c> for a chained multi-source write: whether the
    /// written base row still surfaces through the level that carries the
    /// option — it must find a join partner and pass every WHERE from the
    /// bottom up. Re-runs the join with the target source narrowed to that one
    /// row, so a row that changed which partner it matches is judged on its
    /// new partner (real accepts exactly that — probe-confirmed), and an
    /// INSERT is judged on the row it is about to write.
    /// </summary>
    /// <remarks>
    /// The probe row is encoded with no LOB store, which keeps every value
    /// inline and allocates no off-row chain for a row that may never be
    /// written; the decoder reads inline values without a store. Its ceiling
    /// is the encoder's 65535-byte var-offset cap rather than the heap's
    /// off-row spill.
    /// </remarks>
    private static bool ChainRowRemainsVisible(
        BatchContext batch,
        JoinViewChain chain,
        FromSource[] sources,
        int targetIndex,
        HeapTable table,
        SqlValue[] newValues,
        int throughLevel)
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
        Func<MultiPartName, SqlValue> resolveTuple = null!;
        resolveTuple = name => ResolveAcrossMutationTuple(probeSources, tuple, name, batch, resolveTuple);
        var (_, belowRuntimes) = BuildChainResolvers(batch, chain, resolveTuple);
        foreach (var candidate in Selection.EnumerateJoinedRows(probeSources, chain.Joins, batch, outerResolver: null))
        {
            tuple = candidate;
            if (candidate[targetIndex] is not null && ChainLevelsPass(chain, belowRuntimes, throughLevel))
                return true;
        }
        return false;
    }

    /// <summary>
    /// INSERT through a view whose chain bottoms out in a multi-source body.
    /// Real accepts one whose explicit column list names a single base table's
    /// columns and writes that table, the untargeted columns taking their
    /// defaults; a list spanning two base tables, an implicit list and
    /// <c>DEFAULT VALUES</c> are all <strong>Msg 4405</strong>
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// Which table the write lands in is the column list's to say, and
    /// <see cref="ProcessHeapInsert"/> needs its target before it parses that
    /// list — so the list is scanned off a parser checkpoint first and
    /// replayed through <see cref="JoinViewInsertPlan"/>. The UPDATE path
    /// needs no such scan: its SET list has already parsed by the time it
    /// routes.
    /// </remarks>
    private static SimulatedStatementOutcome ProcessJoinViewInsert(
        View destinationView,
        ParserContext context,
        Selection.DmlTopLimit? top,
        MultiPartName destinationName)
    {
        var batch = context.Batch;
        var viewName = $"{destinationView.Schema.Name}.{destinationView.Name}";
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);

        var chain = BuildJoinViewChain(batch, destinationView);
        var checkpoint = context.SaveCheckpoint();
        var listedNames = ScanInsertColumnNames(context);
        context.RestoreCheckpoint(checkpoint);

        var targetIndex = -1;
        var baseOrdinals = new int[listedNames.Count];
        for (var i = 0; i < listedNames.Count; i++)
        {
            var (sourceIndex, columnIndex) = DescendToBaseColumn(batch, chain, listedNames[i]);
            if (targetIndex >= 0 && targetIndex != sourceIndex)
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);
            targetIndex = sourceIndex;
            baseOrdinals[i] = columnIndex;
        }

        var table = chain.Sources[targetIndex].BackingTable
            ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);

        var columns = new Dictionary<string, HeapColumn>(batch.CurrentDatabase.Collation);
        for (var i = 0; i < listedNames.Count; i++)
            columns[listedNames[i]] = table.Columns[baseOrdinals[i]];

        var checkLevel = HighestCheckOptionLevel(chain);
        var plan = new JoinViewInsertPlan(
            columns,
            checkLevel < 0
                ? null
                : (row, rowBatch) => ChainRowRemainsVisible(rowBatch, chain, chain.Sources, targetIndex, table, row, checkLevel));

        _ = batch.AcquireDataLockIfApplicable(table, default, isWrite: true);
        return ProcessHeapInsert(table, context, top, destinationName, destinationView, plan);
    }

    /// <summary>
    /// Reads an INSERT's parenthesized column list for its names alone, with
    /// the cursor on the opening paren. Callers restore the checkpoint they
    /// took beforehand so <see cref="ProcessHeapInsert"/> parses the same list
    /// again against the target it now knows.
    /// </summary>
    private static List<string> ScanInsertColumnNames(ParserContext context)
    {
        var names = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not StringToken column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(column.Value);

            var separator = context.GetNextRequired();
            if (separator is Operator { Character: ')' })
                return names;
            if (separator is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
