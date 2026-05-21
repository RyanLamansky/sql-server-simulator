using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL aggregate functions: COUNT/COUNT_BIG/SUM/AVG/MAX/MIN,
/// the statistical family (STDEV, STDEVP, VAR, VARP), STRING_AGG, CHECKSUM_AGG,
/// APPROX_COUNT_DISTINCT — both standalone and with GROUP BY / HAVING.
/// </summary>
[TestClass]
public sealed class AggregateTests
{
    private static DbConnection Seeded(string schema, string values)
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ({schema})").ExecuteNonQuery();
        if (!string.IsNullOrEmpty(values))
            _ = connection.CreateCommand($"insert t values {values}").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void Count_Star_CountsRowsIncludingNullColumns()
    {
        using var connection = Seeded("a int", "(1), (2), (null), (3)");
        AreEqual(4, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Count_Column_SkipsNulls()
    {
        using var connection = Seeded("a int", "(1), (2), (null), (3)");
        AreEqual(3, connection.CreateCommand("select count(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Count_Distinct_DedupsAndSkipsNulls()
    {
        using var connection = Seeded("a int", "(1), (2), (1), (null), (2)");
        AreEqual(2, connection.CreateCommand("select count(distinct a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Count_EmptyInput_ReturnsZero()
    {
        // Only aggregate that doesn't return NULL on empty input.
        using var connection = Seeded("a int", "");
        AreEqual(0, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void CountBig_StarAlias_ReturnsBigInt()
    {
        using var connection = Seeded("a int", "(1), (2), (3)");
        AreEqual(3L, connection.CreateCommand("select count_big(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_Int_TracksTotalSkippingNulls()
    {
        using var connection = Seeded("a int", "(10), (20), (null), (30)");
        AreEqual(60, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_Decimal_PreservesScale()
    {
        using var connection = Seeded("p decimal(10, 2)", "(1.50), (2.50), (3.00)");
        AreEqual(7.00m, connection.CreateCommand("select sum(p) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_EmptyInput_ReturnsNull()
    {
        using var connection = Seeded("a int", "");
        AreEqual(DBNull.Value, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_AllNullInput_ReturnsNull()
    {
        using var connection = Seeded("a int", "(null), (null)");
        AreEqual(DBNull.Value, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_IntOverflow_RaisesMsg8115()
    {
        using var connection = Seeded("a int", "(2147483647), (1)");
        var ex = Throws<DbException>(() => connection.CreateCommand("select sum(a) from t").ExecuteScalar());
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Sum_Float_AccumulatesViaDouble()
    {
        using var connection = Seeded("a float", "(1.5), (2.25), (0.25)");
        AreEqual(4.0, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_Real_AccumulatesViaDoubleAndNarrowsBack()
    {
        using var connection = Seeded("a real", "(1.5), (2.25), (0.25)");
        AreEqual(4.0f, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Avg_Int_TruncatesByIntegerDivision()
    {
        using var connection = Seeded("a int", "(1), (2), (2)");
        AreEqual(1, connection.CreateCommand("select avg(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Avg_Decimal_WidensToDecimal38_6()
    {
        using var connection = Seeded("p decimal(10, 2)", "(1.50), (2.50), (3.00)");
        AreEqual(2.333333m, connection.CreateCommand("select avg(p) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Max_OnInt()
    {
        using var connection = Seeded("a int", "(10), (5), (20), (null)");
        AreEqual(20, connection.CreateCommand("select max(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Min_OnInt()
    {
        using var connection = Seeded("a int", "(10), (5), (20), (null)");
        AreEqual(5, connection.CreateCommand("select min(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void MaxMin_OnString_ByCollationOrder()
    {
        using var connection = Seeded("s nvarchar(20)", "('alpha'), ('gamma'), ('beta')");
        AreEqual("gamma", connection.CreateCommand("select max(s) from t").ExecuteScalar());
        AreEqual("alpha", connection.CreateCommand("select min(s) from t").ExecuteScalar());
    }

    [TestMethod]
    public void MaxMin_EmptyInput_ReturnsNull()
    {
        using var connection = Seeded("a int", "");
        AreEqual(DBNull.Value, connection.CreateCommand("select max(a) from t").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select min(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Max_OnText_RaisesMsg8117()
    {
        using var connection = Seeded("t text", "('x')");
        var ex = Throws<DbException>(() => connection.CreateCommand("select max(t) from t").ExecuteScalar());
        AreEqual("8117", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Stdev_SingleRow_ReturnsNull()
    {
        // Sample stddev needs n > 1.
        using var connection = Seeded("a int", "(5)");
        AreEqual(DBNull.Value, connection.CreateCommand("select stdev(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void StdevP_SingleRow_ReturnsZero()
    {
        using var connection = Seeded("a int", "(5)");
        AreEqual(0d, connection.CreateCommand("select stdevp(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Var_VarP_OverIntegerColumn()
    {
        // 10, 20, 30: mean=20, sample var = ((10-20)² + 0 + (30-20)²) / 2 = 100. Population var = 200/3 ≈ 66.67.
        using var connection = Seeded("a int", "(10), (20), (30)");
        AreEqual(100d, connection.CreateCommand("select var(a) from t").ExecuteScalar());
        var pop = (double)connection.CreateCommand("select varp(a) from t").ExecuteScalar()!;
        IsLessThan(1e-5, Math.Abs(pop - 66.6666666));
    }

    [TestMethod]
    public void StringAgg_ConcatsWithSeparator()
    {
        using var connection = Seeded("s nvarchar(20)", "('a'), ('b'), ('c')");
        AreEqual("a,b,c", connection.CreateCommand("select string_agg(s, ',') from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_SkipsNulls()
    {
        using var connection = Seeded("s nvarchar(20)", "('a'), (null), ('b')");
        AreEqual("a,b", connection.CreateCommand("select string_agg(s, ',') from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_EmptyInput_ReturnsNull()
    {
        using var connection = Seeded("s nvarchar(20)", "");
        AreEqual(DBNull.Value, connection.CreateCommand("select string_agg(s, ',') from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_WithinGroup_OrderByAsc_ReordersConcatenation()
        => AreEqual("alice,bob,charlie", Seeded("s nvarchar(20)", "('charlie'), ('alice'), ('bob')")
            .CreateCommand("select string_agg(s, ',') within group (order by s) from t").ExecuteScalar());

    [TestMethod]
    public void StringAgg_WithinGroup_OrderByDesc_ReverseConcatenation()
        => AreEqual("charlie,bob,alice", Seeded("s nvarchar(20)", "('charlie'), ('alice'), ('bob')")
            .CreateCommand("select string_agg(s, ',') within group (order by s desc) from t").ExecuteScalar());

    // ORDER BY a column that's NOT the aggregate operand — sort key and aggregated value differ.
    [TestMethod]
    public void StringAgg_WithinGroup_OrderByDifferentColumn()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (name nvarchar(20), score int);
            insert t values ('charlie', 30), ('alice', 10), ('bob', 20)
            """).ExecuteNonQuery();
        AreEqual("alice,bob,charlie",
            connection.CreateCommand("select string_agg(name, ',') within group (order by score) from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_WithinGroup_OrderByExpression()
    {
        using var connection = Seeded("s nvarchar(20)", "('charlie'), ('alice'), ('bob')");
        // LEN DESC → 'charlie' (7), 'alice' (5), 'bob' (3).
        AreEqual("charlie,alice,bob",
            connection.CreateCommand("select string_agg(s, ',') within group (order by len(s) desc) from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_WithinGroup_MultiColumnOrderBy()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (name nvarchar(20), score int);
            insert t values ('alpha', 10), ('beta', 20), ('gamma', 20), ('delta', 10)
            """).ExecuteNonQuery();
        // ORDER BY score DESC, name ASC: (beta, gamma) tied at 20 → alphabetical, then (alpha, delta) at 10 → alphabetical.
        AreEqual("beta,gamma,alpha,delta",
            connection.CreateCommand("select string_agg(name, ',') within group (order by score desc, name) from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_WithinGroup_PerGroup_OrdersWithinEachGroup()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (g int, name nvarchar(20));
            insert t values (1, 'charlie'), (1, 'alice'), (1, 'bob'), (2, 'echo'), (2, 'delta')
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select g, string_agg(name, ',') within group (order by name) from t group by g order by g").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("alice,bob,charlie", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual("delta,echo", reader.GetString(1));
        IsFalse(reader.Read());
    }

    // NULL operand rows are skipped from the input set; ORDER BY runs over the remaining values.
    [TestMethod]
    public void StringAgg_WithinGroup_SkipsNulls()
        => AreEqual("alice,bob,charlie", Seeded("s nvarchar(20)", "('charlie'), (null), ('alice'), ('bob')")
            .CreateCommand("select string_agg(s, ',') within group (order by s) from t").ExecuteScalar());

    [TestMethod]
    public void StringAgg_WithinGroup_EmptyInput_ReturnsNull()
        => AreEqual(DBNull.Value, Seeded("s nvarchar(20)", "")
            .CreateCommand("select string_agg(s, ',') within group (order by s) from t").ExecuteScalar());

    [TestMethod]
    public void StringAgg_WithinGroup_SingleRow_ReturnsValue()
        => AreEqual("only", Seeded("s nvarchar(20)", "('only')")
            .CreateCommand("select string_agg(s, ',') within group (order by s) from t").ExecuteScalar());

    [TestMethod]
    public void Max_WithinGroup_RaisesMsg10757()
        => new Simulation().AssertSqlError("""
            create table t (s nvarchar(20));
            insert t values ('a');
            select max(s) within group (order by s) from t
            """, 10757,
            "The function 'max' may not have a WITHIN GROUP clause.");

    [TestMethod]
    public void Sum_WithinGroup_RaisesMsg10757()
        => new Simulation().AssertSqlError("""
            create table t (a int);
            insert t values (1);
            select sum(a) within group (order by a) from t
            """, 10757,
            "The function 'sum' may not have a WITHIN GROUP clause.");

    [TestMethod]
    public void StringAgg_WithinGroup_OrderByOrdinal_RaisesMsg5308()
        => new Simulation().AssertSqlError("""
            create table t (s nvarchar(20));
            insert t values ('a');
            select string_agg(s, ',') within group (order by 1) from t
            """, 5308,
            "Windowed functions, aggregates and NEXT VALUE FOR functions do not support integer indices as ORDER BY clause expressions.");

    [TestMethod]
    public void StringAgg_WithinGroup_NoParens_RaisesSyntaxError()
        => _ = new Simulation().AssertSqlError("""
            create table t (s nvarchar(20));
            select string_agg(s, ',') within group from t
            """, 156);

    [TestMethod]
    public void StringAgg_WithinGroup_EmptyParens_RaisesSyntaxError()
        => _ = new Simulation().AssertSqlError("""
            create table t (s nvarchar(20));
            select string_agg(s, ',') within group () from t
            """, 102);

    // Contextual `within`: the parser must not reserve the identifier — column / alias use must still work.
    [TestMethod]
    public void Within_AsColumnName_StillParses()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (within int);
            insert t values (1), (2)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select within from t order by within").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void ChecksumAgg_OrderIndependentFold()
    {
        // Semantic guarantee: same multiset → same checksum (exact bit pattern not pinned).
        using var ascending = Seeded("a int", "(1), (2), (3)");
        using var reversed = Seeded("a int", "(3), (2), (1)");
        AreEqual(
            ascending.CreateCommand("select checksum_agg(a) from t").ExecuteScalar(),
            reversed.CreateCommand("select checksum_agg(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void ApproxCountDistinct_BehavesLikeCountDistinct()
    {
        // Simulator implements as exact COUNT(DISTINCT). Returns bigint.
        using var connection = Seeded("a int", "(1), (2), (1), (null), (3)");
        AreEqual(3L, connection.CreateCommand("select approx_count_distinct(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void GroupBy_PartitionsByKey()
    {
        using var connection = Seeded("s nvarchar(20), a int", "('alpha', 1), ('alpha', 2), ('beta', 5), ('beta', 7)");
        using var reader = connection.CreateCommand("select s, sum(a) from t group by s").ExecuteReader();
        var totals = new Dictionary<string, int>();
        while (reader.Read())
            totals[(string)reader[0]] = (int)reader[1];
        AreEqual(3, totals["alpha"]);
        AreEqual(12, totals["beta"]);
    }

    [TestMethod]
    public void GroupBy_NullKey_OneBucketForNulls()
    {
        // SQL Server: NULL is a valid group key with exactly one bucket.
        using var connection = Seeded("a int, b int", "(null, 1), (null, 2), (1, 5)");
        using var reader = connection.CreateCommand("select a, sum(b) from t group by a").ExecuteReader();
        var seen = new List<(object key, int sum)>();
        while (reader.Read())
            seen.Add((reader[0], (int)reader[1]));
        HasCount(2, seen);
    }

    [TestMethod]
    public void GroupBy_Having_FiltersByAggregatePredicate()
    {
        using var connection = Seeded("s nvarchar(20)", "('alpha'), ('alpha'), ('beta'), ('gamma')");
        using var reader = connection.CreateCommand("select s, count(*) from t group by s having count(*) > 1").ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            AreEqual("alpha", reader[0]);
            AreEqual(2, reader[1]);
            rows++;
        }
        AreEqual(1, rows);
    }

    [TestMethod]
    public void Min_BitColumn_RaisesMsg8117()
        => new Simulation().AssertSqlError(
            "create table t (b bit not null); insert t values (1), (0); select min(b) from t",
            8117,
            "Operand data type bit is invalid for min operator.");

    [TestMethod]
    public void Max_BitColumn_RaisesMsg8117()
        => new Simulation().AssertSqlError(
            "create table t (b bit not null); insert t values (1), (0); select max(b) from t",
            8117,
            "Operand data type bit is invalid for max operator.");

    /// <summary>
    /// Probe-confirmed against SQL Server 2025: every recognized collation
    /// (SQL_*, Windows-style, locale) applies <c>IgnoreSymbols</c> in sort
    /// — apostrophe / hyphen drop out of the primary sort key, so MIN of
    /// ('Aaronsburg', "'Aiea") returns 'Aaronsburg' because apostrophe is
    /// stripped from "'Aiea" leaving "Aiea" > "Aaronsburg". Equality keeps
    /// symbols significant; <see cref="Equality_String_DistinguishesApostrophe"/>
    /// pins that asymmetry.
    /// </summary>
    [TestMethod]
    public void Min_String_DefaultCollation_ApostropheIsIgnoredInSort()
        => AreEqual("Aaronsburg", new Simulation().ExecuteScalar(
            "create table t (s nvarchar(40) not null); insert t values (N'Aaronsburg'), (N'''Aiea'); select min(s) from t"));

    /// <summary>
    /// Equality (=) on the default <c>SQL_Latin1_General_CP1_CI_AS</c> is
    /// strict about apostrophe — the leading 0x27 distinguishes 'O''Brien'
    /// from 'OBrien'. Real SQL Server's Windows-style CI_AS family also
    /// keeps equality strict about symbols (the primary-weight-zero rule
    /// is sort-only); the direct probe of that asymmetry lives in
    /// <c>SqlServerSimulator.Tests.Internal/CollationTests.cs</c>.
    /// </summary>
    [TestMethod]
    public void Equality_String_DistinguishesApostrophe()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select case when N'OBrien' = N'O''Brien' then 1 else 0 end"));

    [TestMethod]
    public void Sum_Distinct_DedupesDuplicates()
        => AreEqual(6, new Simulation().ExecuteScalar(
            "create table t (v int); insert t values (1), (2), (3), (2), (1); select sum(distinct v) from t"));

    [TestMethod]
    public void Avg_Distinct_DedupesDuplicates()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table t (v int); insert t values (1), (2), (3), (2), (1); select avg(distinct v) from t"));

    [TestMethod]
    public void Avg_Decimal_WidensScaleTo6()
    {
        // AVG(decimal(p, s)) → decimal(38, max(s, 6)) per DeriveAvgResultType.
        var result = (decimal)new Simulation().ExecuteScalar("""
            create table t (v decimal(10, 2));
            insert t values (1.0), (2.0);
            select avg(v) from t
            """)!;
        AreEqual(1.500000m, result);
    }
}
