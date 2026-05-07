using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's JOIN forms: <c>INNER JOIN</c>,
/// <c>LEFT [OUTER] JOIN</c>, bare <c>JOIN</c> (treated as INNER),
/// <c>CROSS JOIN</c>, multi-table chains, self-joins via alias, and the
/// shared rules — qualifier-aware column resolution (Msg 209 on
/// unqualified ambiguity), ON-predicate 3VL semantics (UNKNOWN doesn't
/// match), and parser rejections (INNER JOIN without ON, CROSS JOIN
/// with ON). RIGHT JOIN is intentionally not modeled — see
/// <see cref="RightJoin_NotSupported"/>.
/// </summary>
[TestClass]
public sealed class JoinTests
{
    private static DbConnection SeededAB()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table a (id int, name varchar(20))").ExecuteNonQuery();
        _ = connection.CreateCommand("create table b (id int, a_id int, val int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into a values (1, 'one'), (2, 'two'), (3, 'three')").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into b values (10, 1, 100), (11, 1, 200), (12, 2, 300)").ExecuteNonQuery();
        return connection;
    }

    private static List<(int, int)> ReadIntPairs(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? -1 : reader.GetInt32(1)));
        return rows;
    }

    // === INNER JOIN ===

    [TestMethod]
    public void InnerJoin_BasicMatch()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select a.id, b.val from a inner join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    [TestMethod]
    public void InnerJoin_BareJoinKeyword_TreatedAsInner()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select a.id, b.val from a join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    [TestMethod]
    public void InnerJoin_UnmatchedRowsExcluded()
    {
        // a.id=3 has no matching b row, so the row is excluded from INNER JOIN.
        using var connection = SeededAB();
        var matched = new List<int>();
        using var reader = connection.CreateCommand(
            "select a.id from a inner join b on a.id = b.a_id").ExecuteReader();
        while (reader.Read()) matched.Add(reader.GetInt32(0));
        // 1 (matches b twice), 1, 2.
        CollectionAssert.AreEquivalent(new[] { 1, 1, 2 }, matched);
    }

    [TestMethod]
    public void InnerJoin_MissingOn_RaisesSyntaxError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int)");
        _ = simulation.ExecuteNonQuery("create table b (id int)");
        _ = Throws<DbException>(() => _ = simulation.ExecuteScalar("select 1 from a inner join b"));
    }

    // === LEFT JOIN ===

    [TestMethod]
    public void LeftJoin_NullFillsUnmatchedRight()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select a.id, b.val from a left join b on a.id = b.a_id"));
        // a.id=3 has no match; b.val is NULL (we map to -1 for the test pair).
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300), (3, -1) }, rows);
    }

    [TestMethod]
    public void LeftJoin_LeftOuterSpelling_Equivalent()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select a.id, b.val from a left outer join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300), (3, -1) }, rows);
    }

    [TestMethod]
    public void LeftJoin_IsNullPattern_FindsUnmatched()
    {
        // The classic "find rows in A with no match in B" pattern.
        using var connection = SeededAB();
        var matched = new List<int>();
        using var reader = connection.CreateCommand(
            "select a.id from a left join b on a.id = b.a_id where b.val is null").ExecuteReader();
        while (reader.Read()) matched.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 3 }, matched);
    }

    // === CROSS JOIN ===

    [TestMethod]
    public void CrossJoin_CartesianProduct()
    {
        using var connection = SeededAB();
        // a has 3 rows, b has 3 rows → 9.
        AreEqual(9, connection.CreateCommand("select count(*) from a cross join b").ExecuteScalar());
    }

    [TestMethod]
    public void CrossJoin_WithOn_RaisesSyntaxError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int)");
        _ = simulation.ExecuteNonQuery("create table b (id int)");
        _ = Throws<DbException>(() => _ = simulation.ExecuteScalar("select 1 from a cross join b on 1=1"));
    }

    // === Multi-table chain ===

    [TestMethod]
    public void Chain_InnerThenLeft_Composes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int, name varchar(20))");
        _ = simulation.ExecuteNonQuery("create table b (id int, a_id int, val int)");
        _ = simulation.ExecuteNonQuery("create table c (id int, b_id int, label varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into a values (1, 'one'), (2, 'two')");
        _ = simulation.ExecuteNonQuery("insert into b values (10, 1, 100), (11, 1, 200), (12, 2, 300)");
        _ = simulation.ExecuteNonQuery("insert into c values (20, 10, 'first'), (21, 12, 'second')");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select a.name, b.val, c.label from a inner join b on a.id = b.a_id left join c on b.id = c.b_id").ExecuteReader();
        var rows = new List<(string, int, string?)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        CollectionAssert.AreEquivalent(new (string, int, string?)[]
        {
            ("one", 100, "first"),
            ("one", 200, null),
            ("two", 300, "second"),
        }, rows);
    }

    // === Self-join via aliases ===

    [TestMethod]
    public void SelfJoin_DifferentAliases_DistinguishCopies()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int, name varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into a values (1, 'one'), (2, 'two')");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select t1.name, t2.name from a t1 inner join a t2 on t1.id <> t2.id").ExecuteReader();
        var pairs = new List<(string, string)>();
        while (reader.Read()) pairs.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEquivalent(new[] { ("one", "two"), ("two", "one") }, pairs);
    }

    // === Column resolution ===

    [TestMethod]
    public void Ambiguous_UnqualifiedColumn_RaisesMsg209()
    {
        using var connection = SeededAB();
        // Both a and b have an `id` column; bare `id` is ambiguous.
        var ex = Throws<DbException>(() => _ = connection.CreateCommand(
            "select id from a inner join b on a.id = b.a_id").ExecuteScalar());
        AreEqual("209", ex.Data["HelpLink.EvtID"]);
        AreEqual("Ambiguous column name 'id'.", ex.Message);
    }

    [TestMethod]
    public void OnPredicate_NullEqualsNull_ExcludesRow()
    {
        // Two-table inner join where both columns can be NULL. ON `x.k = y.k`
        // with NULLs on both sides → UNKNOWN → excluded (3VL).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table x (k int null)");
        _ = simulation.ExecuteNonQuery("create table y (k int null)");
        _ = simulation.ExecuteNonQuery("insert into x values (1), (null)");
        _ = simulation.ExecuteNonQuery("insert into y values (1), (null)");

        using var connection = simulation.CreateOpenConnection();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select x.k, y.k from x inner join y on x.k = y.k"));
        CollectionAssert.AreEqual(new[] { (1, 1) }, rows);
    }

    // === RIGHT JOIN — explicitly not supported ===

    [TestMethod]
    public void RightJoin_NotSupported()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int)");
        _ = simulation.ExecuteNonQuery("create table b (id int)");
        _ = Throws<NotSupportedException>(() =>
            _ = simulation.ExecuteScalar("select 1 from a right join b on a.id = b.id"));
    }
}
