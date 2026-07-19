using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class OptionalSemicolonTests
{
    [TestMethod]
    public void DeclareThenSelect_NoSeparator_Works()
        => AreEqual(7, ExecuteScalar<int>("declare @v int = 7 select @v"));

    [TestMethod]
    public void SetThenSet_NoSeparator_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("declare @v int, @w int set @v = 1 set @w = 2 select @v + @w");
        AreEqual(3, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void SelectThenSelect_NoSeparator_YieldsTwoResultSets()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("select 1 as a select 2 as b");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public void InsertThenSelect_NoSeparator_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int)").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("insert t values (1) select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void BeginTranThenRollback_NoSeparator_Works()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.ExecuteNonQuery("begin tran insert t values (1) rollback");
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from t"));
    }

    [TestMethod]
    public void CreateInsertSelect_NoSeparators_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(1, conn.CreateCommand(
            "create table t (id int) insert t values (1) select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void NewlineBetweenStatements_NoSeparator_Works()
        => AreEqual(7, ExecuteScalar<int>("declare @v int = 7\nselect @v"));

    [TestMethod]
    public void DoubleSemicolon_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("select 1 as a;;select 2 as b");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public void TrailingSemicolons_Works()
        => AreEqual(1, ExecuteScalar<int>("select 1;;;"));

    [TestMethod]
    public void LeadingSemicolons_Works()
        => AreEqual(1, ExecuteScalar<int>(";;select 1"));

    [TestMethod]
    public void EmptyBatchOfSemicolons_NoOp()
    {
        // SqlClient's ExecuteNonQuery returns -1 when no DML statement ran;
        // a batch of bare `;`s should produce that sentinel rather than
        // throwing.
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(-1, conn.CreateCommand(";;").ExecuteNonQuery());
    }

    [TestMethod]
    public void CteAtBatchStart_NoSemicolonNeeded()
        => AreEqual(1, ExecuteScalar<int>("with cte as (select 1 as x) select x from cte"));

    [TestMethod]
    public void CteAfterSemicolon_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("select 0 as a;with cte as (select 1 as x) select x from cte");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void CteWithoutPrecedingSemicolon_RaisesMsg319()
        => AssertSqlError(
            "select 0 as a with cte as (select 1 as x) select x from cte",
            319,
            "Incorrect syntax near the keyword 'with'. If this statement is a common table expression, an xmlnamespaces clause or a change tracking context clause, the previous statement must be terminated with a semicolon.");

    [TestMethod]
    public void TwoCtesWithoutSemicolon_RaisesMsg319()
    {
        // Even when both statements are CTE-prefixed, the second WITH still
        // requires a `;` to separate it from the prior statement. The error
        // surfaces when the dispatch loop advances to the second WITH —
        // which means the test has to drive iteration past the first result
        // set; ExecuteScalar would short-circuit.
        using var conn = new Simulation().CreateOpenConnection();
        var ex = Throws<System.Data.Common.DbException>(() =>
            conn.CreateCommand(
                "with c1 as (select 1 as x) select x from c1 with c2 as (select 2 as y) select y from c2")
                .ExecuteNonQuery());
        AreEqual("319", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void MergeWithTrailingSemicolon_Works()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dst (id int)");
        _ = sim.ExecuteNonQuery("merge dst using (values (1)) v(x) on 1=0 when not matched then insert (id) values (v.x);");
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from dst"));
    }

    [TestMethod]
    public void MergeWithoutTrailingSemicolon_RaisesMsg10713()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dst (id int)");
        sim.AssertSqlError(
            "merge dst using (values (1)) v(x) on 1=0 when not matched then insert (id) values (v.x)",
            10713,
            "A MERGE statement must be terminated by a semi-colon (;).");
    }

    [TestMethod]
    public void MergeFollowedByAnotherStatementWithoutTerminator_RaisesMsg10713()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dst (id int)");
        _ = sim.AssertSqlError(
            "merge dst using (values (1)) v(x) on 1=0 when not matched then insert (id) values (v.x) select 1",
            10713);
    }

    [TestMethod]
    public void MergeThenSelectWithSemicolon_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table dst (id int)").ExecuteNonQuery();
        AreEqual(
            1,
            conn.CreateCommand(
                "merge dst using (values (1)) v(x) on 1=0 when not matched then insert (id) values (v.x);select count(*) from dst")
            .ExecuteScalar());
    }

    [TestMethod]
    public void DbccFollowedBySelect_NoSeparator_Works()
    {
        // DBCC TRACEON's parser ends on the closing `)` (last consumed) rather
        // than the lookahead. The dispatch loop normalizes by advancing one
        // token when the cursor isn't already at a statement boundary; this
        // test exercises that path.
        AreEqual(1, ExecuteScalar<int>("dbcc traceon(460) select 1"));
    }

    [TestMethod]
    public void AlterDatabaseFollowedBySelect_NoSeparator_Works()
        => AreEqual(2, ExecuteScalar<int>(
            "alter database current set compatibility_level = 160 select 2"));

    [TestMethod]
    public void DeclareSetSelect_NoSeparators_Works()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(15, conn.CreateCommand(
            "declare @x int = 7 set @x = @x + 8 select @x").ExecuteScalar());
    }
}
