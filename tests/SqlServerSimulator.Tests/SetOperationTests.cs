using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's set operators: <c>UNION</c> / <c>UNION ALL</c> /
/// <c>INTERSECT</c> / <c>EXCEPT</c>. Covers dedup semantics (NULL-equals-NULL,
/// opposite of <c>=</c>'s tri-state), type promotion across branches, the precedence
/// rule (INTERSECT &gt; UNION/EXCEPT), Msg 205 on column-count mismatch, Msg 156 on
/// per-branch ORDER BY, and post-chain top-level ORDER BY.
/// </summary>
[TestClass]
public sealed class SetOperationTests
{
    private static List<int> ReadInts(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        return values;
    }

    [TestMethod]
    public void Union_Dedupes()
        => CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(new Simulation().CreateCommand("select 1 union select 2 union select 1")));

    [TestMethod]
    public void UnionAll_PreservesDuplicates()
        => CollectionAssert.AreEqual(new[] { 1, 2, 1 },
            ReadInts(new Simulation().CreateCommand("select 1 union all select 2 union all select 1")));

    [TestMethod]
    public void Union_NullsCompareEqual_DedupedToSingleRow()
    {
        // SET ops treat NULLs as equal — opposite of `=` operator's UNKNOWN.
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select cast(null as int) union select cast(null as int)").ExecuteReader();
        var rows = 0;
        while (reader.Read())
            rows++;
        AreEqual(1, rows);
    }

    [TestMethod]
    public void Intersect_KeepsCommonRows()
        => CollectionAssert.AreEqual(new[] { 1 }, ReadInts(new Simulation().CreateCommand("select 1 intersect select 1")));

    [TestMethod]
    public void Intersect_NoOverlap_Empty()
        => IsEmpty(ReadInts(new Simulation().CreateCommand("select 1 intersect select 2")));

    [TestMethod]
    public void Intersect_NullsMatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select cast(null as int) intersect select cast(null as int)").ExecuteReader();
        var rows = 0;
        while (reader.Read()) rows++;
        AreEqual(1, rows);
    }

    [TestMethod]
    public void Except_RemovesRightSide()
        => CollectionAssert.AreEqual(new[] { 1 }, ReadInts(new Simulation().CreateCommand("select 1 except select 2")));

    [TestMethod]
    public void Except_AllRemoved_Empty()
        => IsEmpty(ReadInts(new Simulation().CreateCommand("select 1 except select 1")));

    // INTERSECT/EXCEPT both dedupe their left side (probe-confirmed).
    [TestMethod]
    public void Except_DedupesLeftBeforeFiltering()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (v int);
            insert t values (1), (1), (2)
            """);

        CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(simulation.CreateCommand("select v from t except select 99")));
    }

    [TestMethod]
    public void TypePromotion_IntPlusDecimal_ProducesDecimal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select 1 union select 2.5").ExecuteReader();
        var values = new List<decimal>();
        while (reader.Read())
            values.Add(reader.GetDecimal(0));
        CollectionAssert.AreEquivalent(new[] { 1m, 2.5m }, values);
    }

    [TestMethod]
    public void MismatchedColumnCount_RaisesMsg205()
        => new Simulation().AssertSqlError("select 1, 2 union select 3", 205,
            "All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.");

    [TestMethod]
    public void Intersect_BindsTighterThanUnion()
    {
        // `1 union 2 intersect 2` parses as `1 union (2 intersect 2)` = {1, 2}.
        CollectionAssert.AreEquivalent(new[] { 1, 2 },
        ReadInts(new Simulation().CreateCommand("select 1 union select 2 intersect select 2")));
    }

    [TestMethod]
    public void ThreeBranchUnion_LeftAssociative()
        => CollectionAssert.AreEquivalent(new[] { 1, 2, 3 },
            ReadInts(new Simulation().CreateCommand("select 1 union select 2 union select 3")));

    [TestMethod]
    public void UnionAllAfterUnion_PreservesDupAtEnd()
    {
        // `(1 union 2) union all 1` = {1, 2} ++ {1} = {1, 2, 1}.
        CollectionAssert.AreEqual(new[] { 1, 2, 1 },
        ReadInts(new Simulation().CreateCommand("select 1 union select 2 union all select 1")));
    }

    [TestMethod]
    public void TopLevelOrderBy_AppliesToCombinedResult()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select 1 as v union select 2 union select 3 order by v desc").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, values);
    }

    [TestMethod]
    public void PerBranchOrderBy_RaisesMsg156()
        => _ = new Simulation().AssertSqlError("select 1 order by 1 union select 2", 156);

    // The top-level (post-set-op) ORDER BY sorts the combined rows in their
    // encoded byte[] form and decodes only the sort-key column per row; the
    // non-key columns (here a string and a NULL-bearing int) must still drain
    // correctly for every row after the sort, and NULL ordering / collation
    // must match the single-SELECT path.
    [TestMethod]
    public void TopLevelOrderBy_MultiColumn_DrainsNonKeyColumnsAfterSort()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (k int, s varchar(10), n int);
            create table b (k int, s varchar(10), n int);
            insert a values (3, 'gamma', 30), (1, 'alpha', null);
            insert b values (2, 'Beta', 20), (4, 'delta', 40)
            """);

        using var reader = simulation.CreateCommand(
            "select k, s, n from a union all select k, s, n from b order by s").ExecuteReader();
        var rows = new List<(int K, string S, int? N)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));

        // Case-insensitive default collation: alpha < Beta < delta < gamma.
        CollectionAssert.AreEqual(
            new[] { (1, "alpha", (int?)null), (2, "Beta", 20), (4, "delta", 40), (3, "gamma", 30) },
            rows);
    }

    // Ordinal ORDER BY over a set-op resolves against the projected column and
    // sorts NULLs first under ASC, exercising the ordinal branch of the
    // top-level key decode.
    [TestMethod]
    public void TopLevelOrderBy_Ordinal_NullsFirst()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (v int);
            create table b (v int);
            insert a values (3), (null);
            insert b values (1), (2)
            """);

        using var reader = simulation.CreateCommand(
            "select v from a union all select v from b order by 1").ExecuteReader();
        var values = new List<int?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        CollectionAssert.AreEqual(new int?[] { null, 1, 2, 3 }, values);
    }

    // Non-set-op SELECT can ORDER BY a non-projected source column.
    [TestMethod]
    public void SingleSelect_OrderByNonProjectedSource_StillWorks()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int);
            insert t values (3, 30), (1, 10), (2, 20)
            """);

        using var reader = simulation.CreateCommand("select b from t order by a").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, values);
    }

    private static Simulation SeededTwoTables()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table left_t (v int);
            create table right_t (v int);
            insert left_t values (1), (2), (3);
            insert right_t values (3), (4), (5)
            """);
        return simulation;
    }

    [TestMethod]
    public void Union_AcrossTwoTables_Dedupes()
        => CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 },
            ReadInts(SeededTwoTables().CreateCommand("select v from left_t union select v from right_t")));

    [TestMethod]
    public void Intersect_AcrossTwoTables_Common()
        => CollectionAssert.AreEqual(new[] { 3 },
            ReadInts(SeededTwoTables().CreateCommand("select v from left_t intersect select v from right_t")));

    [TestMethod]
    public void Except_AcrossTwoTables_LeftMinusRight()
        => CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(SeededTwoTables().CreateCommand("select v from left_t except select v from right_t")));

    // Set-ops inside a subquery body. The simulator's subquery parsers
    // (Expression.cs / BooleanExpression.cs) all route through Selection.Parse,
    // which already drives the set-op chain — exercised here as a regression so
    // refactors of that surface don't silently break TPC-shaped queries.

    [TestMethod]
    public void Union_InsideFromDerivedTable()
        => CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 },
            ReadInts(SeededTwoTables().CreateCommand(
                "select x.v from (select v from left_t union select v from right_t) x")));

    [TestMethod]
    public void UnionAll_InsideFromDerivedTable_PreservesDuplicates()
        => CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 3, 4, 5 },
            ReadInts(SeededTwoTables().CreateCommand(
                "select x.v from (select v from left_t union all select v from right_t) x")));

    [TestMethod]
    public void Union_InsideExistsSubquery_AnyBranchSatisfies()
    {
        using var connection = SeededTwoTables().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select v from left_t where exists (select 1 from right_t where v = left_t.v union select 1 from right_t where v = left_t.v)").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEquivalent(new[] { 3 }, values);
    }

    [TestMethod]
    public void Union_InsideInSubquery_MembershipMatchesEitherBranch()
        => CollectionAssert.AreEquivalent(new[] { 1, 3 },
            ReadInts(SeededTwoTables().CreateCommand(
                "select v from left_t where v in (select 1 union select 3)")));

    [TestMethod]
    public void Union_InsideScalarSubquery_SingleColumnSingleRowOk()
    {
        // Scalar subquery + UNION: both branches project a single column AND the
        // UNION dedups to a single row (max(v) = 3 in both branches), so the scalar
        // value resolves to 3.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v int); insert t values (1), (2), (3)");
        using var reader = simulation.CreateCommand(
            "select (select max(v) from t union select max(v) from t) as m").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Union_InsideScalarSubquery_MultiRowResultRaises512()
        => SeededTwoTables().AssertSqlError(
            "select (select max(v) from left_t union select max(v) from right_t) as m",
            512);

    [TestMethod]
    public void Union_InsideCteBody_RecursiveAndAnchorBranches()
    {
        // Non-recursive CTE body that's itself a UNION: a common EF Core 7 TPC shape
        // wrapped inside WITH for downstream filtering.
        using var connection = SeededTwoTables().CreateOpenConnection();
        var values = ReadInts(connection.CreateCommand(
            "with combined as (select v from left_t union select v from right_t) select v from combined"));
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, values);
    }

    [TestMethod]
    public void TpcDiscriminator_Shape_OuterFiltersRoundTrip()
    {
        // Mimics EF Core 7+'s TPC inheritance emit shape: each concrete table contributes
        // a SELECT with a constant discriminator column, the branches UNION ALL inside
        // a derived table, and the outer query filters / projects through that table.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table dogs (Id int, Name nvarchar(50), BarkVolume int);
            create table cats (Id int, Name nvarchar(50), Purrs bit);
            insert dogs values (1, 'Rex', 5), (2, 'Buddy', 7);
            insert cats values (3, 'Whiskers', 1)
            """);

        using var reader = simulation.CreateCommand("""
            select t.Id, t.Name from (
                select Id, Name, 'Dog' as TypeTag from dogs
                union all
                select Id, Name, 'Cat' as TypeTag from cats
            ) t
            where t.TypeTag = 'Cat'
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        AreEqual("Whiskers", reader.GetString(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Union_DerivedTable_LeftJoinedWithOuterTable()
    {
        // TPC variant: union'd derived table is joined to another table.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table dogs (Id int, Name nvarchar(50));
            create table cats (Id int, Name nvarchar(50));
            create table owners (PetId int, OwnerName nvarchar(50));
            insert dogs values (1, 'Rex');
            insert cats values (2, 'Whiskers');
            insert owners values (1, 'alice'), (2, 'bob')
            """);

        using var reader = simulation.CreateCommand("""
            select t.Id, o.OwnerName from (
                select Id, Name from dogs
                union all
                select Id, Name from cats
            ) t
            left join owners o on t.Id = o.PetId
            order by t.Id
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("alice", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual("bob", reader.GetString(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void NestedUnion_InsideUnionInsideFrom()
    {
        // Tests the parser's depth bookkeeping: a UNION-bearing derived table inside
        // another UNION-bearing derived table.
        using var reader = new Simulation().CreateCommand(
            "select y.a from (select a from (select 1 as a union select 2) x union select 3) y order by y.a").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
    }

    [TestMethod]
    public void Except_InsideFromDerivedTable()
        => CollectionAssert.AreEquivalent(new[] { 1, 2 },
            ReadInts(SeededTwoTables().CreateCommand(
                "select x.v from (select v from left_t except select v from right_t) x")));

    [TestMethod]
    public void Intersect_InsideFromDerivedTable()
        => CollectionAssert.AreEquivalent(new[] { 3 },
            ReadInts(SeededTwoTables().CreateCommand(
                "select x.v from (select v from left_t intersect select v from right_t) x")));
}
