using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// A SELECT's <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> row limit, held
/// unresolved so the counts re-evaluate against the batch that OPENs the
/// cursor (the operands may be variables or parameters). Real SQL Server
/// converts a row-limited cursor to KEYSET rather than refusing to navigate
/// it: the limit picks the membership at OPEN, and the frozen key set is what
/// later FETCHes re-read (probe-confirmed via
/// <c>sys.dm_exec_cursors</c>, for <c>TOP n</c>, <c>TOP n PERCENT</c>,
/// <c>TOP n WITH TIES</c> and <c>OFFSET … FETCH</c> alike).
/// </summary>
internal sealed class CursorRowLimit(
    Expression? top,
    bool percent,
    bool withTies,
    Expression? offset,
    Expression? fetch)
{
    public readonly Expression? Top = top;
    public readonly bool Percent = percent;
    public readonly bool WithTies = withTies;
    public readonly Expression? Offset = offset;
    public readonly Expression? Fetch = fetch;
}

/// <summary>
/// A SELECT's parse-time cursor-navigability capture: the FROM sources and
/// their joins, the projection expressions, the WHERE excluders, and any
/// <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> row limit. Held on
/// <see cref="Selection.CursorShape"/> and resolved into a
/// <see cref="CursorSourcePlan"/> by <c>Selection.TryBuildCursorPlan</c> at
/// DECLARE CURSOR time — the point at which a view body may be parsed, which
/// is too expensive to do for every SELECT.
/// </summary>
internal sealed class CursorShape(
    FromSource[] sources,
    JoinSpec[] joins,
    Expression[] projections,
    BooleanExpression[] excluders,
    CursorRowLimit? rowLimit)
{
    public readonly FromSource[] Sources = sources;
    public readonly JoinSpec[] Joins = joins;
    public readonly Expression[] Projections = projections;
    public readonly BooleanExpression[] Excluders = excluders;
    public readonly CursorRowLimit? RowLimit = rowLimit;
}

/// <summary>
/// One FROM slot of a cursor-navigable SELECT: either a direct base-table
/// scan or a deferred body — a derived table, a CTE reference, an APPLY
/// right side, or a view — whose own nested <see cref="CursorSourcePlan"/>
/// carries the base tables underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ThroughView"/> records the name a positioned <c>WHERE CURRENT
/// OF</c> must write to reach this slot. Real SQL Server resolves positioned
/// DML against the reference as written: a cursor reading a view is mutated
/// by naming the <em>view</em> (Msg 16933 for the base table under it), while
/// a derived table or CTE is transparent and the mutation names the base
/// table. A view therefore stamps its whole subtree — including a view it
/// reads in turn — with itself.
/// </para>
/// </remarks>
internal sealed class CursorSlot
{
    /// <summary>The base table this slot scans directly, or null when the
    /// slot is a deferred body (<see cref="Nested"/> is then set).</summary>
    public readonly HeapTable? Table;

    /// <summary>The deferred body's own plan, which drives this slot's rows;
    /// null for a direct base-table scan.</summary>
    public readonly CursorSourcePlan? Nested;

    /// <summary>The view this slot was written as, when the FROM reached it
    /// through one; null for a base table, derived table or CTE.</summary>
    public readonly View? ThroughView;

    private CursorSlot(HeapTable? table, CursorSourcePlan? nested, View? throughView)
    {
        this.Table = table;
        this.Nested = nested;
        this.ThroughView = throughView;
    }

    public static CursorSlot ForTable(HeapTable table) => new(table, null, null);

    public static CursorSlot ForNested(CursorSourcePlan nested, View? throughView) => new(null, nested, throughView);
}

