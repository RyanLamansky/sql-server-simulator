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
internal sealed class TriggerFrame(Trigger trigger, HeapTable? inserted, HeapTable? deleted)
{
    public readonly Trigger Trigger = trigger;

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
