using System.Globalization;
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
                log.Add(string.Create(CultureInfo.InvariantCulture, $"{error.Number} s{error.State} L{error.LineNumber} {error.Procedure}").TrimEnd());
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
            new[] { "1708 s2 L1", "1708 s2 L-1" },
            Messages(simulation, "if 1 = 0 begin declare @t table (a char(8000), b char(40), c varchar(10)) end"));
        CollectionAssert.AreEqual(
            new[] { "1708 s2 L1 p" },
            Messages(simulation, "create procedure p as declare @t table (a char(8000), b char(40), c varchar(10))"));
        CollectionAssert.AreEqual(new[] { "1708 s2 L1 p", "1708 s2 L1 p" }, Messages(simulation, "exec p"));
        IsEmpty(Messages(simulation, "exec p"));
    }

    private const string Oversized = "table (a char(8000), b char(40), c varchar(10))";

    /// <summary>
    /// The compile's second warning follows every first one and carries the
    /// line the compile ends on: the batch's last statement's, or -1 when that
    /// statement is control flow or a <c>DECLARE</c> without an initializer.
    /// </summary>
    [TestMethod]
    [DataRow($"select 1\ndeclare @t {Oversized}", "L2 L2")]
    [DataRow($"select 1\nwhile 1 = 0\nbegin\n declare @t {Oversized}\nend", "L4 L-1")]
    [DataRow($"while 1 = 0\nbegin\n declare @t {Oversized}\nend\nselect 3", "L3 L5")]
    [DataRow($"select 0\ndeclare @u {Oversized}\nselect 1\nwhile 1 = 0\nbegin\ndeclare @t {Oversized}\nend\nselect 3", "L2 L6 L8 L8")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\nbegin\nselect 5\nend", "L1 L3")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\ndeclare @x int", "L1 L-1")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\ndeclare @x int, @y int = 1", "L1 L2")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\ndeclare @c cursor", "L1 L-1")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\ndeclare c cursor for select 1", "L1 L2")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\nif 1 = 1 select 5", "L1 L-1")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\nbegin try select 1 end try begin catch end catch", "L1 L-1")]
    [DataRow($"while 1 = 0 begin declare @t {Oversized} end\nselect 1\nlbl:", "L1 L-1")]
    [DataRow($"begin try select 1 end try\nbegin catch\n declare @t {Oversized}\nend catch", "L3 L-1")]
    public void TableVariable_SecondWarningCarriesTheCompilesLastLine(string sql, string lines) =>
        AreEqual(lines, string.Join(" ", Messages(new Simulation(), sql).Select(static m => m.Split(' ')[2])));
}
