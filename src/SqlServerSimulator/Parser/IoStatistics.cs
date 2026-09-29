using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// What one statement read, table by table, for the Msg 3615 lines
/// <c>SET STATISTICS IO ON</c> sends after it. Allocated per statement only
/// while the option is on (<see cref="SimulatedDbConnection.StatementIo"/>),
/// so a read path pays one null test per scan or seek when it is off.
/// </summary>
/// <remarks>
/// The figures are the simulator's own storage read honestly: a scan counts
/// one, a seek one unless it looked up a single key of a unique key or index
/// (which real counts as none), and each logical read is one heap page the
/// read entered — a scan every data page it walks, a seek the page of each
/// row it finds — so a seek that finds nothing reads none, where real reads
/// its B-tree's depth. LOB reads are the LOB-chain pages decoded.
/// Physical and read-ahead reads are 0: the simulator has no disk, so its
/// cache is always warm, and <c>DBCC DROPCLEANBUFFERS</c> leaves it so.
/// </remarks>
internal sealed class IoStatistics
{
    /// <summary>
    /// The tables touched, work tables among them, in the order the lines are
    /// sent: the most recently first-touched first, which is how real orders
    /// the tables of a nested-loop join (inner first), an <c>INSERT … SELECT</c>
    /// (target first), a foreign-key check (referenced table first), a
    /// <c>MERGE</c> (target, work table, source) and a sort or hash over what
    /// it read (work table first).
    /// </summary>
    private readonly List<IoTableCounts> tables = [];

    /// <summary>
    /// The counts of <paramref name="table"/>, which becomes one of the
    /// statement's lines; null for a table real never lists — a trigger's
    /// <c>inserted</c> / <c>deleted</c> and the system base tables behind the
    /// catalog views.
    /// </summary>
    public IoTableCounts? Touch(HeapTable table)
    {
        foreach (var counts in this.tables)
        {
            if (ReferenceEquals(counts.Table, table))
                return counts;
        }
        if (table.ReadsAsWorktable)
        {
            this.UseWorktable();
            return null;
        }
        if ((table.IsTableVariable && table.InternalName is null) || Simulation.SystemHeapTables.Values.Contains(table))
            return null;
        var added = new IoTableCounts(table.InternalName ?? table.Name, table);
        this.tables.Insert(0, added);
        return added;
    }

    /// <summary>
    /// Counts a row an <c>INSERT</c> wrote into <paramref name="table"/>: the
    /// page it landed on is one logical read, the write being no scan.
    /// </summary>
    public void CountWrite(HeapTable table) => _ = this.Touch(table)?.LogicalReads += 1;

    /// <summary>
    /// Records a sort's, a hash aggregate's or a <c>MERGE</c>'s work table,
    /// and with <paramref name="hashJoin"/> the spill file a hash join
    /// declares beside it, which lists first. Neither is a page the simulator
    /// reads, so both report no scans and no reads, as real's do for work
    /// that fits in memory.
    /// </summary>
    public void UseWorktable(bool hashJoin = false)
    {
        this.TouchWork("Worktable");
        if (hashJoin)
            this.TouchWork("Workfile");
    }

    private void TouchWork(string name)
    {
        foreach (var counts in this.tables)
        {
            if (counts.Table is null && counts.Name == name)
                return;
        }
        this.tables.Insert(0, new IoTableCounts(name, table: null));
    }

    /// <summary>Whether the statement touched nothing to report.</summary>
    public bool IsEmpty => this.tables.Count == 0;

    /// <summary>
    /// Every logical read counted so far, LOB reads included — the figure
    /// Query Store records as <c>logical_io_reads</c>.
    /// </summary>
    public long TotalLogicalReads()
    {
        long total = 0;
        foreach (var counts in this.tables)
            total += counts.LogicalReads + (counts.Table is { } table ? table.Heap.LobPagesRead - counts.LobPagesReadBefore : 0);
        return total;
    }

    /// <summary>Forgets everything recorded, once an <c>IF</c> or <c>WHILE</c> condition has reported.</summary>
    public void Clear() => this.tables.Clear();

    /// <summary>The Msg 3615 texts, one per line, in the order real sends them.</summary>
    public IEnumerable<string> Lines()
    {
        foreach (var counts in this.tables)
            yield return Line(counts.Name, counts.ScanCount, counts.LogicalReads, counts.Table is { } table ? table.Heap.LobPagesRead - counts.LobPagesReadBefore : 0);
    }

    private static string Line(string table, long scans, long reads, long lobReads) =>
        $"Table '{table}'. Scan count {scans}, logical reads {reads}, physical reads 0, page server reads 0, read-ahead reads 0, page server read-ahead reads 0, lob logical reads {lobReads}, lob physical reads 0, lob page server reads 0, lob read-ahead reads 0, lob page server read-ahead reads 0.";
}

/// <summary>
/// One table's line of an <see cref="IoStatistics"/>: a table, by its name in
/// the message (a temporary table's or table variable's internal name), or a
/// work table, which has none.
/// </summary>
internal sealed class IoTableCounts(string name, HeapTable? table)
{
    public readonly string Name = name;
    public readonly HeapTable? Table = table;
    public long ScanCount;
    public long LogicalReads;

    /// <summary>
    /// The heap's LOB-page read counter when the statement first touched the
    /// table; its growth since is the statement's LOB reads. Shared by every
    /// session reading the heap, so a concurrent reader's LOB reads land here
    /// too.
    /// </summary>
    public readonly long LobPagesReadBefore = table?.Heap.LobPagesRead ?? 0;

    /// <summary>
    /// The page a scan or seek over the table last entered: a read moving to
    /// another page counts one logical read. Starts each scan at -1.
    /// </summary>
    public void Enter(int page, ref int lastPage)
    {
        if (page != lastPage)
        {
            this.LogicalReads++;
            lastPage = page;
        }
    }
}
