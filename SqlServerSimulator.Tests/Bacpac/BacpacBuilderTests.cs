using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Bacpac;

[TestClass]
public class BacpacBuilderTests
{
    [TestMethod]
    public void OneTable_TwoIntRows_RoundTrips()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Customer", t => t
                .Column("Id", "int")
                .Column("Age", "int")
                .Row(1, 30)
                .Row(2, 25))
            .Build();

        var sim = Simulation.FromBacpac(bacpac, out var diagnostics);

        AreEqual(2, sim.ExecuteScalar("select count(*) from Customer"));
        AreEqual(30, sim.ExecuteScalar("select Age from Customer where Id = 1"));
        AreEqual(25, sim.ExecuteScalar("select Age from Customer where Id = 2"));
        AreEqual(1, diagnostics.ElementCounts["SqlTable"]);
        AreEqual(2, diagnostics.ElementCounts["_DataRows"]);
        if (diagnostics.Skipped.Count > 0)
            Fail("Unexpected Skipped: " + string.Join("; ", diagnostics.Skipped.Select(s => $"{s.ElementType}/{s.ElementName}: {s.Reason}")));
    }
}
