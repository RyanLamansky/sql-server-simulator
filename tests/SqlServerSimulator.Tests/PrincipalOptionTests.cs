using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>WITH</c> lists of <c>CREATE USER</c>, <c>ALTER USER</c> and
/// <c>ALTER ROLE</c>, each statement taking its own options once, and
/// <c>ALTER USER … WITH LOGIN</c>'s remapping of a user to another login, the
/// orphaned-user repair (probed 2026-10-21 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class PrincipalOptionTests
{
    private const string Setup = """
        create login l1 with password = 'P@ssw0rd!234', check_policy = off;
        create login l2 with password = 'P@ssw0rd!234', check_policy = off;
        create user u1 for login l1;
        create user free without login;
        create role r;
        """;

    [TestMethod]
    [DataRow("create user x without login with foo = x", "Incorrect syntax near 'foo'.")]
    [DataRow("create user x for login l2 with name = x", "Incorrect syntax near 'name'.")]
    [DataRow("create user x for login l2 with default_schema = dbo, default_schema = guest", "Incorrect syntax near 'guest'.")]
    [DataRow("create user x without login with sid = 'abc'", "Incorrect syntax near 'sid'.")]
    [DataRow("alter user u1 with foo = x", "Incorrect syntax near 'foo'.")]
    [DataRow("alter user u1 with sid = 0x01", "Incorrect syntax near 'sid'.")]
    [DataRow("alter user u1 with login = 123", "Incorrect syntax near 'login'.")]
    [DataRow("alter user u1 with login = l2 garbage", "Incorrect syntax near 'garbage'.")]
    [DataRow("alter user u1 with name = a, name = b", "Incorrect syntax near 'b'.")]
    [DataRow("alter user u1 with login = l1, login = l2", "Incorrect syntax near 'l2'.")]
    [DataRow("alter role r with foo = x", "Incorrect syntax near 'foo'.")]
    [DataRow("alter role r with default_schema = dbo", "Incorrect syntax near 'default_schema'.")]
    [DataRow("alter role r with name = r2, name = r3", "Incorrect syntax near 'r3'.")]
    public void AnOptionTheStatementDoesntTake_IsMsg102(string statement, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        simulation.AssertSqlError(statement, 102, message);
    }

    [TestMethod]
    [DataRow("create user x for login l2 with password = 'P@ssw0rd!234'", 33234)]
    [DataRow("create user x without login with password = 'P@ssw0rd!234'", 33235)]
    [DataRow("create user x with password = 'P@ssw0rd!234'", 33233)]
    [DataRow("create user x for login l2 with sid = 0x01", 33242)]
    [DataRow("create user x for login l2 with default_language = english", 33234)]
    [DataRow("alter user u1 with password = 'P@ssw0rd!234'", 33234)]
    [DataRow("alter user u1 with password = 'P@ssw0rd!234' old_password = 'x'", 33234)]
    [DataRow("alter user free with login = l2", 33016)]
    [DataRow("alter user u1 with login = nobody", 15401)]
    [DataRow("create user u2 for login l2; alter user u1 with login = l2", 33018)]
    [DataRow("alter user u1 with login = sa", 15150)]
    [DataRow("alter user dbo with login = l2", 15150)]
    public void AnOptionTheUserCantTake_IsRealsError(string statement, int number)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        _ = simulation.AssertSqlError(statement, number);
    }

    [TestMethod]
    public void AlterUserWithLogin_RemapsTheUser()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        _ = simulation.ExecuteNonQuery("alter user u1 with login = l2, name = u2; alter user u2 with login = l2");
        AreEqual("u2:l2", simulation.ExecuteScalar("select concat(name, ':', suser_sname(sid)) from sys.database_principals where name = 'u2'"));
    }

    [TestMethod]
    public void AlterUserWithLogin_RepairsAnOrphanedUser()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        _ = simulation.ExecuteNonQuery("drop login l1; alter user u1 with login = l2");
        AreEqual("l2", simulation.ExecuteScalar("select suser_sname(sid) from sys.database_principals where name = 'u1'"));
    }

    [TestMethod]
    [DataRow("create user x for login l2 with allow_encrypted_value_modifications = on")]
    [DataRow("alter user u1 with allow_encrypted_value_modifications = off, default_schema = dbo")]
    public void AnOptionTheStatementTakes_Runs(string statement)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        _ = simulation.ExecuteNonQuery(statement);
    }
}
