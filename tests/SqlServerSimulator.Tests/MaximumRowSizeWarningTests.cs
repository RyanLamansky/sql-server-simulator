using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Msg 1708, the warning a table whose largest row can pass the 8060-byte
/// in-row limit draws: each variable-length column counts at most the 24-byte
/// pointer a value pushed off the row leaves (probed 2026-10-06 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class MaximumRowSizeWarningTests
{
    private static List<string> Messages(Simulation simulation, string sql)
    {
        var connection = simulation.CreateDbConnection();
        connection.Open();
        var log = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors.Cast<SimulatedError>())
                log.Add($"{error.Number} s{error.State} L{error.LineNumber} {error.Procedure}".TrimEnd());
        };
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        return log;
    }

    [TestMethod]
    [DataRow("a char(8000), b char(40), c varchar(10)", true)]
    [DataRow("a char(8000), b char(36), c varchar(10)", false)]
    [DataRow("a char(8000), b char(26), c varchar(24)", true)]
    [DataRow("a char(8000), b char(26), c varchar(23)", false)]
    [DataRow("a char(8000), b char(26), c varchar(100)", true)]
    [DataRow("a char(8000), b varchar(100)", false)]
    [DataRow("a char(8000), b char(26), c varchar(max)", true)]
    [DataRow("a char(8000), b char(26), c nvarchar(12)", true)]
    [DataRow("a char(8000), b char(26), c xml", true)]
    [DataRow("a char(8000), b char(26), c hierarchyid", true)]
    [DataRow("a char(8000), b char(40), c bit, d varchar(10)", true)]
    [DataRow("a char(8000), b char(26), c varchar(10), d as (c + 'x')", false)]
    [DataRow("a varchar(8000), b varchar(8000)", false)]
    public void CreateTable(string columns, bool warns)
    {
        var log = Messages(new Simulation(), $"create table t ({columns})");
        string[] expected = warns ? ["1708 s2 L1"] : [];
        CollectionAssert.AreEqual(expected, log);
    }

    [TestMethod]
    public void AlterTable_AddWarnsAtState2_AlterColumnAtState1()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a char(8000), b char(40))");
        CollectionAssert.AreEqual(new[] { "1708 s2 L2" }, Messages(simulation, "select 1;\nalter table t add c varchar(10)"));
        CollectionAssert.AreEqual(new[] { "1708 s1 L1" }, Messages(simulation, "alter table t alter column c varchar(12)"));
    }

    [TestMethod]
    public void TempTableAndTableType_Warn()
    {
        var simulation = new Simulation();
        CollectionAssert.AreEqual(new[] { "1708 s2 L1" }, Messages(simulation, "create table #t (a char(8000), b char(40), c varchar(10))"));
        CollectionAssert.AreEqual(new[] { "1708 s2 L1" }, Messages(simulation, "create type tt as table (a char(8000), b char(40), c varchar(10))"));
    }

    /// <summary>
    /// A table variable's warning comes as the batch compiles, twice, ahead of
    /// anything the batch runs; a procedure's once at CREATE and twice as its
    /// first call compiles it.
    /// </summary>
    [TestMethod]
    public void TableVariable_WarnsAsTheBatchCompiles()
    {
        var simulation = new Simulation();
        CollectionAssert.AreEqual(
            new[] { "1708 s2 L1", "1708 s2 L1" },
            Messages(simulation, "if 1 = 0 begin declare @t table (a char(8000), b char(40), c varchar(10)) end"));
        CollectionAssert.AreEqual(
            new[] { "1708 s2 L1 p" },
            Messages(simulation, "create procedure p as declare @t table (a char(8000), b char(40), c varchar(10))"));
        CollectionAssert.AreEqual(new[] { "1708 s2 L1 p", "1708 s2 L1 p" }, Messages(simulation, "exec p"));
        IsEmpty(Messages(simulation, "exec p"));
    }
}
