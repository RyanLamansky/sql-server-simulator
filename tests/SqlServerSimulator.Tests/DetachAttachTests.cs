using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Detaching a database to its files and attaching it from them:
/// <c>sp_detach_db</c>, <c>DBCC DETACHDB</c>, <c>CREATE DATABASE … FOR
/// ATTACH</c> / <c>FOR ATTACH_REBUILD_LOG</c>, <c>sp_attach_db</c> and
/// <c>sp_attach_single_file_db</c>. Every expectation probed 2026-10-16
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DetachAttachTests
{
    private const string Data = "/var/opt/mssql/data/";

    private static Simulation WithDatabase(string name = "d")
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches($"create database {name}", $"use {name}; create table t (id int primary key, v nvarchar(20)); insert t values (1, N'a'), (2, N'b')");
        return simulation;
    }

    /// <summary>The cells of every row the command returns, NULL as "NULL", rows joined by " / ".</summary>
    private static string Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }
        return string.Join(" / ", rows);
    }

    private static SimulatedDbConnection Open(Simulation simulation)
    {
        var connection = simulation.CreateDbConnection();
        connection.Open();
        return connection;
    }

    /// <summary>The return code the call answers, read past whatever errors it raises on the way.</summary>
    private static int? ReturnCode(Simulation simulation, string exec)
    {
        using var connection = Open(simulation);
        using var command = connection.CreateCommand($"select 0 opened; declare @r int = -5; exec @r = {exec}; select @r r");
        using var reader = command.ExecuteReader();
        for (var advanced = true; advanced;)
        {
            try
            {
                advanced = reader.NextResult();
                if (advanced && reader.GetName(0) == "r" && reader.Read())
                    return reader.GetInt32(0);
            }
            catch (SimulatedSqlException)
            {
            }
        }
        return null;
    }

    [TestMethod]
    public void Detach_LeavesTheFiles_AndAttachBringsTheDatabaseBack()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("alter database d set compatibility_level = 140; alter database d set recovery simple; alter database d set trustworthy on; alter database d set db_chaining on");
        AreEqual(0, ReturnCode(simulation, "sp_detach_db 'd'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
        AreEqual("1|0|1 / 1|0|1", Rows(simulation, $"select * from sys.dm_os_file_exists('{Data}d.mdf') union all select * from sys.dm_os_file_exists('{Data}d_log.ldf')"));

        _ = simulation.ExecuteNonQuery($"create database d2 on (filename = '{Data}d.mdf') for attach");
        AreEqual("ONLINE|140|SIMPLE|False|False|False|MULTI_USER|sa", Rows(simulation,
            "select state_desc, compatibility_level, recovery_model_desc, is_trustworthy_on, is_db_chaining_on, is_broker_enabled, user_access_desc, suser_sname(owner_sid) from sys.databases where name = 'd2'"));
        AreEqual($"1|d|{Data}d.mdf / 2|d_log|{Data}d_log.ldf", Rows(simulation, "select file_id, name, physical_name from sys.master_files where database_id = db_id('d2') order by file_id"));
        AreEqual("1|a / 2|b", Rows(simulation, "select id, v from d2.dbo.t order by id"));

        // A drop deletes an attached database's files.
        _ = simulation.ExecuteNonQuery("drop database d2");
        AreEqual(0, simulation.ExecuteScalar($"select cast(file_exists as int) from sys.dm_os_file_exists('{Data}d.mdf')"));
    }

    [TestMethod]
    public void Attach_TakesTheSmallestFreeDatabaseId()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("create database e");
        var id = simulation.ExecuteScalar("select db_id('d')");
        _ = simulation.ExecuteNonQuery("exec sp_detach_db 'd'");
        _ = simulation.ExecuteNonQuery($"create database d on (filename = '{Data}d.mdf') for attach");
        AreEqual(id, simulation.ExecuteScalar("select db_id('d')"));
    }

    [TestMethod]
    [DataRow("sp_detach_db 'master'", 7940, "System databases master, model, msdb, and tempdb cannot be detached.", "", 1, 0)]
    [DataRow("sp_detach_db 'nosuch'", 15010, "The database 'nosuch' does not exist. Supply a valid database name. To see available databases, use sys.databases.", "sp_detach_db", 40, 1)]
    public void DetachDb_Refusals(string exec, int number, string message, string procedure, int line, int returnCode)
    {
        var simulation = WithDatabase();
        var error = simulation.AssertSqlError("exec " + exec, number).Errors[0];
        AreEqual((message, procedure, line), (error.Message, error.Procedure, error.LineNumber));
        AreEqual(returnCode, ReturnCode(simulation, exec));
    }

    [TestMethod]
    [DataRow("sp_detach_db null", 17)]
    [DataRow("sp_detach_db 'd', 'maybe'", 25)]
    [DataRow("sp_detach_db 'd', 'true', 'maybe'", 33)]
    public void DetachDb_AnArgumentItCantRead_IsTheUsageMessage(string exec, int line)
    {
        var simulation = WithDatabase();
        using var connection = Open(simulation);
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add($"{error.Number}@{error.Procedure}:{error.LineNumber} {error.Message}");
        };
        AreEqual(1, connection.CreateCommand($"declare @r int; exec @r = {exec}; select @r").ExecuteScalar());
        AreEqual($"15354@sp_detach_db:{line} Usage: sp_detach_db <dbname>, [TRUE|FALSE], [TRUE|FALSE]", string.Join(" | ", messages));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void DetachDb_InATransaction_IsMsg15002()
    {
        var simulation = WithDatabase();
        var error = simulation.AssertSqlError("begin tran; exec sp_detach_db 'd'", 15002).Errors[0];
        AreEqual(("sp_detach_db", 47), (error.Procedure, error.LineNumber));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void DetachingTheSessionsOwnDatabase_IsMsg3703_AndEndsTheBatch()
    {
        var simulation = WithDatabase();
        using var connection = Open(simulation);
        _ = connection.CreateCommand("use d").ExecuteNonQuery();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_detach_db 'd'; select 1").ExecuteScalar());
        AreEqual((3703, "Cannot detach the database 'd' because it is currently in use."), (error.Number, error.Errors[0].Message));
        AreEqual("d", connection.CreateCommand("select db_name()").ExecuteScalar());
    }

    [TestMethod]
    public void AnotherSessionUsingTheDatabase_DoesntHoldTheDetachBack()
    {
        var simulation = WithDatabase();
        using var other = Open(simulation);
        _ = other.CreateCommand("use d").ExecuteNonQuery();
        AreEqual(0, ReturnCode(simulation, "sp_detach_db 'd'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
        AreEqual(2, other.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void SkipChecksFalse_UpdatesStatisticsFirst()
    {
        var simulation = WithDatabase();
        using var connection = Open(simulation);
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add($"{error.Number}@{error.Procedure}");
        };
        _ = connection.CreateCommand("exec sp_detach_db 'd', 'false'").ExecuteNonQuery();
        AreEqual("15650@sys.sp_updatestats", messages[0]);
        AreEqual("15005@sys.sp_updatestats", messages[^1]);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    [DataRow("dbcc detachdb('nosuch')", 7939, "Cannot detach database 'nosuch' because it does not exist.")]
    [DataRow("use master; dbcc detachdb('')", 7940, "System databases master, model, msdb, and tempdb cannot be detached.")]
    [DataRow("dbcc detachdb(null)", 2560, "Parameter 1 is incorrect for this DBCC statement.")]
    [DataRow("dbcc detachdb('d', 'x')", 2560, "Parameter 2 is incorrect for this DBCC statement.")]
    [DataRow("dbcc detachdb()", 2583, "An incorrect number of parameters was given to the DBCC statement.")]
    [DataRow("dbcc detachdb('d', 1, 2)", 2583, "An incorrect number of parameters was given to the DBCC statement.")]
    public void DbccDetachDb_Refusals(string sql, int number, string message)
        => WithDatabase().AssertSqlError(sql, number, message);

    [TestMethod]
    public void DbccDetachDb_DetachesSilently()
    {
        var simulation = WithDatabase();
        using var connection = Open(simulation);
        var messages = 0;
        connection.InfoMessage += (_, _) => messages++;
        _ = connection.CreateCommand("dbcc detachdb(d, 1)").ExecuteNonQuery();
        AreEqual(0, messages);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void DbccDetachDb_OutsideDbOwner_IsMsg7983()
    {
        var simulation = WithDatabase();
        simulation.ExecuteBatches(
            "use master; create login l with password = 'Zz!passw0rd1', check_policy = off; grant create any database to l",
            "use d; create user l for login l");
        AreEqual("User 'l' does not have permission to run DBCC detachdb for database 'd'.",
            simulation.AssertSqlError("use master; execute as login = 'l'; dbcc detachdb('d')", 7983).Errors[0].Message);
    }

    [TestMethod]
    [DataRow($"create database x on (filename = '{Data}missing.mdf') for attach", 5120,
        $"Unable to open the physical file \"{Data}missing.mdf\". Operating system error 2: \"2(The system cannot find the file specified.)\".")]
    [DataRow("create database x on (filename = '/var/opt/mssql/data') for attach", 5120,
        "Unable to open the physical file \"/var/opt/mssql/data\". Operating system error 5: \"5(Access is denied.)\".")]
    [DataRow($"create database x on (filename = '{Data}e.mdf') for attach", 5120,
        $"Unable to open the physical file \"{Data}e.mdf\". Operating system error 32: \"32(The process cannot access the file because it is being used by another process.)\".")]
    [DataRow($"create database x on (filename = '{Data}d_log.ldf') for attach", 1829, "The FOR ATTACH option requires that at least the primary file be specified.")]
    [DataRow("create database x on (filename = 'd.mdf') for attach", 5105,
        "A file activation error occurred. The physical file name 'd.mdf' may be incorrect. Diagnose and correct additional errors, and retry the operation.")]
    [DataRow($"create database e on (filename = '{Data}d.mdf') for attach", 1801, "Database 'e' already exists. Choose a different database name.")]
    [DataRow($"create database x on (filename = '{Data}d.mdf'), (filename = '{Data}f_log.ldf') for attach", 5173,
        "One or more files do not match the primary file of the database. If you are attempting to attach a database, retry the operation with the correct files.  If this is an existing database, the file may be corrupted and should be restored from a backup.")]
    [DataRow($"create database x on (filename = '{Data}d.mdf'), (filename = '{Data}d.mdf') for attach", 1830,
        $"The files '{Data}d.mdf' and '{Data}d.mdf' are both primary files. A database can only have one primary file.")]
    [DataRow($"create database x on (filename = '{Data}d.mdf') log on (filename = '{Data}nolog.ldf') for attach", 5120,
        $"Unable to open the physical file \"{Data}nolog.ldf\". Operating system error 2: \"2(The system cannot find the file specified.)\".")]
    [DataRow($"create database x on (filename = '{Data}x.bak') for attach", 5172,
        $"The header for file '{Data}x.bak' is not a valid database file header. The FILE SIZE property is incorrect.")]
    [DataRow($"create database x containment = none on (filename = '{Data}d.mdf') for attach", 1841,
        "CREATE DATABASE failed. CONTAINMENT clause cannot be used with the FOR ATTACH option.")]
    public void Attach_Refusals(string sql, int number, string message)
    {
        var simulation = WithDatabase();
        simulation.ExecuteBatches("create database e", "create database f", "backup database e to disk = 'x.bak'", "exec sp_detach_db 'd'", "exec sp_detach_db 'f'");
        var error = simulation.AssertSqlError(sql, number);
        AreEqual(message, error.Errors[0].Message);
        if (number is 5120 or 5172 or 5173 or 1830)
            AreEqual(1802, error.Errors[^1].Number);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'x'"));
    }

    [TestMethod]
    [DataRow("for attach with bogus", "Incorrect syntax near 'bogus'.")]
    [DataRow("for attach with enable_broker, new_broker", "Incorrect syntax near 'new_broker'.")]
    [DataRow("for attach_rebuild_log with enable_broker", "Incorrect syntax near 'enable_broker'.")]
    [DataRow("for attach_rebuild_log with restricted_user", "Incorrect syntax near 'restricted_user'.")]
    public void AttachOptions_ThatDontBelong_AreSyntaxErrors(string clause, string message)
        => WithDatabase().AssertSqlError($"create database x on (filename = '{Data}d.mdf') {clause}", 102, message);

    [TestMethod]
    public void AttachOptions_SetTheBrokerAndAccess()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("exec sp_detach_db 'd'");
        _ = simulation.ExecuteNonQuery($"create database d on (filename = '{Data}d.mdf') for attach with restricted_user, enable_broker");
        AreEqual("RESTRICTED_USER|True", Rows(simulation, "select user_access_desc, is_broker_enabled from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void Attach_OwnerIsTheAttachingLogin()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("use master; create login l with password = 'Zz!passw0rd1', check_policy = off; grant create any database to l; exec sp_detach_db 'd'");
        _ = simulation.ExecuteNonQuery($"use master; execute as login = 'l'; create database d on (filename = '{Data}d.mdf') for attach; revert");
        AreEqual("l", simulation.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void Attach_RebuildsAMissingLog()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var simulation = new Simulation
        {
            OpenBackupFile = (path, access) => access == FileAccess.Write
                ? new WriteBack(files, path)
                : files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : null,
        };
        simulation.ExecuteBatches("create database d", "use d; create table t (a int); insert t values (7)", "use master; exec sp_detach_db 'd'");
        IsTrue(files.Remove(Data + "d_log.ldf"));

        using var connection = Open(simulation);
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add($"{error.Number}/{error.State} {error.Message}");
        };
        AreEqual(0, connection.CreateCommand($"declare @r int; exec @r = sp_attach_single_file_db 'd', '{Data}d.mdf'; select @r").ExecuteScalar());
        AreEqual(
            $"5111/5 File activation failure. The physical file name \"{Data}d_log.ldf\" may be incorrect. | 5182/1 New log file '{Data}d_log.ldf' was created.",
            string.Join(" | ", messages));
        AreEqual($"2|{Data}d_log.ldf|63|-1|10|True", Rows(simulation, "select file_id, physical_name, size, max_size, growth, is_percent_growth from sys.master_files where database_id = db_id('d') and type = 1"));
        AreEqual(7, simulation.ExecuteScalar("select a from d.dbo.t"));
        Contains($"New log file '{Data}d_log.ldf' was created.", string.Join("\n", Rows(simulation, "exec sp_readerrorlog 0, 1, N'New log file'")));
    }

    [TestMethod]
    public void SpAttachDb_AttachesTheFilesItLists()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("exec sp_detach_db 'd'");
        AreEqual(0, ReturnCode(simulation, $"sp_attach_db 'e', '{Data}d.mdf', '{Data}d_log.ldf'"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from e.dbo.t"));
    }

    [TestMethod]
    [DataRow("sp_attach_db null, 'x'", 15004, "sp_attach_db", 27, 1)]
    [DataRow("sp_attach_single_file_db 'x', null", 15004, "sp_attach_single_file_db", 17, 1)]
    [DataRow($"sp_attach_db 'x', '{Data}nope.mdf'", 5120, "", 1, 1)]
    [DataRow($"sp_attach_single_file_db 'e', '{Data}nope.mdf'", 1801, "", 1, 1)]
    public void SpAttachDb_Refusals(string exec, int number, string procedure, int line, int returnCode)
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("create database e");
        var error = simulation.AssertSqlError("exec " + exec, number).Errors[0];
        AreEqual((procedure, line), (error.Procedure, error.LineNumber));
        AreEqual(returnCode, ReturnCode(simulation, exec));
    }

    [TestMethod]
    public void DroppingAnOfflineDatabase_LeavesItsFilesAttachable()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("alter database d set offline; drop database d");
        AreEqual(1, simulation.ExecuteScalar($"select cast(file_exists as int) from sys.dm_os_file_exists('{Data}d.mdf')"));
        simulation.AssertSqlError("create database d", 5170, $"Cannot create file '{Data}d.mdf' because it already exists. Change the file path or the file name, and retry the operation.");
        _ = simulation.ExecuteNonQuery($"create database d on (filename = '{Data}d.mdf') for attach");
        AreEqual("ONLINE|2", Rows(simulation, "select state_desc, (select count(*) from d.dbo.t) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void DetachingARestoringDatabase_IsMsg947_AndLeavesFilesNoAttachTakes()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = 'd.bak'; restore database d from disk = 'd.bak' with replace, norecovery");
        AreEqual("Error while closing database 'd'. Check for previous additional errors and retry the operation.",
            simulation.AssertSqlError("exec sp_detach_db 'd', 'true'", 947).Errors[0].Message);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'd'"));
        simulation.AssertSqlError($"create database d on (filename = '{Data}d.mdf') for attach", 1824, "Cannot attach a database that was being restored.");
    }

    [TestMethod]
    public void ADetachedDatabasesFile_IsNoBackupToDelete()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("exec sp_detach_db 'd'");
        AreEqual(1, ReturnCode(simulation, $"xp_delete_file 0, '{Data}d_log.ldf'"));
        AreEqual(1, simulation.ExecuteScalar($"select cast(file_exists as int) from sys.dm_os_file_exists('{Data}d_log.ldf')"));
    }

    [TestMethod]
    public void CreateDatabase_AFileWithoutAName_IsMsg1036_WhileTheBatchCompiles()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError($"create table t (a int); create database x on (filename = '{Data}x.mdf')", 1036);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void TheErrorLog_RecordsAnAttachsFailures()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("exec sp_detach_db 'd'");
        _ = simulation.AssertSqlError($"create database x on (filename = '{Data}missing.mdf') for attach", 5120);
        var log = Rows(simulation, "exec sp_readerrorlog 0, 1, N'FCB::Open'");
        Contains($"FCB::Open failed: Could not open file {Data}missing.mdf for file number 0.  OS error: 2(The system cannot find the file specified.).", log);
        Contains("Error: 17204, Severity: 16, State: 1.", Rows(simulation, "exec sp_readerrorlog 0, 1, N'Error: 17204'"));
    }

    /// <summary>A stream that hands what was written to the host's dictionary as it closes.</summary>
    private sealed class WriteBack(Dictionary<string, byte[]> files, string path) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            files[path] = this.ToArray();
            base.Dispose(disposing);
        }
    }
}
