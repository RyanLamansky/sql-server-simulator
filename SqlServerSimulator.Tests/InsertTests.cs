using System.Data;
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

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoTinyInt_NarrowsInRange()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 200 )").ExecuteNonQuery();

        Assert.AreEqual((byte)200, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoSmallInt_NarrowsInRange()
    {
        // Note: INSERT VALUES doesn't recognize a unary minus token, so we use a
        // positive literal here. Negative literals via parameters work fine.
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v smallint )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 12345 )").ExecuteNonQuery();

        Assert.AreEqual((short)12345, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32LiteralIntoTinyInt_OverflowRaisesSqlException()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();
        var insert = connection.CreateCommand("insert t values ( 300 )");
        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        StringAssert.Contains(ex.Message, "Arithmetic overflow");
        StringAssert.Contains(ex.Message, "tinyint");
    }

    [TestMethod]
    public void InsertCoercion_TinyIntParameterIntoInt32Column_Widens()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "insert t values ( @p )";
            AddTypedParameter(insert, "p", DbType.Byte, (byte)200);
            Assert.AreEqual(1, insert.ExecuteNonQuery());
        }

        Assert.AreEqual(200, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void InsertCoercion_Int32ParameterIntoTinyIntColumn_OverflowRaisesSqlException()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v tinyint )").ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        AddTypedParameter(insert, "p", DbType.Int32, 300);

        var ex = Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
        StringAssert.Contains(ex.Message, "Arithmetic overflow");
    }

    private static void AddTypedParameter(DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
