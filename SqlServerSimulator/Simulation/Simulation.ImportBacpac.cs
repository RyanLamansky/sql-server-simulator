using SqlServerSimulator.Storage.Bacpac;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Imports a BACPAC file as a new database in this <see cref="Simulation"/>.
    /// When <paramref name="options"/> is <see langword="null"/> (or its
    /// <see cref="BacpacImportOptions.DatabaseName"/> is unset) the target
    /// name defaults to <c>Path.GetFileNameWithoutExtension(path)</c>.
    /// A <see cref="InvalidOperationException"/> fires when the resolved
    /// database name already exists in this simulation — bacpac import is a
    /// create-only operation, matching DACFx's contract.
    /// </summary>
    /// <remarks>
    /// The "bacpac" name follows the <c>Bitmap</c> / <c>Sitemap</c> compound-word
    /// convention rather than treating "bacpac" as an acronym (DACFx itself
    /// uses <c>BacPackage</c>, expanding the abbreviation rather than coining
    /// <c>BacPac</c>).
    /// </remarks>
    public void ImportBacpac(string path, out BacpacImportResult result, BacpacImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new BacpacImportOptions();
        var databaseName = options.DatabaseName ?? Path.GetFileNameWithoutExtension(path);
        using var stream = File.OpenRead(path);
        ImportBacpacCore(stream, databaseName, options.MaxDegreeOfParallelism, out result);
    }

    /// <summary>
    /// Imports a BACPAC stream as a new database in this <see cref="Simulation"/>.
    /// When <paramref name="options"/> is <see langword="null"/> (or its
    /// <see cref="BacpacImportOptions.DatabaseName"/> is unset) the target
    /// name defaults to <c>"simulated"</c> — the streaming overload has no
    /// filename to derive from. A <see cref="InvalidOperationException"/>
    /// fires when the resolved database name already exists in this
    /// simulation.
    /// </summary>
    public void ImportBacpac(Stream stream, out BacpacImportResult result, BacpacImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new BacpacImportOptions();
        var databaseName = options.DatabaseName ?? DefaultDatabaseName;
        ImportBacpacCore(stream, databaseName, options.MaxDegreeOfParallelism, out result);
    }

    private void ImportBacpacCore(Stream stream, string databaseName, int maxDegreeOfParallelism, out BacpacImportResult result)
    {
        if (Databases.ContainsKey(databaseName))
            throw new InvalidOperationException($"A database named '{databaseName}' already exists in this Simulation. Import is a create-only operation; choose a different name via BacpacImportOptions.DatabaseName.");
        result = new BacpacImportResult();
        var database = new Database(databaseName);
        Databases.Add(databaseName, database);
        BacpacReader.Load(stream, this, database, maxDegreeOfParallelism, result);
    }
}
