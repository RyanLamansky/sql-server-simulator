using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the bare <c>RETURN</c> statement (the value-form
/// <c>RETURN N</c> is reserved for stored procedures / functions, neither
/// of which is modeled — value form always raises Msg 178). Covers
/// batch-exit semantics, propagation through IF / BEGIN…END / WHILE,
/// the compile-time Msg 178 check (fires even in un-taken IF, same
/// pattern as BREAK Msg 135), and skip-mode interactions. Behavior
/// probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class ReturnStatementTests
{
    [TestMethod]
    public void BareReturn_ExitsBatch()
    {
        using var reader = new Simulation().ExecuteReader("select 'before'; return; select 'after'");
        IsTrue(reader.Read());
        AreEqual("before", reader.GetString(0));
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void BareReturn_AtEndOfBatch_Clean()
    {
        // ExecuteNonQuery returns -1 when no DML / DDL ran; the assertion
        // is just that the batch completed without throwing.
        _ = new Simulation().ExecuteNonQuery("return");
    }

    [TestMethod]
    public void ReturnInTakenIf_ExitsBatch()
    {
        using var reader = new Simulation().ExecuteReader("if 1=1 return; select 'after'");
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void ReturnInUntakenIf_StatementAfterRuns()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "if 1=0 return; select 'after'"));

    [TestMethod]
    public void ReturnInElseBranch_ExitsBatch()
    {
        using var reader = new Simulation().ExecuteReader(
            "if 1=0 select 'then' else return; select 'after'");
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    /// <summary>
    /// Probe-confirmed: RETURN inside a WHILE exits the entire batch, not
    /// just the loop (unlike BREAK).
    /// </summary>
    [TestMethod]
    public void ReturnInWhile_ExitsBatchNotJustLoop()
    {
        using var reader = new Simulation().ExecuteReader("""
            declare @i int = 0;
            while @i < 100
            begin
                set @i = @i + 1;
                if @i = 3 return;
            end;
            select 'after'
            """);
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void ReturnInNestedWhile_ExitsAllLoops()
    {
        using var reader = new Simulation().ExecuteReader("""
            while 1=1
            begin
                while 1=1
                begin
                    return;
                end;
            end;
            select 'after'
            """);
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void ReturnInBlock_SkipsSiblings()
    {
        using var reader = new Simulation().ExecuteReader("""
            begin
                select 'a';
                return;
                select 'b';
            end;
            select 'after'
            """);
        IsTrue(reader.Read());
        AreEqual("a", reader.GetString(0));
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void MultipleReturn_FirstWins()
    {
        _ = new Simulation().ExecuteNonQuery("return; return; return");
    }

    /// <summary>
    /// Bare RETURN followed by a statement-starting keyword is bare RETURN
    /// — the SELECT begins a new statement (which gets abandoned because
    /// the batch is exiting). Probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void ReturnFollowedByStatementKeyword_IsBareReturn()
    {
        // SELECT 1 never runs (batch exits via RETURN); we just verify the
        // batch parses + completes without throwing Msg 178.
        using var reader = new Simulation().ExecuteReader("return select 1");
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    // ---- Msg 178: value-form RETURN ----

    [TestMethod]
    public void ReturnInteger_Msg178()
        => new Simulation().AssertSqlError("return 5", 178,
            "A RETURN statement with a return value cannot be used in this context.");

    [TestMethod]
    public void ReturnZero_Msg178()
        => new Simulation().AssertSqlError("return 0", 178);

    [TestMethod]
    public void ReturnNull_Msg178()
        => new Simulation().AssertSqlError("return null", 178);

    [TestMethod]
    public void ReturnString_Msg178()
        => new Simulation().AssertSqlError("return 'abc'", 178);

    [TestMethod]
    public void ReturnVariable_Msg178()
        => new Simulation().AssertSqlError("declare @v int = 7; return @v", 178);

    [TestMethod]
    public void ReturnExpression_Msg178()
        => new Simulation().AssertSqlError("return (1+2)", 178);

    [TestMethod]
    public void ReturnParenWrapped_Msg178()
        => new Simulation().AssertSqlError("return(1)", 178);

    /// <summary>
    /// Compile-time check: Msg 178 fires even when the RETURN is in an
    /// un-taken IF branch. Probe-confirmed — same semantics as BREAK Msg 135.
    /// </summary>
    [TestMethod]
    public void ReturnValueInUntakenIf_StillMsg178()
        => new Simulation().AssertSqlError("if 1=0 return 5", 178);

    [TestMethod]
    public void ReturnValueInUntakenIf_BeforeFollowingStmt_StillMsg178()
        => new Simulation().AssertSqlError("if 1=0 return 5; select 'ok'", 178);
}
