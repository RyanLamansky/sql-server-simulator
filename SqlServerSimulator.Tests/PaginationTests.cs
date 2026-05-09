using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's <c>OFFSET ... ROWS [FETCH NEXT|FIRST n
/// ROWS|ROW ONLY]</c> pagination clause: syntactic synonyms, required-ORDER-BY,
/// negative/non-positive errors (Msg 10742 / 10744 with verbatim typos),
/// TOP/OFFSET mutual exclusion (Msg 10741), and integration with WHERE / GROUP BY /
/// derived tables / set-op chains.
/// </summary>
[TestClass]
public sealed class PaginationTests
{
    private static List<int> ReadInts(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        return values;
    }

    private static Simulation SeededFiveRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3), (4), (5)");
        return simulation;
    }

    [TestMethod]
    public void OffsetZero_ReturnsAllRows()
        => CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 0 rows")));

    [TestMethod]
    public void OffsetTwo_SkipsFirstTwo()
        => CollectionAssert.AreEqual(new[] { 3, 4, 5 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 2 rows")));

    [TestMethod]
    public void OffsetFetchNext_LimitsAfterSkip()
        => CollectionAssert.AreEqual(new[] { 2, 3 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 1 rows fetch next 2 rows only")));

    [TestMethod]
    public void FetchFirst_IsSynonymForFetchNext()
        => CollectionAssert.AreEqual(new[] { 1, 2, 3 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 0 rows fetch first 3 rows only")));

    [TestMethod]
    public void RowSingular_EquivalentToRowsPlural()
        => CollectionAssert.AreEqual(new[] { 2 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 1 row fetch next 1 row only")));

    [TestMethod]
    public void OffsetLargerThanResult_ReturnsEmpty()
        => IsEmpty(ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 99 rows")));

    [TestMethod]
    public void FetchLargerThanRemaining_ReturnsRemainder()
        => CollectionAssert.AreEqual(new[] { 4, 5 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 3 rows fetch next 99 rows only")));

    [TestMethod]
    public void NegativeOffset_RaisesMsg10742()
        => SeededFiveRows().AssertSqlError("select id from t order by id offset -1 rows", 10742,
            "The offset specified in a OFFSET clause may not be negative.");

    [TestMethod]
    public void FetchZero_RaisesMsg10744()
        => SeededFiveRows().AssertSqlError("select id from t order by id offset 0 rows fetch next 0 rows only", 10744,
            "The number of rows provided for a FETCH clause must be greater then zero.");

    [TestMethod]
    public void NegativeFetch_RaisesMsg10744()
        => _ = SeededFiveRows().AssertSqlError("select id from t order by id offset 0 rows fetch next -1 rows only", 10744);

    [TestMethod]
    public void FetchWithoutOffset_RaisesMsg153()
        => SeededFiveRows().AssertSqlError("select id from t order by id fetch next 2 rows only", 153,
            "Invalid usage of the option next in the FETCH statement.");

    [TestMethod]
    public void TopWithOffset_RaisesMsg10741()
        => SeededFiveRows().AssertSqlError("select top 2 id from t order by id offset 1 rows fetch next 2 rows only", 10741,
            "A TOP can not be used in the same query or sub-query as a OFFSET.");

    [TestMethod]
    public void TopWithOffsetOnly_AlsoRaisesMsg10741()
        => _ = SeededFiveRows().AssertSqlError("select top 2 id from t order by id offset 1 rows", 10741);

    [TestMethod]
    public void OffsetFetch_AcceptsArithmeticExpression()
        => CollectionAssert.AreEqual(new[] { 3, 4 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 1+1 rows fetch next 1+1 rows only")));

    [TestMethod]
    public void OffsetFetch_AcceptsParameters()
    {
        using var connection = SeededFiveRows().CreateOpenConnection();
        using var command = connection.CreateCommand("select id from t order by id offset @skip rows fetch next @take rows only");
        var skip = command.CreateParameter();
        skip.ParameterName = "@skip";
        skip.Value = 2;
        _ = command.Parameters.Add(skip);
        var take = command.CreateParameter();
        take.ParameterName = "@take";
        take.Value = 2;
        _ = command.Parameters.Add(take);

        var values = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 3, 4 }, values);
    }

    [TestMethod]
    public void OffsetFetch_AppliesAfterOrderByDescending()
        => CollectionAssert.AreEqual(new[] { 4, 3 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t order by id desc offset 1 rows fetch next 2 rows only")));

    [TestMethod]
    public void OffsetFetch_AppliesAfterWhere()
        => CollectionAssert.AreEqual(new[] { 3, 4 },
            ReadInts(SeededFiveRows().CreateCommand("select id from t where id >= 2 order by id offset 1 rows fetch next 2 rows only")));

    [TestMethod]
    public void TablelessSelect_OffsetZero_ReturnsRow()
        => CollectionAssert.AreEqual(new[] { 1 },
            ReadInts(new Simulation().CreateCommand("select 1 order by 1 offset 0 rows fetch next 1 rows only")));

    [TestMethod]
    public void TablelessSelect_OffsetOne_ReturnsEmpty()
        => IsEmpty(ReadInts(new Simulation().CreateCommand("select 1 order by 1 offset 1 rows")));

    [TestMethod]
    public void OffsetFetch_InsideDerivedTable()
        => CollectionAssert.AreEqual(new[] { 2, 3 },
            ReadInts(SeededFiveRows().CreateCommand(
                "select v.id from (select id from t order by id offset 1 rows fetch next 2 rows only) v")));

    [TestMethod]
    public void OffsetFetch_OverGroupedAggregate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table g (k int, v int)");
        _ = simulation.ExecuteNonQuery("insert into g values (1, 10), (1, 20), (2, 30), (3, 40), (4, 50)");

        CollectionAssert.AreEqual(new[] { 2, 3 },
            ReadInts(simulation.CreateCommand("select k from g group by k order by k offset 1 rows fetch next 2 rows only")));
    }

    [TestMethod]
    public void TopLevelOffsetFetch_AppliesToSetOpResult()
    {
        // (1..5) UNION (3..6) deduped → {1..6}; ORDER BY v OFFSET 2 FETCH NEXT 2 → {3,4}.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table left_t (v int)");
        _ = simulation.ExecuteNonQuery("create table right_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into left_t values (1), (2), (3), (4), (5)");
        _ = simulation.ExecuteNonQuery("insert into right_t values (3), (4), (5), (6)");

        using var connection = simulation.CreateOpenConnection();
        CollectionAssert.AreEqual(new[] { 3, 4 },
            ReadInts(connection.CreateCommand(
                "select v from left_t union select v from right_t order by v offset 2 rows fetch next 2 rows only")));
    }

    [TestMethod]
    public void PerBranchOffset_RejectedAsPerBranchOrderBy()
    {
        // OFFSET requires ORDER BY; per-branch ORDER BY before set-op already raises Msg 156.
        _ = new Simulation().AssertSqlError(
        "select 1 as v order by v offset 0 rows fetch next 1 rows only union select 2", 156);
    }
}
