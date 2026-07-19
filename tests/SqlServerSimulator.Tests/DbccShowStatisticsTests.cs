using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface tests for <c>DBCC SHOW_STATISTICS(&lt;table&gt;, &lt;stat&gt;)
/// WITH HISTOGRAM</c> — the statement DacFx runs before bulk-reading each table
/// during a bacpac export, to chunk it into extraction ranges. The simulator
/// synthesizes an honest multi-step histogram from live heap data: one step per
/// distinct leading-key value up to 200 steps, MIN always the first step and
/// MAX the last (the envelope DacFx interpolates between); these assert the probe-confirmed 5-column shape, the
/// dynamic <c>RANGE_HI_KEY</c> typing, the empty-table empty result set, the
/// Msg 2767 miss, and the unmodeled-option rejection. Column layout / values are
/// probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DbccShowStatisticsTests
{
    private const string StateProvincesLike =
        """
        create table t (id int not null, v int null, constraint pk_t primary key (id));
        insert t values (1, 0), (2, 0), (3, 0), (4, 0), (5, 0);
        """;

    [TestMethod]
    public void IntPrimaryKey_ReturnsFiveColumnHistogram()
    {
        using var reader = new Simulation().ExecuteReader(
            StateProvincesLike + "dbcc show_statistics(N't', N'pk_t') with histogram");

        AreEqual(5, reader.FieldCount);
        AreEqual("RANGE_HI_KEY", reader.GetName(0));
        AreEqual("RANGE_ROWS", reader.GetName(1));
        AreEqual("EQ_ROWS", reader.GetName(2));
        AreEqual("DISTINCT_RANGE_ROWS", reader.GetName(3));
        AreEqual("AVG_RANGE_ROWS", reader.GetName(4));
        AreEqual(typeof(int), reader.GetFieldType(0));
        AreEqual(typeof(float), reader.GetFieldType(1));
        AreEqual(typeof(float), reader.GetFieldType(2));
        AreEqual(typeof(long), reader.GetFieldType(3));
        AreEqual(typeof(float), reader.GetFieldType(4));

        // One step per distinct value; MIN first, MAX last, no gaps between
        // adjacent steps (RANGE_ROWS 0 / AVG_RANGE_ROWS 1 throughout).
        for (var expected = 1; expected <= 5; expected++)
        {
            IsTrue(reader.Read());
            AreEqual(expected, reader.GetInt32(0));
            AreEqual(0f, reader.GetFloat(1));
            AreEqual(1f, reader.GetFloat(2));
            AreEqual(0L, reader.GetInt64(3));
            AreEqual(1f, reader.GetFloat(4));
        }
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void NonclusteredIndexStat_ResolvesViaIndexIdentity()
    {
        // The named stat is a CREATE INDEX (Index-backed, not a KeyConstraint);
        // its leading key column is v, whose max is 40.
        using var reader = new Simulation().ExecuteReader(
            """
            create table t (id int not null primary key, v int not null);
            insert t values (1, 10), (2, 20), (3, 40);
            create index ix_t_v on t(v);
            dbcc show_statistics(N't', N'ix_t_v') with histogram
            """);

        // Three distinct values → three steps: 10 (MIN), 20, 40.
        IsTrue(reader.Read());
        AreEqual(10, reader.GetInt32(0));
        IsTrue(reader.Read());
        AreEqual(20, reader.GetInt32(0));
        IsTrue(reader.Read());
        AreEqual(40, reader.GetInt32(0));
        AreEqual(0f, reader.GetFloat(1));
        AreEqual(1f, reader.GetFloat(2));
        AreEqual(0L, reader.GetInt64(3));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void StringLeadingKey_RangeHiKeyTypedNVarchar()
    {
        using var reader = new Simulation().ExecuteReader(
            """
            create table s (code nvarchar(10) not null, constraint pk_s primary key (code));
            insert s values (N'alpha'), (N'bravo'), (N'charlie');
            dbcc show_statistics(N's', N'pk_s') with histogram
            """);

        AreEqual(typeof(string), reader.GetFieldType(0));
        IsTrue(reader.Read());
        AreEqual("alpha", reader.GetString(0));     // MIN step first, collation order
        IsTrue(reader.Read());
        AreEqual("bravo", reader.GetString(0));
        IsTrue(reader.Read());
        AreEqual("charlie", reader.GetString(0));   // MAX step last
        AreEqual(0f, reader.GetFloat(1));
        AreEqual(1f, reader.GetFloat(2));
        AreEqual(0L, reader.GetInt64(3));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void EmptyTable_YieldsEmptyHistogram()
    {
        using var reader = new Simulation().ExecuteReader(
            """
            create table e (id int not null, constraint pk_e primary key (id));
            dbcc show_statistics(N'e', N'pk_e') with histogram
            """);

        AreEqual(5, reader.FieldCount);
        AreEqual(typeof(int), reader.GetFieldType(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleRow_AvgRangeRowsIsOne()
    {
        // No range rows below the max, so AVG_RANGE_ROWS is 1, not 0 — matches
        // real's single-row convention (probe-confirmed).
        using var reader = new Simulation().ExecuteReader(
            """
            create table one (id int not null, constraint pk_one primary key (id));
            insert one values (7);
            dbcc show_statistics(N'one', N'pk_one') with histogram
            """);

        IsTrue(reader.Read());
        AreEqual(7, reader.GetInt32(0));
        AreEqual(0f, reader.GetFloat(1));   // RANGE_ROWS
        AreEqual(1f, reader.GetFloat(2));   // EQ_ROWS
        AreEqual(0L, reader.GetInt64(3));   // DISTINCT_RANGE_ROWS
        AreEqual(1f, reader.GetFloat(4));   // AVG_RANGE_ROWS = 1 despite no range rows
    }

    [TestMethod]
    public void UnknownStatistic_Msg2767()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(StateProvincesLike);
        sim.AssertSqlError(
            "dbcc show_statistics(N't', N'no_such_stat') with histogram",
            2767,
            "Could not locate statistics 'no_such_stat' in the system catalogs.");
    }

    [TestMethod]
    public void UnknownTable_Msg2501()
    {
        var ex = new Simulation().AssertSqlError(
            "dbcc show_statistics(N'[dbo].[NoSuchTable]', N'x') with histogram", 2501);
        Contains("Cannot find a table or object with the name \"[dbo].[NoSuchTable]\".", ex.Message);
    }

    [TestMethod]
    public void BareIdentifierArguments_AlsoResolve()
    {
        // Real accepts unquoted names as well as the N'...' string-literal form.
        using var reader = new Simulation().ExecuteReader(
            StateProvincesLike + "dbcc show_statistics(t, pk_t) with histogram");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));   // MIN step first
    }

    [TestMethod]
    public void MidBatch_YieldsTwoResultSets()
    {
        // DacFx's real shape: a probe SELECT immediately precedes the DBCC in the
        // same batch, so the statement must parse mid-batch.
        using var reader = new Simulation().ExecuteReader(
            StateProvincesLike +
            "select top 1 0 from t where id >= (1); dbcc show_statistics(N't', N'pk_t') with histogram");

        AreEqual(1, reader.FieldCount);     // the probe SELECT
        IsTrue(reader.NextResult());
        AreEqual(5, reader.FieldCount);     // the histogram
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));   // MIN step first
    }

    [TestMethod]
    public void StatsStream_NotModeled()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(StateProvincesLike);
        var ex = Throws<NotSupportedException>(
            () => sim.ExecuteScalar("dbcc show_statistics(N't', N'pk_t') with STATS_STREAM"));
        Contains("STATS_STREAM", ex.Message);
    }
}
