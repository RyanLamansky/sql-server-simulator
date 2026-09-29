using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>GRANT</c> / <c>DENY</c> / <c>REVOKE</c> on the <c>TYPE::</c>,
/// <c>XML SCHEMA COLLECTION::</c> and <c>FULLTEXT CATALOG::</c> securable
/// classes, and the CONTROL they carry as the alternative each DROP accepts
/// (probed 2026-09-29 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class DerivedClassGrantTests
{
    private const string Collection = "create xml schema collection dbo.xsc as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"a\" type=\"xs:int\"/></xs:schema>'";

    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create type dbo.myint from int",
            "create type dbo.tt as table (c int)",
            Collection,
            "create fulltext catalog fc");
        return sim;
    }

    [TestMethod]
    [DataRow("type::dbo.myint", "TYPE", "drop type dbo.myint")]
    [DataRow("type::dbo.tt", "TYPE", "drop type dbo.tt")]
    [DataRow("xml schema collection::dbo.xsc", "XML_SCHEMA_COLLECTION", "drop xml schema collection dbo.xsc")]
    [DataRow("fulltext catalog::fc", "FULLTEXT_CATALOG", "drop fulltext catalog fc")]
    public void ControlOnTheSecurable_AdmitsItsDrop(string securable, string classDesc, string drop)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery($"grant control on {securable} to u");
        AreEqual(classDesc, sim.ExecuteScalar("select class_desc from sys.database_permissions where permission_name = 'CONTROL' and class in (6, 10, 23)"));
        _ = sim.ExecuteNonQuery($"execute as user = 'u'; {drop}");
        // A dropped securable takes its grants with it.
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_permissions where class in (6, 10, 23)"));
    }

    [TestMethod]
    [DataRow("drop type dbo.myint", 218)]
    [DataRow("drop xml schema collection dbo.xsc", 15151)]
    [DataRow("drop fulltext catalog fc", 7641)]
    public void WithoutAGrant_TheDropIsRefused(string drop, int number)
        => _ = Seeded().AssertSqlError($"execute as user = 'u'; {drop}", number);

    /// <summary>A lesser permission on the securable doesn't admit a DROP, and DENY removes what CONTROL gave.</summary>
    [TestMethod]
    public void OnlyControlAdmitsTheDrop()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant references, view definition on type::dbo.myint to u");
        _ = sim.AssertSqlError("execute as user = 'u'; drop type dbo.myint", 218);
        _ = sim.ExecuteNonQuery("grant control on type::dbo.myint to u; deny control on type::dbo.myint to u");
        _ = sim.AssertSqlError("execute as user = 'u'; drop type dbo.myint", 218);
    }

    [TestMethod]
    [DataRow("alter on type::dbo.myint", "ALTER")]
    [DataRow("select on type::dbo.myint", "SELECT")]
    [DataRow("create table on type::dbo.myint", "CREATE TABLE")]
    [DataRow("execute on fulltext catalog::fc", "EXECUTE")]
    [DataRow("insert on xml schema collection::dbo.xsc", "INSERT")]
    public void APermissionTheClassLacks_IsASyntaxError(string grant, string near)
    {
        var ex = Seeded().AssertSqlError($"grant {grant} to u", 102);
        AreEqual($"Incorrect syntax near '{near}'.", ex.Errors[0].Message);
    }

    [TestMethod]
    [DataRow("grant alter on xml schema collection::dbo.xsc to u")]
    [DataRow("grant alter on fulltext catalog::fc to u")]
    [DataRow("grant execute on type::dbo.myint to u")]
    [DataRow("grant take ownership, view definition, references on fulltext catalog::fc to u")]
    [DataRow("grant control on type::myint to u")]
    public void APermissionTheClassHas_IsStored(string grant)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(grant);
        IsGreaterThan(0, sim.ExecuteScalar<int>("select count(*) from sys.database_permissions where class in (6, 10, 23)"));
    }

    [TestMethod]
    [DataRow("grant control on type::nosuch.myint to u", "type 'myint'")]
    [DataRow("grant control on type::dbo.nosuch to u", "type 'nosuch'")]
    [DataRow("grant control on type::sys.int to u", "object 'int'")]
    [DataRow("grant control on xml schema collection::nosuch to u", "xml schema collection 'nosuch'")]
    [DataRow("grant control on fulltext catalog::nosuch to u", "fulltext catalog 'nosuch'")]
    [DataRow("grant control on fulltext catalog::dbo.fc to u", "fulltext catalog 'dbo.fc'")]
    public void AMissingSecurable_IsMsg15151(string grant, string named)
    {
        var ex = Seeded().AssertSqlError(grant, 15151);
        Contains($"Cannot find the {named}, because it does not exist", ex.Errors[0].Message);
    }

    /// <summary>Revoking a grantable permission without CASCADE is Msg 4611 whether or not it was delegated (probed 2026-09-29).</summary>
    [TestMethod]
    [DataRow("grant control on type::dbo.myint to u with grant option", "revoke control on type::dbo.myint from u")]
    [DataRow("grant select on dbo.tbl to u with grant option", "revoke select on dbo.tbl from u")]
    [DataRow("grant select on dbo.tbl to u with grant option", "revoke grant option for select on dbo.tbl from u")]
    [DataRow("grant select (a) on dbo.tbl to u with grant option", "revoke select (a) on dbo.tbl from u")]
    public void RevokingAGrantableRow_WithoutCascade_Raises4611(string grant, string revoke)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table dbo.tbl (a int)");
        _ = sim.ExecuteNonQuery(grant);
        _ = sim.AssertSqlError(revoke, 4611);
        _ = sim.ExecuteNonQuery($"{revoke} cascade");
    }

    /// <summary>
    /// <c>TAKE OWNERSHIP</c> on the securable admits <c>ALTER AUTHORIZATION</c>
    /// (the caller also needs to be the new owner or hold IMPERSONATE on it),
    /// and a change of effective owner drops the securable's grants (probed
    /// 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("type::dbo.myint", "select principal_id from sys.types where name = 'myint'")]
    [DataRow("xml schema collection::dbo.xsc", "select principal_id from sys.xml_schema_collections where name = 'xsc'")]
    [DataRow("fulltext catalog::fc", "select principal_id from sys.fulltext_catalogs where name = 'fc'")]
    public void TakeOwnershipOnTheSecurable_AdmitsAnOwnerChange(string securable, string ownerQuery)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user w without login");
        _ = sim.AssertSqlError($"execute as user = 'u'; alter authorization on {securable} to w", 15151);
        _ = sim.ExecuteNonQuery($"grant take ownership on {securable} to u; grant impersonate on user::w to u");
        _ = sim.ExecuteNonQuery($"execute as user = 'u'; alter authorization on {securable} to w");
        AreEqual(sim.ExecuteScalar<int>("select cast(user_id('w') as int)"), sim.ExecuteScalar<int>(ownerQuery));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_permissions where class in (6, 10, 23)"));
    }
}
