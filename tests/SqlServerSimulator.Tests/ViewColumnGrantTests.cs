using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Column-level GRANT / DENY on a <em>view</em>. The ordinals are the view's
/// own projection columns — including one computed from several base columns —
/// so a denial names the view column and the view. Ownership chaining means the
/// base table is never consulted: a grant on the base does not admit a read
/// through the view, and a DENY on the base does not block one. INSERT and
/// DELETE through a view stay object-grain. Probe-confirmed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ViewColumnGrantTests
{
    /// <summary>dbo.v over dbo.t(id, a, b) projecting id / a / b / both (= a + b); u holds SELECT (id) on the view only.</summary>
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int not null, a int not null, b int not null)",
            "insert dbo.t values (1, 10, 100), (2, 20, 200)",
            "create view dbo.v as select id, a, b, a + b as both from dbo.t",
            "create user u without login",
            "grant select (id) on dbo.v to u");
        return sim;
    }

    [TestMethod]
    public void Select_GrantedViewColumn_Succeeds()
        => AreEqual(1, Seeded().ExecuteScalar("execute as user = 'u'; select id from dbo.v order by id"));

    [TestMethod]
    public void Select_UngrantedViewColumn_Raises230()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select a from dbo.v", 230);
        Contains("SELECT permission was denied on the column 'a'", ex.Message);
        Contains("of the object 'v'", ex.Message);
    }

    [TestMethod]
    public void Select_ComputedViewColumn_NamesTheViewColumn_Not_ItsBaseColumns()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select both from dbo.v", 230);
        Contains("column 'both'", ex.Message);
    }

    [TestMethod]
    public void SelectStar_OverPartialViewGrant_Raises230OnFirstOffendingColumn()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select * from dbo.v", 230);
        Contains("column 'a'", ex.Message);
    }

    [TestMethod]
    public void CountStar_ThroughView_ChecksEveryViewColumn()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select count(*) from dbo.v", 230);
        Contains("column 'a'", ex.Message);
    }

    [TestMethod]
    public void ViewColumnGrant_DoesNotReachTheBaseTable()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
        Contains("SELECT permission was denied on the object 't'", ex.Message);
    }

    [TestMethod]
    public void BaseTableColumnGrant_DoesNotAdmitTheViewRead()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; grant select (id) on dbo.t to u2");
        var ex = sim.AssertSqlError("execute as user = 'u2'; select id from dbo.v", 229);
        Contains("denied on the object 'v'", ex.Message);
    }

    [TestMethod]
    public void ViewColumnDeny_BeatsViewTableGrant()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; grant select on dbo.v to u2; deny select (b) on dbo.v to u2");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u2'; select id from dbo.v order by id"));
        var ex = sim.AssertSqlError("execute as user = 'u2'; select b from dbo.v", 230);
        Contains("column 'b'", ex.Message);
    }

    [TestMethod]
    public void BaseTableDeny_DoesNotBlockTheViewRead_OwnershipChaining()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("deny select on dbo.t to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.v order by id"));
    }

    [TestMethod]
    public void NoAccessAtAll_ExplicitViewColumn_FallsBackTo229()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login");
        var ex = sim.AssertSqlError("execute as user = 'u2'; select id from dbo.v", 229);
        Contains("denied on the object 'v'", ex.Message);
    }

    // ---- UPDATE through a view ----

    /// <summary>dbo.uv over dbo.ut(id, a, b); u holds SELECT (id, a) + UPDATE (a) on the view.</summary>
    private static Simulation SeededForUpdate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.ut (id int not null, a int not null, b int not null)",
            "insert dbo.ut values (1, 10, 100)",
            "create view dbo.uv as select id, a, b from dbo.ut",
            "create user u without login",
            "grant select (id, a) on dbo.uv to u",
            "grant update (a) on dbo.uv to u");
        return sim;
    }

    [TestMethod]
    public void Update_GrantedViewColumn_Succeeds()
    {
        var sim = SeededForUpdate();
        _ = sim.ExecuteNonQuery("execute as user = 'u'; update dbo.uv set a = 11 where id = 1");
        AreEqual(11, sim.ExecuteScalar("select a from dbo.ut"));
    }

    [TestMethod]
    public void Update_UngrantedViewColumn_Raises230()
    {
        var ex = SeededForUpdate().AssertSqlError("execute as user = 'u'; update dbo.uv set b = 11 where id = 1", 230);
        Contains("UPDATE permission was denied on the column 'b'", ex.Message);
        Contains("of the object 'uv'", ex.Message);
    }

    [TestMethod]
    public void Update_ReadImpliesSelect_OnViewColumn_Raises230()
    {
        var ex = SeededForUpdate().AssertSqlError("execute as user = 'u'; update dbo.uv set a = 12 where b = 100", 230);
        Contains("SELECT permission was denied on the column 'b'", ex.Message);
    }

    [TestMethod]
    public void Insert_ThroughView_StaysObjectGrain()
    {
        var ex = SeededForUpdate().AssertSqlError("execute as user = 'u'; insert dbo.uv (id, a, b) values (9, 9, 9)", 229);
        Contains("INSERT permission was denied on the object 'uv'", ex.Message);
    }

    [TestMethod]
    public void Delete_ThroughView_StaysObjectGrain()
    {
        var ex = SeededForUpdate().AssertSqlError("execute as user = 'u'; delete dbo.uv where id = 1", 229);
        Contains("DELETE permission was denied on the object 'uv'", ex.Message);
    }

    /// <summary>
    /// The stored <c>minor_id</c> is the view's own projection ordinal, not a
    /// base-table one — <c>both</c> is view column 4 and the base table has only
    /// three columns, so no base ordinal could produce it.
    /// </summary>
    [TestMethod]
    public void ViewColumnGrant_StoresTheViewsOwnOrdinal()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; grant select (both) on dbo.v to u2");
        AreEqual(4, sim.ExecuteScalar(
            "select minor_id from sys.database_permissions " +
            "where major_id = object_id('dbo.v') and permission_name = 'SELECT' and grantee_principal_id = user_id('u2')"));
    }

    // ---- The object-grain grants that must keep covering a view read ----

    [TestMethod]
    public void PlainViewGrant_StillCoversEveryColumn()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; grant select on dbo.v to u2");
        AreEqual(110, sim.ExecuteScalar("execute as user = 'u2'; select both from dbo.v where id = 1"));
    }

    [TestMethod]
    public void SchemaScopeGrant_StillCoversAViewRead()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; grant select on schema::dbo to u2");
        AreEqual(110, sim.ExecuteScalar("execute as user = 'u2'; select both from dbo.v where id = 1"));
    }

    [TestMethod]
    public void DbDataReader_StillCoversAViewRead()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; alter role db_datareader add member u2");
        AreEqual(110, sim.ExecuteScalar("execute as user = 'u2'; select both from dbo.v where id = 1"));
    }

    [TestMethod]
    public void ViewJoinedToTable_ChecksBothAtColumnGrain()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u2 without login; grant select on dbo.v to u2; grant select (id) on dbo.t to u2");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u2'; select v.id from dbo.v v join dbo.t t on t.id = v.id order by v.id"));
        var ex = sim.AssertSqlError("execute as user = 'u2'; select t.a from dbo.v v join dbo.t t on t.id = v.id", 230);
        Contains("column 'a'", ex.Message);
        Contains("of the object 't'", ex.Message);
    }

    [TestMethod]
    public void GrantOnMissingViewColumn_Raises4615()
    {
        var sim = Seeded();
        var ex = sim.AssertSqlError("grant select (nope) on dbo.v to u", 4615);
        Contains("Invalid column name 'nope'", ex.Message);
    }
}