/// <summary>
/// The FROM-clause shape an updatable cursor navigates: the participating
/// sources and their joins, the projection expressions, and the WHERE
/// excluders. Resolved from a <see cref="CursorShape"/> by
/// <c>Selection.TryBuildCursorPlan</c> at DECLARE CURSOR time, and built only
/// when every source bottoms out in base-table scans — the only shape that
/// carries the stable <c>(page, slot)</c> addresses KEYSET membership, DYNAMIC
/// navigation and positioned <c>WHERE CURRENT OF</c> DML ride on. A single
/// source is just the one-slot case; a JOIN adds slots, and a deferred slot
/// (derived table, CTE, APPLY right side, view) contributes the addresses of
/// every base table its body reads.
/// </summary>
/// <remarks>
/// Cursor identity is the <em>flattened</em> tuple of those addresses:
/// <see cref="IdentityTables"/> lists one entry per base-table scan anywhere
/// in the tree, depth-first in slot order, and <see cref="SlotIdentityOffset"/>
/// / <see cref="SlotIdentityWidth"/> locate each slot's contiguous span.
/// <c>Cursor.CurrentRids</c> and <c>CursorRow.Rids</c> are arrays of that
/// width, so positioned DML reaches a base table nested arbitrarily deep
/// without the cursor knowing how it got there.
/// </remarks>
internal sealed class CursorSourcePlan
{
    public readonly FromSource[] Sources;

    /// <summary>The join between slot <c>i</c> and slot <c>i + 1</c>, so
    /// <c>Joins.Length == Sources.Length - 1</c>.</summary>
    public readonly JoinSpec[] Joins;

    /// <summary>What backs each slot of <see cref="Sources"/>.</summary>
    public readonly CursorSlot[] Slots;

    public readonly Expression[] Projections;
    public readonly BooleanExpression[] Excluders;

    /// <summary>The SELECT's ORDER BY items, so KEYSET / DYNAMIC navigation
    /// orders rows the same way a read would. A nested plan carries one only
    /// alongside a <see cref="RowLimit"/> — a deferred body's ORDER BY is Msg
    /// 1033 without the TOP / OFFSET it picks rows for — and it then decides
    /// which rows that limit admits.</summary>
    public readonly List<OrderBySpec> OrderBy;

    /// <summary>The SELECT's output column names, which ORDER BY items resolve
    /// against.</summary>
    public readonly string[] ColumnNames;

    /// <summary>Every base table the plan reads, flattened depth-first in slot
    /// order — one entry per address the cursor's identity tuple carries.</summary>
    public readonly HeapTable[] IdentityTables;

    /// <summary>The view a positioned <c>WHERE CURRENT OF</c> must name to
    /// reach the matching <see cref="IdentityTables"/> entry, or null when the
    /// statement must name the base table itself.</summary>
    public readonly View?[] IdentityViews;

    /// <summary>The surface columns of each identity entry — the view's output
    /// columns when one stamps it, else the base table's own. A
    /// <c>FOR UPDATE OF</c> list narrows the updatable entries to those owning
    /// a listed column.</summary>
    public readonly HeapColumn[][] IdentityColumns;

    /// <summary>Index into <see cref="IdentityTables"/> where slot <c>i</c>'s
    /// span begins.</summary>
    public readonly int[] SlotIdentityOffset;

    /// <summary>How many identity entries slot <c>i</c> contributes.</summary>
    public readonly int[] SlotIdentityWidth;

    /// <summary>This plan's own <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> row
    /// limit, or null when it carries none.</summary>
    public readonly CursorRowLimit? RowLimit;

    /// <summary>True when this plan or anything below it limits rows, which
    /// caps the cursor's sensitivity at KEYSET — the limit chooses membership
    /// at OPEN, so there is no live set for DYNAMIC to walk.</summary>
    public readonly bool HasRowLimit;

