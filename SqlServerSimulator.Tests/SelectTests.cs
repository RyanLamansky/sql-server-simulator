using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class SelectTests
{
    [TestMethod]
    public void Select1ViaExecuteScalar()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1"));

    private static DbDataReader ScalarViaReaderTest(string commandText)
    {
        var reader = new Simulation().ExecuteReader(commandText);
        IsTrue(reader.Read());
        return reader;
    }

    [TestMethod]
    public void Select1ViaExecuteReaderIndexer()
        => AreEqual(1, ScalarViaReaderTest("select 1")[0]);

    [TestMethod]
    public void Select1ViaExecuteReaderGetInt32()
        => AreEqual(1, ScalarViaReaderTest("select 1").GetInt32(0));

    private static void VersionTest(string commandText)
    {
        var simulation = new Simulation();
        var version = simulation.Version;
        IsNotNull(version);

        AreEqual(version, simulation.ExecuteScalar(commandText));
    }

    [TestMethod]
    public void SelectVersion() => VersionTest("SELECT @@VERSION");

    [TestMethod]
    public void SelectVersion_LowerCase() => VersionTest("select @@version");

    [TestMethod]
    public void SelectVersion_MixedCase() => VersionTest("Select @@Version");
}
