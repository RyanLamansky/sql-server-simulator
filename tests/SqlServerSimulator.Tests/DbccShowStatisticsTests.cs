using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface tests for <c>DBCC SHOW_STATISTICS(&lt;table&gt;, &lt;stat&gt;)
/// WITH HISTOGRAM</c> — the statement DacFx runs before bulk-reading each table
/// during a bacpac export, to chunk it into extraction ranges. The histogram
/// describes the rows as the statistic was last built — at its index's creation
/// over existing rows, by UPDATE STATISTICS, or by an automatic update — one
/// step per distinct leading-key value up to 200 steps, MIN always the first
/// step and MAX the last (the envelope DacFx interpolates between); these assert the probe-confirmed 5-column shape, the
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
        update statistics t;
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
    public void NonPersistedComputedKey_EvaluatesTheColumn()
    {
        // The key column has no row slot, so each step's value is evaluated
        // off the row (WideWorldImporters' Sales.Invoices shape), through an
        // index and a constraint alike.
        using var reader = new Simulation().ExecuteReader(
            """
            create table t (id int not null primary key, v int not null, d as v * 2, constraint uq_d unique (d));
            insert t values (1, 10), (2, 20);
            create index ix_t_d on t(d);
            update statistics t;
            dbcc show_statistics(N't', N'ix_t_d') with histogram, no_infomsgs;
            dbcc show_statistics(N't', N'uq_d') with histogram, no_infomsgs;
            """);

        for (var set = 0; set < 2; set++)
        {
            IsTrue(set == 0 || reader.NextResult());
            IsTrue(reader.Read());
            AreEqual(20, reader.GetInt32(0));
            IsTrue(reader.Read());
            AreEqual(40, reader.GetInt32(0));
            IsFalse(reader.Read());
        }
    }

    [TestMethod]
    public void StringLeadingKey_RangeHiKeyTypedNVarchar()
    {
        using var reader = new Simulation().ExecuteReader(
            """
            create table s (code nvarchar(10) not null, constraint pk_s primary key (code));
            insert s values (N'alpha'), (N'bravo'), (N'charlie');
            update statistics s;
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
            update statistics one;
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

    /// <summary>
    /// The checks every form shares run first (probed 2026-10-04 against SQL
    /// Server 2025): one argument is Msg 2583, a missing table Msg 2501, a
    /// missing statistic Msg 2767 and no SELECT on the table Msg 229 then 2557,
    /// each but the table closed with Msg 2528.
    /// </summary>
    [TestMethod]
    public void SharedChecks_PrecedeTheForm()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (id int primary key, b varchar(10)); create index ix_b on dbo.t(b); create user lo without login");
        CollectionAssert.AreEqual(new[] { 2583, 2528 }, sim.AssertSqlError("dbcc show_statistics('dbo.t')", 2583).Errors.Select(static e => e.Number).ToArray());
        _ = sim.AssertSqlError("dbcc show_statistics('dbo.nosuch', ix_b)", 2501);
        CollectionAssert.AreEqual(new[] { 2767, 2528 }, sim.AssertSqlError("dbcc show_statistics('dbo.t', nosuch)", 2767).Errors.Select(static e => e.Number).ToArray());
        var denied = sim.AssertSqlError("execute as user = 'lo'; dbcc show_statistics('dbo.t', ix_b) with histogram", 229);
        CollectionAssert.AreEqual(new[] { 229, 2557, 2528 }, denied.Errors.Select(static e => e.Number).ToArray());
        AreEqual("User 'lo' does not have permission to run DBCC SHOW_STATISTICS for object 'dbo.t'.", denied.Errors[1].Message);
    }

    private const string Indexed = """
        create table t (id int not null constraint pk_t primary key, a int);
        insert t values (1, 10), (2, 20), (3, 20);
        create index ix_a on t (a);
        """;

    /// <summary>
    /// The density vector carries one row per key prefix, a nonclustered
    /// index's followed by the clustered key it reaches its rows by (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void DensityVector_AppendsTheClusteredKey()
    {
        using var reader = new Simulation().ExecuteReader(Indexed + "dbcc show_statistics('t', ix_a) with density_vector, no_infomsgs");
        IsTrue(reader.Read());
        AreEqual(0.5f, reader.GetFloat(0));
        AreEqual("a", reader.GetString(2));
        IsTrue(reader.Read());
        AreEqual(1f / 3, reader.GetFloat(0));
        AreEqual("a, id", reader.GetString(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void StatHeader_OverAStatisticNeverBuilt_IsNulls()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int not null constraint pk_t primary key);
            insert t values (1);
            dbcc show_statistics('t', pk_t) with stat_header, no_infomsgs
            """);
        IsTrue(reader.Read());
        AreEqual("pk_t", reader.GetString(0));
        IsTrue(reader.IsDBNull(1));
        IsTrue(reader.IsDBNull(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void StatisticArgument_MayBeAVariable()
        => AreEqual(10, new Simulation().ExecuteScalar(Indexed + "declare @s sysname = N'ix_a'; dbcc show_statistics('t', @s) with histogram, no_infomsgs"));

    [TestMethod]
    [DataRow("dbcc show_statistics('t', ix_a) with nosuch", 195, "'nosuch' is not a recognized option.")]
    [DataRow("dbcc show_statistics('t', 2) with histogram", 2560, "Parameter 2 is incorrect for this DBCC statement.")]
    public void Arguments_Refusals(string statement, int number, string message)
        => new Simulation().AssertSqlError(Indexed + statement, number, message);

    [TestMethod]
    public void StatsHistogramDmv_ReadsTheSameSteps()
        => AreEqual("10:1,20:2", new Simulation().ExecuteScalar(Indexed + """
            select string_agg(concat(cast(range_high_key as int), ':', cast(equal_rows as int)), ',') within group (order by step_number)
            from sys.dm_db_stats_histogram(object_id('t'), 2)
            """));
}
