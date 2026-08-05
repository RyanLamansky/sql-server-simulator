using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <strong>Msg 422</strong> — a <c>WITH</c> prefix whose statement is a bare
/// <c>SELECT &lt;expression list&gt;</c>. Real's refusal is exactly that narrow:
/// any clause at all on the statement, or a DML target, and the prefix is
/// accepted however many of its CTEs go unread. Probed against SQL Server 2025
/// (2026-08-05).
/// </summary>
[TestClass]
public sealed class UnusedCteTests
{
    private const string Setup = "create table h1 (a int); insert h1 values (1); ";

    private static void Rejects(string statement)
        => new Simulation().AssertSqlError(Setup + statement, 422, "Common table expression defined but not used.");

    private static object? Run(string statement) => new Simulation().ExecuteScalar(Setup + statement);

    [TestMethod]
    public void BareProjection_Msg422()
        => Rejects("with c as (select * from h1) select 1");

    [TestMethod]
    public void SeveralConstantColumns_Msg422()
        => Rejects("with c as (select * from h1) select 1, 2");

    [TestMethod]
    public void FunctionCallProjection_Msg422()
        => Rejects("with c as (select * from h1) select getdate()");

    [TestMethod]
    public void NullProjection_Msg422()
        => Rejects("with c as (select * from h1) select null");

    [TestMethod]
    public void VariableAssignment_Msg422()
        => Rejects("declare @v int; with c as (select * from h1) select @v = 1");

    /// <summary>A CTE reading a CTE doesn't count as a use of the prefix.</summary>
    [TestMethod]
    public void CteReferencedOnlyByAnotherCte_Msg422()
        => Rejects("with c1 as (select * from h1), c2 as (select * from c1) select 1");

    [TestMethod]
    public void RecursiveCte_Msg422()
        => Rejects("with c as (select 1 as n union all select n + 1 from c where n < 3) select 1");

    /// <summary>The CTE body isn't bound at all — a bad table inside it never surfaces.</summary>
    [TestMethod]
    public void SeveralUnusedCtes_Msg422()
        => Rejects("with c1 as (select * from h1), c2 as (select * from h1) select 1");

    // --- Accepted: one CTE read, or any clause on the statement ---

    [TestMethod]
    public void CteRead_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select * from c"));

    [TestMethod]
    public void OneOfTwoCtesRead_Licensed()
        => AreEqual(1, Run("with c1 as (select * from h1), c2 as (select * from h1) select * from c1"));

    [TestMethod]
    public void CteReadThroughAnother_Licensed()
        => AreEqual(1, Run("with c1 as (select * from h1), c2 as (select * from c1) select * from c2"));

    [TestMethod]
    public void WhereClause_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select 1 where 1 = 1"));

    [TestMethod]
    public void OrderBy_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select 1 order by 1"));

    [TestMethod]
    public void Top_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select top 1 1"));

    [TestMethod]
    public void Distinct_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select distinct 1"));

    [TestMethod]
    public void SetOperation_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select 1 union select 2"));

    [TestMethod]
    public void SubqueryInProjection_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select (select max(a) from h1)"));

    [TestMethod]
    public void OptionHint_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select 1 option (maxdop 1)"));

    [TestMethod]
    public void FromClause_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select a from h1"));

    /// <summary>Every DML form accepts the prefix, its own bare-projection source included.</summary>
    [TestMethod]
    public void InsertOverBareProjection_Licensed()
        => AreEqual(2, Run("with c as (select * from h1) insert into h1 select 1; select count(*) from h1"));

    [TestMethod]
    public void Update_Licensed()
        => AreEqual(2, Run("with c as (select * from h1) update h1 set a = 2; select a from h1"));

    [TestMethod]
    public void Delete_Licensed()
        => AreEqual(0, Run("with c as (select * from h1) delete from h1; select count(*) from h1"));

    [TestMethod]
    public void SelectInto_Licensed()
        => AreEqual(1, Run("with c as (select * from h1) select a into h2 from h1; select count(*) from h2"));

    /// <summary>A view body is not a statement the dispatch loop judges.</summary>
    [TestMethod]
    public void ViewBody_Licensed()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Setup, "create view v1 as with c as (select * from h1) select a from h1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from v1"));
    }

    [TestMethod]
    public void ProcedureBody_Msg422()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        var ex = sim.AssertSqlError("create procedure p1 as with c as (select * from h1) select 1", 422);
        Assert.Contains("Common table expression defined but not used.", ex.Message);
    }
}
