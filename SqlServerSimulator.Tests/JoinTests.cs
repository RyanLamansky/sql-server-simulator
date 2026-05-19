using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's JOIN forms: INNER / bare JOIN / LEFT [OUTER] /
/// RIGHT [OUTER] / FULL [OUTER] / CROSS, multi-table chains, self-joins via alias.
/// Shared rules: qualifier-aware resolution (Msg 209 on ambiguity), ON-predicate 3VL
/// semantics, parser rejections. RIGHT / FULL accept a non-correlated or
/// outer-correlated derived-table right side; lateral correlation to the left side
/// is rejected (Msg 207 at parse time, vs real SQL Server's Msg 4104).
/// </summary>
[TestClass]
public sealed class JoinTests
{
    private static DbConnection SeededAB()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int, name varchar(20));
            create table b (id int, a_id int, val int);
            insert a values (1, 'one'), (2, 'two'), (3, 'three');
            insert b values (10, 1, 100), (11, 1, 200), (12, 2, 300)
            """).ExecuteNonQuery();
        return connection;
    }

    private static List<(int, int)> ReadIntPairs(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
        {
            rows.Add((reader.IsDBNull(0) ? -1 : reader.GetInt32(0),
                      reader.IsDBNull(1) ? -1 : reader.GetInt32(1)));
        }
        return rows;
    }

    [TestMethod]
    public void InnerJoin_BasicMatch()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.val from a inner join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    [TestMethod]
    public void InnerJoin_BareJoinKeyword_TreatedAsInner()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.val from a join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    [TestMethod]
    public void InnerJoin_UnmatchedRowsExcluded()
    {
        // a.id=3 has no matching b row.
        using var connection = SeededAB();
        var matched = new List<int>();
        using var reader = connection.CreateCommand("select a.id from a inner join b on a.id = b.a_id").ExecuteReader();
        while (reader.Read())
            matched.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 1, 1, 2 }, matched);
    }

    [TestMethod]
    public void InnerJoin_MissingOn_RaisesSyntaxError()
        => _ = Throws<DbException>(() => _ = new Simulation().ExecuteScalar("""
            create table a (id int);
            create table b (id int);
            select 1 from a inner join b
            """));

    [TestMethod]
    public void LeftJoin_NullFillsUnmatchedRight()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.val from a left join b on a.id = b.a_id"));
        // a.id=3 has no match; b.val NULL → mapped to -1.
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300), (3, -1) }, rows);
    }

    [TestMethod]
    public void LeftJoin_LeftOuterSpelling_Equivalent()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.val from a left outer join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300), (3, -1) }, rows);
    }

    [TestMethod]
    public void LeftJoin_IsNullPattern_FindsUnmatched()
    {
        using var connection = SeededAB();
        var matched = new List<int>();
        using var reader = connection.CreateCommand("select a.id from a left join b on a.id = b.a_id where b.val is null").ExecuteReader();
        while (reader.Read())
            matched.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 3 }, matched);
    }

    [TestMethod]
    public void CrossJoin_CartesianProduct()
    {
        using var connection = SeededAB();
        AreEqual(9, connection.CreateCommand("select count(*) from a cross join b").ExecuteScalar());
    }

    [TestMethod]
    public void CrossJoin_WithOn_RaisesSyntaxError()
        => _ = Throws<DbException>(() => _ = new Simulation().ExecuteScalar("""
            create table a (id int);
            create table b (id int);
            select 1 from a cross join b on 1=1
            """));

    [TestMethod]
    public void Chain_InnerThenLeft_Composes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int, name varchar(20));
            create table b (id int, a_id int, val int);
            create table c (id int, b_id int, label varchar(20));
            insert a values (1, 'one'), (2, 'two');
            insert b values (10, 1, 100), (11, 1, 200), (12, 2, 300);
            insert c values (20, 10, 'first'), (21, 12, 'second')
            """);

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

    [TestMethod]
    public void SelfJoin_DifferentAliases_DistinguishCopies()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int, name varchar(20));
            insert a values (1, 'one'), (2, 'two')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select t1.name, t2.name from a t1 inner join a t2 on t1.id <> t2.id").ExecuteReader();
        var pairs = new List<(string, string)>();
        while (reader.Read())
            pairs.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEquivalent(new[] { ("one", "two"), ("two", "one") }, pairs);
    }

    [TestMethod]
    public void Ambiguous_UnqualifiedColumn_RaisesMsg209()
    {
        using var connection = SeededAB();
        var ex = Throws<DbException>(() => _ = connection.CreateCommand(
            "select id from a inner join b on a.id = b.a_id").ExecuteScalar());
        AreEqual("209", ex.Data["HelpLink.EvtID"]);
        AreEqual("Ambiguous column name 'id'.", ex.Message);
    }

    [TestMethod]
    public void OnPredicate_NullEqualsNull_ExcludesRow()
    {
        // ON `x.k = y.k` with NULLs on both sides → UNKNOWN → excluded (3VL).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table x (k int null);
            create table y (k int null);
            insert x values (1), (null);
            insert y values (1), (null)
            """);

        using var connection = simulation.CreateOpenConnection();
        var rows = ReadIntPairs(connection.CreateCommand("select x.k, y.k from x inner join y on x.k = y.k"));
        CollectionAssert.AreEqual(new[] { (1, 1) }, rows);
    }

    /// <summary>
    /// Seed asymmetric x/y tables where left has an unmatched row (x=3 → no y) and
    /// right has an unmatched row (y=4 → no x). Shared by all RIGHT / FULL tests so the
    /// asymmetric paths actually exercise the unmatched-side NULL fill.
    /// </summary>
    private static DbConnection SeededXY()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table x (id int);
            create table y (x_id int);
            insert x values (1), (2), (3);
            insert y values (1), (2), (4)
            """).ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void RightJoin_BasicMatchAndOrphanRight()
    {
        using var connection = SeededXY();
        var rows = ReadIntPairs(connection.CreateCommand("select x.id, y.x_id from x right join y on x.id = y.x_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (2, 2), (-1, 4) }, rows);
    }

    [TestMethod]
    public void RightOuterJoin_KeywordSpelling_Equivalent()
    {
        using var connection = SeededXY();
        var rows = ReadIntPairs(connection.CreateCommand("select x.id, y.x_id from x right outer join y on x.id = y.x_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (2, 2), (-1, 4) }, rows);
    }

    [TestMethod]
    public void FullJoin_EmitsBothUnmatchedSides()
    {
        using var connection = SeededXY();
        var rows = ReadIntPairs(connection.CreateCommand("select x.id, y.x_id from x full join y on x.id = y.x_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (2, 2), (3, -1), (-1, 4) }, rows);
    }

    [TestMethod]
    public void FullOuterJoin_KeywordSpelling_Equivalent()
    {
        using var connection = SeededXY();
        var rows = ReadIntPairs(connection.CreateCommand("select x.id, y.x_id from x full outer join y on x.id = y.x_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (2, 2), (3, -1), (-1, 4) }, rows);
    }

    [TestMethod]
    public void RightJoin_NullKey_DoesNotMatch()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int);
            create table b (a_id int);
            insert a values (1), (null);
            insert b values (1), (null)
            """).ExecuteNonQuery();

        // ON uses three-valued equality: NULL = NULL is UNKNOWN, so the NULL rows
        // never match and both pair as their respective outer-side row.
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.a_id from a right join b on a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (-1, -1) }, rows);
    }

    [TestMethod]
    public void RightJoin_NoLeftRows_AllRightEmittedNullLeft()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int);
            create table b (id int);
            insert b values (10), (20)
            """).ExecuteNonQuery();

        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.id from a right join b on a.id = b.id"));
        CollectionAssert.AreEquivalent(new[] { (-1, 10), (-1, 20) }, rows);
    }

    [TestMethod]
    public void RightJoin_NoRightRows_EmitsNothing()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int);
            create table b (id int);
            insert a values (1), (2)
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select a.id, b.id from a right join b on a.id = b.id").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void RightJoin_ChainedAfterInnerJoin()
    {
        // Three-table chain: (x INNER JOIN z) RIGHT JOIN y. The RIGHT JOIN's
        // left side is the materialized (x,z) rowset; unmatched y rows emit
        // with both x and z slots NULL-filled.
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table x (id int);
            create table y (x_id int);
            create table z (id int);
            insert x values (1), (2);
            insert y values (1), (2), (4);
            insert z values (1), (2)
            """).ExecuteNonQuery();

        var rows = new List<(int, int, int)>();
        using var reader = connection.CreateCommand(
            "select x.id, z.id, y.x_id from x inner join z on x.id = z.id right join y on x.id = y.x_id").ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.IsDBNull(0) ? -1 : reader.GetInt32(0),
                      reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                      reader.GetInt32(2)));
        }
        CollectionAssert.AreEquivalent(new[] { (1, 1, 1), (2, 2, 2), (-1, -1, 4) }, rows);
    }

    [TestMethod]
    public void FullJoin_ChainedAfterLeftJoin()
    {
        // (a LEFT JOIN b) FULL JOIN c: tests that left-side NULL slots from
        // the upstream LEFT JOIN don't poison the FULL JOIN's matched-bitmap
        // tracking, and that FULL's unmatched-right phase clears all prior slots.
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int);
            create table b (a_id int);
            create table c (id int);
            insert a values (1), (2);
            insert b values (1);
            insert c values (1), (3)
            """).ExecuteNonQuery();

        var rows = new List<(int, int, int)>();
        using var reader = connection.CreateCommand(
            "select a.id, b.a_id, c.id from a left join b on a.id = b.a_id full join c on a.id = c.id").ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.IsDBNull(0) ? -1 : reader.GetInt32(0),
                      reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                      reader.IsDBNull(2) ? -1 : reader.GetInt32(2)));
        }
        // a=1 LEFT b → (1,1); a=2 LEFT b → (2,NULL). FULL c on a.id=c.id:
        //   (1,1) matches c=1 → (1,1,1);
        //   (2,NULL) no c match → (2,NULL,NULL);
        //   c=3 unmatched → (NULL,NULL,3).
        CollectionAssert.AreEquivalent(new[] { (1, 1, 1), (2, -1, -1), (-1, -1, 3) }, rows);
    }

    [TestMethod]
    public void RightJoin_NonCorrelatedDerivedTableRight_EmitsUnmatched()
    {
        using var connection = SeededXY();
        // Derived right side: SELECT x_id FROM y. Probe-confirmed against real SQL Server.
        var rows = ReadIntPairs(connection.CreateCommand(
            "select x.id, bx.x_id from x right join (select x_id from y) bx on x.id = bx.x_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (2, 2), (-1, 4) }, rows);
    }

    [TestMethod]
    public void FullJoin_NonCorrelatedDerivedTableRight_EmitsBothUnmatched()
    {
        using var connection = SeededXY();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select x.id, bx.x_id from x full outer join (select x_id from y) bx on x.id = bx.x_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 1), (2, 2), (3, -1), (-1, 4) }, rows);
    }

    [TestMethod]
    public void RightJoin_DerivedTableRightWithInnerWhere_FiltersBeforeJoin()
    {
        using var connection = SeededXY();
        // Inner WHERE prunes y to {2, 4}; unmatched right row 4 still emits with NULL left.
        var rows = ReadIntPairs(connection.CreateCommand(
            "select x.id, bx.x_id from x right join (select x_id from y where x_id > 1) bx on x.id = bx.x_id"));
        CollectionAssert.AreEquivalent(new[] { (2, 2), (-1, 4) }, rows);
    }

    [TestMethod]
    public void RightJoin_DerivedTableRight_OuterCorrelated_ReExecutesPerOuterRow()
    {
        // Probed against real SQL Server: derived-table right of RIGHT JOIN may correlate
        // to enclosing scope (here, the outer EXISTS host's `o.id`); the simulator's
        // LateralPlan re-executes per outer row via the outer resolver passed through
        // EnumerateJoinedRows.
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table o (id int);
            create table a (id int);
            create table b (id int, ref_id int);
            insert o values (10), (40);
            insert a values (1), (2);
            insert b values (1, 10), (2, 20), (3, 30)
            """).ExecuteNonQuery();

        // For o.id=10: derived (select id from b where ref_id=10) = {1}; RIGHT JOIN a on a.id=bx.id → matched (1,1).
        // For o.id=40: derived = {}; RIGHT JOIN over empty right yields nothing → EXISTS false.
        var matched = new List<int>();
        using var reader = connection.CreateCommand("""
            select o.id from o where exists (
                select 1 from a right join (select id from b where b.ref_id = o.id) bx
                on a.id = bx.id
            )
            """).ExecuteReader();
        while (reader.Read())
            matched.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 10 }, matched);
    }

    [TestMethod]
    public void RightJoin_DerivedTableRight_LateralCorrelationToLeft_Rejected()
    {
        // Real SQL Server raises Msg 4104 ("multi-part identifier could not be bound")
        // at bind-time; the simulator raises Msg 207 ("Invalid column name 'a.id'") at
        // runtime because Reference.Run is the resolution point — the derived-table
        // parse doesn't see the left-side snapshot resolver, so resolution falls through
        // to the (null at top-level) outer resolver and fails on the first inner row.
        // Different code + bind-vs-runtime timing, same end state (rejection).
        _ = new Simulation().AssertSqlError("""
            create table a (id int);
            create table b (id int);
            insert a values (1), (2);
            insert b values (1), (2), (3);
            select a.id, bx.id from a right join (select id from b where b.id = a.id) bx on a.id = bx.id
            """, 207);
    }

    [TestMethod]
    public void CommaFrom_TwoSources_FilterEqualsInnerJoin()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.val from a, b where a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    [TestMethod]
    public void CommaFrom_NoWhere_ProducesCartesianProduct()
    {
        using var connection = SeededAB();
        AreEqual(9, connection.CreateCommand("select count(*) from a, b").ExecuteScalar());
    }

    [TestMethod]
    public void CommaFrom_ThreeSources_AllJoined()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int, av varchar(10));
            create table b (id int, bv varchar(10));
            create table c (id int, cv varchar(10));
            insert a values (1, 'a1'), (2, 'a2'), (3, 'a3');
            insert b values (1, 'b1'), (2, 'b2'), (4, 'b4');
            insert c values (1, 'c1'), (3, 'c3'), (5, 'c5')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select a.av, b.bv, c.cv from a, b, c where a.id = b.id and b.id = c.id").ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        CollectionAssert.AreEquivalent(new[] { ("a1", "b1", "c1") }, rows);
    }

    [TestMethod]
    public void CommaFrom_DerivedTableAfterComma_Works()
    {
        using var connection = SeededAB();
        var rows = ReadIntPairs(connection.CreateCommand(
            "select a.id, d.val from a, (select a_id, val from b) d where a.id = d.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    [TestMethod]
    public void CommaFrom_ExplicitJoinThenComma_Composes()
    {
        // Real SQL Server: `a JOIN b ON ..., c WHERE a.id = c.id` works; the
        // explicit JOIN chain binds first, then a Cross splices in c.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int, av varchar(10));
            create table b (id int, bv varchar(10));
            create table c (id int, cv varchar(10));
            insert a values (1, 'a1'), (2, 'a2'), (3, 'a3');
            insert b values (1, 'b1'), (2, 'b2'), (4, 'b4');
            insert c values (1, 'c1'), (3, 'c3'), (5, 'c5')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select a.av, b.bv, c.cv from a join b on a.id = b.id, c where a.id = c.id").ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        CollectionAssert.AreEquivalent(new[] { ("a1", "b1", "c1") }, rows);
    }

    [TestMethod]
    public void CommaFrom_TrailingComma_RaisesSyntaxError()
        => _ = new Simulation().AssertSqlError("""
            create table a (id int);
            select * from a,
            """, 102);

    [TestMethod]
    public void CommaFrom_LeadingComma_RaisesSyntaxError()
        => _ = new Simulation().AssertSqlError("""
            create table a (id int);
            select * from , a
            """, 102);
}
