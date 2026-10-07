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

    /// <summary>
    /// An untyped <c>NULL</c> branch, aliased or not and on either side, takes
    /// its partner's type rather than imposing the placeholder <c>int</c> — and
    /// a chain of them stays untyped until a typed branch arrives.
    /// </summary>
    [TestMethod]
    [DataRow("select 'a' x union all select null", "varchar")]
    [DataRow("select null x union all select 'a'", "varchar")]
    [DataRow("select (null) x union all select N'z'", "nvarchar")]
    [DataRow("select null x union all select null union all select 'abc'", "varchar")]
    [DataRow("select null x union select cast('2024-01-01' as date) union select null", "date")]
    [DataRow("select 'a' x except select null", "varchar")]
    [DataRow("select null x union all select 1 union all select 2.5", "decimal")]
    [DataRow("select null x union all select null", "int")]
    public void UntypedNullBranch_TakesThePartnersType(string sql, string typeName)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        AreEqual(typeName, reader.GetDataTypeName(0));
        while (reader.Read())
        {
        }
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

    /// <summary>
    /// A top-level ORDER BY over a set operation resolves a name against the
    /// <em>source</em> column behind a projected one, not only its output
    /// alias. ORMs alias every output positionally (<c>num AS Col2</c>) and
    /// then order by the model's field name, so without this the whole shape
    /// raises Msg 207.
    /// </summary>
    [TestMethod]
    [DataRow("[num]", "3,2,1")]
    [DataRow("[Col2]", "3,2,1")]
    [DataRow("2", "3,2,1")]
    public void SetOperation_TopLevelOrderBy_ResolvesSourceNameAliasAndOrdinal(string orderByTerm, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table n (id int, num int, other_num int)",
            "insert n values (1,1,10),(2,2,20),(3,3,30)");

        using var reader = sim.ExecuteReader(
            "select id as Col1, num as Col2 from n where num <= 1 "
            + "union select id as Col1, num as Col2 from n where num >= 2 "
            + $"order by {orderByTerm} desc");
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(1)}");
        AreEqual(expected, string.Join(",", values));
    }

    /// <summary>
    /// The output alias still wins when it shadows a different source column,
    /// so adding the source-name fallback can't change an existing binding.
    /// </summary>
    [TestMethod]
    public void SetOperation_TopLevelOrderBy_OutputAliasWinsOverSourceName()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table n (id int, num int, other_num int)",
            "insert n values (1,1,30),(2,2,20),(3,3,10)");

        // The alias `other_num` names the id column; ordering by it must sort
        // by id (1,2,3), not by the real other_num column (which would be 3,2,1).
        using var reader = sim.ExecuteReader(
            "select id as other_num from n where num <= 1 "
            + "union select id as other_num from n where num >= 2 "
            + "order by other_num");
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(0)}");
        AreEqual("1,2,3", string.Join(",", values));
    }

    /// <summary>
    /// A name matching neither an output alias nor a projected source column
    /// is still Msg 207 — provided it binds nowhere in the first branch's FROM
    /// scope either. A name that *does* bind there is Msg 104 instead
    /// (<see cref="SetOperation_TopLevelOrderBy_UnprojectedColumn_RaisesMsg104"/>).
    /// </summary>
    [TestMethod]
    public void SetOperation_TopLevelOrderBy_UnknownName_RaisesMsg207()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table n (id int, num int, other_num int)",
            "insert n values (1,1,10),(2,2,20),(3,3,30)");

        _ = sim.AssertSqlError(
            "select id as Col1 from n where num <= 1 union select id as Col1 from n where num >= 2 order by [nosuchcol]",
            207);
    }

    /// <summary>
    /// Two tables sharing <c>id</c> / <c>name</c>, each with a column the other
    /// lacks (<c>extra</c> on the left, <c>other</c> on the right), so a
    /// top-level ORDER BY can name a column that exists only in the branch that
    /// isn't in scope. Empty on purpose: real binds a set-op ORDER BY at
    /// compile time, so every rejection below must fire without a row.
    /// </summary>
    private static Simulation SeededSetOpOrderByTables()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table so_a (id int, name varchar(20), extra int);
            create table so_b (id int, name varchar(20), other int)
            """);
        return sim;
    }

    /// <summary>
    /// A top-level ORDER BY over a set operation may name only a projected
    /// column. A term that binds in the first branch's FROM scope but isn't
    /// projected — including any expression over one, since the combined stream
    /// carries no column to evaluate it against — is Msg 104.
    /// </summary>
    [TestMethod]
    // Unprojected column of the first branch, unqualified and qualified.
    [DataRow("select id from so_a union select id from so_b order by name")]
    [DataRow("select id from so_a a union select id from so_b b order by a.name")]
    // Column only the first branch has: in scope, so not Msg 207.
    [DataRow("select id from so_a union select id from so_b order by extra")]
    // Expressions, over a projected column and an unprojected one alike.
    [DataRow("select id from so_a union select id from so_b order by id + 1")]
    [DataRow("select id from so_a union select id from so_b order by name + 'x'")]
    [DataRow("select id from so_a union select id from so_b order by len(name)")]
    [DataRow("select id from so_a union select id from so_b order by (select 1)")]
    // A joined table and a derived table are both in the first branch's scope.
    [DataRow("select a.id from so_a a join so_b b2 on a.id = b2.id union select id from so_b order by b2.other")]
    [DataRow("select id from (select id, name from so_a) q union select id from so_b order by name")]
    // Every set operator, not just UNION.
    [DataRow("select id from so_a except select id from so_b order by name")]
    [DataRow("select id from so_a intersect select id from so_b order by name")]
    public void SetOperation_TopLevelOrderBy_UnprojectedColumn_RaisesMsg104(string sql)
        => AreEqual(
            "ORDER BY items must appear in the select list if the statement contains a UNION, INTERSECT or EXCEPT operator.",
            SeededSetOpOrderByTables().AssertSqlError(sql, 104).Message);

    /// <summary>
    /// The Msg 104 / Msg 207 split: real binds the ORDER BY term against the
    /// first branch's FROM scope and reports that failure ahead of Msg 104, so
    /// a name nothing in scope carries stays Msg 207. Only the *leftmost*
    /// branch is in scope — a column of a later branch alone binds nowhere.
    /// </summary>
    [TestMethod]
    [DataRow("select id from so_a union select id from so_b order by nosuchcol", "nosuchcol")]
    [DataRow("select id from so_a a union select id from so_b b order by a.nosuch", "nosuch")]
    [DataRow("select id from so_a union select id from so_b order by other", "other")]
    [DataRow("select id from so_a union select id from so_b union select other from so_b order by other", "other")]
    [DataRow("select id from so_a union select id from so_b order by nosuch + 1", "nosuch")]
    // An output alias is in scope for a bare term only, so an expression over
    // one binds against the sources and misses there.
    [DataRow("select id + 1 as zz from so_a union select id from so_b order by zz + 1", "zz")]
    public void SetOperation_TopLevelOrderBy_UnboundName_RaisesMsg207(string sql, string name)
    {
        // Msg 104 follows for the term, as it does on real (probed 2026-09-26).
        var error = SeededSetOpOrderByTables().AssertSqlError(sql, 207);
        AreEqual($"Invalid column name '{name}'.", error.Errors[0].Message);
        AreEqual(104, error.Errors[^1].Number);
    }

    /// <summary>
    /// A qualifier no FROM source in the first branch answers to is Msg 4104,
    /// not Msg 207 — including the second branch's own alias, and an output
    /// alias used as though it were a source.
    /// </summary>
    [TestMethod]
    [DataRow("select id from so_a a union select id from so_b b order by b.id", "b.id")]
    [DataRow("select id from so_a a union select id from so_b b order by x.id", "x.id")]
    [DataRow("select id as zz from so_a union select id from so_b order by zz.id", "zz.id")]
    public void SetOperation_TopLevelOrderBy_UnknownQualifier_RaisesMsg4104(string sql, string name)
    {
        var error = SeededSetOpOrderByTables().AssertSqlError(sql, 4104);
        AreEqual($"The multi-part identifier \"{name}\" could not be bound.", error.Errors[0].Message);
        AreEqual(104, error.Errors[^1].Number);
    }

    /// <summary>
    /// Every term's failures are reported, each unbound name then the term's
    /// Msg 104 (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select 1 a union select 2 order by zz, yy", "207,104,207,104")]
    [DataRow("select 1 a union select 2 order by zz + a", "207,207,104")]
    [DataRow("select 1 a union select 2 order by a, zz", "207,104")]
    public void SetOperation_TopLevelOrderBy_ReportsEveryTermsErrors(string sql, string numbers)
        => AreEqual(numbers, string.Join(",", new Simulation().AssertSqlError(sql, 207).Errors.Cast<SimulatedError>().Select(error => error.Number)));

    /// <summary>
    /// An ordinal outside the projection's column count is Msg 108, the same as
    /// on a single SELECT — the top-level sort used to index the projected row
    /// unchecked and surface an <see cref="ArgumentOutOfRangeException"/> — and
    /// real follows it with the set operation's Msg 104 (probed 2026-10-01
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void SetOperation_TopLevelOrderBy_OrdinalOutOfRange_RaisesMsg108(int ordinal)
    {
        var errors = SeededSetOpOrderByTables()
            .AssertSqlError($"select id from so_a union select id from so_b order by {ordinal}", 108).Errors.Cast<SimulatedError>().ToArray();
        AreEqual($"The ORDER BY position number {ordinal} is out of range of the number of items in the select list.", errors[0].Message);
        AreEqual("108,104", string.Join(",", errors.Select(error => error.Number)));
    }

    /// <summary>
    /// A qualified term names the source column, never an output alias, so it
    /// sorts by whichever output column projects that source — here the second,
    /// while the unqualified spelling of the same leaf takes the alias on the
    /// first. Both orderings are probe-confirmed against real.
    /// </summary>
    [TestMethod]
    [DataRow("c.id", "1,2,3")]
    [DataRow("id", "3,2,1")]
    public void SetOperation_TopLevelOrderBy_QualifiedTermTakesSourceOverAlias(string term, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table so_c (id int, extra int);
            insert so_c values (1, 30), (2, 20), (3, 10)
            """);

        using var reader = sim.ExecuteReader(
            $"select c.extra as id, c.id as other from so_c c union select 99, 99 order by {term}");
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(1)}");
        AreEqual($"{expected},99", string.Join(",", values));
    }

    /// <summary>
    /// A FROM-less branch contributes an empty scope, not an unknown one: its
    /// output aliases are the only legal ORDER BY terms and anything else is
    /// Msg 207 (real's binding failure) first, then the term's Msg 104.
    /// </summary>
    [TestMethod]
    public void SetOperation_TopLevelOrderBy_FromLessBranch_UnknownNameRaisesMsg207()
    {
        var error = new Simulation().AssertSqlError("select 2 as x union all select 1 order by y", 207);
        AreEqual("Invalid column name 'y'.", error.Errors[0].Message);
        AreEqual(104, error.Errors[^1].Number);
    }

    /// <summary>
    /// The set-op rule is confined to set-op statements: a single SELECT still
    /// orders by a non-projected source column, and still reports an unbindable
    /// one as Msg 207 rather than Msg 104.
    /// </summary>
    [TestMethod]
    public void SingleSelect_OrderByUnprojectedColumn_StaysLegal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table so_a (id int, name varchar(20), extra int);
            insert so_a values (2, 'b', 20), (1, 'a', 10)
            """);

        AreEqual(1, sim.ExecuteScalar("select id from so_a order by name"));
        _ = sim.AssertSqlError("select id from so_a order by nosuchcol", 207);
    }

    /// <summary>
    /// A set-op branch may be parenthesized, and the parentheses may wrap a
    /// whole nested chain rather than a single SELECT — what an ORM emits when
    /// it combines an already-combined queryset. Without it the opening paren
    /// read as a scalar subquery, so the branch looked like a one-column select
    /// list and the chain failed the equal-expression-count check.
    /// </summary>
    [TestMethod]
    [DataRow("select id, num from nn union (select id, num from nn union select id, num from nn)", "1,10|2,20")]
    [DataRow("select id, num from nn union (select id, num from nn)", "1,10|2,20")]
    [DataRow("select id, num from nn intersect (select id, num from nn union select id, num from nn)", "1,10|2,20")]
    // A EXCEPT (A INTERSECT A) is A EXCEPT A — empty, and that it evaluates at
    // all is the point.
    [DataRow("select id, num from nn except (select id, num from nn intersect select id, num from nn)", "")]
    public void SetOperation_ParenthesizedBranch_Parses(string sql, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table nn (id int, num int)",
            "insert nn values (1,10),(2,20)");

        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetValue(0)},{reader.GetValue(1)}");
        rows.Sort(StringComparer.Ordinal);
        AreEqual(expected, string.Join("|", rows));
    }

    /// <summary>
    /// A query expression written in parentheses may open a statement — alone,
    /// as a set operation's first branch, after a CTE prefix or as an IF body
    /// — and a parenthesized branch without FROM ends at its own closing
    /// parenthesis (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("(select 1)", "1")]
    [DataRow("(select 1) union (select 2) order by 1", "1,2")]
    [DataRow("((select 1)) union all select 2", "1,2")]
    [DataRow("select 1 union (select 2)", "1,2")]
    [DataRow("(select 1 a) order by a", "1")]
    [DataRow("with c as (select 1 a) (select a from c) union all select 5", "1,5")]
    [DataRow("if 1 = 1 (select 1) else (select 2)", "1")]
    public void ParenthesizedQuery_IsAStatement(string sql, string expected)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(0)}");
        AreEqual(expected, string.Join(",", values));
    }

    [TestMethod]
    public void ParenthesizedBareProjection_UnderAnUnusedCte_IsMsg422()
        => new Simulation().AssertSqlError("with c as (select 1 a) (select 7)", 422);

    /// <summary>
    /// Every position that opens a query with its own parenthesis reads a set
    /// operation whose first branch is parenthesized — a derived table, an
    /// <c>APPLY</c> body, a CTE, a subquery, <c>IN</c>, <c>EXISTS</c>, a
    /// quantified comparison, <c>INSERT</c>'s source — while one whose
    /// parentheses group a join, or are followed by <c>ORDER BY</c>, is not
    /// a query (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select * from ((select 1 a) union all (select 2)) d", "1,2", DisplayName = "Derived table")]
    [DataRow("select * from (((select 1 a)) union ((select 2))) d", "1,2", DisplayName = "Derived table, doubled parentheses")]
    [DataRow("select * from ((select 1 a)) d", "1", DisplayName = "Derived table, wrapped")]
    [DataRow("select z from ((select 1 a) union all (select 2)) d(z)", "1,2", DisplayName = "Derived table, column list")]
    [DataRow("select a from ((select 1 a) x cross join (select 2 b) y)", "1", DisplayName = "Join group led by a derived table")]
    [DataRow("select b from (values (1)) v(x) cross apply ((select x b) union all (select 2)) y", "1,2", DisplayName = "APPLY body")]
    [DataRow("with c as ((select 1 a) union all (select 2)) select * from c", "1,2", DisplayName = "CTE")]
    [DataRow("with c(n) as ((select 1) union all (select n + 1 from c where n < 3)) select * from c", "1,2,3", DisplayName = "Recursive CTE")]
    [DataRow("select ((select 1) union all (select 1 where 0 = 1))", "1", DisplayName = "Scalar subquery")]
    [DataRow("select 1 where 1 in ((select 1) union (select 2))", "1", DisplayName = "IN")]
    [DataRow("select 1 where 1 in ((select 1), 2)", "1", DisplayName = "IN list led by a subquery")]
    [DataRow("select 1 where exists ((select 1) union (select 2))", "1", DisplayName = "EXISTS")]
    [DataRow("select 1 where 1 = any ((select 1) union (select 2))", "1", DisplayName = "Quantified comparison")]
    public void ParenthesizedFirstBranch_InEveryQueryPosition(string sql, string expected)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        var values = new List<string>();
        while (reader.Read())
            values.Add($"{reader.GetValue(0)}");
        AreEqual(expected, string.Join(",", values));
    }

    /// <summary>
    /// A view or inline function body may open with a parenthesized branch, as
    /// may an <c>INSERT</c> source after its column list; a body's wrapping
    /// parentheses are left out of what it stores.
    /// </summary>
    [TestMethod]
    public void ParenthesizedFirstBranch_InModuleBodiesAndInsertSources()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int)",
            "create view v1 as (select 1 a) union all (select 2)",
            "create view v2 as ((select 3 a) union (select 4))",
            "create function f1() returns table as return (select 5 a) union all (select 6)",
            "create function f2() returns table as return ((select 7 a)) union all (select 8)");
        AreEqual("1,2|3,4|5,6|7,8", simulation.ExecuteScalar(
            "select concat_ws('|', (select string_agg(a, ',') from v1), (select string_agg(a, ',') from v2), (select string_agg(a, ',') from f1()), (select string_agg(a, ',') from f2()))"));
        AreEqual("((select 3 a) union (select 4))", simulation.ExecuteScalar("select substring(definition, charindex('((', definition), 100) from sys.sql_modules where object_id = object_id('v2')"));
        AreEqual(2, simulation.ExecuteNonQuery("insert into t (a) (select 1) union all (select 2)"));
        AreEqual(2, simulation.ExecuteNonQuery("insert t (a) ((select 3) union all (select 4))"));
        AreEqual("1,2,3,4", simulation.ExecuteScalar("select string_agg(a, ',') within group (order by a) from t"));
        _ = simulation.AssertSqlError("select * from ((select 1 a) order by a) d", 156);
    }

    /// <summary>
    /// A nested query's parenthesized branch may carry the <c>ORDER BY</c> its
    /// <c>TOP</c> or <c>OFFSET</c> takes rows by (Msg 1033 without one), where
    /// any <c>ORDER BY</c> inside a statement's own parentheses is Msg 156 on
    /// the keyword (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void OrderByInParenthesizedBranch()
    {
        var simulation = new Simulation();
        AreEqual("0,2,3", simulation.ExecuteScalar("select string_agg(a, ',') within group (order by a) from ((select top 2 a from (values (3), (1), (2)) v(a) order by a desc) union all (select 0)) d"));
        AreEqual(2, simulation.ExecuteScalar("with c as ((select top 1 1 a order by 1) union all (select 2)) select count(*) from c"));
        simulation.AssertSqlError("select * from ((select 1 a order by 1) union all (select 2)) d", 1033, "The ORDER BY clause is invalid in views, inline functions, derived tables, subqueries, and common table expressions, unless TOP, OFFSET or FOR XML is also specified.");
        simulation.AssertSqlError("(select top 1 1 a order by 1) union (select 2)", 156, "Incorrect syntax near the keyword 'order'.");
        simulation.AssertSqlError("select 1 union (select top 1 2 a order by 1)", 156, "Incorrect syntax near the keyword 'order'.");
        simulation.AssertSqlError("(select top 1 1 a order by 1)", 156, "Incorrect syntax near the keyword 'order'.");
    }

    /// <summary>
    /// A set operation's column is nullable as real infers it — UNION when
    /// either branch's is, INTERSECT only when both are, EXCEPT as the left's —
    /// and a top-level ORDER BY keeps that, where the sort used to report every
    /// column nullable (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select a from sn union all select b from sn order by 1", true)]
    [DataRow("select a from sn union select a from sn order by 1", false)]
    [DataRow("select a from sn intersect select b from sn order by 1", false)]
    [DataRow("select b from sn except select a from sn order by 1", true)]
    [DataRow("select a from sn except select b from sn order by 1", false)]
    [DataRow("select 1 as x union all select '2' order by x", false)]
    public void SetOperation_OrderBy_KeepsNullability(string sql, bool expected)
        => AreEqual(expected, new Simulation().ColumnNullability("create table sn (a int not null, b int null); insert sn values (1, 1); " + sql)[0]);

    /// <summary>
    /// <c>SELECT … INTO</c> from a set operation declares the destination with
    /// the combined nullability: a NOT NULL left column unioned with a nullable
    /// one creates a nullable column (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SetOperation_Into_TakesCombinedNullability()
        => AreEqual("YES", new Simulation().ExecuteScalar("""
            create table sn (a int not null, b int null);
            select a into sz from sn union all select b from sn;
            select IS_NULLABLE from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'sz'
            """));

    /// <summary>
    /// <c>INTERSECT ALL</c> and <c>EXCEPT ALL</c> parse and are refused with
    /// Msg 324, states 1 and 2 (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("intersect", 1)]
    [DataRow("except", 2)]
    public void SetOperation_AllQuantifier_RaisesMsg324(string operation, int state)
    {
        var ex = new Simulation().AssertSqlError($"select 1 {operation} all select 1", 324);
        AreEqual(state, ex.State);
        AreEqual($"The 'ALL' version of the {operation.ToUpperInvariant()} operator is not supported.", ex.Message);
    }

    /// <summary>
    /// Where a query nests — a derived table, a CTE, a subquery, an
    /// <c>APPLY</c> body, a view or inline function body — each branch of a set
    /// operation may carry an <c>ORDER BY</c> with a <c>TOP</c> or
    /// <c>OFFSET</c>, which orders that branch alone, so a trailing <c>ORDER
    /// BY … OFFSET</c> pages the last branch; a statement's own query pages the
    /// combined rows (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select string_agg(a, ',') within group (order by a) from (select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only) d", "2,5,6,7")]
    [DataRow("with c as (select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only) select string_agg(a, ',') within group (order by a) from c", "2,5,6,7")]
    [DataRow("select string_agg(a, ',') within group (order by a) from (select top 1 a from t order by a desc union all select a from u) d", "1,2,3,7")]
    [DataRow("select string_agg(a, ',') within group (order by a) from (select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only union all select 100) d", "2,5,6,7,100")]
    [DataRow("select string_agg(a, ',') within group (order by a) from t where a in (select top 1 a from t order by a desc union select 5)", "5,7")]
    [DataRow("select string_agg(b, ',') within group (order by b) from (select a as b from t union all select b from w order by b offset 1 rows fetch next 1 rows only) d", "2,5,6,7")]
    [DataRow("select string_agg(a, ',') within group (order by a) from (select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only) d cross apply (select 1 z) x", "2,5,6,7")]
    public void NestedSetOperation_BranchOrdersItself(string sql, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int); insert t values (5), (6), (7)",
            "create table u (a int); insert u values (3), (1), (2)",
            "create table w (b int); insert w values (3), (1), (2)");
        AreEqual(expected, sim.ExecuteScalar(sql));
    }

    [TestMethod]
    public void NestedSetOperation_BranchOrdersItself_InViewAndInlineFunctionBodies()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int); insert t values (5), (6), (7)",
            "create table u (a int); insert u values (3), (1), (2)",
            "create view v as select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only",
            "create function f() returns table as return select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only",
            "create function g() returns table as return select a from t order by a offset 1 rows fetch next 1 rows only");
        AreEqual("2,5,6,7", sim.ExecuteScalar("select string_agg(a, ',') within group (order by a) from v"));
        AreEqual("2,5,6,7", sim.ExecuteScalar("select string_agg(a, ',') within group (order by a) from f()"));
        AreEqual(6, sim.ExecuteScalar("select a from g()"));
        // The statement's own query pages the combined rows.
        AreEqual(2, sim.ExecuteScalar("select a from t union all select a from u order by a offset 1 rows fetch next 1 rows only"));
    }

    [TestMethod]
    public void NestedSetOperation_BranchOrderByWithoutTopOrOffset_Msg1033()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int)", "create table u (a int)");
        _ = sim.AssertSqlError("select * from (select a from t order by a union all select a from u) d", 1033);
        _ = sim.AssertSqlError("select * from (select top 1 a from t union all select a from u order by a) d", 1033);
        // A statement's own branch ORDER BY is refused at the operator as written.
        sim.AssertSqlError("select top 1 a from t order by a UNION all select a from u", 156, "Incorrect syntax near the keyword 'UNION'.");
    }

    [TestMethod]
    [Description("An ORDER BY ahead of a set operator is Msg 156 naming the operator; in a subquery Msg 1033 comes first.")]
    [DataRow("select 1 a order by a union select 2", 156, "Incorrect syntax near the keyword 'union'.")]
    [DataRow("select 1 a order by a except select 2", 156, "Incorrect syntax near the keyword 'except'.")]
    [DataRow("select 1 a order by a intersect select 2", 156, "Incorrect syntax near the keyword 'intersect'.")]
    [DataRow("select top 1 1 a order by a union select 2", 156, "Incorrect syntax near the keyword 'union'.")]
    [DataRow("select 1 where exists (select 1 a order by a union select 2)", 1033, "The ORDER BY clause is invalid in views, inline functions, derived tables, subqueries, and common table expressions, unless TOP, OFFSET or FOR XML is also specified.")]
    public void OrderByAheadOfASetOperator(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);
}
