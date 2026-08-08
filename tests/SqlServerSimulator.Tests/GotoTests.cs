using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// T-SQL <c>GOTO</c> and the <c>label:</c> declarations it jumps to. Every
/// behavior below was probed against SQL Server 2025 (2026-08-08). See
/// <c>docs/claude/control-flow.md</c>.
/// </summary>
[TestClass]
public sealed class GotoTests
{
    /// <summary>Collects a batch's PRINT output in order.</summary>
    private static List<string> Print(string commandText)
    {
        var messages = new List<string>();
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var command = connection.CreateCommand(commandText);
        _ = command.ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void ForwardJump_SkipsTheStatementsBetween()
        => AreEqual("a,b", string.Join(",", Print("print 'a'; goto l; print 'skipped'; l: print 'b';")));

    [TestMethod]
    public void BackwardJump_Loops()
        => AreEqual("1,2,3", string.Join(",", Print(
            "declare @i int = 0; l: set @i = @i + 1; print @i; if @i < 3 goto l;")));

    [TestMethod]
    public void LabelIsCaseInsensitive()
        => AreEqual("reached", string.Join(",", Print("goto L; print 'skipped'; l: print 'reached';")));

    /// <summary>A label nothing jumps to is simply a no-op the batch flows through.</summary>
    [TestMethod]
    public void UnreferencedLabel_IsANoOp()
        => AreEqual("a,b", string.Join(",", Print("print 'a'; l: print 'b';")));

    /// <summary>
    /// The jump unwinds whatever it is nested in — a <c>BEGIN…END</c> block, a
    /// <c>WHILE</c> body, a <c>TRY</c> block — without the enclosing construct
    /// demanding its own terminator.
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 1 begin goto l; end print 'skipped'; l: print 'out';", "out")]
    [DataRow("declare @i int = 0; while @i < 3 begin set @i = @i + 1; if @i = 2 goto l; print @i; end l: print 'out';", "1,out")]
    [DataRow("begin try goto l; end try begin catch print 'caught'; end catch print 'skipped'; l: print 'out';", "out")]
    [DataRow("begin begin begin goto l; end end end print 'skipped'; l: print 'out';", "out")]
    public void JumpsOutOfEveryEnclosingConstruct(string commandText, string expected)
        => AreEqual(expected, string.Join(",", Print(commandText)));

    /// <summary>A label inside a loop body is reachable from inside it.</summary>
    [TestMethod]
    public void LabelInsideALoopBody_IsReachableFromIt()
        => AreEqual("1,2,3", string.Join(",", Print(
            "declare @i int = 0; while @i < 3 begin set @i = @i + 1; goto l; l: print @i; end")));

    /// <summary>
    /// The label pass runs while the batch compiles, so an undeclared target is
    /// <strong>Msg 133</strong> at class 15 even when the <c>GOTO</c> is
    /// unreachable — and the statements before it never run.
    /// </summary>
    [TestMethod]
    public void UndeclaredLabel_RaisesMsg133BeforeAnythingRuns()
    {
        var ex = new Simulation().AssertSqlError("print 'a'; goto nosuchlabel;", 133);
        AreEqual("A GOTO statement references the label 'nosuchlabel' but the label has not been declared.", ex.Message);
        AreEqual(15, ex.Class);
        // The PRINT ahead of the GOTO never ran: the refusal is settled while
        // the batch compiles, so nothing reaches the client.
        var messages = new List<string>();
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        using var command = connection.CreateCommand("print 'a'; goto nosuchlabel;");
        _ = Throws<System.Data.Common.DbException>(() => command.ExecuteNonQuery());
        IsEmpty(messages);
    }

    [TestMethod]
    public void UndeclaredLabel_UnderAnUntakenBranch_StillRaises()
        => _ = new Simulation().AssertSqlError("if 1 = 0 goto nosuchlabel; print 'b';", 133);

    /// <summary>
    /// A duplicate label is <strong>Msg 132</strong>, likewise at compile time —
    /// no <c>GOTO</c> need reference it.
    /// </summary>
    [TestMethod]
    public void DuplicateLabel_RaisesMsg132()
    {
        var ex = new Simulation().AssertSqlError("l: print 'a'; l: print 'b';", 132);
        AreEqual("The label 'l' has already been declared. Label names must be unique within a query batch or stored procedure.", ex.Message);
        AreEqual(15, ex.Class);
    }

    /// <summary>
    /// Jumping <em>into</em> a TRY or CATCH scope is <strong>Msg 1026</strong>;
    /// jumping out of one is legal (covered above).
    /// </summary>
    [TestMethod]
    [DataRow("goto l; begin try l: print 'in'; end try begin catch print 'c'; end catch")]
    [DataRow("goto l; begin try print 'in'; end try begin catch l: print 'c'; end catch")]
    public void JumpIntoATryOrCatchScope_RaisesMsg1026(string commandText)
    {
        var ex = new Simulation().AssertSqlError(commandText, 1026);
        AreEqual("GOTO cannot be used to jump into a TRY or CATCH scope.", ex.Message);
        AreEqual(15, ex.Class);
    }

    /// <summary>
    /// Labels are per batch and per module body, so a procedure carries its own
    /// set and a name it reuses from the caller's batch is not a collision.
    /// </summary>
    [TestMethod]
    public void LabelsAreScopedToTheirModuleBody()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create procedure p as begin declare @i int = 0; l: set @i = @i + 1; if @i < 3 goto l; print @i; end");
        var messages = new List<string>();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        // The caller declares `l` too, which is not a collision, and the
        // procedure's own `l` loop still runs to completion. (The two PRINTs
        // arrive in separate info-message batches, so only membership is
        // asserted here.)
        using var command = connection.CreateCommand("l: print 'caller'; exec p;");
        _ = command.ExecuteNonQuery();
        Assert.Contains("caller", messages);
        Assert.Contains("3", messages);
    }

    /// <summary>
    /// The label pre-scan reads a lone colon at parenthesis depth zero, so the
    /// one other bare colon in the grammar — <c>JSON_OBJECT</c>'s key separator
    /// — and the <c>::</c> of a static method call are both left alone.
    /// </summary>
    [TestMethod]
    public void ColonsThatAreNotLabels_AreUntouched()
    {
        AreEqual("""{"a":1}""", new Simulation().ExecuteScalar("select json_object('a': 1); goto l; l: select 1"));
        AreEqual("/1/", new Simulation().ExecuteScalar("goto l; l: select hierarchyid::Parse('/1/').ToString()"));
    }

    /// <summary>A delimited identifier is not a legal label — real reports Msg 102.</summary>
    [TestMethod]
    public void BracketedLabel_IsNotALabel()
        => _ = new Simulation().AssertSqlError("print 'a'; [my label]: print 'b';", 102);
}
