using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Smoke tests for <see cref="Simulation.FromBacpac(string, out BacpacLoadResult)"/>
/// against the AdventureWorks2025 reference bacpac under
/// <c>.vs/AdventureWorks2025.bacpac</c>. The file is gitignored, so each
/// test short-circuits to <see cref="Assert.Inconclusive(string)"/> when
/// the workspace doesn't have it (CI scenario).
/// </summary>
[TestClass]
public sealed class BacpacLoaderTests
{
    private static string ResolveAdventureWorksPath()
    {
        // Walk up from the test bin dir to the repo root, then into .vs/.
        // The test runner cwd is the test-project bin/Debug/net10.0/ dir.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".vs", "AdventureWorks2025.bacpac");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return string.Empty;
    }

    private static Simulation LoadAdventureWorks(out BacpacLoadResult diagnostics)
    {
        var path = ResolveAdventureWorksPath();
        if (string.IsNullOrEmpty(path))
        {
            Inconclusive(".vs/AdventureWorks2025.bacpac not present in this workspace; skipping AW smoke test.");
        }
        return Simulation.FromBacpac(path, out diagnostics);
    }

    [TestMethod]
    public void Load_AW_Creates_All_Five_Schemas()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('dbo','sys','INFORMATION_SCHEMA','guest','public') ORDER BY name;";
        using var reader = command.ExecuteReader();

        var schemas = new List<string>();
        while (reader.Read())
            schemas.Add(reader.GetString(0));

        HasCount(5, schemas, $"expected 5 user schemas, got: {string.Join(", ", schemas)}");
        AreEqual("HumanResources", schemas[0]);
        AreEqual("Person", schemas[1]);
        AreEqual("Production", schemas[2]);
        AreEqual("Purchasing", schemas[3]);
        AreEqual("Sales", schemas[4]);
    }

    [TestMethod]
    public void Load_AW_Database_Options_Applied()
    {
        var simulation = LoadAdventureWorks(out _);
        // AW bacpac carries IsReadCommittedSnapshot=True; the loader emits
        // ALTER DATABASE [simulated] SET READ_COMMITTED_SNAPSHOT ON which
        // flips Database.ReadCommittedSnapshot.
        IsTrue(simulation.Databases["simulated"].ReadCommittedSnapshot);
    }

    [TestMethod]
    public void Load_AW_Element_Counts_Match_Probe()
    {
        _ = LoadAdventureWorks(out var diagnostics);
        AreEqual(5, diagnostics.ElementCounts["SqlSchema"]);
        AreEqual(1, diagnostics.ElementCounts["SqlDatabaseOptions"]);
        AreEqual(71, diagnostics.ElementCounts["SqlTable"]);
    }

    [TestMethod]
    public void Load_AW_Unhandled_Elements_Recorded_In_Skipped()
    {
        _ = LoadAdventureWorks(out var diagnostics);
        // Every non-Schema, non-DatabaseOptions element should currently be
        // on Skipped — they'll move off as each emitter lands.
        IsNotEmpty(diagnostics.Skipped);
        IsNotEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlTable").ToList());
        IsNotEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlExtendedProperty").ToList());
    }
}
