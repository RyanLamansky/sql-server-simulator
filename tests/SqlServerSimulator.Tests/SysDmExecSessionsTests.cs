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

    /// <summary>
    /// An unauthenticated in-process session reports <c>dbo</c> as both
    /// login_name and original_login_name, and both SIDs are the deterministic
    /// per-login bytes rather than a shared literal.
    /// </summary>
    [TestMethod]
    public void LoginName_DefaultSession_ReportsDbo()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("""
            select login_name, original_login_name, datalength(security_id), datalength(original_security_id)
            from sys.dm_exec_sessions where session_id = @@spid
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("dbo", reader.GetString(0));
        AreEqual("dbo", reader.GetString(1));
        AreEqual(16, reader.GetInt32(2));
        AreEqual(16, reader.GetInt32(3));
    }

    /// <summary>
    /// Under <c>EXECUTE AS LOGIN</c> the row's login_name follows the
    /// impersonated login while original_login_name stays the connect-time
    /// one, and security_id tracks login_name rather than staying fixed.
    /// </summary>
    [TestMethod]
    public void LoginName_UnderExecuteAsLogin_FollowsEffectiveLogin()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create login app with password = 'S3cret!Pass';
            create user u for login app
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("execute as login = 'app'").ExecuteNonQuery();
        using var reader = connection.CreateCommand("""
            select login_name, original_login_name,
                   case when security_id = original_security_id then 1 else 0 end
            from sys.dm_exec_sessions where session_id = @@spid
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("app", reader.GetString(0));
        AreEqual("dbo", reader.GetString(1));
        AreEqual(0, reader.GetInt32(2));
    }

    [TestMethod]
    public void TextSize_ReflectsSetTextSize()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(-1, Convert.ToInt32(connection.CreateCommand(
            "select text_size from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
        _ = connection.CreateCommand("set textsize 4096").ExecuteNonQuery();
        AreEqual(4096, Convert.ToInt32(connection.CreateCommand(
            "select text_size from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar()));
    }

    /// <summary>
    /// The ANSI / arithmetic option bits read live session state the way
    /// quoted_identifier does, so a SET flips the row. Fresh-session defaults
    /// are ANSI_NULLS / ANSI_PADDING / ANSI_WARNINGS /
    /// CONCAT_NULL_YIELDS_NULL on and ARITHABORT off.
    /// </summary>
    [TestMethod]
    public void AnsiOptionBits_ReflectLiveSessionState()
    {
        using var connection = new Simulation().CreateOpenConnection();
        const string Read = """
            select cast(ansi_nulls as int), cast(ansi_padding as int), cast(ansi_warnings as int),
                   cast(concat_null_yields_null as int), cast(arithabort as int)
            from sys.dm_exec_sessions where session_id = @@spid
            """;
        using (var reader = connection.CreateCommand(Read).ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(1, reader.GetInt32(0));
            AreEqual(1, reader.GetInt32(1));
            AreEqual(1, reader.GetInt32(2));
            AreEqual(1, reader.GetInt32(3));
            AreEqual(0, reader.GetInt32(4));
        }

        _ = connection.CreateCommand(
            "set ansi_nulls off; set ansi_padding off; set ansi_warnings off; set concat_null_yields_null off; set arithabort on").ExecuteNonQuery();
        using (var reader = connection.CreateCommand(Read).ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(0, reader.GetInt32(0));
            AreEqual(0, reader.GetInt32(1));
            AreEqual(0, reader.GetInt32(2));
            AreEqual(0, reader.GetInt32(3));
            AreEqual(1, reader.GetInt32(4));
        }
    }
}
