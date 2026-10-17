using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>INSERT … EXEC</c> — appending the result sets a
/// stored procedure or dynamic-SQL batch yields into an INSERT target. Covers
/// the dynamic-SQL and procedure forms, INTO + column-list reordering,
/// multi-result-set appending with the total <c>@@ROWCOUNT</c>, the zero-row
/// pure-DML case, table-variable targets, identity allocation parity with
/// INSERT…SELECT, and the rejection edges (column-count mismatch Msg 213,
/// nested INSERT…EXEC Msg 8164, OUTPUT clause Msg 483). Probed against SQL
/// Server 2025 (2026-07-14).
/// </summary>
[TestClass]
public sealed class InsertExecTests
{
    [TestMethod]
    public void DynamicSql_SingleResultSet()
        => AreEqual(42, new Simulation().ExecuteScalar(
            "create table #t (a int); insert #t exec('select 42'); select a from #t"));

    [TestMethod]
    public void Procedure_Target()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 7 union all select 8 end");
        AreEqual(15, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select sum(a) from #t"));
    }

    [TestMethod]
    public void Into_With_ColumnList_ReordersValues()
        => AreEqual(2010, new Simulation().ExecuteScalar(
            "create table #t (a int, b int); insert into #t (b, a) exec('select 10, 20'); select a * 100 + b from #t"));

    [TestMethod]
    public void MultipleResultSets_AppendAllRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 5; select 6 end");
        AreEqual(11, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select sum(a) from #t"));
    }

    [TestMethod]
    public void RowCount_Is_TotalAcrossResultSets()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 5; select 6 end");
        AreEqual(2, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select @@rowcount"));
    }

