using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator;

[TestClass]
public class RoundTripTests
{
    [TestMethod]
    public void InsertedDataIsSelected()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());

        command.CommandText = "insert t values ( 5 )";
        Assert.AreEqual(1, command.ExecuteNonQuery());

        command.CommandText = "select v from t";
        Assert.AreEqual(5, command.ExecuteScalar());
    }
}
