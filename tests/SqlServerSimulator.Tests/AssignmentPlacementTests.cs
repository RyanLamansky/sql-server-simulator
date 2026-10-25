using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// A <c>SELECT @v = …</c> assignment where real refuses one — a set
/// operation's branch, a nested query, an <c>INSERT</c>'s source, a
/// <c>SELECT … INTO</c>, a cursor's query — is refused as the batch compiles,
/// with real's errors, states and lines (probed 2026-10-25 against SQL Server
/// 2025). Each expectation lists the errors as number/state/line.
/// </summary>
[TestClass]
public sealed class AssignmentPlacementTests
{
    private static string Errors(string sql) =>
        string.Join(" ", Throws<SimulatedSqlException>(() => ExecuteScalar(sql)).Errors.Select(static error => $"{error.Number}/{error.State}/L{error.LineNumber}"));

    /// <summary>
    /// Read across each pair of adjacent branches: a pair either of which
    /// assigns is Msg 10734 at its operator, after a Msg 141 at line 0 when one
    /// assigns where the other retrieves, and a pair that raises hides the next.
    /// </summary>
    [TestMethod]
    [DataRow("declare @v int;\nselect @v = 1 union all select 2", "141/1/L0 10734/1/L2")]
    [DataRow("declare @v int;\nselect 1\nunion all\nselect @v = 2", "141/1/L0 10734/1/L3")]
    [DataRow("declare @v int, @w int;\nselect @v = 1 union all select @w = 2", "10734/1/L2")]
    [DataRow("declare @v int, @w int;\nselect @v = 1 union all select @w = 2 union all select 3", "10734/1/L2")]
    [DataRow("declare @v int;\nselect @v = 1\nunion all\nselect 2\nunion all\nselect @v = 3\nunion all\nselect 4", "141/1/L0 10734/1/L3 141/1/L0 10734/1/L7")]
    [DataRow("declare @v int;\nselect @v = 1 except select 2", "141/1/L0 10734/1/L2")]
    [DataRow("declare @v int;\nselect 1 intersect select @v = 2", "141/1/L0 10734/2/L2")]
    [DataRow("declare @v int;\n(select @v = 1) union all select 2 order by 1", "141/1/L0 10734/1/L2")]
    public void SetOperationBranch_RaisesPerPairOfBranches(string sql, string expected) =>
        AreEqual(expected, Errors(sql));

    /// <summary>
    /// A branch mixing assignment and retrieval both assigns and retrieves, and
    /// only a first branch's list reports its own Msg 141, at its SELECT.
    /// </summary>
    [TestMethod]
    [DataRow("declare @v int;\nselect @v = 1, 2\nunion all\nselect 3, 4", "141/1/L2 141/1/L0 10734/1/L3")]
    [DataRow("declare @v int;\nselect 1, 2\nunion all\nselect @v = 3, 4", "141/1/L0 10734/1/L3")]
    [DataRow("declare @v int;\nselect @v = 1, 2\nunion all\nselect @v = 3,\n4", "141/1/L2 141/1/L0 10734/1/L3")]
    public void SetOperationBranchMixingAssignmentAndRetrieval_AssignsAndRetrieves(string sql, string expected) =>
        AreEqual(expected, Errors(sql));

    /// <summary>A module body's refusal counts its lines from the batch creating it.</summary>
    [TestMethod]
    public void SetOperationBranch_InAProcedureBody_ReportsTheCreatingBatchsLines() =>
        AreEqual("141/1/L0 10734/1/L5", Errors("create procedure p as\ndeclare @v int;\nselect 1;\nselect @v = 1\nunion all\nselect 2"));

    [TestMethod]
    public void SetOperationBranch_RefusesTheBatchBeforeItRuns()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("create table #ran (a int); declare @v int; select @v = 1 union all select 2").ExecuteNonQuery());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('tempdb..#ran')").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("declare @v int;\nselect (select @v = 1)", "=")]
    [DataRow("declare @v int = 0;\nselect (select @v += 1)", "+=")]
    [DataRow("declare @v int;\nselect 1 where exists (select @v = 1)", "=")]
    [DataRow("declare @v int;\nselect 1 where 1 in (select @v = 1)", "=")]
    [DataRow("declare @v int;\nselect * from (select @v = 1) d", "=")]
    [DataRow("declare @v int;\nwith c as (select @v = 1 as a) select * from c", "=")]
    [DataRow("declare @v int;\nselect * from (select 1 as a) d cross apply (select @v = d.a) x", "=")]
    public void NestedQuery_RaisesMsg102AtTheAssignment(string sql, string near) =>
        AssertSqlError(sql, 102, $"Incorrect syntax near '{near}'.");

    [TestMethod]
    public void NestedQuery_ReportsTheAssignmentsOwnLine() =>
        AreEqual("102/1/L5", Errors("declare @v int;\nselect x from (values (1)) t(x)\nwhere x in (select\n@v\n=\n1)"));

    [TestMethod]
    [DataRow("declare @v int;\ninsert into #nosuch select @v = 1", "199/1/L0")]
    [DataRow("declare @v int;\ninsert into #nosuch\nselect @v = 1,\n@v = 2", "199/1/L0")]
    [DataRow("declare @v int, @w int;\ninsert into #nosuch select @v = 1 union all select @w = 2", "10734/1/L2")]
    public void InsertSource_RaisesMsg199AheadOfAMissingTarget(string sql, string expected) =>
        AreEqual(expected, Errors(sql));

    [TestMethod]
    public void InsertSource_RefusesTheBatchBeforeItRuns()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table #t (a int)").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert #t values (1); declare @v int; insert into #t select @v = 1").ExecuteNonQuery());
        AreEqual(0, CountRows(connection, "#t"));
    }

    [TestMethod]
    [DataRow("declare @v int;\nselect\n1 as b,\n@v = x\ninto #x from (values (1)) t(x)", "194/1/L2")]
    [DataRow("declare @v int;\nselect @v = 1 union all select 2 into #x", "194/1/L0")]
    [DataRow("select 1 as a\nunion all\nselect 2\ninto #x", "196/1/L1")]
    public void SelectInto_RaisesMsg194AtItsSelect(string sql, string expected) =>
        AreEqual(expected, Errors(sql));

    [TestMethod]
    [DataRow("declare @v int;\ndeclare c cursor\nfor\nselect @v = x\nfrom (values (1)) t(x)", "154/3/L4")]
    [DataRow("declare @v int;\ndeclare c cursor for select 1 as b, @v = 2", "154/3/L2")]
    [DataRow("declare @v int, @c cursor;\nset @c = cursor for select @v = 1", "154/3/L2")]
    [DataRow("declare @v int;\ndeclare c cursor for select 1 as a\nunion all\nselect @v = 2", "154/3/L0")]
    public void CursorQuery_RaisesMsg154(string sql, string expected) =>
        AreEqual(expected, Errors(sql));

    [TestMethod]
    [DataRow("declare @v int;\nselect\n@v = 1,\n2", "141/1/L2")]
    [DataRow("declare @v int;\nselect 1,\n@v = 2\nfrom (values (1)) t(x)\nwhere x = 1", "141/1/L2")]
    [DataRow("declare @v int;\nselect 1, @v = 2\norder by 1", "141/1/L2 156/1/L3")]
    public void SelectListMixingAssignmentAndRetrieval_RaisesMsg141AtItsSelect(string sql, string expected) =>
        AreEqual(expected, Errors(sql));

    [TestMethod]
    public void ParenthesizedStatement_Assigns() =>
        AreEqual(1, ExecuteScalar<int>("declare @v int; (select @v = 1); select @v"));
}
