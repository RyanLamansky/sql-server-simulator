using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Bacpac;

/// <summary>
/// Migrated bacpac-loader tests. Each test owns a synthetic bacpac built
/// via <see cref="BacpacBuilder"/> that exercises exactly the feature
/// its assertion measures — no shared multi-GB reference fixtures.
/// </summary>
[TestClass]
public class BacpacLoaderTests
{
    [TestMethod]
    public void NamedSchemas_LandIn_sys_schemas()
    {
        using var bacpac = BacpacBuilder.Create()
            .Schema("HumanResources")
            .Schema("Person")
            .Schema("Production")
            .Schema("Purchasing")
            .Schema("Sales")
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diagnostics);
        IsEmpty(diagnostics.Skipped);
        AreEqual(5, diagnostics.ElementCounts["SqlSchema"]);

        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('dbo','sys','INFORMATION_SCHEMA','guest','public') ORDER BY name;";
        using var reader = command.ExecuteReader();

        var schemas = new List<string>();
        while (reader.Read())
            schemas.Add(reader.GetString(0));

        HasCount(5, schemas);
        AreEqual("HumanResources", schemas[0]);
        AreEqual("Person", schemas[1]);
        AreEqual("Production", schemas[2]);
        AreEqual("Purchasing", schemas[3]);
        AreEqual("Sales", schemas[4]);
    }
}
