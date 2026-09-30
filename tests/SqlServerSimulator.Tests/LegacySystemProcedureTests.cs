using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The pre-DDL system procedures: <c>sp_addtype</c> / <c>sp_droptype</c>, the login,
/// user, role and server-role management set, the table / index / statistics option
/// procedures and the small report procedures.
/// </summary>
[TestClass]
public sealed class LegacySystemProcedureTests
{
    [TestMethod]
    public void AddType_CreatesAliasType()
        => AreEqual("varchar", new Simulation().ExecuteScalar("""
            exec sp_addtype zip, 'varchar(10)', 'not null';
            select type_name(system_type_id) from sys.types where name = 'zip'
            """));

    [TestMethod]
    public void DropType_RemovesAliasType()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            exec sp_addtype zip, 'varchar(10)';
            exec sp_droptype zip;
            select count(*) from sys.types where name = 'zip'
            """));

    [TestMethod]
    public void DropType_Missing_Raises15036()
        => _ = new Simulation().AssertSqlError("exec sp_droptype nosuch", 15036);

    [TestMethod]
    public void AddLogin_CreatesSqlLogin()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            exec sp_addlogin 'zl1', 'Passw0rd!x';
            select count(*) from sys.server_principals where name = 'zl1'
            """));

    [TestMethod]
    public void DropLogin_Missing_Raises15007()
        => _ = new Simulation().AssertSqlError("exec sp_droplogin 'nosuch'", 15007);

    [TestMethod]
    public void CheckPolicy_ShortPassword_Raises33062()
        => _ = new Simulation().AssertSqlError("create login zl2 with password = 'Ab1!'", 33062);

    [TestMethod]
    public void AddUser_CreatesUserWithLoginLink()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create login zl3 with password = 'Passw0rd!x';
            exec sp_adduser 'zl3';
            select count(*) from sys.database_principals where name = 'zl3'
            """));

    [TestMethod]
    public void AddRole_ThenAddRoleMember_ListsMember()
        => AreEqual("zr1", new Simulation().ExecuteScalar("""
            create user zu1 without login;
            exec sp_addrole 'zr1';
            exec sp_addrolemember 'zr1', 'zu1';
            select top 1 r.name from sys.database_role_members m join sys.database_principals r on r.principal_id = m.role_principal_id join sys.database_principals u on u.principal_id = m.member_principal_id where u.name = 'zu1'
            """));

    [TestMethod]
    public void TableOption_TextInRow_SetsCatalogColumn()
        => AreEqual(256, new Simulation().ExecuteScalar("""
            create table t (a int, b text);
            exec sp_tableoption 't', 'text in row', 'on';
            select text_in_row_limit from sys.tables where name = 't'
            """));

    [TestMethod]
    public void TableOption_UnknownTable_Raises15388()
        => _ = new Simulation().AssertSqlError("exec sp_tableoption 'nosuch', 'text in row', 'on'", 15388);

    [TestMethod]
    public void IndexOption_DisallowPageLocks_ShowsInCatalog()
        => IsFalse((bool)new Simulation().ExecuteScalar("""
            create table t (a int);
            create index ix on t (a);
            exec sp_indexoption 't.ix', 'DisallowPageLocks', 'true';
            select allow_page_locks from sys.indexes where name = 'ix'
            """)!);

    [TestMethod]
    public void AutoStats_Off_SetsNoRecompute()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (a int);
            create index ix on t (a);
            exec sp_autostats 't', 'OFF', 'ix';
            select no_recompute from sys.stats where name = 'ix'
            """)!);

    [TestMethod]
    public void CreateStats_CreatesAutoNamedStatistic()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (a int, b int);
            exec sp_createstats;
            select count(*) from sys.stats where object_id = object_id('t') and name = '_WA_Sys_a' or name like 'a'
            """));

    [TestMethod]
    public void HelpLanguage_ListsEnglishWithMonthNames()
        => AreEqual("January,February,March,April,May,June,July,August,September,October,November,December",
            new Simulation().ExecuteScalar("select months from sys.syslanguages where name = 'us_english'"));

    [TestMethod]
    public void HelpLanguage_Unknown_Raises15033()
        => _ = new Simulation().AssertSqlError("exec sp_helplanguage 'bogus'", 15033);

    [TestMethod]
    public void HelpServer_Unknown_Raises15015()
        => _ = new Simulation().AssertSqlError("exec sp_helpserver 'nope'", 15015);

    [TestMethod]
    public void HelpSort_DescribesDefaultCollation()
        => AreEqual("Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 52 on Code Page 1252 for non-Unicode Data",
            new Simulation().ExecuteScalar("exec sp_helpsort"));

    [TestMethod]
    public void Monitor_ReturnsFourResultSets()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("exec sp_monitor").ExecuteReader();
        var sets = 1;
        while (reader.NextResult())
            sets++;
        AreEqual(4, sets);
    }

    [TestMethod]
    public void ForEachWorker_WithoutCommand_Raises201()
        => _ = new Simulation().AssertSqlError("exec sp_MSforeach_worker", 201);
}
