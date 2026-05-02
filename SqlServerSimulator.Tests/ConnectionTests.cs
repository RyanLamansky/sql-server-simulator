using System.Data;

namespace SqlServerSimulator;

[TestClass]
public class ConnectionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void OpenCloseSync()
    {
        using var connection = new Simulation().CreateDbConnection();

        Assert.AreEqual(ConnectionState.Closed, connection.State);
        connection.Open();
        Assert.AreEqual(ConnectionState.Open, connection.State);
        connection.Close();
        Assert.AreEqual(ConnectionState.Closed, connection.State);
    }

    [TestMethod]
    public async Task OpenCloseAsync()
    {
        using var connection = new Simulation().CreateDbConnection();

        Assert.AreEqual(ConnectionState.Closed, connection.State);
        await connection.OpenAsync(this.TestContext.CancellationToken);
        Assert.AreEqual(ConnectionState.Open, connection.State);
        await connection.CloseAsync();
        Assert.AreEqual(ConnectionState.Closed, connection.State);
    }

    [TestMethod]
    public async Task OpenAsyncCancellable()
    {
        using var connection = new Simulation().CreateDbConnection();

        Assert.AreEqual(ConnectionState.Closed, connection.State);
        await connection.OpenAsync(this.TestContext.CancellationToken);
        Assert.AreEqual(ConnectionState.Open, connection.State);
    }
}
