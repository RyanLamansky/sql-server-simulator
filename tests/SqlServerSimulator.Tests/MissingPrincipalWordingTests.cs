using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How a statement names a principal that isn't there, and the kinds DROP USER
/// and DROP ROLE each see (probed 2026-09-29 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class MissingPrincipalWordingTests
{
    [TestMethod]
    [DataRow("grant select on dbo.t to nosuch", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("deny select on dbo.t to nosuch", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("revoke select on dbo.t from nosuch", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("grant impersonate on user::nosuch to public", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("drop user nosuch", 15151, "Cannot drop the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("drop role nosuch", 15151, "Cannot drop the role 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("drop role u", 15151, "Cannot drop the role 'u', because it does not exist or you do not have permission.")]
    [DataRow("drop user r", 15151, "Cannot drop the user 'r', because it does not exist or you do not have permission.")]
    [DataRow("drop application role nosuch", 15151, "Cannot drop the application role 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter application role nosuch with name = z", 15151, "Cannot alter the application role 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("create user x for login nosuch", 15007, "'nosuch' is not a valid login or you do not have permission.")]
    [DataRow("create user x from login nosuch", 15007, "'nosuch' is not a valid login or you do not have permission.")]
    public void AMissingPrincipal_IsNamedAsRealNamesIt(string sql, int number, string message)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int); create user u without login; create role r");
        sim.AssertSqlError(sql, number, message);
    }

    /// <summary>A user mapped to an existing login is created, and <c>DROP … IF EXISTS</c> over another kind is quiet.</summary>
    [TestMethod]
    public void AKnownLogin_MapsAndIfExistsIsQuiet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create login lg with password = 'P@ssw0rd!12345'", "create user x for login lg", "create role r");
        _ = sim.ExecuteNonQuery("drop user if exists r; drop role if exists x");
        AreEqual(2, sim.ExecuteScalar<int>("select count(*) from sys.database_principals where name in ('x', 'r')"));
    }
}
