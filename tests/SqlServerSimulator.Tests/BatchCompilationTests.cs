using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A batch compiles before any of it runs, so an error real raises while
/// compiling is the batch's whole response and nothing in the batch runs,
/// while a statement naming an object that doesn't exist yet defers to when it
/// runs. Every shape here was probed through SqlClient 7 against SQL Server
/// 2025 (2026-09-24).
/// </summary>
[TestClass]
public sealed class BatchCompilationTests
{
    private static (Simulation Simulation, SimulatedDbConnection Connection) Open(string setup = "create table t (a int)")
    {
        var simulation = new Simulation();
        var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = setup;
        _ = command.ExecuteNonQuery();
        return (simulation, connection);
    }

    /// <summary>
    /// The batch's informational messages, which a failed
    /// <c>ExecuteNonQuery</c> carries after its errors.
    /// </summary>
    private static string[] Messages(SimulatedSqlException ex) =>
        [.. ex.Errors.Where(e => e.Class == 0).Select(e => e.Message)];

    private static SimulatedSqlException Fails(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
    }

    private static object? Scalar(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    [DataRow("print 'first'; select nosuch from t", 207)]
    [DataRow("print 'first'; select top 0 1; select top (1.5) 1", 1060)]
    [DataRow("print 'first'; select a from t order by 5", 108)]
    [DataRow("print 'first'; insert t values (1, 2)", 213)]
    [DataRow("print 'first'; select @nope", 137)]
    [DataRow("print 'first'; declare @x int; declare @x int", 134)]
    [DataRow("print 'first'; select 1; create view v as select 1 a", 111)]
    [DataRow("print 'first'; if 1 = 0 select nosuch from t", 207)]
    [DataRow("print 'first'; while 1 = 0 begin select nosuch from t end", 207)]
    public void CompileError_RunsNothing(string sql, int number)
    {
        var (_, connection) = Open();
        var ex = Fails(connection, sql);
        AreEqual(number, ex.Number);
        IsEmpty(Messages(ex));
    }

    [TestMethod]
    public void CompileError_CreatesNothing()
    {
        var (_, connection) = Open();
        AreEqual(111, Fails(connection, "select 1; create view v as select 1 a").Number);
        AreEqual(0, Scalar(connection, "select count(*) from sys.views"));
    }

    /// <summary>
    /// Every statement's binder error is reported, in order, where the parse
    /// phase fails first on an undeclared variable, which it reports alone.
    /// </summary>
    [TestMethod]
    public void BinderErrorsAreGathered_ParsePhaseErrorsPreempt()
    {
        var (_, connection) = Open();
        CollectionAssert.AreEqual(
            new[] { "Invalid column name 'nosuch1'.", "Invalid column name 'nosuch2'." },
            Fails(connection, "select nosuch1 from t; select nosuch2 from t").Errors.Select(e => e.Message).ToArray());
        var preempted = Fails(connection, "select nosuch1 from t; insert t values (1, 2); select @nope");
        HasCount(1, preempted.Errors);
        AreEqual(137, preempted.Number);
    }

    /// <summary>
    /// A column the batch adds doesn't exist when the batch compiles, so a
    /// statement reading it fails the batch and the column is never added.
    /// </summary>
    [TestMethod]
    public void ColumnAddedInTheSameBatch_FailsTheBatch()
    {
        var (_, connection) = Open();
        AreEqual(207, Fails(connection, "alter table t add b int; select b from t").Number);
        AreEqual(1, Scalar(connection, "select count(*) from sys.columns where object_id = object_id('t')"));
    }

    [TestMethod]
    [DataRow("drop table t; create table t (b int); select b from t")]
    [DataRow("drop table #t; create table #t (b int); select b from #t")]
    public void TableRecreatedInTheSameBatch_BindsTheOldDefinition(string sql)
    {
        var (_, connection) = Open("create table t (a int); create table #t (a int)");
        AreEqual(207, Fails(connection, sql).Number);
        AreEqual(1, Scalar(connection, "select count(*) from sys.columns where object_id = object_id('t') and name = 'a'"));
    }

    /// <summary>
    /// The statements after a <c>USE</c> compile in the database it names,
    /// before any of the batch runs (probed 2026-09-28 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    public void UsedDatabase_IsWhereTheRestCompiles()
    {
        var (_, connection) = Open("create table t (a int); create database other");
        _ = Scalar(connection, "use other; create table t (b int); use simulated");
        var ex = Fails(connection, "print 'first'; use other; select a from t");
        AreEqual(207, ex.Number);
        IsEmpty(Messages(ex));
        AreEqual("simulated", Scalar(connection, "select db_name()"));
        AreEqual("other", Scalar(connection, "use other; select db_name() from t right join (values (1)) v (x) on b = x"));
    }

    /// <summary>
    /// A statement naming a table the batch creates compiles when it runs, so
    /// the statements before it have run by the time its error surfaces.
    /// </summary>
    [TestMethod]
    public void TableCreatedInTheBatch_DefersItsStatements()
    {
        var (_, connection) = Open();
        var ex = Fails(connection, "print 'first'; create table t2 (a int); select nosuch from t2");
        AreEqual(207, ex.Number);
        CollectionAssert.AreEqual(new[] { "first" }, Messages(ex));
        AreEqual(1, Scalar(connection, "select count(*) from sys.tables where name = 't2'"));
    }

    [TestMethod]
    public void CompileError_IsNotCaught()
    {
        var (_, connection) = Open();
        var ex = Fails(connection, "begin try select nosuch from t end try begin catch print 'caught' end catch; print 'after'");
        AreEqual(207, ex.Number);
        IsEmpty(Messages(ex));
    }

    [TestMethod]
    public void CompileError_SetsAtAtError()
    {
        var (_, connection) = Open();
        _ = Fails(connection, "select nosuch from t");
        AreEqual(207, Scalar(connection, "select @@error"));
    }

    /// <summary>
    /// Dynamic SQL is a batch of its own: an error compiling it, or a missing
    /// object ending it, is the <c>EXEC</c>'s error, and the caller goes on.
    /// So does a procedure whose statement names a missing object.
    /// </summary>
    [TestMethod]
    [DataRow("exec ('print ''inner''; select nosuch from t')", 207)]
    [DataRow("exec sp_executesql N'select nosuch from t'", 207)]
    [DataRow("exec ('select * from nope')", 208)]
    [DataRow("exec p", 208)]
    public void CalledBatchError_LetsTheCallerContinue(string exec, int number)
    {
        var (simulation, connection) = Open();
        simulation.ExecuteBatches("create proc p as select * from nope");
        var ex = Fails(connection, $"print 'first'; {exec}; print 'after'");
        AreEqual(number, ex.Number);
        CollectionAssert.AreEqual(new[] { "first", "after" }, Messages(ex));
    }

    [TestMethod]
    public void CalledBatchError_IsCaughtByTheCaller()
        => AreEqual(207, new Simulation().ExecuteScalar(
            "begin try exec ('select nosuch from sys.objects') end try begin catch select error_number() end catch"));

    /// <summary>
    /// A missing data type is reported and the variable declared anyway, so a
    /// later scalar reference binds, while a table use of it is Msg 1087.
    /// </summary>
    [TestMethod]
    public void MissingDeclaredType_DeclaresTheVariableAnyway()
    {
        var (_, connection) = Open();
        var scalar = Fails(connection, "create type dbo.Probe from int; declare @v dbo.Probe; set @v = 42; select @v");
        CollectionAssert.AreEqual(new[] { 2715, 2724 }, scalar.Errors.Select(e => e.Number).ToArray());
        var table = Fails(connection, "create type dbo.t1 as table (id int); declare @t t1; insert @t values (5); select id from @t");
        CollectionAssert.AreEqual(new[] { 2715, 1087, 1087, 2724 }, table.Errors.Select(e => e.Number).ToArray());
    }

    /// <summary>
    /// The compile doesn't run anything, so session state the batch sets
    /// before a statement that depends on it is in place when that statement
    /// runs.
    /// </summary>
    [TestMethod]
    public void SessionStateSetInTheBatch_AppliesWhenItsStatementRuns()
    {
        var (_, connection) = Open("create table i (id int identity, v int); create sequence s");
        AreEqual(5, Scalar(connection, "set identity_insert i on; insert i (id, v) values (5, 1); set identity_insert i off; select id from i"));
        AreEqual(10L, Scalar(connection, "alter sequence s restart with 10; select next value for s"));
    }

    [TestMethod]
    public void UnterminatedMergeAtTheEndOfTheBatch_RaisesMsg10713()
    {
        var (_, connection) = Open("create table m (a int primary key)");
        AreEqual(10713, Fails(connection, "merge m as d using (select 1 as a) s on d.a = s.a when not matched then insert (a) values (s.a)").Number);
    }

    /// <summary>
    /// A batch that compiled is remembered under its text, so repeating it
    /// skips the compile — until the schema changes.
    /// </summary>
    [TestMethod]
    public void RepeatedBatch_RecompilesAfterSchemaChange()
    {
        var (_, connection) = Open();
        const string Batch = "print 'first'; select a from t";
        using (var command = connection.CreateCommand())
        {
            command.CommandText = Batch;
            _ = command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "exec sp_rename 't.a', 'b', 'column'";
            _ = command.ExecuteNonQuery();
        }
        var ex = Fails(connection, Batch);
        AreEqual(207, ex.Number);
        IsEmpty(Messages(ex));
    }

    /// <summary>
    /// A syntax error in a statement writing a table the batch creates refuses
    /// the whole batch while compiling.
    /// </summary>
    [TestMethod]
    public void SyntaxErrorInADeferredWrite_RunsNothing()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create table t (a int); insert t (a) with (tablock) values (1); create table u (b int)", 156);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
    }

