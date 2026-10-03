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
        List<(Parser.Expression Low, Parser.Expression? High)>? partitions = null;
        var afterName = context.SaveCheckpoint();
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.With })
            partitions = ParseTruncatePartitions(context);
        else
            context.RestoreCheckpoint(afterName);

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
        // A view is Msg 4708 (probed 2026-10-01).
        if (destination is null || !destination.TryGetValue(name.Leaf, out var table))
        {
            throw batch.TryResolveView(name, out _)
                ? SimulatedSqlException.CannotTruncateNonTable(name.Leaf)
                : SimulatedSqlException.CannotTruncateObjectDoesNotExist(name.Leaf);
        }

        // TRUNCATE deallocates pages, so a read-only database refuses it even
        // when the table already holds no rows (probe-confirmed).
        RejectOnMemoryOptimized(table, "The statement 'TRUNCATE TABLE'", 88);
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        if (table.VectorIndexes.Count > 0)
            throw SimulatedSqlException.VectorIndexedTableTruncate(table.Name);

        // TRUNCATE requires ALTER on the object; denial surfaces as Msg 1088
        // (its own double-quoted shape), not Msg 229.
        if (!isLocalTempTable && !isGlobalTempTable
            && !PermissionEnforcement.HasObjectAlter(batch, batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
        {
            throw SimulatedSqlException.CannotFindObjectForAlter(name.Leaf);
        }

        // Another table's FOREIGN KEY blocks the truncation even while it is
        // disabled; only a self-reference is let through (probe-confirmed
        // against SQL Server 2025).
        if (table.IncomingForeignKeys.Exists(fk => fk.ChildTable != table))
            throw SimulatedSqlException.CannotTruncateTableReferencedByForeignKey(name.Written);
        if (table.GraphKind == GraphTableKind.Node && EdgeConstraintsReferencing(context.Batch.DatabaseFor(table), table).Count > 0)
            throw SimulatedSqlException.CannotTruncateNodeTableReferencedByEdgeConstraint(name.Written);

        // Sch-M on the target — waits for every concurrent reader and open
        // writer to drain before the destructive page-swap and identity
        // reset proceed.
        batch.AcquireTableRedefinitionLock(table);
        VersionStore.NoteDefinitionChange(batch, table);

        // WITH (PARTITIONS …) deletes the listed partitions' rows instead.
        if (partitions is not null)
        {
            TruncatePartitions(batch, table, partitions);
            return;
        }

        var identitySnapshots = new List<(IdentityState State, Int128? HighWaterMark)>();
        foreach (var column in table.Columns)
        {
            if (column.Identity is { } identity)
                identitySnapshots.Add((identity, identity.Snapshot()));
        }

        // Sch-M excludes every reader, but not a version-store sweep freeing
        // chains, so the swap still runs under the heap's latch.
        List<HeapPage> oldPages;
        List<HeapLobPage> oldLobPages;
        HashSet<(int Page, int Slot)> oldForwardTargets;
        int[] oldFreeLobPages;
        using (table.Heap.EnterLatch())
        {
            oldPages = [.. table.Heap.Pages];
            oldLobPages = [.. table.Heap.LobPages];
            oldForwardTargets = table.Heap.SnapshotForwardTargets();
            oldFreeLobPages = table.Heap.SnapshotFreeLobPages();
            table.Heap.Pages.Clear();
            _ = table.Heap.RecomputeRowCount();
            table.Heap.LobPages.Clear();
            table.Heap.RestoreForwardTargets([]);
            table.Heap.ClearFreeLobPages();
            table.Heap.ClearReclaimablePages();
            table.Heap.RootedNullLobCells = null;
            // The page-swap rewinds heap state without going through Insert / DeleteAt,
            // so force any live seek cache to rebuild against the now-empty heap.
            table.Heap.InvalidateSeekJournal();
        }
        VersionStore.SetAsideVersions(batch, table);
        for (var i = 0; i < identitySnapshots.Count; i++)
            identitySnapshots[i].State.Restore(null);

        if (context.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTruncation(table.Heap, oldPages, oldLobPages, oldForwardTargets, oldFreeLobPages, [.. identitySnapshots]);

        // A tracked table forgets its change history and restarts it at the
        // current version, which the truncation itself doesn't advance (probed
        // 2026-09-27 against SQL Server 2025).
        if (table.ChangeTracking is { } tracking)
        {
            var database = batch.DatabaseFor(table);
            if (context.Connection.CurrentTransaction is { } trackingTx)
                trackingTx.UndoLog.RecordChangeTrackingTruncation(tracking, database);
            else
                tracking.Reset(database.ChangeTrackingVersion);
        }
    }
}
