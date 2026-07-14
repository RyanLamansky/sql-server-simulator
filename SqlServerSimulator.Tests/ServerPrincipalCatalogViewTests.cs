using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>sys.server_principals</c> / <c>sys.sql_logins</c>
/// catalog views, projected over the per-Simulation login registry
/// (<c>CREATE</c> / <c>ALTER</c> / <c>DROP LOGIN</c>) plus the synthetic fixed
/// <c>sa</c> (principal_id 1) and <c>public</c> (principal_id 2) rows.
/// </summary>
[TestClass]
public sealed class ServerPrincipalCatalogViewTests
{
    [TestMethod]
    public void FreshSimulation_HasOnlySaAndPublic()
    {
        var sim = new Simulation();
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.server_principals"));

        AreEqual("sa", sim.ExecuteScalar("select name from sys.server_principals where principal_id = 1"));
        AreEqual("SQL_LOGIN", sim.ExecuteScalar("select type_desc from sys.server_principals where principal_id = 1"));
        IsFalse((bool)sim.ExecuteScalar("select is_fixed_role from sys.server_principals where name = 'sa'")!);
        AreEqual("01", Convert.ToHexString((byte[])sim.ExecuteScalar("select sid from sys.server_principals where name = 'sa'")!));
        IsTrue(sim.ExecuteScalar("select owning_principal_id from sys.server_principals where name = 'sa'") is DBNull);

        AreEqual("public", sim.ExecuteScalar("select name from sys.server_principals where principal_id = 2"));
        AreEqual("SERVER_ROLE", sim.ExecuteScalar("select type_desc from sys.server_principals where principal_id = 2"));
        IsFalse((bool)sim.ExecuteScalar("select is_fixed_role from sys.server_principals where name = 'public'")!);
        AreEqual("02", Convert.ToHexString((byte[])sim.ExecuteScalar("select sid from sys.server_principals where name = 'public'")!));
        AreEqual(1, sim.ExecuteScalar("select owning_principal_id from sys.server_principals where name = 'public'"));
    }

    [TestMethod]
    public void CreateLogin_AddsRowWithPrincipalIdThree()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app_login with password = 'P@ssw0rd1'");
        AreEqual(3, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'app_login'"));
        AreEqual("SQL_LOGIN", sim.ExecuteScalar("select type_desc from sys.server_principals where name = 'app_login'"));
        AreEqual("master", sim.ExecuteScalar("select default_database_name from sys.server_principals where name = 'app_login'"));
        AreEqual("us_english", sim.ExecuteScalar("select default_language_name from sys.server_principals where name = 'app_login'"));
        var sid = (byte[])sim.ExecuteScalar("select sid from sys.server_principals where name = 'app_login'")!;
        HasCount(16, sid);
    }

    [TestMethod]
    public void TwoLogins_GetDistinctIdsAndSids()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login login_a with password = 'P@ssw0rd1'; create login login_b with password = 'P@ssw0rd2'");
        AreEqual(3, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'login_a'"));
        AreEqual(4, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'login_b'"));
        var sidA = Convert.ToHexString((byte[])sim.ExecuteScalar("select sid from sys.server_principals where name = 'login_a'")!);
        var sidB = Convert.ToHexString((byte[])sim.ExecuteScalar("select sid from sys.server_principals where name = 'login_b'")!);
        AreNotEqual(sidA, sidB);
    }

    [TestMethod]
    public void AlterLogin_PreservesIdAndCreateDate_AdvancesModifyDate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login rotate_login with password = 'P@ssw0rd1'");
        var idBefore = sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'rotate_login'");
        var createBefore = (DateTime)sim.ExecuteScalar("select create_date from sys.server_principals where name = 'rotate_login'")!;

        _ = sim.ExecuteNonQuery("alter login rotate_login with password = 'N3wP@ssw0rd'");
        AreEqual(idBefore, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'rotate_login'"));
        var createAfter = (DateTime)sim.ExecuteScalar("select create_date from sys.server_principals where name = 'rotate_login'")!;
        var modifyAfter = (DateTime)sim.ExecuteScalar("select modify_date from sys.server_principals where name = 'rotate_login'")!;
        AreEqual(createBefore, createAfter);
        IsGreaterThanOrEqualTo(createAfter, modifyAfter);
    }

    [TestMethod]
    public void DropLogin_RemovesRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login gone_login with password = 'P@ssw0rd1'");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_principals where name = 'gone_login'"));
        _ = sim.ExecuteNonQuery("drop login gone_login");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_principals where name = 'gone_login'"));
    }

    [TestMethod]
    public void SqlLogins_ContainsSaAndLoginsButNotPublic()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login sql_login with password = 'P@ssw0rd1'");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sql_logins where name = 'sa'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sql_logins where name = 'sql_login'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.sql_logins where name = 'public'"));
        IsTrue(sim.ExecuteScalar("select password_hash from sys.sql_logins where name = 'sa'") is DBNull);
        IsTrue(sim.ExecuteScalar("select password_hash from sys.sql_logins where name = 'sql_login'") is DBNull);
        IsTrue((bool)sim.ExecuteScalar("select is_policy_checked from sys.sql_logins where name = 'sql_login'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_expiration_checked from sys.sql_logins where name = 'sql_login'")!);
    }

    [TestMethod]
    public void WhereFilterAndProjection_OrderByPrincipalId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login filtered_login with password = 'P@ssw0rd1'");
        using var reader = sim.ExecuteReader("select name from sys.server_principals where type = 'S' order by principal_id");
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        HasCount(2, names);
        AreEqual("sa", names[0]);
        AreEqual("filtered_login", names[1]);
    }

    [TestMethod]
    public void BothViews_Expose14Columns()
    {
        var sim = new Simulation();
        using (var reader = sim.ExecuteReader("select * from sys.server_principals"))
            AreEqual(14, reader.FieldCount);
        using (var reader = sim.ExecuteReader("select * from sys.sql_logins"))
            AreEqual(14, reader.FieldCount);
    }
}
