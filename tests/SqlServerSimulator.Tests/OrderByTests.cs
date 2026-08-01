using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior of the ORDER BY clause: ASC/DESC parsing, ordinal references,
/// alias-vs-source resolution, NULL ordering (NULL first ASC, NULL last DESC),
/// and interaction with WHERE/TOP/DISTINCT.
/// </summary>
[TestClass]
public class OrderByTests
{
    private static List<object?> Column0(DbDataReader reader)
    {
        var values = new List<object?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader[0]);
        return values;
    }

    private static DbConnection Seeded(string columns, string values)
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ( {columns} )").ExecuteNonQuery();
        _ = connection.CreateCommand($"insert t values {values}").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_AscDefault()
    {
        using var connection = Seeded("v int", "(3),(1),(2)");
        using var reader = connection.CreateCommand("select v from t order by v").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_AscExplicit()
    {
        using var connection = Seeded("v int", "(3),(1),(2)");
        using var reader = connection.CreateCommand("select v from t order by v asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SingleIntColumn_Desc()
    {
        using var connection = Seeded("v int", "(3),(1),(2)");
        using var reader = connection.CreateCommand("select v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 2, 1 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_NullsFirstUnderAsc()
    {
        using var connection = Seeded("v int", "(3),(null),(1),(null),(2)");
        using var reader = connection.CreateCommand("select v from t order by v asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { null, null, 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_NullsLastUnderDesc()
    {
        using var connection = Seeded("v int", "(3),(null),(1),(null),(2)");
        using var reader = connection.CreateCommand("select v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 2, 1, null, null }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_MultiColumnMixedDirections()
    {
        using var connection = Seeded("a int, b int", "(1,2),(1,1),(2,1),(2,2)");
        using var reader = connection.CreateCommand("select a, b from t order by a asc, b desc").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (1, 2), (1, 1), (2, 2), (2, 1) }, rows);
    }

    [TestMethod]
    public void OrderBy_StringColumn_CollationAware()
    {
        // Default collation is case-insensitive: 'a' < 'B' < 'C'.
        using var connection = Seeded("s varchar(10)", "('B'),('a'),('C')");
        using var reader = connection.CreateCommand("select s from t order by s asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { "a", "B", "C" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_AliasReference_ResolvesToProjection()
    {
        // `b AS a` overrides — `order by a` sees the aliased projection (b).
        using var connection = Seeded("a int, b int", "(1,3),(2,2),(3,1)");
        using var reader = connection.CreateCommand("select b as a from t order by a").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 1, 2, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SourceColumnNotInProjection_ResolvesToSource()
    {
        // Without DISTINCT, ORDER BY can reach a source column not in projection.
        using var connection = Seeded("a int, b int", "(1,30),(2,10),(3,20)");
        using var reader = connection.CreateCommand("select a from t order by b").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 2, 3, 1 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_Expression_Length()
    {
        using var connection = Seeded("s varchar(20)", "('xx'),('a'),('hello')");
        using var reader = connection.CreateCommand("select s from t order by len(s)").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { "a", "xx", "hello" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_Ordinal_OrdersByNthProjectionColumn()
    {
        using var connection = Seeded("a int, b int", "(1,30),(2,10),(3,20)");
        using var reader = connection.CreateCommand("select a, b from t order by 2").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (2, 10), (3, 20), (1, 30) }, rows);
    }

    [TestMethod]
    public void OrderBy_OrdinalZero_ThrowsMsg108()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            connection.CreateCommand("select v from t order by 0").ExecuteReader().Read());
        AreEqual("The ORDER BY position number 0 is out of range of the number of items in the select list.", ex.Message);
    }

    [TestMethod]
    public void OrderBy_OrdinalAboveProjectionCount_ThrowsMsg108()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            connection.CreateCommand("select v from t order by 5").ExecuteReader().Read());
        AreEqual("The ORDER BY position number 5 is out of range of the number of items in the select list.", ex.Message);
    }

    [TestMethod]
    public void OrderBy_AppliesAfterWhere()
    {
        using var connection = Seeded("v int", "(5),(1),(4),(2),(3)");
        using var reader = connection.CreateCommand("select v from t where v > 2 order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 5, 4, 3 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_AppliedBeforeTop()
    {
        using var connection = Seeded("v int", "(5),(1),(4),(2),(3)");
        using var reader = connection.CreateCommand("select top 2 v from t order by v desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 5, 4 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_StringWithNulls_NullsFirstAsc()
    {
        using var connection = Seeded("s varchar(10)", "('b'),(null),('a'),(null),('c')");
        using var reader = connection.CreateCommand("select s from t order by s asc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { null, null, "a", "b", "c" }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_DateTime_OrdersChronologically()
    {
        using var connection = Seeded("d datetime", "('2026-05-04'),('2024-01-15'),('2025-07-22')");
        using var reader = connection.CreateCommand("select d from t order by d desc").ExecuteReader();
        var rows = new List<DateTime>();
        while (reader.Read())
            rows.Add((DateTime)reader[0]);
        CollectionAssert.AreEqual(new[] {
            new DateTime(2026, 5, 4),
            new DateTime(2025, 7, 22),
            new DateTime(2024, 1, 15)
        }, rows);
    }

    [TestMethod]
    public void OrderBy_OnEmptyTable_ReturnsNoRows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select v from t order by v").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void OrderBy_OnTablelessSelect_NoOpButParses()
        => AreEqual(1, new Simulation().ExecuteReader("select 1 order by 1").EnumerateRecords().Count());

    [TestMethod]
    public void OrderBy_StableSort_PreservesInsertionOrderForEqualKeys()
    {
        // List.Sort is unstable; assert set equivalence and the last-row guarantee only.
        using var connection = Seeded("k int, v int", "(1,100),(1,101),(1,102),(2,200)");
        using var reader = connection.CreateCommand("select v from t order by k").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { 100, 101, 102, 200 }, rows);
        AreEqual(200, rows[^1]);
    }

    [TestMethod]
    public void OrderBy_AggregateExpression_OnGroupedQuery_SortsByAggregate()
    {
        using var connection = Seeded("k int, v int", "(1,10),(1,20),(2,5),(3,100)");
        using var reader = connection.CreateCommand(
            "select k from t group by k order by sum(v) desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 1, 2 }, Column0(reader));
    }

    [TestMethod]
    public void OrderBy_SelectAlias_OnGroupedQuery_SortsByAlias()
    {
        using var connection = Seeded("k int, v int", "(1,10),(1,20),(2,5),(3,100)");
        using var reader = connection.CreateCommand(
            "select k, sum(v) as s from t group by k order by s desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 1, 2 }, Column0(reader));
    }

    [TestMethod]
    public void Top_WithOrderByAggregate_OnGroupedQuery_SelectsHighestGroups()
    {
        // TOP must apply AFTER the ORDER BY aggregate sort, not to an arbitrary prefix.
        using var connection = Seeded("k int, v int", "(1,10),(1,20),(2,5),(3,100)");
        using var reader = connection.CreateCommand(
            "select top (2) k from t group by k order by sum(v) desc").ExecuteReader();
        CollectionAssert.AreEqual(new object?[] { 3, 1 }, Column0(reader));
    }

    [TestMethod]
    public void GroupBy_ScalarExpression_ProjectsAndOrdersByExpression()
    {
        // GROUP BY <expression> while projecting and ordering by that same
        // expression: it resolves against the group (constant within it), not
        // by re-evaluating the now-grouped-away underlying column.
        using var connection = Seeded("v int", "(1),(2),(11),(12),(21)");
        using var reader = connection.CreateCommand(
            "select v / 10 as bucket, count(*) as c from t group by v / 10 order by v / 10").ExecuteReader();
        var rows = new List<(int Bucket, int Count)>();
        while (reader.Read())
            rows.Add(((int)reader[0], (int)reader[1]));
        CollectionAssert.AreEqual(new[] { (0, 2), (1, 2), (2, 1) }, rows);
    }

    // === FROM-less SELECT with a trailing ORDER BY ===
    // A SELECT with no FROM yields exactly one row, so ORDER BY is a no-op
    // sort, but SQL Server still accepts the clause (probed 2026-07-14).
    // The SSMS server-properties query ends `… AS [IsFullTextInstalled]
    // ORDER BY [Server_Name] ASC` with no FROM. The clause reaches the parser
    // through the projection-alias continuation, which previously raised
    // Msg 156 near ORDER.

    [TestMethod]
    public void Fromless_OrderByAlias_ReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 2 as x, 1 as y order by x");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetValue(0));
        AreEqual(1, reader.GetValue(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_OrderByAliasDescending_ReturnsRow()
    {
        // The exact SSMS shape: a trailing bracketed-alias ORDER BY, no FROM.
        using var reader = new Simulation().ExecuteReader("select 7 as [Server_Name] order by [Server_Name] desc");
        IsTrue(reader.Read());
        AreEqual("Server_Name", reader.GetName(0));
        AreEqual(7, reader.GetValue(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_OrderByOrdinal_ReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 2 as x, 1 as y order by 2");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetValue(0));
        AreEqual(1, reader.GetValue(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_OrderByWithOffsetFetch_ReturnsRow()
    {
        using var reader = new Simulation().ExecuteReader("select 5 as x order by x offset 0 rows fetch next 1 rows only");
        IsTrue(reader.Read());
        AreEqual(5, reader.GetValue(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Fromless_SetOpChain_TopLevelOrderBy_Sorts()
    {
        // The final ORDER BY of a set-op chain whose branches are all
        // FROM-less: `SELECT 2 AS X UNION ALL SELECT 1 ORDER BY X DESC` → 2, 1.
        using var reader = new Simulation().ExecuteReader("select 2 as x union all select 1 order by x desc");
        CollectionAssert.AreEqual(new object?[] { 2, 1 }, Column0(reader));
    }

    /// <summary>
    /// A <em>qualified</em> ORDER BY term names a source column, never an
    /// output alias. Matching on the leaf alone silently sorted by the wrong
    /// column whenever a join brought a same-named column into scope — an ORM
    /// ordering by a related model's field (`ORDER BY child.id`) bound to the
    /// projected `parent.id` instead.
    /// </summary>
    [TestMethod]
    public void OrderBy_QualifiedTerm_BindsToTheSourceColumnNotTheOutputAlias()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table parent (id int identity primary key, name varchar(20))",
            "create table child (id int identity primary key, parent_id int)",
            "insert parent (name) values ('p1'), ('p2')",
            "insert child (parent_id) values (1), (2), (1)");

        // Ordering by child.id gives p1, p2, p1; by parent.id it would be p1, p1, p2.
        using var reader = sim.ExecuteReader(
            "select parent.id, parent.name from parent left outer join child on parent.id = child.parent_id "
            + "order by child.id asc, parent.id asc");
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetValue(1)}");
        AreEqual("p1,p2,p1", string.Join(",", rows));
    }

    /// <summary>
    /// The alias is bypassed even when it shadows the qualified name outright:
    /// <c>SELECT val AS id … ORDER BY t.id</c> sorts by <c>t.id</c>, so the
    /// projected values come back in id order rather than val order.
    /// </summary>
    [TestMethod]
    public void OrderBy_QualifiedTerm_IgnoresAShadowingOutputAlias()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ob (id int, val int)",
            "insert ob values (1,30),(2,20),(3,10)");

        AreEqual("30,20,10", string.Join(",", Column(sim, "select val as id from ob t order by t.id")));
        // An unqualified term still binds to the alias.
        AreEqual("10,20,30", string.Join(",", Column(sim, "select val as x from ob t order by x")));
        // And a qualified source column that isn't projected still resolves.
        AreEqual("10,20,30", string.Join(",", Column(sim, "select val as x from ob t order by t.val")));
    }

    /// <summary>
    /// Msg 408: a term real folds to a constant at compile time is rejected,
    /// on a single SELECT and on a set-op chain alike, and the position is the
    /// term's 1-based index in the ORDER BY list.
    /// </summary>
    [TestMethod]
    [DataRow("select v from t order by 'x'", 1)]
    [DataRow("select v from t order by 1.5", 1)]
    [DataRow("select v from t order by 1e0", 1)]
    [DataRow("select v from t order by 1 + 0", 1)]
    [DataRow("select v from t order by 2 - 1", 1)]
    [DataRow("select v from t order by 'a' + 'b'", 1)]
    [DataRow("select v from t order by cast(1 as int)", 1)]
    [DataRow("select v from t order by convert(varchar(5), 1)", 1)]
    [DataRow("select v from t order by coalesce(null, 1)", 1)]
    [DataRow("select v from t order by null", 1)]
    [DataRow("select v from t order by 0x01", 1)]
    [DataRow("select v from t order by (1 + 1)", 1)]
    [DataRow("select v from t order by ('x')", 1)]
    [DataRow("select v from t order by -1.5", 1)]
    [DataRow("select top 1 v from t order by 'x'", 1)]
    [DataRow("select v from t order by 'x' offset 0 rows", 1)]
    [DataRow("select v from t order by v, 'x'", 2)]
    [DataRow("select v from t union all select v from t order by 'x'", 1)]
    [DataRow("select v from t union all select v from t order by 1, 'x'", 2)]
    public void OrderBy_ConstantTerm_RaisesMsg408(string commandText, int position)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2)");
        sim.AssertSqlError(
            commandText,
            408,
            $"A constant expression was encountered in the ORDER BY list, position {position}.");
    }

    /// <summary>
    /// The Msg 408 gate is syntactic: a term that reaches a variable, a
    /// subquery, a UDF, or any server- / session-state function sorts, because
    /// real evaluates rather than folds it. A select-list alias naming a
    /// constant projection is likewise fine — only the written ORDER BY term is
    /// inspected.
    /// </summary>
    [TestMethod]
    [DataRow("select v from t order by getdate()")]
    [DataRow("select v from t order by newid()")]
    [DataRow("select v from t order by rand()")]
    [DataRow("select v from t order by (select 1)")]
    [DataRow("select v from t order by @@spid")]
    [DataRow("select v from t order by @@version")]
    [DataRow("select v from t order by db_name()")]
    [DataRow("select v from t order by isnull(null, 1)")]
    [DataRow("select v from t order by cast(getdate() as date)")]
    [DataRow("select v from t order by case when v = 1 then 1 else 2 end")]
    [DataRow("declare @p int = 1; select v from t order by @p + 1")]
    [DataRow("select 5 as x from t order by x")]
    public void OrderBy_NonConstantTerm_Sorts(string commandText)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2)");
        Assert.HasCount(3, Column(sim, commandText));
    }

    /// <summary>
    /// The ordinal form is a <em>signed</em> integer literal, parentheses
    /// included: <c>(1)</c> and <c>+1</c> name the first column, while
    /// <c>-1</c> and <c>-(1)</c> are position -1 (Msg 108). An arithmetic
    /// expression folding to the same number is a constant instead (Msg 408,
    /// covered above).
    /// </summary>
    [TestMethod]
    public void OrderBy_SignedIntegerLiteral_IsTheOrdinalForm()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2)");
        AreEqual("1,2,3", string.Join(",", Column(sim, "select v from t order by (1)")));
        AreEqual("1,2,3", string.Join(",", Column(sim, "select v from t order by +1")));
        AreEqual("3,2,1", string.Join(",", Column(sim, "select v from t order by (1) desc")));
        sim.AssertSqlError("select v from t order by -1", 108, "The ORDER BY position number -1 is out of range of the number of items in the select list.");
        sim.AssertSqlError("select v from t order by -(1)", 108, "The ORDER BY position number -1 is out of range of the number of items in the select list.");
        sim.AssertSqlError("select v from t order by (2)", 108, "The ORDER BY position number 2 is out of range of the number of items in the select list.");
    }

    /// <summary>
    /// Msg 1008: real reads a variable term as a variable column position
    /// rather than a sort expression, whenever the variable is reachable
    /// through pure conversions — bare, parenthesized, or CAST. A variable
    /// inside arithmetic sorts per row instead.
    /// </summary>
    [TestMethod]
    public void OrderBy_VariableColumnPositionTerm_RaisesMsg1008()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2)");
        sim.AssertSqlError(
            "declare @p int = 1; select v from t order by @p",
            1008,
            "The SELECT item identified by the ORDER BY number 1 contains a variable as part of the expression identifying a column position. Variables are only allowed when ordering by an expression referencing a column name.");
        _ = sim.AssertSqlError("declare @p int = 1; select v from t order by (@p)", 1008);
        _ = sim.AssertSqlError("declare @p int = 1; select v from t order by ((@p))", 1008);
        _ = sim.AssertSqlError("declare @p int = 1; select v from t order by cast(@p as int)", 1008);
        _ = sim.AssertSqlError("declare @p int = 1; select v from t union all select v from t order by @p", 1008);
        Assert.Contains("ORDER BY number 2", sim.AssertSqlError("declare @p int = 1; select v from t order by v, @p desc", 1008).Message);
        // A variable inside arithmetic is a sort expression.
        Assert.HasCount(3, Column(sim, "declare @p int = 1; select v from t order by -@p"));
        Assert.HasCount(3, Column(sim, "declare @p int = 1; select v from t order by (@p) + 0"));
    }

    private static List<string> Column(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(0)}");
        return values;
    }
}
