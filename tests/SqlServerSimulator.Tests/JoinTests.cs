using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's JOIN forms: INNER / bare JOIN / LEFT [OUTER] /
/// RIGHT [OUTER] / FULL [OUTER] / CROSS, multi-table chains, self-joins via alias.
/// Shared rules: qualifier-aware resolution (Msg 209 on ambiguity), ON-predicate 3VL
/// semantics, parser rejections. RIGHT / FULL accept a non-correlated or
/// outer-correlated derived-table right side; lateral correlation to the left side
/// is rejected with real's Msg 4104, though at runtime rather than bind time.
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
        // A non-APPLY derived table's body binds in a scope holding none of the
        // FROM's own sources, so `a.id` names a qualifier that isn't there —
        // real's Msg 4104 on the written identifier, matched verbatim
        // (probe-confirmed 2026-08-05). The simulator's rejection arrives at
        // runtime rather than bind time; the message is the same either way.
        new Simulation().AssertSqlError("""
            create table a (id int);
            create table b (id int);
            insert a values (1), (2);
            insert b values (1), (2), (3);
            select a.id, bx.id from a right join (select id from b where b.id = a.id) bx on a.id = bx.id
            """, 4104, "The multi-part identifier \"a.id\" could not be bound.");
    }

    /// <summary>
    /// Msg 207 renders only the leaf identifier — a qualified reference to a
    /// nonexistent column drops the table / alias qualifier, matching real
    /// SQL Server verbatim (probe-confirmed: <c>col.is_replicated</c> surfaces
    /// as <c>"Invalid column name 'is_replicated'."</c>, not the qualified
    /// form). Surfaced by SSMS's Table-Designer column query.
    /// </summary>
    [TestMethod]
    public void InvalidColumnName_QualifiedReference_RendersLeafOnly()
        => new Simulation().AssertSqlError(
            "create table t (id int); select t.nosuchcol from t",
            207, "Invalid column name 'nosuchcol'.");

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

    // A NULL join key never matches under `=` (NULL = x is UNKNOWN), so the
    // comma-join's Cross→Inner rewrite must drop those rows — exactly as the
    // pre-rewrite cross-product-then-WHERE did. b row (13, NULL) and a rows
    // with no matching b are absent.
    [TestMethod]
    public void CommaFrom_NullJoinKey_ExcludedAfterRewrite()
    {
        using var connection = SeededAB();
        _ = connection.CreateCommand("insert b values (13, null, 400)").ExecuteNonQuery();
        var rows = ReadIntPairs(connection.CreateCommand("select a.id, b.val from a, b where a.id = b.a_id"));
        CollectionAssert.AreEquivalent(new[] { (1, 100), (1, 200), (2, 300) }, rows);
    }

    // The comma source's equi-predicate references the NULL-extended side of a
    // preceding LEFT JOIN. A null-extended b.id makes `b.id = c.id` UNKNOWN, so
    // those rows drop — identical whether the predicate filters post-cross
    // (pre-rewrite) or anchors the synthesized INNER JOIN ON (post-rewrite),
    // because the predicate stays in WHERE as the residual.
    [TestMethod]
    public void CommaFrom_AfterLeftJoin_NullExtendedKeyDropsRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int, av varchar(10));
            create table b (id int, bv varchar(10));
            create table c (id int, cv varchar(10));
            insert a values (1, 'a1'), (2, 'a2');
            insert b values (1, 'b1');
            insert c values (1, 'c1'), (2, 'c2')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select a.av, b.bv, c.cv from a left join b on a.id = b.id, c where b.id = c.id").ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        // a2's b is null-extended → b.id = c.id is UNKNOWN → only a1's row survives.
        CollectionAssert.AreEquivalent(new[] { ("a1", "b1", "c1") }, rows);
    }

    // A comma source feeding a later LEFT JOIN must still null-extend: the
    // rewrite only flips the Cross (a,b) level to INNER, leaving the explicit
    // LEFT JOIN to c untouched, so an a/b pair with no c match keeps its row.
    [TestMethod]
    public void CommaFrom_FeedingLeftJoin_StillNullExtends()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int, av varchar(10));
            create table b (id int, j int, bv varchar(10));
            create table c (k int, cv varchar(10));
            insert a values (1, 'a1'), (2, 'a2');
            insert b values (1, 9, 'b1'), (2, 8, 'b2');
            insert c values (9, 'c9')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select a.av, b.bv, c.cv from a, b left join c on b.j = c.k where a.id = b.id order by a.av").ExecuteReader();
        var rows = new List<(string, string, string?)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        CollectionAssert.AreEqual(new[] { ("a1", "b1", (string?)"c9"), ("a2", "b2", null) }, rows);
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

    // --- Equi-join hash path edge cases (Selection.Execution.Joins.cs). ---
    // The `a.col = b.col` fast path replaces the nested loop; these pin the
    // semantics that differ from a naive hash: NULL keys, type promotion,
    // composite keys, residual non-equi conjuncts, and collation folding.

    private static Simulation SeededNullKeys()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table l (id int, k int);
            create table r (k int, v int);
            insert l values (1, 10), (2, null);
            insert r values (10, 100), (null, 999)
            """);
        return sim;
    }

    /// <summary>
    /// l.id=2 (NULL k) and r's NULL-k row must not pair — NULL = NULL is UNKNOWN.
    /// </summary>
    [TestMethod]
    public void HashEquiJoin_NullKey_ExcludedFromInnerMatch()
        => AreEqual(1, SeededNullKeys().ExecuteScalar("select count(*) from l join r on l.k = r.k"));

    [TestMethod]
    public void HashEquiJoin_NullKey_LeftJoinEmitsNullFilledRight()
    {
        var sim = SeededNullKeys();
        // Both left rows survive; the NULL-key one is NULL-filled, not matched to r's NULL row.
        AreEqual(2, sim.ExecuteScalar("select count(*) from l left join r on l.k = r.k"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from l left join r on l.k = r.k where r.v is null"));
    }

    /// <summary>
    /// r's NULL-key row never matches, so RIGHT JOIN emits it with left NULL-filled (2 rows total).
    /// </summary>
    [TestMethod]
    public void HashEquiJoin_NullKey_RightJoinEmitsUnmatchedRightRow()
        => AreEqual(2, SeededNullKeys().ExecuteScalar("select count(*) from l right join r on l.k = r.k"));

    /// <summary>
    /// bigint = int must hash under the promoted common type, not by raw SqlValue.Type.
    /// </summary>
    [TestMethod]
    public void HashEquiJoin_CrossTypeKey_PromotesAndMatches()
        => AreEqual(1L, new Simulation().ExecuteScalar("""
            create table l (k bigint);
            create table r (k int);
            insert l values (5);
            insert r values (5);
            select count_big(*) from l join r on l.k = r.k
            """));

    [TestMethod]
    public void HashEquiJoin_CompositeKey_AllColumnsMustMatch()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table l (a int, b int);
            create table r (a int, b int);
            insert l values (1, 2), (1, 3);
            insert r values (1, 2), (9, 9);
            select count(*) from l join r on l.a = r.a and l.b = r.b
            """));

    /// <summary>
    /// The `r.v > 150` conjunct isn't an equi-key; it's re-checked per probed candidate.
    /// </summary>
    [TestMethod]
    public void HashEquiJoin_ResidualNonEquiConjunct_FiltersCandidates()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table l (k int);
            create table r (k int, v int);
            insert l values (10);
            insert r values (10, 100), (10, 200);
            select count(*) from l join r on l.k = r.k and r.v > 150
            """));

    /// <summary>
    /// Default SQL_Latin1_General_CP1_CI_AS is case-insensitive — the hash key
    /// must fold case (GetHashCode agreeing with collation-aware Equals).
    /// </summary>
    [TestMethod]
    public void HashEquiJoin_StringKey_FoldsCaseUnderDefaultCollation()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table l (name varchar(20));
            create table r (name varchar(20));
            insert l values ('One');
            insert r values ('one');
            select count(*) from l join r on l.name = r.name
            """));

    /// <summary>
    /// 1 match + 1 unmatched-left + 1 unmatched-right = 3 rows.
    /// </summary>
    [TestMethod]
    public void HashEquiJoin_FullJoin_EmitsUnmatchedFromBothSides()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table l (k int);
            create table r (k int);
            insert l values (1), (2);
            insert r values (1), (3);
            select count(*) from l full outer join r on l.k = r.k
            """));

    /// <summary>
    /// Filter-then-join with an indexed inner takes the per-outer seek path; the
    /// result must match the hash path exactly (3 children of the filtered parent).
    /// </summary>
    [TestMethod]
    public void InnerJoin_FilterThenIndexedInner_SeekPath_ReturnsCorrectRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key, label varchar(20));
            create table c (cid int not null primary key, pid int, amt int);
            create index ix_c_pid on c (pid);
            insert p values (1, 'a'), (2, 'b');
            insert c values (10, 1, 100), (11, 1, 200), (12, 1, 300), (13, 2, 999);
            select count(*) from p join c on c.pid = p.id where p.id = 1
            """));

    /// <summary>
    /// LEFT JOIN on the seek path still NULL-extends an outer row whose seek
    /// finds no inner match.
    /// </summary>
    [TestMethod]
    public void LeftJoin_FilterThenIndexedInner_SeekPath_NullExtendsUnmatched()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table p (id int not null primary key);
            create table c (cid int not null primary key, pid int);
            create index ix_c_pid on c (pid);
            insert p values (1), (2), (3);
            insert c values (10, 1), (11, 2)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select c.cid from p left join c on c.pid = p.id where p.id = 3").ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// A large unfiltered outer exceeds the per-outer-seek cap and falls back to
    /// the hash build; every parent's single child must still match (200 rows).
    /// </summary>
    [TestMethod]
    public void InnerJoin_LargeOuter_IndexedInner_HashFallback_ReturnsCorrectRows()
        => AreEqual(200, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (cid int not null primary key, pid int);
            create index ix_c_pid on c (pid);
            declare @i int = 1;
            while @i <= 200 begin insert p values (@i); insert c values (@i, @i); set @i += 1; end;
            select count(*) from p join c on c.pid = p.id
            """));
}
