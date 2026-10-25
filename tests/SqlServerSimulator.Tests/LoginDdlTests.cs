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
    public void CreateLogin_FromCertificate_NotSupported()
    {
        var ex = Throws<NotSupportedException>(
            () => new Simulation().ExecuteNonQuery("create login w from certificate c"));
        Assert.Contains("Certificate and asymmetric-key logins", ex.Message);
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
        => new Simulation().ExecuteBatches($"create login app with password = '{new string('a', 126)}A1'");

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

    // A login name matches without trailing spaces, while the login keeps the
    // name as written: real refuses a second login named as the first plus a
    // space, and resolves any spelling of it, case and trailing spaces aside
    // (probed 2026-10-25 against SQL Server 2025).
    [TestMethod]
    public void CreateLogin_NamedAsAnotherPlusATrailingSpace_Raises15025()
        => new Simulation().AssertSqlError(
            "create login bob with password = 'Zz!98765'; create login [bob ] with password = 'Zz!98766'",
            15025, "The server principal 'bob ' already exists.");

    [TestMethod]
    public void Login_NamedWithATrailingSpace_KeepsItAndResolvesWithout()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login [carol ] with password = 'Zz!98765'; alter login [CAROL] disable");
        AreEqual(12, sim.ExecuteScalar("select datalength(name) from sys.server_principals where name = 'carol'"));
        var id = sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'carol'");
        AreEqual(id, sim.ExecuteScalar("select suser_id('carol')"));
        AreEqual(id, sim.ExecuteScalar("select suser_id('Carol  ')"));
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.server_principals where name = 'carol'")!);
    }

    // A function naming a server principal finds it without its trailing
    // spaces too (probed 2026-10-25 against SQL Server 2025).
    [TestMethod]
    [DataRow("suser_id('SA  ')")]
    [DataRow("is_srvrolemember('public ')")]
    [DataRow("has_perms_by_name('sa ', 'LOGIN', 'ALTER')")]
    public void ServerPrincipalName_ResolvesWithoutItsTrailingSpaces(string call)
        => AreEqual(1, new Simulation().ExecuteScalar($"select {call}"));

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

    [TestMethod]
    public void CreateLogin_BackslashInName_Raises15006()
        => new Simulation().AssertSqlError(@"create login [zl\x] with password = 'Zc0v!Passw0rd'", 15006,
            @"'zl\x' is not a valid name because it contains invalid characters.");

    [TestMethod]
    public void CheckExpirationWithoutPolicy_Raises15122_OnCreateAndAlter()
    {
        const string message = "The CHECK_EXPIRATION option cannot be used when CHECK_POLICY is OFF.";
        var sim = new Simulation();
        sim.AssertSqlError("create login zl5 with password = 'Zc0v!Passw0rd', check_policy = off, check_expiration = on", 15122, message);
        _ = sim.ExecuteNonQuery("create login zl5 with password = 'Zc0v!Passw0rd', check_policy = off");
        sim.AssertSqlError("alter login zl5 with check_expiration = on", 15122, message);
    }

    [TestMethod]
    [DataRow("abcdefgh1")]
    [DataRow("Xzl5!9xyz")]
    public void CreateLogin_PasswordNotComplexOrHoldingTheName_Raises33064(string password)
    {
        var ex = new Simulation().AssertSqlError($"create login zl5 with password = '{password}'", 33064);
        AreEqual((byte)2, ex.State);
        StartsWith("Password validation failed. The password does not meet SQL Server password policy requirements because it is not complex enough.", ex.Errors[0].Message);
    }

    /// <summary>
    /// A login's option list takes the options its statement knows, once each,
    /// with values of their kind (probed 2026-10-21 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd' garbage garbage 123", "garbage")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', foo = on", "foo")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', 123", "123")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', default_database = 123", "123")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', default_language = 1033", "1033")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', check_policy = maybe", "check_policy")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', check_policy = off, check_policy = off", "off")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', default_database = master, default_database = master", "master")]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd', name = x", "name")]
    [DataRow("alter login zl5 with foo = 1", "1")]
    [DataRow("alter login zl5 with foo = bar", "foo")]
    [DataRow("alter login zl5 with foo", ";")]
    [DataRow("alter login zl5 with check_expiration = maybe", "check_expiration")]
    [DataRow("alter login zl5 with name = a, name = b", "b")]
    [DataRow("alter login zl5 with sid = 0x01", "sid")]
    [DataRow("alter login zl5 with password = 'Zc0v!Passw0rd' garbage", "garbage")]
    [DataRow("alter login zl5 with default_database = master garbage", "garbage")]
    [DataRow("alter login zl5 enable garbage", "garbage")]
    public void LoginOptionsOutsideTheGrammar_AreMsg102(string statement, string near)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login zl5 with password = 'Zc0v!Passw0rd', check_policy = off");
        sim.AssertSqlError(statement + ";", 102, $"Incorrect syntax near '{near}'.");
    }

    /// <summary>
    /// <c>MUST_CHANGE</c> needs <c>CHECK_EXPIRATION</c>, a clear-text password
    /// is no <c>HASHED</c> one, and the password's own modifiers take no comma
    /// (probed 2026-10-21 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd' must_change", 15099)]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd' must_change, check_expiration = off", 15099)]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd' must_change, check_expiration = on, check_policy = off", 15122)]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd' hashed", 15021)]
    [DataRow("alter login zl5 with password = 'Zc0v!Passw0rd' must_change", 15099)]
    public void PasswordModifiersRealRefuses_RaiseItsError(string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login zl5 with password = 'Zc0v!Passw0rd', check_policy = off");
        _ = sim.AssertSqlError(statement, number);
    }

    [TestMethod]
    [DataRow("create login zl6 with password = 'Zc0v!Passw0rd' must_change, check_expiration = on")]
    [DataRow("alter login zl5 with password = 'Zc0v!Passw0rd' unlock, check_policy = on")]
    [DataRow("alter login zl5 with password = 'Zc0v!Passw0rd' old_password = 'Zc0v!Passw0rd'")]
    public void PasswordModifiersRealTakes_Run(string statement)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login zl5 with password = 'Zc0v!Passw0rd', check_policy = off");
        _ = sim.ExecuteNonQuery(statement);
    }
}
