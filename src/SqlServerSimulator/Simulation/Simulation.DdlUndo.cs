using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Logs, in the session's open transaction, how to reverse a DDL change to
    /// a permanent object, so a <c>ROLLBACK</c> — or a savepoint rollback past
    /// it — undoes the DDL as SQL Server does (probed 2026-09-25: a rolled-back
    /// <c>CREATE TABLE</c>, view, procedure, <c>ALTER TABLE … ADD</c> or
    /// <c>CREATE INDEX</c> leaves no trace, and a rolled-back <c>DROP TABLE</c>
    /// restores the table with its rows). Outside a transaction the statement
    /// commits as it runs, so there is nothing to log.
    /// </summary>
    internal static void RecordDdlUndo(BatchContext batch, Action undo)
    {
        if (batch.Connection.CurrentTransaction is { } transaction)
            transaction.UndoLog.RecordSchemaChange(batch.Connection.Simulation, undo);
    }

    internal static void RecordDdlUndo(ParserContext context, Action undo) => RecordDdlUndo(context.Batch, undo);

    /// <summary>
    /// <see cref="RecordDdlUndo(BatchContext, Action)"/> for a catalog slot the statement stored or
    /// removed: the undo puts <paramref name="previous"/> back under
    /// <paramref name="name"/>, or empties the slot when there was none.
    /// </summary>
    internal static void RecordSlotUndo<T>(ParserContext context, ConcurrentDictionary<string, T> owner, string name, T? previous)
        where T : class =>
        RecordDdlUndo(context, () =>
        {
            if (previous is null)
                _ = owner.TryRemove(name, out _);
            else
                owner[name] = previous;
        });

    /// <summary>
    /// <see cref="RecordDdlUndo(BatchContext, Action)"/> for a statement about to change
    /// <paramref name="table"/> in place — the <c>ALTER TABLE</c> family, index
    /// DDL, a column or index rename — capturing it whole first. A table
    /// variable is left alone, since its changes never roll back.
    /// </summary>
    internal static void RecordTableDdlUndo(BatchContext batch, HeapTable table)
    {
        if (batch.Connection.CurrentTransaction is null || table.IsTableVariable)
            return;
        var snapshot = new HeapTableSnapshot(table, table.OwningDatabase ?? batch.CurrentDatabase);
        RecordDdlUndo(batch, snapshot.Restore);
    }

    internal static void RecordTableDdlUndo(ParserContext context, HeapTable table) => RecordTableDdlUndo(context.Batch, table);
}