    public CursorSourcePlan(
        FromSource[] sources,
        JoinSpec[] joins,
        CursorSlot[] slots,
        Expression[] projections,
        BooleanExpression[] excluders,
        List<OrderBySpec> orderBy,
        string[] columnNames,
        CursorRowLimit? rowLimit)
    {
        this.RowLimit = rowLimit;
        this.HasRowLimit = rowLimit is not null;
        this.Sources = sources;
        this.Joins = joins;
        this.Slots = slots;
        this.Projections = projections;
        this.Excluders = excluders;
        this.OrderBy = orderBy;
        this.ColumnNames = columnNames;

        this.SlotIdentityOffset = new int[slots.Length];
        this.SlotIdentityWidth = new int[slots.Length];
        var tables = new List<HeapTable>();
        var views = new List<View?>();
        var columns = new List<HeapColumn[]>();
        for (var i = 0; i < slots.Length; i++)
        {
            this.SlotIdentityOffset[i] = tables.Count;
            var slot = slots[i];
            if (slot.Table is { } table)
            {
                tables.Add(table);
                views.Add(null);
                columns.Add(table.Columns);
            }
            else
            {
                var nested = slot.Nested!;
                this.HasRowLimit |= nested.HasRowLimit;
                for (var k = 0; k < nested.IdentityTables.Length; k++)
                {
                    tables.Add(nested.IdentityTables[k]);
                    // A view is opaque: everything under it is addressed by the
                    // view's own name, overriding whatever the body wrote.
                    views.Add(slot.ThroughView ?? nested.IdentityViews[k]);
                    columns.Add(slot.ThroughView is { } outer ? outer.OutputColumns : nested.IdentityColumns[k]);
                }
            }
            this.SlotIdentityWidth[i] = tables.Count - this.SlotIdentityOffset[i];
        }
        this.IdentityTables = [.. tables];
        this.IdentityViews = [.. views];
        this.IdentityColumns = [.. columns];
    }
}

