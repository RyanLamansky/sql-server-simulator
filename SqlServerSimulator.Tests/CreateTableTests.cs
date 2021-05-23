using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator
{
    [TestClass]
    public class CreateTableTests

    {
        [TestMethod]
        public void CreateTableMinimal()
        {
            var simulation = new Simulation();

            using var connection = simulation.CreateDbConnection();
            using var command = connection.CreateCommand("create table t ( v int )");

            connection.Open();
            using var reader = command.ExecuteReader(); // TODO: Switch to ExecuteNonQuery when implemented.
        }
    }
}