    [TestMethod]
    public void NoResultSet_InsertsZeroRows_Succeeds()
    {
        // A pure-DML procedure yields no tabular output; INSERT…EXEC leaves
        // the target empty and completes without error (probe-confirmed).
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin declare @x int = 1 end");
        AreEqual(0, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select count(*) from #t"));
    }

    [TestMethod]
    public void TableVariable_Target()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "declare @tv table (a int); insert @tv exec('select 1 union all select 2'); select sum(a) from @tv"));

    [TestMethod]
    public void Identity_AllocatesLikeInsertSelect()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table #t (id int identity, a int); insert #t (a) exec('select 7 union all select 8'); select max(id) from #t"));

    [TestMethod]
    public void ColumnCountMismatch_MoreValues_Raises213()
        => new Simulation().AssertSqlError(
            "create table #t (a int); insert #t exec('select 1, 2')",
            213, "Column name or number of supplied values does not match table definition.");

    [TestMethod]
    public void ColumnCountMismatch_FewerValues_Raises213()
        => new Simulation().AssertSqlError(
            "create table #t (a int, b int); insert #t exec('select 1')",
            213, "Column name or number of supplied values does not match table definition.");

    [TestMethod]
    public void UncoercibleValue_FailsInTheBodysSelect_EndingTheBatch()
    {
        // Real converts each row to the target's types inside the body's
        // SELECT: Msg 8114 at its line and procedure, ending the batch.
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int)", "create procedure dbo.p as\nbegin\n  select 1;\n  select 'x'\nend");
        var ex = sim.AssertSqlError("insert dbo.t exec dbo.p; print 'ran on'", 8114);
        AreEqual("Error converting data type varchar to int.", ex.Errors[0].Message);
        AreEqual((1, 4, "dbo.p"), (ex.Errors[0].State, ex.Errors[0].LineNumber, ex.Errors[0].Procedure));
        HasCount(1, ex.Errors);
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.t"));
    }

    [TestMethod]
    public void BodyPlanReplayed_ConvertsToEachExecutionsTarget()
    {
        // The body's SELECT keeps its plan across executions, and each one
        // converts its rows to that INSERT's own target.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.s (v varchar(10)); insert dbo.s values ('12'), ('abcd'); create table dbo.wide (a varchar(10)); create table dbo.narrow (a varchar(3)); create table dbo.num (a int)",
            "create procedure dbo.p as\nbegin\n  select v from dbo.s order by v\nend");
        AreEqual(2, sim.ExecuteScalar("insert dbo.wide exec dbo.p; insert dbo.wide exec dbo.p; select count(*) / 2 from dbo.wide"));
        var truncated = sim.AssertSqlError("insert dbo.narrow exec dbo.p", 8152);
        AreEqual((14, 3), (truncated.Errors[0].State, truncated.Errors[0].LineNumber));
        var converted = sim.AssertSqlError("insert dbo.num exec dbo.p", 8114);
        AreEqual("Error converting data type varchar to int.", converted.Errors[0].Message);
        AreEqual(2, sim.ExecuteScalar("insert dbo.wide exec dbo.p; select count(*) / 3 from dbo.wide"));
        AreEqual(0, sim.ExecuteScalar("select (select count(*) from dbo.narrow) + (select count(*) from dbo.num)"));
    }

    [TestMethod]
    public void ConversionCaughtInTheBody_FailsTheWholeInsert()
    {
        // A TRY in the body catches the conversion, which dooms the unit the
        // body runs in (@@TRANCOUNT 1 there); the body goes on, its write
        // refused with Msg 3930, and the INSERT writes nothing — not even
        // another statement's rows — while the batch goes on with @@ERROR 0.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int, b varchar(3)); create table dbo.t2 (a int)",
            """
            create procedure dbo.p as
            begin
              select 7, 'ok';
              begin try
                select v, 'ok' from (values ('8'), ('x')) d(v);
              end try
              begin catch
                print concat(error_number(), ' ', error_state(), ' ', error_line(), ' ', xact_state(), ' ', @@trancount);
              end catch
              insert dbo.t2 values (1);
              select 9, 'ok'
            end
            """);
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert dbo.t exec dbo.p; print concat('after ', @@error, ' ', @@rowcount)").ExecuteNonQuery());
        CollectionAssert.AreEqual(
            new[] { "3930", "8114 1 5 -1 1", "after 0 0" },
            ex.Errors.Select(error => error.Number == 0 ? error.Message : error.Number.ToInvariantString()).ToArray());
        AreEqual(0, connection.CreateCommand("select count(*) from dbo.t").ExecuteScalar());
    }

    [TestMethod]
    public void ConversionCaughtInTheBody_EndsTheInsertWithTheClientsSevereError()
    {
        // With nothing else reported, the INSERT's DONE carrying the error bit
        // alone is SqlClient's class-11 Msg 0.
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int)", "create procedure dbo.p as begin try select 'x' end try begin catch end catch");
        var ex = sim.AssertSqlError("insert dbo.t exec dbo.p", 0);
        AreEqual(((byte)11, 0, "A severe error occurred on the current command.  The results, if any, should be discarded."), (ex.Errors[0].Class, ex.Errors[0].LineNumber, ex.Errors[0].Message));
    }

    [TestMethod]
    public void TruncationCaughtInTheBody_StatesNameTheTypePair_AndLeaveTheUnitCommittable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (b varchar(3), n nvarchar(3), v varbinary(2))",
            """
            create procedure dbo.p @k int as
            begin
              begin try
                if @k = 1 select 'abcd', null, null;
                if @k = 2 select N'abcd', null, null;
                if @k = 3 select null, 'abcd', null;
                if @k = 4 select null, N'abcd', null;
                if @k = 5 select null, null, 0x010203;
                if @k = 6 select cast('abcd' as varchar(max)), null, null;
                if @k = 7 select cast('abcd' as char(4)), null, null;
              end try
              begin catch
                print concat(error_number(), ' ', error_state(), ' ', xact_state());
              end catch
            end
            """);
        using var connection = sim.CreateOpenConnection();
        var printed = Enumerable.Range(1, 7).Select(k => Throws<SimulatedSqlException>(() => connection.CreateCommand($"insert dbo.t exec dbo.p {k.ToInvariantString()}").ExecuteNonQuery()).Errors[^1].Message).ToArray();
        CollectionAssert.AreEqual(new[] { "8152 14 1", "8152 2 1", "8152 4 1", "8152 13 1", "8152 11 1", "8152 10 1", "8152 14 1" }, printed);
    }

    [TestMethod]
    public void OnlyPaddingCut_IsNoTruncation()
        => AreEqual("[ab ][ab ]", new Simulation().ExecuteScalar(
            "create table #t (b varchar(3), n nvarchar(3)); insert #t exec('select ''ab     '', N''ab    '''); select concat('[', b, '][', n, ']') from #t"));

    [TestMethod]
    public void UncaughtTruncation_TheBodyGoesOn_AndTheInsertWritesNothing()
    {
        // The body's own Msg 8152 is all the client hears: SqlClient raises
        // nothing for the INSERT's error bit behind it, and @@ERROR reads 0.
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int, b varchar(3))", "create procedure dbo.p as\nbegin\n  select 1, 'xxxx';\n  select 2, 'y'\nend");
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert dbo.t exec dbo.p; print concat('after ', @@error, ' ', @@rowcount)").ExecuteNonQuery());
        CollectionAssert.AreEqual(new[] { 8152, 0 }, ex.Errors.Select(error => error.Number).ToArray());
        AreEqual((14, 3, "dbo.p"), (ex.Errors[0].State, ex.Errors[0].LineNumber, ex.Errors[0].Procedure));
        AreEqual("after 0 0", ex.Errors[1].Message);
        AreEqual(0, connection.CreateCommand("select count(*) from dbo.t").ExecuteScalar());
    }

    [TestMethod]
    public void ConversionCaughtInTheBody_UnderAnOuterTry_EndsTheBatch()
    {
        // The INSERT's failure is no error a CATCH sees: with a TRY open around
        // it the whole batch ends, nothing after it running.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int)",
            "create procedure dbo.p as begin try select 'x' end try begin catch end catch");
        var ex = sim.AssertSqlError("begin try insert dbo.t exec dbo.p; print 'in try' end try begin catch print 'caught' end catch; print 'after'", 0);
        HasCount(1, ex.Errors);
    }

    [TestMethod]
    public void BodyWrites_JoinTheInsertsUnit()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int, b varchar(3)); create table dbo.t2 (a int)",
            "create procedure dbo.p @k int as begin insert dbo.t2 values (@@trancount); begin try if @k = 1 select 1, 'abcd' end try begin catch end catch select 2, 'ok' end",
            "insert dbo.t exec dbo.p 0");
        _ = sim.AssertSqlError("insert dbo.t exec dbo.p 1", 0);
        AreEqual("2 1 1", sim.ExecuteScalar("select concat((select a from dbo.t2), ' ', (select count(*) from dbo.t), ' ', (select count(*) from dbo.t2))"));
    }

    [TestMethod]
    public void RollbackInTheBody_Raises3915()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int)", "create procedure dbo.p as begin select 1; rollback end");
        _ = sim.AssertSqlError("insert dbo.t exec dbo.p; print 'ran on'", 3915);
    }

    [TestMethod]
    public void CommitInTheBody_Raises3916_UnlessTheBodyBeganIt()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int)",
            "create procedure dbo.p @k int as begin select 1; if @k = 1 commit; if @k = 2 begin begin tran; commit end end");
        _ = sim.AssertSqlError("insert dbo.t exec dbo.p 1", 3916);
        AreEqual(1, sim.ExecuteScalar("insert dbo.t exec dbo.p 2; select count(*) from dbo.t"));
    }

    [TestMethod]
    public void Nested_InsertExec_Raises8164()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p_inner as begin select 99 end",
            "create procedure dbo.p_outer as begin create table #inner (a int); insert #inner exec dbo.p_inner; select a from #inner end");
        sim.AssertSqlError(
            "create table #t (a int); insert #t exec dbo.p_outer",
            8164, "An INSERT EXEC statement cannot be nested.");
    }

    [TestMethod]
    public void OutputClause_Raises483()
        => new Simulation().AssertSqlError(
            "create table #t (a int); insert #t output inserted.a exec('select 42')",
            483, "The OUTPUT clause cannot be used in an INSERT...EXEC statement.");

    /// <summary>
    /// Each SELECT the executed body runs meets the target columns' one-way
    /// assignment rule as it compiles, over no rows too, at its own line in
    /// the body; a constant NULL is exempt, and the error ends the INSERT but
    /// not the caller's batch (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AssignmentRule_JudgesEachResultSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (d decimal(10, 2))",
            "create procedure p as begin select 1.5; select getdate() where 1 = 0; end");
        var ex = sim.AssertSqlError("insert t exec p; select 1", 257);
        AreEqual("3 1 p", $"{ex.Errors[0].State} {ex.Errors[0].LineNumber} {ex.Errors[0].Procedure}");
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void AssignmentRule_ContinuesTheCallersBatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d date)");
        using var connection = sim.CreateOpenConnection();
        using var reader = connection.CreateCommand("""
            begin try insert t exec ('select 1'); end try begin catch select error_number(); end catch
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(206, reader.GetInt32(0));
    }

    [TestMethod]
    [DataRow("select null")]
    [DataRow("select cast(null as datetime)")]
    public void AssignmentRule_ExemptsAConstantNull(string body)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d decimal(10, 2))");
        _ = sim.ExecuteNonQuery($"insert t exec ('{body.Replace("'", "''", StringComparison.Ordinal)}')");
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>
    /// The executed body runs on past an error that ends only its statement,
    /// as any called batch does: its errors and later messages reach the
    /// client, every later result set is inserted, and the INSERT
    /// itself succeeds — <c>@@ROWCOUNT</c> counting every row, <c>@@ERROR</c>
    /// reading 0 (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void BodyError_RunsTheBodyOn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select 1/0; print 'body after'; select 2; raiserror('x', 16, 1); select 3 end");
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t exec p; print concat(@@rowcount, ' ', @@error)").ExecuteNonQuery());
        CollectionAssert.AreEqual(
            new[] { "8134 Divide by zero error encountered.", "50000 x", "0 body after", "0 3 0" },
            ex.Errors.Select(e => $"{e.Number} {e.Message}").ToArray());
        AreEqual(6, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    /// <summary>
    /// A nested procedure's error continues the same way, its later rows
    /// inserted with the caller's.
    /// </summary>
    [TestMethod]
    public void NestedBodyError_RunsBothBodiesOn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure q as begin select 5; select 1/0; select 6 end",
            "create procedure p as begin select 1; exec q; select 2 end");
        using var connection = sim.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t exec p").ExecuteNonQuery());
        AreEqual("4 14", connection.CreateCommand("select concat(count(*), ' ', sum(a)) from t").ExecuteScalar());
    }

    /// <summary>
    /// An error that ends the executed batch — a missing object, from a
    /// procedure or dynamic SQL — still lets the INSERT write the rows
    /// returned before it, inside a transaction too; the INSERT then reports
    /// no count and leaves <c>@@ERROR</c> reading the error.
    /// </summary>
    [TestMethod]
    [DataRow("exec ('select 1; select * from nosuch; select 2')")]
    [DataRow("exec p")]
    public void BodyEndedByAMissingObject_InsertsWhatItReturned(string exec)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select * from nosuch; select 2 end");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand($"begin tran; insert t {exec}; print concat(@@rowcount, ' ', @@error); commit");
        var ex = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        CollectionAssert.AreEqual(new[] { "208", "0" }, ex.Errors.Select(e => e.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        AreEqual("1 208", ex.Errors[1].Message);
        AreEqual(1, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    /// <summary>
    /// A result set the target can't take ends the body and the INSERT with
    /// nothing written, rows of earlier result sets included, yet the INSERT
    /// doesn't fail: <c>@@ERROR</c> reads 0 after it, and the batch goes on.
    /// </summary>
    [TestMethod]
    public void AssignmentRule_WritesNothingAndLeavesAtAtErrorClear()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select newid(); select 2 end");
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t exec p; print concat(@@rowcount, ' ', @@error)").ExecuteNonQuery());
        AreEqual(206, ex.Number);
        AreEqual("0 0", ex.Errors[1].Message);
        AreEqual(0, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>
    /// Inside a <c>TRY</c> the body's first error is caught instead, and the
    /// INSERT writes nothing.
    /// </summary>
    [TestMethod]
    public void BodyErrorInsideTry_IsCaughtAndInsertsNothing()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select 1/0; select 2 end");
        AreEqual("8134 0", sim.ExecuteScalar(
            "begin try insert t exec p end try begin catch select concat(error_number(), ' ', (select count(*) from t)) end catch"));
    }

    /// <summary>
    /// A nested INSERT … EXEC fails only its own statement in the executed
    /// body, which goes on (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void NestedInsertExec_EndsOnlyItsStatement()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int); create table u (a int)", "create procedure p as begin insert u exec('select 5'); select 1; end");
        AreEqual(8164, Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("insert t exec p")).Number);
        AreEqual("1|0", (string?)simulation.ExecuteScalar("select concat((select count(*) from t where a = 1), '|', (select count(*) from u))"));
    }

    [TestMethod]
    public void InsertExecWithOutput_EndsTheBatch()
    {
        var simulation = new Simulation();
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("create table t (a int); insert t output inserted.a exec ('select 1'); create table after_error (a int)"));
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from sys.tables where name = 'after_error'"));
    }

    [TestMethod]
    public void ASysnameParameter_ReachesAnNVarcharColumn()
        => AreEqual("db", new Simulation().ExecuteScalar("""
            create table t (a int);
            insert t values (1);
            create table #u (n nvarchar(128) not null);
            insert #u exec sp_executesql N'select @i from t', N'@i sysname', N'db';
            select n from #u
            """));
}
