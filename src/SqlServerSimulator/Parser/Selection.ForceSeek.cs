using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// The joined UPDATE / DELETE's form of the check, over its FROM clause
    /// and its WHERE.
    /// </summary>
    internal static void ValidateForcedSeeks(ParserContext context, FromSource[] sources, JoinSpec[] joins, BooleanExpression? where)
        => ValidateForcedSeeks(context, sources, joins, where is null ? [] : [where], projections: null, soleSubqueryColumn: null);

    /// <summary>
    /// Refuses a query real's optimizer can't plan under its <c>FORCESEEK</c>
    /// hints — Msg 8622, raised once the query's predicates have parsed. A
    /// hinted source needs some predicate an index seek can answer: a
    /// top-level conjunct of the WHERE clause or of any join's ON clause that
    /// compares a key-leading column against something the source doesn't
    /// read (see <see cref="BooleanExpression.OffersSeek"/>), or, for a
    /// one-column subquery, its own select-list column, which an enclosing
    /// <c>IN</c> seeks on (probed 2026-09-28 against SQL Server 2025).
    /// <para>
    /// A named index — <c>FORCESEEK(ix)</c>, or an <c>INDEX</c> hint beside
    /// the bare form — narrows the keys to that one, <c>INDEX(0)</c> to none;
    /// a seek-column list <c>FORCESEEK(ix(a, b))</c> needs a predicate on every
    /// column it names. A heap or a columnstore-only table has no key to seek.
    /// </para>
    /// <para>
    /// The check is a compile-time one, so it runs while the batch compiles
    /// and when a statement over a table the batch creates first runs, but not
    /// while a module body binds at <c>CREATE</c>: real's optimizer meets the
    /// body at its first execution. A statement whose binding already failed
    /// isn't optimized at all, which is why an earlier binder error in the
    /// batch keeps this one from being reported.
    /// </para>
    /// </summary>
    private static void ValidateForcedSeeks(
        ParserContext context,
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        List<Expression>? projections,
        Expression? soleSubqueryColumn)
    {
        RecordJoinHintSite(context, sources, joins, excluders);
        var batch = context.Batch;
        if (batch.IsSkipping ? !batch.CompilingForRun : batch.CreateTimeBinding)
            return;
        if (batch.CreateTimeBindErrors is { Count: > 0 })
            return;

        List<BooleanExpression>? conjuncts = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].ForcedSeekThrough is { } through)
            {
                conjuncts ??= CollectConjuncts(excluders, joins);
                if (through.ForceSeekColumns is not null && sources[i].BackingView is { } view)
                    throw SimulatedSqlException.ForceSeekOnViewWithoutNoExpand(view.Name);
                if (!BodySeeksAreFeasible(batch, sources[i], i, sources, conjuncts, soleSubqueryColumn))
                    throw SimulatedSqlException.ForceSeekPlanInfeasible();
                continue;
            }
            if (sources[i] is not { BackingTable: { } table } source || (source.ForcedAccessPath is null && source.WrittenHints?.IndexArguments is null))
                continue;
            conjuncts ??= CollectConjuncts(excluders, joins);
            if (HintsUnimpliedFilteredIndex(batch, source, i, sources, table, conjuncts))
                throw SimulatedSqlException.ForceSeekPlanInfeasible();
            if (source.ForcedAccessPath is not { } hints)
                continue;

            table.SettleIndexIds();
            // INDEX(0) — the heap or clustered scan — beside an index that
            // is anything else is a plan no access path builds (state 2,
            // probed 2026-10-05 against SQL Server 2025).
            if (hints.IndexArguments is { Count: > 1 } arguments
                && arguments.Exists(static argument => argument.Id == 0)
                && arguments.Exists(static argument => argument.Id != 0))
            {
                throw SimulatedSqlException.ForceSeekPlanInfeasible(state: 2);
            }
            if (hints.ForceSeek
                ? !ForcedSeekIsFeasible(batch, source, i, sources, table, hints, conjuncts, soleSubqueryColumn)
                : hints.ForceScan && !ForcedScanIsFeasible(batch, source, i, sources, table, hints, conjuncts, projections))
            {
                throw SimulatedSqlException.ForceSeekPlanInfeasible();
            }
        }
    }

    private static List<BooleanExpression> CollectConjuncts(List<BooleanExpression> excluders, JoinSpec[] joins)
    {
        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);
        foreach (var join in joins)
            join.OnPredicate?.CollectConjuncts(conjuncts);
        return conjuncts;
    }

    /// <summary>
    /// The stored ordinals leading a key of <paramref name="table"/> that
    /// <paramref name="hints"/> leave a seek — every enabled one unless an
    /// index is named. A disabled index seeks nothing (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    private static HashSet<int> SeekableLeads(BatchContext batch, HeapTable table, TableHintInfo hints)
    {
        var leads = new HashSet<int>();
        foreach (var owner in EnumerateKeyOwners(table))
        {
            (string Name, int[] Ordinals, int IndexId, bool Disabled) described = owner switch
            {
                KeyConstraint key => (key.Name, key.StorageOrdinals, key.IndexId, key.IsDisabled),
                Storage.Index index => (index.Name, index.KeyStorageOrdinals, index.IndexId, index.IsDisabled),
                _ => ("", [], -1, true),
            };
            var (name, ordinals, indexId, disabled) = described;
            if (!disabled && ordinals.Length != 0 && ordinals[0] >= 0 && NamedByIndexHint(batch, hints, name, indexId))
                _ = leads.Add(ordinals[0]);
        }
        return leads;
    }

    /// <summary>
    /// Whether a <c>FORCESEEK</c> on a view or CTE reference can be honored:
    /// real carries it to every table the body reads, at any depth of views,
    /// CTEs, derived tables and <c>UNION</c> branches, and each must seek on a
    /// predicate of its own block — a join's only once the table on its other
    /// side seeks — or on one the reading query writes on a column the body
    /// passes the key through as (probed 2026-10-07 against SQL Server 2025).
    /// A body whose shape went unrecorded is given the benefit of the doubt.
    /// </summary>
    private static bool BodySeeksAreFeasible(
        BatchContext batch,
        FromSource source,
        int sourceIndex,
        FromSource[] all,
        List<BooleanExpression> conjuncts,
        Expression? soleSubqueryColumn)
    {
        return NestedBody(source) is not { } body || ShapeCanSeek(batch, body, source.ForcedSeekThrough!.Value, ReaderSeeks);

        bool ReaderSeeks(HashSet<int> columns)
        {
            var probe = new ForceSeekProbe(batch, source, sourceIndex, all, columns);
            return (soleSubqueryColumn is not null && probe.IsSeekColumn(soleSubqueryColumn))
                || conjuncts.Exists(conjunct => conjunct.OffersSeek(probe, negated: false));
        }
    }

    /// <summary>The shape of the query body a view, CTE or derived-table source reads, or null for any other source or an unrecorded one.</summary>
    private static SeekBodyShape? NestedBody(FromSource source) =>
        source.BodySeekShape ?? (source.LateralIsQueryBody ? source.LateralPlan?.SeekShape : null);

    /// <summary>
    /// Whether every table <paramref name="shape"/> reads can seek, given
    /// <paramref name="readerSeeks"/>, which answers whether the reading query
    /// seeks on one of a set of the shape's output columns. A <c>UNION</c>
    /// needs each branch to.
    /// </summary>
    private static bool ShapeCanSeek(BatchContext batch, SeekBodyShape shape, TableHintInfo hints, Func<HashSet<int>, bool> readerSeeks)
    {
        if (shape.Branches is { } branches)
            return Array.TrueForAll(branches, branch => ShapeCanSeek(batch, branch, hints, readerSeeks));
        var sources = shape.Sources;
        var conjuncts = CollectConjuncts(shape.Filters, shape.Joins);
        var leads = new HashSet<int>?[sources.Length];
        var seeking = new bool[sources.Length];
        var remaining = 0;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].BackingTable is { } table)
            {
                // A heap or a table with no enabled key has nothing to seek.
                table.SettleIndexIds();
                leads[i] = SeekableLeads(batch, table, hints);
                if (leads[i]!.Count == 0)
                    return false;
                remaining++;
            }
            else if (NestedBody(sources[i]) is not null)
            {
                remaining++;
            }
            else
            {
                seeking[i] = true;
            }
        }

        // A join's predicate seeks one side only once the other side's rows
        // come from a seek of their own, so seeking spreads from the tables
        // a constant or the reading query anchors.
        bool progressed;
        do
        {
            progressed = false;
            for (var i = 0; i < sources.Length; i++)
            {
                if (seeking[i])
                    continue;
                var source = i;
                if (leads[i] is { } own ? ColumnsSeek(source, own) : ShapeCanSeek(batch, NestedBody(sources[i])!, hints, columns => ColumnsSeek(source, columns)))
                {
                    seeking[i] = true;
                    progressed = true;
                    remaining--;
                }
            }
        }
        while (progressed && remaining > 0);
        return remaining == 0;

        bool ColumnsSeek(int sourceIndex, HashSet<int> columns)
        {
            var probe = new ForceSeekProbe(batch, sources[sourceIndex], sourceIndex, sources, columns);
            foreach (var conjunct in conjuncts)
            {
                if (conjunct.OffersSeek(probe, negated: false) && ReadsOnlySeekingSources(conjunct, sourceIndex))
                    return true;
            }
            var outward = new HashSet<int>();
            for (var p = 0; p < shape.Projections.Length; p++)
            {
                if (shape.Projections[p] is { } projection && probe.IsSeekColumn(projection))
                    _ = outward.Add(p);
            }
            return outward.Count > 0 && readerSeeks(outward);
        }

        bool ReadsOnlySeekingSources(BooleanExpression conjunct, int sourceIndex)
        {
            var only = true;
            conjunct.Walk((_, node) =>
            {
                if (only && node.Column is { } column)
                {
                    var read = FindSourceColumnOfAnyKind(sources, column).SourceIndex;
                    only = read < 0 || read == sourceIndex || seeking[read];
                }
                return only;
            });
            return only;
        }
    }

    private static bool ForcedSeekIsFeasible(
        BatchContext batch,
        FromSource source,
        int sourceIndex,
        FromSource[] all,
        HeapTable table,
        TableHintInfo hints,
        List<BooleanExpression> conjuncts,
        Expression? soleSubqueryColumn)
    {
        var leads = SeekableLeads(batch, table, hints);
        if (leads.Count == 0)
            return false;

        if (hints.ForceSeekColumns is { } seekColumns)
        {
            // Every column the list names has to be sought.
            foreach (var column in seekColumns)
            {
                var ordinal = Array.FindIndex(table.Columns, c => batch.CurrentDatabase.Collation.Equals(c.Name, column));
                var stored = ordinal >= 0 && table.StorageOrdinals is { } map ? map[ordinal] : ordinal;
                var probe = new ForceSeekProbe(batch, source, sourceIndex, all, [stored]);
                if (stored < 0 || !conjuncts.Exists(conjunct => conjunct.OffersSeek(probe, negated: false)))
                    return false;
            }
            return true;
        }

        var seekProbe = new ForceSeekProbe(batch, source, sourceIndex, all, leads);
        return (soleSubqueryColumn is not null && seekProbe.IsSeekColumn(soleSubqueryColumn))
            || conjuncts.Exists(conjunct => conjunct.OffersSeek(seekProbe, negated: false));
    }

    // FORCESCAN beside an INDEX hint naming nonclustered indexes only: the
    // scan of such an index has to fetch every column it doesn't carry by a
    // lookup, which is a seek, so a query reading one is refused (probed
    // 2026-09-28 against SQL Server 2025 — even a lookup of one column the
    // WHERE alone reads).
    private static bool ForcedScanIsFeasible(
        BatchContext batch,
        FromSource source,
        int sourceIndex,
        FromSource[] all,
        HeapTable table,
        TableHintInfo hints,
        List<BooleanExpression> conjuncts,
        List<Expression>? projections)
    {
        // INDEX(0) scans the heap or the clustered index, which carry every column.
        if (hints.IndexArguments!.Exists(static argument => argument.Id == 0))
            return true;
        var carried = new HashSet<int>();
        foreach (var owner in EnumerateKeyOwners(table))
        {
            var (name, isClustered, indexId, ordinals) = owner switch
            {
                KeyConstraint key => (key.Name, key.IsClustered, key.IndexId, key.StorageOrdinals),
                Storage.Index index => (index.Name, index.IsClustered, index.IndexId, [.. index.KeyStorageOrdinals, .. index.IncludedColumns]),
                _ => ("", false, -1, []),
            };
            if (!NamedByIndexHint(batch, hints, name, indexId))
                continue;
            if (isClustered)
                return true;
            carried.UnionWith(ordinals);
        }
        if (KeyLockGroup.ClusteredOwner(table) is { } clustered)
        {
            carried.UnionWith(clustered switch
            {
                KeyConstraint key => key.StorageOrdinals,
                Storage.Index index => index.KeyStorageOrdinals,
                _ => [],
            });
        }

        var covered = true;
        void Read(MultiPartName name)
        {
            var (index, column) = FindSourceColumn(all, name);
            if (index == sourceIndex && !carried.Contains(source.StorageOrdinals is { } map ? map[column] : column))
                covered = false;
        }
        foreach (var conjunct in conjuncts)
            conjunct.VisitOperandExpressions(operand => operand.VisitColumnReferences(Read));
        foreach (var projection in projections ?? [])
        {
            if (projection is Expressions.StarProjection star)
            {
                if (star.Qualifier is null || batch.CurrentDatabase.Collation.Equals(star.Qualifier, source.Qualifier))
                    return false;
                continue;
            }
            projection.VisitColumnReferences(Read);
        }
        return covered;
    }

    // Whether an index hint beside FORCESEEK lets the seek use this key or
    // index: every key when none names one, else only those it names — by
    // name or by index_id; INDEX(0), the heap, names no key.
    private static bool NamedByIndexHint(BatchContext batch, TableHintInfo hints, string name, int indexId)
    {
        if (hints.IndexArguments is not { Count: > 0 } arguments)
            return true;
        foreach (var argument in arguments)
        {
            if (argument.Name is { } named ? batch.CurrentDatabase.Collation.Equals(named, name) : argument.Id == indexId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// What a query block hands a <c>FORCESEEK</c> on a view or CTE reading it
    /// (<see cref="Selection.SeekShape"/>): the block's sources and joins, its
    /// WHERE and HAVING conjunct roots, and the select-list expressions a
    /// predicate outside can reach, whose ordinals are the reading source's
    /// columns.
    /// </summary>
    internal sealed class SeekBodyShape
    {
        public readonly FromSource[] Sources;
        public readonly JoinSpec[] Joins;
        public readonly List<BooleanExpression> Filters;

        /// <summary>The select list by ordinal, null where a predicate outside can't reach the column.</summary>
        public readonly Expression?[] Projections;

        /// <summary>A <c>UNION</c>'s two branches, the shape's other fields then empty; null for a query block.</summary>
        public readonly SeekBodyShape[]? Branches;

        public SeekBodyShape(FromSource[] sources, JoinSpec[] joins, List<BooleanExpression> filters, Expression?[] projections)
        {
            this.Sources = sources;
            this.Joins = joins;
            this.Filters = filters;
            this.Projections = projections;
        }

        public SeekBodyShape(SeekBodyShape[] branches)
        {
            this.Sources = [];
            this.Joins = [];
            this.Filters = [];
            this.Projections = [];
            this.Branches = branches;
        }
    }

    /// <summary>
    /// The select list a predicate outside the block can reach: none past a
    /// row limit, and past a window function only a column every window
    /// partitions by (probed 2026-10-07 against SQL Server 2025: a view's
    /// <c>ROW_NUMBER() OVER (PARTITION BY id …)</c> passes <c>id = 1</c> to
    /// the seek, an <c>OVER ()</c> passes nothing).
    /// </summary>
    private static Expression?[] SeekReachableProjections(Collation collation, List<Expression> expressions, List<Expressions.WindowExpression> windows, bool rowLimited)
    {
        if (rowLimited)
            return [];
        var reachable = new Expression?[expressions.Count];
        for (var p = 0; p < reachable.Length; p++)
        {
            var expression = expressions[p] is Expressions.NamedExpression named ? named.Inner : expressions[p];
            if (windows.TrueForAll(window => expression is Expressions.Reference column
                && Array.Exists(window.PartitionBy, key => key is Expressions.Reference partition && collation.Equals(partition.ReferencedName.ToString(), column.ReferencedName.ToString()))))
            {
                reachable[p] = expressions[p];
            }
        }
        return reachable;
    }

    /// <summary>
    /// This query's <see cref="SeekBodyShape"/>, across which a view, CTE or
    /// derived-table reference's <c>FORCESEEK</c> is judged; null for a block
    /// reading no source, and for a set operation other than a <c>UNION</c>
    /// of recorded branches, which is left unjudged.
    /// </summary>
    internal SeekBodyShape? SeekShape;

    /// <summary>
    /// What <see cref="BooleanExpression.OffersSeek"/> asks of one hinted
    /// source: which of its columns lead a key the seek may use, and whether
    /// an expression reads the source at all.
    /// </summary>
    internal sealed class ForceSeekProbe(BatchContext batch, FromSource source, int sourceIndex, FromSource[] sources, HashSet<int> leadingOrdinals)
    {
        /// <summary>
        /// Whether <paramref name="expression"/> is a column of the source that
        /// leads a usable key — through a conversion that stays within the
        /// column's type family (<c>CAST(a AS bigint)</c> over an int seeks, a
        /// cast to a string doesn't).
        /// </summary>
        public bool IsSeekColumn(Expression expression)
        {
            if (expression is Expressions.NamedExpression named)
                expression = named.Inner;
            while (expression.PureConversionOperand is { } inner)
            {
                if (this.TypeOf(expression) is { } converted && this.TypeOf(inner) is { } operand && converted.Category != operand.Category)
                    return false;
                expression = inner;
            }
            if (expression is not Expressions.Reference reference)
                return false;
            var (index, column) = FindSourceColumn(sources, reference.ReferencedName);
            if (index != sourceIndex)
                return false;
            var ordinal = source.StorageOrdinals is { } ordinals ? ordinals[column] : column;
            return leadingOrdinals.Contains(ordinal);
        }

        /// <summary>Whether <paramref name="expression"/> reads no column of the source.</summary>
        public bool IsValueSide(Expression expression)
        {
            var readsSource = false;
            expression.VisitColumnReferences(name => readsSource |= FindSourceColumn(sources, name).SourceIndex == sourceIndex);
            return !readsSource;
        }

        /// <summary>
        /// A seek column on one side and a value on the other, either way round
        /// — unless comparing them converts the column: a <c>varchar</c> column
        /// under a SQL collation against a Unicode value compares as
        /// <c>nvarchar</c>, which leaves nothing to seek on (a Windows
        /// collation keeps the seek).
        /// </summary>
        public bool IsColumnAgainstValue(Expression a, Expression b) =>
            (this.IsSeekColumn(a) && this.IsValueSide(b) && !this.ConvertsColumn(a, b))
            || (this.IsSeekColumn(b) && this.IsValueSide(a) && !this.ConvertsColumn(b, a));

        /// <summary>Whether comparing <paramref name="column"/> against <paramref name="value"/> converts the column away from its index.</summary>
        public bool ConvertsColumn(Expression column, Expression value) =>
            this.TypeOf(column) is { Category: SqlTypeCategory.String, Collation: { } collation } columnType
            && !SqlType.IsNationalStringCategory(columnType)
            && this.TypeOf(value) is { Category: SqlTypeCategory.String } valueType
            && SqlType.IsNationalStringCategory(valueType)
            && collation.Name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase);

        // The expression's type, or null where it can't be settled here — which
        // leaves the question to the permissive answer.
        private SqlType? TypeOf(Expression expression)
        {
            try
            {
                return expression.GetSqlType(batch, name => ResolveColumnTypeAcrossSources(sources, name, batch.Parser.OuterTypeResolver));
            }
            catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
