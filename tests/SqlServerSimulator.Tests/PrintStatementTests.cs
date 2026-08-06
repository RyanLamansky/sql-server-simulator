using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>PRINT</c> statement's evaluation semantics: the operand
/// parses + evaluates (so type / coercion errors surface naturally), with
/// side-effect-free behavior (<c>@@ROWCOUNT</c> reset, skip-mode
/// suppression, runtime errors from the operand path). Message delivery
/// through <see cref="SimulatedDbConnection.InfoMessage"/> is covered by
/// <c>InfoMessageEventTests</c>. Behavior probed against SQL Server 2025
/// (2026-05-11).
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

    /// <summary>
    /// The formatted message <c>PRINT</c> delivers, captured through the
    /// public <see cref="SimulatedDbConnection.InfoMessage"/> surface.
    /// </summary>
    private static string PrintOutput(string commandText)
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        var captured = new List<string>();
        connection.InfoMessage += (_, e) => captured.Add(e.Message);
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = command.ExecuteNonQuery();
        return string.Join("\n", captured);
    }

    /// <summary>
    /// PRINT renders a non-string operand through the implicit conversion to a
    /// character string, which is style 0's layout for the legacy datetime
    /// family and for <c>float</c> / <c>real</c>. Every expected rendering is
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("print 1", "1")]
    [DataRow("print 1.5", "1.5")]
    [DataRow("print cast(1 as smallint)", "1")]
    [DataRow("print cast(1 as bigint)", "1")]
    [DataRow("print cast(1 as bit)", "1")]
    [DataRow("print cast(1 as money)", "1.00")]
    [DataRow("print cast(1.5 as money)", "1.50")]
    [DataRow("print cast(12345.6789 as decimal(12, 4))", "12345.6789")]
    [DataRow("print cast(1.5 as float)", "1.5")]
    [DataRow("print cast(1.5 as real)", "1.5")]
    [DataRow("print cast(1234567890123456789 as float)", "1.23457e+018")]
    [DataRow("print cast(0.000001234 as float)", "1.234e-006")]
    [DataRow("print cast(1000000 as float)", "1e+006")]
    [DataRow("print cast(123456 as float)", "123456")]
    [DataRow("print 0x41424344", "0x41424344")]
    [DataRow("print cast('2026-08-06 13:45:12.345' as datetime)", "Aug  6 2026  1:45PM")]
    [DataRow("print cast('2026-08-06 13:45' as smalldatetime)", "Aug  6 2026  1:45PM")]
    [DataRow("print cast('2026-08-06 13:45:12.345' as datetime2(3))", "2026-08-06 13:45:12.345")]
    [DataRow("print cast('2026-08-06' as date)", "2026-08-06")]
    [DataRow("print cast('13:45:12.1234567' as time)", "13:45:12.1234567")]
    [DataRow("print cast('2026-08-06 13:45:12.345 +05:30' as datetimeoffset)", "2026-08-06 13:45:12.3450000 +05:30")]
    public void Print_FormatsOperand(string commandText, string expected) =>
        AreEqual(expected, PrintOutput(commandText));

    /// <summary>
    /// A NULL operand and an empty string both deliver a single space, not an
    /// empty message (probe-confirmed: the message is exactly U+0020).
    /// </summary>
    [TestMethod]
    [DataRow("print null")]
    [DataRow("print ''")]
    [DataRow("print N''")]
    [DataRow("declare @v varchar(10) = null; print @v")]
    public void Print_NullOrEmpty_EmitsSingleSpace(string commandText) =>
        AreEqual(" ", PrintOutput(commandText));

    /// <summary>
    /// The message truncates at 8000 characters, or 4000 for the national
    /// string types — probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("declare @s varchar(max) = replicate(cast('a' as varchar(max)), 8010); print @s", 8000)]
    [DataRow("declare @s nvarchar(max) = replicate(cast(N'b' as nvarchar(max)), 4010); print @s", 4000)]
    public void Print_TruncatesAtFamilyCap(string commandText, int expectedLength) =>
        AreEqual(expectedLength, PrintOutput(commandText).Length);

    /// <summary>
    /// The operand has no column scope, so a name — bare, bracketed or dotted —
    /// is real's own Msg 128 rather than the Msg 207 / 4104 a query scope would
    /// report.
    /// </summary>
    [TestMethod]
    [DataRow("print some_identifier", "some_identifier")]
    [DataRow("print [foo]", "foo")]
    [DataRow("print a.b", "a.b")]
    [DataRow("print upper(zzz)", "zzz")]
    [DataRow("print aaa + bbb", "aaa")]
    [DataRow("print bbb + (select 1)", "bbb")]
    public void Print_ColumnName_RaisesMsg128(string commandText, string name)
    {
        var ex = new Simulation().AssertSqlError(commandText, 128);
        AreEqual($"The name \"{name}\" is not permitted in this context. Valid expressions are constants, constant expressions, and (in some contexts) variables. Column names are not permitted.", ex.Message);
        AreEqual((byte)15, ex.Class);
        AreEqual((byte)1, ex.State);
    }

    /// <summary>
    /// A subquery anywhere in the operand — nested in a function argument or
    /// inside a CASE — is Msg 1046, and the refusal is settled while parsing,
    /// so an un-taken IF branch raises it too.
    /// </summary>
    [TestMethod]
    [DataRow("print (select 1)")]
    [DataRow("print len((select 'ab'))")]
    [DataRow("print case when exists(select 1) then 'y' else 'n' end")]
    [DataRow("print (select 1) + 1")]
    [DataRow("if 1 = 0 print (select 1)")]
    public void Print_Subquery_RaisesMsg1046(string commandText)
    {
        var ex = new Simulation().AssertSqlError(commandText, 1046);
        AreEqual("Subqueries are not allowed in this context. Only scalar expressions are allowed.", ex.Message);
        AreEqual((byte)15, ex.Class);
    }

    /// <summary>
    /// The name gate withdraws a reference the parser turned into a function
    /// call, so the built-in and user-function spellings still print.
    /// </summary>
    [TestMethod]
    [DataRow("print db_name()")]
    [DataRow("print len('abc')")]
    [DataRow("print @@spid")]
    [DataRow("declare @i int = 7; print @i")]
    public void Print_FunctionOrVariable_IsNotAName(string commandText) =>
        _ = new Simulation().ExecuteNonQuery(commandText);

    /// <summary>
    /// Msg 128 is settled while parsing, so an un-taken IF branch raises it —
    /// probe-confirmed (<c>IF 1 = 0 PRINT zzz</c> reports Msg 128 on real).
    /// </summary>
    [TestMethod]
    public void Print_ColumnName_InSkippedBranch_StillRaises() =>
        _ = new Simulation().AssertSqlError("if 1 = 0 print zzz", 128);
}