    /// <summary>
    /// Real parses the whole batch before running any of it and defers only
    /// the binding of a statement over an object that doesn't exist yet, so
    /// the compile walks on past a write to a table the batch creates: a later
    /// statement's syntax error — or its binder error, or one in the write's
    /// own <c>VALUES</c>, which read no column of the target — refuses the
    /// batch before anything runs, from inside an <c>IF</c> or a <c>TRY</c>
    /// too, while a binder error that reads the missing table waits for the
    /// run (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert t2 values (1); select [abs](-1), a from t2", 102)]
    [DataRow("insert t2 select 1; select 1 +", 102)]
    [DataRow("update t2 set a = 2 where a = 1; select 1 +", 102)]
    [DataRow("delete t2 where a = 1; select 1 +", 102)]
    [DataRow("merge t2 using (select 1 a) s on t2.a = s.a when not matched then insert values (s.a); select 1 +", 102)]
    [DataRow("insert t2 values (1); select nosuch from t", 207)]
    [DataRow("insert t2 values (nosuch)", 207)]
    [DataRow("insert t2 values (cast(1 as xml))", 529)]
    [DataRow("if 1 = 1 insert t2 values (1) else print 'x'; select 1 +", 102)]
    [DataRow("begin try insert t2 values (1); select 1 + end try begin catch print 'caught' end catch", 156)]
    [DataRow("select nosuch from t; insert t2 values (1); select [abs](-1)", 102)]
    [DataRow("insert t2 values (1); select @nope", 137)]
    public void CompilePastADeferredWrite_RunsNothing(string tail, int number)
    {
        var (_, connection) = Open();
        var ex = Fails(connection, "print 'first'; create table t2 (a int); " + tail);
        AreEqual(number, ex.Number);
        IsEmpty(Messages(ex));
        AreEqual(DBNull.Value, Scalar(connection, "select object_id('t2')"));
    }

