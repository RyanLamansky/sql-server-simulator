using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the bounded per-partition <c>ROW_NUMBER()</c>
/// selection, which no correctness test can see: the bound is
/// result-transparent, so a silent loss of it (or a silent engagement where the
/// shape must decline) would only show as a slow query. Reads the opt-in
/// <see cref="WindowStrategyDiagnostics"/> trace, written at the exact point
/// <c>Selection.BoundRowNumberBodies</c> binds a windowed body to the row-number
/// window an enclosing WHERE leaves surviving.
/// </summary>
[TestClass]
public sealed class WindowTopNStrategyTests
{
    private static SimulatedDbConnection Open()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table t (id int not null primary key, g int not null, k int not null);
            declare @i int = 1;
            while @i <= 200 begin
                insert t values (@i, @i % 8, (@i * 37) % 50);
                set @i += 1;
            end
            create table peer (g int not null, label nvarchar(10) not null);
            insert peer values (0, 'a'), (1, 'b'), (2, 'c'), (3, 'd')
            """);
        return connection;
    }

    private static void Exec(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Runs <paramref name="query"/> to completion, returning the strategy trace and the row count drained.</summary>
    private static (List<string> Trace, int Rows) Run(SimulatedDbConnection connection, string query)
    {
        WindowStrategyDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read())
                rows++;
            return (WindowStrategyDiagnostics.Sink, rows);
        }
        finally
        {
            WindowStrategyDiagnostics.Sink = null;
        }
    }

    private static List<string> TraceOf(string query)
    {
        using var connection = Open();
        return Run(connection, query).Trace;
    }

    /// <summary>The `SELECT … FROM (<paramref name="body"/>) x WHERE <paramref name="filter"/>` shape every test here drives.</summary>
    private static string Shape(string filter, string body =
        "select g, id, row_number() over (partition by g order by k, id) as rn from t") =>
        $"select g, id from ({body}) x where {filter}";

    // ---- the bound engages ----

    [TestMethod]
    public void RowNumberEqualsOne_Binds() =>
        Contains("RowNumberBound(x,1..1)", TraceOf(Shape("rn = 1")));

    [TestMethod]
    public void RowNumberAtMost_Binds() =>
        Contains("RowNumberBound(x,1..3)", TraceOf(Shape("rn <= 3")));

    [TestMethod]
    public void RowNumberLessThan_BindsOneLower() =>
        Contains("RowNumberBound(x,1..3)", TraceOf(Shape("rn < 4")));

    [TestMethod]
    public void RowNumberBetween_BindsBothEnds() =>
        Contains("RowNumberBound(x,2..5)", TraceOf(Shape("rn between 2 and 5")));

    [TestMethod]
    public void TwoOneSidedConjuncts_CombineIntoOneWindow() =>
        Contains("RowNumberBound(x,2..4)", TraceOf(Shape("rn > 1 and rn <= 4")));

    [TestMethod]
    public void ReversedOperandOrder_Binds() =>
        Contains("RowNumberBound(x,1..3)", TraceOf(Shape("3 >= rn")));

    [TestMethod]
    public void EqualityFamily_BindsTheSpanOfItsComparands() =>
        Contains("RowNumberBound(x,1..4)", TraceOf(Shape("rn in (1, 4, 2)")));

    [TestMethod]
    public void FractionalComparand_RoundsOutward() =>
        // `rn <= 2.5` admits two rows; rounding the other way would drop one the
        // residual keeps.
        Contains("RowNumberBound(x,1..2)", TraceOf(Shape("rn <= 2.5")));

    [TestMethod]
    public void VariableBound_BindsItsValue()
    {
        using var connection = Open();
        var (trace, rows) = Run(connection, "declare @k int = 2; " + Shape("rn <= @k"));
        Contains("RowNumberBound(x,1..2)", trace);
        AreEqual(16, rows);
    }

    [TestMethod]
    public void PartitionlessRowNumber_Binds() =>
        Contains(
            "RowNumberBound(x,1..1)",
            TraceOf(Shape("rn = 1", "select g, id, row_number() over (order by k, id) as rn from t")));

    [TestMethod]
    public void BoundPastTheHeapCeiling_StillBinds() =>
        // Past BoundedRowNumberHeapMaxRows the partition sorts as it always did,
        // but the bound still narrows what gets projected — the deep-paging
        // shape's whole win.
        Contains("RowNumberBound(x,5001..5050)", TraceOf(Shape("rn between 5001 and 5050")));

    [TestMethod]
    public void EmptyWindow_Binds() =>
        Contains("RowNumberBound(x,1..0)", TraceOf(Shape("rn < 1")));

    [TestMethod]
    public void JoinedBody_Binds() =>
        Contains(
            "RowNumberBound(x,1..1)",
            TraceOf(Shape(
                "rn = 1",
                "select t.g, t.id, row_number() over (partition by t.g order by t.k, t.id) as rn "
                    + "from t join peer p on p.g = t.g")));

    [TestMethod]
    public void BodyWithItsOwnWhere_Binds() =>
        Contains(
            "RowNumberBound(x,1..1)",
            TraceOf(Shape("rn = 1", "select g, id, row_number() over (partition by g order by k, id) as rn from t where k > 10")));

    [TestMethod]
    public void CteReference_Binds()
    {
        var trace = TraceOf(
            "with c as (select g, id, row_number() over (partition by g order by k, id) as rn from t) "
            + "select g, id from c x where x.rn = 1");
        Contains("RowNumberBound(x,1..1)", trace);
    }

    [TestMethod]
    public void ThroughAPlainDerivedTable_BindsAtTheLevelThatOwnsTheWindow() =>
        // The outer conjunct reaches the window body by riding the ordinary
        // predicate push down the chain, and binds where it lands rather than at
        // the level the filter was written against.
        Contains(
            "RowNumberBound(inner1,1..2)",
            TraceOf("select g, id from (select g, id, rn from "
                + "(select g, id, row_number() over (partition by g order by k, id) as rn from t) inner1) x "
                + "where rn <= 2"));

    [TestMethod]
    public void UnderAnAggregateOuterQuery_Binds() =>
        Contains(
            "RowNumberBound(x,1..1)",
            TraceOf("select count(*) as n from (select g, id, row_number() over (partition by g order by k, id) as rn from t) x where rn = 1"));

    [TestMethod]
    public void InsideASubqueryOfADelete_Binds()
    {
        using var connection = Open();
        WindowStrategyDiagnostics.Sink = [];
        try
        {
            Exec(connection,
                """
                delete from t where id in (
                    select id from (
                        select id, g, row_number() over (partition by g order by k, id) as rn from t) x
                    where rn = 1)
                """);
            Contains("RowNumberBound(x,1..1)", WindowStrategyDiagnostics.Sink);
        }
        finally
        {
            WindowStrategyDiagnostics.Sink = null;
        }
    }

    // ---- the bound declines ----

    [TestMethod]
    public void Rank_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select g, id, rank() over (partition by g order by k) as rn from t")));

    [TestMethod]
    public void DenseRank_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select g, id, dense_rank() over (partition by g order by k) as rn from t")));

    [TestMethod]
    public void NTile_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select g, id, ntile(4) over (partition by g order by k) as rn from t")));

    [TestMethod]
    public void ASecondWindowFunction_Declines() =>
        // A partition buffer serving a SUM OVER can't be bounded: that aggregate
        // reads every row of the partition, not just the ranked few.
        IsEmpty(TraceOf(Shape(
            "rn = 1",
            "select g, id, row_number() over (partition by g order by k, id) as rn, sum(k) over (partition by g) as s from t")));

    [TestMethod]
    public void NonConstantBound_Declines() =>
        IsEmpty(TraceOf(Shape("rn <= g")));

    [TestMethod]
    public void LowerBoundAlone_Declines() =>
        // Nothing to bound: every row of the partition still has to be ranked.
        IsEmpty(TraceOf(Shape("rn >= 2")));

    [TestMethod]
    public void BoundOnAnotherColumnOfTheSameBody_Declines() =>
        IsEmpty(TraceOf(Shape("id <= 10")));

    [TestMethod]
    public void ConjunctUnderAnOr_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1 or g = 3")));

    [TestMethod]
    public void ConjunctInAJoinOn_Declines() =>
        IsEmpty(TraceOf(
            "select p.label from peer p left join "
            + "(select g, id, row_number() over (partition by g order by k, id) as rn from t) x "
            + "on x.g = p.g and x.rn = 1"));

    [TestMethod]
    public void RowNumberInsideAnExpression_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select g, id, row_number() over (partition by g order by k, id) + 0 as rn from t")));

    [TestMethod]
    public void ExpressionOverTheBoundColumn_Declines() =>
        IsEmpty(TraceOf(Shape("rn + 0 = 1")));

    [TestMethod]
    public void DistinctBody_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select distinct g, id, row_number() over (partition by g order by k, id) as rn from t")));

    [TestMethod]
    public void TopBody_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select top (50) g, id, row_number() over (partition by g order by k, id) as rn from t")));

    [TestMethod]
    public void OrderedPagingBody_Declines() =>
        IsEmpty(TraceOf(Shape(
            "rn = 1",
            "select g, id, row_number() over (partition by g order by k, id) as rn from t order by id offset 0 rows fetch next 50 rows only")));

    [TestMethod]
    public void GroupedBody_Declines() =>
        IsEmpty(TraceOf(Shape(
            "rn = 1",
            "select g, count(*) as id, row_number() over (order by count(*) desc) as rn from t group by g")));

    [TestMethod]
    public void NoWindowAtAll_Declines() =>
        IsEmpty(TraceOf(Shape("rn = 1", "select g, id, id as rn from t")));
}
