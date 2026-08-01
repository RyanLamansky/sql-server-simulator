using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>WITH cte AS (…) MERGE INTO … USING cte …</c> — the
/// CTE-precedes-MERGE shape where the bare CTE name is the source. Real SQL
/// Server (probe-confirmed 2026-05-19) accepts the bare-name form with or
/// without an alias; rejects the related but invalid shape
/// <c>MERGE … USING (WITH cte AS …)</c> at parse with Msg 156 — the CTE
/// can't live inside the <c>USING (…)</c> parens, which the simulator
/// mirrors.
/// </summary>
[TestClass]
public sealed class MergeCteTests
{
    [TestMethod]
    public void CtePrecedingMerge_BareName_InsertsThroughCte()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int, v int);
            insert src values (1, 10), (2, 20);
            with c as (select id, v from src)
            merge into tgt using c on tgt.id = c.id
            when matched then update set v = c.v
            when not matched then insert (id, v) values (c.id, c.v);
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from tgt"));
        AreEqual(10, sim.ExecuteScalar("select v from tgt where id = 1"));
        AreEqual(20, sim.ExecuteScalar("select v from tgt where id = 2"));
    }

    [TestMethod]
    public void CtePrecedingMerge_BareNameWithAlias_Works()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int, v int);
            insert src values (1, 10);
            with c as (select id, v from src)
            merge into tgt using c as s on tgt.id = s.id
            when not matched then insert (id, v) values (s.id, s.v);
            """);
        AreEqual(10, sim.ExecuteScalar("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void CtePrecedingMerge_UpdateMatched_Works()
    {
        // Mixed insert + update via CTE source.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            insert tgt values (1, 0);
            create table src (id int, v int);
            insert src values (1, 100), (2, 200);
            with c as (select id, v from src)
            merge into tgt using c on tgt.id = c.id
            when matched then update set v = c.v
            when not matched then insert (id, v) values (c.id, c.v);
            """);
        AreEqual(100, sim.ExecuteScalar("select v from tgt where id = 1"));
        AreEqual(200, sim.ExecuteScalar("select v from tgt where id = 2"));
    }

    [TestMethod]
    public void CtePrecedingMerge_CteWithComputedColumns_PreservesProjection()
    {
        // CTE projects a computed value; MERGE references it via the CTE's
        // column name.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, doubled int);
            create table src (id int, v int);
            insert src values (1, 5), (2, 7);
            with c as (select id, v * 2 as doubled from src)
            merge into tgt using c on tgt.id = c.id
            when not matched then insert (id, doubled) values (c.id, c.doubled);
            """);
        AreEqual(10, sim.ExecuteScalar("select doubled from tgt where id = 1"));
        AreEqual(14, sim.ExecuteScalar("select doubled from tgt where id = 2"));
    }

    [TestMethod]
    public void CteInsideUsingParens_Raises156()
    {
        // The MERGE source is a table source, and no parenthesized query
        // position accepts a CTE prefix — real answers Msg 156 (followed by
        // Msg 319 and Msg 102, of which the simulator raises the first).
        var ex = new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            create table src (id int, v int);
            merge into tgt using (with c as (select id, v from src) select * from c) s on tgt.id = s.id
            when not matched then insert (id, v) values (s.id, s.v);
            """, 156);
        AreEqual("Incorrect syntax near the keyword 'with'.", ex.Message);
    }

    [TestMethod]
    public void CtePrecedingMerge_ShadowsRealTableOfSameName()
    {
        // Real table `c` with different shape exists; the CTE binding takes
        // precedence in the MERGE source resolution (same shadowing semantic
        // SELECT has via Selection.cs's CTE-first lookup).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table c (other_col varchar(50));
            insert c values ('should not appear');
            with c as (select 1 as id, 10 as v)
            merge into tgt using c on tgt.id = c.id
            when not matched then insert (id, v) values (c.id, c.v);
            """);
        AreEqual(10, sim.ExecuteScalar("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void SubqueryReferencingCte_Works_BaselineFromAuditDontRegress()
    {
        // B2 from the audit probe — this already worked before the B1 fix;
        // pin it so a regression there surfaces clearly.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int, v int);
            insert src values (1, 10);
            with c as (select id, v from src)
            merge into tgt using (select id, v from c) s on tgt.id = s.id
            when not matched then insert (id, v) values (s.id, s.v);
            """);
        AreEqual(10, sim.ExecuteScalar("select v from tgt where id = 1"));
    }
}
