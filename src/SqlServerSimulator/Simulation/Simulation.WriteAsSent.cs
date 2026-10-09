using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// A DML statement whose client reads its OUTPUT rows as they come writes each
// row as it sends it (WritesAsItSends): the shapes real's plans give that
// pipeline, and the target walk it reads its rows through.
partial class Simulation
{
    /// <summary>
    /// When a DML statement writing as it sends (<see cref="WritesAsItSends"/>)
    /// writes its rows, which is the shape of real's plan for it (probed
    /// 2026-10-09 against SQL Server 2025, an <c>UPDATE</c> or <c>DELETE</c> of
    /// 2,000 rows a reader is two rows into).
    /// </summary>
    private enum WriteOrder : byte
    {
        /// <summary>
        /// Every row before the first goes out: an <c>UPDATE</c> of a
        /// clustered or unique key, which real sorts and collapses whole; a
        /// system-versioned target, whose current rows real writes whole
        /// before the history rows go out with the rows; a cascading foreign
        /// key's parent; an indexed view over the target.
        /// </summary>
        WholeFirst,

        /// <summary>
        /// Every qualifying row read first, held in U, and each written as it
        /// goes out: real's spool between the read and the write when the
        /// write could meet its own rows again — an <c>UPDATE</c> of a
        /// nonclustered index's column, a subquery in the statement — the
        /// rows ahead holding U and reading as they were read.
        /// </summary>
        ReadsFirst,

        /// <summary>
        /// Each row read, locked and written as it goes out, the rows ahead
        /// untouched: another session's write to one goes in, and is read as
        /// written when the statement reaches it.
        /// </summary>
        AsRead,
    }

