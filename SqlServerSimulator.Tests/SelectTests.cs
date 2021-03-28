using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator
{
    [TestClass]
    public class SelectTests
    {
        [TestMethod]
        public void SelectVersion()
        {
            var simulation = new Simulation();
            var version = simulation.Version;
            IsNotNull(version);

            using var connection = simulation.CreateDbConnection();
            using var command = connection.CreateCommand("SELECT @@VERSION");

            connection.Open();
            using var reader = command.ExecuteReader();

            IsTrue(reader.Read());
            AreEqual(version, reader.GetString(0));
        }
    }
}
