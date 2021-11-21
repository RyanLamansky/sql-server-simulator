using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator;

[TestClass]
public class InsertTests
{
    [TestMethod]
    [Ignore("Insert not yet sufficiently implemented.")]
    public void InsertWithColumnNames()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int );insert t ( v ) values ( 1 )");

        connection.Open();
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }
}
