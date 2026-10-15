using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_prepare</c>, <c>sp_execute</c>, <c>sp_prepexec</c> and
/// <c>sp_unprepare</c> written as <c>EXEC</c> text, probed 2026-10-15 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class PreparedStatementProcedureTests
{
    /// <summary>Every result set <paramref name="sql"/> returns, each as its column names then its rows, joined.</summary>
    private static List<string> ResultSets(DbConnection connection, string sql)
    {
        var sets = new List<string>();
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        do
        {
            var rows = new List<string> { string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)) };
            while (reader.Read())
                rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToInvariantString())));
            sets.Add(string.Join(';', rows));
        }
        while (reader.NextResult());
        return sets;
    }

    [TestMethod]
    public void Prepare_DescribesTheResult_ThenExecuteRunsItByHandle()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual("r # ;1 # r;6 # r;8", string.Join(" # ", ResultSets(connection, """
            declare @h int;
            exec sp_prepare @h output, N'@a int', N'select @a + 1 as r';
            select @h;
            exec sp_execute @h, 5;
            exec sp_execute @h, @a = 7;
            """)));
    }

    [TestMethod]
    public void Handles_LastTheSession_AndNumberFromOne()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("declare @h int; exec sp_prepare @h output, null, N'select 1 as one'").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "one;1" }, ResultSets(connection, "exec sp_execute 1"));
        CollectionAssert.AreEqual(new[] { "a|b;2|x", "|;2|0" }, ResultSets(connection, """
            declare @h int, @rc int;
            exec @rc = sp_prepexec @h output, N'@a int, @b nvarchar(10)', N'select @a as a, @b as b', 2, N'x';
            select @h, @rc;
            """));
        CollectionAssert.AreEqual(new[] { "a|b;3|y" }, ResultSets(connection, "exec sp_execute 2, 3, N'y'"));
        _ = connection.CreateCommand("exec sp_unprepare 2").ExecuteNonQuery();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_execute 2, 3, N'y'").ExecuteNonQuery()).Errors[0];
        AreEqual((8179, (byte)4, "sp_execute", 1, "Could not find prepared statement with handle 2."), (error.Number, error.State, error.Procedure, error.LineNumber, error.Message));
    }

    [TestMethod]
    public void Execute_WritesBackOutputParameters()
        => AreEqual(42, new Simulation().ExecuteScalar("""
            declare @h int, @o int;
            exec sp_prepare @h output, N'@a int output', N'set @a = 42';
            exec sp_execute @h, @o output;
            select @o;
            """));

    [TestMethod]
    [DataRow("exec sp_prepare", (byte)11, "Procedure or function 'sp_prepare' expects parameter '@handle', which was not supplied.", "sp_prepare")]
    [DataRow("exec sp_execute", (byte)12, "Procedure or function 'sp_execute' expects parameter '@handle', which was not supplied.", "sp_execute")]
    [DataRow("exec sp_unprepare", (byte)13, "Procedure or function 'sp_unprepare/sp_cursorunprepare' expects parameter '@handle', which was not supplied.", "sp_unprepare")]
    [DataRow("declare @h int; exec sp_prepexec @h output, N'@a int'", (byte)11, "Procedure or function 'sp_prepexec' expects parameter '@stmt', which was not supplied.", "sp_prepexec")]
    public void MissingArguments_AreMsg201AtLine1(string sql, byte state, string message, string procedure)
    {
        var error = new Simulation().AssertSqlError(sql, 201).Errors[0];
        AreEqual((state, message, procedure, 1), (error.State, error.Message, error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void Unprepare_OfAnUnknownHandle_IsMsg8179State8()
    {
        var error = new Simulation().AssertSqlError("exec sp_unprepare 99999", 8179).Errors[0];
        AreEqual(((byte)8, "sp_unprepare", 1), (error.State, error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void Prepare_OfAStatementThatDoesntCompile_IsItsErrorThenMsg8180()
    {
        var error = new Simulation().AssertSqlError("declare @h int; exec sp_prepare @h output, N'@a int', N'select * from'", 102);
        AreEqual((8180, "Statement(s) could not be prepared.", "sp_prepare"), (error.Errors[1].Number, error.Errors[1].Message, error.Errors[1].Procedure));
    }

    [TestMethod]
    public void Prepare_HandleNotMarkedOutput_IsMsg591_EndingTheBatch()
    {
        var error = new Simulation().AssertSqlError("declare @h int = 5; exec sp_prepare @h, N'@a int', N'select @a'; select @h", 591);
        AreEqual("sp_prepare: The formal parameter \"@handle\" was defined as OUTPUT, but the actual parameter was not declared as OUTPUT.", error.Errors[0].Message);
        HasCount(1, error.Errors);
    }

    [TestMethod]
    public void Prepare_HandleOfAnotherType_IsMsg214()
        => new Simulation().AssertSqlError("declare @h bigint; exec sp_prepare @h output, N'@a int', N'select @a'", 214, "Procedure expects parameter '@handle' of type 'int'.");

    [TestMethod]
    [DataRow("exec sp_execute @h", 8178, "The parameterized query '(@a int)select @a' expects the parameter '@a', which was not supplied.")]
    [DataRow("exec sp_execute @h, 1, 2", 8144, "Procedure or function  has too many arguments specified.")]
    public void Execute_BindsAsSpExecuteSqlDoes(string execute, int number, string message)
        => new Simulation().AssertSqlError("declare @h int; exec sp_prepare @h output, N'@a int', N'select @a'; " + execute, number, message);
}
