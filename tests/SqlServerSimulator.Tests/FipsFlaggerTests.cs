using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET FIPS_FLAGGER</c>'s warnings — Msg 1021 for a statement, 1022 for a
/// clause, 1024 for a function and 1025 for an identifier longer than 18
/// characters — sent as a batch compiles, each case probed 2026-10-17 against
/// SQL Server 2025 with the statement on the batch's second line.
/// </summary>
[TestClass]
public sealed class FipsFlaggerTests
{
    private static string Warnings(string level, string batch)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand("create table t (a int primary key, b varchar(10)); create table u (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand($"set fips_flagger '{level}'").ExecuteNonQuery();
        var log = new List<string>();
        void Note(SimulatedError error)
        {
            if (error.Number is >= 1021 and <= 1025)
                log.Add(string.Create(CultureInfo.InvariantCulture, $"{error.Number}/{error.State}@{error.LineNumber} {error.Message.Replace("FIPS Warning: ", "", StringComparison.Ordinal)}"));
        }
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors.Cast<SimulatedError>())
                Note(error);
        };
        try
        {
            _ = connection.CreateCommand("--\n" + batch).ExecuteNonQuery();
        }
        catch (SimulatedSqlException ex)
        {
            foreach (var error in ex.Errors.Cast<SimulatedError>())
                Note(error);
        }
        return string.Join(" | ", log);
    }

    [TestMethod]
    [DataRow("print 1", "1021/1@2 Line 2 has the non-ANSI statement 'PRINT'.")]
    [DataRow("select a from t", "")]
    [DataRow("create table v (a int)", "")]
    [DataRow("truncate table u", "1021/1@2 Line 2 has the non-ANSI statement 'TRUNCATE TABLE'.")]
    [DataRow("create unique index ix on t (b)", "1021/1@2 Line 2 has the non-ANSI statement 'CREATE INDEX'.")]
    [DataRow("drop table u", "1021/1@2 Line 2 has the non-ANSI statement 'DROP'.")]
    [DataRow("begin tran", "1021/1@2 Line 2 has the non-ANSI statement 'BEGIN TRANSACTION'.")]
    [DataRow("set nocount on", "1021/50@2 Line 2 has the non-ANSI statement 'SET'.")]
    [DataRow("set datefirst 1", "1021/1@2 Line 2 has the non-ANSI statement 'SET'.")]
    [DataRow("declare @x int\nset @x = 1", "")]
    [DataRow("with c as (select 1 a) select a from c", "1021/1@2 Line 2 has the non-ANSI statement 'WITH'.")]
    [DataRow("exec ('select 1')", "1021/1@2 Line 2 has the non-ANSI statement 'EXECUTE'.")]
    [DataRow("insert into u exec ('select 1')", "1021/1@2 Line 2 has the non-ANSI statement 'EXECUTE'.")]
    [DataRow("if 1 = 1 select 1", "")]
    [DataRow("if 1 = 1\nselect 1\nelse\nselect 2", "1021/1@4 Line -1 has the non-ANSI statement 'IF'.")]
    [DataRow("while 1 = 0\nbegin\nselect 1\nend\nselect 3", "1021/1@6 Line -1 has the non-ANSI statement 'WHILE'.")]
    [DataRow("while 1 = 0 begin break end", "1021/1@2 Line 2 has the non-ANSI statement 'BREAK'. | 1021/1@2 Line -1 has the non-ANSI statement 'WHILE'.")]
    [DataRow("select top 1 a from t", "1022/62@2 Line 2 has the non-ANSI clause 'TOP'.")]
    [DataRow("delete top (1) from u", "")]
    [DataRow("update top (1) t set a = a", "1022/62@2 Line 2 has the non-ANSI clause 'TOP'.")]
    [DataRow("select top 1 * from (select top 1 * from t) d", "1022/62@2 Line 2 has the non-ANSI clause 'TOP'. | 1022/62@2 Line 2 has the non-ANSI clause 'TOP'.")]
    [DataRow("select coalesce(1, 2), nullif(1, 2), isnull(1, 2)", "1022/6@2 Line 2 has the non-ANSI clause 'CASE'. | 1022/6@2 Line 2 has the non-ANSI clause 'CASE'.")]
    [DataRow("select\n1,\ncase when 1 = 1 then 1 end", "1022/6@4 Line 4 has the non-ANSI clause 'CASE'.")]
    [DataRow("select current_timestamp, convert(int, 1), cast(1 as int), getdate()", "1024/6@2 Line 2 has the non-ANSI function 'current_timestamp'. | 1024/5@2 Line 2 has the non-ANSI function 'convert'.")]
    [DataRow("select abcdefghijklmnopqr = 1", "")]
    [DataRow("select abcdefghijklmnopqrs = 1", "1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("select 1 as [abcdefghijklmnopqrs], 2 as \"abcdefghijklmnopqrt\"", "1025/2@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("select\nabcdefghijklmnopqrs = 1", "1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("declare @abcdefghijklmnopqrs int", "")]
    [DataRow("select top 1 abcdefghijklmnopqrs = convert(int, 1), case when 1 = 1 then 1 end abcdefghijklmnopqrt from t",
        "1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18. | 1022/62@2 Line 2 has the non-ANSI clause 'TOP'. | 1024/5@2 Line 2 has the non-ANSI function 'convert'. | 1022/6@2 Line 2 has the non-ANSI clause 'CASE'. | 1025/1@2 The length of identifier 'abcdefghijklmnopqrt' exceeds 18.")]
    [DataRow("set implicit_transactions off", "1025/1@2 The length of identifier 'implicit_transactions' exceeds 18. | 1021/50@2 Line 2 has the non-ANSI statement 'SET'.")]
    [DataRow("create sequence abcdefghijklmnopqrs", "1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18. | 1021/1@2 Line 2 has the non-ANSI statement 'CREATE SEQUENCE'.")]
    [DataRow("create procedure abcdefghijklmnopqrs as print 1",
        "1021/1@2 Line 2 has the non-ANSI statement 'CREATE PROCEDURE'. | 1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18. | 1021/1@2 Line 2 has the non-ANSI statement 'PRINT'.")]
    [DataRow("create or alter procedure abcdefghijklmnopqrs as select 1",
        "1021/1@2 Line 2 has the non-ANSI statement 'CREATE PROCEDURE'. | 1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("drop index t.abcdefghijklmnopqrs", "1021/1@2 Line 2 has the non-ANSI statement 'DROP INDEX'. | 1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("print abcdefghijklmnopqrs", "1021/1@2 Line 2 has the non-ANSI statement 'PRINT'. | 1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("set rowcount abcdefghijklmnopqrs", "1025/1@2 The length of identifier 'abcdefghijklmnopqrs' exceeds 18.")]
    [DataRow("declare c cursor for select 1\ndeallocate c", "")]
    public void Entry_FlagsWhatRealFlags(string batch, string expected) => AreEqual(expected, Warnings("ENTRY", batch));

    [TestMethod]
    [DataRow("ENTRY", "1022/6@2 Line 2 has the non-ANSI clause 'CASE'. | 1024/5@2 Line 2 has the non-ANSI function 'convert'.")]
    [DataRow("INTERMEDIATE", "1024/5@2 Line 2 has the non-ANSI function 'convert'.")]
    [DataRow("FULL", "")]
    public void Levels_FlagCaseAndConvertAsRealDoes(string level, string expected) =>
        AreEqual(expected, Warnings(level, "select case when 1 = 1 then 1 end, convert(int, 1)"));

    [TestMethod]
    [DataRow("ENTRY", "")]
    [DataRow("INTERMEDIATE", "1021/1@3 Line 3 has the non-ANSI statement 'DEALLOCATE'.")]
    [DataRow("FULL", "1021/1@3 Line 3 has the non-ANSI statement 'DEALLOCATE'.")]
    public void Deallocate_IsFlaggedAboveEntry(string level, string expected) =>
        AreEqual(expected, Warnings(level, "declare c cursor for select 1\ndeallocate c"));

    /// <summary>
    /// The level applies as the batch parses: a <c>SET FIPS_FLAGGER</c> turning
    /// it on flags itself, at the next statement's line, and what follows it in
    /// its batch; turning it off flags nothing.
    /// </summary>
    [TestMethod]
    public void SetFipsFlagger_AppliesAsTheBatchParses()
    {
        AreEqual(
            "1021/1@2 Line 2 has the non-ANSI statement 'PRINT'. | 1021/1@4 Line 3 has the non-ANSI statement 'SET'. | 1021/1@4 Line 4 has the non-ANSI statement 'PRINT'.",
            Warnings("ENTRY", "print 1\nset fips_flagger 'FULL'\nprint 1"));
        AreEqual("", Warnings("ENTRY", "set fips_flagger off\nprint 1"));
    }

    /// <summary>A text compiled before runs on its plan, so its warnings go out the first time only.</summary>
    [TestMethod]
    public void CachedBatch_SendsItsWarningsOnce()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand("create table t (a int); set fips_flagger 'ENTRY'").ExecuteNonQuery();
        var count = 0;
        connection.InfoMessage += (_, e) => count += e.Errors.Cast<SimulatedError>().Count(static error => error.Number == 1022);
        _ = connection.CreateCommand("select top 1 a from t").ExecuteNonQuery();
        _ = connection.CreateCommand("select top 1 a from t").ExecuteNonQuery();
        AreEqual(1, count);
    }
}
