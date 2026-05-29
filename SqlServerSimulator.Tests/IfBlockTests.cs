using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>IF cond stmt [ELSE stmt]</c> and <c>BEGIN…END</c> compound
/// statements. Covers branch selection (three-valued UNKNOWN → ELSE),
/// dangling-else (binds inner IF), the one-statement-body footgun
/// (<c>IF cond SELECT 1 SELECT 2</c> runs both — the second escapes the IF),
/// Msg 4145 on non-boolean cond, BEGIN disambiguation (TRAN / TRY / block),
/// empty-block Msg 102, batch-wide variable scope inside blocks, @@ROWCOUNT
/// semantics, and the un-taken-branch skip mode (CREATE doesn't fire
/// Msg 2714, DROP doesn't fire Msg 3701, INSERT / UPDATE / DELETE don't
/// mutate). Behavior probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class IfBlockTests
{
    [TestMethod]
    public void IfTrue_RunsThen()
        => AreEqual("taken", new Simulation().ExecuteScalar("if 1=1 select 'taken'"));

    [TestMethod]
    public void IfFalse_NoElse_NoOutput()
        => IsNull(new Simulation().ExecuteScalar("if 1=0 select 'taken'"));

    [TestMethod]
    public void IfFalse_RunsElse()
        => AreEqual("else-branch", new Simulation().ExecuteScalar(
            "if 1=0 select 'then-branch' else select 'else-branch'"));

    [TestMethod]
    public void IfUnknown_GoesToElse()
        => AreEqual("not-taken", new Simulation().ExecuteScalar(
            "if 1 = null select 'taken' else select 'not-taken'"));

    [TestMethod]
    public void IfBoolNull_NoElse_NoOutput()
        => IsNull(new Simulation().ExecuteScalar(
            "declare @v int; if @v = 1 select 'taken'"));

    [TestMethod]
    public void IfParenCond_Works()
        => AreEqual("p", new Simulation().ExecuteScalar("if (1=1) select 'p'"));

    [TestMethod]
    public void IfExistsEmpty_GoesToElse()
        => AreEqual("not-taken", new Simulation().ExecuteScalar(
            "if exists (select 1 where 1=0) select 'taken' else select 'not-taken'"));

    [TestMethod]
    public void IfNotExistsEmpty_GoesToThen()
        => AreEqual("taken", new Simulation().ExecuteScalar(
            "if not exists (select 1 where 1=0) select 'taken' else select 'not-taken'"));

    // Msg 4145: non-boolean cond (value-typed expression where a predicate
    // was expected). Probe-confirmed across bare integers, NULL, strings,
    // and cast-to-bit (bit is NOT considered boolean by SQL Server's static
    // type check).
    [TestMethod]
    public void IfNullLiteral_Msg4145()
        => new Simulation().AssertSqlError("if null select 'x'", 4145);

    [TestMethod]
    public void IfBareInteger_Msg4145()
        => new Simulation().AssertSqlError("if 1 select 'x'", 4145);

    [TestMethod]
    public void IfBareString_Msg4145()
        => new Simulation().AssertSqlError("if 'abc' select 'x'", 4145);

    [TestMethod]
    public void IfBitCast_Msg4145()
        => new Simulation().AssertSqlError("if (cast(null as bit)) select 'x'", 4145);

    /// <summary>
    /// Dangling-else binds to the nearest unmatched IF. Outer cond false,
    /// inner cond true: ELSE belongs to inner, which never runs (outer
    /// skipped everything) → no output.
    /// </summary>
    [TestMethod]
    public void DanglingElse_OuterFalseInnerTrue_BindsInner_NoOutput()
        => IsNull(new Simulation().ExecuteScalar(
            "if 1=0 if 1=1 select 'inner-then' else select 'outer-else'"));

    /// <summary>
    /// Outer cond true, inner cond false: inner-IF runs its ELSE branch.
    /// Result confirms ELSE bound to inner IF (not outer).
    /// </summary>
    [TestMethod]
    public void DanglingElse_OuterTrueInnerFalse_BindsInner_RunsInnerElse()
        => AreEqual("inner-else", new Simulation().ExecuteScalar(
            "if 1=1 if 1=0 select 'inner-then' else select 'inner-else'"));

    [TestMethod]
    public void DanglingElse_BothTrue_BindsInner_RunsInnerThen()
        => AreEqual("inner-then", new Simulation().ExecuteScalar(
            "if 1=1 if 1=1 select 'inner-then' else select 'outer-else'"));

    /// <summary>
    /// IF takes exactly one statement. <c>IF cond SELECT 1 SELECT 2</c>
    /// runs the first SELECT as the IF body and the second SELECT as a
    /// subsequent batch-level statement — runs unconditionally. Famous
    /// T-SQL footgun, probe-confirmed.
    /// </summary>
    [TestMethod]
    public void IfBody_OneStatementOnly_SecondSelectRunsUnconditionally()
    {
        using var reader = new Simulation().ExecuteReader("if 1=0 select 'a' select 'b'");
        IsTrue(reader.Read());
        AreEqual("b", reader.GetString(0));
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void ElseIfCascade_PicksMatchingBranch()
        => AreEqual("b", new Simulation().ExecuteScalar(
            "if 1=0 select 'a' else if 1=1 select 'b' else select 'c'"));

    // ---- BEGIN…END ----

    [TestMethod]
    public void BeginEnd_Empty_Msg102()
        => new Simulation().AssertSqlError("begin end", 102);

    [TestMethod]
    public void BeginEnd_OnlySemicolons_Msg102()
        => new Simulation().AssertSqlError("begin ; end", 102);

    [TestMethod]
    public void BeginEnd_NoSeparators_DispatchesAllStatements()
    {
        using var reader = new Simulation().ExecuteReader("begin select 1 as a select 2 as b end");
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public void BeginEnd_SemicolonSeparated_DispatchesAllStatements()
    {
        using var reader = new Simulation().ExecuteReader("begin select 1 as a; select 2 as b end");
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0));
    }

    /// <summary>
    /// <c>BEGIN</c> followed by <c>TRAN</c> is BEGIN TRANSACTION, not a
    /// block. After the bare <c>BEGIN</c> consumes a transaction start,
    /// the subsequent <c>ROLLBACK</c> runs as a separate batch statement.
    /// </summary>
    [TestMethod]
    public void IfBody_BeginTran_DispatchesAsTransaction()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "if 1=1 begin tran rollback; select @@trancount"));

    /// <summary>
    /// <c>IF cond BEGIN … END</c> wraps the body as a compound block. The
    /// inner <c>BEGIN TRAN</c> is then disambiguated as a transaction
    /// because its next token is TRAN.
    /// </summary>
    [TestMethod]
    public void IfBody_BeginBlockWithTranInside_RunsBlock()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "if 1=1 begin begin tran rollback end; select @@trancount"));

    [TestMethod]
    public void IfBody_NestedBlock_DispatchesInnerStatement()
        => AreEqual("inside-if", new Simulation().ExecuteScalar(
            "begin if 1=1 select 'inside-if' end"));

    [TestMethod]
    public void BeginAtomic_AtBatchTopLevel_DispatchesBody()
    {
        // BEGIN ATOMIC at batch top level (no enclosing CREATE PROCEDURE)
        // is uncommon but legal grammar. The body dispatches like a regular
        // BEGIN…END block; the WITH (...) options block parses-and-discards.
        // Coverage of the natively-compiled-SP shape lives in
        // StoredProcedureTests; this one verifies the dispatcher path
        // outside the procedure context.
        AreEqual(1, new Simulation().ExecuteScalar(
            "begin atomic with (transaction isolation level = snapshot, language = N'us_english') select 1 end"));
    }

    [TestMethod]
    public void BeginDistributedTran_NotSupported()
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "begin distributed tran"));

    // ---- @@ROWCOUNT ----

    [TestMethod]
    public void RowCount_AfterSkippedIfNoElse_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @prime int = 1;
            if 1=0 select 'taken';
            select @@rowcount
            """));

    [TestMethod]
    public void RowCount_AfterTakenIf_ReflectsBranchStatement()
    {
        // ExecuteScalar reads the first row of the first result set. Pipe
        // the IF body's output away first (assign to a local var) so the
        // exposed result is the @@ROWCOUNT we want to assert on.
        using var reader = new Simulation().ExecuteReader("""
            if 1=1 select 'one';
            select @@rowcount
            """);
        IsTrue(reader.Read());
        AreEqual("one", reader.GetString(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void RowCount_AfterElseRan_ReflectsElseStatement()
    {
        using var reader = new Simulation().ExecuteReader("""
            if 1=0 select 'then' else select 'else';
            select @@rowcount
            """);
        IsTrue(reader.Read());
        AreEqual("else", reader.GetString(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void RowCount_AfterSkippedDeclareInBlock_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @prime int = 1;
            if 1=0 begin declare @x int = 5 end;
            select @@rowcount
            """));

    // ---- Variable scope ----

    /// <summary>
    /// <c>DECLARE</c> inside a block is batch-scoped — variable remains
    /// visible after END. Probe-confirmed.
    /// </summary>
    [TestMethod]
    public void Variable_DeclaredInBlock_VisibleOutside()
        => AreEqual(7, new Simulation().ExecuteScalar<int>(
            "begin declare @v int = 7 end; select @v"));

    /// <summary>
    /// <c>DECLARE</c> in an un-taken IF branch never executes, so the
    /// variable isn't bound — reading it raises Msg 137. The skip-mode
    /// gate suppresses both the dict insertion and the Msg 134 duplicate-
    /// check, so subsequent real DECLAREs of the same name still work.
    /// </summary>
    [TestMethod]
    public void Variable_DeclaredInSkippedBranch_NotVisible()
        => new Simulation().AssertSqlError("""
            if 1=0 declare @v int = 7;
            select @v
            """, 137);

    // ---- Un-taken-branch skip mode ----

    /// <summary>
    /// Safe-CREATE idiom: when the destination already exists (cond false),
    /// the un-taken <c>CREATE TABLE</c> doesn't fire Msg 2714.
    /// </summary>
    [TestMethod]
    public void SkippedCreateTable_DoesNotFireDuplicate()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table foo (id int);
            if 1=0 create table foo (id int);
            select count(*) from foo
            """));

    /// <summary>
    /// Safe-DROP idiom: when the target doesn't exist (cond false),
    /// the un-taken <c>DROP TABLE</c> doesn't fire Msg 3701.
    /// </summary>
    [TestMethod]
    public void SkippedDropTable_DoesNotFireMissingTable()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            if 1=0 drop table nonexistent;
            select 1
            """));

    [TestMethod]
    public void SkippedInsert_DoesNotWriteRow()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table t (id int);
            if 1=0 insert t values (1);
            select count(*) from t
            """));

    [TestMethod]
    public void SkippedUpdate_DoesNotMutate()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (id int);
            insert t values (1);
            if 1=0 update t set id = 99;
            select id from t
            """));

    [TestMethod]
    public void SkippedDelete_DoesNotMutate()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (id int);
            insert t values (1);
            if 1=0 delete t;
            select count(*) from t
            """));

    [TestMethod]
    public void SkippedSet_DoesNotMutateVariable()
        => AreEqual(7, new Simulation().ExecuteScalar<int>("""
            declare @v int = 7;
            if 1=0 set @v = 99;
            select @v
            """));

    [TestMethod]
    public void SkippedSelectInto_DoesNotCreateDest()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table src (id int); insert src values (1);
            create table dst (id int);
            if 1=0 select id into dst from src;
            select 1
            """));

    [TestMethod]
    public void SkippedBeginTran_DoesNotIncrementTranCount()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            if 1=0 begin tran;
            select @@trancount
            """));

    [TestMethod]
    public void SkippedCommit_DoesNotFireNoBegin()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            if 1=0 commit;
            select 1
            """));

    [TestMethod]
    public void SkippedRollback_DoesNotFireNoBegin()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            if 1=0 rollback;
            select 1
            """));

    /// <summary>
    /// Nested skip propagates: outer IF is false, inner IF inside an un-
    /// taken block also skips its branches without evaluating cond
    /// (so a cond that would raise an error doesn't).
    /// </summary>
    [TestMethod]
    public void NestedSkip_InnerCondNotEvaluated()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            if 1=0 begin
                if 1/0 = 0 select 'inner';
            end;
            select 1
            """));

    /// <summary>
    /// Cond eval errors propagate when the IF is reached. Divide-by-zero in a
    /// cond surfaces as Msg 8134, matching SQL Server.
    /// </summary>
    [TestMethod]
    public void CondEvalError_PropagatesAsRuntimeError()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery(
            "if 1/0 = 0 select 'ran'"));
        AreEqual(8134, ex.Number);
    }

    /// <summary>
    /// A taken branch propagates errors normally — a CHECK violation in the
    /// IF body raises Msg 547 and aborts the batch.
    /// </summary>
    [TestMethod]
    public void TakenBranch_ErrorsPropagate()
        => new Simulation().AssertSqlError("""
            create table t (id int check (id > 0));
            if 1=1 insert t values (-1)
            """, 547);
}
