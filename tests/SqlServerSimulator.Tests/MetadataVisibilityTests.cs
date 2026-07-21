
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
