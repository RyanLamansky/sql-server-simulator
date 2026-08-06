using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// MERGE INTO updatable view — full parity with the
/// UPDATE / INSERT / DELETE-through-view shapes: view-column lookups
/// translate through <c>BaseColumnOrdinals</c>, visibility filters scope
/// the target row set, <c>WITH CHECK OPTION</c> is enforced on
/// inserts/updates, and INSTEAD OF triggers replace the heap-write path
/// per action. Probed against real SQL Server 2025 (2026-05-19).
/// </summary>
[TestClass]
public sealed class MergeViewTests
{
    [TestMethod]
    public void SimpleView_NotMatchedInsert_LandsInBase()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_base as select id, v from base_t",
            """
            merge into v_base using (values(1,10),(2,20)) src(id, v) on v_base.id = src.id
            when not matched then insert (id, v) values (src.id, src.v);
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from base_t"));
        AreEqual(10, sim.ExecuteScalar("select v from base_t where id = 1"));
    }

    /// <summary>
    /// With no column list the implied one is the <em>view's</em> projection,
    /// not the base table's: three values land against a three-column view
    /// over a four-column table, and the untargeted base column takes its
    /// default. A fourth value is Msg 213. Probed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void SimpleView_NotMatchedInsertWithoutColumnList_UsesViewProjection()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, a int, b varchar(10), c int default 7)",
            "create view v_base as select id, a, b from base_t",
            """
            merge into v_base using (values(3, 30, 'three')) src(id, a, b) on v_base.id = src.id
            when not matched then insert values (src.id, src.a, src.b);
            """);
        AreEqual("30|three|7", sim.ExecuteScalar(
            "select cast(a as varchar(10)) + '|' + b + '|' + cast(c as varchar(10)) from base_t where id = 3"));

        var ex = ThrowsExactly<SimulatedSqlException>(() => sim.ExecuteBatches("""
            merge into v_base using (values(4, 40, 'four')) src(id, a, b) on v_base.id = src.id
            when not matched then insert values (src.id, src.a, src.b, 5);
            """));
        AreEqual(213, ex.Number);
    }

    [TestMethod]
    public void SimpleView_MatchedUpdate_LandsInBase()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int); insert base_t values (1, 0)",
            "create view v_base as select id, v from base_t",
            """
            merge into v_base using (values(1, 99)) src(id, v) on v_base.id = src.id
            when matched then update set v = src.v;
            """);
        AreEqual(99, sim.ExecuteScalar("select v from base_t where id = 1"));
    }

    [TestMethod]
    public void SimpleView_NotMatchedBySourceDelete_RemovesBaseRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int); insert base_t values (1,1),(2,2),(3,3)",
            "create view v_base as select id, v from base_t",
            """
            merge into v_base using (values(1,11)) src(id, v) on v_base.id = src.id
            when matched then update set v = src.v
            when not matched by source then delete;
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from base_t"));
        AreEqual(11, sim.ExecuteScalar("select v from base_t where id = 1"));
    }

    [TestMethod]
    public void RenamedColumns_ViewColumnNamesAreUsedInMerge()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_renamed as select id as pk, v as score from base_t",
            """
            merge into v_renamed using (values(1, 100), (2, 200)) src(pk, score) on v_renamed.pk = src.pk
            when not matched then insert (pk, score) values (src.pk, src.score);
            """);
        AreEqual(100, sim.ExecuteScalar("select v from base_t where id = 1"));
        AreEqual(200, sim.ExecuteScalar("select v from base_t where id = 2"));
    }

    [TestMethod]
    public void ReorderedColumns_BaseColumnOrdinalsTranslate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int, label varchar(20))",
            "create view v_reordered as select label, id, v from base_t",
            """
            merge into v_reordered using (values('foo', 1, 10)) src(label, id, v) on v_reordered.id = src.id
            when not matched then insert (label, id, v) values (src.label, src.id, src.v);
            """);
        AreEqual("foo", sim.ExecuteScalar("select label from base_t where id = 1"));
        AreEqual(10, sim.ExecuteScalar("select v from base_t where id = 1"));
    }

    [TestMethod]
    public void VisibilityFilter_HidesNonMatchingTargetRows()
    {
        // Base row id=2 has active=0, so the view filter excludes it. The
        // MERGE source has both id=1 and id=2; id=2 is invisible to the view
        // so the NMBT branch triggers an INSERT — which conflicts with the
        // hidden row's PK, raising Msg 2627.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int, active bit); insert base_t values (1, 100, 1), (2, 200, 0)",
            "create view v_active as select id, v from base_t where active = 1");
        var ex = sim.AssertSqlError("""
            merge into v_active using (values(1, 11), (2, 22)) src(id, v) on v_active.id = src.id
            when matched then update set v = src.v
            when not matched then insert (id, v) values (src.id, src.v);
            """, 2627);
        Assert.Contains("PRIMARY KEY", ex.Message);
    }

    [TestMethod]
    public void VisibilityFilter_OnlyVisibleRowsParticipateInMatched()
    {
        // Same setup, but a source that only hits the visible row → no
        // conflict; UPDATE applies to id=1 and leaves id=2 untouched.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int, active bit); insert base_t values (1, 100, 1), (2, 200, 0)",
            "create view v_active as select id, v from base_t where active = 1",
            """
            merge into v_active using (values(1, 11)) src(id, v) on v_active.id = src.id
            when matched then update set v = src.v;
            """);
        AreEqual(11, sim.ExecuteScalar("select v from base_t where id = 1"));
        AreEqual(200, sim.ExecuteScalar("select v from base_t where id = 2"));
    }

    [TestMethod]
    public void CheckOption_InsertViolation_RaisesMsg550()
    {
        // CHECK OPTION on v_pos requires v > 0; INSERT of v = -5 violates.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_pos as select id, v from base_t where v > 0 with check option");
        _ = sim.AssertSqlError("""
            merge into v_pos using (values(1, -5)) src(id, v) on v_pos.id = src.id
            when not matched then insert (id, v) values (src.id, src.v);
            """, 550);
    }

    [TestMethod]
    public void CheckOption_UpdateViolation_RaisesMsg550()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int); insert base_t values (1, 10)",
            "create view v_pos as select id, v from base_t where v > 0 with check option");
        _ = sim.AssertSqlError("""
            merge into v_pos using (values(1, -1)) src(id, v) on v_pos.id = src.id
            when matched then update set v = src.v;
            """, 550);
    }

    [TestMethod]
    public void NonUpdatableView_RaisesMsg4405()
    {
        // Multi-base view (join) — not updatable.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table a (id int primary key, v int); create table b (id int primary key, v int)",
            "create view v_join as select a.id, a.v as av, b.v as bv from a join b on a.id = b.id");
        _ = sim.AssertSqlError("""
            merge into v_join using (values(1, 1, 1)) src(id, av, bv) on v_join.id = src.id
            when not matched then insert (id, av, bv) values (src.id, src.av, src.bv);
            """, 4405);
    }

    [TestMethod]
    public void InsteadOfInsertTrigger_FiresInsteadOfHeapWrite()
    {
        // INSTEAD OF INSERT routes through the trigger; the base table sees
        // no writes from the MERGE itself — only what the trigger body does.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create table log_t (id int identity, msg varchar(50))",
            "create view v_base as select id, v from base_t",
            "create trigger tr_v_ins on v_base instead of insert as begin insert into log_t (msg) values ('fired'); insert into base_t select * from inserted; end",
            """
            merge into v_base using (values(1, 10)) src(id, v) on v_base.id = src.id
            when not matched then insert (id, v) values (src.id, src.v);
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from log_t"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from base_t"));
        AreEqual(10, sim.ExecuteScalar("select v from base_t where id = 1"));
    }

    [TestMethod]
    public void InsteadOfUpdateTrigger_FiresInsteadOfHeapWrite()
    {
        // INSTEAD OF UPDATE: the heap row stays put unless the trigger
        // explicitly mutates it; the trigger gets INSERTED/DELETED in
        // view shape.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int); insert base_t values (1, 0)",
            "create table log_t (id int identity, before_v int, after_v int)",
            "create view v_base as select id, v from base_t",
            "create trigger tr_v_upd on v_base instead of update as insert into log_t (before_v, after_v) select d.v, i.v from inserted i join deleted d on i.id = d.id",
            """
            merge into v_base using (values(1, 99)) src(id, v) on v_base.id = src.id
            when matched then update set v = src.v;
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from log_t"));
        AreEqual(0, sim.ExecuteScalar("select before_v from log_t"));
        AreEqual(99, sim.ExecuteScalar("select after_v from log_t"));
        AreEqual(0, sim.ExecuteScalar("select v from base_t where id = 1"));
    }

    [TestMethod]
    public void InsteadOfDeleteTrigger_FiresInsteadOfHeapWrite()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int); insert base_t values (1, 10)",
            "create table log_t (id int identity, msg varchar(50))",
            "create view v_base as select id, v from base_t",
            "create trigger tr_v_del on v_base instead of delete as insert into log_t (msg) values ('deleted')",
            """
            merge into v_base using (values(1, 99)) src(id, v) on v_base.id = src.id
            when matched then delete;
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from log_t"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from base_t"));
    }

    [TestMethod]
    public void DerivedColumn_WriteRejected_Msg4406()
    {
        // View projects a computed column; writes to it raise Msg 4406.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_derived as select id, v, v * 2 as doubled from base_t");
        _ = sim.AssertSqlError("""
            merge into v_derived using (values(1, 10, 20)) src(id, v, doubled) on v_derived.id = src.id
            when not matched then insert (id, v, doubled) values (src.id, src.v, src.doubled);
            """, 4406);
    }

    [TestMethod]
    public void DerivedColumn_OmittedFromInsert_Works()
    {
        // Same view, but the INSERT only writes the non-derived columns.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_derived as select id, v, v * 2 as doubled from base_t",
            """
            merge into v_derived using (values(1, 7)) src(id, v) on v_derived.id = src.id
            when not matched then insert (id, v) values (src.id, src.v);
            """);
        AreEqual(7, sim.ExecuteScalar("select v from base_t where id = 1"));
        AreEqual(14, sim.ExecuteScalar("select doubled from v_derived where id = 1"));
    }

    [TestMethod]
    public void OutputThroughView_RaisesNotSupported()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_base as select id, v from base_t");
        _ = Throws<NotSupportedException>(() => sim.ExecuteNonQuery("""
            merge into v_base using (values(1, 10)) src(id, v) on v_base.id = src.id
            when not matched then insert (id, v) values (src.id, src.v) output inserted.id;
            """));
    }

    [TestMethod]
    public void ViewWithAlias_Works()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table base_t (id int primary key, v int)",
            "create view v_base as select id, v from base_t",
            """
            merge into v_base as v using (values(1, 10)) src(id, v) on v.id = src.id
            when not matched then insert (id, v) values (src.id, src.v);
            """);
        AreEqual(10, sim.ExecuteScalar("select v from base_t where id = 1"));
    }
}
