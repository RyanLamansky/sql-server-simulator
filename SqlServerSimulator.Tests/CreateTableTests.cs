using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator;

[TestClass]
public class CreateTableTests
{
    [TestMethod]
    public void CreateTableMinimal()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }
}
