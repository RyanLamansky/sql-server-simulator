using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A synonym is its own securable. A reference written through one is checked
/// against the synonym and never walks through to the base: a grant on the base
/// alone does not admit it (and the denial names the synonym), a DENY on the
/// base does not block it, and the reverse holds for a direct reference to the
/// base. Column lists are rejected outright (Msg 1020), so every check through
/// a synonym is object-grain. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SynonymPermissionTests
{
    /// <summary>dbo.s FOR dbo.t, dbo.sp FOR dbo.p; u holds nothing beyond CONNECT.</summary>
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int not null, a int not null)",
            "insert dbo.t values (1, 10), (2, 20)",
            "create procedure dbo.p as select 7 as x",
            "create synonym dbo.s for dbo.t",
            "create synonym dbo.sp for dbo.p",
            "create user u without login");
        return sim;
    }

    [TestMethod]
    public void GrantOnSynonym_RecordsTheSynonymAsTheSecurable()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.database_permissions " +
            "where class = 1 and major_id = object_id('dbo.s') and minor_id = 0 " +
            "and permission_name = 'SELECT' and grantee_principal_id = user_id('u')"));
    }

    [TestMethod]
    public void Select_ThroughSynonym_HonorsTheSynonymGrant()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.s order by id"));
    }

    [TestMethod]
    public void Select_ThroughUnqualifiedSynonym_HonorsTheSynonymGrant()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from s order by id"));
    }

    [TestMethod]
    public void SynonymGrant_DoesNotReachTheBaseObject()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
        Contains("SELECT permission was denied on the object 't'", ex.Message);
    }

    [TestMethod]
    public void BaseGrant_DoesNotAdmitTheSynonymRead_AndTheDenialNamesTheSynonym()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.t to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; select id from dbo.s", 229);
        Contains("SELECT permission was denied on the object 's'", ex.Message);
    }

    [TestMethod]
    public void DenyOnSynonym_DoesNotBlockTheDirectBaseRead()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.t to u; deny select on dbo.s to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.s", 229);
    }

    [TestMethod]
    public void DenyOnBase_DoesNotBlockTheSynonymRead()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u; deny select on dbo.t to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.s order by id"));
    }

    [TestMethod]
    public void SchemaScopeGrant_CoversTheSynonym()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on schema::dbo to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.s order by id"));
    }

    [TestMethod]
    public void Select_ThroughSynonymInASubquery_ChecksTheSynonym()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.s where id in (select id from dbo.s) order by id"));
        var ex = sim.AssertSqlError("execute as user = 'u'; select s.id from dbo.s s where s.id in (select id from dbo.t)", 229);
        Contains("denied on the object 't'", ex.Message);
    }

    /// <summary>
    /// Provenance is per-reference, not per-object: one query naming both the
    /// synonym and its base checks the synonym object-grain and the base
    /// column-grain, independently. So <c>s.a</c> rides the synonym's
    /// object-level grant while <c>t.a</c> — the same stored column — is denied
    /// for want of a column grant on the base.
    /// </summary>
    [TestMethod]
    public void SynonymAndBaseInOneQuery_AreCheckedIndependently()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u; grant select (id) on dbo.t to u");
        AreEqual(10, sim.ExecuteScalar("execute as user = 'u'; select s.a from dbo.s s join dbo.t t on t.id = s.id where t.id = 1"));
        var ex = sim.AssertSqlError("execute as user = 'u'; select t.a from dbo.s s join dbo.t t on t.id = s.id", 230);
        Contains("column 'a'", ex.Message);
        Contains("of the object 't'", ex.Message);
    }

    // ---- Synonym over a view ----

    [TestMethod]
    public void SynonymOverView_ChecksTheSynonym_NotTheView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int not null)",
            "insert dbo.t values (1)",
            "create view dbo.v as select id from dbo.t",
            "create synonym dbo.sv for dbo.v",
            "create user u without login",
            "grant select on dbo.sv to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.sv"));
        var ex = sim.AssertSqlError("execute as user = 'u'; select id from dbo.v", 229);
        Contains("denied on the object 'v'", ex.Message);
    }

    // ---- Writes ----

    [TestMethod]
    public void Insert_ThroughSynonym_DeniedWithoutGrant_NamesTheSynonym()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; insert dbo.s values (3, 30)", 229);
        Contains("INSERT permission was denied on the object 's'", ex.Message);
    }

    [TestMethod]
    public void InsertUpdateDelete_ThroughSynonym_HonorTheSynonymGrants()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select, insert, update, delete on dbo.s to u");
        _ = sim.ExecuteNonQuery("""
            execute as user = 'u';
            insert dbo.s values (3, 30);
            update dbo.s set a = 31 where id = 3;
            delete dbo.s where id = 3
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.t"));
    }

    [TestMethod]
    public void Update_ThroughSynonym_StaysObjectGrain()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; update dbo.s set a = 1 where id = 1", 229);
        Contains("UPDATE permission was denied on the object 's'", ex.Message);
    }

    /// <summary>
    /// Without a WHERE clause the read requirement comes from the SET list
    /// alone: a constant right-hand side needs only UPDATE, while one that
    /// reads a column needs SELECT as well — object-grain on the synonym, so
    /// the denial names it. Probed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void Update_ThroughSynonymWithNoWhere_NeedsSelectOnlyWhenTheSetListReads()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant update on dbo.s to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; update dbo.s set a = 1");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.t where a = 1"));

        var ex = sim.AssertSqlError("execute as user = 'u'; update dbo.s set a = a + 1", 229);
        Contains("SELECT permission was denied on the object 's'", ex.Message);

        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; update dbo.s set a = a + 1");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.t where a = 2"));
    }

    [TestMethod]
    public void Delete_ThroughSynonym_StaysObjectGrain()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on dbo.s to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; delete dbo.s where id = 1", 229);
        Contains("DELETE permission was denied on the object 's'", ex.Message);
    }

    // ---- EXECUTE ----

    [TestMethod]
    public void Exec_ThroughProcedureSynonym_HonorsTheSynonymGrant()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant execute on dbo.sp to u");
        AreEqual(7, sim.ExecuteScalar("execute as user = 'u'; exec dbo.sp"));
    }

    /// <summary>
    /// The denial names the synonym and carries no <c>Procedure</c> attribution
    /// — the module was never entered — unlike a direct <c>EXEC dbo.p</c>, which
    /// attributes the proc.
    /// </summary>
    [TestMethod]
    public void Exec_ThroughProcedureSynonym_BaseGrantDoesNotAdmitIt()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant execute on dbo.p to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; exec dbo.sp", 229);
        Contains("EXECUTE permission was denied on the object 'sp'", ex.Message);
        IsTrue(string.IsNullOrEmpty(ex.Procedure));
    }

    [TestMethod]
    public void Exec_OnBaseProcedure_SynonymGrantDoesNotAdmitIt()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant execute on dbo.sp to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; exec dbo.p", 229);
        Contains("EXECUTE permission was denied on the object 'p'", ex.Message);
        AreEqual("dbo.p", ex.Procedure);
    }

    // ---- Column lists are not a synonym thing ----

    [TestMethod]
    public void ColumnListOnSynonym_Raises1020()
    {
        var ex = Seeded().AssertSqlError("grant select (id) on dbo.s to u", 1020);
        Contains("Sub-entity lists", ex.Message);
        AreEqual(3, ex.State);
    }

    /// <summary>Msg 1020 beats the Msg 4615 unknown-column check — the sub-entity rejection fires as soon as the securable resolves to a synonym.</summary>
    [TestMethod]
    public void ColumnListOnSynonym_BeatsTheUnknownColumnCheck()
        => _ = Seeded().AssertSqlError("grant select (nope) on dbo.s to u", 1020);

    [TestMethod]
    public void ColumnListAfterSynonymName_Raises1020()
        => _ = Seeded().AssertSqlError("deny select on dbo.s (id) to u", 1020);
}
