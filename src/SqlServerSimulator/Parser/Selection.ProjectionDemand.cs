using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// The select-list columns a reading query never names, which a derived
// table's, view's, CTE's or inline function's body then leaves uncomputed.
partial class Selection
{
    /// <summary>
    /// The columns some reader of a view or inline function reference reads,
    /// handed to the body each time it is parsed for a run (see
    /// <see cref="AcceptDemand"/>); null when every column is read.
    /// </summary>
    internal bool[]? ReaderDemand;

    /// <summary>
    /// Whether this plan is a view or inline function reference, whose body
    /// takes <see cref="ReaderDemand"/> once it is parsed for a run.
    /// </summary>
    private bool takesReaderDemand;

    /// <summary>
    /// What a query built by <see cref="BuildSqlProjection"/> reads for each
    /// of its select-list columns, which <see cref="AcceptDemand"/> narrows to
    /// the columns its reader reads; null for every other plan.
    /// </summary>
    private ProjectionDemand? projectionDemand;

    /// <summary>
    /// The names this query reads that none of its own sources binds — an
    /// enclosing query's columns, among others — which an enclosing query
    /// counts as reads of its own sources; null when this plan's reach isn't
    /// known (<see cref="ReachOf"/>).
    /// </summary>
    private MultiPartName[]? freeNames;

    /// <summary>
    /// The deferred form of each select-list column of a plan whose body this
    /// partial records no demand for — a view reference's, taken from the body
    /// the reference bound (see <see cref="DeferringAs"/>), or a one-row
    /// <c>VALUES</c> list's (see <see cref="DeferringValues"/>); null for every
    /// other plan.
    /// </summary>
    private DeferredColumn?[]? offeredColumns;

    /// <summary>
    /// Per select-list column, the form a query reading this plan as a FROM
    /// source may evaluate the column in itself, where it reads it, rather than
    /// have this plan compute it per row (see <see cref="DeferredColumn"/>);
    /// null for a column this plan must compute, and null outright for a plan
    /// whose body isn't known here.
    /// </summary>
    internal DeferredColumn?[]? DeferrableColumns()
    {
        if (this.IsForClauseDocument)
            return null;
        return this.projectionDemand is { } demand ? demand.Templates() : this.offeredColumns;
    }

    /// <summary>
    /// Leaves uncomputed each select-list column <paramref name="read"/> says
    /// no reader reads (null: every column is read), as real's optimizer never
    /// computes a column nothing above it reads: <c>SELECT COUNT(*) FROM
    /// (SELECT k, 10 / (k - 3) AS x FROM src) d</c> answers there (probed
    /// 2026-10-17 against SQL Server 2025). Answers whether the plan takes the
    /// demand at all; a plan that doesn't computes every column.
    /// </summary>
    internal bool AcceptDemand(bool[]? read)
    {
        if (this.projectionDemand is { } demand)
            return !this.IsForClauseDocument && demand.Apply(read);
        if (this.takesReaderDemand)
        {
            this.ReaderDemand = read;
            return read is not null;
        }
        // A UNION ALL hands each branch the demand, its own rows being theirs
        // unchanged; every other set operation compares whole rows.
        if (this.UnionAllBranches is { } branches && !this.HasOrderBy && !this.HasTopOrOffsetOrFetch)
        {
            var takes = false;
            foreach (var branch in branches)
                takes |= branch.AcceptDemand(read);
            return takes;
        }
        return false;
    }

    /// <summary>
    /// Records what a FROM-less query reads — names only an enclosing or
    /// <c>APPLY</c>-left row can bind — and, when its select list is evaluated
    /// per run rather than settled while it parsed (<paramref name="prunable"/>),
    /// lets a reader leave the columns it never reads uncomputed:
    /// <c>CROSS APPLY (SELECT 10 / (t.k - 3) AS x) a</c> computes nothing when
    /// nothing reads <c>a.x</c> (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private Selection NotingSynthesizedDemand(List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, bool prunable)
    {
        var collector = new ProjectionDemand.Collector([], expressions.Count, [], []);
        for (var i = 0; i < expressions.Count; i++)
            collector.Walk(expressions[i], ProjectionDemand.Collector.ProjectionSlot(i));
        foreach (var excluder in excluders)
            collector.Walk(excluder, ProjectionDemand.Always);
        this.freeNames = collector.Unknown ? null : [.. collector.FreeNames];
        if (prunable)
        {
            var selfRead = new bool[expressions.Count];
            foreach (var term in orderBy)
            {
                if (term.IsOrdinal && term.Ordinal - 1 is >= 0 and var ordinal && ordinal < selfRead.Length)
                    selfRead[ordinal] = true;
            }
            this.projectionDemand = collector.Finish(this.Schema, this.ColumnNames, expressions, selfRead, readsWholeRow: orderBy.Exists(static term => !term.IsOrdinal), [], [], ownsStartupConstant: null, lateral: []);
        }
        return this;
    }