    [TestMethod]
    public void CompilePastADeferredWrite_ReportsBinderErrorsOnBothSides()
    {
        var (_, connection) = Open();
        var ex = Fails(connection, "select nosuch1 from t; create table t2 (a int); insert t2 values (1); select nosuch2 from t");
        CollectionAssert.AreEqual(new[] { "Invalid column name 'nosuch1'.", "Invalid column name 'nosuch2'." }, ex.Errors.Select(e => e.Message).ToArray());
    }

    [TestMethod]
    public void BinderErrorReadingTheDeferredTable_WaitsForTheRun()
    {
        var (_, connection) = Open();
        var ex = Fails(connection, "print 'first'; create table t2 (a int); update t2 set a = 2 where nosuch = 1; print 'after'");
        AreEqual(207, ex.Number);
        CollectionAssert.AreEqual(new[] { "first" }, Messages(ex));
    }

    /// <summary>
    /// A procedure compiles again as its call first runs it, so a statement
    /// naming a table created after the procedure — and a body's untaken
    /// branch alike — reports every binder error before the body's first
    /// statement runs. The error is the <c>EXEC</c>'s own: the caller goes on,
    /// a <c>TRY</c> around it catches it, no return status or <c>OUTPUT</c>
    /// value comes back, and no plan is kept, so the next call reports again
    /// until the table changes (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ProcedureCompileAtFirstCall_ReportsTheBodysErrorsBeforeItRuns()
    {
        var (simulation, connection) = Open();
        simulation.ExecuteBatches(
            "create procedure p @o int = 0 output as begin print 'body'; set @o = 5; if @o = 0 select nosuch1 from t2; select a from t2 where nosuch2 = 1; return 7 end",
            "create table t2 (a int)");
        var ex = Fails(connection, "declare @r int = 1, @o int = 1; exec @r = p @o output; exec @r = p @o output; print concat(@r, ' ', @o)");
        CollectionAssert.AreEqual(
            new[] { "Invalid column name 'nosuch1'.", "Invalid column name 'nosuch2'.", "Invalid column name 'nosuch1'.", "Invalid column name 'nosuch2'.", "1 1" },
            ex.Errors.Select(e => e.Message).ToArray());
        AreEqual("p", ex.Errors[0].Procedure);
        AreEqual("207 p", Scalar(connection, "begin try exec p end try begin catch select concat(error_number(), ' ', error_procedure()) end catch"));
        _ = Scalar(connection, "alter table t2 add nosuch1 int, nosuch2 int");
        using var command = connection.CreateCommand();
        command.CommandText = "declare @r int; exec @r = p; select @r";
        using var reader = command.ExecuteReader();
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(7, reader.GetInt32(0));
    }

