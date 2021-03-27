using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data;
using System.Threading.Tasks;

namespace SqlServerSimulator
{
    [TestClass]
    public class ConnectionTests
    {
        [TestMethod]
        public void OpenCloseSync()
        {
            using var connnection = new Simulation().CreateDbConnection();

            Assert.AreEqual(ConnectionState.Closed, connnection.State);
            connnection.Open();
            Assert.AreEqual(ConnectionState.Open, connnection.State);
            connnection.Close();
            Assert.AreEqual(ConnectionState.Closed, connnection.State);
        }

        [TestMethod]
        public async Task OpenCloseAsync()
        {
            using var connnection = new Simulation().CreateDbConnection();

            Assert.AreEqual(ConnectionState.Closed, connnection.State);
            await connnection.OpenAsync();
            Assert.AreEqual(ConnectionState.Open, connnection.State);
            await connnection.CloseAsync();
            Assert.AreEqual(ConnectionState.Closed, connnection.State);
        }

        [TestMethod]
        public async Task OpenAsyncCancellable()
        {
            using var connnection = new Simulation().CreateDbConnection();

            Assert.AreEqual(ConnectionState.Closed, connnection.State);
            await connnection.OpenAsync(default);
            Assert.AreEqual(ConnectionState.Open, connnection.State);
        }
    }
}
