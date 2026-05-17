using System.Collections.Concurrent;
using System.IO.Compression;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Top-level loader for a <c>.bacpac</c> archive. Opens the OPC zip,
/// dispatches <c>model.xml</c> to <see cref="ModelXmlReader"/>, then loads
/// the <c>Data/&lt;schema&gt;.&lt;table&gt;/TableData-NNN-NNNNN.BCP</c>
/// entries in parallel — one worker per CPU, longest-processing-table-first.
/// </summary>
/// <remarks>
/// A bacpac is a SQL Server data-tier package wrapped in OPC packaging
/// (Open Packaging Conventions — the same zip-plus-relationships format as
/// .docx / .pptx). Top-level entries observed in AdventureWorks2025:
/// <c>[Content_Types].xml</c> (OPC manifest), <c>_rels/.rels</c> (OPC
/// relationships), <c>DacMetadata.xml</c> (DACFx version stamp),
/// <c>Origin.xml</c> (per-table BCP-file inventory),
/// <c>model.xml</c> (schema definition), and per-table data folders under
/// <c>Data/</c>.
/// </remarks>
internal static class BacpacReader
{
    /// <summary>
    /// Loads <paramref name="stream"/> as a bacpac into <paramref name="simulation"/>.
    /// The input stream is drained into a read-only byte buffer up front so
    /// the data-load phase can hand each parallel worker its own
    /// <see cref="ZipArchive"/> view — <see cref="ZipArchive"/> isn't
    /// thread-safe across entries (its produced sub-streams share the
    /// archive's underlying-stream cursor), but multiple archives over the
    /// same read-only buffer are race-free because each
    /// <see cref="MemoryStream"/> wrapper holds its own cursor.
    /// </summary>
    public static void Load(Stream stream, Simulation simulation, BacpacLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(result);

        var buffer = DrainToByteArray(stream);
        var database = simulation.Databases[Simulation.DefaultDatabaseName];

        // DDL phase: serial. Uses one transient archive over the buffer.
        using (var ms = new MemoryStream(buffer, writable: false))
        using (var ddlArchive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false))
        {
            var modelEntry = ddlArchive.GetEntry("model.xml")
                ?? throw new InvalidDataException("bacpac: model.xml entry not found.");
            using var modelStream = modelEntry.Open();
            ModelXmlReader.Apply(modelStream, simulation, result);
        }

        // Build per-table work items by enumerating the archive once on the
        // main thread. This pass also owns all serial result mutations:
        // _DataFile counter increments and missing-destination Skipped
        // entries.
        var workItems = BuildTableWorkItems(buffer, database, result);
        if (workItems.Count == 0)
            return;

