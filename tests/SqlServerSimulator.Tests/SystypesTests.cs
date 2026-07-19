using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class SystypesTests
{
    [TestMethod]
    public void Returns34Rows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(34, connection.CreateCommand("select name from systypes").ExecuteReader().EnumerateRecords().Count());
    }

    [TestMethod]
    public void NameColumnIsSysname()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select name from systypes").ExecuteReader();
        IsTrue(reader.Read());
        // name column carries a string value and is correctly decoded as sysname.
        _ = IsInstanceOfType<string>(reader[0]);
    }

    [TestMethod]
    public void FilterByXtypeViaParameter()
    {
        // WHERE comparison is type-strict. Filter xtype (tinyint) by a Byte
        // parameter so both operands resolve to SqlType.TinyInt and the equality
        // succeeds. xtype 56 uniquely identifies the "int" row.
        using var connection = new Simulation().CreateOpenConnection();

        using var select = connection.CreateCommand();
        select.CommandText = "select name from systypes where xtype = @x";
        var parameter = select.CreateParameter();
        parameter.ParameterName = "x";
        parameter.DbType = DbType.Byte;
        parameter.Value = (byte)56;
        _ = select.Parameters.Add(parameter);

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("int", reader[0]);
        IsFalse(reader.Read());
    }
}
