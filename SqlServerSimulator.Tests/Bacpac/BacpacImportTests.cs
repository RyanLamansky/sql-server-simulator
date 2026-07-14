using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Bacpac;

/// <summary>
/// Covers <c>Simulation.ImportBacpac</c>: multiple bacpacs into a single
/// <see cref="Simulation"/>, the <see cref="BacpacImportOptions.DatabaseName"/>
/// override, the <see cref="BacpacImportOptions.MaxDegreeOfParallelism"/>
/// override, and the create-only collision rule.
/// </summary>
[TestClass]
public class BacpacImportTests
{
    [TestMethod]
    public void ImportBacpac_NamedDatabase_LandsInThatName()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1).Row(2))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "audit" });

        // No lazy "simulated" seed sneaks in alongside — Simulation's ctor
        // starts Databases empty; the connection only materializes the
        // default seed when there's nothing else to point at.
        IsTrue(SimulationHasDatabase(sim, "audit"));
        IsFalse(SimulationHasDatabase(sim, "simulated"));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM T"));
    }

    [TestMethod]
    public void ImportBacpac_DefaultStreamName_AfterDbUse_CollidesWithLazySeed()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();
        // Opening a connection on a fresh Simulation lazily creates the
        // default "simulated" database. A subsequent ImportBacpac without
        // a name override therefore collides — the user has signaled the
        // default DB is in use.
        var sim = new Simulation();
        using (sim.CreateDbConnection()) { /* trigger lazy seed */ }
        var ex = Throws<InvalidOperationException>(() => sim.ImportBacpac(bacpac, out _));
        Contains("'simulated'", ex.Message);
    }

    [TestMethod]
    public void ImportBacpac_StreamDefaultName_OnFreshSimulation_LandsAsSimulated()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1).Row(2).Row(3))
            .Build();
        // Fresh Simulation has no databases (lazy-seed only fires on first
        // connection). The stream overload's default "simulated" name is
        // therefore free to land cleanly.
        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out _);
        IsTrue(SimulationHasDatabase(sim, "simulated"));
        AreEqual(3, sim.ExecuteScalar("SELECT COUNT(*) FROM T"));
    }

    [TestMethod]
    public void ImportBacpac_MultipleBacpacs_AllLandAsSeparateDatabases()
    {
        using var first = BacpacBuilder.Create()
            .Table("dbo", "Customer", t => t.Column("Id", "int").Row(1).Row(2).Row(3))
            .Build();
        using var second = BacpacBuilder.Create()
            .Table("dbo", "Order_", t => t.Column("Id", "int").Row(10).Row(20))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(first, out _, new BacpacImportOptions { DatabaseName = "sales" });
        sim.ImportBacpac(second, out _, new BacpacImportOptions { DatabaseName = "ops" });

        // sys.databases is instance-scoped — both imported names appear.
        // No lazy "simulated" seed: the connection's fallback when no
        // "simulated" is present picks the alphabetically-first existing
        // database ("ops" here) without creating an extra DB.
        IsTrue(SimulationHasDatabase(sim, "sales"));
        IsTrue(SimulationHasDatabase(sim, "ops"));
        IsFalse(SimulationHasDatabase(sim, "simulated"));
        // Six rows: the four always-present system databases (master / tempdb
        // / model / msdb) plus the two imported user databases.
        AreEqual(6, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.databases"));
        // Both imported databases are queryable via cross-database 3-part
        // names (CrossDatabaseTests covers USE + joins + DML rejection in
        // depth; here we just confirm the per-database row count surfaces).
        AreEqual(3, sim.ExecuteScalar("SELECT COUNT(*) FROM sales.dbo.Customer"));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM ops.dbo.Order_"));
    }

    [TestMethod]
    public void ImportBacpac_NameCollision_Throws()
    {
        using var first = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();
        using var second = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(2))
            .Build();

        var sim = new Simulation();
        sim.ImportBacpac(first, out _, new BacpacImportOptions { DatabaseName = "x" });
        var ex = Throws<InvalidOperationException>(() =>
            sim.ImportBacpac(second, out _, new BacpacImportOptions { DatabaseName = "x" }));
        Contains("'x'", ex.Message);
        Contains("already exists", ex.Message);
    }

    [TestMethod]
    public void ImportBacpac_PathOverload_DerivesDatabaseNameFromFilename()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();
        var tempPath = Path.Combine(Path.GetTempPath(), $"bacpac-named-{Guid.NewGuid():N}.bacpac");
        try
        {
            using (var file = File.Create(tempPath))
                bacpac.CopyTo(file);
            var sim = new Simulation();
            sim.ImportBacpac(tempPath, out _);
            // The expected database name matches Path.GetFileNameWithoutExtension(tempPath).
            var expected = Path.GetFileNameWithoutExtension(tempPath);
            IsTrue(SimulationHasDatabase(sim, expected));
            // Default connection on a single-DB simulation falls back to the
            // sole entry, so unqualified table lookup works.
            AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM T"));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [TestMethod]
    public void ImportBacpac_MaxDegreeOfParallelism_OneStillRoundTrips()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1).Row(2).Row(3).Row(4))
            .Build();

        // Stream overload's default "simulated" name lands cleanly on a
        // fresh Simulation (Databases starts empty). The default connection
        // then routes to it — DOP=1 round-trip without cross-database name
        // routing.
        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out _, new BacpacImportOptions { MaxDegreeOfParallelism = 1 });
        AreEqual(4, sim.ExecuteScalar("SELECT COUNT(*) FROM T"));
    }

    /// <summary>
    /// Probes for a database by issuing a three-part-name lookup against
    /// <c>sys.databases</c>. Public-API-only — <c>Simulation.Databases</c>
    /// is internal so tests can't enumerate it directly.
    /// </summary>
    private static bool SimulationHasDatabase(Simulation sim, string name)
    {
        using var conn = sim.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @n";
        var p = cmd.CreateParameter();
        p.ParameterName = "@n";
        p.Value = name;
        _ = cmd.Parameters.Add(p);
        return (int)cmd.ExecuteScalar()! == 1;
    }
}