    /// <summary>
    /// A grouping or type error the compile meets is the call's own as well,
    /// where the body's run would have ended the caller's batch.
    /// </summary>
    [TestMethod]
    [DataRow("select a, count(*) from t2", 8120)]
    [DataRow("select a from t2 where a = cast(1 as xml)", 529)]
    public void ProcedureCompileAtFirstCall_LetsTheCallerContinue(string statement, int number)
    {
        var (simulation, connection) = Open();
        simulation.ExecuteBatches($"create procedure p as begin print 'body'; {statement} end", "create table t2 (a int)");
        var ex = Fails(connection, "exec p; print 'after'");
        AreEqual(number, ex.Number);
        CollectionAssert.AreEqual(new[] { "after" }, Messages(ex));
    }

    /// <summary>
    /// A DML trigger compiles as it first fires: an error compiling it ends
    /// the firing statement as one its body raised would — the batch ends and
    /// the write rolls back — and a <c>TRY</c> around the statement catches it,
    /// as it does a missing object the body names when it runs.
    /// </summary>
    [TestMethod]
    [DataRow("select nosuch from t2")]
    [DataRow("select * from nope")]
    public void TriggerError_EndsTheFiringBatchUnlessCaught(string statement)
    {
        var (simulation, connection) = Open();
        simulation.ExecuteBatches($"create trigger tr on t after insert as begin print 'body'; {statement} end", "create table t2 (a int)");
        var ex = Fails(connection, "insert t values (1); print 'after'");
        IsFalse(Messages(ex).Contains("after"));
        AreEqual(0, Scalar(connection, "select count(*) from t"));
        AreEqual("tr 0", Scalar(connection, "begin try insert t values (1) end try begin catch select concat(error_procedure(), ' ', @@trancount) end catch"));
    }

    /// <summary>
    /// A batch that creates one temp table twice is refused while compiling —
    /// Msg 2714 state 1, nothing run — whether by CREATE TABLE or SELECT INTO,
    /// from opposite IF branches or with a DROP between; a module body doing
    /// it is refused at CREATE, and a dynamic batch doing it fails only its
    /// EXEC (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table #t (a int); create table #t (a int)")]
    [DataRow("create table #t (a int); drop table #t; create table #t (b int)")]
    [DataRow("print 'x'; if 1 = 0 create table #t (a int) else create table #t (b int)")]
    [DataRow("select 1 a into #t; create table #t (a int)")]
    [DataRow("select 1 a into #t; select 1 a into #t")]
    [DataRow("create table ##g (a int); create table ##g (a int)")]
    public void TempTableCreatedTwice_FailsTheBatch(string sql)
    {
        var simulation = new Simulation();
        AreEqual((byte)1, simulation.AssertSqlError(sql, 2714).State);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('tempdb..#t')"));
    }

    [TestMethod]
    public void TempTableCreatedTwice_InAProcedure_RefusesTheCreate()
    {
        var simulation = new Simulation();
        AreEqual((byte)1, simulation.AssertSqlError("create proc p as create table #t (a int); create table #t (a int)", 2714).State);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('p')"));
    }

