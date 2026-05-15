using SqlServerSimulator.Storage.Bacpac;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Loads a SQL Server BACPAC file into a fresh <see cref="Simulation"/>.
    /// Convenience wrapper over <see cref="FromBacpac(Stream, out BacpacLoadResult)"/>
    /// that opens <paramref name="path"/> as a read-only file stream.
    /// </summary>
    /// <remarks>
    /// Internal until the loader handles AdventureWorks2025 end-to-end. The
    /// "bacpac" name follows the <c>Bitmap</c> / <c>Sitemap</c> compound-word
    /// convention rather than treating "bacpac" as an acronym (DACFx itself
    /// uses <c>BacPackage</c>, expanding the abbreviation rather than coining
    /// <c>BacPac</c>).
    /// </remarks>
    internal static Simulation FromBacpac(string path, out BacpacLoadResult diagnostics)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return FromBacpac(stream, out diagnostics);
    }

    /// <summary>
    /// Loads a SQL Server BACPAC archive from <paramref name="stream"/> into
    /// a fresh <see cref="Simulation"/>. The stream must be seekable
    /// (<see cref="System.IO.Compression.ZipArchive"/> requirement); callers
    /// reading from a network source should buffer to a
    /// <see cref="MemoryStream"/> first.
    /// </summary>
    /// <param name="stream">Source archive — read but not closed by this call.</param>
    /// <param name="diagnostics">Receives element counts + skipped-element list.</param>
    /// <returns>A new <see cref="Simulation"/> populated from the archive.</returns>
    internal static Simulation FromBacpac(Stream stream, out BacpacLoadResult diagnostics)
    {
        ArgumentNullException.ThrowIfNull(stream);
        diagnostics = new BacpacLoadResult();
        var simulation = new Simulation();
        BacpacReader.Load(stream, simulation, diagnostics);
        return simulation;
    }
}
