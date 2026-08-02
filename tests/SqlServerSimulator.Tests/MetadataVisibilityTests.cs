
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Metadata-visibility filtering for a restricted principal: object-scoped
/// catalog-view rows (and <c>OBJECT_ID</c> / <c>OBJECT_NAME</c> /
/// <c>OBJECT_SCHEMA_NAME</c> results) surface only for objects the effective
/// principal may view metadata for — any granted permission (including
/// VIEW DEFINITION and column-scope grants) at object / schema / database scope,
/// or the full-visibility bypass (dbo / db_owner / db_ddladmin /
/// db_securityadmin / CONTROL or VIEW DEFINITION at database scope). A non-dbo
/// session is established in-batch via <c>EXECUTE AS USER = 'u'</c>.
/// Probe-confirmed against SQL Server 2025 (2026-07-21).
/// </summary>
[TestClass]
public sealed class MetadataVisibilityTests
{
    // tab_sel: SELECT-granted. tab_vd: VIEW-DEFINITION-only. tab_none: no grant.
    // A trigger hangs off tab_sel to exercise trigger-follows-parent visibility.
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.tab_sel (id int not null, secretcol int null)",
            "create table dbo.tab_none (id int not null)",
            "create table dbo.tab_vd (id int not null)",
            "create trigger dbo.trg_sel on dbo.tab_sel after insert as select 1",
            "create user u without login",
            "grant select on object::dbo.tab_sel to u",
            "grant view definition on object::dbo.tab_vd to u");
        return sim;
    }

    private static List<string> QueryNames(Simulation sim, string commandText)
    {
        var names = new List<string>();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand(commandText);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [TestMethod]
    public void SysTables_RestrictedPrincipal_ShowsOnlyGrantedObjects()
    {
        var names = QueryNames(Seeded(),
            "execute as user = 'u'; select name from sys.tables where name like 'tab[_]%' order by name");
        CollectionAssert.AreEqual(new[] { "tab_sel", "tab_vd" }, names);
    }

    [TestMethod]
    public void SysTables_UngrantedObject_IsHidden()
        => AreEqual(0, Seeded().ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name = 'tab_none'"));

    [TestMethod]
    public void ViewDefinitionOnly_RevealsColumnsButDeniesData()
    {
        var sim = Seeded();
        // Object-grain: VIEW DEFINITION on tab_vd surfaces its column metadata,
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.columns where object_id = object_id('dbo.tab_vd')"));
        // but data access is still denied (Msg 229).
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.tab_vd", 229);
    }

    [TestMethod]
    public void SysColumns_IsObjectGrain_RevealsEvenUnreadableColumn()
    {
        // A SELECT grant on tab_sel reveals every column's metadata, including
        // secretcol (probe Q2). sys.columns visibility is object-grain.
        var names = QueryNames(Seeded(),
            "execute as user = 'u'; select name from sys.columns where object_id = object_id('dbo.tab_sel') order by column_id");
        CollectionAssert.AreEqual(new[] { "id", "secretcol" }, names);
    }

    [TestMethod]
    public void SysColumns_UngrantedObject_HasNoRows()
    {
        var sim = Seeded();
        // Fetch the hidden id as dbo, then read sys.columns as the restricted user.
        AreEqual(0, sim.ExecuteScalar("""
            declare @id int = object_id('dbo.tab_none');
            execute as user = 'u';
            select count(*) from sys.columns where object_id = @id
            """));
    }

    [TestMethod]
    public void SysTriggers_FollowsParentTableVisibility()
    {
        // trg_sel's parent tab_sel is visible → the trigger row shows.
        var names = QueryNames(Seeded(),
            "execute as user = 'u'; select name from sys.triggers order by name");
        CollectionAssert.AreEqual(new[] { "trg_sel" }, names);
    }

    [TestMethod]
    public void InformationSchemaTables_FiltersIdentically()
    {
        var names = QueryNames(Seeded(),
            "execute as user = 'u'; select table_name from information_schema.tables where table_name like 'tab[_]%' order by table_name");
        CollectionAssert.AreEqual(new[] { "tab_sel", "tab_vd" }, names);
    }

    [TestMethod]
    public void ObjectId_ReturnsNull_ForInvisibleObject()
    {
        var sim = Seeded();
        AreEqual(DBNull.Value, sim.ExecuteScalar("execute as user = 'u'; select object_id('dbo.tab_none')"));
        _ = IsInstanceOfType<int>(sim.ExecuteScalar("execute as user = 'u'; select object_id('dbo.tab_sel')"));
    }

    [TestMethod]
    public void ObjectNameAndSchemaName_ReturnNull_ForInvisibleId()
    {
        var sim = Seeded();
        AreEqual(DBNull.Value, sim.ExecuteScalar("""
            declare @id int = object_id('dbo.tab_none');
            execute as user = 'u';
            select object_name(@id)
            """));
        AreEqual(DBNull.Value, sim.ExecuteScalar("""
            declare @id int = object_id('dbo.tab_none');
            execute as user = 'u';
            select object_schema_name(@id)
            """));
    }

    [TestMethod]
    public void DatabaseScopeViewDefinition_RevealsAllObjects()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant view definition to u");
        AreEqual(3, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name like 'tab[_]%'"));
        _ = IsInstanceOfType<int>(sim.ExecuteScalar("execute as user = 'u'; select object_id('dbo.tab_none')"));
    }

    [TestMethod]
    public void DbOwner_SeesEveryObject()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_owner add member u");
        AreEqual(3, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name like 'tab[_]%'"));
    }

    [TestMethod]
    public void DbDdlAdmin_SeesEveryObject()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_ddladmin add member u");
        AreEqual(3, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name like 'tab[_]%'"));
    }

    [TestMethod]
    public void DbSecurityAdmin_SeesEveryObject()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_securityadmin add member u");
        AreEqual(3, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name like 'tab[_]%'"));
    }

    [TestMethod]
    public void Dbo_SeesEveryObject_NoFiltering()
        => AreEqual(3, Seeded().ExecuteScalar("select count(*) from sys.tables where name like 'tab[_]%'"));

    [TestMethod]
    public void SchemaScopeGrant_RevealsAllObjectsInSchema()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on schema::dbo to u");
        AreEqual(3, sim.ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name like 'tab[_]%'"));
    }

    [TestMethod]
    public void ConnectGrant_DoesNotReveal_Objects()
    {
        // CREATE USER auto-seeds a CONNECT grant; it must not blanket-reveal the
        // catalog. Only the explicitly granted tab_sel / tab_vd stay visible.
        AreEqual(2, Seeded().ExecuteScalar(
            "execute as user = 'u'; select count(*) from sys.tables where name like 'tab[_]%'"));
    }
}

/// <summary>
/// Metadata visibility across a database boundary: a catalog-view read of
/// <c>other.sys.*</c> asks exactly what a data reference asks — the login's
/// user <em>there</em> — and filters by that principal's visibility, with the
/// same Msg 916 for a login that has no user in the target. Real refuses every
/// cross-database catalog view that way, filtered and unfiltered alike
/// (<c>sys.databases</c> included), while the guest-served system databases
/// pass. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CrossDatabaseMetadataVisibilityTests
{
    /// <summary>
    /// <c>home</c> holds the session's user; <c>away</c> holds three tables, of
    /// which the away user sees one by SELECT and one by VIEW DEFINITION.
    /// </summary>
    private static Simulation TwoDatabaseFixture(bool createAwayUser = true)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'S3cret!Pass'; create database home; create database away");
        _ = sim.ExecuteNonQuery("use home; create user homeuser for login app");
        _ = sim.ExecuteNonQuery("""
            use away;
            create table dbo.rem_sel (id int not null);
            create table dbo.rem_vd (id int not null);
            create table dbo.rem_none (id int not null)
            """);
        if (createAwayUser)
        {
            _ = sim.ExecuteNonQuery("""
                use away;
                create user awayuser for login app;
                grant select on dbo.rem_sel to awayuser;
                grant view definition on dbo.rem_vd to awayuser
                """);
        }
        return sim;
    }

    private static SimulatedDbConnection ConnectAsApp(Simulation sim)
    {
        var connection = sim.CreateDbConnection();
        connection.ConnectionString = "User ID=app;Password=S3cret!Pass;Initial Catalog=home";
        connection.Open();
        return connection;
    }

    private static List<string> Names(SimulatedDbConnection connection, string commandText)
    {
        var names = new List<string>();
        using var command = connection.CreateCommand(commandText);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [TestMethod]
    public void SysTables_CrossDatabase_FiltersByTheTargetUsersVisibility()
    {
        using var connection = ConnectAsApp(TwoDatabaseFixture());
        CollectionAssert.AreEqual(
            new[] { "rem_sel", "rem_vd" },
            Names(connection, "select name from away.sys.tables where name like 'rem[_]%' order by name"));
    }

    [TestMethod]
    public void InformationSchemaTables_CrossDatabase_FiltersByTheTargetUsersVisibility()
    {
        using var connection = ConnectAsApp(TwoDatabaseFixture());
        CollectionAssert.AreEqual(
            new[] { "rem_sel", "rem_vd" },
            Names(connection, "select table_name from away.information_schema.tables where table_name like 'rem[_]%' order by table_name"));
    }

    [TestMethod]
    public void SysColumns_CrossDatabase_FollowsTheSameObjectSet()
    {
        using var connection = ConnectAsApp(TwoDatabaseFixture());
        using var command = connection.CreateCommand(
            "select count(*) from away.sys.columns c join away.sys.tables t on t.object_id = c.object_id where t.name like 'rem[_]%'");
        AreEqual(2, command.ExecuteScalar());
    }

    [TestMethod]
    public void SysTables_CrossDatabase_SessionGrantsDoNotTravel()
    {
        // A grant held in `home` reveals nothing in `away` — the target user's
        // own visibility is the only input.
        var sim = TwoDatabaseFixture();
        _ = sim.ExecuteNonQuery("use home; create table dbo.local_only (id int); grant control on dbo.local_only to homeuser");
        using var connection = ConnectAsApp(sim);
        HasCount(2, Names(connection, "select name from away.sys.tables where name like 'rem[_]%'"));
    }

    [TestMethod]
    public void SysTables_CrossDatabase_TargetUserWithFullVisibility_SeesEverything()
    {
        var sim = TwoDatabaseFixture();
        _ = sim.ExecuteNonQuery("use away; alter role db_ddladmin add member awayuser");
        using var connection = ConnectAsApp(sim);
        HasCount(3, Names(connection, "select name from away.sys.tables where name like 'rem[_]%'"));
    }

    [TestMethod]
    public void SysTables_CrossDatabase_NoUserInTarget_Raises916()
    {
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select name from away.sys.tables").ExecuteScalar());
        AreEqual(916, ex.Number);
        AreEqual(14, ex.Class);
        AreEqual(2, ex.State);
        AreEqual("The server principal \"app\" is not able to access the database \"away\" under the current security context.", ex.Message);
    }

    [TestMethod]
    public void UnfilteredCatalogView_CrossDatabase_NoUserInTarget_Raises916()
    {
        // sys.databases carries no metadata filter of its own, yet real refuses
        // the cross-database read the same way — the refusal is about reaching
        // the database, not about the view.
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select count(*) from away.sys.databases").ExecuteScalar());
        AreEqual(916, ex.Number);
    }

    [TestMethod]
    public void DataRead_AndCatalogRead_DivergeForAnUnrevealedObject()
    {
        // The contrast the filter draws: an object the target user can't see is
        // absent from the catalog, while naming it in a query is Msg 229.
        using var connection = ConnectAsApp(TwoDatabaseFixture());
        AreEqual(0, connection.CreateCommand("select count(*) from away.sys.tables where name = 'rem_none'").ExecuteScalar());
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select id from away.dbo.rem_none").ExecuteScalar());
        AreEqual(229, ex.Number);
    }

    [TestMethod]
    public void GuestServedSystemDatabase_CrossDatabaseCatalogRead_Passes()
    {
        // guest is accessible in master / tempdb / msdb, so the catalog read
        // resolves there and filters by guest instead of refusing.
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        AreEqual(0, connection.CreateCommand("select count(*) from master.sys.tables").ExecuteScalar());
        AreEqual(0, connection.CreateCommand("select count(*) from msdb.sys.tables where name = 'nope'").ExecuteScalar());
    }

    [TestMethod]
    public void RestrictedTemplateDatabase_CrossDatabaseCatalogRead_Raises916()
    {
        // `model` allows no guest access, so it refuses like any user database.
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select count(*) from model.sys.tables").ExecuteScalar());
        AreEqual(916, ex.Number);
    }

    [TestMethod]
    public void SysadminLogin_CrossDatabaseCatalogRead_Unfiltered()
    {
        var sim = TwoDatabaseFixture(createAwayUser: false);
        _ = sim.ExecuteNonQuery("alter server role sysadmin add member app");
        using var connection = ConnectAsApp(sim);
        HasCount(3, Names(connection, "select name from away.sys.tables where name like 'rem[_]%'"));
    }

    [TestMethod]
    public void DboSession_CrossDatabaseCatalogRead_Unfiltered()
        => AreEqual(3, TwoDatabaseFixture().ExecuteScalar(
            "use home; select count(*) from away.sys.tables where name like 'rem[_]%'"));

    [TestMethod]
    public void ExecuteAsUser_CrossDatabaseCatalogRead_Raises916()
    {
        // A database-scoped identity can't reach the away catalog any more than
        // it can reach away data — unless the source database is trustworthy.
        var sim = TwoDatabaseFixture();
        _ = sim.AssertSqlError("use home; execute as user = 'homeuser'; select name from away.sys.tables", 916);
        _ = sim.ExecuteNonQuery("alter database home set trustworthy on");
        AreEqual(2, sim.ExecuteScalar(
            "use home; execute as user = 'homeuser'; select count(*) from away.sys.tables where name like 'rem[_]%'"));
    }

    // ---- the OBJECT_* scalars ask the same question in the named database ----

    /// <summary>The <c>object_id</c> of <c>away.dbo.<paramref name="name"/></c>, read as dbo.</summary>
    private static int AwayObjectId(Simulation sim, string name) =>
        (int)sim.ExecuteScalar($"use away; select object_id('dbo.{name}')")!;

    [TestMethod]
    public void ObjectId_ThreePartName_TargetUserCanView_ReturnsTheId()
    {
        var sim = TwoDatabaseFixture();
        var expected = AwayObjectId(sim, "rem_sel");
        using var connection = ConnectAsApp(sim);
        AreEqual(expected, connection.CreateCommand("select object_id('away.dbo.rem_sel')").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_ThreePartName_TargetUserCannotView_ReturnsNull()
    {
        // rem_none is revealed by no grant in `away`, so the id is hidden the
        // same way the sys.tables row is.
        using var connection = ConnectAsApp(TwoDatabaseFixture());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('away.dbo.rem_none')").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_ThreePartName_SessionGrantsDoNotTravel()
    {
        // A grant held in `home` reveals nothing across the boundary — the away
        // user's own visibility is the only input.
        var sim = TwoDatabaseFixture();
        _ = sim.ExecuteNonQuery("use home; create table dbo.rem_none (id int); grant control on dbo.rem_none to homeuser");
        using var connection = ConnectAsApp(sim);
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('away.dbo.rem_none')").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_ThreePartName_NoUserInTarget_Raises916()
    {
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select object_id('away.dbo.rem_sel')").ExecuteScalar());
        AreEqual(916, ex.Number);
        AreEqual(14, ex.Class);
        AreEqual(2, ex.State);
        AreEqual("The server principal \"app\" is not able to access the database \"away\" under the current security context.", ex.Message);
    }

    [TestMethod]
    public void ObjectId_ThreePartName_NoUserInTarget_UnresolvedName_ReturnsNull()
    {
        // The gate runs after resolution, so a name that matches nothing in an
        // unreachable database is NULL rather than a refusal (probe-confirmed) —
        // as is a name whose object the type filter excludes.
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('away.dbo.no_such_table')").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('away.dbo.rem_sel', 'P')").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('nosuchdb.dbo.rem_sel')").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_ThreePartName_GuestServedSystemDatabase_FiltersByGuest()
    {
        // master serves guest, so the lookup resolves there instead of refusing —
        // and then filters by guest, which sees only what guest is granted.
        var sim = TwoDatabaseFixture(createAwayUser: false);
        _ = sim.ExecuteNonQuery("use master; create table dbo.m_open (id int); create table dbo.m_shut (id int); grant select on dbo.m_open to guest");
        var expected = (int)sim.ExecuteScalar("use master; select object_id('dbo.m_open')")!;
        using var connection = ConnectAsApp(sim);
        AreEqual(expected, connection.CreateCommand("select object_id('master.dbo.m_open')").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('master.dbo.m_shut')").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_ThreePartName_RestrictedTemplateDatabase_Raises916()
    {
        // `model` allows no guest access, so it refuses like any user database.
        var sim = TwoDatabaseFixture(createAwayUser: false);
        _ = sim.ExecuteNonQuery("use model; create table dbo.mo_tab (id int)");
        using var connection = ConnectAsApp(sim);
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("select object_id('model.dbo.mo_tab')").ExecuteScalar());
        AreEqual(916, ex.Number);
    }

    [TestMethod]
    public void ObjectId_ThreePartName_SysadminLogin_Answers()
    {
        var sim = TwoDatabaseFixture(createAwayUser: false);
        _ = sim.ExecuteNonQuery("alter server role sysadmin add member app");
        var expected = AwayObjectId(sim, "rem_none");
        using var connection = ConnectAsApp(sim);
        AreEqual(expected, connection.CreateCommand("select object_id('away.dbo.rem_none')").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectId_ThreePartName_DboSession_Answers()
    {
        var sim = TwoDatabaseFixture(createAwayUser: false);
        AreEqual(AwayObjectId(sim, "rem_none"), sim.ExecuteScalar("use home; select object_id('away.dbo.rem_none')"));
    }

    [TestMethod]
    public void ObjectNameIdForm_ExplicitDatabaseId_FiltersByThatDatabasesPrincipal()
    {
        var sim = TwoDatabaseFixture();
        var visible = AwayObjectId(sim, "rem_sel");
        var hidden = AwayObjectId(sim, "rem_none");
        using var connection = ConnectAsApp(sim);
        AreEqual("rem_sel", connection.CreateCommand($"select object_name({visible}, db_id('away'))").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand($"select object_name({hidden}, db_id('away'))").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectNameIdForm_NoUserInTarget_ReturnsNullWithoutRaising()
    {
        // The id form asks the visibility question alone: a database the login
        // can't reach reveals nothing, and there is no Msg 916 to catch — unlike
        // the three-part name form (probe-confirmed).
        var sim = TwoDatabaseFixture(createAwayUser: false);
        var id = AwayObjectId(sim, "rem_sel");
        using var connection = ConnectAsApp(sim);
        AreEqual(DBNull.Value, connection.CreateCommand($"select object_name({id}, db_id('away'))").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectSchemaNameIdForm_ExplicitDatabaseId_ResolvesInThatDatabase()
    {
        var sim = TwoDatabaseFixture();
        sim.ExecuteBatches("use away", "create schema rems", "create table rems.rem_sch (id int); grant select on rems.rem_sch to awayuser");
        var id = (int)sim.ExecuteScalar("use away; select object_id('rems.rem_sch')")!;
        using var connection = ConnectAsApp(sim);
        AreEqual("rems", connection.CreateCommand($"select object_schema_name({id}, db_id('away'))").ExecuteScalar());
        // Without the database-id argument the lookup stays in `home`, where the
        // id belongs to nothing.
        AreEqual(DBNull.Value, connection.CreateCommand($"select object_schema_name({id})").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectSchemaNameIdForm_TargetUserCannotView_ReturnsNull()
    {
        var sim = TwoDatabaseFixture();
        var hidden = AwayObjectId(sim, "rem_none");
        using var connection = ConnectAsApp(sim);
        AreEqual(DBNull.Value, connection.CreateCommand($"select object_schema_name({hidden}, db_id('away'))").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectSchemaNameIdForm_NoUserInTarget_ReturnsNullWithoutRaising()
    {
        var sim = TwoDatabaseFixture(createAwayUser: false);
        var id = AwayObjectId(sim, "rem_sel");
        using var connection = ConnectAsApp(sim);
        AreEqual(DBNull.Value, connection.CreateCommand($"select object_schema_name({id}, db_id('away'))").ExecuteScalar());
    }

    [TestMethod]
    public void DbIdAndDbName_StillAnswerForAnUnreachableDatabase()
    {
        // They read no metadata of the database, so the refusal doesn't reach
        // them (probe-confirmed).
        using var connection = ConnectAsApp(TwoDatabaseFixture(createAwayUser: false));
        AreEqual("away", connection.CreateCommand("select db_name(db_id('away'))").ExecuteScalar());
    }
}
