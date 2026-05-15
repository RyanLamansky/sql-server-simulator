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

        // Data load deferred to a follow-up phase. The Data/ entries are
        // enumerated here so the caller sees the inventory in
        // result.ElementCounts under a synthetic "_DataFile" bucket; the
        // actual row-decoder pass lands when BcpRowReader is implemented.
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("Data/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".BCP", StringComparison.Ordinal))
            {
                _ = result.ElementCounts.TryGetValue("_DataFile", out var current);
                result.ElementCounts["_DataFile"] = current + 1;
            }
        }
    }
}
