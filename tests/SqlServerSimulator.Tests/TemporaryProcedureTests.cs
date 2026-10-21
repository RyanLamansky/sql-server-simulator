using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Temporary procedures, local (<c>#p</c>) and global (<c>##p</c>), as SQL
/// Server 2025 keeps them in <c>tempdb</c>: a local one scoped to the session
/// that creates it and listed under its padded name, a global one visible to
/// every session, both dropped when their creator's session ends (probed
/// 2026-10-16).
/// </summary>
[TestClass]
public sealed class TemporaryProcedureTests
{
    private static object? Scalar(DbConnection connection, string commandText) =>
        connection.CreateCommand(commandText).ExecuteScalar();

    private static void Run(DbConnection connection, string commandText) =>
        _ = connection.CreateCommand(commandText).ExecuteNonQuery();

    private static List<int> Messages(DbConnection connection)
    {
        var numbers = new List<int>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => numbers.AddRange(e.Errors.Cast<SimulatedError>().Select(error => error.Number));
        return numbers;
    }

    [TestMethod]
    public void Local_RunsAndIsListedInTempdbUnderItsPaddedName()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "create proc #p @x int as select @x * 2");
        AreEqual(42, Scalar(connection, "exec #p 21"));
        AreEqual("P |SQL_STORED_PROCEDURE|dbo|128|#p______________________________________________________________________________________________________________________00000001", Scalar(connection, """
            select concat_ws('|', o.type collate database_default, o.type_desc collate database_default, s.name, len(o.name), o.name)
            from tempdb.sys.objects o join tempdb.sys.procedures p on p.object_id = o.object_id join tempdb.sys.schemas s on s.schema_id = o.schema_id
            where o.name like '#p[_]%'
            """));
        AreEqual(1, Scalar(connection, "select count(*) from tempdb.sys.sql_modules where object_id = object_id('tempdb..#p') and definition = 'create proc #p @x int as select @x * 2'"));
        AreEqual(1, Scalar(connection, "select count(*) from tempdb.sys.parameters where object_id = object_id('tempdb..#p') and name = '@x'"));
        AreEqual(1, Scalar(connection, "select count(*) from tempdb.INFORMATION_SCHEMA.ROUTINES where ROUTINE_NAME like '#p[_]%' and ROUTINE_TYPE = 'PROCEDURE'"));
        // Outside tempdb only a name naming tempdb finds it.
        AreEqual(0, Scalar(connection, "select count(*) where object_id('#p') is not null or object_id('master..#p') is not null"));
        Run(connection, "use tempdb");
        AreEqual(Scalar(connection, "select object_id from sys.objects where name like '#p[_]%'"), Scalar(connection, "select object_id('#p')"));
        AreEqual(1, Scalar(connection, "select objectproperty(object_id('#p'), 'IsProcedure')"));
    }

    [TestMethod]
    public void Local_OtherSessionsNeitherSeeNorCollideWithIt()
    {
        var sim = new Simulation();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        Run(first, "create proc #p as select 'first'");
        AreEqual(2812, Throws<SimulatedSqlException>(() => Run(second, "exec #p")).Number);
        AreEqual(DBNull.Value, Scalar(second, "select object_id('tempdb..#p')"));
        AreEqual(1, Scalar(second, "select count(*) from tempdb.sys.objects where name like '#p[_]%'"));
        Run(second, "create proc #p as select 'second'");
        AreEqual("first", Scalar(first, "exec #p"));
        AreEqual("second", Scalar(second, "exec #p"));
        AreEqual(2, Scalar(first, "select count(distinct right(name, 8)) from tempdb.sys.objects where name like '#p[_]%'"));
        Run(second, "drop proc #p");
        AreEqual("first", Scalar(first, "exec #p"));
    }

    [TestMethod]
    public void Local_EveryProcedureOfOneSessionSharesItsSuffix()
        => AreEqual(1, new Simulation().ExecuteBatchesScalar(
            "create proc #a as select 1",
            "create proc #b as select 1",
            "select count(distinct right(name, 8)) from tempdb.sys.objects where name like '#[ab][_]%'"));

    [TestMethod]
    public void Local_DroppedWhenItsSessionEnds()
    {
        var sim = new Simulation();
        using (var creator = sim.CreateOpenConnection())
            Run(creator, "create proc #p as select 1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from tempdb.sys.objects where name like '#p[_]%'"));
    }

    [TestMethod]
    public void Local_AlterDropAndTheirRefusals()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "create proc #p as select 1");
        var duplicate = Throws<SimulatedSqlException>(() => Run(connection, "create proc #p as select 2"));
        AreEqual((2714, (byte)3, "#p"), (duplicate.Number, duplicate.State, duplicate.Errors[0].Procedure));
        Run(connection, "alter proc #p as select 'altered'");
        AreEqual("altered", Scalar(connection, "exec #p"));
        Run(connection, "create or alter proc #p as select 'again'");
        AreEqual("again", Scalar(connection, "exec #p"));
        Run(connection, "drop proc #p");
        AreEqual(2812, Throws<SimulatedSqlException>(() => Run(connection, "exec #p")).Number);
        var missing = Throws<SimulatedSqlException>(() => Run(connection, "drop proc #p"));
        AreEqual((3701, "Cannot drop the procedure '#p', because it does not exist or you do not have permission."), (missing.Number, missing.Errors[0].Message));
        Run(connection, "drop procedure if exists #p");
        var alterMissing = Throws<SimulatedSqlException>(() => Run(connection, "alter proc #p as select 1"));
        AreEqual((208, (byte)6), (alterMissing.Number, alterMissing.State));
    }

    [TestMethod]
    public void Local_CreateAndDropRollBack()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "begin tran; exec ('create proc #p as select 1'); rollback");
        AreEqual(2812, Throws<SimulatedSqlException>(() => Run(connection, "exec #p")).Number);
        Run(connection, "create proc #q as select 'kept'");
        Run(connection, "begin tran; drop proc #q; rollback");
        AreEqual("kept", Scalar(connection, "exec #q"));
    }

    [TestMethod]
    public void CreatedInDynamicSqlOrAProcedure_OutlivesTheScope()
        => AreEqual("d|in", new Simulation().ExecuteBatchesScalar(
            "exec ('create proc #d as select ''d''')",
            "create proc dbo.outer1 as exec ('create proc #in as select ''in''')",
            "exec outer1",
            "create table #r (v varchar(9)); insert #r exec #d; insert #r exec #in",
            "select string_agg(v, '|') within group (order by v) from #r"));

    [TestMethod]
    public void Body_BindsInItsCreationDatabase_WhileDbNameAndDynamicSqlSeeTheCallers()
        => AreEqual("db1|db2|db2", new Simulation().ExecuteBatchesScalar(
            "create database db1",
            "create database db2",
            "create table db1.dbo.t (a varchar(9)); insert db1.dbo.t values ('db1'); create table db2.dbo.t (a varchar(9)); insert db2.dbo.t values ('db2')",
            "use db1",
            "create proc #p as begin create table #r (a varchar(9), d nvarchar(128), x varchar(9)); insert #r (a, d) select a, db_name() from t; insert #r (x) exec ('select a from t'); select concat_ws('|', max(a), max(d), max(x)) from #r end",
            "use db2",
            "exec #p"));

    [TestMethod]
    public void RunsFromNestedScopes()
        => AreEqual(3, new Simulation().ExecuteBatchesScalar(
            "create proc #p as select 1",
            "create proc dbo.caller as exec #p",
            "create table #r (a int); insert #r exec caller; insert #r exec ('exec #p'); insert #r exec sp_executesql N'exec #p'",
            "select count(*) from #r"));

    [TestMethod]
    public void Global_VisibleToEverySession_AndDroppedWithItsCreator()
    {
        var sim = new Simulation();
        using var other = sim.CreateOpenConnection();
        using (var creator = sim.CreateOpenConnection())
        {
            Run(creator, "create proc ##g as select 'g'");
            AreEqual("g", Scalar(other, "exec ##g"));
            AreEqual(1, Scalar(other, "select count(*) from tempdb.sys.objects where name = '##g' and object_id = object_id('tempdb..##g')"));
            var duplicate = Throws<SimulatedSqlException>(() => Run(other, "create proc ##g as select 2"));
            AreEqual((2714, (byte)3), (duplicate.Number, duplicate.State));
            Run(other, "alter proc ##g as select 'altered'");
            AreEqual("altered", Scalar(creator, "exec ##g"));
        }
        AreEqual(2812, Throws<SimulatedSqlException>(() => Run(other, "exec ##g")).Number);
        AreEqual(0, Scalar(other, "select count(*) from tempdb.sys.objects where name = '##g'"));
    }

    [TestMethod]
    public void SharesTheTemporaryNamespaceWithTempTables()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "create table #t (a int); create table ##gt (a int)");
        var overTable = Throws<SimulatedSqlException>(() => Run(connection, "create proc #t as select 1"));
        AreEqual((2714, (byte)6), (overTable.Number, overTable.State));
        AreEqual(2714, Throws<SimulatedSqlException>(() => Run(connection, "create proc ##gt as select 1")).Number);
        AreEqual(2010, Throws<SimulatedSqlException>(() => Run(connection, "create or alter proc #t as select 1")).Number);
        var exec = Throws<SimulatedSqlException>(() => Run(connection, "exec #t"));
        AreEqual(2809, exec.Number);
        Assert.Contains("'#t____", exec.Errors[0].Message);
        AreEqual(3705, Throws<SimulatedSqlException>(() => Run(connection, "drop proc #t")).Number);
        Run(connection, "create proc #p as select 1");
        AreEqual(2714, Throws<SimulatedSqlException>(() => Run(connection, "create table #p (a int)")).Number);
        AreEqual(2714, Throws<SimulatedSqlException>(() => Run(connection, "select 1 a into #p")).Number);
    }

    [TestMethod]
    public void AQualifiedName_IgnoresItsDatabaseWithMsg2701()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        var messages = Messages(connection);
        Run(connection, "create proc nosuch.dbo.#p as select 7");
        AreEqual("2701", string.Join(",", messages));
        messages.Clear();
        // The first exec names tempdb (ignored, one 2701) and compiles the
        // body, which names the stored 'nosuch' qualifier (a second 2701); a
        // later cached exec that names no database sends none (probed
        // 2026-10-21 against SQL Server 2025).
        AreEqual(7, Scalar(connection, "exec tempdb..#p"));
        AreEqual(7, Scalar(connection, "exec dbo.#p"));
        AreEqual("2701,2701", string.Join(",", messages));
        // A recompile compiles the body afresh, so the 'nosuch' 2701 returns.
        messages.Clear();
        AreEqual(7, Scalar(connection, "exec #p with recompile"));
        AreEqual("2701", string.Join(",", messages));
        var missing = Throws<SimulatedSqlException>(() => Run(connection, "exec tempdb..#nope"));
        AreEqual("Could not find stored procedure '#nope'.", missing.Errors[0].Message);
    }

    [TestMethod]
    public void Messages_NameItAsTempdbHoldsIt_AndErrorsAsWritten()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "create proc #p @x int as select 1/0");
        var missingArgument = Throws<SimulatedSqlException>(() => Run(connection, "exec tempdb..#p"));
        AreEqual(201, missingArgument.Number);
        Assert.Contains("'#p______", missingArgument.Errors[0].Message);
        AreEqual("#p", missingArgument.Errors[0].Procedure);
        Run(connection, "create table #r (v varchar(99)); begin try exec #p 1 end try begin catch insert #r select concat_ws('|', error_procedure(), error_number()) end catch");
        AreEqual("#p|8134", Scalar(connection, "select v from #r"));
    }

    [TestMethod]
    public void NameOver116Characters_Msg193()
        => new Simulation().AssertSqlError($"create proc #{new string('p', 116)} as select 1", 193);

    [TestMethod]
    public void NativelyCompiled_Msg12321()
        => new Simulation().AssertSqlError(
            "create proc #p with native_compilation, schemabinding as begin atomic with (transaction isolation level = snapshot, language = N'us_english') select 1 end",
            12321, "Creating a temporary procedure is not supported with natively compiled modules.");

    [TestMethod]
    [DataRow("create function #f() returns int as begin return 1 end", "1074,178")]
    [DataRow("create function #f() returns table as return select 1 a", "1074")]
    [DataRow("create view #v as select 1 a", "4103")]
    [DataRow("create synonym #s for dbo.x", "208")]
    [DataRow("create default #d as 1", "208")]
    [DataRow("create rule #r as @x > 1", "208")]
    public void OtherTemporaryModules_AreRefused(string statement, string numbers)
    {
        var error = new Simulation().AssertSqlError(statement, int.Parse(numbers.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture));
        AreEqual(numbers, string.Join(",", error.Errors.Select(entry => entry.Number)));
    }

    [TestMethod]
    public void TemporaryTriggerName_Msg208State77()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        var error = sim.AssertSqlError("create trigger #tr on t for insert as select 1", 208);
        AreEqual(((byte)77, "#tr"), (error.State, error.Errors[0].Procedure));
    }

    [TestMethod]
    public void SpHelpText_FindsNoText_WhileSpHelpDescribesIt()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        Run(connection, "create proc #p @x int as select 1");
        Run(connection, "use tempdb");
        var noText = Throws<SimulatedSqlException>(() => Run(connection, "exec sp_helptext '#p'"));
        AreEqual((15197, 91), (noText.Number, noText.LineNumber));
        using var reader = connection.CreateCommand("exec sp_help '#p'").ExecuteReader();
        IsTrue(reader.Read());
        StartsWith("#p_____", reader.GetString(0));
        AreEqual("stored procedure", reader.GetString(2).TrimEnd());
    }

    [TestMethod]
    public void AnyPrincipalMayCreateAndRunOne()
        => AreEqual("u", new Simulation().ExecuteBatchesScalar(
            "create login l with password = 'Pa55word!x'; create user u for login l",
            "execute as login = 'l'",
            "create proc #p as select user_name()",
            "create table #r (n sysname); insert #r exec #p; select n from #r"));

    [TestMethod]
    public void DatabaseDdlTriggers_DoNotFire()
        => AreEqual(0, new Simulation().ExecuteBatchesScalar(
            "create table fired (n int)",
            "create trigger ddl on database for create_procedure, drop_procedure as insert fired values (1)",
            "create proc #p as select 1",
            "drop proc #p",
            "select count(*) from fired"));
}