    /// <summary>
    /// Hands the sources of a joined <c>UPDATE</c> or <c>DELETE</c> what the
    /// statement reads of them — <paramref name="reads"/>, its ON predicates
    /// and whatever its sources reach — so a derived table, view or CTE it
    /// joins leaves the rest uncomputed, as the same body under a SELECT does.
    /// The target at <paramref name="targetIndex"/> is written through by
    /// position and computes everything.
    /// </summary>
    internal static void NoteMutationDemand(FromSource[] sources, JoinSpec[] joins, int targetIndex, List<ExpressionNode> reads)
    {
        var collector = new ProjectionDemand.Collector(sources, 0, [], []);
        if (targetIndex >= 0 && targetIndex < sources.Length)
            collector.Prunable[targetIndex] = false;
        foreach (var read in reads)
            collector.Walk(read, ProjectionDemand.Always);
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                collector.Walk(on, ProjectionDemand.Always);
        }
        foreach (var source in sources)
            collector.ReadReachOf(source);
        _ = collector.Finish([], [], [], [], readsWholeRow: false, [], [], ownsStartupConstant: null, LateralSlots(sources, joins)).Apply(null);
    }

    /// <summary>
    /// Per column of the target at <paramref name="targetIndex"/> that a
    /// joined write reads its rows through (a view, CTE or derived table),
    /// whether the statement reads it — <paramref name="reads"/>, its ON
    /// predicates, what its other sources reach — so the body may leave the
    /// rest uncomputed; null when something reads what no walk sees.
    /// </summary>
    internal static bool[]? TargetColumnReads(FromSource[] sources, JoinSpec[] joins, int targetIndex, List<ExpressionNode> reads)
    {
        var state = (Sources: sources, Target: targetIndex, Read: new bool[sources[targetIndex].ColumnNames.Length], Known: true);
        static void Name(MultiPartName name, ref (FromSource[] Sources, int Target, bool[] Read, bool Known) state)
        {
            if (TryBindSourceColumn(state.Sources, name) is (var source, var column) && source == state.Target && column < state.Read.Length)
                state.Read[column] = true;
        }
        static void Reach(MultiPartName[]? names, ref (FromSource[] Sources, int Target, bool[] Read, bool Known) state)
        {
            if (names is null)
            {
                state.Known = false;
                return;
            }
            foreach (var name in names)
                Name(name, ref state);
        }
        var roots = new List<ExpressionNode>(reads);
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                roots.Add(on);
        }
        for (var s = 0; s < sources.Length; s++)
        {
            if (s == targetIndex || sources[s] is not { LateralPlan: { } plan, IsPlaceholder: false, BackingView: null, BackingCatalogView: null } source)
                continue;
            if (source.InlineCall is var (_, arguments, _))
            {
                foreach (var argument in arguments)
                {
                    if (argument is not null)
                        roots.Add(argument);
                }
            }
            else
            {
                Reach(ReachOf(plan), ref state);
            }
        }
        foreach (var root in roots)
        {
            root.Walk(ref state, static (node, shape, ref state) =>
            {
                if (shape.Column is { } name)
                    Name(name, ref state);
                foreach (var local in shape.Locals)
                {
                    if (local is Selection subquery)
                        Reach(ReachOf(subquery), ref state);
                }
                return true;
            });
        }
        return state.Known ? state.Read : null;
    }

    /// <summary>
    /// Hands the sources of a <c>MERGE</c>'s joined <c>USING</c> clause the
    /// columns the statement reads of them — <paramref name="read"/>, over
    /// every source's columns in turn (null: all of them) — beside what its
    /// ON predicates and its sources reach.
    /// </summary>
    internal static void NoteMergeSourceDemand(FromSource[] sources, JoinSpec[] joins, bool[]? read)
    {
        var collector = new ProjectionDemand.Collector(sources, 0, [], []);
        var k = 0;
        for (var s = 0; s < sources.Length; s++)
        {
            for (var c = 0; c < sources[s].ColumnNames.Length; c++, k++)
            {
                if (collector.Prunable[s] && (read is null || read[k]))
                    collector.Reads.Add((ProjectionDemand.Always, s, c));
            }
        }
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                collector.Walk(on, ProjectionDemand.Always);
        }
        foreach (var source in sources)
            collector.ReadReachOf(source);
        _ = collector.Finish([], [], [], [], readsWholeRow: false, [], [], ownsStartupConstant: null, LateralSlots(sources, joins)).Apply(null);
    }

    /// <summary>
    /// The startup constants of the select-list columns this plan computes —
    /// <paramref name="constants"/> itself when it computes them all — each
    /// constant's column given by <paramref name="owners"/>.
    /// </summary>
    private List<Expression> ComputedColumnConstants(List<Expression> constants, List<int> owners)
    {
        if (constants.Count == 0 || this.projectionDemand is not { Narrowed: true } demand)
            return constants;
        var computed = new List<Expression>(constants.Count);
        for (var i = 0; i < constants.Count; i++)
        {
            if (!demand.IsUncomputed(owners[i]))
                computed.Add(constants[i]);
        }
        return computed;
    }

    /// <summary>
    /// The select list a cursor re-reads this plan's rows through:
    /// <paramref name="written"/>, the list as the query wrote it, unless a
    /// reader leaves some of its columns uncomputed.
    /// </summary>
    private Expression[] ComputedProjection(Expression[] written) =>
        this.projectionDemand is { Narrowed: true } demand ? demand.CurrentProjection : written;

    /// <summary>
    /// The windows this plan computes, with their operand and result types —
    /// the ones it was built with unless a reader leaves some unread.
    /// </summary>
    private (List<WindowExpression> Windows, SqlType[] OperandTypes, SqlType[] ResultTypes) ComputedWindows(List<WindowExpression> windows, SqlType[] operandTypes, SqlType[] resultTypes) =>
        this.projectionDemand?.LiveWindows ?? (windows, operandTypes, resultTypes);

    /// <summary>
    /// Marks this plan as a view or inline function reference, which carries
    /// <see cref="ReaderDemand"/> to each parse of its body.
    /// </summary>
    private Selection TakingReaderDemand(bool[]? demand)
    {
        this.takesReaderDemand = true;
        this.ReaderDemand = demand;
        return this;
    }

    /// <summary>
    /// Hands a view reference the deferred columns of <paramref name="body"/>,
    /// the view's body as the reference bound it — each run parses the same
    /// definition again, laying its columns out the same way.
    /// </summary>
    internal Selection DeferringAs(Selection? body)
    {
        if (body?.DeferrableColumns() is { } columns && columns.Length == this.Schema.Length)
            this.offeredColumns = columns;
        return this;
    }

    /// <summary>
    /// Offers a one-row <c>VALUES</c> list's cells to its reader, which merges
    /// the row into itself as it does a FROM-less <c>SELECT</c>: <c>CROSS
    /// APPLY (VALUES (t.k, 10 / (t.k - 3))) v(kk, x) WHERE kk &lt;&gt; 3</c>
    /// answers on real (probed 2026-10-17 against SQL Server 2025). A longer
    /// list is a constant scan computing every row, which offers none.
    /// </summary>
    private Selection DeferringValues(List<Expression[]> tuples)
    {
        if (tuples is not [var row])
            return this;
        DeferredColumn?[]? offered = null;
        for (var c = 0; c < row.Length && c < this.Schema.Length; c++)
        {
            if (DeferredColumn.Of(row[c], this.Schema[c], static _ => DeferredBinding.FreeName) is { } column)
                (offered ??= new DeferredColumn?[this.Schema.Length])[c] = column;
        }
        this.offeredColumns = offered;
        return this;
    }

    /// <summary>
    /// Per source, whether it is the right side of an <c>APPLY</c>, whose body
    /// reads the sources to its left.
    /// </summary>
    private static bool[] LateralSlots(FromSource[] sources, JoinSpec[] joins)
    {
        var lateral = new bool[sources.Length];
        for (var s = 1; s < sources.Length && s - 1 < joins.Length; s++)
            lateral[s] = joins[s - 1].Kind is JoinKind.CrossApply or JoinKind.OuterApply;
        return lateral;
    }

    /// <summary>
    /// <paramref name="name"/>'s column among <paramref name="sources"/>,
    /// whether or not its body computes it; null when none binds the name and
    /// <c>(-1, -1)</c> when it is ambiguous among them.
    /// </summary>
    internal static (int Source, int Column)? TryBindSourceColumn(FromSource[] sources, MultiPartName name)
    {
        try
        {
            var (source, column) = FindSourceColumn(sources, name, reads: false);
            return source < 0 ? null : (source, column);
        }
        catch (SimulatedSqlException)
        {
            return (-1, -1);
        }
    }

    /// <summary>
    /// The names <paramref name="plan"/> reads from outside its own sources,
    /// or null when its reach isn't known — a plan built by a path that
    /// records none (a rowset function, a multi-statement function), which
    /// may read anything an enclosing query has.
    /// </summary>
    internal static MultiPartName[]? ReachOf(Selection plan)
    {
        if (plan.freeNames is { } names)
            return names;
        if (plan.SetOperands is var (left, right, _) && ReachOf(left) is { } leftNames && ReachOf(right) is { } rightNames)
            return [.. leftNames, .. rightNames];
        return null;
    }

    /// <summary>
    /// Records what each select-list column of the query just built reads, and
    /// hands its sources the columns the query reads of them — every select
    /// list column counted read until a reader of this query says otherwise.
    /// </summary>
    private static void NoteProjectionDemand(
        Selection selection,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        List<OrderBySpec> orderBy,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        SqlType[] windowOperandTypes,
        SqlType[] windowResultTypes,
        bool distinct,
        Expression? topExpression,
        string[] outputColumnNames,
        List<int> startupConstantOwners)
    {
        var collector = new ProjectionDemand.Collector(sources, expressions.Count, aggregates, windows);
        for (var i = 0; i < expressions.Count; i++)
            collector.Walk(expressions[i], ProjectionDemand.Collector.ProjectionSlot(i));
        foreach (var excluder in fromClause.Excluders)
            collector.Walk(excluder, ProjectionDemand.Always);
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                collector.Walk(on, ProjectionDemand.Always);
        }
        foreach (var grouping in fromClause.AllGroupingExpressions)
            collector.Walk(grouping, ProjectionDemand.Always);
        foreach (var filter in fromClause.GroupByAllFilter ?? [])
            collector.Walk(filter, ProjectionDemand.Always);
        if (fromClause.Having is { } having)
            collector.Walk(having, ProjectionDemand.Always);
        foreach (var term in orderBy)
        {
            if (term.Expr is { } key)
                collector.Walk(key, ProjectionDemand.Always);
        }
        foreach (var limit in (ReadOnlySpan<Expression?>)[topExpression, fromClause.OffsetExpression, fromClause.FetchExpression])
        {
            if (limit is not null)
                collector.Walk(limit, ProjectionDemand.Always);
        }
        // A MATCH reads its graph tables through hidden columns no walk names.
        if (fromClause.Match is not null)
            collector.ReadEverything(ProjectionDemand.Always);
        for (var s = 0; s < sources.Length; s++)
            collector.ReadReachOf(sources[s]);
        collector.WalkUnreached();

        // The columns the query itself reads off its own select list: an
        // ORDER BY ordinal or alias, and every column a DISTINCT compares —
        // save one over a key, whose duplicates can't occur, so real drops it
        // and needs only the key.
        var selfRead = new bool[expressions.Count];
        foreach (var term in orderBy)
        {
            if (term.IsOrdinal)
            {
                if (term.Ordinal - 1 is >= 0 and var ordinal && ordinal < selfRead.Length)
                    selfRead[ordinal] = true;
                continue;
            }
            term.Expr!.VisitColumnReferences(name =>
            {
                if (name.ImmediateQualifier is not null)
                    return;
                for (var j = 0; j < outputColumnNames.Length && j < selfRead.Length; j++)
                {
                    if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                        selfRead[j] = true;
                }
            });
        }
        var readsWholeRow = selection.Schema.Length != expressions.Count;
        if (distinct)
        {
            if (DeriveOutputKeys(sources, joins, fromClause, expressions, distinct: false, aggregates: false) is [var key, ..])
            {
                foreach (var ordinal in key)
                    selfRead[ordinal] = true;
            }
            else
            {
                readsWholeRow = true;
            }
        }
        // A browse statement's or a graph identity's hidden trailing columns
        // are read by position, never by name.
        for (var i = Math.Max(0, expressions.Count - selection.TrailingHiddenColumnCount); i < expressions.Count; i++)
            selfRead[i] = true;

        selection.freeNames = collector.Unknown ? null : [.. collector.FreeNames];
        // A column holding a constant real runs as its plan starts stays in
        // the body, where that constant runs whenever the column is computed.
        var ownsStartupConstant = new bool[expressions.Count];
        foreach (var owner in startupConstantOwners)
            ownsStartupConstant[owner] = true;
        selection.projectionDemand = collector.Finish(selection.Schema, outputColumnNames, expressions, selfRead, readsWholeRow, windowOperandTypes, windowResultTypes, ownsStartupConstant, LateralSlots(sources, joins));
        _ = selection.projectionDemand.Apply(null);
    }
}

