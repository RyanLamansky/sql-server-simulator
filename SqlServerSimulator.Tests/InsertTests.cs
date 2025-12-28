using System.Data.Common;

namespace SqlServerSimulator;

[TestClass]
public class InsertTests
{
    [TestMethod]
    public void InsertRequiresTableToExist() => Assert.Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("insert t ( v ) values ( 1 )")
        .ExecuteNonQuery()
    );

    [TestMethod]
    [DataRow("t values ( 1 )", 1)]
    [DataRow("T values ( 1 )", 1)]
    [DataRow("t ( v ) values ( 1 )", 1)]
    [DataRow("t ( V ) values ( 1 )", 1)]
    [DataRow("t values ( 1 ), ( 2 )", 2)]
    public void Insert(string commandText, int expectedRecordsAffected)
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("create table t ( v int )")
            .ExecuteNonQuery();

        var result = simulation
            .CreateCommand($"insert {commandText}")
            .ExecuteNonQuery();

        Assert.AreEqual(expectedRecordsAffected, result);
    }

    [TestMethod]
    public void InsertParameterized()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p0", 1))
            .ExecuteNonQuery();

        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void InsertParameterizedNameMismatch() => Assert.Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t values ( @p0 )", ("p1", 1))
        .ExecuteNonQuery()
    );

    [TestMethod]
    public void InsertRequiresValidColumnNames() => Assert.Throws<DbException>(() => new Simulation()
        .CreateOpenConnection()
        .CreateCommand("create table t ( v int );insert t ( x ) values ( 1 )")
        .ExecuteNonQuery()
    );
}
