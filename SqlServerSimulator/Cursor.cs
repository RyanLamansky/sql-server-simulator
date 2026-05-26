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
/// A session-scoped T-SQL cursor (declared with <c>DECLARE … CURSOR FOR
/// &lt;select&gt;</c>). Lives in <see cref="SimulatedDbConnection.Cursors"/>.
/// Position is tracked by the base row's stable <c>(page, slot)</c> address,
/// which <see cref="Heap.UpdateAt"/> preserves through value updates by
/// rewriting in place (or installing a forwarding pointer) — so KEYSET
/// membership tracking and positioned <c>WHERE CURRENT OF</c> DML work
/// without requiring the base table to have a unique key.
/// </summary>
internal sealed class Cursor(
    string name,
    Selection selection,
    CursorSensitivity sensitivity,
    bool scrollable,
    bool readOnly,
    HeapTable? baseTable)
{
    public readonly string Name = name;
    public readonly Selection Selection = selection;
    public readonly CursorSensitivity Sensitivity = sensitivity;
    public readonly bool Scrollable = scrollable;
    public readonly bool ReadOnly = readOnly;

    /// <summary>The single base table, non-null for KEYSET / DYNAMIC / positioned DML.</summary>
    public readonly HeapTable? BaseTable = baseTable;

    public bool IsOpen;

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

    /// <summary>Stable <c>(page, slot)</c> address of the row the cursor is positioned
    /// on, or null when not on a live row (before first FETCH, past the end, or on a
    /// keyset hole). Read by positioned <c>WHERE CURRENT OF</c> DML.</summary>
    public (int Page, int Slot)? CurrentRid;

    // STATIC: frozen projected values, walked by index.
    private List<SqlValue[]>? staticRows;
    // KEYSET: ordered snapshot of row identities (membership frozen at OPEN).
    // UniqueKey carries the tuple when the base table has a PK/UNIQUE — KEYSET
    // membership tracks by that, matching SQL Server's keyset-is-identified-by-
    // the-unique-index behavior. UniqueKey null falls back to Rid (no-unique-
    // key heap path; simulator extension).
    private List<(SqlValue[]? UniqueKey, (int Page, int Slot) Rid)>? keysetIdentities;
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
                this.keysetIdentities = [.. this.Selection.EnumerateForCursor(batch).Select(r => (r.UniqueKey, r.Rid))];
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

        this.CurrentRid = null;
        this.IsOpen = true;
    }

    /// <summary>CLOSE the cursor: release the materialized state and reset
    /// position. The cursor stays declared (re-OPEN-able). Raises Msg 16917
    /// (state 1) if not open.</summary>
    public void Close()
    {
        if (!this.IsOpen)
            throw SimulatedSqlException.CursorNotOpen(state: 1);
        this.staticRows = null;
        this.keysetIdentities = null;
        this.dynamicLast = null;
        this.CurrentRid = null;
        this.IsOpen = false;
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
        return this.Sensitivity switch
        {
            CursorSensitivity.Static => this.FetchStatic(direction, offset),
            CursorSensitivity.Keyset => this.FetchKeyset(batch, direction, offset),
            _ => this.FetchDynamic(batch, direction),
        };
    }

    /// <summary>
    /// Forward-only cursors allow only NEXT; DYNAMIC never supports the
    /// absolute-positioning forms — both raise Msg 16925 (probe-confirmed).
    /// </summary>
    private void EnsureDirectionAllowed(FetchDirection direction)
    {
        if ((direction != FetchDirection.Next && !this.Scrollable)
            || (this.Sensitivity == CursorSensitivity.Dynamic
                && direction is FetchDirection.Absolute or FetchDirection.Relative))
        {
            throw SimulatedSqlException.CursorFetchTypeNotAllowed(direction.ToString());
        }
    }

    private (int, SqlValue[]?) FetchStatic(FetchDirection direction, long offset)
    {
        var count = this.staticRows!.Count;
        if (!this.TryMoveIndex(direction, offset, count))
        {
            this.CurrentRid = null;
            return (-1, null);
        }
        // STATIC is read-only; CurrentRid stays null (WHERE CURRENT OF rejected upstream).
        return (0, this.staticRows[this.position]);
    }

    private (int, SqlValue[]?) FetchKeyset(BatchContext batch, FetchDirection direction, long offset)
    {
        var count = this.keysetIdentities!.Count;
        if (!this.TryMoveIndex(direction, offset, count))
        {
            this.CurrentRid = null;
            return (-1, null);
        }

        var (key, rid) = this.keysetIdentities[this.position];
        foreach (var row in this.Selection.EnumerateForCursor(batch))
        {
            var match = key is not null
                ? row.UniqueKey is not null && Selection.CompareKeyTuples(row.UniqueKey, key) == 0
                : row.Rid.Equals(rid);
            if (match)
            {
                this.CurrentRid = row.Rid;
                return (0, row.Values);
            }
        }
        // Member deleted out from under the keyset (or its unique-key columns
        // changed, making the row no longer findable by the snapshotted key):
        // status -2, no current row.
        this.CurrentRid = null;
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

    private (int, SqlValue[]?) FetchDynamic(BatchContext batch, FetchDirection direction)
    {
        var live = this.Selection.EnumerateForCursor(batch);
        var target = direction switch
        {
            FetchDirection.First => live.Count > 0 ? live[0] : null,
            FetchDirection.Last => live.Count > 0 ? live[^1] : null,
            FetchDirection.Prior => this.DynamicPrior(live),
            _ => this.DynamicNext(live), // Next
        };

        if (target is null)
        {
            this.CurrentRid = null;
            return (-1, null);
        }

        this.dynamicLast = target;
        this.dynamicBeforeFirst = false;
        this.dynamicAfterLast = false;
        this.CurrentRid = target.Rid;
        return (0, target.Values);
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