        // Parallel data load. Worker count is bounded by both CPU count and
        // the number of work items so we don't spin up workers that would
        // never dequeue anything. Workers pull from a concurrent queue, so
        // the LPT-sorted (longest-first) work-item list naturally produces
        // optimal scheduling: the heaviest table starts at t=0 and runs
        // alongside everything else.
        var queue = new ConcurrentQueue<TableWorkItem>(workItems);
        var workerCount = Math.Min(Environment.ProcessorCount, workItems.Count);
        var resultLock = new object();
        var tasks = new Task[workerCount];
        for (var w = 0; w < workerCount; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                using var workerStream = new MemoryStream(buffer, writable: false);
                using var workerArchive = new ZipArchive(workerStream, ZipArchiveMode.Read, leaveOpen: false);
                while (queue.TryDequeue(out var work))
                    ProcessTable(workerArchive, work, result, resultLock);
            });
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// Drains <paramref name="stream"/> into a byte buffer. When the stream
    /// is seekable, sizes the buffer exactly from <see cref="Stream.Length"/>;
    /// otherwise grows a <see cref="MemoryStream"/> and snapshots its array.
    /// </summary>
    private static byte[] DrainToByteArray(Stream stream)
    {
        if (stream.CanSeek)
        {
            var remaining = checked((int)(stream.Length - stream.Position));
            var buf = new byte[remaining];
            stream.ReadExactly(buf);
            return buf;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// One per-table unit of parallel work. Each worker iterates
    /// <see cref="EntryNames"/> in declared order (DACFx names its BCP shards
    /// <c>TableData-NNN-NNNNN.BCP</c>, which sort lexicographically), opens
    /// each entry against the worker's own archive, decodes rows, and inserts
    /// into <see cref="Table"/>'s heap. Per-table ownership means
    /// <see cref="Heap.Insert"/> never needs a lock — only one worker ever
    /// touches a given heap.
    /// </summary>
    private sealed class TableWorkItem(string schemaName, string tableName, HeapTable table)
    {
        public readonly string SchemaName = schemaName;
        public readonly string TableName = tableName;
        public readonly HeapTable Table = table;
        public readonly List<string> EntryNames = [];
        public long TotalCompressedSize;
    }

    /// <summary>
    /// Walks the archive once, groups BCP entries by destination table,
    /// updates the <c>_DataFile</c> counter, and logs Skipped entries for
    /// data targeting tables that didn't materialize in the DDL phase.
    /// Returns the list sorted descending by total compressed size so a
    /// dynamic-pull worker queue gets longest-processing-time-first
    /// scheduling for free (the heaviest table starts at t=0).
    /// </summary>
    private static List<TableWorkItem> BuildTableWorkItems(byte[] buffer, Database database, BacpacLoadResult result)
    {
        var items = new Dictionary<string, TableWorkItem>(StringComparer.OrdinalIgnoreCase);
        using var ms = new MemoryStream(buffer, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("Data/", StringComparison.Ordinal)
                || !entry.FullName.EndsWith(".BCP", StringComparison.Ordinal))
            {
                continue;
            }

            result.IncrementElementCount("_DataFile");

            var (schemaName, tableName) = ParseDataFolderName(entry.FullName);
            if (!database.Schemas.TryGetValue(schemaName, out var schema)
                || !schema.HeapTables.TryGetValue(tableName, out var table))
            {
                result.AddSkipped(new BacpacSkipped("_DataFile", entry.FullName,
                    $"Destination table {schemaName}.{tableName} not found — likely failed to load during DDL phase."));
                continue;
            }

            var key = schemaName + "." + tableName;
            if (!items.TryGetValue(key, out var item))
            {
                item = new TableWorkItem(schemaName, tableName, table);
                items[key] = item;
            }
            item.EntryNames.Add(entry.FullName);
            item.TotalCompressedSize += entry.CompressedLength;
        }

        var ordered = items.Values.ToList();
        foreach (var item in ordered)
            item.EntryNames.Sort(StringComparer.Ordinal);
        ordered.Sort((a, b) => b.TotalCompressedSize.CompareTo(a.TotalCompressedSize));
        return ordered;
    }

    /// <summary>
    /// Iterates a single table's BCP shards in declared order, decoding into
    /// <see cref="TableWorkItem.Table"/>. Per-decode-failure entries land on
    /// <see cref="BacpacLoadResult.Skipped"/> under <paramref name="resultLock"/>;
    /// the aggregate row count for the table updates the <c>_DataRows</c>
    /// counter once at the end (one lock acquire per table instead of per
    /// entry).
    /// </summary>
    private static void ProcessTable(ZipArchive archive, TableWorkItem work, BacpacLoadResult result, object resultLock)
    {
        var tableRowCount = 0;
        foreach (var entryName in work.EntryNames)
        {
            var entry = archive.GetEntry(entryName);
            if (entry is null)
                continue;

            try
            {
                using var rawStream = entry.Open();
                using var bcpStream = new BufferedStream(rawStream, 64 * 1024);
                tableRowCount += LoadRowsFromBcp(bcpStream, work.Table, work.SchemaName, work.TableName, result);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException
                or EndOfStreamException or ArgumentOutOfRangeException
                or ArgumentException or OverflowException or FormatException
                or IndexOutOfRangeException)
            {
                lock (resultLock)
                {
                    result.AddSkipped(new BacpacSkipped("_DataFile", entryName,
                        $"BCP decode failed ({ex.GetType().Name}): {ex.Message}"));
                }
            }
        }

        if (tableRowCount > 0)
        {
            lock (resultLock)
            {
                result.AddToElementCount("_DataRows", tableRowCount);
            }
        }
    }

    /// <summary>
    /// Walks the per-row BCP stream and pokes each row directly into the
    /// destination heap. Bypasses the simulator's INSERT machinery — no
    /// IDENTITY allocation (bacpac data carries the original identity
    /// values), no FK enforcement, no triggers (data is already consistent
    /// from the source DB). Returns the number of rows successfully
    /// inserted; the caller aggregates per-table to update the
    /// <c>_DataRows</c> counter under the result lock.
    /// </summary>
    private static int LoadRowsFromBcp(BufferedStream bcpStream, HeapTable table, string schemaName, string tableName, BacpacLoadResult result)
    {
        var qualifiedKey = $"[{schemaName}].[{tableName}]";
        var columnIsAlias = result.TableColumnIsAlias.TryGetValue(qualifiedKey, out var flags)
            ? flags
            : new bool[table.Columns.Length];

        // BCP files don't carry data for computed columns — neither real bcp.exe
        // nor DACFx export them. Filter computed columns out of the per-row
        // wire layout (read N stored values, encode an N-column row); the
        // simulator's row-storage and read paths already treat computed
        // columns as metadata-only (recomputed on read), so an encoded row
        // missing the computed-column slot is the same shape any INSERT
        // through normal DML would produce.
        var storedColumns = new List<HeapColumn>(table.Columns.Length);
        var storedIsAlias = new List<bool>(table.Columns.Length);
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (table.Columns[i].Computed is not null)
                continue;
            storedColumns.Add(table.Columns[i]);
            storedIsAlias.Add(i < columnIsAlias.Length && columnIsAlias[i]);
        }
        var storedCols = storedColumns.ToArray();
        var storedAlias = storedIsAlias.ToArray();

        var rowCount = 0;
        while (true)
        {
            var values = BcpRowReader.TryReadRow(bcpStream, storedCols, storedAlias);
            if (values is null)
                break;
            var rowBytes = RowEncoder.EncodeRow(storedCols, values, table.Heap);
            _ = table.Heap.Insert(rowBytes);
            rowCount++;
        }

        return rowCount;
    }

    /// <summary>
    /// Splits <c>Data/&lt;schema&gt;.&lt;table&gt;/TableData-NNN-NNNNN.BCP</c>
    /// into <c>(schema, table)</c>. The folder name is always two bracketed
    /// segments separated by a single dot — sub-schema dots aren't a concern
    /// here since SQL Server schemas can't contain dots in their names.
    /// </summary>
    private static (string Schema, string Table) ParseDataFolderName(string entryFullName)
    {
        var firstSlash = entryFullName.IndexOf('/', StringComparison.Ordinal);
        var secondSlash = entryFullName.IndexOf('/', firstSlash + 1);
        var folder = entryFullName[(firstSlash + 1)..secondSlash];
        var dot = folder.IndexOf('.', StringComparison.Ordinal);
        return (folder[..dot], folder[(dot + 1)..]);
    }
}
