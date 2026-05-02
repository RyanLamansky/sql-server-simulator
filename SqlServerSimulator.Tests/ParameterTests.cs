using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ParameterTests
{
    [TestMethod]
    [DataRow(DbType.Boolean, true)]
    [DataRow(DbType.Boolean, false)]
    [DataRow(DbType.Byte, (byte)0)]
    [DataRow(DbType.Byte, (byte)200)]
    [DataRow(DbType.Int16, (short)-1)]
    [DataRow(DbType.Int16, (short)32000)]
    [DataRow(DbType.AnsiString, "ansi")]
    [DataRow(DbType.String, "unicodeé")]
    public void TypedParameter_RoundTrips(DbType dbType, object value)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select @p";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "p";
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);

        AreEqual(value, command.ExecuteScalar());
    }

    [TestMethod]
    [DataRow(DbType.Boolean)]
    [DataRow(DbType.Byte)]
    [DataRow(DbType.Int16)]
    [DataRow(DbType.AnsiString)]
    [DataRow(DbType.String)]
    public void TypedNullParameter_ReturnsDBNull(DbType dbType)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select @p";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "p";
        parameter.DbType = dbType;
        parameter.Value = null;
        _ = command.Parameters.Add(parameter);

        AreEqual(DBNull.Value, command.ExecuteScalar());
    }

    [TestMethod]
    public void ParameterArithmetic()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("select @p0 + 1", ("p0", 41))
            .ExecuteScalar();

        AreEqual(42, result);
    }
}
