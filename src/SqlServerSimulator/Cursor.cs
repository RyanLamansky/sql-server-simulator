using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Effective sensitivity of an open cursor (resolved at DECLARE from the
/// requested keywords plus whether the SELECT is updatable — a non-updatable
/// query is forced to <see cref="Static"/>, matching SQL Server).
/// </summary>
internal enum CursorSensitivity
{
    /// <summary>Snapshot of projected rows taken at OPEN; immune to later
    /// changes; read-only. Covers STATIC / INSENSITIVE / FAST_FORWARD and any
    /// non-updatable query.</summary>
    Static,

    /// <summary>Membership (the set of unique keys) frozen at OPEN; each FETCH
    /// re-reads the live row's column values; a member deleted out from under
    /// the cursor yields <c>@@FETCH_STATUS = -2</c>.</summary>
    Keyset,

    /// <summary>Fully live: membership and values both reflect committed
    /// changes between FETCHes; <c>@@CURSOR_ROWS = -1</c>.</summary>
    Dynamic,
}

/// <summary>A FETCH direction. Only <see cref="Next"/> is legal on a
/// forward-only cursor; the rest require a scrollable cursor.</summary>
internal enum FetchDirection { Next, Prior, First, Last, Absolute, Relative }

/// <summary>
/// The concurrency-control model of an updatable cursor (the
/// <c>READ_ONLY</c> / <c>SCROLL_LOCKS</c> / <c>OPTIMISTIC</c> keyword family).
/// A read-only cursor carries <see cref="Cursor.ReadOnly"/> instead.
/// </summary>
internal enum CursorConcurrency
{
    /// <summary>Default optimistic-without-detection: positioned DML just
    /// re-locates the row and rewrites it (the pre-existing behavior).</summary>
    Default,

    /// <summary><c>SCROLL_LOCKS</c>: a U lock is held on the currently-fetched
    /// row (cursor-scoped — released when the cursor scrolls off the row,
    /// closes, or deallocates), and positioned DML upgrades it to X.</summary>
    ScrollLocks,

    /// <summary><c>OPTIMISTIC</c>: no lock is held; positioned DML re-reads the
    /// row and raises the optimistic-conflict chain (Msg 16947 / 16934) when it
    /// was modified out-of-band since the fetch.</summary>
    Optimistic,
}

/// <summary>
/// A session-scoped T-SQL cursor (declared with <c>DECLARE … CURSOR FOR
/// &lt;select&gt;</c>). Lives in <see cref="SimulatedDbConnection.Cursors"/>.
/// Position is tracked by the tuple of source rows' stable <c>(page, slot)</c>
/// addresses — one per FROM slot, so a JOIN cursor reaches every participating
/// row — which <see cref="Heap.UpdateAt"/> preserves through value updates by
/// rewriting in place (or installing a forwarding pointer). KEYSET membership
/// tracking and positioned <c>WHERE CURRENT OF</c> DML therefore work without
/// requiring any base table to have a unique key.
/// </summary>
internal sealed class Cursor(
    string name,
    Selection selection,
    CursorSensitivity sensitivity,
    bool scrollable,
    bool readOnly,
    HeapTable[] baseTables,
    CursorConcurrency concurrency = CursorConcurrency.Default,
    List<string>? forUpdateColumns = null)
{
    public readonly string Name = name;
    public readonly Selection Selection = selection;
    public readonly CursorSensitivity Sensitivity = sensitivity;
    public readonly bool Scrollable = scrollable;
    public readonly bool ReadOnly = readOnly;

    /// <summary>The base table behind each FROM slot, in source order —
    /// non-empty for KEYSET / DYNAMIC / positioned DML, empty for a STATIC
    /// (non-updatable) cursor. A self-join repeats a table; positioned DML
    /// binds to its first occurrence, as real SQL Server does.</summary>
    public readonly HeapTable[] BaseTables = baseTables;

    /// <summary>The concurrency model — <see cref="CursorConcurrency.ScrollLocks"/>
    /// holds a cursor-scoped U lock on the fetched row, <see cref="CursorConcurrency.Optimistic"/>
    /// detects out-of-band modification at positioned DML time.</summary>
    public readonly CursorConcurrency Concurrency = concurrency;

    /// <summary>The <c>FOR UPDATE OF (col, …)</c> column list (surface names),
    /// or null when the cursor was declared <c>FOR UPDATE</c> without an OF list
    /// (every column updatable) or without a FOR UPDATE clause. A positioned
    /// UPDATE of a column absent from a non-null list raises Msg 16932.</summary>
    public readonly List<string>? ForUpdateColumns = forUpdateColumns;

    /// <summary>
    /// Reference count of cursor variables (<c>DECLARE @c CURSOR</c>) pointing
    /// at this object. A named cursor sits at 0; each <c>SET @c = …</c> binding
    /// increments, each <c>DEALLOCATE @c</c> decrements, and the object is torn
    /// down only when the count returns to 0 with no name. Matches SQL Server's
    /// refcounted cursor-variable model (probe-confirmed).
    /// </summary>
    public int VariableRefCount;

    /// <summary>True for an unnamed cursor that exists only through cursor
    /// variables (created by <c>SET @c = CURSOR FOR …</c>); such a cursor is
    /// destroyed when its last variable reference is deallocated.</summary>
    public bool IsUnnamed;

    public bool IsOpen;

    /// <summary>
    /// OPTIMISTIC snapshot of the currently-fetched row's full stored bytes —
    /// one slot per FROM source, captured at each FETCH. Positioned DML
    /// compares the live bytes at <see cref="CurrentRids"/> against this; any
    /// difference (a value change, a rowversion bump, or the row's
    /// disappearance) is an optimistic conflict. A full-row byte compare
    /// subsumes both real detection bases — rowversion column when present,
    /// column checksum otherwise. Null when the cursor isn't OPTIMISTIC or
    /// isn't on a live row.
    /// </summary>
    private byte[]?[]? optimisticSnapshot;

    // SCROLL_LOCKS: cursor-scoped locks held directly (not through the
    // statement / transaction release lists). A table-IX per participating
    // base table is held for the cursor's open lifetime; the row-U locks
    // follow the current fetch position (one per non-NULL-extended slot) and
    // are released when the cursor scrolls off the row, closes, or deallocates.
    private readonly List<LockResource> scrollTableLocks = [];
    private readonly List<LockResource> scrollRowLocks = [];

    /// <summary>
    /// The value <c>CURSOR_STATUS</c> reports for this (existing) cursor:
    /// <c>-1</c> closed, <c>1</c> open DYNAMIC or open with ≥1 row, <c>0</c>
    /// open but empty. (A nonexistent / deallocated cursor reports <c>-3</c>
    /// from the lookup miss, not here.)
    /// </summary>
    public int StatusValue => !this.IsOpen
        ? -1
        : this.Sensitivity == CursorSensitivity.Dynamic
            ? 1
            : (this.staticRows?.Count ?? this.keysetIdentities?.Count ?? 0) > 0 ? 1 : 0;

    /// <summary>Stable <c>(page, slot)</c> address of each source row the cursor is
    /// positioned on — one slot per FROM source, null within the array on a
    /// NULL-extended outer-join side. The whole array is null when the cursor
    /// isn't on a live row (before first FETCH, past the end, or on a keyset
    /// hole). Read by positioned <c>WHERE CURRENT OF</c> DML.</summary>
    public (int Page, int Slot)?[]? CurrentRids;

    // STATIC: frozen projected values, walked by index.
    private List<SqlValue[]>? staticRows;
    // KEYSET: ordered snapshot of row identities (membership frozen at OPEN),
    // one slot per FROM source. UniqueKeys carries the tuple when that source's
    // table has a PK/UNIQUE — KEYSET membership tracks by that, matching SQL
    // Server's keyset-is-identified-by-the-unique-index behavior. A null slot
    // falls back to the address (no-unique-key heap path; simulator extension).
    private List<(SqlValue[]?[] UniqueKeys, (int Page, int Slot)?[] Rids)>? keysetIdentities;
    // Position for indexed (STATIC / KEYSET): -1 before-first, == count after-last.
    private int position;

    // DYNAMIC: last-emitted row plus before/after sentinels (no stored list).
    private Selection.CursorRow? dynamicLast;
    private bool dynamicBeforeFirst;
    private bool dynamicAfterLast;

    /// <summary>OPEN the cursor: materialize per sensitivity and seed
    /// <c>@@CURSOR_ROWS</c>. Raises Msg 16905 if already open.</summary>
    public void Open(BatchContext batch)
    {
        if (this.IsOpen)
            throw SimulatedSqlException.CursorAlreadyOpen();

        switch (this.Sensitivity)
        {
            case CursorSensitivity.Static:
                this.staticRows = [.. this.Selection.Execute(batch).RowBytes.Select(b => RowDecoder.DecodeRow(this.Selection.Schema, b))];
                this.position = -1;
                batch.Connection.LastCursorRows = this.staticRows.Count;
                break;
            case CursorSensitivity.Keyset:
                this.keysetIdentities = [.. this.Selection.EnumerateForCursor(batch).Select(r => (r.UniqueKeys, r.Rids))];
                this.position = -1;
                batch.Connection.LastCursorRows = this.keysetIdentities.Count;
                break;
            default: // Dynamic
                this.dynamicLast = null;
                this.dynamicBeforeFirst = true;
                this.dynamicAfterLast = false;
                batch.Connection.LastCursorRows = -1;
                break;
        }

        this.CurrentRids = null;
        this.IsOpen = true;

        // SCROLL_LOCKS: take table-IX on every participating table for the
        // cursor's open lifetime (the per-row U locks ride the fetch position).
        // Held cursor-scoped, so they outlive individual statements and any
        // autocommit boundary — matching real SQL Server, where scroll locks
        // persist while the cursor is positioned regardless of an enclosing
        // transaction.
        if (this.Concurrency == CursorConcurrency.ScrollLocks)
        {
            var connection = batch.Connection;
            foreach (var table in this.BaseTables)
            {
                if (this.scrollTableLocks.Contains(table.TableDataLock))
                    continue;
                connection.Simulation.LockManager.Acquire(table.TableDataLock, LockMode.IntentExclusive, connection, connection.LockTimeoutMillis);
                this.scrollTableLocks.Add(table.TableDataLock);
            }
        }
    }

    /// <summary>CLOSE the cursor: release the materialized state and reset
    /// position. The cursor stays declared (re-OPEN-able). Raises Msg 16917
    /// (state 1) if not open.</summary>
    public void Close(BatchContext batch)
    {
        if (!this.IsOpen)
            throw SimulatedSqlException.CursorNotOpen(state: 1);
        this.ReleaseScrollLocks(batch.Connection);
        this.staticRows = null;
        this.keysetIdentities = null;
        this.dynamicLast = null;
        this.CurrentRids = null;
        this.optimisticSnapshot = null;
        this.IsOpen = false;
    }

    /// <summary>
    /// Releases both cursor-scoped SCROLL_LOCKS locks (the position-following
    /// row-U and the open-lifetime table-IX). Called on CLOSE, on the last
    /// DEALLOCATE, and at connection dispose. No-op for non-SCROLL_LOCKS
    /// cursors.
    /// </summary>
    internal void ReleaseScrollLocks(SimulatedDbConnection connection)
    {
        var lockManager = connection.Simulation.LockManager;
        foreach (var row in this.scrollRowLocks)
            lockManager.Release(row, LockMode.Update, connection);
        this.scrollRowLocks.Clear();
        foreach (var table in this.scrollTableLocks)
            lockManager.Release(table, LockMode.IntentExclusive, connection);
        this.scrollTableLocks.Clear();
    }

    /// <summary>
    /// Moves the SCROLL_LOCKS row-U locks onto the freshly-fetched row:
    /// releases the rows we scrolled off (if any) and acquires U on every
    /// non-NULL-extended slot of <see cref="CurrentRids"/>. A concurrent writer
    /// of a currently-held row then blocks (U conflicts with the writer's X);
    /// scrolling away frees it.
    /// </summary>
    private void MoveScrollLock(BatchContext batch)
    {
        var connection = batch.Connection;
        var lockManager = connection.Simulation.LockManager;
        foreach (var prior in this.scrollRowLocks)
            lockManager.Release(prior, LockMode.Update, connection);
        this.scrollRowLocks.Clear();
        if (this.CurrentRids is not { } rids)
            return;
        for (var i = 0; i < rids.Length; i++)
        {
            if (rids[i] is not { } rid)
                continue;
            // A self-join reaches the same row through two slots; one U lock
            // covers it, and holding one reference keeps the release balanced.
            var resource = this.BaseTables[i].GetOrCreateRowLock(rid.Page, rid.Slot);
            if (this.scrollRowLocks.Contains(resource))
                continue;
            lockManager.Acquire(resource, LockMode.Update, connection, connection.LockTimeoutMillis);
            this.scrollRowLocks.Add(resource);
        }
    }

    /// <summary>
    /// For an <see cref="CursorConcurrency.Optimistic"/> cursor, raises the
    /// optimistic-conflict chain (Msg 16947 / 16934) when the current row's
    /// live bytes differ from the snapshot captured at FETCH — a value change,
    /// a rowversion bump, or the row's deletion out-of-band. No-op for other
    /// concurrency modes. Called at positioned UPDATE / DELETE time.
    /// </summary>
    internal void CheckOptimisticConflict()
    {
        if (this.Concurrency != CursorConcurrency.Optimistic)
            return;
        if (this.CurrentRids is not { } rids || this.optimisticSnapshot is not { } snapshot)
            throw SimulatedSqlException.CursorOptimisticConflict();
        for (var i = 0; i < rids.Length; i++)
        {
            var live = rids[i] is { } rid ? this.BaseTables[i].Heap.ReadSlotBytes(rid.Page, rid.Slot) : null;
            if (live is null
                ? snapshot[i] is not null
                : snapshot[i] is null || !live.AsSpan().SequenceEqual(snapshot[i]))
            {
                throw SimulatedSqlException.CursorOptimisticConflict();
            }
        }
    }

    /// <summary>
    /// The index of the first FROM slot backed by <paramref name="table"/>, or
    /// <c>-1</c> when the cursor doesn't read it. Positioned DML binds to the
    /// first occurrence, matching real SQL Server (which reports Msg 16961 when
    /// a self-join makes the choice ambiguous).
    /// </summary>
    internal int IndexOfBaseTable(HeapTable table)
    {
        for (var i = 0; i < this.BaseTables.Length; i++)
        {
            if (ReferenceEquals(this.BaseTables[i], table))
                return i;
        }
        return -1;
    }

    /// <summary>How many FROM slots are backed by <paramref name="table"/> —
    /// more than one means a self-join, which real reports Msg 16961 for at
    /// positioned-DML time.</summary>
    internal int CountBaseTable(HeapTable table)
    {
        var count = 0;
        foreach (var candidate in this.BaseTables)
        {
            if (ReferenceEquals(candidate, table))
                count++;
        }
        return count;
    }

    /// <summary>
    /// True when <paramref name="table"/> may be mutated through a positioned
    /// <c>WHERE CURRENT OF</c>: always true unless the cursor carries a
    /// <c>FOR UPDATE OF (…)</c> column list naming none of its columns.
    /// Probe-confirmed: real narrows the cursor's updatable <em>tables</em> to
    /// those owning a listed column and reports Msg 16933 (not 16932) for any
    /// other table, including on a DELETE.
    /// </summary>
    internal bool IsTableUpdatable(HeapTable table, BatchContext batch)
    {
        if (this.ForUpdateColumns is null)
            return true;
        var collation = batch.CurrentDatabase.Collation;
        foreach (var allowed in this.ForUpdateColumns)
        {
            foreach (var column in table.Columns)
            {
                if (collation.Equals(allowed, column.Name))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="column"/> may be updated through a positioned
    /// <c>WHERE CURRENT OF</c>: always true unless the cursor carries a
    /// <c>FOR UPDATE OF (…)</c> column list that omits it (Msg 16932).
    /// </summary>
    internal bool IsColumnUpdatable(string column, BatchContext batch)
    {
        if (this.ForUpdateColumns is null)
            return true;
        foreach (var allowed in this.ForUpdateColumns)
        {
            if (batch.CurrentDatabase.Collation.Equals(allowed, column))
                return true;
        }
        return false;
    }

    /// <summary>
    /// FETCH one row in the requested direction. Returns the SQL Server
    /// <c>@@FETCH_STATUS</c> (0 success, -1 past end / no row, -2 keyset member
    /// deleted) and the projected values (null when status ≠ 0). Validates
    /// scrollability (Msg 16925) and open-state (Msg 16917 state 2).
    /// </summary>
    public (int Status, SqlValue[]? Values) Fetch(BatchContext batch, FetchDirection direction, long offset)
    {
        if (!this.IsOpen)
            throw SimulatedSqlException.CursorNotOpen(state: 2);
        this.EnsureDirectionAllowed(direction);
        var result = this.Sensitivity switch
        {
            CursorSensitivity.Static => this.FetchStatic(direction, offset),
            CursorSensitivity.Keyset => this.FetchKeyset(batch, direction, offset),
            _ => this.FetchDynamic(batch, direction, offset),
        };

        // OPTIMISTIC: snapshot the landed row's live bytes (per source) so a
        // later positioned UPDATE / DELETE can detect out-of-band modification.
        // SCROLL_LOCKS: move the cursor-scoped U locks onto the newly-fetched
        // row (releasing the rows we scrolled off), so a concurrent writer of a
        // current row blocks. Both are no-ops when the fetch didn't land on a
        // live row.
        this.optimisticSnapshot = null;
        if (this.Concurrency == CursorConcurrency.Optimistic && this.CurrentRids is { } fetched)
        {
            var snapshot = new byte[]?[fetched.Length];
            for (var i = 0; i < fetched.Length; i++)
                snapshot[i] = fetched[i] is { } orid ? this.BaseTables[i].Heap.ReadSlotBytes(orid.Page, orid.Slot) : null;
            this.optimisticSnapshot = snapshot;
        }
        if (this.Concurrency == CursorConcurrency.ScrollLocks)
            this.MoveScrollLock(batch);

        return result;
    }

    /// <summary>
    /// A dynamic-sensitivity cursor can't position by ordinal, so ABSOLUTE
    /// raises Msg 16925 — real checks that before scrollability, which is why
    /// a bare FORWARD_ONLY cursor reports it too. Anything other than NEXT on
    /// a cursor that isn't scrollable raises Msg 16911. RELATIVE is legal on a
    /// scrollable dynamic cursor; only ABSOLUTE isn't (probe-confirmed).
    /// </summary>
    private void EnsureDirectionAllowed(FetchDirection direction)
    {
        if (this.Sensitivity == CursorSensitivity.Dynamic && direction == FetchDirection.Absolute)
            throw SimulatedSqlException.CursorFetchTypeNotAllowed(direction.ToString());
        if (direction != FetchDirection.Next && !this.Scrollable)
            throw SimulatedSqlException.CursorFetchTypeForwardOnly(LowercaseDirection(direction));
    }

    /// <summary>Direction name as Msg 16911 spells it.</summary>
    private static string LowercaseDirection(FetchDirection direction) => direction switch
    {
        FetchDirection.Absolute => "absolute",
        FetchDirection.First => "first",
        FetchDirection.Last => "last",
        FetchDirection.Next => "next",
        FetchDirection.Prior => "prior",
        _ => "relative",
    };

    private (int, SqlValue[]?) FetchStatic(FetchDirection direction, long offset)
    {
        var count = this.staticRows!.Count;
        if (!this.TryMoveIndex(direction, offset, count))
        {
            this.CurrentRids = null;
            return (-1, null);
        }
        // STATIC is read-only; CurrentRids stays null (WHERE CURRENT OF rejected upstream).
        return (0, this.staticRows[this.position]);
    }

    private (int, SqlValue[]?) FetchKeyset(BatchContext batch, FetchDirection direction, long offset)
    {
        var count = this.keysetIdentities!.Count;
        if (!this.TryMoveIndex(direction, offset, count))
        {
            this.CurrentRids = null;
            return (-1, null);
        }

        var (keys, rids) = this.keysetIdentities[this.position];
        foreach (var row in this.Selection.EnumerateForCursor(batch))
        {
            if (Selection.CursorIdentityMatches(row, keys, rids))
            {
                this.CurrentRids = row.Rids;
                return (0, row.Values);
            }
        }
        // Member deleted out from under the keyset (or its unique-key columns
        // changed, making the row no longer findable by the snapshotted key —
        // on a join, either side going away is enough): status -2, no current
        // row.
        this.CurrentRids = null;
        return (-2, null);
    }

    /// <summary>
    /// Advances <see cref="position"/> for an indexed (STATIC / KEYSET) cursor.
    /// Returns false (leaving position at the before-first / after-last
    /// sentinel) when the move lands outside <c>[0, count)</c>.
    /// </summary>
    private bool TryMoveIndex(FetchDirection direction, long offset, int count)
    {
        var target = direction switch
        {
            FetchDirection.Next => (long)this.position + 1,
            FetchDirection.Prior => (long)this.position - 1,
            FetchDirection.First => 0L,
            FetchDirection.Last => (long)count - 1,
            FetchDirection.Absolute => offset > 0 ? offset - 1 : offset < 0 ? count + offset : -1,
            _ => this.position + offset, // Relative
        };

        if (target < 0)
        {
            this.position = -1;
            return false;
        }
        if (target >= count)
        {
            this.position = count;
            return false;
        }
        this.position = (int)target;
        return true;
    }

    private (int, SqlValue[]?) FetchDynamic(BatchContext batch, FetchDirection direction, long offset)
    {
        var live = this.Selection.EnumerateForCursor(batch);
        var target = direction switch
        {
            FetchDirection.First => live.Count > 0 ? live[0] : null,
            FetchDirection.Last => live.Count > 0 ? live[^1] : null,
            FetchDirection.Prior => this.DynamicPrior(live),
            FetchDirection.Relative => this.DynamicRelative(live, offset),
            _ => this.DynamicNext(live), // Next
        };

        if (target is null)
        {
            this.CurrentRids = null;
            return (-1, null);
        }

        this.dynamicLast = target;
        this.dynamicBeforeFirst = false;
        this.dynamicAfterLast = false;
        this.CurrentRids = target.Rids;
        return (0, target.Values);
    }

    /// <summary>
    /// RELATIVE on a dynamic cursor walks the live set one row at a time,
    /// since there is no stable ordinal to jump to. A zero offset re-reads the
    /// row the cursor sits on; walking off either end leaves the cursor there,
    /// exactly as the single-step forms do.
    /// </summary>
    private Selection.CursorRow? DynamicRelative(List<Selection.CursorRow> live, long offset)
    {
        if (offset == 0)
            return this.dynamicLast is null ? null : this.DynamicCurrent(live);

        Selection.CursorRow? target = null;
        for (var i = 0L; i < Math.Abs(offset); i++)
        {
            target = offset > 0 ? this.DynamicNext(live) : this.DynamicPrior(live);
            if (target is null)
                return null;
            this.dynamicLast = target;
            this.dynamicBeforeFirst = false;
            this.dynamicAfterLast = false;
        }
        return target;
    }

    /// <summary>The live row the cursor currently sits on, or null once it has moved off the set.</summary>
    private Selection.CursorRow? DynamicCurrent(List<Selection.CursorRow> live)
    {
        foreach (var row in live)
        {
            if (this.Selection.CompareCursorRows(row, this.dynamicLast!) == 0)
                return row;
        }
        return null;
    }

    private Selection.CursorRow? DynamicNext(List<Selection.CursorRow> live)
    {
        if (this.dynamicAfterLast)
            return null;
        if (this.dynamicBeforeFirst || this.dynamicLast is null)
        {
            if (live.Count == 0)
            {
                this.dynamicAfterLast = true;
                return null;
            }
            return live[0];
        }
        foreach (var row in live)
        {
            if (this.Selection.CompareCursorRows(row, this.dynamicLast) > 0)
                return row;
        }
        this.dynamicAfterLast = true;
        return null;
    }

    private Selection.CursorRow? DynamicPrior(List<Selection.CursorRow> live)
    {
        if (this.dynamicBeforeFirst)
            return null;
        if (this.dynamicAfterLast)
            return live.Count > 0 ? live[^1] : null;
        if (this.dynamicLast is null)
            return null;
        for (var i = live.Count - 1; i >= 0; i--)
        {
            if (this.Selection.CompareCursorRows(live[i], this.dynamicLast) < 0)
                return live[i];
        }
        this.dynamicBeforeFirst = true;
        return null;
    }
}
