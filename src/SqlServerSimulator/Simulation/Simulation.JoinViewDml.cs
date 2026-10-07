using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
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
    /// the last is the view the statement names. A joined statement naming a
    /// join view in its <c>FROM</c> clause is a chain of no levels over the
    /// statement's own sources, the view nested at its target source.
    /// </summary>
    /// <remarks>
    /// A join view has no single base table, so the
    /// <see cref="View.BaseColumnOrdinals"/> map every single-source level
    /// composes through stops there. Keeping the levels as levels — and
    /// chaining a resolver per level — is what lets a write reach the base
    /// heap anyway: each level's projections are expressions over the level
    /// below, and the bottom's are expressions over the join tuple.
    /// </remarks>
    private sealed class JoinViewChain
    {
        public JoinViewChain(View[] views, ViewUpdatabilityProfile[] profiles)
            : this(views, profiles, (FromSource[])profiles[0].Sources.Clone(), profiles[0].Joins, $"{views[^1].Schema.Name}.{views[^1].Name}")
        {
        }

        /// <summary>
        /// A statement's own <c>FROM</c> clause as a chain of no levels, its
        /// target source nesting the view the statement writes through: its
        /// <c>WHERE</c> and <c>SET</c> read the sources directly.
        /// </summary>
        public JoinViewChain(FromSource[] sources, JoinSpec[] joins, string targetName)
            : this([], [], sources, joins, targetName)
        {
        }

        private JoinViewChain(View[] views, ViewUpdatabilityProfile[] profiles, FromSource[] sources, JoinSpec[] joins, string targetName)
        {
            this.Views = views;
            this.Profiles = profiles;
            this.Sources = sources;
            this.Joins = joins;
            this.Nested = new JoinViewChain?[sources.Length];
            this.TargetName = targetName;
        }

        public readonly View[] Views;

        /// <summary>Body profile of <see cref="Views"/> at the same index.</summary>
        public readonly ViewUpdatabilityProfile[] Profiles;

        /// <summary>
        /// The bottom level's FROM sources, cloned so the UPDATE path can swap
        /// the target slot for its write-target read without disturbing the
        /// parsed profile.
        /// </summary>
        public readonly FromSource[] Sources;

        public readonly JoinSpec[] Joins;

        /// <summary>
        /// Per bottom source, the chain of the join view that source reads,
        /// built when a written column descends into it — the level stack a
        /// join view over a join view nests.
        /// </summary>
        public readonly JoinViewChain?[] Nested;

        /// <summary>Name the DML errors report — the view the statement named, as it wrote it.</summary>
        public string TargetName;

        /// <summary>Whether the target is a derived table, whose refusals are real's derived-table messages.</summary>
        public bool TargetIsDerivedTable;

        /// <summary>
        /// The heap a write along <paramref name="path"/> reaches: one bottom
        /// source index per nested chain, outermost first.
        /// </summary>
        public HeapTable? TableAt(int[] path)
        {
            var chain = this;
            for (var depth = 0; depth < path.Length - 1; depth++)
                chain = chain.Nested[path[depth]]!;
            return chain.Sources[path[^1]].BackingTable;
        }

        /// <summary>Whether any view on <paramref name="path"/>'s chains carries <c>WITH CHECK OPTION</c>.</summary>
        public bool HasCheckOptionAlong(int[] path)
        {
            var chain = this;
            for (var depth = 0; ; depth++)
            {
                if (HighestCheckOptionLevel(chain) >= 0)
                    return true;
                if (depth == path.Length - 1)
                    return false;
                chain = chain.Nested[path[depth]]!;
            }
        }
    }

    /// <summary>
    /// The routing a join-view INSERT hands <see cref="ProcessHeapInsert"/>:
    /// the listed view column names pre-resolved to base-table columns (the
    /// column list has to be read before the target table is known, so it is
    /// scanned once and replayed here), plus the chained
    /// <c>WITH CHECK OPTION</c> predicate when some level carries one, and the
    /// shape an <c>OUTPUT</c> clause binds <c>INSERTED</c> to.
    /// </summary>
    private sealed class JoinViewInsertPlan(Dictionary<string, HeapColumn> columns, Func<SqlValue[], BatchContext, bool>? checkOption, Func<ViewOutputShape> outputShape)
    {
        public readonly Dictionary<string, HeapColumn> Columns = columns;
        public readonly Func<SqlValue[], BatchContext, bool>? CheckOption = checkOption;
        public readonly Func<ViewOutputShape> OutputShape = outputShape;
    }

    /// <summary>
    /// Walks a view down to the multi-source body underneath it, re-parsing
    /// each level's body: the profiles carry live <see cref="FromSource"/> row
    /// enumerators, the same reason the read path re-parses per reference.
    /// A level that isn't DML-eligible, or one whose single source is neither
    /// a view nor part of a multi-source body, is <strong>Msg 4405</strong>.
    /// </summary>
    private static JoinViewChain BuildJoinViewChain(BatchContext batch, View view, string? writtenName = null, bool nested = false)
    {
        var viewName = writtenName ?? $"{view.Schema.Name}.{view.Name}";
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
            // A single-table view a join view reads is the degenerate bottom of
            // a nested chain: one source, no joins.
            if (nested && profile.Sources is [{ BackingTable: not null }])
                break;
            if (profile.Sources is not [var lowerSource] || lowerSource.UpdatableView() is not { } lower)
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);
            level = lower;
        }

        views.Reverse();
        profiles.Reverse();
        return new JoinViewChain([.. views], [.. profiles]) { TargetName = viewName };
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
    /// The predicates a write through <paramref name="chain"/> narrows its join
    /// by: every level's own WHERE and the statement's
    /// <paramref name="where"/>, each conjunct rebound from the view columns it
    /// names onto the join's sources by following each column's projection
    /// down the levels, by ordinal. Only a conjunct of a shape the narrowing
    /// passes read (<see cref="BooleanExpression.TryRebindOperands"/>) whose
    /// every column projects a bare column all the way down is kept; its other
    /// operands are row-independent. A rebound conjunct answers as the one it
    /// came from for every tuple, so the passes may narrow by it, while the
    /// walk still filters by the levels' and the statement's own predicates.
    /// </summary>
    private static List<BooleanExpression> JoinViewNarrowing(BatchContext batch, JoinViewChain chain, BooleanExpression? where)
    {
        var collation = batch.CurrentDatabase.Collation;
        var narrowing = new List<BooleanExpression>(chain.Profiles[0].Excluders);
        var conjuncts = new List<BooleanExpression>();
        for (var level = 1; level <= chain.Views.Length; level++)
        {
            conjuncts.Clear();
            if (level < chain.Views.Length)
            {
                foreach (var excluder in chain.Profiles[level].Excluders)
                    excluder.CollectConjuncts(conjuncts);
            }
            else
            {
                where?.CollectConjuncts(conjuncts);
            }
            var namedLevel = level - 1;
            foreach (var conjunct in conjuncts)
            {
                if (BooleanExpression.TryRebindOperands(conjunct, operand => Rebind(operand, namedLevel)) is { } rebound)
                    narrowing.Add(rebound);
            }
        }
        return narrowing;

        // The bottom column a name written against level's output columns
        // reads, or a row-independent operand as it is; else null.
        Expression? Rebind(Expression operand, int level)
        {
            while (operand is Parenthesized parenthesized)
                operand = parenthesized.Wrapped;
            if (operand is not Reference reference)
                return operand.IsRowIndependent ? operand : null;
            for (; level >= 0; level--)
            {
                var ordinal = IndexOfViewOutputColumn(collation, chain.Views[level], reference.ReferencedName.Leaf);
                if (ordinal < 0 || UnwrapDirectRef(chain.Profiles[level].Projections[ordinal]) is not { } below)
                    return null;
                reference = below;
            }
            return reference;
        }
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
    /// one written). A chain of no levels — a joined statement's own
    /// <c>FROM</c> clause — starts at the column of source
    /// <paramref name="targetSource"/>, the one the statement writes.
    /// </summary>
    private static (int[] Path, int ColumnIndex) DescendToBaseColumn(BatchContext batch, JoinViewChain chain, string columnName, int targetSource = -1)
    {
        var collation = batch.CurrentDatabase.Collation;
        var path = new List<int>();
        var current = chain;
        var name = columnName;
        var level = current.Views.Length - 1;
        while (true)
        {
            int sourceIndex, columnIndex;
            if (level < 0)
            {
                // A chain of no levels — a statement's own FROM clause — names
                // a column of the source the statement writes.
                sourceIndex = targetSource;
                columnIndex = Array.FindIndex(current.Sources[sourceIndex].ColumnNames, column => collation.Equals(column, name));
                if (columnIndex < 0)
                    throw SimulatedSqlException.InvalidColumnName(name);
            }
            else
            {
                var ordinal = IndexOfViewOutputColumn(collation, current.Views[level], name);
                if (ordinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(name);
                if (UnwrapDirectRef(current.Profiles[level].Projections[ordinal]) is not { ReferencedName: { } referenced })
                    throw SimulatedSqlException.ViewDmlTouchesDerivedField(chain.TargetName, chain.TargetIsDerivedTable);
                if (level > 0)
                {
                    name = referenced.Leaf;
                    level--;
                    continue;
                }

                (sourceIndex, columnIndex) = Selection.FindSourceColumn(current.Sources, referenced);
                if (sourceIndex < 0)
                    throw SimulatedSqlException.InvalidColumnName(referenced);
            }
            path.Add(sourceIndex);

            // A source that is itself a join view flattens into the write:
            // the column descends through that view's own level stack.
            if (current.Sources[sourceIndex] is not { BackingTable: null } source || source.UpdatableView() is not { } inner || !ReadsThroughChain(inner))
                return ([.. path], columnIndex);
            current = current.Nested[sourceIndex] ??= BuildJoinViewChain(batch, inner, nested: true);
            name = current.Views[^1].OutputColumns[columnIndex].Name;
            level = current.Views.Length - 1;
        }
    }

    /// <summary>
    /// Whether a view a join reads passes a write down as a level stack of its
    /// own: a join view, or a single-table view whose body a write can reach the
    /// table through (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    private static bool ReadsThroughChain(View view) => view.IsJoinUpdatable || view.BaseTable is not null;

    /// <summary>
    /// The bottom sources of <paramref name="chain"/> for a write whose target
    /// lies along <paramref name="path"/> from <paramref name="depth"/>: at
    /// the innermost chain the target source goes through
    /// <paramref name="wrapTarget"/>; at every chain above it the nested join
    /// view's slot is replaced by that view's rows, computed from the inner
    /// chain's own tuples, each encoded row recorded against the inner tuple
    /// it came from in <paramref name="rowMaps"/> at its depth — which is how a
    /// tuple of the outer view leads back to the base row it shows.
    /// </summary>
    private static FromSource[] SourcesAlongPath(
        BatchContext batch,
        JoinViewChain chain,
        int[] path,
        int depth,
        Func<FromSource, FromSource> wrapTarget,
        Dictionary<byte[], byte[]?[]>[] rowMaps)
    {
        var sources = (FromSource[])chain.Sources.Clone();
        var slot = path[depth];
        if (depth == path.Length - 1)
        {
            sources[slot] = wrapTarget(sources[slot]);
            return sources;
        }

        var inner = chain.Nested[slot]!;
        var innerSources = SourcesAlongPath(batch, inner, path, depth + 1, wrapTarget, rowMaps);
        var rowMap = rowMaps[depth] = new(ReferenceEqualityComparer.Instance);
        var original = sources[slot];

        byte[]?[] tuple = [];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(innerSources, tuple, name, batch);
        var (resolvers, belowRuntimes) = BuildChainResolvers(batch, inner, resolveTuple);
        var topLevel = inner.Views.Length - 1;
        var rows = new List<byte[]>();
        foreach (var candidate in Selection.EnumerateJoinedRows(innerSources, inner.Joins, batch, outerResolver: null))
        {
            tuple = candidate;
            if (!ChainLevelsPass(inner, belowRuntimes, topLevel))
                continue;
            var values = new SqlValue[original.Columns.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = resolvers[topLevel](new MultiPartName(inner.Views[topLevel].OutputColumns[i].Name));
            var bytes = RowEncoder.EncodeRow(original.Columns, values);
            rowMap[bytes] = (byte[]?[])candidate.Clone();
            rows.Add(bytes);
        }

        sources[slot] = new FromSource(
            qualifier: original.Qualifier,
            columnNames: original.ColumnNames,
            columns: original.Columns,
            storedSchema: original.Columns,
            storageOrdinals: null,
            lobStore: null,
            rows: rows,
            backingTable: null,
            unaliasedName: original.UnaliasedName);
        return sources;
    }

    /// <summary>
    /// The innermost chain's target-slot bytes an outer tuple shows along
    /// <paramref name="path"/>, or null when a level's slot is NULL-extended.
    /// </summary>
    private static byte[]? TargetBytesAlongPath(byte[]?[] tuple, int[] path, Dictionary<byte[], byte[]?[]>[] rowMaps)
    {
        var bytes = tuple[path[0]];
        for (var depth = 0; depth < path.Length - 1 && bytes is not null; depth++)
            bytes = rowMaps[depth][bytes][path[depth + 1]];
        return bytes;
    }

    /// <summary>
    /// <c>WITH CHECK OPTION</c> for a write along <paramref name="path"/>:
    /// the written row, standing in for its target source, must surface
    /// through the outermost chain carrying the option, up through that
    /// chain's highest option-bearing level. The join is re-run with the target
    /// source narrowed to that one row, so a row that changed which partner it
    /// matches is judged on its new partner (real accepts exactly that —
    /// probe-confirmed), and an INSERT is judged on the row it is about to
    /// write.
    /// </summary>
    /// <remarks>
    /// The probe row is encoded with no LOB store, which keeps every value
    /// inline and allocates no off-row chain for a row that may never be
    /// written; the decoder reads inline values without a store. Its ceiling
    /// is the encoder's 65535-byte var-offset cap rather than the heap's
    /// off-row spill.
    /// </remarks>
    private static bool PathRowRemainsVisible(BatchContext batch, JoinViewChain chain, int[] path, HeapTable table, SqlValue[] newValues)
    {
        var depth = 0;
        while (HighestCheckOptionLevel(chain) < 0)
        {
            chain = chain.Nested[path[depth]]!;
            depth++;
        }
        var remaining = path[depth..];
        var rowMaps = new Dictionary<byte[], byte[]?[]>[remaining.Length - 1];
        var probeRow = RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, newValues));
        var sources = SourcesAlongPath(
            batch, chain, remaining, 0,
            original => SingleRowSource(original, probeRow, lobStore: null),
            rowMaps);

        var throughLevel = HighestCheckOptionLevel(chain);
        byte[]?[] tuple = [];
        SqlValue resolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, tuple, name, batch);
        var (_, belowRuntimes) = BuildChainResolvers(batch, chain, resolveTuple);
        foreach (var candidate in Selection.EnumerateJoinedRows(sources, chain.Joins, batch, outerResolver: null))
        {
            tuple = candidate;
            if (TargetBytesAlongPath(candidate, remaining, rowMaps) is not null && ChainLevelsPass(chain, belowRuntimes, throughLevel))
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
        var viewName = destinationName.ToString();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);

        var chain = BuildJoinViewChain(batch, destinationView, viewName);
        var checkpoint = context.SaveCheckpoint();
        var listedNames = ScanInsertColumnNames(context);
        context.RestoreCheckpoint(checkpoint);

        int[]? path = null;
        var baseOrdinals = new int[listedNames.Count];
        for (var i = 0; i < listedNames.Count; i++)
        {
            var (columnPath, columnIndex) = DescendToBaseColumn(batch, chain, listedNames[i]);
            if (path is not null && !path.AsSpan().SequenceEqual(columnPath))
                throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);
            path = columnPath;
            baseOrdinals[i] = columnIndex;
        }

        var table = chain.TableAt(path!)
            ?? throw SimulatedSqlException.ViewUpdateAffectsMultipleTables(viewName);

        var columns = new Dictionary<string, HeapColumn>(batch.CurrentDatabase.Collation);
        for (var i = 0; i < listedNames.Count; i++)
            columns[listedNames[i]] = table.Columns[baseOrdinals[i]];

        var plan = new JoinViewInsertPlan(
            columns,
            chain.HasCheckOptionAlong(path!)
                ? (row, rowBatch) => PathRowRemainsVisible(rowBatch, chain, path!, table, row)
                : null,
            () => JoinViewOutputShape(batch, ViewColumnsFor(batch, destinationView, destinationName), chain, chain.Sources, path!, table, tuplesByRow: null));

        _ = batch.AcquireDataLockIfApplicable(table, default, isWrite: true);
        // A base table with another owner than the view breaks the chain,
        // and an INSERT checks only its own write there (probed 2026-09-27
        // against SQL Server 2025).
        if (!batch.IsSkipping)
            CheckJoinViewInsertChain(batch, chain, path!, table);
        return ProcessHeapInsert(table, context, top, destinationName, destinationView, plan);
    }

    /// <summary>
    /// The links an INSERT through a join view crosses, top down: each view
    /// whose owner differs from the one above it needs INSERT at object grain,
    /// a join view reading another join view descends into it, and the written
    /// table closes the chain (probed 2026-09-29 against SQL Server 2025:
    /// <c>dbo.v1</c> over a <c>u1</c>-owned join view is INSERT on the join
    /// view, then on the table).
    /// </summary>
    private static void CheckJoinViewInsertChain(BatchContext batch, JoinViewChain chain, int[] path, HeapTable table)
    {
        for (var level = chain.Views.Length - 1; level >= 1; level--)
            PermissionEnforcement.CheckBrokenChainLink(batch, "INSERT", chain.Views[level], chain.Views[level - 1]);
        if (path.Length == 1 || chain.Sources[path[0]].BackingView is not { } sourceView)
        {
            PermissionEnforcement.CheckBrokenChainLink(batch, "INSERT", chain.Views[0], table);
            return;
        }
        PermissionEnforcement.CheckBrokenChainLink(batch, "INSERT", chain.Views[0], sourceView);
        CheckJoinViewInsertChain(batch, chain.Nested[path[0]]!, path[1..], table);
    }

    /// <summary>
    /// The broken ownership chain an UPDATE through a join view crosses: every
    /// base table whose owner isn't the view's is checked for SELECT on the
    /// columns the statement reads of it — its join and filter columns, and
    /// the ones the <c>WHERE</c> and <c>SET</c> values reach through the view
    /// — and the written table for UPDATE on the columns assigned (probed
    /// 2026-09-27 against SQL Server 2025, column-grain both ways). Only the
    /// outermost chain's own base tables are gathered.
    /// </summary>
    private static void CheckJoinViewBrokenChains(
        BatchContext batch,
        JoinViewChain chain,
        HeapTable table,
        List<string> readViewColumns,
        List<(int Ordinal, Expression Expr)> assignments,
        string[]? writes = null)
    {
        if (batch.IsSkipping)
            return;
        writes ??= PermissionEnforcement.UpdateWrites;
        // The join view reads the base tables, so it is the module their owners
        // are compared with; the views above it are links of their own.
        var view = chain.Views[0];
        var reads = new ColumnReadTarget?[chain.Sources.Length];
        var viewReads = new ColumnReadTarget?[chain.Views.Length];
        var sourceViewReads = new ColumnReadTarget?[chain.Sources.Length];
        void AddBottom(MultiPartName name)
        {
            var (sourceIndex, columnIndex) = Selection.FindSourceColumn(chain.Sources, name);
            if (sourceIndex < 0)
                return;
            if (chain.Sources[sourceIndex].BackingTable is { } backing)
                _ = (reads[sourceIndex] ??= new ColumnReadTarget(backing)).Ordinals.Add(columnIndex + 1);
            else if (chain.Sources[sourceIndex].BackingView is { } sourceView)
                _ = (sourceViewReads[sourceIndex] ??= new ColumnReadTarget(sourceView)).Ordinals.Add(columnIndex + 1);
        }

        var collation = batch.CurrentDatabase.Collation;
        void AddViewColumn(int level, string columnName)
        {
            var ordinal = IndexOfViewOutputColumn(collation, chain.Views[level], columnName);
            if (ordinal < 0)
                return;
            _ = (viewReads[level] ??= new ColumnReadTarget(chain.Views[level])).Ordinals.Add(ordinal + 1);
            chain.Profiles[level].Projections[ordinal].VisitColumnReferences(name =>
            {
                if (level == 0)
                    AddBottom(name);
                else
                    AddViewColumn(level - 1, name.Leaf);
            });
        }

        foreach (var join in chain.Joins)
            join.OnPredicate?.VisitOperandExpressions(operand => operand.VisitColumnReferences(AddBottom));
        for (var level = 0; level < chain.Views.Length; level++)
        {
            var below = level - 1;
            foreach (var excluder in chain.Profiles[level].Excluders)
            {
                excluder.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
                {
                    if (below < 0)
                        AddBottom(name);
                    else
                        AddViewColumn(below, name.Leaf);
                }));
            }
        }
        foreach (var columnName in readViewColumns)
            AddViewColumn(chain.Views.Length - 1, columnName);

        // Top down: the first view whose owner differs from the one above it
        // is the one refused, ahead of anything under it.
        for (var level = chain.Views.Length - 1; level >= 1; level--)
            PermissionEnforcement.CheckBrokenChainViewLink(batch, chain.Views[level], chain.Views[level - 1], viewReads[level - 1], writes);

        // The last-bound source is the one real names when several are denied.
        for (var i = chain.Sources.Length - 1; i >= 0; i--)
        {
            if (reads[i] is { } read)
                PermissionEnforcement.CheckBrokenChainTableColumns(batch, Permission.Select, view, read);
            if (chain.Sources[i].BackingView is not { } sourceView)
                continue;
            // A view the join reads is a link of its own — refused for the
            // columns read of it, and for UPDATE too when the write goes
            // through it — and a join view among them has its own tables
            // (probed 2026-09-29 against SQL Server 2025).
            var nested = chain.Nested[i];
            var writesThrough = nested is not null || (sourceView.BaseTable is { } sourceTable && ReferenceEquals(sourceTable, table));
            PermissionEnforcement.CheckBrokenChainViewLink(batch, view, sourceView, sourceViewReads[i], writesThrough ? writes : PermissionEnforcement.NoWrites);
            if (nested is null)
                continue;
            var nestedRead = new List<string>();
            if (sourceViewReads[i] is { } readOfNested)
            {
                foreach (var ordinal in readOfNested.Ordinals)
                    nestedRead.Add(sourceView.OutputColumns[ordinal - 1].Name);
            }
            CheckJoinViewBrokenChains(batch, nested, table, nestedRead, assignments, writes);
        }

        // Only the chain whose own sources include the written table judges the write.
        if (!Array.Exists(chain.Sources, source => ReferenceEquals(source.BackingTable, table)))
            return;
        foreach (var write in writes)
        {
            if (write != "UPDATE")
            {
                PermissionEnforcement.CheckBrokenChainLink(batch, write, view, table);
                continue;
            }
            var assigned = new ColumnReadTarget(table);
            foreach (var (ordinal, _) in assignments)
            {
                if (ordinal >= 0)
                    _ = assigned.Ordinals.Add(ordinal + 1);
            }
            PermissionEnforcement.CheckBrokenChainTableColumns(batch, Permission.Update, view, assigned);
        }
    }

    /// <summary>
    /// OUTPUT through a join view whose written table sits under a nested join
    /// view, which isn't built: <c>INSERTED</c> would have to be computed up
    /// through the nested view from the written row alone, and its columns
    /// reading the nested view's other sources refused.
    /// </summary>
    private static NotSupportedException JoinOverJoinViewOutputNotModeled(View view) =>
        new($"OUTPUT through '{view.Name}', a join view whose written table another join view it reads supplies, isn't modeled.");

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
