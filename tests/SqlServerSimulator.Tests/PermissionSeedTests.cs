using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The grants every database starts with, and how <c>GRANT</c> / <c>REVOKE</c>
/// / <c>DENY</c> treat them (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class PermissionSeedTests
{
    [TestMethod]
    public void FreshDatabase_ListsTheDatabaseScopeSeed()
    {
        using var reader = new Simulation().ExecuteReader("""
            select grantee_principal_id, grantor_principal_id, permission_name, state
            from sys.database_permissions where class = 0 order by grantee_principal_id, permission_name
            """);
        var rows = reader.EnumerateRecords().Select(r => $"{r.GetInt32(0)}|{r.GetInt32(1)}|{r.GetString(2)}|{r.GetString(3)}").ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                "0|1|VIEW ANY COLUMN ENCRYPTION KEY DEFINITION|G",
                "0|1|VIEW ANY COLUMN MASTER KEY DEFINITION|G",
                "1|1|CONNECT|G",
            },
            rows);
    }

    [TestMethod]
    public void FreshDatabase_GrantsPublicSelectOnTheSystemObjects()
    {
        var sim = new Simulation();
        AreEqual(232, sim.ExecuteScalar("select count(*) from sys.database_permissions where class = 1 and grantee_principal_id = 0 and type = 'SL'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_permissions where major_id = object_id('sys.tables')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_permissions where major_id = object_id('sys.databases')"));
    }

    [TestMethod]
    [DataRow("master", 1)]
    [DataRow("tempdb", 1)]
    [DataRow("msdb", 1)]
    [DataRow("model", 0)]
    public void Guest_HoldsConnectInTheSystemDatabasesThatServeIt(string database, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            $"select count(*) from {database}.sys.database_permissions where grantee_principal_id = 2 and permission_name = 'CONNECT'"));

    [TestMethod]
    [DataRow("master", "revoke connect from guest")]
    [DataRow("tempdb", "revoke connect from guest")]
    [DataRow("tempdb", "deny connect to guest")]
    public void Guest_KeepsConnectInMasterAndTempdb(string database, string statement)
        => new Simulation().AssertSqlError($"use {database}; {statement}", 15182,
            "Cannot disable access to the guest user in master or tempdb.");

    [TestMethod]
    public void Guest_LosesConnectInMsdb()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "use msdb; revoke connect from guest; select count(*) from sys.database_permissions where grantee_principal_id = 2"));

    [TestMethod]
    public void Revoke_RemovesASeededGrant()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            revoke view any column master key definition from public;
            select count(*) from sys.database_permissions where permission_name = 'VIEW ANY COLUMN MASTER KEY DEFINITION'
            """));

    [TestMethod]
    public void Deny_ReplacesASeededGrant()
        => AreEqual("D", new Simulation().ExecuteScalar("""
            deny view any column master key definition to public;
            select string_agg(state, ',') from sys.database_permissions where permission_name = 'VIEW ANY COLUMN MASTER KEY DEFINITION'
            """));

    [TestMethod]
    public void Grant_ReplacesADeny()
        => AreEqual("G", new Simulation().ExecuteScalar("""
            create table t (a int);
            create user u without login;
            deny select on t to u;
            grant select on t to u;
            select string_agg(state, ',') from sys.database_permissions where major_id = object_id('t')
            """));

    [TestMethod]
    public void Grant_ToDbo_IsAClassZeroNotice()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var messages = new List<(int Number, byte Class, byte State)>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add((error.Number, error.Class, error.State));
        };
        _ = connection.CreateCommand("revoke connect from dbo").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { (4624, (byte)0, (byte)2) }, messages);
    }

    [TestMethod]
    public void RestrictedUser_ReadsCatalogViewsThroughTheSeededGrant()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            create user u without login;
            execute as user = 'u';
            select isnull((select top 1 1 from sys.objects where 1 = 0), 7)
            """));

    [TestMethod]
    public void RevokedSeededGrant_RefusesTheCatalogView()
        => new Simulation().AssertSqlError("""
            create user u without login;
            revoke select on sys.tables from public;
            execute as user = 'u';
            select count(*) from sys.tables
            """, 229, "The SELECT permission was denied on the object 'tables', database 'mssqlsystemresource', schema 'sys'.");

    [TestMethod]
    public void DeniedCatalogView_IsRefused()
        => new Simulation().AssertSqlError("""
            create user u without login;
            deny select on sys.indexes to u;
            execute as user = 'u';
            select count(*) from sys.indexes
            """, 229, "The SELECT permission was denied on the object 'indexes', database 'mssqlsystemresource', schema 'sys'.");

    [TestMethod]
    public void CatalogViewGrant_StoresTheViewsObjectId()
        => AreEqual("G", new Simulation().ExecuteScalar("""
            create user u without login;
            deny select on sys.indexes to u;
            grant select on sys.indexes to u;
            select string_agg(state, ',') from sys.database_permissions where major_id = object_id('sys.indexes') and grantee_principal_id = user_id('u')
            """));

    [TestMethod]
    public void RestrictedUser_HasPermsByName_AnswersFromTheSeed()
    {
        using var reader = new Simulation().ExecuteReader("""
            create user u without login;
            execute as user = 'u';
            select has_perms_by_name(null, 'DATABASE', 'VIEW ANY COLUMN MASTER KEY DEFINITION'),
                   has_perms_by_name('sys.tables', 'OBJECT', 'SELECT'),
                   has_perms_by_name(null, 'DATABASE', 'CONNECT')
            """);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(1, reader.GetInt32(2));
    }

    [TestMethod]
    public void Master_GrantsPublicItsSystemProcedures_AndSeedsItsPolicyUsers()
    {
        // Probed 2026-09-28 against SQL Server 2025.
        var sim = new Simulation();
        AreEqual("877|1827|4", sim.ExecuteScalar("""
            use master;
            select concat(
                (select count(*) from sys.database_permissions where class = 1 and grantee_principal_id = 0 and type = 'SL'), '|',
                (select count(*) from sys.database_permissions where class = 1 and grantee_principal_id = 0 and type = 'EX'), '|',
                (select count(*) from sys.database_permissions p join sys.database_principals u on u.principal_id = p.grantee_principal_id where u.name like '##%'))
            """));
        AreEqual("C", sim.ExecuteScalar("select type from master.sys.database_principals where name = '##MS_AgentSigningCertificate##'"));
    }

    [TestMethod]
    public void Msdb_SeedsItsAgentRoles_WithTheirMemberships()
    {
        var sim = new Simulation();
        AreEqual("SQLAgentReaderRole", sim.ExecuteScalar("""
            select r.name from msdb.sys.database_role_members m
            join msdb.sys.database_principals r on r.principal_id = m.role_principal_id
            join msdb.sys.database_principals u on u.principal_id = m.member_principal_id
            where u.name = 'SQLAgentOperatorRole'
            """));
        AreEqual(25, sim.ExecuteScalar("use msdb; create user fresh without login; select database_principal_id('fresh')"));
    }
}
