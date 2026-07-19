using Microsoft.SqlServer.Management.Smo;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A plain query through the SMO connection: SMO's own session must appear in
/// <c>sys.dm_exec_sessions</c>, proving the endpoint tracks the live connection
/// SMO opened.
/// </summary>
[TestClass]
public sealed class SessionsTests
{
    private static Server server = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _) => server = SmoFixture.NewServer();

    [ClassCleanup]
    public static void ClassCleanup() => server.ConnectionContext.Disconnect();

    [TestMethod]
    public void DmExecSessions_ContainsOwnConnection()
    {
        var count = Convert.ToInt32(server.ConnectionContext.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id = @@SPID"));
        AreEqual(1, count);
    }
}
