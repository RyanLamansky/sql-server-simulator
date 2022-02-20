using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.Common;

namespace SqlServerSimulator;

[TestClass]
public class SelectTests
{
    [TestMethod]
    public void Select1ViaExecuteScalar()
        => Assert.AreEqual(1, new Simulation().ExecuteScalar("select 1"));

    private static DbDataReader ScalarViaReaderTest(string commandText)
    {
        var reader = new Simulation().ExecuteReader(commandText);
        Assert.IsTrue(reader.Read());
        return reader;
    }

    [TestMethod]
    public void Select1ViaExecuteReaderIndexer()
        => Assert.AreEqual(1, ScalarViaReaderTest("select 1")[0]);

    [TestMethod]
    public void Select1ViaExecuteReaderGetInt32()
        => Assert.AreEqual(1, ScalarViaReaderTest("select 1").GetInt32(0));

    private static void VersionTest(string commandText)
    {
        var simulation = new Simulation();
        var version = simulation.Version;
        Assert.IsNotNull(version);

        Assert.AreEqual(version, simulation.ExecuteScalar(commandText));
    }

    [TestMethod]
    public void SelectVersion() => VersionTest("SELECT @@VERSION");

    [TestMethod]
    public void SelectVersion_LowerCase() => VersionTest("select @@version");

    [TestMethod]
    public void SelectVersion_MixedCase() => VersionTest("Select @@Version");

    [TestMethod]
    public void SelectParameterValue()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("select @p0", createParameter =>
            {
                var parm = createParameter();
                parm.ParameterName = "p0";
                parm.Value = 5;
            })
            .ExecuteScalar();

        Assert.AreEqual(5, result);
    }

    [TestMethod]
    public void SelectParameterValueWithAtInName()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("select @p0", createParameter =>
            {
                var parm = createParameter();
                parm.ParameterName = "@p0";
                parm.Value = 6;
            })
            .ExecuteScalar();

        Assert.AreEqual(6, result);
    }

    [TestMethod]
    public void Select1Plus1()
    {
        var result = new Simulation().ExecuteScalar("select 1 + 1");

        Assert.AreEqual(2, result);
    }

    [TestMethod]
    public void SelectAliasedExpression()
    {
        using var reader = new Simulation().ExecuteReader("select 1 as c");

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("c", reader.GetName(0));
        Assert.AreEqual(1, reader.GetInt32(0));
    }
}
