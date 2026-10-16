namespace SqlServerSimulator.Storage.Backup;

/// <summary>
/// What a database's backups have left it with: the full backup a
/// differential builds on, the log backup the next one continues from, and
/// the log position each backup's LSNs are drawn from. A log position is a
/// block of the active virtual log file; <see cref="Lsn"/> spells it as real's
/// <c>numeric(25, 0)</c> LSNs are spelled.
/// </summary>
/// <remarks>
/// Real moves these as SQL Server 2025 was probed moving them (2026-10-16): a
/// full backup that isn't <c>COPY_ONLY</c> becomes the differential base and,
/// outside <c>SIMPLE</c> recovery, starts the log chain when none runs; a
/// differential starts a chain likewise; a log backup that isn't
/// <c>COPY_ONLY</c> moves the chain to its last LSN; and switching to
/// <c>SIMPLE</c> recovery breaks the chain, which only a full or
/// differential backup starts again.
/// </remarks>
internal sealed class DatabaseBackupChain
{
    /// <summary>The log position the last backup drew, which the next draws past.</summary>
    public long LogBlock = 40;

    /// <summary>The checkpoint LSN block of the full backup a differential builds on, null before the first.</summary>
    public long? DifferentialBaseBlock;

    /// <summary>The differential base's backup set GUID.</summary>
    public Guid? DifferentialBaseGuid;

    /// <summary>When the differential base's backup began.</summary>
    public DateTime? DifferentialBaseUtc;

    /// <summary>The LSN block the next log backup starts from, null while no log chain runs.</summary>
    public long? LastLogBackupBlock;

    /// <summary>When the last log backup that moved the chain finished, null before the first.</summary>
    public DateTime? LastLogBackupUtc;

    /// <summary>Whether a log backup has continued the running chain, so the next doesn't begin it.</summary>
    public bool LogChainContinued;

    /// <summary>The checkpoint LSN block the last backup took, which a log backup reports as its checkpoint.</summary>
    public long LastCheckpointBlock;

    /// <summary>
    /// Each table's heap generation as the differential base was taken, by
    /// object id, which the extents changed since are counted against.
    /// </summary>
    public Dictionary<int, long> BaseGenerations = [];

    /// <summary>
    /// The pages of changed extents since the differential base,
    /// <c>sys.dm_db_file_space_usage.modified_extent_page_count</c>: none
    /// before a base exists, then the 72 of real's own catalog extents a base
    /// leaves marked, and for each table written or created since, its
    /// pages in whole extents and one extent of allocation pages (probed
    /// 2026-10-16 against SQL Server 2025: 72 right after a full backup, 88
    /// once a row went into a one-page table).
    /// </summary>
    public long ModifiedExtentPages(IEnumerable<HeapTable> tables)
    {
        if (this.DifferentialBaseBlock is null)
            return 0;
        long pages = 72;
        foreach (var table in tables)
        {
            var generation = Volatile.Read(ref table.Heap.MutationGeneration);
            if (this.BaseGenerations.TryGetValue(table.ObjectId, out var atBase) && atBase == generation)
                continue;
            pages += ((table.Heap.Pages.Count + table.Heap.LobPages.Count + 7) / 8 * 8) + 8;
        }
        return pages;
    }

    /// <summary>Marks every table as it stands as the differential base's.</summary>
    public void TakeBaseGenerations(List<HeapTable> tables)
    {
        var generations = new Dictionary<int, long>();
        foreach (var table in tables)
            generations[table.ObjectId] = Volatile.Read(ref table.Heap.MutationGeneration);
        this.BaseGenerations = generations;
    }

    /// <summary>
    /// Draws the log position a backup starts at, past both the last one and
    /// the server's transaction counter, so a database written to since its
    /// last backup reports a later LSN.
    /// </summary>
    public long Draw(long transactionCounter, int gap)
    {
        var start = (Math.Max(this.LogBlock, transactionCounter * 8) + gap) / 8 * 8;
        this.LogBlock = start;
        return start;
    }

    /// <summary>A log position as real's <c>numeric(25, 0)</c> LSN: the virtual log file, the block and slot 1.</summary>
    public static decimal Lsn(long block) => (Simulation.ActiveLogSequence * 1_000_000_000_000_000m) + (block * 100_000m) + 1;

    /// <summary>A log position as the log DMVs spell an LSN, <c>vlf:block:slot</c> in hex.</summary>
    public static string HexLsn(long block) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Simulation.ActiveLogSequence:X8}:{block:X8}:0001");

    /// <summary>Forgets the log chain, as switching to <c>SIMPLE</c> recovery does.</summary>
    public void BreakLogChain()
    {
        this.LastLogBackupBlock = null;
        this.LastLogBackupUtc = null;
        this.LogChainContinued = false;
    }
}
