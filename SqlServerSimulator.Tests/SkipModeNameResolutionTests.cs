using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Deferred name resolution in skipped control-flow branches. Real SQL Server
/// binds base object names lazily, so an un-taken IF / WHILE branch (or a block
/// skipped after BREAK / CONTINUE / RETURN) that references a nonexistent table
/// / view / function compiles fine and is discarded. The simulator resolves
/// names inline with parsing, so a skip-mode FROM / function-call miss
/// substitutes placeholder metadata and the statement parses to completion (no
/// Msg 208 / 4121). Deferral is scoped to the missing base object, though: a
/// missing column on a <em>resolvable</em> table binds eagerly and raises
/// Msg 207 even in a dead branch (probe-confirmed against SQL Server 2025), and
/// a taken branch still raises. Syntax / structural errors in skipped branches
/// still raise (only name resolution of a missing object defers).
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

    /// <summary>
    /// A missing column on a <em>resolvable</em> table is not deferred — real
    /// SQL Server binds an existing table's columns at compile time and raises
    /// Msg 207 even from an un-taken branch (probe-confirmed SQL Server 2025,
    /// 2026-07-17). Contrast <see cref="IfFalse_UnknownTable_NoBlock"/>, where
    /// the missing base object defers.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownColumnInKnownTable_StillRaises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("if 1 = 0 begin select bad_col from t end select 'ok'", 207);
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

    // ---- Placeholder parse-continuation (probe matrix, 2026-07-17) ----

    /// <summary>
    /// A missing schema-qualified scalar function in an un-taken branch defers
    /// (probe-confirmed: real SQL Server binds user functions lazily too, unlike
    /// a bare 1-part call which is a compile-time Msg 195). The call parses to
    /// completion and is discarded.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownQualifiedFunction_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select dbo.no_such_fn(1, 2) select 'ok'"));

    /// <summary>
    /// A bare (1-part) unresolved function is NOT deferred — real SQL Server
    /// treats it as a missing built-in and raises Msg 195 at compile time, even
    /// in a dead branch.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownBareFunction_StillRaises195()
        => new Simulation().AssertSqlError("if 1 = 0 select no_such_fn(1) select 'ok'", 195);

    /// <summary>
    /// The un-taken THEN branch defers, so its trailing ELSE runs — the missing
    /// function must not orphan the ELSE into a spurious Msg 102.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownFunctionThenElse_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select dbo.no_such_fn(1) as r else select 'else' as r"));

    /// <summary>
    /// The SSMS Query Store / server-properties shape: a missing table behind an
    /// EXISTS in an un-taken outer branch. Regression for the orphaned-fragment
    /// cascade — the recovery scan used to abandon this mid-parse and re-dispatch
    /// the inner branch as a bare statement (spurious Msg 102 / 156).
    /// </summary>
    [TestMethod]
    public void SkippedOuter_ExistsMissingTableWithInnerElse_Tolerated()
        => AreEqual("after", new Simulation().ExecuteScalar("""
            if 1 = 0 begin if exists(select * from missing) select 1 as r else select 2 as r end
            select 'after' as r
            """));

    /// <summary>
    /// The same EXISTS-behind-a-missing-table shape at top level: the un-taken
    /// THEN's inner IF/ELSE parses to completion and the following statement
    /// runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_ExistsMissingTableWithElse_Tolerated()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "if 1 = 0 if exists(select * from missing) select 1 as r else select 2 as r select 'after' as r"));

    /// <summary>
    /// A missing table plus a second missing table inside a scalar subquery,
    /// both in the un-taken THEN — the whole statement defers, the ELSE runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_MissingTableWithMissingSubqueryTable_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from missing where a = (select b from other) else select 'else' as r"));

    /// <summary>
    /// A missing table behind a CASE WHEN EXISTS in a skipped block, with the
    /// block's own ELSE — parses to completion and the ELSE runs.
    /// </summary>
    [TestMethod]
    public void SkippedBlock_CaseWhenExistsMissing_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar("""
            if 1 = 0 begin select case when exists(select * from missing) then 1 else 2 end as r end
            else select 'else' as r
            """));

    /// <summary>
    /// A missing TVF invoked in the FROM clause of an un-taken branch defers
    /// (the argument list is parsed and discarded); the ELSE runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_MissingTvfInFrom_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from dbo.no_such_tvf(1, 2) else select 'else' as r"));

    /// <summary>
    /// ORDER BY referencing a column of a missing table defers along with the
    /// table — no compile error in the dead branch.
    /// </summary>
    [TestMethod]
    public void SkippedIf_MissingTableOrderByMissingColumn_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from missing order by also_missing select 'ok'"));

    /// <summary>
    /// ORDER BY referencing a missing column of a <em>resolvable</em> table is
    /// not deferred — Msg 207 fires even in the dead branch.
    /// </summary>
    [TestMethod]
    public void SkippedIf_KnownTableOrderByMissingColumn_StillRaises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("if 1 = 0 select id from t order by nope select 'ok'", 207);
    }

    /// <summary>
    /// A missing table in one statement of a skipped block plus a missing column
    /// on a resolvable table in the next: real SQL Server binds the resolvable
    /// table's columns eagerly, so Msg 207 wins and aborts the batch — the
    /// block's ELSE never runs.
    /// </summary>
    [TestMethod]
    public void SkippedBlock_MissingTableThenBadColumnOnRealTable_Raises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("""
            if 1 = 0 begin select * from missing; select bad_col from t end
            else select 'else' as r
            select 'after' as r
            """, 207);
    }

    /// <summary>
    /// A qualified column reference against a placeholder (missing) table
    /// resolves leniently, so the whole SELECT parses to completion and the ELSE
    /// runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_QualifiedColumnsOnMissingTable_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select m.foo, m.bar from missing m else select 'else' as r"));
}