/// <summary>
/// What each select-list column of one query reads of the query's sources,
/// aggregates and windows, so a reader that reads only some of the columns
/// lets the query leave the others — and whatever only they read — uncomputed.
/// </summary>
/// <remarks>
/// Reads are attributed to <em>slots</em>: <see cref="Always"/> for every
/// clause that runs whatever the select list holds (WHERE, ON, GROUP BY,
/// HAVING, ORDER BY), one per select-list column, one per window and one per
/// aggregate — a window or aggregate live while any slot holding it is. A
/// narrowed demand mutates the query's select list in place, a column no one
/// reads becoming a typed NULL; every path that runs the query — its own rows,
/// a pushed-down variant, a row-number bound — reads that same list, so all of
/// them leave it out.
/// </remarks>
internal sealed class ProjectionDemand
{
    public const int Always = 0;

    private readonly FromSource[] sources;
    private readonly bool[] prunable;
    private readonly (int Slot, int Source, int Column)[] reads;
    private readonly List<Expression> projection;
    private readonly Expression[] written;
    private readonly Expression?[] uncomputed;
    private readonly SqlType[] schema;
    private readonly string[] columnNames;
    private readonly bool[] selfRead;
    private readonly bool readsWholeRow;
    private readonly List<AggregateExpression> aggregates;
    private readonly int[][] aggregateOwners;
    private readonly List<WindowExpression> windows;
    private readonly int[][] windowOwners;
    private readonly SqlType[] windowOperandTypes;
    private readonly SqlType[] windowResultTypes;
    private readonly bool[]? ownsStartupConstant;
    private readonly bool[] lateral;
    private DeferredColumn?[]? templates;
    private bool templatesSettled;

