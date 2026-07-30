using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-trigger-invocation frame attached to the child <see cref="BatchContext"/>
/// of a firing trigger body. Holds the <c>INSERTED</c> / <c>DELETED</c>
/// pseudo-table rowsets a trigger body queries from. Routed through
/// <see cref="BatchContext.TryResolveTable"/>'s 1-part name path before
/// the schema / temp-table dispatch, so bare <c>FROM inserted</c> /
/// <c>JOIN deleted ON ...</c> resolves to these instances. Set non-null
/// only while a trigger body is executing.
/// </summary>
internal sealed class TriggerFrame(Trigger trigger, HeapTable? inserted, HeapTable? deleted, byte[] columnsUpdatedMask)
{
    public readonly Trigger Trigger = trigger;

    /// <summary>
    /// The firing statement's updated-column bitmask, as
    /// <c>COLUMNS_UPDATED()</c> returns it and <c>UPDATE(col)</c> tests it.
    /// Bit <c>(id - 1) % 8</c> of byte <c>(id - 1) / 8</c> marks column_id
    /// <c>id</c> — least-significant bit first — over
    /// <c>ceil(MaxColumnIdUsed / 8)</c> bytes, so a dropped column keeps its
    /// bit position (probe-confirmed against SQL Server 2025).
    /// An INSERT sets every bit through the watermark whatever its column
    /// list named, an UPDATE sets exactly the SET-clause columns (whether or
    /// not the value actually changed, and even when no row matched), and a
    /// DELETE leaves this empty — <c>COLUMNS_UPDATED()</c> is a zero-length
    /// varbinary there rather than a run of zero bytes.
    /// </summary>
    public readonly byte[] ColumnsUpdatedMask = columnsUpdatedMask;

    /// <summary>
    /// Whether the firing statement touched the column with the given
    /// 1-based <c>column_id</c>. Out-of-range ids and the empty DELETE mask
    /// both report false.
    /// </summary>
    public bool IsColumnUpdated(int columnId)
    {
        var index = (columnId - 1) / 8;
        return columnId >= 1
            && index < this.ColumnsUpdatedMask.Length
            && (this.ColumnsUpdatedMask[index] & (1 << ((columnId - 1) % 8))) != 0;
    }

    /// <summary>
    /// The INSERTED pseudo-table: visible inside INSERT / UPDATE
    /// triggers (DELETE triggers don't populate it — probe-confirmed:
    /// queries from <c>inserted</c> inside an AFTER DELETE trigger
    /// return an empty rowset rather than raising). Carries the parent
    /// table's column schema; rows are the post-DML values for INSERT,
    /// the new values for UPDATE.
    /// </summary>
    public readonly HeapTable? Inserted = inserted;

    /// <summary>
    /// The DELETED pseudo-table: visible inside DELETE / UPDATE
    /// triggers. Carries the parent table's column schema; rows are
    /// the pre-DML (deleted) values for DELETE, the old values for UPDATE.
    /// </summary>
    public readonly HeapTable? Deleted = deleted;
}
