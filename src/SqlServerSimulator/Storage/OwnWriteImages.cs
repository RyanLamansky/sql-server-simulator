namespace SqlServerSimulator.Storage;

/// <summary>
/// The rows a statement waiting on its client reads in place of what another
/// request of its own transaction wrote while it waited: each row's image as
/// the statement found it, or its absence for a row inserted since. Real
/// versions such a write, so the suspended statement goes on reading the table
/// as its transaction left it when the statement began, while another session's
/// committed write ahead of it reads as written (probed 2026-10-09 against SQL
/// Server 2025 at <c>READ COMMITTED</c>, <c>REPEATABLE READ</c>,
/// <c>SERIALIZABLE</c> and under <c>NOLOCK</c>: a row the other request
/// updated ahead reads as before, one it deleted is read, one it inserted
/// isn't, a key it moved reads at its old key, and the reader keeps the locks
/// of the rows it produced, nothing more).
/// </summary>
/// <remarks>
/// The transaction's <see cref="UndoLog"/> notes each visible row write here
/// before it lands (<see cref="UndoLog.NoteVisibleWrite"/>), the first per
/// address winning, and forgets the writes a rollback to a save point or a
/// failed statement takes back (<see cref="Forget"/>), whose rows are their
/// noted images again. A statement registers once it first waits on its client
/// inside a transaction (<see cref="ResultStream.Suspend"/>), and its reads
/// consult it only while it produces rows
/// (<see cref="SimulatedDbConnection.ActiveOwnWriteImages"/>).
/// </remarks>
internal sealed class OwnWriteImages
{
    private readonly Dictionary<Heap, Dictionary<(int Page, int Slot), NotedImage>> heaps = [];

    private readonly Lock gate = new();

    /// <summary>A row's image when the statement began — null for a row it didn't find — and where in the log its first write since began.</summary>
    private readonly struct NotedImage(byte[]? image, int position)
    {
        public readonly byte[]? Image = image;
        public readonly int Position = position;
    }

    /// <summary>
    /// Notes <paramref name="image"/> as the row at <paramref name="address"/>
    /// of <paramref name="heap"/> as the statement found it, unless an earlier
    /// write since the statement began already noted the row;
    /// <paramref name="position"/> is the log's length before this write.
    /// </summary>
    public void Note(Heap heap, (int Page, int Slot) address, byte[]? image, int position)
    {
        lock (this.gate)
        {
            if (!this.heaps.TryGetValue(heap, out var rows))
                this.heaps[heap] = rows = [];
            _ = rows.TryAdd(address, new NotedImage(image, position));
        }
    }

    /// <summary>Forgets the writes from log position <paramref name="position"/> on, which a rollback took back.</summary>
    public void Forget(int position)
    {
        lock (this.gate)
        {
            foreach (var (_, rows) in this.heaps)
            {
                List<(int, int)>? undone = null;
                foreach (var (address, noted) in rows)
                {
                    if (noted.Position >= position)
                        (undone ??= []).Add(address);
                }
                if (undone is not null)
                {
                    foreach (var address in undone)
                        _ = rows.Remove(address);
                }
            }
        }
    }

    /// <summary>Whether a write since the statement began reached a row of <paramref name="heap"/>.</summary>
    public bool Covers(Heap heap)
    {
        lock (this.gate)
            return this.heaps.TryGetValue(heap, out var rows) && rows.Count != 0;
    }

    /// <summary>
    /// Whether a write since the statement began reached the row at
    /// <paramref name="address"/>, with <paramref name="image"/> the row as the
    /// statement found it — null for a row it didn't find.
    /// </summary>
    public bool TryGet(Heap heap, (int Page, int Slot) address, out byte[]? image)
    {
        lock (this.gate)
        {
            if (this.heaps.TryGetValue(heap, out var rows) && rows.TryGetValue(address, out var noted))
            {
                image = noted.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    /// <summary>The rows of <paramref name="heap"/> the statement found that a write since reached, with their images then.</summary>
    public List<((int Page, int Slot) Address, byte[] Image)> Found(Heap heap)
    {
        List<((int Page, int Slot), byte[])> found = [];
        lock (this.gate)
        {
            if (this.heaps.TryGetValue(heap, out var rows))
            {
                foreach (var (address, noted) in rows)
                {
                    if (noted.Image is { } image)
                        found.Add((address, image));
                }
            }
        }
        return found;
    }
}
