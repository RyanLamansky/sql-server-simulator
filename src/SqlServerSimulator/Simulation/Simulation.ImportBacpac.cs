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
    /// create-only operation, matching DACFx's contract. A package that can't
    /// load at all throws — <see cref="InvalidDataException"/> for an archive
    /// that doesn't read or holds no <c>model.xml</c>,
    /// <see cref="System.Xml.XmlException"/> for a model that doesn't parse —
    /// and leaves no database behind.
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
    /// simulation, and a package that can't load at all throws as the path
    /// overload's does, leaving no database behind.
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
        using var culture = CultureScope.Engine();
        if (Databases.ContainsKey(databaseName))
            throw new InvalidOperationException($"A database named '{databaseName}' already exists in this Simulation. Import is a create-only operation; choose a different name via BacpacImportOptions.DatabaseName.");
        result = new BacpacImportResult();
        var database = new Database(databaseName, this.ServerCollation);
        // Registers under lock and assigns a stored database_id (smallest free
        // id ≥ 5), the single-source-of-truth id DatabasesWithIds surfaces.
        RegisterUserDatabase(database);
        try
        {
            BacpacReader.Load(stream, this, database, maxDegreeOfParallelism, result);
        }
        catch
        {
            // A package that doesn't load — a zip that doesn't read, a model
            // that doesn't parse — leaves no database behind, so its name is
            // free for another try.
            lock (this.Databases)
                _ = this.Databases.TryRemove(KeyValuePair.Create(databaseName, database));
            this.NoteDatabaseAdmissionChanged();
            this.CatalogRows.Invalidate();
            throw;
        }
        finally
        {
            // The new database adds schema not visible to any plan parsed
            // before this call; bump so a stale cache entry parsed under the
            // prior version misses lookup and re-parses.
            BumpSchemaVersion();
        }
    }
}