    [TestMethod]
    public void TempTableCreatedTwice_InDynamicSql_FailsOnlyTheExec()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("exec ('create table #t (a int); create table #t (a int)'); create table u (a int)", 2714);
        AreNotEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('u')"));
    }

    /// <summary>
    /// Real's parser recovers from a syntax error and reports the ones after
    /// it, holding back any met before three tokens have parsed again (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select 1 +;\nselect 2 +;", "102@1 102@2")]
    [DataRow("select 1 frm t;\nselect * from where;", "102@1 156@2")]
    [DataRow("select * from (select 1 a) d NATURAL JOIN (select 1 a) e;", "102@1 102@1")]
    [DataRow("select (1;\nselect 2;", "102@1")]
    [DataRow("select from t;\nselect 3;", "156@1")]
    [DataRow("select 1;\ninsert t (c) with (tablock) values (1);", "156@2 319@2")]
    [DataRow("select 1;\nmerge t as a with (holdlock) using (select 1 c) s on a.c = s.c when not matched then insert values (s.c);", "156@2 319@2 102@2")]
    [DataRow("select 1 with;", "319@1")]
    [DataRow("(select 1) as q", "156@1")]
    [DataRow("begin select 1 +; select 2 end", "102@1")]
    [DataRow("begin select 1 +; select 2 + end", "102@1 156@1")]
    [DataRow("begin try select 1 +; end try begin catch select 3 end catch", "102@1")]
    [DataRow("if 1=1 begin select 1 +; select 2 end select 3 +; select 4", "102@1 102@1")]
    [DataRow("begin select case when 1 +; select 2\nend\nend", "102@1 102@3")]
    [DataRow("begin tran; select 1 +; select 2 end", "102@1 102@1")]
    [DataRow("select * from where;\nselect case when 1 then 2 end", "156@1 4145@2")]
    [DataRow("select 1 +;\nif 1 select 2", "102@1 4145@2")]
    [DataRow("begin\n  if 1=1 begin select * from where; end\n  select case when 1 then\nend", "156@2 4145@3")]
    [DataRow("select case when 1 then 2 end;\nselect 1 +;", "4145@1 102@2")]
    [DataRow("select case when 1 then 2 end; select case when 2 then 3 end", "4145@1 4145@1")]
    [DataRow("select 1 where 1 and 2; select 1 +;", "4145@1 102@1")]
    [DataRow("select 1 where 1; select 2 +", "4145@1 102@1")]
    [DataRow("select 1 where 1\nselect 2 +", "4145@2 102@2")]
    [DataRow("select 2 +; select 1 where 1", "102@1 4145@1")]
    [DataRow("select 1 where 1; select nosuch from sys.objects", "4145@1")]
    [DataRow("select 1 where 1 select 2 + 3 +", "4145@1 102@1")]
    [DataRow("select 1 where 1; select 2 from", "4145@1 102@1")]
    [DataRow("select 1 where 1; select @nope", "4145@1 137@1")]
    [DataRow("select @nope; select 1 +;", "137@1 102@1")]
    [DataRow("select @nope; select @nope2; select 3", "137@1 137@1")]
    [DataRow("declare @a int = @b + @c", "137@1")]
    [DataRow("select @nope\nselect 2 +", "137@1 102@2")]
    [DataRow("select @n1 + @n2; select 3", "137@1")]
    [DataRow("select @nope; select 1 where 1", "137@1 4145@1")]
    [DataRow("set @x = 1; select 2 +;", "137@1 102@1")]
    [DataRow("set @x = 1 +", "102@1")]
    [DataRow("exec sp_executesql N'select @q; select 1 +'", "137@1 102@1")]
    public void SyntaxErrors_ReportThoseRecoveryReaches(string batch, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (c int)");
        var ex = sim.AssertSqlError(batch, int.Parse(expected[..expected.IndexOf('@')], System.Globalization.CultureInfo.InvariantCulture));
        AreEqual(expected, string.Join(" ", ex.Errors.Cast<SimulatedError>().Select(error => $"{error.Number}@{error.LineNumber}")));
    }

    /// <summary>
    /// Recovery runs inside a module body as it does in a batch, reading the
    /// rest of the body as statements with the body's own frame and the
    /// variables it declared, and an END closing a block the error left open
    /// as the block's end (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create procedure p as\nselect 1 +;\nselect 2 +;", "102@2 102@3")]
    [DataRow("create function f() returns int as begin declare @x int; set @x = 1 +; set @x = 2 +; return 1 end", "102@1 102@1")]
    [DataRow("create function f() returns table as return select 1 + from (select 1 a) q", "156@1 102@1")]
    [DataRow("create procedure p as if 1=1 begin select 1 +; end else select 2", "102@1")]
    [DataRow("create procedure p as begin try select 1 +; end try begin catch select 2 end catch", "102@1")]
    [DataRow("create function f() returns int as begin declare @x int; if 1=1 begin set @x = 1 +; end; return @x end", "102@1")]
    [DataRow("create trigger tr on dbo.t after insert as begin select 1 +; select 2 end", "102@1")]
    [DataRow("create procedure p as while 1=0 begin select 1 +; break; end", "102@1")]
    [DataRow("create procedure p as\nselect 1 + ;\ndeclare @y int;\nset @y = 2 +;\nselect @y", "102@2 102@4")]
    [DataRow("create function f() returns @r table (a int, b int) as begin insert @r select 1 +, 2; insert @r select 3 +, 4; return end", "102@1 102@1")]
    [DataRow("create procedure p as select * from dbo.t where c = ; select 1 from dbo.t where c = ;", "102@1 102@1")]
    [DataRow("create function f() returns int as begin return 1 +; end", "102@1")]
    [DataRow("create procedure p @a int as select @a +; select @a", "102@1")]
    [DataRow("create procedure p as begin select 1 +; select 2\nend\nselect 3\nend", "102@1 102@4")]
    [DataRow("create procedure p as\nbegin\n  if 1=1 begin select * from where; end\n  select case when 1 then\nend", "156@3 4145@4")]
    [DataRow("create procedure p as if 1 select 2\nselect 1 +", "4145@1 102@2")]
    [DataRow("create procedure p as select 1 where 1; select 2 +", "4145@1 102@1")]
    [DataRow("create procedure p as select 1 where 1\nselect 2 +", "4145@2 102@2")]
    [DataRow("create procedure p as select 1 where 1; select nosuch from sys.objects", "4145@1")]
    [DataRow("create function f() returns int as begin if 1 return 1; return 2 + end", "4145@1 156@1")]
    [DataRow("create function f() returns int as begin return 2 + end", "156@1")]
    [DataRow("create function f() returns int as begin\nreturn 2 +\nend", "156@3")]
    [DataRow("create function f() returns int as begin declare @x int; set @x = 1 +; return 2 + end", "102@1 156@1")]
    [DataRow("create function f() returns @r table (a int) as begin insert @r select 1 +; insert @r select 2 + end", "102@1 156@1")]
    [DataRow("create function f() returns int as begin declare @x int; select @x = 1 where 1; return 2 + END", "4145@1 156@1")]
    [DataRow("create procedure p as\nbegin\n  declare @c int = 1 +;\n  select @c;\n  set @c = ;\nend", "102@3 137@4 102@5")]
    [DataRow("create function f() returns int as begin declare @x int = 1 +; return @x end", "102@1 137@1")]
    public void SyntaxErrors_RecoveryInsideAModuleBody(string batch, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (c int)");
        var ex = sim.AssertSqlError(batch, int.Parse(expected[..expected.IndexOf('@')], System.Globalization.CultureInfo.InvariantCulture));
        AreEqual(expected, string.Join(" ", ex.Errors.Cast<SimulatedError>().Select(error => $"{error.Number}@{error.LineNumber}")));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('p')"));
    }

    /// <summary>
    /// A <c>SET</c> to an undeclared variable is Msg 137 at state 1, checked
    /// once the statement has parsed — after a syntax error or an undeclared
    /// variable on its right-hand side (state 2) — where a read is state 2
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("set @x = 1", "137/1 @x")]
    [DataRow("set @x += 1", "137/1 @x")]
    [DataRow("set @x.modify('delete /a')", "137/1 @x")]
    [DataRow("set @x = @y", "137/2 @y")]
    [DataRow("set @x = (select 1 from nosuch)", "137/1 @x")]
    [DataRow("select @x", "137/2 @x")]
    public void SetTargetUndeclared_IsState1(string batch, string expected)
    {
        var error = new Simulation().AssertSqlError(batch, 137);
        AreEqual(expected, $"{error.Number}/{error.State} {error.Message[error.Message.IndexOf('@')..^2]}");
    }
}
