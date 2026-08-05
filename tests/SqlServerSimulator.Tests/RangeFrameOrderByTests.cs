using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real's Msg 8728 — a window whose frame is <c>RANGE</c> may not order by an
/// expression of a MAX (LOB) type — and the transaction-aborting behavior that
/// comes with it. Every case here was probed against SQL Server 2025
/// (2026-08-05); the neighbouring shapes that real accepts, and the
/// neighbouring errors that leave a transaction standing, are covered too,
/// because the whole point of the diagnostic is that it draws an unusually
/// narrow line.
/// </summary>
/// <remarks>
/// This is the root of the Django <c>expressions_window</c> cascade: the
/// simulator used to run the query, so the atomic block Django wrapped it in
/// stayed usable, while on real the aborted transaction made Django's
/// subsequent <c>ROLLBACK TRANSACTION &lt;savepoint&gt;</c> fail (Msg 6401)
/// and poisoned every later test in the class.
/// </remarks>
[TestClass]
public sealed class RangeFrameOrderByTests
{
    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table dbo.w (id int identity primary key, nv nvarchar(max), v varchar(max),
                                vb varbinary(max), n int null, s nvarchar(50));
            insert dbo.w (nv, v, vb, n, s) values (N'a', 'a', 0x01, 1, N'p'), (N'b', 'b', 0x02, 2, N'q')
            """).ExecuteNonQuery();
        return connection;
    }

    private static SimulatedSqlException Refused(DbConnection connection, string sql)
    {
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(sql).ExecuteScalar());
        AreEqual(8728, ex.Number);
        AreEqual("ORDER BY list of RANGE window frame cannot contain expressions of LOB type.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
        return ex;
    }

    private static void Accepted(DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        IsTrue(reader.Read());
    }

    private static object? Scalar(DbConnection connection, string sql) =>
        connection.CreateCommand(sql).ExecuteScalar();

    [TestMethod]
    public void DefaultFrame_OverEachMaxType_IsMsg8728()
    {
        using var connection = Seeded();
        _ = Refused(connection, "select id, sum(n) over (order by nv) from dbo.w");
        _ = Refused(connection, "select id, sum(n) over (order by v) from dbo.w");
        _ = Refused(connection, "select id, sum(n) over (order by vb) from dbo.w");
    }

    [TestMethod]
    public void ExplicitRangeFrame_IsMsg8728()
    {
        using var connection = Seeded();
        _ = Refused(
            connection,
            "select id, sum(n) over (order by nv range between unbounded preceding and current row) from dbo.w");
    }

    [TestMethod]
    public void ExplicitRowsFrame_IsAccepted()
    {
        // The gate is the RANGE frame, not the ordering: spelling ROWS makes
        // the same query legal on real.
        using var connection = Seeded();
        Accepted(connection, "select id, sum(n) over (order by nv rows unbounded preceding) from dbo.w");
    }

    [TestMethod]
    public void FrameTakingFunctions_AreRefused()
    {
        using var connection = Seeded();
        _ = Refused(connection, "select id, max(n) over (order by nv) from dbo.w");
        _ = Refused(connection, "select id, count(*) over (order by nv) from dbo.w");
        _ = Refused(connection, "select id, first_value(n) over (order by nv) from dbo.w");
        _ = Refused(connection, "select id, last_value(n) over (order by nv) from dbo.w");
        // CUME_DIST is the surprise in real's answer set — the one ranking-
        // shaped function that carries a RANGE frame.
        _ = Refused(connection, "select id, cume_dist() over (order by nv) from dbo.w");
    }

    [TestMethod]
    public void FrameLessFunctions_AreAccepted()
    {
        using var connection = Seeded();
        Accepted(connection, "select id, row_number() over (order by nv) from dbo.w");
        Accepted(connection, "select id, rank() over (order by nv) from dbo.w");
        Accepted(connection, "select id, dense_rank() over (order by nv) from dbo.w");
        Accepted(connection, "select id, ntile(2) over (order by nv) from dbo.w");
        Accepted(connection, "select id, percent_rank() over (order by nv) from dbo.w");
        Accepted(connection, "select id, lag(n) over (order by nv) from dbo.w");
        Accepted(connection, "select id, lead(n) over (order by nv) from dbo.w");
    }

    [TestMethod]
    public void OnlyTheOrderByListCounts()
    {
        using var connection = Seeded();
        // A MAX-typed partition key beside an ordinary ordering key is legal.
        Accepted(connection, "select id, sum(n) over (partition by nv order by n) from dbo.w");
        Accepted(connection, "select id, sum(n) over (partition by nv) from dbo.w");
        // A statement-level ORDER BY over the same column is legal too.
        Accepted(connection, "select id from dbo.w order by nv");
        // Any position in the ORDER BY list counts, not just the first.
        _ = Refused(connection, "select id, sum(n) over (order by n, nv) from dbo.w");
    }

    [TestMethod]
    public void BoundedStringOrdering_IsAccepted()
    {
        using var connection = Seeded();
        Accepted(connection, "select id, sum(n) over (order by s) from dbo.w");
        Accepted(connection, "select id, sum(n) over (order by cast(nv as nvarchar(50))) from dbo.w");
    }

    [TestMethod]
    public void ComputedOrderingExpression_IsRefused()
    {
        using var connection = Seeded();
        _ = Refused(connection, "select id, sum(n) over (order by nv + N'x') from dbo.w");
    }

    [TestMethod]
    public void NamedWindowDefinition_IsRefused()
    {
        using var connection = Seeded();
        _ = Refused(connection, "select id, sum(n) over w from dbo.w window w as (order by nv)");
    }

    [TestMethod]
    public void EmptyRowset_StillRaises()
    {
        // Real settles this while compiling, so the absence of rows makes no
        // difference.
        using var connection = Seeded();
        _ = Refused(connection, "select id, sum(n) over (order by nv) from dbo.w where 1 = 0");
    }

    [TestMethod]
    public void TheErrorRollsTheWholeTransactionBack()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("begin transaction; insert dbo.w (nv, n) values (N'in-tx', 99)").ExecuteNonQuery();
        AreEqual(1, Convert.ToInt32(Scalar(connection, "select @@trancount"), null));
        AreEqual(3, Convert.ToInt32(Scalar(connection, "select count(*) from dbo.w"), null));

        _ = Refused(connection, "select id, sum(n) over (order by nv) from dbo.w");

        // Probed against real: @@TRANCOUNT 1 → 0, XACT_STATE() 1 → 0, and the
        // row inserted inside the transaction is gone.
        AreEqual(0, Convert.ToInt32(Scalar(connection, "select @@trancount"), null));
        AreEqual(0, Convert.ToInt32(Scalar(connection, "select xact_state()"), null));
        AreEqual(2, Convert.ToInt32(Scalar(connection, "select count(*) from dbo.w"), null));
    }

    [TestMethod]
    public void TheErrorRollsBackEveryNestingLevel()
    {
        // Probed: @@TRANCOUNT 2 reads 0 afterwards, not 1.
        using var connection = Seeded();
        _ = connection.CreateCommand("begin transaction; begin transaction").ExecuteNonQuery();
        AreEqual(2, Convert.ToInt32(Scalar(connection, "select @@trancount"), null));
        _ = Refused(connection, "select id, sum(n) over (order by nv) from dbo.w");
        AreEqual(0, Convert.ToInt32(Scalar(connection, "select @@trancount"), null));
    }

    [TestMethod]
    public void TryCatchDoesNotCatchIt()
    {
        // Probed: the CATCH block never runs and the batch ends there.
        using var connection = Seeded();
        _ = connection.CreateCommand("begin transaction").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin try
                select id, sum(n) over (order by nv) from dbo.w;
            end try
            begin catch
                select 'caught' as caught;
            end catch
            """).ExecuteScalar());
        AreEqual(8728, ex.Number);
        AreEqual(0, Convert.ToInt32(Scalar(connection, "select @@trancount"), null));
    }

    [TestMethod]
    public void NeighbouringErrorsLeaveTheTransactionStanding()
    {
        // The contrast that makes Msg 8728 worth modeling separately: probed
        // against real, each of these leaves @@TRANCOUNT at 1.
        foreach (var (sql, number) in new (string, int)[]
        {
            ("select 1/0", 8134),
            ("select * from dbo.no_such_table", 208),
            ("select nosuchcolumn from dbo.w", 207),
            ("select id, count(*) from dbo.w group by n", 8120),
            ("select id, sum(n) over (order by n range between 1 preceding and current row) from dbo.w", 4194),
        })
        {
            using var connection = Seeded();
            _ = connection.CreateCommand("begin transaction").ExecuteNonQuery();
            var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(sql).ExecuteScalar());
            AreEqual(number, ex.Number, sql);
            AreEqual(1, Convert.ToInt32(Scalar(connection, "select @@trancount"), null), sql);
        }
    }
}
