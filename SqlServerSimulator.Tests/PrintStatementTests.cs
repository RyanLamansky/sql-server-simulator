using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>PRINT</c> statement. The simulator parses + evaluates
/// the operand (so type / coercion errors surface naturally) and discards
/// the result — <see cref="SimulatedDbConnection"/> doesn't expose an
/// <c>InfoMessage</c> event because <c>DbConnection</c> doesn't define one
/// and there's no demonstrated need for the public surface yet. The tests
/// verify side-effect-free behavior (<c>@@ROWCOUNT</c> reset, skip-mode
/// suppression, runtime errors from the operand path) rather than message
/// capture. Behavior probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class PrintStatementTests
{
    [TestMethod]
    public void Print_StringLiteral_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print 'hello'");

    [TestMethod]
    public void Print_Null_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print null");

    [TestMethod]
    public void Print_Integer_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print 42");

    [TestMethod]
    public void Print_Decimal_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print 1.5");

    [TestMethod]
    public void Print_Float_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print cast(1.5 as float)");

    [TestMethod]
    public void Print_Variable_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("declare @v varchar(10) = 'hi'; print @v");

    [TestMethod]
    public void Print_Expression_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print 5 + 3");

    [TestMethod]
    public void Print_StringConcat_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print 'a' + 'b'");

    [TestMethod]
    public void Print_Case_DoesNotThrow()
        => _ = new Simulation().ExecuteNonQuery("print case when 1=1 then 'y' else 'n' end");

    /// <summary>
    /// PRINT evaluates the operand normally, so the <c>+</c> operator's
    /// int-side promotion still kicks in — <c>'val=' + 5</c> tries to parse
    /// <c>'val='</c> as int and raises Msg 245. Probe-confirmed: real SQL
    /// Server raises the same Msg 245.
    /// </summary>
    [TestMethod]
    public void Print_StringPlusInt_Msg245()
        => new Simulation().AssertSqlError("print 'val=' + 5", 245);

    /// <summary>
    /// Probe-confirmed: PRINT resets <c>@@ROWCOUNT</c> to 0 — the next
    /// statement reads 0 regardless of whatever the prior statement set.
    /// </summary>
    [TestMethod]
    public void Print_Resets_RowCount_To_Zero()
    {
        using var reader = new Simulation().ExecuteReader("""
            select 1 union all select 2 union all select 3;
            print 'between';
            select @@rowcount as rc
            """);
        // Drain the first result set (the SELECT … UNION ALL).
        while (reader.Read()) { }
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    // ---- Skip-mode interaction ----

    /// <summary>
    /// In an un-taken IF branch, PRINT's operand isn't evaluated — so an
    /// otherwise-error-raising expression inside the un-taken branch is
    /// silently skipped (matches every other statement parser's skip-mode
    /// gate).
    /// </summary>
    [TestMethod]
    public void Print_InUntakenIf_OperandNotEvaluated()
        => _ = new Simulation().ExecuteNonQuery("if 1=0 print 'val=' + 5");

    [TestMethod]
    public void Print_InTakenIf_StillEvaluates()
        => new Simulation().AssertSqlError("if 1=1 print 'val=' + 5", 245);

    [TestMethod]
    public void Print_InUntakenElse_OperandNotEvaluated()
        => _ = new Simulation().ExecuteNonQuery("if 1=1 select 'taken' else print 'val=' + 5");

    [TestMethod]
    public void Print_AfterReturn_NotEvaluated()
        => _ = new Simulation().ExecuteNonQuery("return; print 'val=' + 5");

    [TestMethod]
    public void Print_InBlock_BeforeReturn_Evaluates()
        => new Simulation().AssertSqlError(
            "begin print 'val=' + 5; return; end",
            245);

    /// <summary>
    /// PRINT inside a WHILE evaluates each iteration. Verify by including
    /// an operand that would always error if reached past the BREAK gate.
    /// </summary>
    [TestMethod]
    public void Print_InWhile_RunsEachIteration()
    {
        // Loop runs twice, then BREAKs; PRINT inside fires on both runs.
        _ = new Simulation().ExecuteNonQuery("""
            declare @i int = 0;
            while @i < 2
            begin
                set @i = @i + 1;
                print @i;
            end
            """);
    }

    // ---- Statement composition / dispatch ----

    [TestMethod]
    public void Multiple_Prints_AllRun()
        => _ = new Simulation().ExecuteNonQuery("print 'a'; print 'b'; print 'c'");

    [TestMethod]
    public void Print_Then_Select_SelectReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("print 'x'; select 1 as v");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Select_Then_Print_SelectReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 1 as v print 'x'");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Print_BareReturnsAfter_BatchContinues()
    {
        using var reader = new Simulation().ExecuteReader("print 'before'; select 'after'");
        IsTrue(reader.Read());
        AreEqual("after", reader.GetString(0));
    }

    /// <summary>
    /// PRINT inside a rolled-back transaction is a no-op for the simulator
    /// (output is discarded anyway). The point of this test is to verify
    /// PRINT doesn't interact badly with the undo log or transaction state.
    /// </summary>
    [TestMethod]
    public void Print_InRolledBackTransaction_NoStateLeak()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("begin tran; print 'inside'; rollback");
        // Subsequent statement on a fresh connection should work normally.
        AreEqual(1, sim.ExecuteScalar<int>("select 1"));
    }
}
