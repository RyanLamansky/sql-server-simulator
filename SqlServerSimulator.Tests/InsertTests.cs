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
    [DataRow("create table t ( v int );insert t values ( 1 )", 1)]
    [DataRow("create table t ( v int );insert T values ( 1 )", 1)]
    [DataRow("create table t ( v int );insert t ( v ) values ( 1 )", 1)]
    [DataRow("create table t ( v int );insert t ( V ) values ( 1 )", 1)]
    public void Insert(string commandText, int expectedRecordsAffected)
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand(commandText)
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
