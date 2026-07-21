using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// In-process connection-string authentication: <c>User ID</c> / <c>Password</c>
/// validate against the <c>CREATE LOGIN</c> registry, and the session principal
/// is stamped to the login's mapped database user in the target database
/// (<c>Initial Catalog</c> / <c>Database</c>). An empty registry accepts any
/// credentials; no <c>User ID</c> keeps the default dbo identity.
/// </summary>
[TestClass]
public sealed class ConnectionStringAuthTests
{
    private static SimulatedDbConnection Authenticate(Simulation simulation, string connectionString)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = connectionString;
        connection.Open();
        return connection;
    }

    private static string Identity(SimulatedDbConnection connection) =>
        (string)connection.CreateCommand("select current_user + '|' + system_user + '|' + original_login()").ExecuteScalar()!;

    [TestMethod]
    public void EmptyRegistry_AcceptsAnyCredentials_PermissiveDbo()
    {
        using var connection = Authenticate(new Simulation(), "User ID=whoever;Password=whatever");
        AreEqual("dbo|whoever|whoever", Identity(connection));
    }

    [TestMethod]
    public void CorrectPassword_Connects()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'S3cret!Pass'");
        using var connection = Authenticate(simulation, "User ID=app;Password=S3cret!Pass");
        AreEqual("dbo|app|app", Identity(connection));
    }

    [TestMethod]
    public void WrongPassword_Raises18456()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'S3cret!Pass'");
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=app;Password=wrong";
        var ex = Throws<SimulatedSqlException>(connection.Open);
        AreEqual(18456, ex.Number);
        AreEqual("Login failed for user 'app'.", ex.Message);
    }

    [TestMethod]
    public void MappedUser_BecomesCurrentUser()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'; create user mapped for login app");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word");
        AreEqual("mapped|app|app", Identity(connection));
    }

    [TestMethod]
    public void MappedLogin_ToMaster_LandsAsGuest()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'; create user mapped for login app");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word;Initial Catalog=master");
        AreEqual("master", connection.CreateCommand("select db_name()").ExecuteScalar());
        AreEqual("guest|app|app", Identity(connection));
    }

    [TestMethod]
    public void MappedLogin_ToInaccessibleDatabase_Raises4060()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'; create user mapped for login app");
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=app;Password=P@ss1word;Initial Catalog=msdb";
        var ex = Throws<SimulatedSqlException>(connection.Open);
        AreEqual(4060, ex.Number);
    }

    [TestMethod]
    public void MissingDatabase_Raises4060()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.ConnectionString = "User ID=sa;Password=anything;Initial Catalog=no_such_db";
        var ex = Throws<SimulatedSqlException>(connection.Open);
        AreEqual(4060, ex.Number);
        Contains("no_such_db", ex.Message);
    }

    [TestMethod]
    public void Sa_ToMaster_IsDbo()
    {
        using var connection = Authenticate(new Simulation(), "User ID=sa;Password=anything;Database=master");
        AreEqual("master", connection.CreateCommand("select db_name()").ExecuteScalar());
        AreEqual("dbo|sa|sa", Identity(connection));
    }

    [TestMethod]
    public void RestrictedPrincipal_ChangeDatabase_Raises916()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'; create user mapped for login app");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word");
        var ex = Throws<SimulatedSqlException>(() => connection.ChangeDatabase("master"));
        AreEqual(916, ex.Number);
    }
}
