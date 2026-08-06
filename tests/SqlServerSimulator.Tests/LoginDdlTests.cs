using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the server-scope login DDL
/// (<c>CREATE LOGIN</c> / <c>ALTER LOGIN</c> / <c>DROP LOGIN</c>) and the
/// <c>LOGINPROPERTY</c> wiring that reads the resulting registry. The registry
/// (<c>Simulation.Logins</c>) is enforced only by the TDS network endpoint —
/// covered by the SqlClient oracle, not here; these tests exercise the parse /
/// registry-maintenance / error-shape surface reachable through plain ADO.NET.
/// </summary>
[TestClass]
public sealed class LoginDdlTests
{
    [TestMethod]
    public void CreateThenDropLogin_Succeeds()
        => new Simulation().ExecuteBatches(
            "create login app with password = 'Xy!12345'",
            "drop login app");

    [TestMethod]
    public void CreateLogin_Duplicate_Raises15025()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'Xy!12345'");
        var ex = sim.AssertSqlError("create login app with password = 'Zz!98765'", 15025);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("The server principal 'app' already exists.", ex.Message);
    }

    [TestMethod]
    public void AlterLogin_Missing_Raises15151_AlterWording()
    {
        var ex = new Simulation().AssertSqlError("alter login nosuch with password = 'Xy!12345'", 15151);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("Cannot alter the login 'nosuch', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropLogin_Missing_Raises15151_DropWording()
    {
        var ex = new Simulation().AssertSqlError("drop login nosuch", 15151);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("Cannot drop the login 'nosuch', because it does not exist or you do not have permission.", ex.Message);
    }

    // Real SQL Server has no IF EXISTS clause on DROP LOGIN — probe-confirmed
    // Msg 156, the keyword-flavored syntax error, near 'IF'.
    [TestMethod]
    public void DropLogin_IfExists_Raises156()
        => new Simulation().AssertSqlError("drop login if exists app", 156, "Incorrect syntax near the keyword 'if'.");

    [TestMethod]
    public void CreateLogin_FromWindows_NotSupported()
    {
        var ex = Throws<NotSupportedException>(
            () => new Simulation().ExecuteNonQuery("create login w from windows"));
        Assert.Contains("SQL-authentication", ex.Message);
    }

    [TestMethod]
    public void CreateLogin_HashedPassword_NotSupported()
    {
        var ex = Throws<NotSupportedException>(
            () => new Simulation().ExecuteNonQuery("create login h with password = 0x1234 hashed"));
        Assert.Contains("hashed-password", ex.Message);
    }

    // Real documents the 128-char password cap; its exact CREATE LOGIN
    // rejection shape is unverifiable from the reference instance (the probe
    // login hits the Msg 15247 permission wall first), so the simulator
    // reuses the password-machinery Msg 6607 that PWDENCRYPT's cap raises.
    [TestMethod]
    public void CreateLogin_PasswordOver128Chars_Raises6607()
    {
        var password = new string('a', 129);
        _ = new Simulation().AssertSqlError($"create login app with password = '{password}'", 6607);
    }

    [TestMethod]
    public void AlterLogin_PasswordOver128Chars_Raises6607()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'Xy!12345'");
        var password = new string('a', 129);
        _ = sim.AssertSqlError($"alter login app with password = '{password}'", 6607);
    }

    [TestMethod]
    public void CreateLogin_128CharPassword_Succeeds()
        => new Simulation().ExecuteBatches($"create login app with password = '{new string('a', 128)}'");

    [TestMethod]
    public void CreateLogin_WithOptionTail_Parses()
        => new Simulation().ExecuteBatches(
            "create login t with password = 'Xy!12345', check_policy = off, check_expiration = off, default_database = master");

    [TestMethod]
    public void AlterLogin_Disable_ExistingLogin_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login t with password = 'Xy!12345'");
        _ = sim.ExecuteNonQuery("alter login t disable");
    }

    [TestMethod]
    public void AlterLogin_WithDefaultDatabase_ExistingLogin_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login t with password = 'Xy!12345'");
        _ = sim.ExecuteNonQuery("alter login t with default_database = master");
    }

    [TestMethod]
    public void AlterLogin_Disable_Missing_Raises15151_AlterWording()
        => new Simulation().AssertSqlError(
            "alter login nosuch disable",
            15151,
            "Cannot alter the login 'nosuch', because it does not exist or you do not have permission.");

    [TestMethod]
    public void LoginProperty_IsLocked_CreatedLogin_ReturnsZero()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login t with password = 'Xy!12345'");
        AreEqual(0, sim.ExecuteScalar("select loginproperty('t', 'IsLocked')"));
    }

    [TestMethod]
    public void LoginProperty_IsLocked_MissingLogin_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select loginproperty('nosuch_xyz', 'IsLocked')"));

    [TestMethod]
    public void LoginProperty_PasswordLastSetTime_CreatedLogin_NonNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login t with password = 'Xy!12345'");
        var result = sim.ExecuteScalar("select loginproperty('t', 'PasswordLastSetTime')");
        Assert.IsNotNull(result);
        AreNotEqual(DBNull.Value, result);
    }

    [TestMethod]
    public void CreateLogin_InSkippedBranch_DoesNotCreate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("if 1 = 0 create login skipme with password = 'Xy!12345'");
        // Never created, so the follow-up ALTER hits the missing-login path.
        _ = sim.AssertSqlError("alter login skipme with password = 'Zz!98765'", 15151);
    }

    [TestMethod]
    public void CreateLogin_RegistryIsCaseInsensitive_Raises15025()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login App with password = 'Xy!12345'");
        _ = sim.AssertSqlError("create login APP with password = 'Zz!98765'", 15025);
    }

    // === The built-in server principals ===
    //
    // `sa` is a fixed login the catalog views synthesize rather than a registry
    // entry — the registry has to stay empty in a simulation nobody created a
    // login in, because the TDS endpoint reads an empty one as "accept any
    // credentials". So the DDL resolves it by name, the way EXECUTE AS and the
    // GRANT family already do. Real accepts ALTER LOGIN [sa] (probe-confirmed
    // with DEFAULT_LANGUAGE against SQL Server 2025).

    [TestMethod]
    public void AlterLogin_Sa_Resolves()
    {
        var sim = new Simulation();
        // Would have been Msg 15151 while only the registry was consulted, even
        // though sys.sql_logins listed it the whole time.
        _ = sim.ExecuteNonQuery("alter login [sa] with default_language = [us_english]");
        AreEqual("sa", sim.ExecuteScalar("select name from sys.sql_logins where name = 'sa'"));
    }

    [TestMethod]
    public void AlterLogin_Sa_WithPassword_IsNotModeled()
    {
        var sim = new Simulation();
        // Recording it would mean adding sa to the registry, which flips the
        // endpoint from accepting any credentials to enforcing them.
        _ = ThrowsExactly<NotSupportedException>(
            () => sim.ExecuteNonQuery("alter login [sa] with password = 'Xy!12345'"));
    }

    [TestMethod]
    public void AlterLogin_MissingLogin_StillRaises15151()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("alter login [no_such_login] with default_language = [us_english]", 15151);
    }

    [TestMethod]
    [DataRow("sa")]
    [DataRow("public")]
    [DataRow("sysadmin")]
    public void CreateLogin_CollidingWithABuiltInServerPrincipal_Raises15025(string name)
    {
        // The collision is against every server principal, not just a
        // previously created login — otherwise the catalog views would project
        // two rows for the same name.
        var sim = new Simulation();
        _ = sim.AssertSqlError($"create login [{name}] with password = 'Xy!12345'", 15025);
    }
}