    /// <summary>
    /// The windows a reader reads, with their operand and result types, once
    /// <see cref="Apply"/> found some unread; null while every window is read.
    /// </summary>
    public (List<WindowExpression> Windows, SqlType[] OperandTypes, SqlType[] ResultTypes)? LiveWindows;

    /// <summary>Whether the last <see cref="Apply"/> left any column uncomputed.</summary>
    public bool Narrowed;

    private ProjectionDemand(Collector collector, SqlType[] schema, string[] columnNames, List<Expression> projection, bool[] selfRead, bool readsWholeRow, SqlType[] windowOperandTypes, SqlType[] windowResultTypes, bool[]? ownsStartupConstant, bool[] lateral)
    {
        this.ownsStartupConstant = ownsStartupConstant;
        this.lateral = lateral;
        this.columnNames = columnNames;
        this.sources = collector.Sources;
        this.prunable = collector.Prunable;
        this.reads = [.. collector.Reads];
        this.projection = projection;
        this.written = [.. projection];
        this.uncomputed = new Expression?[projection.Count];
        this.schema = schema;
        this.selfRead = selfRead;
        this.readsWholeRow = readsWholeRow;
        this.aggregates = collector.Aggregates;
        this.aggregateOwners = Array.ConvertAll(collector.AggregateOwners, static owners => owners is { Count: > 0 } ? [.. owners] : new[] { Always });
        this.windows = collector.Windows;
        this.windowOwners = Array.ConvertAll(collector.WindowOwners, static owners => owners is { Count: > 0 } ? [.. owners] : new[] { Always });
        this.windowOperandTypes = windowOperandTypes;
        this.windowResultTypes = windowResultTypes;
    }

