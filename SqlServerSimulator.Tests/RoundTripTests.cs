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

    [TestMethod]
    public void InsertedDataIsSelectedWithColumnAlias()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());

        command.CommandText = "insert t values ( 5 )";
        Assert.AreEqual(1, command.ExecuteNonQuery());

        command.CommandText = "select v as c from t";

        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("c", reader.GetName(0));
        Assert.AreEqual(5, reader.GetInt32(0));
    }

    [TestMethod]
    public void InsertedDataIsSelectedWithMultiPartColumnName()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());

        command.CommandText = "insert t values ( 5 )";
        Assert.AreEqual(1, command.ExecuteNonQuery());

        command.CommandText = "select t.v as c from t";

        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("c", reader.GetName(0));
        Assert.AreEqual(5, reader.GetInt32(0));
    }
}
