namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// Replays logged commits' rows onto a restoring database's tables — forward,
/// or undone newest first — which is how a <c>RESTORE LOG … WITH STOPAT</c>
/// lands between two transactions.
/// </summary>
/// <remarks>
/// A row is matched by its whole image, not its address: a restore loads rows
/// afresh, so the addresses a log recorded mean nothing to the restored heap,
/// and two rows alike in every column are interchangeable. Each table a replay
/// touches is read, edited and loaded back whole, as the in-place restore loads
/// it.
/// </remarks>
internal static class LogReplay
{
    /// <summary>
    /// Applies <paramref name="commits"/> to <paramref name="database"/> in
    /// log order, or with <paramref name="inverse"/> undoes them newest first;
    /// forward, each written table's identity positions take the last
    /// commit's. Throws <see cref="InvalidOperationException"/> when a table
    /// or a row the log names isn't there.
    /// </summary>
    public static void Apply(Database database, List<LoggedCommit> commits, bool inverse)
    {
        var changes = new Dictionary<int, List<LoggedRowChange>>();
        var identities = new Dictionary<(int ObjectId, int Ordinal), (Int128? HighWaterMark, Int128? ReseededStart)>();
        for (var c = 0; c < commits.Count; c++)
        {
            var commit = commits[inverse ? commits.Count - 1 - c : c];
            for (var r = 0; r < commit.Rows.Count; r++)
            {
                var row = commit.Rows[inverse ? commit.Rows.Count - 1 - r : r];
                if (!changes.TryGetValue(row.ObjectId, out var list))
                    changes[row.ObjectId] = list = [];
                list.Add(inverse ? new LoggedRowChange(row.ObjectId, row.After, row.Before) : row);
            }
            if (!inverse)
            {
                foreach (var (objectId, ordinal, highWaterMark, reseededStart) in commit.Identities)
                    identities[(objectId, ordinal)] = (highWaterMark, reseededStart);
            }
        }
        foreach (var (objectId, list) in changes)
            Replay(FindTable(database, objectId), list);
        foreach (var ((objectId, ordinal), snapshot) in identities)
        {
            if (FindTable(database, objectId).Columns is var columns && ordinal < columns.Length && columns[ordinal].Identity is { } identity)
                identity.RestoreReseed(snapshot);
        }
    }

    /// <summary>
    /// A row as the log keeps it: its stored bytes behind a 0 when no cell
    /// lies off-row — the common case, a copy — else its values in
    /// <see cref="BackupRowCodec"/>'s form behind a 1, since an off-row
    /// cell's LOB chain belongs to the heap. One row's values always encode to
    /// one image, so equal rows compare equal whichever heap holds them.
    /// </summary>
    public static byte[] Image(HeapTable table, ReadOnlySpan<byte> row)
    {
        var columns = table.StoredColumns;
        if (table.Heap.ReclaimColumns is { } reclaim)
        {
            var heads = lobHeads ??= new List<int>(1);
            heads.Clear();
            RowDecoder.CollectLobHeads(reclaim, row, heads);
            if (heads.Count != 0)
                return [1, .. BackupRowCodec.Portable(columns, RowDecoder.DecodeRow(columns, row, table.Heap))];
        }
        return [0, .. row];
    }

    [ThreadStatic]
    private static List<int>? lobHeads;

    private static HeapTable FindTable(Database database, int objectId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.ObjectId == objectId)
                    return table;
            }
        }
        throw new InvalidOperationException($"the log names table {objectId}, which the restored database doesn't hold.");
    }

    /// <summary>Rewrites one table's rows: each change's prior image out, its new image in.</summary>
    private static void Replay(HeapTable table, List<LoggedRowChange> changes)
    {
        var columns = table.StoredColumns;
        var rows = new List<byte[]?>(table.Heap.RowCount + changes.Count);
        var positions = new Dictionary<RowImage, Stack<int>>();
        foreach (var bytes in table.Heap.EnumerateRows())
            Add(Image(table, bytes));
        foreach (var change in changes)
        {
            if (change.Before is { } before)
            {
                if (!positions.TryGetValue(new(before), out var held) || held.Count == 0)
                    throw new InvalidOperationException($"the log removes a row of [{table.Name}] the restored database doesn't hold.");
                rows[held.Pop()] = null;
            }
            if (change.After is { } after)
                Add(after);
        }

        DatabaseImage.ClearRows(table);
        var values = new SqlValue[columns.Length];
        byte[]? buffer = null;
        foreach (var row in rows)
        {
            if (row is null)
                continue;
            if (row[0] == 0)
            {
                _ = table.Heap.Insert(row.AsSpan(1));
                continue;
            }
            var offset = 1;
            BackupRowCodec.ReadRow(columns, row, ref offset, values);
            var length = RowEncoder.EncodeRowInto(columns, values, table.Heap, ref buffer);
            _ = table.Heap.Insert(buffer.AsSpan(0, length));
        }

        void Add(byte[] image)
        {
            if (!positions.TryGetValue(new(image), out var held))
                positions[new(image)] = held = new();
            held.Push(rows.Count);
            rows.Add(image);
        }
    }

    /// <summary>A row image compared by its bytes, the key rows alike in every column share.</summary>
    private readonly struct RowImage(byte[] bytes) : IEquatable<RowImage>
    {
        private readonly byte[] bytes = bytes;

        public bool Equals(RowImage other) => this.bytes.AsSpan().SequenceEqual(other.bytes);

        public override bool Equals(object? obj) => obj is RowImage other && this.Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(this.bytes);
            return hash.ToHashCode();
        }
    }
}
