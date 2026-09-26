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

    [TestMethod]
    public void UsedDatabase_DoesNotChangeTheCompileContext()
    {
        var (_, connection) = Open();
        var ex = Fails(connection, "print 'first'; use master; select b from t");
        AreEqual(207, ex.Number);
        IsEmpty(Messages(ex));
        AreEqual("simulated", Scalar(connection, "select db_name()"));
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
        CollectionAssert.AreEqual(new[] { 2715 }, scalar.Errors.Select(e => e.Number).ToArray());
        var table = Fails(connection, "create type dbo.t1 as table (id int); declare @t t1; insert @t values (5); select id from @t");
        CollectionAssert.AreEqual(new[] { 2715, 1087, 1087 }, table.Errors.Select(e => e.Number).ToArray());
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
    /// A syntax error the compile walk couldn't reach — past a statement
    /// naming a table the batch creates — still ends the batch when it
    /// surfaces, rather than the dispatch resuming inside the broken
    /// statement's tail, since real would have refused the whole batch.
    /// </summary>
    [TestMethod]
    public void SyntaxErrorPastADeferredTarget_EndsTheBatch()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create table t (a int); insert t (a) with (tablock) values (1); create table u (b int)", 156);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('u')"));
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
}