    /// <summary>The select list as the last <see cref="Apply"/> left it.</summary>
    public Expression[] CurrentProjection => [.. this.projection];

    /// <summary>Whether the select-list column at <paramref name="index"/> is left uncomputed.</summary>
    public bool IsUncomputed(int index) => !ReferenceEquals(this.projection[index], this.written[index]);

    /// <summary>
    /// Narrows the select list to the columns <paramref name="read"/> names
    /// (null: all of them) beside the ones the query reads itself, and hands
    /// each source what the live columns and clauses read of it. Answers
    /// whether any column is left uncomputed.
    /// </summary>
    public bool Apply(bool[]? read)
    {
        var count = this.written.Length;
        var windowBase = 1 + count;
        var aggregateBase = windowBase + this.windows.Count;
        var slotLive = new bool[aggregateBase + this.aggregates.Count];
        slotLive[Always] = true;
        var narrowed = false;
        for (var i = 0; i < count; i++)
        {
            var live = this.readsWholeRow || read is null || i >= read.Length || read[i] || this.selfRead[i];
            slotLive[1 + i] = live;
            narrowed |= !live;
            this.projection[i] = live ? this.written[i] : this.uncomputed[i] ??= new NamedExpression(new Value(SqlValue.Null(this.schema[i])), this.columnNames[i]);
        }
        var windowsUnread = false;
        for (var w = 0; w < this.windows.Count; w++)
        {
            slotLive[windowBase + w] = AnyLive(this.windowOwners[w], slotLive);
            windowsUnread |= !slotLive[windowBase + w];
        }
        this.LiveWindows = windowsUnread ? this.WindowsLiveIn(slotLive, windowBase) : null;
        for (var a = 0; a < this.aggregates.Count; a++)
        {
            var live = AnyLive(this.aggregateOwners[a], slotLive);
            slotLive[aggregateBase + a] = live;
            this.aggregates[a].ResultUnread = !live;
        }

        var masks = new bool[this.sources.Length][];
        for (var s = 0; s < this.sources.Length; s++)
        {
            if (!this.prunable[s])
                continue;
            var columns = this.sources[s].Columns;
            var mask = new bool[columns.Length];
            for (var c = 0; c < columns.Length; c++)
                mask[c] = columns[c].IsHidden;
            masks[s] = mask;
        }
        foreach (var (slot, source, column) in this.reads)
        {
            if (!slotLive[slot])
                continue;
            if (column < 0)
                Array.Fill(masks[source], true);
            else
                masks[source][column] = true;
        }
        for (var s = 0; s < this.sources.Length; s++)
        {
            if (masks[s] is { } mask)
                Hand(this.sources[s], mask, this.DeferredAt(s, mask));
        }
        this.Narrowed = narrowed;
        return narrowed;
    }

