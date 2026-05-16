using System.IO.Compression;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Top-level loader for a <c>.bacpac</c> archive. Opens the OPC zip,
/// dispatches <c>model.xml</c> to <see cref="ModelXmlReader"/>, then loads
/// the <c>Data/&lt;schema&gt;.&lt;table&gt;/TableData-NNN-NNNNN.BCP</c>
/// entries.
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
    /// The stream must be seekable (<see cref="ZipArchive"/> requirement);
    /// callers reading from a network source should buffer to a
    /// <see cref="MemoryStream"/> first.
    /// </summary>
    public static void Load(Stream stream, Simulation simulation, BacpacLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(result);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var modelEntry = archive.GetEntry("model.xml")
            ?? throw new InvalidDataException("bacpac: model.xml entry not found.");
        using (var modelStream = modelEntry.Open())
            ModelXmlReader.Apply(modelStream, simulation, result);

        // Data load pass — iterate every Data/<schema>.<table>/TableData-NNN-NNNNN.BCP
        // entry, resolve the destination HeapTable, and stream-decode rows
        // into the simulator's storage via Heap.Insert (bypasses SQL parsing
        // so the AW 760K-row payload loads in tolerable time).
        var database = simulation.Databases[Simulation.DefaultDatabaseName];
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("Data/", StringComparison.Ordinal)
                || !entry.FullName.EndsWith(".BCP", StringComparison.Ordinal))
            {
                continue;
            }

            _ = result.ElementCounts.TryGetValue("_DataFile", out var current);
            result.ElementCounts["_DataFile"] = current + 1;

            var (schemaName, tableName) = ParseDataFolderName(entry.FullName);
            if (!database.Schemas.TryGetValue(schemaName, out var schema)
                || !schema.HeapTables.TryGetValue(tableName, out var table))
            {
                result.Skipped.Add(new BacpacSkipped("_DataFile", entry.FullName,
                    $"Destination table {schemaName}.{tableName} not found — likely failed to load during DDL phase."));
                continue;
            }

            try
            {
                using var rawStream = entry.Open();
                using var bcpStream = new BufferedStream(rawStream, 64 * 1024);
                LoadRowsFromBcp(bcpStream, table, schemaName, tableName, result);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException
                or EndOfStreamException or ArgumentOutOfRangeException
                or ArgumentException or OverflowException or FormatException
                or IndexOutOfRangeException)
            {
                // Best-effort: a single malformed row aborts that file but
                // doesn't kill the whole load. Each per-file failure lands on
                // Skipped with the exception type + message so the caller has
                // a precise inventory.
                result.Skipped.Add(new BacpacSkipped("_DataFile", entry.FullName,
                    $"BCP decode failed ({ex.GetType().Name}): {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Walks the per-row BCP stream and pokes each row directly into the
    /// destination heap. Bypasses the simulator's INSERT machinery — no
    /// IDENTITY allocation (bacpac data carries the original identity
    /// values), no FK enforcement, no triggers (data is already consistent
    /// from the source DB).
    /// </summary>
    private static void LoadRowsFromBcp(BufferedStream bcpStream, HeapTable table, string schemaName, string tableName, BacpacLoadResult result)
    {
        // Look up the per-column alias map populated during model.xml table
        // emission. Key uses bracketed [schema].[table] form to match the
        // DACFx name shape; default to empty if missing.
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

        _ = result.ElementCounts.TryGetValue("_DataRows", out var rowsSoFar);
        result.ElementCounts["_DataRows"] = rowsSoFar + rowCount;
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
