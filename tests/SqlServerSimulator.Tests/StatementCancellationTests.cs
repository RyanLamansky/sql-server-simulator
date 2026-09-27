using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A cancellation observed <em>inside</em> a running statement — a join whose
/// fold would run for minutes — rather than at a statement boundary. Probed
/// 2026-09-27 against SQL Server 2025 through SqlClient 7.0.2 with the same
/// nine-way cross product and a one-second <c>CommandTimeout</c>: Msg -2, no
/// <c>CATCH</c>, the interrupted statement rolled back, an open transaction
/// kept under <c>XACT_ABORT OFF</c> and rolled back under <c>ON</c>.
/// </summary>
// Sequential, like the WAITFOR timeout tests: each one occupies a thread for
// its whole timeout, and several at once starve the pool.
[TestClass]
[DoNotParallelize]
public sealed class StatementCancellationTests
{
    /// <summary>
    /// A cross product of nine ten-row tables whose WHERE no tuple passes and
    /// no seek, hash or reorder can shortcut: a billion tuples, each one
    /// evaluated.
    /// </summary>
    private const string LongJoin = """
        select count(*) from pa a, pa b, pa c, pa d, pa e, pa f, pa g, pa h, pa i
        where a.x + b.x + c.x + d.x + e.x + f.x + g.x + h.x + i.x < 0
        """;

    private static SimulatedDbConnection Open()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Run(connection, "create table pa (x int); insert pa values (1), (2), (3), (4), (5), (6), (7), (8), (9), (10); create table logt (v int)");
        return connection;
    }

    private static void Run(SimulatedDbConnection connection, string sql, int timeout = 30)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        _ = command.ExecuteNonQuery();
    }

    private static object? Scalar(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    public void Timeout_InterruptsALongJoin()
    {
        using var connection = Open();
        var elapsed = Stopwatch.StartNew();

        var timeout = Throws<SimulatedSqlException>(() => Run(connection, LongJoin, timeout: 1));

        AreEqual(-2, timeout.Number);
        AreEqual(11, timeout.Class);
        IsLessThan(10, elapsed.Elapsed.TotalSeconds);
    }

    [TestMethod]
    public void Timeout_InsideTry_IsNotCaught()
    {
        using var connection = Open();

        var timeout = Throws<SimulatedSqlException>(() => Run(connection, $"begin try {LongJoin} end try begin catch insert logt values (1) end catch", timeout: 1));

        AreEqual(-2, timeout.Number);
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Timeout_RollsBackTheStatement_AndKeepsTheTransaction()
    {
        using var connection = Open();

        _ = Throws<SimulatedSqlException>(() => Run(connection, $"begin tran; insert logt values (1); insert logt {LongJoin}", timeout: 1));

        AreEqual(1, Scalar(connection, "select @@trancount"));
        AreEqual(1, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Timeout_UnderXactAbort_RollsBackTheTransaction()
    {
        using var connection = Open();

        _ = Throws<SimulatedSqlException>(() => Run(connection, $"set xact_abort on; begin tran; insert logt values (1); {LongJoin}", timeout: 1));

        AreEqual(0, Scalar(connection, "select @@trancount"));
        AreEqual(0, Scalar(connection, "select count(*) from logt"));
    }

    [TestMethod]
    public void Cancel_InterruptsALongJoin_AsMsg0()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = LongJoin;
        command.CommandTimeout = 0;

        var done = false;
        var canceller = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                Thread.Sleep(100);
                command.Cancel();
            }
        });
        canceller.Start();
        var cancelled = Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery());
        Volatile.Write(ref done, true);
        canceller.Join();

        AreEqual(0, cancelled.Number);
    }
}
