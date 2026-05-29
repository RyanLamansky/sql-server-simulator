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

    [TestMethod]
    public void DatabaseReflectsCurrentDatabaseAndRoundTripsThroughChangeDatabase()
    {
        using var connection = new Simulation().CreateDbConnection();
        var current = connection.Database;
        Assert.IsFalse(string.IsNullOrEmpty(current));

        connection.ChangeDatabase(current);
        Assert.AreEqual(current, connection.Database);
    }

    [TestMethod]
    public void ChangeDatabaseToMissingRaisesMsg911()
    {
        using var connection = new Simulation().CreateDbConnection();
        var exception = Assert.ThrowsExactly<SimulatedSqlException>(() => connection.ChangeDatabase("no_such_database"));
        Assert.AreEqual(911, exception.Number);
    }

    [TestMethod]
    public void ChangeDatabaseToWhitespaceRaisesArgumentException()
    {
        using var connection = new Simulation().CreateDbConnection();
        _ = Assert.ThrowsExactly<ArgumentException>(() => connection.ChangeDatabase("   "));
    }

    [TestMethod]
    public void ConnectionStringSetterIsNotSupported()
    {
        using var connection = new Simulation().CreateDbConnection();
        _ = Assert.ThrowsExactly<NotSupportedException>(() => connection.ConnectionString = "anything");
    }
}
