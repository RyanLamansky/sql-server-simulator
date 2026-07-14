using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Deferred name resolution in skipped control-flow branches. Real SQL Server
/// binds object / column names lazily, so an un-taken IF / WHILE branch (or a
/// block skipped after BREAK / CONTINUE / RETURN) that references a
/// nonexistent table / column compiles fine and is discarded. The simulator
/// resolves names inline with parsing, so it swallows the Msg 208 / Msg 207
/// that would otherwise surface — but only while dispatching in skip mode.
/// A taken branch still raises, and syntax / structural errors in skipped
/// branches still raise (only name resolution defers).
/// </summary>
[TestClass]
public sealed class SkipModeNameResolutionTests
{
    [TestMethod]
    public void IfFalse_UnknownTable_ThenFollowingStatementsRun()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 begin select * from nosuchtable end select 'ok'"));

    [TestMethod]
    public void IfFalse_UnknownTable_NoBlock()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from nosuchtable select 'ok'"));

    [TestMethod]
    public void IfTrue_UnknownTable_StillRaises208()
        => new Simulation().AssertSqlError("if 1 = 1 select * from nosuchtable", 208);

    [TestMethod]
    public void IfFalse_UnknownColumnInKnownTable_Tolerated()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        AreEqual("ok", sim.ExecuteScalar("if 1 = 0 begin select bad_col from t end select 'ok'"));
    }

    [TestMethod]
    public void IfTrue_UnknownColumn_StillRaises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("if 1 = 1 select bad_col from t", 207);
    }

    [TestMethod]
    public void IfFalse_UnknownDatabaseQualifier_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from nosuchdb.dbo.t select 'ok'"));

    /// <summary>
    /// A malformed FROM (a keyword where a table source is required) is a
    /// structural error, not name resolution — it fires (Msg 102) before
    /// any name lookup, even in a skipped branch.
    /// </summary>
    [TestMethod]
    public void SkippedBranch_SyntaxErrorStillRaises()
        => new Simulation().AssertSqlError("if 1 = 0 select * from select 'ok'", 102);

    [TestMethod]
    public void SkippedBranch_UnknownInsertTarget_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 insert into nosuchtable values (1) select 'ok'"));

    [TestMethod]
    public void SkippedBranch_UnknownUpdateTarget_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 update nosuchtable set x = 1 select 'ok'"));

    [TestMethod]
    public void SkippedBranch_UnknownDeleteTarget_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 delete from nosuchtable select 'ok'"));

    [TestMethod]
    public void WhileFalse_UnknownTable_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "while 1 = 0 begin select * from nosuchtable end select 'ok'"));

    [TestMethod]
    public void ElseBranchSkipped_UnknownTable_Tolerated()
        => AreEqual("taken", new Simulation().ExecuteScalar(
            "if 1 = 1 select 'taken' else select * from nosuchtable"));

    [TestMethod]
    public void NestedIf_InsideSkippedBlock_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 begin if 1 = 1 select * from nosuchtable end select 'ok'"));

    // The taken outer branch runs; the un-taken nested IF's unknown-table
    // reference must not raise, and the nested taken statement executes.
    [TestMethod]
    public void NestedIf_TakenOuter_SkippedInner_Tolerated()
        => AreEqual("inner", new Simulation().ExecuteScalar(
            "if 1 = 1 begin if 1 = 0 select * from nosuchtable select 'inner' end"));

    // Variable declarations are compile-scoped batch-wide (probe-confirmed
    // against SQL Server 2025): a DECLARE in an un-taken branch registers
    // its slot for the whole batch, only the initializer is suppressed, and
    // duplicate names raise Msg 134 even across dead branches. SSMS's
    // server-properties batch relies on this — its Managed-Instance-only
    // block declares variables the following statements assign.
    [TestMethod]
    public void DeclareInSkippedBranch_VariableUsableAfter()
        => AreEqual(5, new Simulation().ExecuteScalar(
            "if 1 = 0 begin declare @x int end set @x = 5 select @x"));

    [TestMethod]
    public void DeclareInSkippedBranch_InitializerSuppressed()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "if 1 = 0 begin declare @y int = 42 end select @y";
        AreEqual(DBNull.Value, command.ExecuteScalar());
    }

    [TestMethod]
    public void DuplicateDeclare_AcrossDeadBranch_StillRaises134()
        => new Simulation().AssertSqlError(
            "declare @z int = 1 if 1 = 0 begin declare @z int end select @z", 134);

    [TestMethod]
    public void TableVariableDeclaredInSkippedBranch_UsableAfter()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "if 1 = 0 begin declare @t table(a int) end insert @t values (3) select a from @t"));

    /// <summary>
    /// Variable-name resolution is NOT deferred (unlike table/column
    /// names) — real SQL Server raises Msg 137 at compile even for a
    /// dead branch.
    /// </summary>
    [TestMethod]
    public void SetUndeclaredVariableInSkippedBranch_StillRaises137()
        => new Simulation().AssertSqlError("if 1 = 0 begin set @never = 1 end select 'ok'", 137);
}
