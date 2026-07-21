using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>TRUNCATE TABLE &lt;name&gt;</c>. Routes <c>#foo</c> names to
    /// the connection's <see cref="SimulatedDbConnection.TempTables"/> dict;
    /// everything else to the named schema's heap-table dict via
    /// <see cref="Database.Schemas"/>. Missing target
    /// raises <c>Msg 4701</c> — distinct from <c>DROP TABLE</c>'s Msg 3701 and
    /// generic INSERT/UPDATE/DELETE's Msg 208.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Truncation clears every row and resets each identity column's
    /// high-water mark to its declared seed — probe-confirmed against
    /// SQL Server 2025 (2026-05-11) that a subsequent INSERT receives the
    /// seed, not the next-after-the-prior-max. Distinct from the simulator's
    /// general "identity bypasses the undo log" rule (which is INSERT-only):
    /// TRUNCATE's reset DOES participate in rollback, so a
    /// <c>BEGIN TRAN; TRUNCATE; ROLLBACK</c> restores both the row data and
    /// the original identity counter — also probe-confirmed.
    /// </para>
    /// <para>
    /// Rollback support uses a single <see cref="UndoLog"/> entry that
    /// snapshots the heap's pre-truncate <see cref="Heap.Pages"/> /
    /// <see cref="Heap.LobPages"/> lists and each identity column's
    /// pre-truncate high-water mark. Outside an explicit transaction the
    /// truncation commits immediately (no log entry — same pattern as
    /// regular CREATE / DROP TABLE).
    /// </para>
    /// <para>
    /// <c>@@ROWCOUNT</c> resets to 0 (probe-confirmed); skip-mode (un-taken
    /// IF, after BREAK / CONTINUE / RETURN) suppresses the entire action.
    /// Multi-part names (<c>tempdb..#foo</c>, <c>claude.dbo.t</c>) are
    /// accepted via the same lenient parser <c>DROP TABLE</c> uses —
    /// qualifier segments are cosmetic; the leaf is the routing key.
    /// </para>
    /// </remarks>
    private static void ParseTruncateStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume TRUNCATE
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Table })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired(); // consume TABLE

        var name = BatchContext.ParseObjectName(context);

        if (batch.IsSkipping)
            return;

        var isLocalTempTable = BatchContext.IsLocalTempName(name.Leaf);
        var isGlobalTempTable = BatchContext.IsGlobalTempName(name.Leaf);
        var destination = isLocalTempTable
            ? context.Connection.TempTables
            : isGlobalTempTable
                ? context.Connection.Simulation.GlobalTempTables
                : batch.TryResolveSchema(name, out var schema) ? schema.HeapTables : null;

        // Msg 4701 carries only the leaf name (probe-confirmed against SQL
        // Server 2025), distinct from Msg 208 / 3701 which embed the qualifier.
        if (destination is null || !destination.TryGetValue(name.Leaf, out var table))
            throw SimulatedSqlException.CannotTruncateObjectDoesNotExist(name.Leaf);

        // TRUNCATE requires ALTER on the object; denial surfaces as Msg 1088
        // (its own double-quoted shape), not Msg 229.
        if (!isLocalTempTable && !isGlobalTempTable && PermissionEnforcement.Applies(batch)
            && !PermissionChecker.IsGranted(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId,
                Permission.Alter, PermissionChecker.ClassObject, table.ObjectId, table.SchemaId))
        {
            throw SimulatedSqlException.CannotFindObjectForAlter(name.Leaf);
        }

        // Sch-M on the target for the duration of the statement — waits for
        // any concurrent Sch-S holders to drain before the destructive page-
        // swap and identity reset proceed.
        batch.AcquireStatementLock(table.SchemaLock, LockMode.SchemaModification);

        var oldPages = new List<HeapPage>(table.Heap.Pages);
        var oldLobPages = new List<HeapLobPage>(table.Heap.LobPages);
        var oldForwardTargets = new HashSet<(int Page, int Slot)>(table.Heap.ForwardTargets);
        var oldFreeLobPages = table.Heap.SnapshotFreeLobPages();

        var identitySnapshots = new List<(IdentityState State, long? HighWaterMark)>();
        foreach (var column in table.Columns)
        {
            if (column.Identity is { } identity)
                identitySnapshots.Add((identity, identity.Snapshot()));
        }

        table.Heap.Pages.Clear();
        table.Heap.LobPages.Clear();
        table.Heap.ForwardTargets.Clear();
        table.Heap.ClearFreeLobPages();
        table.Heap.ClearReclaimablePages();
        // The page-swap rewinds heap state without going through Insert / DeleteAt,
        // so force any live seek cache to rebuild against the now-empty heap.
        table.Heap.InvalidateSeekJournal();
        for (var i = 0; i < identitySnapshots.Count; i++)
            identitySnapshots[i].State.Restore(null);

        if (context.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTruncation(table.Heap, oldPages, oldLobPages, oldForwardTargets, oldFreeLobPages, [.. identitySnapshots]);
    }
}
