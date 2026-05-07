using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's <c>OFFSET ... ROWS [FETCH NEXT|FIRST n
/// ROWS|ROW ONLY]</c> pagination clause, attached to ORDER BY. Covers
/// syntactic synonyms (NEXT/FIRST, ROW/ROWS), required-ORDER-BY rule,
/// FETCH-without-OFFSET rejection (Msg 153), negative-offset (Msg 10742) /
/// non-positive-fetch (Msg 10744) rejection, TOP/OFFSET mutual exclusion
/// (Msg 10741), parameter binding, expression evaluation, and integration
/// with ORDER BY / WHERE / GROUP BY / derived tables / set-op chains.
/// Sourced from probes against SQL Server 2025.
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

    // === Basic syntax variations ===

    [TestMethod]
    public void OffsetZero_ReturnsAllRows()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 0 rows"));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, values);
    }

    [TestMethod]
    public void OffsetTwo_SkipsFirstTwo()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 2 rows"));
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, values);
    }

    [TestMethod]
    public void OffsetFetchNext_LimitsAfterSkip()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 1 rows fetch next 2 rows only"));
        CollectionAssert.AreEqual(new[] { 2, 3 }, values);
    }

    [TestMethod]
    public void FetchFirst_IsSynonymForFetchNext()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 0 rows fetch first 3 rows only"));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
    }

    [TestMethod]
    public void RowSingular_EquivalentToRowsPlural()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 1 row fetch next 1 row only"));
        CollectionAssert.AreEqual(new[] { 2 }, values);
    }

    // === Boundary values ===

    [TestMethod]
    public void OffsetLargerThanResult_ReturnsEmpty()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 99 rows"));
        IsEmpty(values);
    }

    [TestMethod]
    public void FetchLargerThanRemaining_ReturnsRemainder()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 3 rows fetch next 99 rows only"));
        CollectionAssert.AreEqual(new[] { 4, 5 }, values);
    }

    // === Errors ===

    [TestMethod]
    public void NegativeOffset_RaisesMsg10742()
    {
        var ex = Throws<DbException>(() =>
            _ = SeededFiveRows().ExecuteScalar("select id from t order by id offset -1 rows"));
        AreEqual("10742", ex.Data["HelpLink.EvtID"]);
        AreEqual("The offset specified in a OFFSET clause may not be negative.", ex.Message);
    }

    [TestMethod]
    public void FetchZero_RaisesMsg10744()
    {
        var ex = Throws<DbException>(() =>
            _ = SeededFiveRows().ExecuteScalar("select id from t order by id offset 0 rows fetch next 0 rows only"));
        AreEqual("10744", ex.Data["HelpLink.EvtID"]);
        AreEqual("The number of rows provided for a FETCH clause must be greater then zero.", ex.Message);
    }

    [TestMethod]
    public void NegativeFetch_RaisesMsg10744()
    {
        var ex = Throws<DbException>(() =>
            _ = SeededFiveRows().ExecuteScalar("select id from t order by id offset 0 rows fetch next -1 rows only"));
        AreEqual("10744", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void FetchWithoutOffset_RaisesMsg153()
    {
        var ex = Throws<DbException>(() =>
            _ = SeededFiveRows().ExecuteScalar("select id from t order by id fetch next 2 rows only"));
        AreEqual("153", ex.Data["HelpLink.EvtID"]);
        AreEqual("Invalid usage of the option next in the FETCH statement.", ex.Message);
    }

    [TestMethod]
    public void TopWithOffset_RaisesMsg10741()
    {
        var ex = Throws<DbException>(() =>
            _ = SeededFiveRows().ExecuteScalar("select top 2 id from t order by id offset 1 rows fetch next 2 rows only"));
        AreEqual("10741", ex.Data["HelpLink.EvtID"]);
        AreEqual("A TOP can not be used in the same query or sub-query as a OFFSET.", ex.Message);
    }

    [TestMethod]
    public void TopWithOffsetOnly_AlsoRaisesMsg10741()
    {
        // Even without a FETCH clause: TOP + OFFSET still mutually exclusive.
        var ex = Throws<DbException>(() =>
            _ = SeededFiveRows().ExecuteScalar("select top 2 id from t order by id offset 1 rows"));
        AreEqual("10741", ex.Data["HelpLink.EvtID"]);
    }

    // === Expressions and parameters ===

    [TestMethod]
    public void OffsetFetch_AcceptsArithmeticExpression()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id offset 1+1 rows fetch next 1+1 rows only"));
        CollectionAssert.AreEqual(new[] { 3, 4 }, values);
    }

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

    // === ORDER direction and combinations ===

    [TestMethod]
    public void OffsetFetch_AppliesAfterOrderByDescending()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t order by id desc offset 1 rows fetch next 2 rows only"));
        CollectionAssert.AreEqual(new[] { 4, 3 }, values);
    }

    [TestMethod]
    public void OffsetFetch_AppliesAfterWhere()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand("select id from t where id >= 2 order by id offset 1 rows fetch next 2 rows only"));
        CollectionAssert.AreEqual(new[] { 3, 4 }, values);
    }

    // === Tableless SELECT ===

    [TestMethod]
    public void TablelessSelect_OffsetZero_ReturnsRow()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 order by 1 offset 0 rows fetch next 1 rows only"));
        CollectionAssert.AreEqual(new[] { 1 }, values);
    }

    [TestMethod]
    public void TablelessSelect_OffsetOne_ReturnsEmpty()
    {
        var values = ReadInts(new Simulation().CreateCommand("select 1 order by 1 offset 1 rows"));
        IsEmpty(values);
    }

    // === Derived tables ===

    [TestMethod]
    public void OffsetFetch_InsideDerivedTable()
    {
        var values = ReadInts(SeededFiveRows().CreateCommand(
            "select v.id from (select id from t order by id offset 1 rows fetch next 2 rows only) v"));
        CollectionAssert.AreEqual(new[] { 2, 3 }, values);
    }

    // === Aggregate path (GROUP BY) ===

    [TestMethod]
    public void OffsetFetch_OverGroupedAggregate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table g (k int, v int)");
        _ = simulation.ExecuteNonQuery("insert into g values (1, 10), (1, 20), (2, 30), (3, 40), (4, 50)");

        var values = ReadInts(simulation.CreateCommand(
            "select k from g group by k order by k offset 1 rows fetch next 2 rows only"));
        CollectionAssert.AreEqual(new[] { 2, 3 }, values);
    }

    // === Set-op chains ===

    [TestMethod]
    public void TopLevelOffsetFetch_AppliesToSetOpResult()
    {
        // (1, 2, 3, 4, 5) UNION (3, 4, 5, 6) deduped → {1,2,3,4,5,6}; ORDER BY v OFFSET 2 FETCH NEXT 2 → {3,4}.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table left_t (v int)");
        _ = simulation.ExecuteNonQuery("create table right_t (v int)");
        _ = simulation.ExecuteNonQuery("insert into left_t values (1), (2), (3), (4), (5)");
        _ = simulation.ExecuteNonQuery("insert into right_t values (3), (4), (5), (6)");

        using var connection = simulation.CreateOpenConnection();
        var values = ReadInts(connection.CreateCommand(
            "select v from left_t union select v from right_t order by v offset 2 rows fetch next 2 rows only"));
        CollectionAssert.AreEqual(new[] { 3, 4 }, values);
    }

    [TestMethod]
    public void PerBranchOffset_RejectedAsPerBranchOrderBy()
    {
        // OFFSET requires ORDER BY, and per-branch ORDER BY before a set-op
        // already raises Msg 156. So OFFSET in a non-final branch falls
        // through that existing rejection path.
        var ex = Throws<DbException>(() =>
            _ = new Simulation().ExecuteScalar(
                "select 1 as v order by v offset 0 rows fetch next 1 rows only union select 2"));
        AreEqual("156", ex.Data["HelpLink.EvtID"]);
    }
}
