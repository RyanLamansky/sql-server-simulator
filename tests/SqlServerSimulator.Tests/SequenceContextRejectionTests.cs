using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Where <c>NEXT VALUE FOR</c> is refused as a <b>nested query or stored
/// expression</b> — Msg 11719, real's own list: a derived table, a common table
/// expression, a subquery, an <c>APPLY</c> body, a view or user-defined-function
/// body. Real settles all of it while parsing, so the batch is rejected and the
/// sequence is left exactly where it stood; the positions that stay legal (a
/// bare projection, a <c>VALUES</c> tuple, a column <c>DEFAULT</c>, a stored
/// procedure's own statements, and a joined <c>UPDATE</c> / <c>DELETE</c>'s FROM
/// clause) are pinned here too.
/// <para>
/// Probe citations (<c>N2.nn</c> / <c>N2b.nn</c>) are from the matrix run
/// against SQL Server 2025 on 2026-08-05.
/// </para>
/// </summary>
[TestClass]
public sealed class SequenceContextRejectionTests
{
    private static Simulation WithSequence()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table n (id int not null primary key, v int null);
            insert n (id) values (1), (2), (3);
            """);
        sim.ExecuteBatches("create sequence dbo.s as int start with 1 increment by 1");
        return sim;
    }

    /// <summary>Raises Msg 11719 at severity 15 <em>and</em> leaves the sequence unadvanced — the state leak is half of what the refusal prevents.</summary>
    private static void Msg11719(Simulation sim, string sql)
    {
        Contains("NEXT VALUE FOR function is not allowed in check constraints", sim.AssertSqlError(sql, 11719).Message);
        // `last_used_value` is NULL until something is emitted, which is the
        // only reading that distinguishes "never drawn" from "drew the start
        // value" (`current_value` reports 1 either way — see sequences.md).
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sequences where name = 's' and last_used_value is null"));
    }

    // ---- the nested-query family (Msg 11719) ------------------------------

    /// <summary>N2.02.</summary>
    [TestMethod]
    public void DerivedTable_IsMsg11719()
        => Msg11719(WithSequence(), "select * from (select next value for dbo.s as v) d");

    /// <summary>N2.03 — a derived table over a real FROM source.</summary>
    [TestMethod]
    public void DerivedTableOverASource_IsMsg11719()
        => Msg11719(WithSequence(), "select * from (select id, next value for dbo.s as v from n) d");

    /// <summary>N2.04.</summary>
    [TestMethod]
    public void CommonTableExpression_IsMsg11719()
        => Msg11719(WithSequence(), "with c as (select next value for dbo.s as v) select * from c");

    /// <summary>N2.05 — a scalar subquery in the select list.</summary>
    [TestMethod]
    public void ScalarSubquery_IsMsg11719()
        => Msg11719(WithSequence(), "select (select next value for dbo.s) as v");

    /// <summary>N2.06 — a subquery in the WHERE, which real reports as the nested case rather than the clause one.</summary>
    [TestMethod]
    public void SubqueryInWhere_IsMsg11719()
        => Msg11719(WithSequence(), "select id from n where id = (select next value for dbo.s)");

    /// <summary>N2.29 — an EXISTS body.</summary>
    [TestMethod]
    public void ExistsSubquery_IsMsg11719()
        => Msg11719(WithSequence(), "select id from n where exists (select next value for dbo.s)");

    /// <summary>N2.28 — an APPLY body.</summary>
    [TestMethod]
    public void ApplyBody_IsMsg11719()
        => Msg11719(WithSequence(), "select t.id, x.v from n t cross apply (select next value for dbo.s as v) x");

    /// <summary>N2.30 — the derived table an INSERT … SELECT reads.</summary>
    [TestMethod]
    public void InsertSelectFromADerivedTable_IsMsg11719()
        => Msg11719(WithSequence(), "insert into n (id) select v from (select next value for dbo.s as v) d");

    /// <summary>N2.32 — a MERGE's USING source.</summary>
    [TestMethod]
    public void MergeUsingADerivedTable_IsMsg11719()
        => Msg11719(
            WithSequence(),
            "merge n as tgt using (select next value for dbo.s as v) src on tgt.id = src.v when matched then update set tgt.id = tgt.id;");

    /// <summary>N2b.04 — a SELECT … INTO reading the same derived table.</summary>
    [TestMethod]
    public void SelectIntoFromADerivedTable_IsMsg11719()
        => Msg11719(WithSequence(), "select v into #w from (select next value for dbo.s as v) d");

    /// <summary>N2.35 — the <c>OVER</c> form inside a derived table refuses on the nesting, not the OVER.</summary>
    [TestMethod]
    public void OverFormInsideADerivedTable_IsMsg11719()
        => Msg11719(WithSequence(), "select * from (select next value for dbo.s over (order by id) as v from n) d");

    // ---- module bodies, refused at CREATE ---------------------------------

    /// <summary>N2.18 — a view body, refused at <c>CREATE</c> so no view is left behind.</summary>
    [TestMethod]
    public void ViewBody_IsMsg11719AtCreate()
    {
        var sim = WithSequence();
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteBatches("create view dbo.v as select next value for dbo.s as n"));
        AreEqual(11719, ex.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.views where name = 'v'"));
    }

    /// <summary>N2.19 — a scalar UDF body.</summary>
    [TestMethod]
    public void ScalarFunctionBody_IsMsg11719AtCreate()
    {
        var sim = WithSequence();
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteBatches("create function dbo.f () returns int as begin return next value for dbo.s; end"));
        AreEqual(11719, ex.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.objects where name = 'f'"));
    }

    /// <summary>N2.20 — an inline TVF body.</summary>
    [TestMethod]
    public void InlineTvfBody_IsMsg11719AtCreate()
    {
        var sim = WithSequence();
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteBatches("create function dbo.itvf () returns table as return (select next value for dbo.s as n)"));
        AreEqual(11719, ex.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.objects where name = 'itvf'"));
    }

    /// <summary>N2.21 — a multi-statement TVF body.</summary>
    [TestMethod]
    public void MultiStatementTvfBody_IsMsg11719AtCreate()
    {
        var sim = WithSequence();
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteBatches(
            "create function dbo.mtvf () returns @r table (n int) as begin insert into @r values (next value for dbo.s); return; end"));
        AreEqual(11719, ex.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.objects where name = 'mtvf'"));
    }

    /// <summary>N2.26 — a procedure body is legal, but a derived table <em>inside</em> one still isn't.</summary>
    [TestMethod]
    public void DerivedTableInsideAProcedureBody_IsMsg11719AtCreate()
    {
        var sim = WithSequence();
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteBatches("create procedure dbo.p as select * from (select next value for dbo.s as v) d"));
        AreEqual(11719, ex.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.objects where name = 'p'"));
    }

    // ---- what stays legal -------------------------------------------------

    /// <summary>N2.01 — a bare projection.</summary>
    [TestMethod]
    public void BareProjection_Answers()
        => AreEqual(1, WithSequence().ExecuteScalar("select next value for dbo.s"));

    /// <summary>N2.24 — a column DEFAULT.</summary>
    [TestMethod]
    public void ColumnDefault_Answers()
    {
        var sim = WithSequence();
        _ = sim.ExecuteNonQuery("create table d (a int default (next value for dbo.s), b int); insert d (b) values (1);");
        AreEqual(1, sim.ExecuteScalar("select a from d"));
    }

    /// <summary>N2.25 — a stored procedure's own statement, drawing a value per call.</summary>
    [TestMethod]
    public void ProcedureBody_Answers()
    {
        var sim = WithSequence();
        sim.ExecuteBatches("create procedure dbo.p as select next value for dbo.s as n");
        AreEqual(1, sim.ExecuteScalar("exec dbo.p"));
        AreEqual(2, sim.ExecuteScalar("exec dbo.p"));
    }

    /// <summary>
    /// N2b.02 — real exempts a joined <c>UPDATE</c>'s own FROM-clause derived
    /// table, where the identical derived table under a SELECT is refused.
    /// </summary>
    [TestMethod]
    public void JoinedUpdateFromClauseDerivedTable_Answers()
    {
        var sim = WithSequence();
        _ = sim.ExecuteNonQuery("update u set u.v = d.v from n u join (select next value for dbo.s as v) d on 1 = 1");
        AreEqual(1, sim.ExecuteScalar("select cast(last_used_value as int) from sys.sequences where name = 's'"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from n where v = 1"));
    }

    /// <summary>N2b.03 — and a joined DELETE's.</summary>
    [TestMethod]
    public void JoinedDeleteFromClauseDerivedTable_Answers()
    {
        var sim = WithSequence();
        _ = sim.ExecuteNonQuery("delete u from n u join (select next value for dbo.s as v) d on u.id = d.v");
        AreEqual(2, sim.ExecuteScalar("select count(*) from n"));
    }
}
