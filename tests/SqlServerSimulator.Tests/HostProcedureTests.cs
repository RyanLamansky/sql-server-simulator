using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The host and maintenance procedures DBA scripts call — <c>xp_fixeddrives</c>,
/// <c>xp_loginconfig</c>, <c>xp_servicecontrol</c>, <c>sp_srvrolepermission</c>,
/// <c>sp_sqlexec</c>, <c>sp_flush_log</c>, <c>sp_resetstatus</c> and the
/// <c>sp_clean_db_*free_space</c> pair — each asserted shape probed against
/// SQL Server 2025 (2026-10-16).
/// </summary>
[TestClass]
public sealed class HostProcedureTests
{
    private static List<string> Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToInvariantString())));
        return rows;
    }

    private static List<string> Messages(Simulation sim, string sql)
    {
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}|{error.Class}|{error.LineNumber}|{error.Procedure}|{error.Message}"));
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        while (reader.NextResult())
        {
        }
        return messages;
    }

    [TestMethod]
    public void XpFixedDrives_ListsTheOneDriveForASysadmin()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new[] { "C|900000" }, Rows(sim, "exec xp_fixeddrives"));
        CollectionAssert.AreEqual(new[] { "C|900000" }, Rows(sim, "exec xp_fixeddrives 0"));
        IsEmpty(Rows(sim, "exec xp_fixeddrives 2"));
        _ = sim.ExecuteNonQuery("create login lo with password = 'Pa55word!1', check_policy = off");
        IsEmpty(Rows(sim, "use master; execute as login = 'lo'; exec xp_fixeddrives; revert"));
    }

    [TestMethod]
    [DataRow("exec @rc = xp_fixeddrives 'x'", 22005, 12, 0, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_fixeddrives 0, 2", 22005, 15, 0, "Error executing extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_servicecontrol 'querystate'", 22003, 15, 0, "Error executing xp_servicecontrol extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_servicecontrol 'bogus', 'nope'", 22003, 15, 0, "Error executing xp_servicecontrol: Invalid config change command")]
    [DataRow("exec @rc = xp_servicecontrol 'querystate', 'MSSQLSERVER'", 22003, 16, 1, "OpenService() returned error 1060, 'The specified service does not exist as an installed service.'")]
    [DataRow("exec @rc = xp_servicecontrol 'start', 'SQLServerAgent'", 22003, 16, 1, "StartService() returned error 1053, 'The service did not respond to the start or control request in a timely fashion.'")]
    [DataRow("exec @rc = xp_servicecontrol 'pause', 'sqlserveragent'", 22003, 16, 1, "StartService() returned error 1062, 'The service has not been started.'")]
    public void ExtendedProcedureRefusals_NeitherCatchNorSetAtAtError(string call, int number, int severity, int state, string message)
    {
        var simulation = new Simulation();
        var error = simulation.AssertSqlError($"""
            create table r (e int, rc int);
            declare @rc int;
            begin try
                {call};
                insert r values (@@error, @rc);
            end try
            begin catch
                insert r values (-1, -1);
            end catch
            """, number).Errors[0];
        AreEqual(((byte)severity, (byte)state, 0, message), (error.Class, error.State, error.LineNumber, error.Message));
        AreEqual("0|1", simulation.ExecuteScalar("select concat(e, '|', rc) from r"));
    }

    [TestMethod]
    public void XpServiceControl_ReportsTheAgentStopped()
        => CollectionAssert.AreEqual(new[] { "Stopped." }, Rows(new Simulation(), "exec xp_servicecontrol 'QUERYSTATE', N'sqlserveragent'"));

    [TestMethod]
    public void XpLoginConfig_ReportsMixedAuthentication()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new[]
        {
            "login mode|Mixed", "default login|guest", "default domain|NULL", "audit level|failure",
            "set hostname|false", "map _|domain separator", "map $|NULL", "map #|-",
        }, Rows(sim, "exec xp_loginconfig"));
        CollectionAssert.AreEqual(new[] { "login mode|Mixed" }, Rows(sim, "exec master..xp_loginconfig 'LOGIN MODE'"));
        CollectionAssert.AreEqual(new[] { "0|0|0||Error executing extended stored procedure: Invalid parameter value." }, Messages(sim, "exec xp_loginconfig 'bogus'"));
        AreEqual(1, sim.ExecuteScalar("declare @rc int; exec @rc = xp_loginconfig 1; select @rc"));
        _ = sim.ExecuteNonQuery("create login lo with password = 'Pa55word!1', check_policy = off");
        _ = sim.AssertSqlError("use master; execute as login = 'lo'; exec xp_loginconfig", 229);
    }

    [TestMethod]
    public void SpSrvRolePermission_ListsEachFixedServerRolesPermissions()
    {
        var sim = new Simulation();
        HasCount(203, Rows(sim, "exec sp_srvrolepermission"));
        CollectionAssert.AreEqual(new[] { "processadmin|Add member to processadmin", "processadmin|KILL" }, Rows(sim, "exec sp_srvrolepermission 'PROCESSADMIN'"));
        var error = sim.AssertSqlError("exec sp_srvrolepermission 'public'", 15412).Errors[0];
        AreEqual(("sp_srvrolepermission", 10), (error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void SpSqlExec_RunsItsTextAndReturnsZero()
    {
        var sim = new Simulation();
        AreEqual(7, sim.ExecuteScalar("exec sp_sqlexec 'select 7'"));
        _ = sim.AssertSqlError("exec sp_sqlexec", 201);
        AreEqual(0, sim.ExecuteScalar("declare @rc int = 5; exec @rc = sp_sqlexec 'declare @x int'; select @rc"));
    }

    [TestMethod]
    public void SpFlushLog_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("declare @rc int; exec @rc = sp_flush_log 'anything'; select @rc"));

    [TestMethod]
    public void SpResetStatus_FindsNoSuspectFlag()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new[] { "15056|0|59|sp_resetstatus|The suspect flag on the database \"master\" is already reset." }, Messages(sim, "exec sp_resetstatus 'master'"));
        AreEqual(1, sim.ExecuteScalar("declare @rc int; exec @rc = sp_resetstatus 'master'; select @rc"));
        var error = sim.AssertSqlError("exec sp_resetstatus 'nope'", 15010).Errors[0];
        AreEqual(35, error.LineNumber);
    }

    [TestMethod]
    public void SpCleanDbFreeSpace_ClosesADbccPerDataFile()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database d; alter database d add file (name = d2, filename = '/var/opt/mssql/data/d2.ndf')");
        var completed = "2528|0|69|sp_clean_db_file_free_space|DBCC execution completed. If DBCC printed error messages, contact your system administrator.";
        CollectionAssert.AreEqual(new[] { completed, completed }, Messages(sim, "exec sp_clean_db_free_space 'd'"));
        CollectionAssert.AreEqual(new[] { completed }, Messages(sim, "exec sp_clean_db_file_free_space 'd', 99"));
        AreEqual("The database '[nope]' does not exist. Supply a valid database name. To see available databases, use sys.databases.",
            sim.AssertSqlError("exec sp_clean_db_free_space 'nope'", 15010).Errors[0].Message);
        _ = sim.AssertSqlError("exec sp_clean_db_file_free_space 'd'", 201);
        // Without CONTROL SERVER the cleaning's own batch is refused, and the
        // caller's goes on (probed 2026-10-20).
        _ = sim.ExecuteNonQuery("create login l with password = 'Zz!12345678x', check_policy = off; use d; create user l for login l");
        sim.AssertSqlError("use d; execute as login = 'l'; exec sp_clean_db_free_space 'd'; revert; create table after_refusal (a int)", 14260,
            "You do not have sufficient permission to run this command. Contact your system administrator.");
        AreEqual(1, sim.ExecuteScalar("select count(*) from d.sys.tables where name = 'after_refusal'"));
    }
}
