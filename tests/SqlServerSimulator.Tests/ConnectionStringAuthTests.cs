using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// In-process connection-string authentication: <c>User ID</c> / <c>Password</c>
/// validate against the <c>CREATE LOGIN</c> registry, and the session principal
/// is stamped to the login's database user in the target database
/// (<c>Initial Catalog</c> / <c>Database</c>) via the faithful login→user
/// mapping — sysadmin→dbo, <c>FOR LOGIN</c>→that user, guest where accessible,
/// else a Msg 4060 connect refusal. An empty registry is the open dev mode
/// (any credentials → dbo); no <c>User ID</c> keeps the default dbo identity.
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
    public void SysadminLogin_ConnectsAsDbo()
    {
        // A validated login that is a sysadmin member maps to dbo everywhere,
        // overriding any FOR LOGIN mapping.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create login app with password = 'S3cret!Pass';
            alter server role sysadmin add member app
            """);
        using var connection = Authenticate(simulation, "User ID=app;Password=S3cret!Pass");
        AreEqual("dbo|app|app", Identity(connection));
    }

    [TestMethod]
    public void UnmappedLogin_ToUserDatabase_Refused4060()
    {
        // A validated login with no FOR LOGIN user, connecting to a user
        // database where guest is inaccessible, is refused — no dbo fallback.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login solo with password = 'S3cret!Pass'");
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=solo;Password=S3cret!Pass";
        var ex = Throws<SimulatedSqlException>(connection.Open);
        AreEqual(4060, ex.Number);
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
    public void UnmappedLogin_ToMaster_LandsAsGuest()
    {
        // guest is accessible in master, so an unmapped login runs as guest.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word;Initial Catalog=master");
        AreEqual("master", connection.CreateCommand("select db_name()").ExecuteScalar());
        AreEqual("guest|app|app", Identity(connection));
    }

    [TestMethod]
    public void UnmappedLogin_ToMsdb_LandsAsGuest()
    {
        // guest is accessible in msdb too (aligned with HAS_DBACCESS).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word;Initial Catalog=msdb");
        AreEqual("guest|app|app", Identity(connection));
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
    public void RestrictedPrincipal_ChangeDatabase_ToUnreachableDatabase_Raises916()
    {
        // A login's rights are per database: it may switch to one it maps into
        // and is refused by one it doesn't. `other` has no user for the login
        // and guest is inaccessible in a user database.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'; create user mapped for login app; create database other");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word");
        var ex = Throws<SimulatedSqlException>(() => connection.ChangeDatabase("other"));
        AreEqual(916, ex.Number);
        AreEqual("simulated", connection.CreateCommand("select db_name()").ExecuteScalar());
    }

    [TestMethod]
    public void RestrictedPrincipal_ChangeDatabase_ToGuestAccessibleDatabase_RebindsPrincipal()
    {
        // guest is accessible in master, so the switch stands and the session's
        // database user follows it (probe-confirmed) — the login and
        // ORIGINAL_LOGIN() stay put.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login app with password = 'P@ss1word'; create user mapped for login app");
        using var connection = Authenticate(simulation, "User ID=app;Password=P@ss1word");
        AreEqual("mapped|app|app", Identity(connection));
        connection.ChangeDatabase("master");
        AreEqual("guest|app|app", Identity(connection));
    }
}
