using SqlServerSimulator.Network;

namespace SqlServerSimulator;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>
    /// The worker threads the pool starts without its injection delay. This
    /// process hosts the client and the server of every connection at once,
    /// and both sides block pool threads: SqlClient's open waits on one, and
    /// the endpoint runs a statement on the thread that read its request, so a
    /// <c>WAITFOR</c>, a lock wait or an <c>ALTER DATABASE</c> settle holds one
    /// throughout. Under the parallel run on a runner with few cores, the pool's
    /// default floor (one thread per core, past which it adds them gradually) left a
    /// login or a <c>Task.Delay</c> continuation queued for seconds, which no
    /// timing a test makes survives; a real server's sessions and its clients
    /// never share one pool.
    /// </summary>
    private const int PoolThreadFloor = 64;

    /// <summary>
    /// Warms what would otherwise be first touched in the middle of the parallel
    /// run: the process-wide certificate every listener presents, whose creation
    /// generates an RSA key pair, and JIT compilation of the most common path among
    /// all tests. The latter also improves the accuracy of their timings and
    /// functions as a sanity check against the simulator being completely broken.
    /// Pins <see cref="HostileCulture"/> first, then raises the thread pool's
    /// floor (<see cref="PoolThreadFloor"/>).
    /// </summary>
    [AssemblyInitialize]
    public static void HotPath(TestContext _)
    {
        HostileCulture.Pin();
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        Assert.IsTrue(ThreadPool.SetMinThreads(Math.Max(workers, PoolThreadFloor), completionPorts));
        Assert.IsTrue(TdsServerCertificate.Shared.HasPrivateKey);

        if (System.Diagnostics.Debugger.IsAttached)
            return;

        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select 1";
        Assert.AreEqual(1, command.ExecuteScalar());
    }
}
