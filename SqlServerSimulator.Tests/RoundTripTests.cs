using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

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

    [TestMethod]
    public void InsertMultipleRowsAndColumns()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        Assert.AreEqual(3, connection.CreateCommand("insert t values ( 1, 10 ), ( 2, 20 ), ( 3, 30 )").ExecuteNonQuery());

        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();

        IsTrue(reader.Read());
        Assert.AreEqual(1, reader[0]);
        Assert.AreEqual(10, reader[1]);

        IsTrue(reader.Read());
        Assert.AreEqual(2, reader[0]);
        Assert.AreEqual(20, reader[1]);

        IsTrue(reader.Read());
        Assert.AreEqual(3, reader[0]);
        Assert.AreEqual(30, reader[1]);

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsertPartialColumnList_LeavesUnspecifiedColumnsNull()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( a int, b int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t ( a ) values ( 7 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        IsTrue(reader.Read());
        Assert.AreEqual(7, reader[0]);
        Assert.AreEqual(DBNull.Value, reader[1]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsertAllSupportedColumnTypes_ViaParameters()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( b bit, ti tinyint, si smallint, i int )").ExecuteNonQuery();

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "insert t values ( @b, @ti, @si, @i )";
            AddTypedParameter(insert, "b", DbType.Boolean, true);
            AddTypedParameter(insert, "ti", DbType.Byte, (byte)200);
            AddTypedParameter(insert, "si", DbType.Int16, (short)-1);
            AddTypedParameter(insert, "i", DbType.Int32, 12345);
            Assert.AreEqual(1, insert.ExecuteNonQuery());
        }

        using var reader = connection.CreateCommand("select b, ti, si, i from t").ExecuteReader();
        IsTrue(reader.Read());
        Assert.AreEqual(true, reader[0]);
        Assert.AreEqual((byte)200, reader[1]);
        Assert.AreEqual((short)-1, reader[2]);
        Assert.AreEqual(12345, reader[3]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsertManyRows_SpansMultiplePages()
    {
        // 4-byte int + row overhead is small, so a single 8KB page holds many
        // hundred rows. Picking a row count that comfortably exceeds one page's
        // capacity guarantees the heap allocates a second page; the SELECT must
        // still return them in insertion order.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();

        const int rowCount = 1500;
        for (var i = 0; i < rowCount; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "insert t values ( @p )";
            AddTypedParameter(insert, "p", DbType.Int32, i);
            Assert.AreEqual(1, insert.ExecuteNonQuery());
        }

        using var reader = connection.CreateCommand("select v from t").ExecuteReader();
        for (var i = 0; i < rowCount; i++)
        {
            IsTrue(reader.Read(), $"Reader exhausted before row {i}.");
            Assert.AreEqual(i, reader[0]);
        }
        IsFalse(reader.Read());
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
