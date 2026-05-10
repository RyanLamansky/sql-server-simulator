using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Per-session state on <see cref="SimulatedDbConnection"/> must not leak
/// between connections that share a <see cref="Simulation"/>. Before
/// per-connection scoping these all lived on <see cref="Simulation"/> and
/// each test below would have failed in the cross-connection direction.
/// </summary>
[TestClass]
public sealed class ConnectionIsolationTests
{
    [TestMethod]
    public void ScopeIdentity_DoesNotLeakBetweenConnections()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), x int)");

        using var connA = simulation.CreateOpenConnection();
        using var connB = simulation.CreateOpenConnection();

        _ = connA.CreateCommand("insert t (x) values (42)").ExecuteNonQuery();

        AreEqual(1m, connA.CreateCommand("select SCOPE_IDENTITY()").ExecuteScalar());
        AreEqual(DBNull.Value, connB.CreateCommand("select SCOPE_IDENTITY()").ExecuteScalar());
    }

    [TestMethod]
    public void RowCount_DoesNotLeakBetweenConnections()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");

        using var connA = simulation.CreateOpenConnection();
        using var connB = simulation.CreateOpenConnection();

        _ = connA.CreateCommand("insert t values (1),(2),(3)").ExecuteNonQuery();

        // Connection B never ran a row-producing statement, so its @@ROWCOUNT
        // is still 0 — A's INSERT writing 3 doesn't bleed across.
        AreEqual(0, connB.CreateCommand("select @@ROWCOUNT").ExecuteScalar());
    }

    [TestMethod]
    public void IdentityInsert_DoesNotLeakBetweenConnections()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), x int)");

        using var connA = simulation.CreateOpenConnection();
        using var connB = simulation.CreateOpenConnection();

        _ = connA.CreateCommand("set identity_insert t on").ExecuteNonQuery();

        // SET IDENTITY_INSERT scopes to A. B still sees the column as a
        // generated identity and rejects an explicit value with Msg 544.
        var ex = Throws<System.Data.Common.DbException>(
            () => connB.CreateCommand("insert t (id, x) values (99, 1)").ExecuteNonQuery());
        AreEqual("544", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void TimeDefault_ParsedOnOneConnection_RunsOnDispatchingConnection()
    {
        // Regression coverage for the parse-once-run-many trap: a column
        // default's getutcdate() is parsed once at CREATE TABLE time on whichever
        // connection issued the CREATE, but every later INSERT can run on a
        // different connection. The default must read the *dispatching*
        // connection's per-statement freeze, not the long-frozen value left on
        // the connection that parsed it. Capturing the connection at parse time
        // would freeze both INSERTs at the CREATE-TABLE timestamp.
        var simulation = new Simulation();
        using var connA = simulation.CreateOpenConnection();
        using var connB = simulation.CreateOpenConnection();

        _ = connA.CreateCommand(
            "create table t (id int identity, note nvarchar(10), stamp datetime2(7) default getutcdate())")
            .ExecuteNonQuery();

        _ = connB.CreateCommand("insert t (note) values ('a')").ExecuteNonQuery();
        Thread.Sleep(10);
        _ = connB.CreateCommand("insert t (note) values ('b')").ExecuteNonQuery();

        using var reader = connB.CreateCommand("select stamp from t order by id").ExecuteReader();
        IsTrue(reader.Read());
        var first = reader.GetDateTime(0);
        IsTrue(reader.Read());
        var second = reader.GetDateTime(0);
        IsGreaterThan(first, second);
    }
}