    /// <summary>
    /// The columns of the source at <paramref name="slot"/> this query
    /// evaluates itself where it reads them (see <see cref="DeferredColumn"/>),
    /// moving each from <paramref name="mask"/> — the columns its body
    /// computes — to the columns it reads there instead; null when it defers
    /// none.
    /// </summary>
    private DeferredColumn?[]? DeferredAt(int slot, bool[] mask)
    {
        if (SourceTemplates(this.sources[slot]) is not { } offered)
            return null;
        DeferredColumn?[]? deferred = null;
        for (var c = 0; c < mask.Length && c < offered.Length; c++)
        {
            if (mask[c] && offered[c]?.BindAtReader(this.sources, slot, this.lateral.Length > slot && this.lateral[slot]) is { } bound)
                (deferred ??= new DeferredColumn?[mask.Length])[c] = bound;
        }
        DeferredColumn.ComputeDependenciesInstead(mask, deferred);
        return deferred;
    }

    /// <summary>The deferred columns the body behind <paramref name="source"/> offers.</summary>
    private static DeferredColumn?[]? SourceTemplates(FromSource source) =>
        source.Cte is { } binding ? binding.Plan?.DeferrableColumns() : source.LateralPlan?.DeferrableColumns();

    /// <summary>
    /// Per select-list column, the form a reader may evaluate it in itself
    /// (see <see cref="Selection.DeferrableColumns"/>): a column the query
    /// doesn't read itself, holding no constant real runs as its plan starts,
    /// whose expression reads only columns the query projects as they are —
    /// directly or through a source's own deferred column — or names from
    /// outside the query. Settled once, the first time a reader asks.
    /// </summary>
    public DeferredColumn?[]? Templates()
    {
        if (this.templatesSettled)
            return this.templates;
        var count = this.written.Length;
        DeferredColumn?[]? result = null;
        if (!this.readsWholeRow)
        {
            var plain = new (int Source, int Column)?[count];
            for (var j = 0; j < count; j++)
            {
                if (Unwrap(this.written[j]) is Reference reference && Selection.TryBindSourceColumn(this.sources, reference.ReferencedName) is { Source: >= 0 } id)
                    plain[j] = id;
            }
            for (var i = 0; i < count; i++)
            {
                if (this.selfRead[i] || this.ownsStartupConstant?[i] == true)
                    continue;
                var template = plain[i] is var (source, column)
                    ? this.Lift(source, column, plain)
                    : DeferredColumn.Of(Unwrap(this.written[i]), this.schema[i], name => this.BindInBody(name, plain));
                if (template is not null)
                    (result ??= new DeferredColumn?[count])[i] = template;
            }
        }
        this.templates = result;
        this.templatesSettled = true;
        return result;
    }

    private static Expression Unwrap(Expression expression) => expression is NamedExpression named ? named.Inner : expression;

    /// <summary>
    /// Where a deferred column of this query finds <paramref name="name"/>: a
    /// column of one of its sources, or — binding none of them — a name from
    /// outside the query, placed once a reader binds the column.
    /// </summary>
    private DeferredBinding? BindInBody(MultiPartName name, (int Source, int Column)?[] plain) =>
        Selection.TryBindSourceColumn(this.sources, name) switch
        {
            null => DeferredBinding.FreeName,
            (-1, _) => null,
            var (source, column) => this.BindSourceColumn(source, column, plain),
        };

    /// <summary>
    /// Where a deferred column of this query finds column
    /// <paramref name="column"/> of source <paramref name="source"/>: that
    /// source's own deferred column, lifted to this query's row, or the select
    /// list column projecting it as it is.
    /// </summary>
    private DeferredBinding? BindSourceColumn(int source, int column, (int Source, int Column)?[] plain)
    {
        if (this.prunable[source] && SourceTemplates(this.sources[source]) is { } offered && column < offered.Length && offered[column] is not null)
            return this.Lift(source, column, plain) is { } lifted ? DeferredBinding.NestedColumn(lifted) : null;
        for (var j = 0; j < plain.Length; j++)
        {
            if (plain[j] == (source, column))
                return DeferredBinding.OutputColumn(j);
        }
        return null;
    }

    /// <summary>
    /// Column <paramref name="column"/> of source <paramref name="source"/> as
    /// a deferred column of this query's own row; null when the source's body
    /// computes it, or what it reads isn't projected here.
    /// </summary>
    private DeferredColumn? Lift(int source, int column, (int Source, int Column)?[] plain)
    {
        if (!this.prunable[source] || SourceTemplates(this.sources[source]) is not { } offered || column >= offered.Length || offered[column] is not { } template)
            return null;
        var lateral = this.lateral.Length > source && this.lateral[source];
        return template.Rebind((binding, name) => binding.Kind switch
        {
            DeferredBindingKind.Output => this.BindSourceColumn(source, binding.Column, plain),
            DeferredBindingKind.Free when lateral => Selection.TryBindSourceColumn(this.sources[..source], name) switch
            {
                null => DeferredBinding.FreeName,
                (-1, _) => null,
                var (left, leftColumn) => this.BindSourceColumn(left, leftColumn, plain),
            },
            DeferredBindingKind.Free => DeferredBinding.FreeName,
            _ => null,
        });
    }

