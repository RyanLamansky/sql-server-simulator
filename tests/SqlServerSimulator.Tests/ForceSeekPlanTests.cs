using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Whether real's optimizer can honor a <c>FORCESEEK</c> — or a
/// <c>FORCESCAN</c> beside an <c>INDEX</c> hint — and Msg 8622 when it can't.
/// Every shape was probed 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ForceSeekPlanTests
{
    private const string Setup = """
        create table t (k int primary key, a int, b int, c varchar(20), d int, index ia (a), index iab (a, b) include (d), index ic (c));
        insert t values (1, 1, 1, 'abc', 1), (2, 2, 2, 'bcd', 2);
        create table h (k int, a int, index ia (a));
        insert h values (1, 1);
        create table hn (k int, a int);
        """;

    private static void Accepts(string query) => _ = new Simulation().ExecuteNonQuery(Setup + query);

    [TestMethod]
    [DataRow("select * from t with (forceseek)")]
    [DataRow("select count(*) from t with (forceseek)")]
    [DataRow("select top 1 * from t with (forceseek) order by a")]
    [DataRow("select * from t with (forceseek) where d = 1")]
    [DataRow("select * from t with (forceseek) where abs(a) = 1")]
    [DataRow("select * from t with (forceseek) where a + 1 = 2")]
    [DataRow("select * from t with (forceseek) where -a = 1")]
    [DataRow("select * from t with (forceseek) where isnull(a, 0) = 1")]
    [DataRow("select * from t with (forceseek) where cast(a as varchar(10)) = '1'")]
    [DataRow("select * from t with (forceseek) where c like '%bc'")]
    [DataRow("select * from t with (forceseek) where c like '_bc'")]
    [DataRow("select * from t with (forceseek) where c = N'abc'")]
    [DataRow("select * from t with (forceseek) where c like N'ab%'")]
    [DataRow("select * from t with (forceseek) where a = 1 or d = 1")]
    [DataRow("select * from t with (forceseek) where b = 1")]
    [DataRow("select * from t with (forceseek) where a = b")]
    [DataRow("select * from t with (forceseek) where 1 = 1")]
    [DataRow("select * from t with (forceseek(ia(a))) where k = 1")]
    [DataRow("select * from t with (forceseek(iab(a, b))) where a = 1")]
    [DataRow("select * from t with (forceseek, index(0)) where a = 1")]
    [DataRow("select * from t with (forceseek, index(ia)) where k = 1")]
    [DataRow("select * from hn with (forceseek) where a = 1")]
    [DataRow("select * from h with (forceseek) where k = 1")]
    [DataRow("select * from t x join t y with (forceseek) on y.d = x.d")]
    [DataRow("select * from t x cross join t y with (forceseek)")]
    [DataRow("update x set d = 1 from t x join t y with (forceseek) on y.d = x.d")]
    [DataRow("select * from t where exists (select 1 from t y with (forceseek) where y.d = 1)")]
    [DataRow("select * from t where a in (select d from t y with (forceseek))")]
    [DataRow("select * from t with (forcescan, index(ia)) where d = 1")]
    [DataRow("select * from t with (forcescan, index(ia))")]
    public void UnseekableShapes_RaiseMsg8622(string query)
        => new Simulation().AssertSqlError(
            Setup + query,
            8622,
            "Query processor could not produce a query plan because of the hints defined in this query. Resubmit the query without specifying any hints and without using SET FORCEPLAN.");

    [TestMethod]
    [DataRow("select * from t with (forceseek) where k = 1")]
    [DataRow("select * from t with (forceseek) where c like 'ab%'")]
    [DataRow("select * from t with (forceseek) where c like '[ab]bc'")]
    [DataRow("declare @p varchar(10) = 'ab%'; select * from t with (forceseek) where c like @p")]
    [DataRow("select * from t with (forceseek) where a = 1 or c = 'x'")]
    [DataRow("select * from t with (forceseek) where a = 1 or a = 2")]
    [DataRow("select * from t with (forceseek) where a <> 1")]
    [DataRow("select * from t with (forceseek) where not (a = 1)")]
    [DataRow("select * from t with (forceseek) where a is null")]
    [DataRow("select * from t with (forceseek) where a > 1")]
    [DataRow("select * from t with (forceseek) where a between 1 and 2")]
    [DataRow("select * from t with (forceseek) where 1 between a and b")]
    [DataRow("select * from t with (forceseek) where a not in (1, 2)")]
    [DataRow("select * from t with (forceseek) where a in (select 1)")]
    [DataRow("select * from t with (forceseek) where a = null")]
    [DataRow("select * from t with (forceseek) where 1 = 0")]
    [DataRow("select * from t with (forceseek) where cast(a as bigint) = 1")]
    [DataRow("select * from t with (forceseek) where a = '1'")]
    [DataRow("select * from t with (forceseek) where (a = 1 and d = 3) or (c = 'x' and d = 4)")]
    [DataRow("select a, count(*) from t with (forceseek) group by a having a > 0")]
    [DataRow("select * from t with (forceseek(ia(a))) where a = 1")]
    [DataRow("select * from t with (forceseek(iab(a, b))) where a = 1 and b > 1")]
    [DataRow("select * from t with (forceseek, index(1)) where k = 1")]
    [DataRow("select * from t with (forceseek, index(ia, ic)) where a = 1")]
    [DataRow("select * from h with (forceseek) where a = 1")]
    [DataRow("select * from t x join t y with (forceseek) on y.a = x.a")]
    [DataRow("select * from t x join t y with (forceseek) on y.a > x.a")]
    [DataRow("select * from t x join t y with (forceseek) on y.d = x.d where y.a = 1")]
    [DataRow("select * from t x, t y with (forceseek) where y.a = x.d")]
    [DataRow("select * from t x where exists (select 1 from t y with (forceseek) where y.a = x.b)")]
    [DataRow("select * from t where d in (select a from t y with (forceseek))")]
    [DataRow("delete t where k in (select k from t y with (forceseek) where y.d = 1)")]
    [DataRow("select a, k from t with (forcescan, index(ia))")]
    [DataRow("select a, b, d from t with (forcescan, index(iab)) where d = 1")]
    [DataRow("select * from t with (forcescan, index(1)) where k = 1")]
    [DataRow("select * from h with (forcescan, index(0))")]
    public void SeekableShapes_Run(string query) => Accepts(query);

    /// <summary>
    /// The refusal is the optimizer's, while the batch compiles: nothing in
    /// the batch runs, and a TRY around the statement doesn't catch it.
    /// </summary>
    [TestMethod]
    public void RefusalEndsTheWholeBatchBeforeItRuns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() =>
            connection.CreateCommand("insert hn values (5, 5); begin try select * from t with (forceseek) where d = 1 end try begin catch select 'caught' end catch").ExecuteNonQuery());
        AreEqual(8622, ex.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from hn"));
    }

    /// <summary>A statement over a table the batch creates meets the refusal when it runs, after the statements before it.</summary>
    [TestMethod]
    public void DeferredStatement_RaisesWhenItRuns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(8622, sim.AssertSqlError("insert hn values (6, 6); create table n (a int); select * from n with (forceseek) where a = 1; insert hn values (7, 7)", 8622).Number);
        AreEqual(1, sim.ExecuteScalar("select count(*) from hn"));
    }

    /// <summary>
    /// Only the first refusal is reported, and a binder error ahead of it
    /// keeps it from being reported at all — a statement that didn't bind is
    /// never optimized.
    /// </summary>
    [TestMethod]
    [DataRow("select * from t with (forceseek) where d = 1; select * from t with (forceseek) where b = 1", 8622)]
    [DataRow("select * from t with (forceseek) where d = 1; select nope from t", 8622)]
    [DataRow("select nope from t; select * from t with (forceseek) where d = 1", 207)]
    public void TheBatchReportsOneError(string batch, int number)
    {
        var ex = new Simulation().AssertSqlError(Setup + batch, number);
        AreEqual(1, ex.Errors.Count);
    }

    /// <summary>A procedure whose body can't seek creates, and refuses when it runs.</summary>
    [TestMethod]
    public void Procedure_CreatesAndRefusesWhenExecuted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        _ = sim.ExecuteNonQuery("create procedure p as select * from t with (forceseek) where d = 1");
        AreEqual("p", sim.AssertSqlError("exec p", 8622).Errors[0].Procedure);
    }

    [TestMethod]
    public void DynamicSql_Refuses()
        => new Simulation().AssertSqlError(Setup + "exec('select * from t with (forceseek) where d = 1')", 8622);

    [TestMethod]
    public void ColumnstoreOnly_CantSeek()
        => new Simulation().AssertSqlError("create table cs (k int, a int, index ccs clustered columnstore); select * from cs with (forceseek) where a = 1", 8622);

    [TestMethod]
    public void ForceSeekWithForceScan_RaisesMsg10746()
        => new Simulation().AssertSqlError(
            Setup + "select * from t with (forceseek, forcescan) where a = 1",
            10746,
            "The FORCESCAN hint is specified simultaneously with the FORCESEEK hint. Remove one of the hints and resubmit the query.");

    [TestMethod]
    public void ParameterizedForceSeekWithIndexHint_RaisesMsg10747()
        => new Simulation().AssertSqlError(Setup + "select * from t with (forceseek(ia(a)), index(ic)) where a = 1", 10747);

    [TestMethod]
    public void ForceScanWithTwoIndexes_RaisesMsg10750()
        => new Simulation().AssertSqlError(
            Setup + "select a, c from t with (forcescan, index(ia, ic))",
            10750,
            "The FORCESCAN hint cannot be used with more than one INDEX hint. Remove the extra INDEX hints and resubmit the query.");

    /// <summary>The nested form's column list is part of its grammar: a bare <c>FORCESEEK(ix)</c> is a syntax error.</summary>
    [TestMethod]
    public void ForceSeekNamingAnIndexWithoutColumns_IsMsg102()
        => new Simulation().AssertSqlError(Setup + "select * from t with (forceseek(ia)) where a = 1", 102, "Incorrect syntax near ')'.");

    /// <summary>
    /// A seek the refusal lets through reads the same rows it would without
    /// the hint.
    /// </summary>
    [TestMethod]
    public void AcceptedForceSeek_ReturnsTheRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        using var connection = sim.CreateOpenConnection();
        AreEqual(2, connection.CreateCommand("select count(*) from t with (forceseek) where a in (1, 2)").ExecuteScalar());
    }

    /// <summary>
    /// INDEX(0), the heap or clustered scan, beside any other index is a plan
    /// no access path builds — Msg 8622 in state 2 — while repeating one index
    /// is fine (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select count(*) from t with (index(0, 1))")]
    [DataRow("select count(*) from t with (index(ia, 0))")]
    [DataRow("select count(*) from t with (index(0), index(2))")]
    public void IndexZeroBesideAnotherIndex_IsMsg8622State2(string query)
        => AreEqual(2, new Simulation().AssertSqlError(Setup + query, 8622).State);

    [TestMethod]
    public void IndexRepeated_IsTaken()
        => Accepts("select count(*) from t with (index(0, 0)); select count(*) from t with (index(1, 1))");

    /// <summary>
    /// FORCESEEK's nested form names its index by id as well as by name, the
    /// messages then giving the id as its name, and index 0 seeks nothing.
    /// </summary>
    [TestMethod]
    public void ForceSeek_ByIndexId()
    {
        // The inline indexes take their ids in reverse, as on real: ia is 4.
        Accepts("select count(*) from t with (forceseek(4(a))) where a = 1");
        AreEqual(
            "The query processor could not produce a query plan because the name 'k' in the FORCESEEK hint on table or view 't' did not match the key column names of the index '4'.",
            new Simulation().AssertSqlError(Setup + "select count(*) from t with (forceseek(4(k))) where a = 1", 362).Message);
        _ = new Simulation().AssertSqlError(Setup + "select count(*) from t with (forceseek(9(k))) where k = 1", 307);
        _ = new Simulation().AssertSqlError(Setup + "select count(*) from t with (forceseek(0(k))) where k = 1", 10749);
    }

    [TestMethod]
    public void ForceSeek_ADisabledIndexSeeksNothing()
        => _ = new Simulation().AssertSqlError(Setup + "alter index ia on t disable; alter index iab on t disable; select count(*) from t with (forceseek) where a = 1", 8622);

    private static Simulation Views()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table h1 (id int primary key, d int, e int, index ix_e (e));
            create table h2 (id int, d int);
            create table h3 (id int primary key, x int);
            insert h1 values (1, 1, 1), (2, 2, 2); insert h2 values (1, 1); insert h3 values (1, 5), (2, 6);
            """,
            "create view v1 as select id, d, e from h1",
            "create view v2 as select id, d from h2",
            "create view v4 as select * from v1",
            "create view v5 as select id, d, e from h1 where id = 1",
            "create view j1 as select h1.id, h1.d, h3.x from h1 join h3 on h3.id = h1.id",
            "create view u1 as select id, d from h1 union all select id, x from h3",
            "create view g1 as select d, count(*) c from h1 group by d",
            "create view t1 as select top 5 id, d from h1 order by id",
            "create view w1 as select id, d, row_number() over (partition by id order by d) rn from h1",
            "create view w2 as select id, d, sum(d) over () s from h1");
        return simulation;
    }

    /// <summary>
    /// A FORCESEEK on a view or CTE reaches every table the body reads, at any
    /// depth, each of which must seek on the body's own predicates or on the
    /// reading query's through a column the body passes on (probed 2026-10-07
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select * from v1 with (forceseek) where d = 1")]
    [DataRow("select * from v1 x with (forceseek) where x.d = 1")]
    [DataRow("select * from v1 x (forceseek) where x.d = 1")]
    [DataRow("select * from v1 with (forceseek)")]
    [DataRow("select * from v2 with (forceseek) where id = 1")]
    [DataRow("select * from v4 with (forceseek) where d = 1")]
    [DataRow("select * from j1 with (forceseek) where d = 1")]
    [DataRow("select * from j1 with (forceseek) where x = 5")]
    [DataRow("select * from u1 with (forceseek) where d = 1")]
    [DataRow("select * from g1 with (forceseek) where d = 1")]
    [DataRow("select * from t1 with (forceseek) where id = 1")]
    [DataRow("select * from w1 with (forceseek) where d = 1")]
    [DataRow("select * from w2 with (forceseek) where id = 1")]
    [DataRow("select * from h3 where id in (select d from j1 with (forceseek))")]
    [DataRow("select * from j1 with (forceseek) where cast(id as varchar(9)) like '1%'")]
    [DataRow("with c as (select * from h1) select * from c with (forceseek) where d = 1")]
    [DataRow("with c as (select id, d from h1), c2 as (select * from c) select * from c2 with (forceseek) where d = 1")]
    [DataRow("if 1 = 0 select * from v1 with (forceseek) where d = 1")]
    public void ForceSeekThroughAView_UnseekableShapes_RaiseMsg8622(string query)
        => Views().AssertSqlError(query, 8622, "Query processor could not produce a query plan because of the hints defined in this query. Resubmit the query without specifying any hints and without using SET FORCEPLAN.");

    [TestMethod]
    [DataRow("select * from v1 with (forceseek) where id = 1")]
    [DataRow("select * from v1 with (forceseek) where e = 1")]
    [DataRow("select * from v4 with (forceseek) where id = 1")]
    [DataRow("select * from v5 with (forceseek)")]
    [DataRow("select * from h1 join v1 with (forceseek) on v1.id = h1.id")]
    [DataRow("select * from j1 with (forceseek) where id = 1")]
    [DataRow("select * from j1 with (forceseek) where id between 1 and 2")]
    [DataRow("select * from j1 with (forceseek) where id = 1 or id = 2")]
    [DataRow("select * from u1 with (forceseek) where id = 1")]
    [DataRow("select * from w1 with (forceseek) where id = 1")]
    [DataRow("select * from h3 where id in (select id from j1 with (forceseek))")]
    [DataRow("select * from h3 cross apply (select * from j1 with (forceseek) where j1.id = h3.id) q")]
    [DataRow("select * from v1 with (forcescan) where id = 1")]
    [DataRow("with c as (select h1.id, h3.x from h1 join h3 on h3.id = h1.id) select * from c with (forceseek) where id = 1")]
    public void ForceSeekThroughAView_SeekableShapes_Run(string query)
        => _ = Views().ExecuteNonQuery(query);

    [TestMethod]
    public void ForceSeekThroughAView_IsACompileError()
    {
        // The batch never starts, so neither the PRINT nor a CATCH runs.
        var ex = Views().AssertSqlError("print 'x'; begin try select * from v1 with (forceseek) where d = 1 end try begin catch print 'caught' end catch", 8622);
        AreEqual(1, ex.Errors.Count);
    }

    [TestMethod]
    public void ForceSeekNamingAnIndexOnAView_RaisesMsg364()
        => Views().AssertSqlError(
            "select * from v1 with (forceseek(ix_e(e))) where e = 1",
            364,
            "The query processor could not produce a query plan because the FORCESEEK hint on view 'v1' is used without a NOEXPAND hint. Resubmit the query with the NOEXPAND hint or remove the FORCESEEK hint on the view.");
}
