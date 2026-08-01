using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real SQL Server's three function body-shape rules, all reported at
/// <c>CREATE</c> / <c>ALTER</c> alongside the body bind
/// (<c>ModuleBodyBindingTests</c>): <strong>Msg 455</strong> (the last
/// statement must be <c>RETURN</c>), <strong>Msg 444</strong> (a <c>SELECT</c>
/// returning rows to the client) and <strong>Msg 443</strong> (a side-effecting
/// operator). They apply to a scalar UDF and a multi-statement TVF; procedures,
/// triggers and inline TVFs are exempt.
/// Every case is probe-confirmed against SQL Server 2025 (2026-08-01).
/// </summary>
[TestClass]
public sealed class FunctionBodyShapeTests
{
    private static Simulation WithFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int identity(1, 1), b int)",
            "create procedure dbo.callee as select 1 as v",
            "create sequence dbo.sq as int start with 1");
        return sim;
    }

    private static int ObjectCount(Simulation sim, string name)
        => sim.ExecuteScalar<int>($"select count(*) from sys.objects where name = '{name}'");

    private static SimulatedSqlException AssertScalarBodyError(string body, int number)
    {
        var sim = WithFixture();
        var ex = sim.AssertSqlError($"create function dbo.f(@x int) returns int as begin {body} end", number);
        AreEqual(0, ObjectCount(sim, "f"));
        return ex;
    }

    // === Msg 455: the last statement must be RETURN ===

    /// <summary>
    /// The message, severity and state, for both function kinds real applies
    /// the rule to. A multi-statement TVF's bare <c>RETURN</c> counts the same
    /// as a scalar UDF's value form.
    /// </summary>
    [TestMethod]
    [DataRow("create function dbo.f(@x int) returns int as begin set @x = 1 end")]
    [DataRow("create function dbo.f() returns @r table (a int) as begin insert @r values (1) end")]
    public void MissingTrailingReturn_IsMsg455(string create)
    {
        var sim = WithFixture();
        var ex = sim.AssertSqlError(create, 455);
        AreEqual("The last statement included within a function must be a return statement.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(2, ex.State);
        AreEqual("f", ex.Procedure);
        AreEqual(0, ObjectCount(sim, "f"));
    }

    /// <summary>
    /// Real means the <em>last top-level statement</em> literally. A trailing
    /// <c>IF</c> whose every arm returns doesn't satisfy it, and neither does a
    /// <c>WHILE</c> whose body ends in <c>RETURN</c> — but a trailing bare
    /// <c>BEGIN … END</c> is transparent and its last inner statement counts.
    /// </summary>
    [TestMethod]
    [DataRow("if @x = 1 return 1", 455)]
    [DataRow("if @x = 1 return 1 else return 2", 455)]
    [DataRow("if @x = 1 begin set @x = 2 return 1 end", 455)]
    [DataRow("while @x < 10 begin set @x = @x + 1 return @x end", 455)]
    [DataRow("return 1 set @x = 2", 455)]
    public void TrailingConstructThatOnlyLooksLikeAReturn_IsMsg455(string body, int number)
        => _ = AssertScalarBodyError(body, number);

    [TestMethod]
    [DataRow("set @x = 2 begin set @x = 3 return @x end")]
    [DataRow("begin begin return 1 end end")]
    [DataRow("if @x = 1 begin return 1 end return 2")]
    [DataRow("while @x < 10 begin set @x = @x + 1 end return @x")]
    public void TrailingReturnAtTopLevel_Creates(string body)
    {
        var sim = WithFixture();
        sim.ExecuteBatches($"create function dbo.f(@x int) returns int as begin {body} end");
        AreEqual(1, ObjectCount(sim, "f"));
    }

    /// <summary>
    /// Msg 455 carries the line of the innermost trailing statement the parser
    /// reached — even when the rule fails precisely because that statement sits
    /// inside an <c>IF</c> body.
    /// </summary>
    [TestMethod]
    public void Msg455_ReportsTheLastStatementsLine()
    {
        var sim = WithFixture();
        var ex = sim.AssertSqlError("""
            create function dbo.f(@x int) returns int as
            begin
                set @x = 1
                if @x = 1
                begin
                    set @x = 2
                    return 1
                end
            end
            """, 455);
        AreEqual(7, ex.LineNumber);
    }

    // === Msg 444: a SELECT returning rows to the client ===

    /// <summary>
    /// State 2 when the query reads a rowset — a FROM clause at any depth, or a
    /// set operator — and state 3 for a wholly-computed projection.
    /// </summary>
    [TestMethod]
    [DataRow("select 1", 3)]
    [DataRow("select 1, 2", 3)]
    [DataRow("select @x", 3)]
    [DataRow("select b from dbo.t", 2)]
    [DataRow("select top 1 b from dbo.t order by b", 2)]
    [DataRow("select (select max(b) from dbo.t)", 2)]
    [DataRow("select 1 union select 2", 2)]
    [DataRow("select a from (values (1)) v(a)", 2)]
    public void ClientReturningSelect_IsMsg444(string body, int state)
    {
        var ex = AssertScalarBodyError($"{body} return 1", 444);
        AreEqual("Select statements included within a function cannot return data to a client.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(state, ex.State);
    }

    /// <summary>A multi-statement TVF takes the same rule.</summary>
    [TestMethod]
    public void ClientReturningSelectInMultiStatementTvf_IsMsg444()
    {
        var sim = WithFixture();
        var ex = sim.AssertSqlError("create function dbo.f() returns @r table (a int) as begin select 1 return end", 444);
        AreEqual(3, ex.State);
        AreEqual(0, ObjectCount(sim, "f"));
    }

    /// <summary>
    /// An assignment-only <c>SELECT</c> is legal; so is a <c>SELECT</c> feeding
    /// an <c>INSERT</c> into the return table, and one inside a scalar
    /// subquery.
    /// </summary>
    [TestMethod]
    [DataRow("create function dbo.f(@x int) returns int as begin declare @v int select @v = 1 return @v end")]
    [DataRow("create function dbo.f(@x int) returns int as begin declare @v int select @v = max(b) from dbo.t return @v end")]
    [DataRow("create function dbo.f(@x int) returns int as begin return (select max(b) from dbo.t) end")]
    [DataRow("create function dbo.f() returns @r table (a int) as begin insert @r select b from dbo.t return end")]
    public void SelectThatDoesNotReachTheClient_Creates(string create)
    {
        var sim = WithFixture();
        sim.ExecuteBatches(create);
        AreEqual(1, ObjectCount(sim, "f"));
    }

    // === Msg 443: side-effecting operators ===

    /// <summary>
    /// The statement operators, each with the name and state real reports.
    /// </summary>
    [TestMethod]
    [DataRow("insert dbo.t (b) values (1)", "INSERT", 15)]
    [DataRow("update dbo.t set b = 1", "UPDATE", 15)]
    [DataRow("delete dbo.t", "DELETE", 15)]
    [DataRow("merge dbo.t as tgt using (select 1 as b) as src on tgt.b = src.b when matched then update set b = 1;", "MERGE", 15)]
    [DataRow("truncate table dbo.t", "TRUNCATE TABLE", 15)]
    [DataRow("select b into dbo.copied from dbo.t", "SELECT INTO", 15)]
    [DataRow("begin tran", "BEGIN TRANSACTION", 15)]
    [DataRow("commit", "COMMIT TRANSACTION", 15)]
    [DataRow("rollback", "ROLLBACK TRANSACTION", 15)]
    [DataRow("save tran s1", "SAVEPOINT", 15)]
    [DataRow("set nocount on", "SET OPTION ON", 15)]
    [DataRow("set nocount off", "SET OPTION OFF", 15)]
    [DataRow("set transaction isolation level read committed", "SET TRANSACTION ISOLATION LEVEL", 15)]
    [DataRow("set rowcount 5", "SET ROW COUNT", 15)]
    [DataRow("set textsize 100", "SET TEXTSIZE", 15)]
    [DataRow("set statistics io on", "SET STATISTICS ON", 15)]
    [DataRow("set identity_insert dbo.t on", "SET IDENTITY_INSERT ON", 15)]
    [DataRow("set identity_insert dbo.t off", "SET IDENTITY_INSERT OFF", 15)]
    [DataRow("set lock_timeout 100", "SET COMMAND", 15)]
    [DataRow("set language us_english", "SET COMMAND", 15)]
    [DataRow("print 'hi'", "PRINT", 14)]
    [DataRow("raiserror('x', 16, 1)", "RAISERROR", 14)]
    [DataRow("throw 50000, 'x', 1", "THROW", 14)]
    [DataRow("waitfor delay '00:00:01'", "WAITFOR", 14)]
    [DataRow("exec('select 1')", "EXECUTE STRING", 14)]
    [DataRow("begin try set @x = 1 end try begin catch set @x = 2 end catch", "BEGIN TRY", 14)]
    public void SideEffectingStatement_IsMsg443(string body, string operatorName, int state)
    {
        var ex = AssertScalarBodyError($"{body} return 1", 443);
        AreEqual($"Invalid use of a side-effecting operator '{operatorName}' within a function.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(state, ex.State);
    }

    /// <summary>
    /// The side-effecting built-ins, named the way real names them (state 1) —
    /// the date / time readers stay legal even though the indexed-view
    /// determinism battery rejects them. <c>NEWSEQUENTIALID</c> is real's Msg
    /// 443 too, but the simulator's own Msg 302 (the outside-a-DEFAULT gate,
    /// which real raises everywhere else) fires from the call's parse first.
    /// </summary>
    [TestMethod]
    [DataRow("return newid()", "newid")]
    [DataRow("return rand()", "rand")]
    [DataRow("declare @g uniqueidentifier = newid() return 1", "newid")]
    public void SideEffectingBuiltIn_IsMsg443(string body, string operatorName)
    {
        var ex = AssertScalarBodyError(body, 443);
        AreEqual($"Invalid use of a side-effecting operator '{operatorName}' within a function.", ex.Message);
        AreEqual(1, ex.State);
    }

    /// <summary>
    /// What stays legal inside a function: writing a table variable (the
    /// scalar-UDF's own and the TVF's return table alike), calling a procedure
    /// or <c>sp_executesql</c>, and the current-time readers.
    /// </summary>
    [TestMethod]
    [DataRow("create function dbo.f(@x int) returns int as begin declare @tv table (a int) insert into @tv values (1) return 1 end")]
    [DataRow("create function dbo.f(@x int) returns int as begin declare @tv table (a int) update @tv set a = 1 return 1 end")]
    [DataRow("create function dbo.f(@x int) returns int as begin declare @tv table (a int) delete @tv return 1 end")]
    [DataRow("create function dbo.f() returns @r table (a int) as begin insert into @r values (1) return end")]
    [DataRow("create function dbo.f(@x int) returns int as begin exec dbo.callee return 1 end")]
    [DataRow("create function dbo.f(@x int) returns int as begin exec sp_executesql N'select 1' return 1 end")]
    [DataRow("create function dbo.f(@x int) returns int as begin declare @d datetime = getdate() return 1 end")]
    public void LegalFunctionBody_Creates(string create)
    {
        var sim = WithFixture();
        sim.ExecuteBatches(create);
        AreEqual(1, ObjectCount(sim, "f"));
    }

    /// <summary>
    /// A violation in a never-taken branch still refuses the function: real
    /// checks the body's shape, not its reachable statements.
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 0 print 'hi' return 1")]
    [DataRow("while 1 = 0 begin select 1 end return 1")]
    public void ViolationInADeadBranch_StillRefuses(string body)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create function dbo.f(@x int) returns int as begin {body} end", body.StartsWith("if", StringComparison.Ordinal) ? 443 : 444);
        AreEqual(0, ObjectCount(sim, "f"));
    }

    // === Ordering and the exempt module kinds ===

    /// <summary>
    /// Real reports every binder error in the body before any shape error, so
    /// a body with both reports the binder's — whichever statement each is on.
    /// </summary>
    [TestMethod]
    [DataRow("select nosuchcol from dbo.t print 'hi' return 1")]
    [DataRow("print 'hi' select nosuchcol from dbo.t return 1")]
    public void BinderErrorPrecedesTheShapeError(string body)
        => _ = AssertScalarBodyError(body, 207);

    /// <summary>
    /// Among shape errors, source order decides — and Msg 455 comes last
    /// because its statement is the body's last.
    /// </summary>
    [TestMethod]
    public void FirstShapeViolationInSourceOrderWins()
    {
        var ex = AssertScalarBodyError("select 1 set @x = 2", 444);
        AreEqual(3, ex.State);
    }

    /// <summary>
    /// Procedures, triggers and inline TVFs are exempt from all three rules —
    /// probe-confirmed that each of these creates on real.
    /// </summary>
    [TestMethod]
    [DataRow("create procedure dbo.p as begin select 1 print 'x' insert dbo.t (b) values (1) set nocount on end", "p")]
    [DataRow("create trigger dbo.tr on dbo.t after insert as begin print 'x' select 1 end", "tr")]
    [DataRow("create function dbo.itvf() returns table as return (select b from dbo.t)", "itvf")]
    public void ExemptModuleKinds_Create(string create, string name)
    {
        var sim = WithFixture();
        sim.ExecuteBatches(create);
        AreEqual(1, ObjectCount(sim, name));
    }

    // === Replacement paths ===

    /// <summary>
    /// A shape violation refuses an <c>ALTER</c> / <c>CREATE OR ALTER</c> the
    /// same way, leaving the previous body standing.
    /// </summary>
    [TestMethod]
    [DataRow("alter")]
    [DataRow("create or alter")]
    public void FailedReshape_LeavesThePreviousBodyStanding(string verb)
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create function dbo.f(@x int) returns int as begin return 7 end");
        _ = sim.AssertSqlError($"{verb} function dbo.f(@x int) returns int as begin print 'x' return 7 end", 443);
        AreEqual(7, sim.ExecuteScalar("select dbo.f(1)"));
    }

    /// <summary>
    /// The shape error is an ordinary catchable one carrying the function's
    /// unqualified name, like every other CREATE-time body error.
    /// </summary>
    [TestMethod]
    public void ShapeError_IsCatchableAndNamesTheFunction()
    {
        var sim = WithFixture();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("""
            begin try
                exec('create function dbo.fcaught(@x int) returns int as begin print ''x'' return 1 end');
            end try
            begin catch
                select error_number() as n, error_procedure() as p;
            end catch
            """);
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(443, reader.GetInt32(0));
        AreEqual("fcaught", reader.GetString(1));
    }

    /// <summary>
    /// A body whose bind stopped at a deferred missing object never saw the
    /// last statement, so Msg 455 isn't reported on a guess. Real, which knows
    /// where the deferred statement ended, keeps checking and reports it.
    /// </summary>
    [TestMethod]
    public void BindAbandonedAtADeferral_LeavesTheLastStatementRuleUnrun()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create function dbo.f(@x int) returns int as begin set @x = next value for dbo.missing_seq set @x = 2 end");
        AreEqual(1, ObjectCount(sim, "f"));
    }
}
