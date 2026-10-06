using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Inline indexes and keys one declaration writes — a table's, a table
/// variable's, a table type's or a multi-statement function's return table —
/// and the catalog they land in (probed 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class InlineIndexDeclarationTests
{
    private static string Indexes(Simulation simulation, string objectName) => (string)simulation.ExecuteScalar(
        $"select string_agg(isnull(name, 'heap') + ':' + cast(index_id as varchar) + ':' + cast(type as varchar), ',') within group (order by index_id) from sys.indexes where object_id = object_id('{objectName}')")!;

    /// <summary>
    /// A return table's inline indexes are catalogued under the function, the
    /// clustered one first and the rest in reverse declaration order, each
    /// with its statistic.
    /// </summary>
    [TestMethod]
    public void ReturnTableIndexes_AreCataloguedUnderTheFunction()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create function f() returns @r table (a int index ia, b int, index i1 (a), index i2 (b), c int index ic) as begin return; end");
        AreEqual("heap:0:0,ic:2:2,i2:3:2,i1:4:2,ia:5:2", Indexes(simulation, "f"));
        AreEqual(4, simulation.ExecuteScalar("select count(*) from sys.stats where object_id = object_id('f')"));
        AreEqual(4, simulation.ExecuteScalar("select count(*) from sys.index_columns where object_id = object_id('f')"));
    }

    /// <summary>
    /// An inline clustered index takes the clustering a PRIMARY KEY would by
    /// default; a key written CLUSTERED beside it is Msg 8112 state 0.
    /// </summary>
    [TestMethod]
    public void InlineClusteredIndex_LeavesThePrimaryKeyNonclustered()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int primary key, b int, index i2 clustered (b))",
            "create function f() returns @r table (a int primary key, b int, index i2 clustered (b)) as begin insert @r values (1, 2); return; end");
        StartsWith("i2:1:1,PK__t__", Indexes(simulation, "t"));
        StartsWith("i2:1:1,PK__f__", Indexes(simulation, "f"));
        AreEqual(2, simulation.ExecuteScalar("declare @t table (a int primary key, b int, index i2 clustered (b)); insert @t values (1, 2); select b from @t"));
        var ex = simulation.AssertSqlError("create table u (a int primary key clustered, b int, index i2 clustered (b))", 8112);
        AreEqual<byte>(0, ex.Errors[0].State);
    }

    [TestMethod]
    [DataRow("create table t (a int, b int, index i3 (a), index i3 (b))")]
    [DataRow("declare @t table (a int, b int, index i3 (a), index i3 (b))")]
    [DataRow("create type tt as table (a int, b int, index i3 (a), index i3 (b))")]
    public void TwoInlineIndexesNamedAlike_AreMsg8168State1(string sql)
    {
        var ex = new Simulation().AssertSqlError(sql, 8168);
        AreEqual<byte>(1, ex.Errors[0].State);
    }

    [TestMethod]
    [DataRow("create table t (a int, b int, index i3 (nosuch))")]
    [DataRow("declare @t table (a int, b int, index i3 (nosuch))")]
    [DataRow("create function f() returns @r table (a int, index i3 (nosuch)) as begin return; end")]
    public void InlineIndexOnAMissingColumn_IsFollowedByMsg1750(string sql)
    {
        var ex = new Simulation().AssertSqlError(sql, 1911);
        CollectionAssert.AreEqual(new[] { 1911, 1750 }, ex.Errors.Select(error => error.Number).ToArray());
    }

    [TestMethod]
    [DataRow("create function f() returns @r table (a int sparse null) as begin return; end")]
    [DataRow("create type tt as table (a int sparse null)")]
    public void Sparse_IsASyntaxErrorOutsideATableAndTableVariable(string sql) =>
        new Simulation().AssertSqlError(sql, 102, "Incorrect syntax near 'sparse'.");

    [TestMethod]
    public void Sparse_InATableVariable_IsAccepted() =>
        AreEqual(1, new Simulation().ExecuteScalar("declare @t table (a int sparse null); insert @t values (1); select a from @t"));
}