/// <summary>
/// Cursor-side enumeration for updatable cursors (KEYSET / DYNAMIC and
/// positioned <c>WHERE CURRENT OF</c> DML). A cursor whose SELECT bottoms out
/// in base tables re-reads live rows here instead of snapshotting bytes
/// through <see cref="Execute"/>, so column changes (and, for DYNAMIC,
/// membership changes) made between <c>FETCH</c>es are visible — matching SQL
/// Server's sensitivity model. Each row carries its projected output values,
/// its ORDER BY key, the chosen unique-key tuple per base table (when that
/// table has a PK or UNIQUE constraint — matches SQL Server's KEYSET
/// identity), and each base row's stable <c>(page, slot)</c> address (always —
/// used as the cursor identity when no unique key exists and as the
/// deterministic tiebreak for the ORDER BY total order).
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// One row produced by <see cref="EnumerateForCursor"/>: the projected
    /// output values, the ORDER BY key, and — one slot per base table the plan
    /// reads, flattened through any deferred bodies — the optional unique-key
    /// tuple (null when that table has no PK/UNIQUE, falling back to
    /// <see cref="Rids"/> for cursor identity) and the base row's stable
    /// address (null on a NULL-extended outer-join side).
    /// </summary>
    internal sealed class CursorRow(SqlValue[] values, SqlValue[] orderKey, SqlValue[]?[] uniqueKeys, (int Page, int Slot)?[] rids)
    {
        public readonly SqlValue[] Values = values;
        public readonly SqlValue[] OrderKey = orderKey;
        public readonly SqlValue[]?[] UniqueKeys = uniqueKeys;
        public readonly (int Page, int Slot)?[] Rids = rids;
    }

    /// <summary>
    /// The candidate rows of one FROM slot for a single enumeration: the bytes
    /// column resolution reads plus, per row, the slot's contiguous span of
    /// identity addresses and unique keys. The identity lists are flat — row
    /// <c>r</c> occupies <c>[r * IdentityWidth, (r + 1) * IdentityWidth)</c> —
    /// so a base-table slot (width 1) costs no per-row array, matching the
    /// pre-nesting layout on the path every simple cursor takes.
    /// </summary>
    private sealed class CursorSlotScan(int identityWidth)
    {
        public readonly int IdentityWidth = identityWidth;
        public readonly List<byte[]> Bytes = [];
        public readonly List<(int Page, int Slot)?> Rids = [];
        public readonly List<SqlValue[]?> UniqueKeys = [];
    }

    /// <summary>
    /// Storage ordinals of a base table's chosen unique key (PRIMARY KEY
    /// preferred, else the first UNIQUE constraint), or null when the table
    /// has neither. KEYSET cursors track by these columns when present —
    /// matching SQL Server's "keyset is identified by the unique index"
    /// behavior (probe-confirmed: an UPDATE to a unique-key column makes the
    /// next fetch return <c>@@FETCH_STATUS = -2</c>). When null, the cursor
    /// falls back to the row's stable <c>(page, slot)</c> address — a
    /// simulator extension over real SQL Server's no-unique-key heap
    /// behavior, which is documented as undefined.
    /// </summary>
    internal static int[]? CursorUniqueKeyOrdinals(HeapTable table)
    {
        KeyConstraint? chosen = null;
        foreach (var key in table.KeyConstraints)
        {
            if (key.Kind == KeyConstraintKind.PrimaryKey)
                return key.StorageOrdinals;
            chosen ??= key;
        }
        return chosen?.StorageOrdinals;
    }

    /// <summary>
    /// Resolves a SELECT's parse-time <see cref="CursorShape"/> into the plan a
    /// KEYSET / DYNAMIC cursor navigates, or null when some source doesn't
    /// bottom out in base tables and the cursor must fall back to a STATIC
    /// snapshot. Called at DECLARE CURSOR time because a view slot's body is
    /// parsed here (real captures the cursor's plan at DECLARE too), and
    /// recursively for every deferred slot — so a view over a view, or a
    /// derived table over a view, resolves through as many layers as it takes.
    /// </summary>
    /// <remarks>
    /// The returned plan is a fresh object per call and is never stored back on
    /// the <see cref="Selection"/>: a cached plan is shared across executions,
    /// and a view body parsed into it is not.
    /// </remarks>
    internal static CursorSourcePlan? TryBuildCursorPlan(Selection selection, BatchContext batch, int depth = 0)
    {
        // A view that reads itself (creatable through deferred name resolution)
        // would recurse forever; real reports the nesting cap instead, and the
        // cursor falling back to STATIC surfaces that at OPEN.
        if (depth >= SimulatedDbConnection.MaxNestingLevel || selection.CursorShape is not { } shape)
            return null;

        var slots = new CursorSlot[shape.Sources.Length];
        for (var i = 0; i < shape.Sources.Length; i++)
        {
            var source = shape.Sources[i];
            if (source is { BackingTable: { } table, LateralPlan: null, IsPlaceholder: false } && source.Rows is not TemporalRowSource)
            {
                slots[i] = CursorSlot.ForTable(table);
                continue;
            }
            if (TryBuildDeferredSlot(source, batch, depth) is not { } deferred)
                return null;
            slots[i] = deferred;
        }

        return new CursorSourcePlan(
            shape.Sources, shape.Joins, slots, shape.Projections, shape.Excluders,
            selection.CursorOrderBy ?? [], selection.ColumnNames, shape.RowLimit);
    }

    /// <summary>
    /// Builds the slot for a source whose rows arrive through a
    /// <see cref="FromSource.LateralPlan"/>: a view (whose stored body is
    /// parsed here), or a derived table / CTE / APPLY right side (whose body
    /// plan the parser already holds). Returns null for every other deferred
    /// source — a TVF, a catalog view, <c>VALUES</c>, <c>OPENJSON</c>, PIVOT, a
    /// linked server — matching real SQL Server, which reports those cursors as
    /// read-only snapshots.
    /// </summary>
    private static CursorSlot? TryBuildDeferredSlot(FromSource source, BatchContext batch, int depth)
    {
        if (source.IsPlaceholder || source.LateralPlan is null)
            return null;

        if (source.BackingView is { } view)
        {
            if (batch.Connection.Simulation.TryParseViewBodyPlan(batch, view) is not { } body)
                return null;
            var viewPlan = TryBuildCursorPlan(body, batch, depth + 1);
            // A body whose projection count drifted from the columns captured
            // at CREATE VIEW (a `SELECT *` over a table that gained or lost a
            // column) can't be re-encoded to the source's declared layout.
            return viewPlan is null || viewPlan.Projections.Length != source.StoredSchema.Length
                ? null
                : CursorSlot.ForNested(viewPlan, view);
        }

        if (!source.LateralIsQueryBody)
            return null;
        var bodyPlan = TryBuildCursorPlan(source.LateralPlan, batch, depth + 1);
        return bodyPlan is null ? null : CursorSlot.ForNested(bodyPlan, null);
    }

    /// <summary>
    /// Enumerates the cursor's source rows live from the base heaps, folding
    /// the JOIN chain, applying the SELECT's WHERE and projection, and
    /// ordering by its ORDER BY (with the flattened stable addresses as a
    /// final tiebreak for a total order). Re-invoked per KEYSET / DYNAMIC
    /// <c>FETCH</c> so the latest committed values and (for DYNAMIC)
    /// membership are observed.
    /// </summary>
    /// <remarks>
    /// <c>applyRowLimit</c> is true at OPEN, where a <c>TOP</c> /
    /// <c>OFFSET</c> / <c>FETCH</c> written on the cursor's own statement (or
    /// on a derived table / CTE / APPLY body under it) chooses the KEYSET's
    /// membership. It is false per FETCH: membership is frozen, so a member
    /// that has since slid out of the window is still re-read live rather than
    /// reported deleted (probe-confirmed — a keyset member pushed out of a
    /// statement-level <c>TOP 3</c> by a mid-loop insert still fetches with
    /// status 0). A limit written inside a <em>view</em> body is the exception
    /// and re-applies on every FETCH, so a member the view no longer returns
    /// fetches as <c>@@FETCH_STATUS = -2</c> — also probe-confirmed, and the
    /// reason the flag is re-raised per view slot rather than threaded
    /// straight down.
    /// </remarks>
    internal static List<CursorRow> EnumerateForCursor(CursorSourcePlan plan, BatchContext batch, bool applyRowLimit = false) =>
        EnumerateCursorRows(plan, batch, outerResolver: null, applyRowLimit);

    /// <summary>
    /// The recursive body of <see cref="EnumerateForCursor"/>. A deferred
    /// slot re-enters here on its own plan, so its rows carry the addresses of
    /// the base tables its body read; <paramref name="outerResolver"/> is the
    /// enclosing tuple's resolver, non-null when this plan is an APPLY right
    /// side (or sits under one).
    /// </summary>
    private static List<CursorRow> EnumerateCursorRows(CursorSourcePlan plan, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, bool applyRowLimit)
    {
        var sources = plan.Sources;
        var width = sources.Length;
        var identityWidth = plan.IdentityTables.Length;
        var orderBy = plan.OrderBy;

        // Hoisted per-row scaffolding: one mutable tuple, one cached
        // self-referencing resolver lambda (never a local function passed as
        // its own selfRecursive argument — that allocates a delegate per
        // resolution per row), one RuntimeContext. The join fold, the deferred
        // slots' correlation and the projection pass below share all three.
        var current = new byte[]?[width];
        var memo = new SourceColumnMemo();
        Func<MultiPartName, SqlValue> resolve = null!;
        resolve = name => ResolveAcrossTuple(sources, current, name, batch, outerResolver, resolve, memo);
        var runtime = new RuntimeContext(resolve, batch);

        // Live per-slot candidates: a base-table slot snapshots its heap, a
        // deferred slot runs its nested plan. Taken once per call so the join
        // fold can revisit the inner sides without re-walking pages, and against
        // the *enclosing* scope — only APPLY makes a right side lateral, so a
        // plain derived table naming a sibling's column reports Msg 207 here
        // exactly as it does on the read path. An APPLY right side does
        // correlate, so its scan starts empty and the fold appends per left row
        // against the tuple resolver.
        var scans = new CursorSlotScan[width];
        for (var i = 0; i < width; i++)
        {
            scans[i] = new CursorSlotScan(plan.SlotIdentityWidth[i]);
            if (!IsApplySlot(plan, i))
                AppendCursorSlotRows(scans[i], plan, i, batch, outerResolver, applyRowLimit);
        }

        var tuples = FoldCursorTuples(plan, scans, current, runtime, batch, resolve, applyRowLimit);

        var rows = new List<CursorRow>(tuples.Count);
        foreach (var tuple in tuples)
        {
            var rids = new (int Page, int Slot)?[identityWidth];
            var uniqueKeys = new SqlValue[]?[identityWidth];
            for (var i = 0; i < width; i++)
            {
                var scan = scans[i];
                var offset = plan.SlotIdentityOffset[i];
                if (tuple[i] < 0)
                {
                    current[i] = null;
                    continue; // NULL-extended: identity span stays null
                }
                current[i] = scan.Bytes[tuple[i]];
                var span = tuple[i] * scan.IdentityWidth;
                for (var k = 0; k < scan.IdentityWidth; k++)
                {
                    rids[offset + k] = scan.Rids[span + k];
                    uniqueKeys[offset + k] = scan.UniqueKeys[span + k];
                }
            }

            var keep = true;
            foreach (var excluder in plan.Excluders)
            {
                if (excluder.Run(runtime) != true)
                {
                    keep = false;
                    break;
                }
            }
            if (!keep)
                continue;

            var values = new SqlValue[plan.Projections.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = plan.Projections[i].Run(runtime);

            var orderKey = orderBy.Count == 0
                ? []
                : ComputeOrderKeys(orderBy, values, plan.ColumnNames, projectionSources: null, distinct: false, batch, resolve);

            rows.Add(new CursorRow(values, orderKey, uniqueKeys, rids));
        }

        rows.Sort((a, b) => CompareCursorRows(plan, a, b));
        return applyRowLimit && plan.RowLimit is { } limit ? ApplyCursorRowLimit(plan, rows, limit, batch) : rows;
    }

    /// <summary>
    /// Trims an ordered cursor row list to the plan's <c>TOP</c> /
    /// <c>OFFSET</c> / <c>FETCH</c> window, resolving the counts against the
    /// executing batch and honoring <c>PERCENT</c> / <c>WITH TIES</c> through
    /// the same <see cref="ComputeTopCap{T}"/> the read path uses — so the rows
    /// a KEYSET cursor admits at OPEN are exactly the rows the equivalent
    /// SELECT would return.
    /// </summary>
    private static List<CursorRow> ApplyCursorRowLimit(CursorSourcePlan plan, List<CursorRow> rows, CursorRowLimit limit, BatchContext batch)
    {
        var top = limit.Top is null
            ? default
            : limit.Percent
                ? new TopSpec(null, ResolveTopPercentValue(limit.Top, batch), limit.WithTies)
                : new TopSpec(ResolveRowCountLimit(limit.Top, RowLimitKind.Top, batch), null, limit.WithTies);
        var offset = ResolveRowCountLimit(limit.Offset, RowLimitKind.Offset, batch) ?? 0;
        var cap = ComputeTopCap(rows, row => row.OrderKey, plan.OrderBy, top, ResolveRowCountLimit(limit.Fetch, RowLimitKind.Fetch, batch));

        var start = Math.Min(offset, rows.Count);
        var length = rows.Count - start;
        if (cap is { } limited)
            length = Math.Min(length, Math.Max(limited, 0));
        return rows.GetRange(start, length);
    }

    /// <summary>True when slot <paramref name="index"/> is the right side of a
    /// <c>CROSS</c> / <c>OUTER APPLY</c>, whose rows depend on the left tuple
    /// and so can't be scanned once up front.</summary>
    private static bool IsApplySlot(CursorSourcePlan plan, int index) =>
        index > 0 && plan.Joins[index - 1].Kind is JoinKind.CrossApply or JoinKind.OuterApply;

    /// <summary>
    /// Appends slot <paramref name="index"/>'s current candidate rows to
    /// <paramref name="scan"/>. A base-table slot walks its heap, decoding the
    /// chosen unique key per row; a deferred slot re-enters
    /// <see cref="EnumerateCursorRows"/> on its body plan and re-encodes the
    /// body's projected values into the bytes the outer plan's column
    /// resolution decodes, carrying the body's own addresses through unchanged.
    /// A <em>view</em> slot re-raises <paramref name="applyRowLimit"/> for its
    /// subtree: real re-evaluates a view's own row limit on every FETCH, while
    /// a derived table's or CTE's limit only picks the KEYSET membership at
    /// OPEN (both probe-confirmed).
    /// </summary>
    private static void AppendCursorSlotRows(
        CursorSlotScan scan,
        CursorSourcePlan plan,
        int index,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        bool applyRowLimit)
    {
        if (plan.Slots[index].Table is { } table)
        {
            var ordinals = CursorUniqueKeyOrdinals(table);
            var storedColumns = table.StoredColumns;
            foreach (var (page, slot, bytes) in table.Heap.EnumerateRowsWithAddress())
            {
                scan.Bytes.Add(bytes);
                scan.Rids.Add((page, slot));
                if (ordinals is not { } keyOrdinals)
                {
                    scan.UniqueKeys.Add(null);
                    continue;
                }
                var key = new SqlValue[keyOrdinals.Length];
                for (var k = 0; k < keyOrdinals.Length; k++)
                    key[k] = RowDecoder.DecodeColumn(storedColumns, bytes, keyOrdinals[k], table.Heap);
                scan.UniqueKeys.Add(key);
            }
            return;
        }

        // The outer plan resolves this slot's columns by decoding bytes laid
        // out per the source's declared schema, so the body's projected values
        // are re-encoded to it — the same schema the deferred source's row
        // stream carries on the ordinary read path.
        var deferred = plan.Slots[index];
        var storedSchema = plan.Sources[index].StoredSchema;
        foreach (var row in EnumerateCursorRows(deferred.Nested!, batch, outerResolver, applyRowLimit || deferred.ThroughView is not null))
        {
            var values = row.Values;
            for (var c = 0; c < values.Length; c++)
            {
                if (values[c].Type != storedSchema[c].Type)
                    values[c] = values[c].CoerceTo(storedSchema[c].Type);
            }
            scan.Bytes.Add(RowEncoder.EncodeRow(storedSchema, values));
            scan.Rids.AddRange(row.Rids);
            scan.UniqueKeys.AddRange(row.UniqueKeys);
        }
    }

    /// <summary>
    /// Folds the cursor's JOIN chain into one row-index tuple per joined row:
    /// slot <c>i</c> holds the index into <c>scans[i]</c>, or <c>-1</c> for a
    /// NULL-extended outer-join side. A left-deep nested loop — the equi-join
    /// hash / seek strategies of the read path don't apply here because every
    /// intermediate row must keep its per-source addresses, and the cursor
    /// re-folds on every FETCH anyway.
    /// </summary>
    /// <remarks>
    /// The ON predicate evaluates through the shared <paramref name="current"/>
    /// tuple, whose slots past the level being joined are cleared so a
    /// forward reference reads as NULL rather than a stale left-sibling row.
    /// RIGHT / FULL track a matched bitmap across the whole left iteration and
    /// emit the unmatched right rows afterwards with every prior slot
    /// NULL-filled, matching <c>EnumerateJoinedRows</c>'s semantics. An APPLY
    /// right side has no precomputed scan: its rows are appended to the shared
    /// slot scan per left tuple (so the indices stay stable) and consumed
    /// immediately, with <c>OUTER APPLY</c> null-filling an empty result.
    /// </remarks>
    private static List<int[]> FoldCursorTuples(
        CursorSourcePlan plan,
        CursorSlotScan[] scans,
        byte[]?[] current,
        RuntimeContext runtime,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolve,
        bool applyRowLimit)
    {
        var width = scans.Length;
        var accumulated = new List<int[]>(scans[0].Bytes.Count);
        for (var r = 0; r < scans[0].Bytes.Count; r++)
        {
            var seed = new int[width];
            Array.Fill(seed, -1);
            seed[0] = r;
            accumulated.Add(seed);
        }

        for (var level = 1; level < width; level++)
        {
            var join = plan.Joins[level - 1];
            var right = scans[level];
            var lateral = IsApplySlot(plan, level);
            // RIGHT / FULL need the unmatched-right tail, so track which right
            // rows a left row paired with across the whole left iteration.
            var keepUnmatchedRight = join.Kind is JoinKind.Right or JoinKind.Full;
            var matched = new bool[lateral ? 0 : right.Bytes.Count];
            var next = new List<int[]>();
            foreach (var left in accumulated)
            {
                for (var i = 0; i < width; i++)
                    current[i] = i < level && left[i] >= 0 ? scans[i].Bytes[left[i]] : null;

                var from = 0;
                var to = right.Bytes.Count;
                if (lateral)
                {
                    // The right side correlates with the left tuple `current`
                    // holds at this point; its rows append to the shared scan so
                    // the tuple indices below stay valid for the projection pass.
                    from = to;
                    AppendCursorSlotRows(right, plan, level, batch, resolve, applyRowLimit);
                    to = right.Bytes.Count;
                }

                var any = false;
                for (var r = from; r < to; r++)
                {
                    current[level] = right.Bytes[r];
                    if (join.OnPredicate is { } on && on.Run(runtime) != true)
                        continue;
                    var row = (int[])left.Clone();
                    row[level] = r;
                    next.Add(row);
                    any = true;
                    if (!lateral)
                        matched[r] = true;
                }

                if (!any && join.Kind is JoinKind.Left or JoinKind.Full or JoinKind.OuterApply)
                    next.Add((int[])left.Clone());
            }

            for (var r = 0; keepUnmatchedRight && r < matched.Length; r++)
            {
                if (matched[r])
                    continue;
                var row = new int[width];
                Array.Fill(row, -1);
                row[level] = r;
                next.Add(row);
            }

            accumulated = next;
        }

        return accumulated;
    }

    /// <summary>
    /// Total-order comparison between two cursor rows: ORDER BY key first (per
    /// the SELECT's ASC/DESC flags), then the flattened stable addresses
    /// ascending as a deterministic tiebreak (addresses are unique within a
    /// heap, so the tuple of them is unique across the join). Drives both the
    /// stable sort in <see cref="EnumerateForCursor"/> and DYNAMIC next/prior
    /// navigation.
    /// </summary>
    internal static int CompareCursorRows(CursorSourcePlan plan, CursorRow a, CursorRow b)
    {
        var orderBy = plan.OrderBy;
        var c = orderBy.Count == 0 ? 0 : CompareOrderKeys(a.OrderKey, b.OrderKey, orderBy);
        for (var i = 0; c == 0 && i < a.Rids.Length; i++)
            c = CompareRids(a.Rids[i], b.Rids[i]);
        return c;
    }

    /// <summary>Ascending compare of two stable addresses, a missing one
    /// (NULL-extended outer-join side) sorting first.</summary>
    private static int CompareRids((int Page, int Slot)? a, (int Page, int Slot)? b)
    {
        if (a is not { } left)
            return b is null ? 0 : -1;
        if (b is not { } right)
            return 1;
        var c = left.Page.CompareTo(right.Page);
        return c != 0 ? c : left.Slot.CompareTo(right.Slot);
    }

    /// <summary>
    /// True when <paramref name="row"/> is the same joined row a KEYSET
    /// member snapshotted at OPEN: per base table, the unique-key tuple when
    /// that table has one (so an UPDATE to those columns unmakes the match, as
    /// on real SQL Server), else the stable address.
    /// </summary>
    internal static bool CursorIdentityMatches(CursorRow row, SqlValue[]?[] keys, (int Page, int Slot)?[] rids)
    {
        for (var i = 0; i < rids.Length; i++)
        {
            var same = keys[i] is { } key
                ? row.UniqueKeys[i] is { } live && CompareKeyTuples(live, key) == 0
                : Nullable.Equals(row.Rids[i], rids[i]);
            if (!same)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Ascending lexicographic compare of two key tuples (NULL smallest,
    /// cross-type promoted). Used by the keyset's identity-match step when
    /// the base table has a unique key.
    /// </summary>
    internal static int CompareKeyTuples(SqlValue[] a, SqlValue[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var lk = a[i];
            var rk = b[i];
            int c;
            if (lk.IsNull && rk.IsNull)
            {
                c = 0;
            }
            else if (lk.IsNull)
            {
                c = -1;
            }
            else if (rk.IsNull)
            {
                c = 1;
            }
            else if (lk.Type == rk.Type)
            {
                c = lk.CompareTo(rk);
            }
            else
            {
                var common = SqlType.Promote(lk.Type, rk.Type);
                c = lk.CoerceTo(common).CompareTo(rk.CoerceTo(common));
            }
            if (c != 0)
                return c;
        }
        return 0;
    }
}