    private (List<WindowExpression>, SqlType[], SqlType[]) WindowsLiveIn(bool[] slotLive, int windowBase)
    {
        var live = new List<WindowExpression>();
        var operands = new List<SqlType>();
        var results = new List<SqlType>();
        for (var w = 0; w < this.windows.Count; w++)
        {
            if (!slotLive[windowBase + w])
                continue;
            live.Add(this.windows[w]);
            operands.Add(this.windowOperandTypes[w]);
            results.Add(this.windowResultTypes[w]);
        }
        return (live, [.. operands], [.. results]);
    }

    private static bool AnyLive(int[] owners, bool[] slotLive)
    {
        foreach (var owner in owners)
        {
            if (slotLive[owner])
                return true;
        }
        return false;
    }

    /// <summary>
    /// Records on <paramref name="source"/> the columns its body computes for
    /// its reader and the ones the reader evaluates itself
    /// (<paramref name="deferred"/>), and hands its body the demand; a CTE's
    /// body is handed what every reference to it reads together (see
    /// <see cref="HandToCte"/>).
    /// </summary>
    private static void Hand(FromSource source, bool[] mask, DeferredColumn?[]? deferred)
    {
        source.ReadColumns = mask;
        source.DeferredColumns = deferred;
        var narrowed = Array.IndexOf(mask, false) >= 0;
        var takes = source.Cte is { } binding
            ? HandToCte(binding)
            : source.LateralPlan?.AcceptDemand(narrowed ? mask : null) == true;
        if (!(takes && narrowed))
        {
            source.UncomputedColumns = null;
            return;
        }
        // A deferred column is read, by its reader rather than the body.
        var uncomputed = new bool[mask.Length];
        for (var c = 0; c < mask.Length; c++)
            uncomputed[c] = !mask[c] && deferred?[c] is null;
        source.UncomputedColumns = uncomputed;
    }

    /// <summary>
    /// Hands a CTE's body the columns its references read together, once every
    /// one of them has said and only when nothing but those references reads
    /// it; answers whether the body took a narrowed demand.
    /// </summary>
    public static bool HandToCte(CteBinding binding)
    {
        if (!binding.TakesReaderDemand || binding.Plan is not { } plan)
            return false;
        bool[]? union = null;
        foreach (var reader in binding.Readers)
        {
            if (reader.ReadColumns is not { } readerColumns)
            {
                union = null;
                break;
            }
            union ??= new bool[readerColumns.Length];
            for (var c = 0; c < union.Length && c < readerColumns.Length; c++)
                union[c] |= readerColumns[c];
        }
        return plan.AcceptDemand(union) && union is not null;
    }

    /// <summary>The reads one query's clauses make, gathered as it finishes binding.</summary>
    internal sealed class Collector
    {
        public readonly FromSource[] Sources;

        public readonly bool[] Prunable;

        public readonly List<(int Slot, int Source, int Column)> Reads = [];

        public readonly List<MultiPartName> FreeNames = [];

        public readonly List<AggregateExpression> Aggregates;

        public readonly List<int>?[] AggregateOwners;

        public readonly List<WindowExpression> Windows;

        public readonly List<int>?[] WindowOwners;

        /// <summary>Whether the query reaches something whose reads can't be seen.</summary>
        public bool Unknown;

        private readonly int projectionCount;

        public Collector(FromSource[] sources, int projectionCount, List<AggregateExpression> aggregates, List<WindowExpression> windows)
        {
            this.Sources = sources;
            this.projectionCount = projectionCount;
            this.Aggregates = aggregates;
            this.AggregateOwners = new List<int>?[aggregates.Count];
            this.Windows = windows;
            this.WindowOwners = new List<int>?[windows.Count];
            this.Prunable = new bool[sources.Length];
            for (var s = 0; s < sources.Length; s++)
                this.Prunable[s] = sources[s] is { LateralPlan: not null, IsPlaceholder: false, ForPath: false, BrowseBody: null, MaterializeOnce: false };
        }

        public static int ProjectionSlot(int index) => 1 + index;

        private int WindowSlot(int index) => 1 + this.projectionCount + index;

        private int AggregateSlot(int index) => 1 + this.projectionCount + this.Windows.Count + index;

        public ProjectionDemand Finish(SqlType[] schema, string[] columnNames, List<Expression> projection, bool[] selfRead, bool readsWholeRow, SqlType[] windowOperandTypes, SqlType[] windowResultTypes, bool[]? ownsStartupConstant, bool[] lateral) =>
            new(this, schema, columnNames, projection, selfRead, readsWholeRow, windowOperandTypes, windowResultTypes, ownsStartupConstant, lateral);

