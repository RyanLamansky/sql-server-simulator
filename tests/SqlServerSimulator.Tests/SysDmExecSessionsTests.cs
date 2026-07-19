using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>sys.dm_exec_sessions</c>: one row per live connection on the
/// server, with the session-backed columns reflecting the connection's real
/// state (quoted_identifier, lock_timeout, transaction_isolation_level,
/// open_transaction_count) and the querying session reporting <c>running</c>
/// while the rest report <c>sleeping</c>. SMO's contained-authentication and
/// monitoring queries read this DMV. Session-state semantics probed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SysDmExecSessionsTests
{
    [TestMethod]
    public void OwnSession_ProjectsSingleRunningRow()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select status, cast(is_user_process as int) from sys.dm_exec_sessions where session_id = @@spid").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("running", reader.GetString(0).TrimEnd());
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SecondConnection_AppearsAsSleeping()
    {
        var sim = new Simulation();
        using var querying = sim.CreateOpenConnection();
        using var idle = sim.CreateOpenConnection();
        var idleSpid = Convert.ToInt32(idle.CreateCommand("select @@spid").ExecuteScalar());
        // Seen from the querying session, the idle connection reports sleeping.
        AreEqual("sleeping", Convert.ToString(
            querying.CreateCommand($"select status from sys.dm_exec_sessions where session_id = {idleSpid}").ExecuteScalar())!.TrimEnd());
        AreEqual(2, Convert.ToInt32(querying.CreateCommand("select count(*) from sys.dm_exec_sessions").ExecuteScalar()));
    }

    [TestMethod]
    public void QuotedIdentifier_FlipsAfterSetOff()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(1, Convert.ToInt32(connection.CreateCommand(
            "select cast(quoted_identifier as int) from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
        _ = connection.CreateCommand("set quoted_identifier off").ExecuteNonQuery();
        AreEqual(0, Convert.ToInt32(connection.CreateCommand(
            "select cast(quoted_identifier as int) from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
    }

    [TestMethod]
    public void LockTimeout_ReflectsSetLockTimeout()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("set lock_timeout 2500").ExecuteNonQuery();
        AreEqual(2500, Convert.ToInt32(connection.CreateCommand(
            "select lock_timeout from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
    }

    [TestMethod]
    public void OpenTransactionCount_ReflectsBeginTran()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, Convert.ToInt32(connection.CreateCommand(
            "select open_transaction_count from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
        _ = connection.CreateCommand("begin tran").ExecuteNonQuery();
        AreEqual(1, Convert.ToInt32(connection.CreateCommand(
            "select open_transaction_count from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
    }

    [TestMethod]
    public void TransactionIsolationLevel_ReflectsSetLevel()
    {
        using var connection = new Simulation().CreateOpenConnection();
        // 2 = READ COMMITTED (the fresh-session default).
        AreEqual((short)2, Convert.ToInt16(connection.CreateCommand(
            "select transaction_isolation_level from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
        _ = connection.CreateCommand("set transaction isolation level serializable").ExecuteNonQuery();
        // 4 = SERIALIZABLE.
        AreEqual((short)4, Convert.ToInt16(connection.CreateCommand(
            "select transaction_isolation_level from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
    }
}
