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
        // Phase A + B handled element types (SqlSchema, SqlDatabaseOptions,
        // SqlTable, SqlSimpleColumn) are off Skipped; everything else still
        // appears there awaiting future bundles.
        IsNotEmpty(diagnostics.Skipped);
        IsEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlTable").ToList());
        IsNotEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlExtendedProperty").ToList());
        IsNotEmpty(diagnostics.Skipped.Where(s => s.ElementType == "SqlComputedColumn").ToList());
    }

    [TestMethod]
    public void Load_AW_Tables_Land_With_Correct_Column_Counts()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // 71 user tables. Pre-existing system tables (sys.*) are filtered by
        // the schema_id range (user schemas use ids >= 5; built-in sys = 4).
        command.CommandText = "SELECT COUNT(*) FROM sys.tables;";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(71, reader.GetInt32(0));
    }

    [TestMethod]
    public void Load_AW_Production_ProductCategory_Has_Expected_Columns()
    {
        var simulation = LoadAdventureWorks(out _);
        using var connection = (SimulatedDbConnection)simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        // ProductCategory has 4 cols: ProductCategoryID int IDENTITY PK,
        // Name [dbo].[Name] NOT NULL, rowguid uniqueidentifier NOT NULL
        // ROWGUIDCOL, ModifiedDate datetime NOT NULL.
        command.CommandText = """
            SELECT c.name, t.name AS type_name, c.is_nullable, c.is_identity
              FROM sys.columns c
              JOIN sys.tables tab ON c.object_id = tab.object_id
              JOIN sys.types t ON c.user_type_id = t.user_type_id
              JOIN sys.schemas s ON tab.schema_id = s.schema_id
             WHERE s.name = 'Production' AND tab.name = 'ProductCategory'
             ORDER BY c.column_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(string Name, string TypeName, bool Nullable, bool Identity)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }
        HasCount(4, rows);
        AreEqual("ProductCategoryID", rows[0].Name);
        AreEqual("int", rows[0].TypeName);
        IsFalse(rows[0].Nullable);
        IsTrue(rows[0].Identity);
        AreEqual("Name", rows[1].Name);
        // [dbo].[Name] is an alias over nvarchar(50); user_type_id surfaces the
        // alias's allocated id (>=256) and joining to sys.types resolves the
        // alias name. NOT NULL is preserved through the column declaration.
        IsFalse(rows[1].Nullable);
        AreEqual("rowguid", rows[2].Name);
        AreEqual("uniqueidentifier", rows[2].TypeName);
        AreEqual("ModifiedDate", rows[3].Name);
        AreEqual("datetime", rows[3].TypeName);
    }
}
