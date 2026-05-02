using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class WhereTests
{
    [TestMethod]
    [DataRow("1 = 0", 0)]
    [DataRow("1 = 1", 1)]
    [DataRow("1 > 0", 1)]
    [DataRow("1 > 1", 0)]
    [DataRow("1 >= 0", 1)]
    [DataRow("1 >= 1", 1)]
    [DataRow("1 >= 2", 0)]
    [DataRow("1 > = 0", 1)]
    [DataRow("1 > = 1", 1)]
    [DataRow("1 > = 2", 0)]
    [DataRow("0 < 1", 1)]
    [DataRow("1 < 1", 0)]
    [DataRow("1 <= 0", 0)]
    [DataRow("1 <= 1", 1)]
    [DataRow("1 <= 2", 1)]
    [DataRow("1 < = 0", 0)]
    [DataRow("1 < = 1", 1)]
    [DataRow("1 < = 2", 1)]
    [DataRow("1 <> 0", 1)]
    [DataRow("1 <> 1", 0)]
    [DataRow("1 < > 0", 1)]
    [DataRow("1 < > 1", 0)]
    [DataRow("1 != 0", 1)]
    [DataRow("1 != 1", 0)]
    [DataRow("1 ! = 0", 1)]
    [DataRow("1 ! = 1", 0)]
    [DataRow("1 !> 0", 0)]
    [DataRow("1 !> 1", 1)]
    [DataRow("1 !> 2", 1)]
    [DataRow("1 ! > 0", 0)]
    [DataRow("1 ! > 1", 1)]
    [DataRow("1 ! > 2", 1)]
    [DataRow("1 !< 0", 1)]
    [DataRow("1 !< 1", 1)]
    [DataRow("1 !< 2", 0)]
    [DataRow("1 ! < 0", 1)]
    [DataRow("1 ! < 1", 1)]
    [DataRow("1 ! < 2", 0)]
    public void PureExpressionFilter(string whereExpression, int expectedCount)
    {
        AreEqual(expectedCount, new Simulation().ExecuteReader($"select 1 where {whereExpression}").EnumerateRecords().Count());
    }

    [TestMethod]
    public void TablelessWhere_NullOperand_ReturnsZeroRows()
        => AreEqual(0, new Simulation().ExecuteReader("select 1 where null = 1").EnumerateRecords().Count());

    [TestMethod]
    public void FromTableWhere_FiltersByEqualityToLiteral()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int, v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1, 100 ), ( 2, 200 ), ( 3, 300 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id, v from t where id = 2").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        AreEqual(200, reader[1]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByGreaterThan()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 ), ( 4 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t where v > 2").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsTrue(reader.Read()); AreEqual(4, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_NullColumnNeverMatches()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int, v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t ( id ) values ( 1 )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 2, 99 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id from t where v = 99").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByParameter()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 )").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where id = @id";
        AddTypedParameter(select, "id", DbType.Int32, 2);

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    private static void AddTypedParameter(System.Data.Common.DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