    /// <summary>
    /// The <see cref="WriteOrder"/> of a single-table <c>UPDATE</c> setting
    /// <paramref name="setOrdinals"/> of <paramref name="table"/>, whose
    /// <c>WHERE</c> or <c>SET</c> list holds a subquery when
    /// <paramref name="readsSubquery"/> says so (<see cref="ReadsSubquery"/>).
    /// </summary>
    private static WriteOrder UpdateWriteOrder(BatchContext batch, HeapTable table, IReadOnlyList<int> setOrdinals, bool readsSubquery, Selection.DmlTopLimit? top)
    {
        if (table.SystemVersioning is not null
            || table.DependentIndexedViews.Count != 0
            || table.IsMemoryOptimized
            || HasAfterTrigger(batch, table, TriggerActions.Update)
            || HasInsteadOfTrigger(batch, table, TriggerActions.Update))
        {
            return WriteOrder.WholeFirst;
        }
        foreach (var key in table.KeyConstraints)
        {
            if (SetsAnyOf(table, setOrdinals, key.FullOrdinals))
                return WriteOrder.WholeFirst;
        }
        var order = WriteOrder.AsRead;
        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled)
                continue;
            if (SetsAnyOf(table, setOrdinals, index.KeyFullOrdinals))
            {
                if (index.IsClustered || index.IsUnique)
                    return WriteOrder.WholeFirst;
                order = WriteOrder.ReadsFirst;
            }
            else if (SetsAnyOf(table, setOrdinals, index.IncludedColumnOrdinals))
            {
                order = WriteOrder.ReadsFirst;
            }
        }
        if (table.XmlIndexes.Count != 0 || table.SpatialIndexes.Count != 0 || table.JsonIndexes.Count != 0 || table.VectorIndexes.Count != 0)
            return WriteOrder.WholeFirst;
        return order == WriteOrder.AsRead && (top is { Percent: true } || readsSubquery) ? WriteOrder.ReadsFirst : order;
    }

    /// <summary>
    /// The <see cref="WriteOrder"/> of a joined <c>UPDATE</c> or
    /// <c>DELETE</c> whose single-table form writes in <paramref name="single"/>
    /// order: its join is read whole here before the first row is written,
    /// each row's U held until its write when real's plan spools the read —
    /// the single-table form's reasons, or another source reading the target
    /// table — and left free otherwise, as real's unspooled join leaves the
    /// rows ahead of it (probed 2026-10-09 against SQL Server 2025: a self-join
    /// two rows into its 2,000 rows held U on the 1,980 ahead, a join to
    /// another table nothing ahead).
    /// </summary>
    private static WriteOrder JoinedWriteOrder(WriteOrder single, FromSource[] sources, int targetIndex, HeapTable table)
    {
        if (single != WriteOrder.AsRead)
            return single;
        for (var i = 0; i < sources.Length; i++)
        {
            if (i != targetIndex && ReferenceEquals(sources[i].BackingTable, table))
                return WriteOrder.ReadsFirst;
        }
        return WriteOrder.AsRead;
    }

    /// <summary>
    /// The <see cref="WriteOrder"/> of a single-table <c>DELETE</c> from
    /// <paramref name="table"/>, whose <c>WHERE</c> holds a subquery when
    /// <paramref name="readsSubquery"/> says so.
    /// </summary>
    private static WriteOrder DeleteWriteOrder(BatchContext batch, HeapTable table, bool readsSubquery, Selection.DmlTopLimit? top)
    {
        if (table.SystemVersioning is not null
            || table.DependentIndexedViews.Count != 0
            || table.IsMemoryOptimized
            || table.GraphKind == GraphTableKind.Node
            || HasAfterTrigger(batch, table, TriggerActions.Delete)
            || HasInsteadOfTrigger(batch, table, TriggerActions.Delete))
        {
            return WriteOrder.WholeFirst;
        }
        foreach (var key in table.IncomingForeignKeys)
        {
            if (key is { IsDisabled: false, DeleteAction: not ReferentialAction.NoAction })
                return WriteOrder.WholeFirst;
        }
        return top is { Percent: true } || readsSubquery ? WriteOrder.ReadsFirst : WriteOrder.AsRead;
    }

    /// <summary>
    /// Whether a <c>DELETE</c> writing as it sends sends each row before it
    /// deletes it, as real's plan does for a table with no nonclustered index
    /// (probed 2026-10-09 against SQL Server 2025, a foreign key referencing
    /// the table or none: a reader two rows into 2,000 deleted rows of 2,010
    /// bytes leaves 20 held in X and 19 gone, as a heap's do; with a
    /// nonclustered index, 20 gone). A row a foreign key's child still
    /// references is refused before it is sent (<see cref="CheckBeforeSending"/>),
    /// as real's is; a table referencing itself, or with a block predicate
    /// to judge the row first, deletes it before sending it.
    /// </summary>
    private static bool SendsBeforeDeleting(BatchContext batch, HeapTable table)
    {
        if (ReferencesItself(table) || RowSecurity.For(batch, table)?.BlockFor(BlockOperation.BeforeDelete) is not null)
            return false;
        foreach (var key in table.KeyConstraints)
        {
            if (!key.IsClustered)
                return false;
        }
        foreach (var index in table.Indexes)
        {
            if (!index.IsClustered && !index.IsDisabled)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Refuses, ahead of sending it, a row a <c>DELETE</c> sends before
    /// deleting (<see cref="SendsBeforeDeleting"/>) that a foreign key's child
    /// still references, as real's plan checks the key before the row goes
    /// out (probed 2026-10-09 against SQL Server 2025: a delete of 2,000 rows
    /// whose 1,500th is referenced sends 1,499, then Msg 547).
    /// </summary>
    private static void CheckBeforeSending(ParserContext context, HeapTable table, SqlValue[]? fullOld)
    {
        if (table.IncomingForeignKeys.Count != 0 && fullOld is not null)
            EnforceIncomingForeignKeysOnDelete(table, [fullOld], context, "DELETE", depth: 0);
    }

    /// <summary>
    /// Whether <paramref name="setOrdinals"/> sets a column of
    /// <paramref name="ordinals"/>, a computed one among them taken as set
    /// whenever anything is.
    /// </summary>
    private static bool SetsAnyOf(HeapTable table, IReadOnlyList<int> setOrdinals, int[] ordinals)
    {
        foreach (var ordinal in ordinals)
        {
            if (setOrdinals.Count != 0 && table.Columns[ordinal].Computed is not null)
                return true;
            for (var i = 0; i < setOrdinals.Count; i++)
            {
                if (setOrdinals[i] == ordinal)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether any of <paramref name="nodes"/> holds a subquery, which real's
    /// plan spools the write's read ahead of (probed 2026-10-09 against SQL
    /// Server 2025 for one reading the target; one reading only another table
    /// is taken the same way here). A plan asks once and keeps the answer.
    /// </summary>
    private static bool ReadsSubquery(IEnumerable<ExpressionNode?> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is null)
                continue;
            var found = false;
            node.Walk(ref found, static (_, shape, ref found) =>
            {
                foreach (var local in shape.Locals)
                {
                    if (local is Selection)
                        found = true;
                }
                return !found;
            });
            if (found)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The heap generation a write walking its target as it writes
    /// (<see cref="TargetRowsAsTheyStand"/>) last saw settle: its own writes
    /// move it on (<see cref="NoteOwnWrites"/>), so a change found once the
    /// statement has waited on its client is another session's.
    /// </summary>
    private sealed class TargetWalk
    {
        public long Generation;

        /// <summary>Takes the heap's generation after the walk's own writes as the one it reads from.</summary>
        public void NoteOwnWrites(HeapTable table) => this.Generation = Volatile.Read(ref table.Heap.MutationGeneration);
    }

    /// <summary>
    /// The rows a single-table write walks as it writes them
    /// (<see cref="WriteOrder.AsRead"/>), each as it stands when the walk
    /// reaches it: <paramref name="seek"/>'s rows read again at their
    /// addresses where the statement seeks, a scan's read
    /// on in key order from where the statement stood once it waited on its
    /// client while another session changed the table, as a streaming
    /// <c>SELECT</c>'s scan reads on (<see cref="ClusteredScan.Rows"/>), so a
    /// key inserted ahead is written and one deleted ahead isn't.
    /// </summary>
    private static IEnumerable<(int Page, int Slot, byte[] Bytes)> TargetRowsAsTheyStand(HeapTable table, IEnumerable<(int Page, int Slot, byte[] Bytes)>? seek, BatchContext batch, TargetWalk walk)
    {
        if (seek is not null)
        {
            foreach (var (page, slot, _) in seek)
            {
                if (table.Heap.ReadLiveRow(page, slot) is { } live)
                    yield return (page, slot, live);
            }
            yield break;
        }
        var counts = batch.Connection.StatementIo?.Touch(table);
        _ = counts?.ScanCount += 1;
        var lastPage = -1;
        var heap = table.Heap;
        var statement = batch.CurrentStatement;
        var suspensions = statement.Suspensions;
        walk.Generation = Volatile.Read(ref heap.MutationGeneration);
        var rowGroup = KeyLockGroup.RowGroupOf(table);
        var keys = rowGroup is null ? null : new List<SqlValueKey>();
        var order = ClusteredScan.Order(table, keys);
        if (keys is not null && (order is null || keys.Count != order.Count))
            keys = null;
        byte[]? lastRead = null;
        var seen = new HashSet<(int, int)>();
        using var heapRows = order is null ? heap.EnumerateRowsWithAddress().GetEnumerator() : null;
        for (var position = 0; ; position++)
        {
            if (statement.Suspensions != suspensions)
            {
                suspensions = statement.Suspensions;
                if (Volatile.Read(ref heap.MutationGeneration) is var now && now != walk.Generation)
                {
                    walk.Generation = now;
                    var readKey = default(SqlValueKey);
                    if (rowGroup is not null && (order is null ? ClusteredScan.ServesKeyedOrder(table) : keys is not null)
                        && (lastRead is null || rowGroup.TryReadKey(lastRead, out readKey)))
                    {
                        order = [];
                        keys = [];
                        foreach (var (key, rids) in BatchContext.KeysArrivedBetween(table, rowGroup, lastRead is null ? null : readKey, null))
                        {
                            foreach (var rid in rids)
                            {
                                order.Add(rid);
                                keys.Add(key!.Value);
                            }
                        }
                        position = 0;
                    }
                }
            }
            (int Page, int Slot) address;
            byte[]? bytes;
            if (order is not null)
            {
                if (position >= order.Count)
                    yield break;
                address = order[position];
                if (!seen.Add(address))
                    continue;
                bytes = heap.ReadLiveRow(address.Page, address.Slot);
            }
            else
            {
                if (!heapRows!.MoveNext())
                    yield break;
                var (page, slot, live) = heapRows.Current;
                address = (page, slot);
                bytes = live;
            }
            if (bytes is null)
                continue;
            counts?.Enter(address.Page, ref lastPage);
            lastRead = bytes;
            yield return (address.Page, address.Slot, bytes);
        }
    }

    /// <summary>
    /// Takes and keeps U on each row a walk read whole before writing
    /// (<see cref="WriteOrder.ReadsFirst"/>), as real's spooled read holds
    /// them until the write converts each to X.
    /// </summary>
    private static void HoldReadRows(BatchContext batch, HeapTable table, List<(int PageIndex, int SlotIndex, byte[] Bytes)> rows)
    {
        if (!IsLockableTable(table))
            return;
        foreach (var (page, slot, _) in rows)
            _ = batch.HoldTargetRowForUpdate(table, page, slot);
    }

    /// <summary>The one <c>OUTPUT</c> row a write of one row produced.</summary>
    private static byte[] OutputRowOf(SimulatedStatementOutcome outcome) => ((List<byte[]>)((SimulatedSqlResultSet)outcome).RowBytes)[0];

    /// <summary>The result set of <paramref name="output"/>'s rows, written as they go out (see <see cref="SendAsWritten"/>).</summary>
    private static SimulatedSqlResultSet SentAsWritten(OutputProjection output, IEnumerable<byte[]> rows) =>
        new(output.Schema, output.ColumnNames, rows, recordsAffected: 0)
        {
            ColumnNullability = output.Nullability,
            WritesAsItSends = true,
        };
}