        /// <summary>Counts every column of every source read by <paramref name="slot"/>.</summary>
        public void ReadEverything(int slot)
        {
            for (var s = 0; s < this.Sources.Length; s++)
            {
                if (this.Prunable[s])
                    this.Reads.Add((slot, s, -1));
            }
        }

        /// <summary>
        /// Counts <paramref name="name"/> as read by <paramref name="slot"/>:
        /// a column of one of the sources, or a name reaching past them.
        /// </summary>
        private void ReadName(MultiPartName name, int slot)
        {
            if (Selection.TryResolveSourceColumn(this.Sources, name) is not { } id)
            {
                this.FreeNames.Add(name);
                return;
            }
            if (this.Prunable[id.Source])
                this.Reads.Add((slot, id.Source, id.Column < this.Sources[id.Source].Columns.Length ? id.Column : -1));
        }

        /// <summary>
        /// Counts what a subquery, or a source's body, reads from outside
        /// itself as read by <paramref name="slot"/>; one whose reach isn't
        /// known reads everything.
        /// </summary>
        private void ReadReach(MultiPartName[]? names, int slot)
        {
            if (names is null)
            {
                this.Unknown = true;
                this.ReadEverything(slot);
                return;
            }
            foreach (var name in names)
                this.ReadName(name, slot);
        }

        /// <summary>
        /// Counts what <paramref name="source"/> reads from outside itself — an
        /// <c>APPLY</c> body's correlation, a function's arguments, a derived
        /// table's reach into an enclosing query — as read whatever the select
        /// list holds.
        /// </summary>
        public void ReadReachOf(FromSource source)
        {
            if (source.LateralPlan is not { } plan || source.IsPlaceholder
                || source.BackingView is not null || source.BackingCatalogView is not null)
            {
                return;
            }
            if (source.InlineCall is var (_, arguments, _))
            {
                foreach (var argument in arguments)
                {
                    if (argument is not null)
                        this.Walk(argument, Always);
                }
                return;
            }
            this.ReadReach(Selection.ReachOf(plan), Always);
        }

        /// <summary>
        /// Counts as read whatever a window or aggregate no clause's walk
        /// reached reads — an aggregate a <c>PIVOT</c> builds for its spreading
        /// columns, or one written in a subquery that belongs to this query.
        /// </summary>
        public void WalkUnreached()
        {
            for (var w = 0; w < this.Windows.Count; w++)
            {
                if (this.WindowOwners[w] is null)
                    this.Walk(this.Windows[w], Always);
            }
            for (var a = 0; a < this.Aggregates.Count; a++)
            {
                if (this.AggregateOwners[a] is null)
                    this.Walk(this.Aggregates[a], Always);
            }
        }

        /// <summary>Counts every read in <paramref name="root"/> as <paramref name="slot"/>'s.</summary>
        public void Walk(ExpressionNode root, int slot)
        {
            var state = (Collector: this, Slot: slot);
            root.Walk(ref state, static (node, shape, ref state) =>
            {
                var collector = state.Collector;
                // A window or an aggregate is computed while any slot holding
                // it is live, so what it reads is its own slot's.
                var ownSlot = -1;
                switch (node)
                {
                    case WindowExpression window when IndexOf(collector.Windows, window) is >= 0 and var index:
                        (collector.WindowOwners[index] ??= []).Add(state.Slot);
                        ownSlot = collector.WindowSlot(index);
                        break;
                    case AggregateExpression aggregate when IndexOf(collector.Aggregates, aggregate) is >= 0 and var index:
                        (collector.AggregateOwners[index] ??= []).Add(state.Slot);
                        ownSlot = collector.AggregateSlot(index);
                        break;
                }
                if (ownSlot >= 0 && ownSlot != state.Slot)
                {
                    collector.WalkChildren(shape, ownSlot);
                    return false;
                }
                if (shape.Column is { } name)
                    collector.ReadName(name, state.Slot);
                foreach (var local in shape.Locals)
                {
                    if (local is Selection subquery)
                        collector.ReadReach(Selection.ReachOf(subquery), state.Slot);
                }
                return true;
            });
        }

        private static int IndexOf<T>(List<T> nodes, T node) where T : class
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (ReferenceEquals(nodes[i], node))
                    return i;
            }
            return -1;
        }

        private void WalkChildren(NodeShape shape, int slot)
        {
            if (shape.Column is { } name)
                this.ReadName(name, slot);
            foreach (var local in shape.Locals)
            {
                if (local is Selection subquery)
                    this.ReadReach(Selection.ReachOf(subquery), slot);
            }
            foreach (var child in shape.ChildNodes.ToArray())
            {
                if (child is not null)
                    this.Walk(child, slot);
            }
        }
    }
}
