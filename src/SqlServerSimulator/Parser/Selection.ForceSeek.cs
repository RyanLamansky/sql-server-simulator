using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// The joined UPDATE / DELETE's form of the check, over its FROM clause
    /// and its WHERE.
    /// </summary>
    internal static void ValidateForcedSeeks(ParserContext context, FromSource[] sources, JoinSpec[] joins, BooleanExpression? where)
        => _ = ValidateForcedSeeks(context, sources, joins, where is null ? [] : [where], projections: null, soleSubqueryColumn: null, out _);

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
    /// <para>
    /// A derived table's or CTE's block (<see cref="ParserContext.ForceSeekDefersBlock"/>)
    /// answers true instead of refusing a seek it can't make alone, and the
    /// query reading it judges the body again with its own predicates, as
    /// real pushes them in (probed 2026-10-16 against SQL Server 2025).
    /// </para>
    /// </summary>
    private static bool ValidateForcedSeeks(
        ParserContext context,
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        List<Expression>? projections,
        Expression? soleSubqueryColumn,
        out bool leansOnEnclosingIn)
    {
        leansOnEnclosingIn = false;
        RecordJoinHintSite(context, sources, joins, excluders);
        var batch = context.Batch;
        if (batch.IsSkipping ? !batch.CompilingForRun : batch.CreateTimeBinding)
            return false;
        if (batch.CreateTimeBindErrors is { Count: > 0 })
            return false;
        var defers = context.ForceSeekDefersBlock;

        List<BooleanExpression>? conjuncts = null;
        List<int>? seekers = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].ForcedSeekThrough is { } through)
            {
                conjuncts ??= CollectConjuncts(excluders, joins);
                if (through.ForceSeekColumns is not null && sources[i].BackingView is { } view)
                    throw SimulatedSqlException.ForceSeekOnViewWithoutNoExpand(view.Name);
                if (!BodySeeksAreFeasible(batch, sources[i], i, sources, conjuncts, soleSubqueryColumn))
                    throw SimulatedSqlException.ForceSeekPlanInfeasible();
                (seekers ??= []).Add(i);
                continue;
            }
            if (sources[i] is { ForcedViewIndexSeek: { } viewHints, BackingView: { } indexedView })
            {
                // NOEXPAND reads the view's own index, which seeks as a
                // table's does (probed 2026-10-16 against SQL Server 2025).
                conjuncts ??= CollectConjuncts(excluders, joins);
                var viewLeads = new HashSet<int>();
                foreach (var index in indexedView.Indexes)
                {
                    if (!index.IsDisabled && index.KeyStorageOrdinals.Length != 0 && NamedByIndexHint(batch, viewHints, index.Name, index.IndexId))
                        _ = viewLeads.Add(index.KeyStorageOrdinals[0]);
                }
                var viewProbe = new ForceSeekProbe(batch, sources[i], i, sources, viewLeads);
                if (viewLeads.Count == 0 || !conjuncts.Exists(conjunct => conjunct.OffersSeek(viewProbe, negated: false)))
                {
                    if (defers)
                        return true;
                    throw SimulatedSqlException.ForceSeekPlanInfeasible();
                }
                continue;
            }
            if (sources[i] is { LateralIsQueryBody: true, LateralPlan.ForceSeekDeferred: true } && !IsApplyBody(joins, i))
            {
                conjuncts ??= CollectConjuncts(excluders, joins);
                if (!BodySeeksAreFeasible(batch, sources[i], i, sources, conjuncts, soleSubqueryColumn))
                {
                    if (defers)
                        return true;
                    throw SimulatedSqlException.ForceSeekPlanInfeasible();
                }
                (seekers ??= []).Add(i);
                continue;
            }
            if (sources[i] is not { BackingTable: { } table } source || (source.ForcedAccessPath is null && source.WrittenHints?.IndexArguments is null))
                continue;
            conjuncts ??= CollectConjuncts(excluders, joins);
            if (HintsUnimpliedFilteredIndex(batch, source, i, sources, table, conjuncts))
                throw SimulatedSqlException.ForceSeekPlanInfeasible();
            if (source.ForcedAccessPath is not { ForceSeek: true } && SpatialIndexHintRefusal(batch, source, i, sources, table, conjuncts) is { } spatialRefusal)
                throw spatialRefusal;
            if (source.WrittenHints is { IndexArguments.Count: > 0 } written)
            {
                table.SettleIndexIds();
                if (table.Indexes.Exists(named => NamedByIndexHint(batch, written, named.Name, named.IndexId) && UnusableUnderSessionOptions(batch, table, named)))
                    throw SimulatedSqlException.ForceSeekPlanInfeasible();
            }
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
            if (hints.ForceSeek && !ForcedSeekIsFeasible(batch, source, i, sources, table, hints, conjuncts, soleSubqueryColumn))
            {
                if (defers)
                    return true;
                throw SimulatedSqlException.ForceSeekPlanInfeasible();
            }
            if (!hints.ForceSeek && hints.ForceScan && !ForcedScanIsFeasible(batch, source, i, sources, table, hints, conjuncts, projections))
                throw SimulatedSqlException.ForceSeekPlanInfeasible();
            if (hints.ForceSeek)
            {
                (seekers ??= []).Add(i);
                // A seek only the enclosing IN's values make, which an outer
                // FORCESEEK on the IN's column then can't lean on in turn.
                if (soleSubqueryColumn is not null && !ForcedSeekIsFeasible(batch, source, i, sources, table, hints, conjuncts, soleSubqueryColumn: null))
                    leansOnEnclosingIn = true;
            }
        }

        if (seekers is { Count: > 1 } && !SeeksSpread(batch, sources, seekers, conjuncts!, soleSubqueryColumn))
        {
            if (defers)
                return true;
            throw SimulatedSqlException.ForceSeekPlanInfeasible();
        }
        return false;
    }

    /// <summary>
    /// What an <c>INDEX</c> hint naming a spatial index meets when no
    /// top-level conjunct is a predicate the index answers — a binary method
    /// of its column (or taking it) compared <c>= 1</c>, or its
    /// <c>STDistance</c> below a value: Msg 8635 for the first conjunct reading
    /// the column through a method that can't serve, the lowest state among
    /// several, Msg 8622 when none reads it so (probed 2026-10-19 against SQL
    /// Server 2025); null when one serves or no spatial index is named.
    /// </summary>
    private static SimulatedSqlException? SpatialIndexHintRefusal(BatchContext batch, FromSource source, int index, FromSource[] sources, HeapTable table, List<BooleanExpression> conjuncts)
    {
        if (source.WrittenHints?.IndexArguments is not { } arguments || table.SpatialIndexes.Count == 0)
            return null;
        var collation = batch.CurrentDatabase.Collation;
        var spatial = table.SpatialIndexes.Find(candidate => arguments.Exists(argument => argument.Name is { } name ? collation.Equals(candidate.Name, name) : argument.Id == candidate.IndexId));
        if (spatial is null)
            return null;
        var probe = new ForceSeekProbe(batch, source, index, sources, [spatial.ColumnOrdinal - 1]);
        byte? refusal = null;
        foreach (var conjunct in conjuncts)
        {
            switch (SpatialConjunctVerdict(conjunct, probe))
            {
                case 0:
                    return null;
                // The lowest state wins: a comparand over a comparator over a
                // method comparing nothing over an OR.
                case { } state when refusal is null || state < refusal:
                    refusal = state;
                    break;
            }
        }
        return refusal is { } reason ? SimulatedSqlException.SpatialIndexHintNoPlan(reason) : SimulatedSqlException.ForceSeekPlanInfeasible();
    }

    /// <summary>
    /// 0 when <paramref name="conjunct"/> is a predicate the spatial index
    /// answers, the Msg 8635 state naming why not when it reads the index's
    /// column through a spatial method, null when it doesn't.
    /// </summary>
    private static byte? SpatialConjunctVerdict(BooleanExpression conjunct, ForceSeekProbe probe)
    {
        // Only a method written on the comparison's left is a candidate:
        // `1 = s.STIntersects(@p)` and `5 > s.STDistance(@p)` find none.
        if (conjunct.TryGetEqualityOperands(out var left, out var right))
        {
            if (OnColumn(left, probe) is not { } call)
                return OnColumn(right, probe) is not null ? 9 : Mentions(conjunct, probe) ? 10 : null;
            if (!IsBinaryMethod(call.memberName))
                return BuiltInToken.Equals(call.memberName, "STDistance") ? (byte)4 : (byte)9;
            return right is Expressions.Value { Constant: { IsNull: false } one } && one.Type.Category == SqlTypeCategory.Integer && Expressions.MathScalars.AsLong(one) == 1 ? (byte)0 : (byte)3;
        }
        if (conjunct.TryGetRangeOperands(out var lower, out var op, out var upper))
        {
            if (OnColumn(lower, probe) is not { } call)
                return OnColumn(upper, probe) is not null ? 9 : Mentions(conjunct, probe) ? 10 : null;
            if (!BuiltInToken.Equals(call.memberName, "STDistance"))
                return IsBinaryMethod(call.memberName) ? (byte)4 : (byte)9;
            return op is RangeComparison.Less or RangeComparison.LessOrEqual ? (byte)0 : (byte)4;
        }
        return Mentions(conjunct, probe) ? 10 : null;

        static bool IsBinaryMethod(string member) =>
            BuiltInToken.EqualsAny(member, "Filter", "STContains", "STEquals", "STIntersects", "STOverlaps", "STTouches", "STWithin");

        // A spatial method reading the column: called on it, or, a binary
        // one, taking it as the other instance.
        static Expressions.SpatialMethodCall? OnColumn(Expression expression, ForceSeekProbe probe) =>
            expression is Expressions.SpatialMethodCall call
                && (probe.IsSeekColumn(call.target) || (call.arguments is [var argument, ..] && IsBinaryMethod(call.memberName) && probe.IsSeekColumn(argument)))
                ? call
                : null;

        static bool Mentions(BooleanExpression conjunct, ForceSeekProbe probe)
        {
            var found = false;
            conjunct.Walk((node, _) =>
            {
                found |= node is Expression expression && OnColumn(expression, probe) is not null;
                return !found;
            });
            return found;
        }
    }

    /// <summary>Whether source <paramref name="index"/> is an <c>APPLY</c>'s body, which a predicate of the reading query doesn't reach.</summary>
    private static bool IsApplyBody(JoinSpec[] joins, int index) =>
        index > 0 && index - 1 < joins.Length && joins[index - 1].Kind is JoinKind.CrossApply or JoinKind.OuterApply;

    /// <summary>
    /// Whether every <c>FORCESEEK</c> table — or view, CTE or derived table
    /// carrying the hint — can seek, where some seek only on predicates
    /// reading each other (Msg 8622 when not): a join's
    /// predicate seeks one side only once the rows of the other come from a
    /// seek of their own or from a source the hint doesn't bind, so seeking
    /// spreads from what a constant or an unhinted source anchors (probed
    /// 2026-10-16 against SQL Server 2025: <c>t1 WITH (FORCESEEK) JOIN t2
    /// WITH (FORCESEEK) ON t1.id = t2.id</c> is refused, and a
    /// <c>WHERE t1.id = 1</c> plans it).
    /// </summary>
    private static bool SeeksSpread(BatchContext batch, FromSource[] sources, List<int> seekers, List<BooleanExpression> conjuncts, Expression? soleSubqueryColumn)
    {
        var seeking = new bool[sources.Length];
        Array.Fill(seeking, true);
        foreach (var index in seekers)
            seeking[index] = false;
        var remaining = seekers.Count;
        bool progressed;
        do
        {
            progressed = false;
            foreach (var index in seekers)
            {
                if (seeking[index])
                    continue;
                var anchored = conjuncts.FindAll(conjunct => ReadsOnlySeekingSources(sources, conjunct, index, seeking));
                var source = sources[index];
                if (source.ForcedSeekThrough is not null || source.BackingTable is null
                    ? BodySeeksAreFeasible(batch, source, index, sources, anchored, soleSubqueryColumn)
                    : ForcedSeekIsFeasible(batch, source, index, sources, source.BackingTable, source.ForcedAccessPath!.Value, anchored, soleSubqueryColumn))
                {
                    seeking[index] = true;
                    progressed = true;
                    remaining--;
                }
            }
        }
        while (progressed && remaining > 0);
        return remaining == 0;
    }

    /// <summary>
    /// Whether a <c>FORCESEEK</c> table a hash or merge join reads on its
    /// inner side can seek without the join's predicate: on one of its own,
    /// or on one the optimizer implies from a predicate over the column the
    /// join equates it with (probed 2026-10-16 against SQL Server 2025:
    /// <c>t1 INNER HASH JOIN t2 WITH (FORCESEEK) ON t1.id = t2.id WHERE
    /// t1.id = 1</c> plans, the join's equality alone doesn't).
    /// </summary>
    private static bool SeeksWithoutJoin(BatchContext batch, FromSource[] sources, int index, List<BooleanExpression> conjuncts)
    {
        var source = sources[index];
        if (source.BackingTable is not { } table || source.ForcedAccessPath is not { } hints)
            return true;
        var alone = new bool[sources.Length];
        if (ForcedSeekIsFeasible(batch, source, index, sources, table, hints, conjuncts.FindAll(conjunct => ReadsOnlySeekingSources(sources, conjunct, index, alone)), soleSubqueryColumn: null))
            return true;
        if (hints.ForceSeekColumns is not null)
            return false;
        // The columns the joins' equalities chain a seek column to, each
        // probed for a predicate of its own: the optimizer implies the same
        // predicate on the seek column.
        var probe = new ForceSeekProbe(batch, source, index, sources, SeekableLeads(batch, table, hints));
        var equated = new List<(int Source, int Column)>();
        var frontier = new Queue<Expression>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var a, out var b))
            {
                if (probe.IsSeekColumn(a))
                    frontier.Enqueue(b);
                if (probe.IsSeekColumn(b))
                    frontier.Enqueue(a);
            }
        }
        while (frontier.TryDequeue(out var operand))
        {
            if (operand is not Expressions.Reference reference)
                continue;
            var (partner, column) = FindSourceColumn(sources, reference.ReferencedName);
            if (partner < 0 || partner == index || equated.Contains((partner, column)))
                continue;
            equated.Add((partner, column));
            var ordinal = sources[partner].StorageOrdinals is { } map ? map[column] : column;
            var partnerProbe = new ForceSeekProbe(batch, sources[partner], partner, sources, [ordinal]);
            foreach (var conjunct in conjuncts)
            {
                if (conjunct.TryGetEqualityOperands(out var a, out var b) && (partnerProbe.IsSeekColumn(a) || partnerProbe.IsSeekColumn(b)))
                {
                    var other = partnerProbe.IsSeekColumn(a) ? b : a;
                    if (other is Expressions.Reference)
                    {
                        frontier.Enqueue(other);
                        continue;
                    }
                }
                // Only a constant real folds implies a predicate: a variable or
                // @@SPID doesn't (probed 2026-10-16 against SQL Server 2025).
                var constant = true;
                conjunct.VisitOperandExpressions(operand => constant &= partnerProbe.IsSeekColumn(operand) || operand.IsWrittenConstant);
                if (constant && conjunct.ImpliedThroughEquality && ReadsOnlySeekingSources(sources, conjunct, partner, alone) && conjunct.OffersSeek(partnerProbe, negated: false))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Whether every column <paramref name="conjunct"/> reads is the seeking source's own, an outer one, or one of a source already seeking.</summary>
    private static bool ReadsOnlySeekingSources(FromSource[] sources, BooleanExpression conjunct, int sourceIndex, bool[] seeking)
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
            (string Name, int Lead, int IndexId, bool Disabled) described = owner switch
            {
                KeyConstraint key => (key.Name, key.StorageOrdinals is [var first, ..] ? first : -1, key.IndexId, key.IsDisabled),
                // A non-persisted computed key has no storage slot, so it
                // leads by its stand-in (see ForceSeekProbe.IsSeekColumn).
                Storage.Index index => (index.Name, index.KeyStorageOrdinals is [var first, ..] ? first >= 0 ? first : UnstoredLead(index.KeyFullOrdinals[0]) : -1, index.IndexId, index.IsDisabled),
                _ => ("", -1, -1, true),
            };
            var (name, lead, indexId, disabled) = described;
            if (!disabled && lead != -1 && NamedByIndexHint(batch, hints, name, indexId)
                && !(owner is Storage.Index computedKeyed && UnusableUnderSessionOptions(batch, table, computedKeyed)))
            {
                _ = leads.Add(lead);
            }
        }
        return leads;
    }

    /// <summary>
    /// The seek lead standing for a key column with no storage slot — a
    /// non-persisted computed column, which real's index seeks as any other
    /// (probed 2026-10-19 against SQL Server 2025) — by its position among the
    /// table's columns, below every storage ordinal and the -1 of none.
    /// </summary>
    private static int UnstoredLead(int fullOrdinal) => -2 - fullOrdinal;

    /// <summary>
    /// Whether real's optimizer leaves <paramref name="index"/> out under the
    /// session's options: one keyed on a computed column, persisted or not,
    /// needs the options an index on one does to be built (Msg 1934's set), or
    /// no hint can use it (probed 2026-10-19 against SQL Server 2025).
    /// </summary>
    private static bool UnusableUnderSessionOptions(BatchContext batch, HeapTable table, Storage.Index index) =>
        Array.Exists(index.KeyFullOrdinals, ordinal => ordinal >= 0 && table.Columns[ordinal].Computed is not null)
        && Simulation.IncorrectSetOptionNames(batch.Parser) is not null;

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
        return NestedBody(batch, source) is not { } body || ShapeCanSeek(batch, body, source.ForcedSeekThrough, ReaderSeeks);

        bool ReaderSeeks(HashSet<int> columns)
        {
            var probe = new ForceSeekProbe(batch, source, sourceIndex, all, columns);
            return (soleSubqueryColumn is not null && probe.IsSeekColumn(soleSubqueryColumn))
                || conjuncts.Exists(conjunct => conjunct.OffersSeek(probe, negated: false));
        }
    }

    /// <summary>
    /// The shape of the query body a view, CTE, derived-table or inline
    /// function source reads, or null for any other source or an unrecorded
    /// one. An inline function's body is parsed here, as the hint reaches it.
    /// </summary>
    private static SeekBodyShape? NestedBody(BatchContext batch, FromSource source) =>
        source.BodySeekShape
            ?? (source.LateralIsQueryBody ? source.LateralPlan?.SeekShape
            : source.InlineCall is var (function, arguments, name) ? Simulation.InlineTvfSeekShape(batch, function, arguments, name)
            : null);

    /// <summary>
    /// Whether every table <paramref name="shape"/> reads that has to seek
    /// can, given <paramref name="readerSeeks"/>, which answers whether the
    /// reading query seeks on one of a set of the shape's output columns. A
    /// set operation needs each branch to. With <paramref name="carried"/>
    /// — a view's or CTE reference's hint — every table has to seek under
    /// it; without, only the tables the body hints itself, and a nested body
    /// that couldn't honor its own alone.
    /// </summary>
    private static bool ShapeCanSeek(BatchContext batch, SeekBodyShape shape, TableHintInfo? carried, Func<HashSet<int>, bool> readerSeeks)
    {
        if (shape.Branches is { } branches)
            return Array.TrueForAll(branches, branch => ShapeCanSeek(batch, branch, carried, readerSeeks));
        var sources = shape.Sources;
        var conjuncts = CollectConjuncts(shape.Filters, shape.Joins);
        var leads = new HashSet<int>?[sources.Length];
        var bodies = new SeekBodyShape?[sources.Length];
        var seeking = new bool[sources.Length];
        var remaining = 0;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].BackingTable is { } table)
            {
                if ((carried ?? sources[i].ForcedAccessPath) is not { ForceSeek: true } hints)
                {
                    seeking[i] = true;
                    continue;
                }
                // A heap or a table with no enabled key has nothing to seek.
                table.SettleIndexIds();
                leads[i] = SeekableLeads(batch, table, hints);
                if (leads[i]!.Count == 0)
                    return false;
                remaining++;
            }
            else if ((carried is not null || sources[i] is { LateralPlan.ForceSeekDeferred: true }) && (bodies[i] = NestedBody(batch, sources[i])) is not null)
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
                if (leads[i] is { } own ? ColumnsSeek(source, own) : ShapeCanSeek(batch, bodies[i]!, carried, columns => ColumnsSeek(source, columns)))
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
                if (conjunct.OffersSeek(probe, negated: false) && ReadsOnlySeekingSources(sources, conjunct, sourceIndex, seeking))
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
            if (windows.Count == 0 || (expression is Expressions.Reference column && EveryWindowPartitionsBy(collation, windows, column)))
                reachable[p] = expressions[p];
        }
        return reachable;
    }

    private static bool EveryWindowPartitionsBy(Collation collation, List<Expressions.WindowExpression> windows, Expressions.Reference column)
    {
        var name = column.ReferencedName.ToString();
        foreach (var window in windows)
        {
            if (!Array.Exists(window.PartitionBy, key => key is Expressions.Reference partition && collation.Equals(partition.ReferencedName.ToString(), name)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a <c>FORCESEEK</c> this derived table's or CTE's body hints
    /// couldn't be honored by the body alone, which leaves it to the query
    /// reading the body (<see cref="ParserContext.ForceSeekDefersBlock"/>).
    /// </summary>
    internal bool ForceSeekDeferred;

    /// <summary>
    /// Whether this one-column subquery's <c>FORCESEEK</c> seeks only on the
    /// values an enclosing <c>IN</c> hands it, so the <c>IN</c> offers no seek
    /// to a <c>FORCESEEK</c> on its own column (probed 2026-10-16 against SQL
    /// Server 2025: <c>t1 WITH (FORCESEEK) WHERE id IN (SELECT id FROM t2
    /// WITH (FORCESEEK))</c> is refused).
    /// </summary>
    internal bool ForceSeekLeansOnIn;

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
            return leadingOrdinals.Contains(ordinal < 0 ? UnstoredLead(column) : ordinal);
        }

        /// <summary>
        /// Whether <paramref name="predicate"/> — under a <c>NOT</c> when
        /// <paramref name="negated"/> — is a written constant settled never
        /// TRUE as the statement compiles; one that raises as it folds is not.
        /// </summary>
        public bool SettlesNeverTrue(BooleanExpression predicate, bool negated)
        {
            if (!predicate.IsWrittenConstant)
                return false;
            bool? value;
            try
            {
                value = predicate.Run(new RuntimeContext(static _ => throw new NotSupportedException(), batch));
            }
            catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
            {
                return false;
            }
            return negated ? value != false : value != true;
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

        /// <summary>
        /// Whether <paramref name="column"/> reads as a string, which a
        /// <c>LIKE</c> seeks on: one over a number or a date converts the
        /// column, so offers none (probed 2026-10-19 against SQL Server 2025).
        /// </summary>
        public bool ReadsAsString(Expression column) => this.TypeOf(column) is not { } type || type.Category == SqlTypeCategory.String;

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
