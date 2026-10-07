using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>DATABASE</c> rows of <c>sys.dm_tran_locks</c>: each session's shared
/// lock on the databases it is working in, as SQL Server 2025 lists them
/// (probed 2026-10-07).
/// </summary>
[TestClass]
public class DatabaseLockTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database other; create table other.dbo.t2 (a int)");
        return sim;
    }

    private static object? Scalar(DbConnection connection, string sql) => connection.CreateCommand(sql).ExecuteScalar();

    private const string Mine = "select string_agg(db_name(resource_database_id), ',') within group (order by db_name(resource_database_id)) from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id = @@spid";

    [TestMethod]
    public void TheCurrentDatabase_IsHeldShared_SaveMasterAndTempdb()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        AreEqual(
            "S|GRANT|0|512|0|",
            Scalar(connection, "select concat(request_mode, '|', request_status, '|', len(resource_description), '|', datalength(resource_description), '|', resource_associated_entity_id, '|', resource_subtype) from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id = @@spid"));
        AreEqual("simulated", Scalar(connection, Mine));
        AreEqual("other", Scalar(connection, "use other; " + Mine));
        AreEqual("msdb", Scalar(connection, "use msdb; " + Mine));
        AreEqual(DBNull.Value, Scalar(connection, "use master; " + Mine));
        AreEqual(DBNull.Value, Scalar(connection, "use tempdb; " + Mine));
    }

    [TestMethod]
    public void EveryOtherSessionsRow_IsListed()
    {
        var sim = Seeded();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        _ = second.CreateCommand("use other").ExecuteNonQuery();
        var spid = (short)Scalar(second, "select @@spid")!;
        AreEqual("other", Scalar(first, $"select db_name(resource_database_id) from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id = {spid}"));
    }

    [TestMethod]
    public void ANestedContext_HoldsItsDatabaseBesideTheEnclosingOnes()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("exec ('use other; exec (''create procedure p as " + Mine.Replace("'", "''''", StringComparison.Ordinal) + "'')')");
        using var connection = sim.CreateOpenConnection();
        AreEqual("other,simulated", Scalar(connection, $"exec ('use other; {Mine.Replace("'", "''", StringComparison.Ordinal)}')"));
        AreEqual("msdb,other,simulated", Scalar(connection, $"exec ('use other; exec (''use msdb; {Mine.Replace("'", "''''", StringComparison.Ordinal)}'')')"));
        AreEqual("other,simulated", Scalar(connection, "exec other.dbo.p"));
        AreEqual("simulated", Scalar(connection, Mine));
    }

    [TestMethod]
    public void ADatabaseAStatementReads_IsHeldForTheStatement_OrTheTransaction()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        AreEqual("other,simulated", Scalar(connection, $"select (select string_agg(db_name(resource_database_id), ',') within group (order by db_name(resource_database_id)) from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id = @@spid) from other.dbo.t2 right join (select 1 x) x on 1 = 1"));
        AreEqual(0, Scalar(connection, "select count(*) from other.dbo.t2"));
        AreEqual("simulated", Scalar(connection, Mine));
        AreEqual(0, Scalar(connection, "begin tran; select count(*) from other.dbo.t2"));
        AreEqual("other,simulated", Scalar(connection, Mine));
        AreEqual("simulated", Scalar(connection, "rollback; " + Mine));
    }

    [TestMethod]
    public void AClosedSession_HoldsNothing()
    {
        var sim = Seeded();
        var closed = sim.CreateOpenConnection();
        var spid = (short)Scalar(closed, "select @@spid")!;
        closed.Close();
        using var observer = sim.CreateOpenConnection();
        AreEqual(0, Scalar(observer, $"select count(*) from sys.dm_tran_locks where request_session_id = {spid}"));
    }
}
