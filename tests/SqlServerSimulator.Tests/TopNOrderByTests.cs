using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>TOP (n)</c> over an ORDER BY is served from a bounded top-N heap rather
/// than by sorting the whole buffer — the operator shape real picks too (its
/// plan for the same query is a Clustered Index Scan under a <em>TopN Sort</em>).
/// These pin that the rows are the ones the full sort would have produced, and
/// that every shape needing the full ordered set behind it still gets it.
/// </summary>
[TestClass]
public sealed class TopNOrderByTests
{
    /// <summary>
    /// 200 rows whose sort column is deliberately low-cardinality: <c>grade</c>
    /// cycles 0-9 so every value has 20 ties, which is what puts a tie group
    /// across the boundary for most values of n.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null primary key, grade int not null, label varchar(10) not null);
            declare @i int = 1;
            while @i <= 200
            begin
                insert t values (@i, @i % 10, 'r' + cast(@i as varchar(10)));
                set @i = @i + 1;
            end
            """);
        return simulation;
    }

    private static List<int> Ids(Simulation simulation, string sql)
    {
        var ids = new List<int>();
        using var reader = simulation.ExecuteReader(sql);
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    /// <summary>
    /// The heap's rows are the full sort's first n. Compared against the
    /// identical order taken through <c>OFFSET 0 ROWS FETCH NEXT n</c>, which
    /// declines the heap (an OFFSET is present) and so runs the full sort.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(25)]
    [DataRow(199)]
    [DataRow(200)]
    [DataRow(500)]
    public void TopN_MatchesTheFullSort_OnAUniqueKey(int n)
        => CollectionAssert.AreEqual(
            Ids(Seeded(), $"select id from t order by id desc offset 0 rows fetch next {n} rows only"),
            Ids(Seeded(), $"select top ({n}) id from t order by id desc"));

    /// <summary>
    /// The same, with a tie group straddling the boundary: <c>grade</c> has 20
    /// rows per value, so any n that isn't a multiple of 20 cuts one in half.
    /// The <em>keys</em> must match even where the individual rows chosen from
    /// the straddling group need not (neither the heap nor real's TopN Sort is
    /// stable), so this compares the grade column.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(15)]
    [DataRow(20)]
    [DataRow(21)]
    [DataRow(37)]
    [DataRow(100)]
    public void TopN_MatchesTheFullSort_OnATiedKey(int n)
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Ids(simulation, $"select grade from t order by grade offset 0 rows fetch next {n} rows only"),
            Ids(simulation, $"select top ({n}) grade from t order by grade"));
    }

    /// <summary>
    /// Two tie groups entirely inside the window come back whole: grades 0 and
    /// 1 are 20 rows each, so a cap of exactly 40 is the set of those rows and
    /// nothing else, in <c>grade, id</c> order.
    /// </summary>
    [TestMethod]
    public void TopN_CoveringWholeTieGroups_ReturnsEveryMember()
    {
        var simulation = Seeded();
        var top40 = Ids(simulation, "select top (40) id from t order by grade, id");
        CollectionAssert.AreEqual(
            Ids(simulation, "select id from t where grade in (0, 1) order by grade, id"),
            top40);
        HasCount(40, top40);
        AreEqual(0, simulation.ExecuteScalar($"select count(*) from t where grade not in (0, 1) and id in ({string.Join(", ", top40)})"));
    }

    /// <summary>A multi-key sort with a unique tail is fully determined, ties or not.</summary>
    [TestMethod]
    public void TopN_MultiKeySort_MatchesTheFullSort()
    {
        var simulation = Seeded();
        for (var n = 1; n <= 45; n += 11)
        {
            CollectionAssert.AreEqual(
                Ids(simulation, $"select id from t order by grade desc, id asc offset 0 rows fetch next {n} rows only"),
                Ids(simulation, $"select top ({n}) id from t order by grade desc, id asc"));
        }
    }

    /// <summary>NULLs sort first ascending; the heap must order them the same way.</summary>
    [TestMethod]
    public void TopN_WithNulls_OrdersThemFirstAscending()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null primary key, v int null);
            insert t values (1, 5), (2, null), (3, 1), (4, null), (5, 3)
            """);
        CollectionAssert.AreEqual(
            Ids(simulation, "select id from t order by v, id offset 0 rows fetch next 3 rows only"),
            Ids(simulation, "select top (3) id from t order by v, id"));
        CollectionAssert.AreEqual(
            Ids(simulation, "select id from t order by v desc, id offset 0 rows fetch next 3 rows only"),
            Ids(simulation, "select top (3) id from t order by v desc, id"));
    }

    /// <summary>
    /// The shapes that decline the heap because they need the ordered set
    /// behind the cap. Each is checked against the answer it has always given.
    /// </summary>
    [TestMethod]
    public void WithTies_StillExtendsPastTheCap()
    {
        // grade 0 holds 20 rows, so TOP (5) WITH TIES over `grade` returns all 20.
        AreEqual(20, Seeded().ExecuteScalar("select count(*) from (select top (5) with ties grade from t order by grade) x"));
    }

    [TestMethod]
    public void Percent_StillComputesFromTheTotalCount()
        => AreEqual(20, Seeded().ExecuteScalar("select count(*) from (select top (10) percent id from t order by id) x"));

    [TestMethod]
    public void Offset_StillSkipsIntoTheOrder()
    {
        var simulation = Seeded();
        // Descending from 200, the first 10 skipped are 200 … 191.
        CollectionAssert.AreEqual(
            new[] { 190, 189, 188 },
            Ids(simulation, "select id from t order by id desc offset 10 rows fetch next 3 rows only"));
    }

    [TestMethod]
    public void Distinct_StillDedupesBeforeTheCap()
        => CollectionAssert.AreEqual(
            new[] { 0, 1, 2 },
            Ids(Seeded(), "select distinct top (3) grade from t order by grade"));

    /// <summary>
    /// A cap past the heap's own ceiling falls back to the full sort and must
    /// answer identically — the boundary is a performance switch, not a
    /// semantic one.
    /// </summary>
    [TestMethod]
    [DataRow(1024)]
    [DataRow(1025)]
    public void CapsAroundTheHeapCeiling_AnswerIdentically(int n)
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Ids(simulation, $"select id from t order by id desc offset 0 rows fetch next {n} rows only"),
            Ids(simulation, $"select top ({n}) id from t order by id desc"));
    }

    /// <summary>TOP (0) and a variable-valued TOP both stay correct.</summary>
    [TestMethod]
    public void ZeroAndVariableCaps_StayCorrect()
    {
        var simulation = Seeded();
        IsEmpty(Ids(simulation, "select top (0) id from t order by id"));
        CollectionAssert.AreEqual(
            new[] { 200, 199 },
            Ids(simulation, "declare @n int = 2; select top (@n) id from t order by id desc"));
    }

    /// <summary>The cap applies to the joined result, not to either input.</summary>
    [TestMethod]
    public void TopN_OverAJoin_MatchesTheFullSort()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("""
            create table g (grade int not null primary key, name varchar(10) not null);
            insert g values (0,'zero'),(1,'one'),(2,'two'),(3,'three'),(4,'four'),
                            (5,'five'),(6,'six'),(7,'seven'),(8,'eight'),(9,'nine')
            """);
        CollectionAssert.AreEqual(
            Ids(simulation, "select t.id from t join g on g.grade = t.grade order by t.id desc offset 0 rows fetch next 7 rows only"),
            Ids(simulation, "select top (7) t.id from t join g on g.grade = t.grade order by t.id desc"));
    }
}
