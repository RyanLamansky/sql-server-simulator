using SqlServerSimulator.Network;

namespace SqlServerSimulator;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>
    /// Warms what would otherwise be first touched in the middle of the parallel
    /// run: the process-wide certificate every listener presents, whose creation
    /// generates an RSA key pair, and JIT compilation of the most common path among
    /// all tests. The latter also improves the accuracy of their timings and
    /// functions as a sanity check against the simulator being completely broken.
    /// </summary>
    [AssemblyInitialize]
    public static void HotPath(TestContext _)
    {
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
