using System.Diagnostics;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>BACKUP DATABASE … TO DISK</c> and <c>RESTORE DATABASE … FROM DISK</c>:
/// what a restore brings back, and the refusals, probed 2026-10-11 against
/// SQL Server 2022 and the common ones re-checked against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BackupRestoreTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>A simulation holding database <c>d</c> with one table of two rows.</summary>
    private static Simulation WithDatabase()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "use d; create table t (id int identity primary key, v nvarchar(50) not null default (N'x')); insert t (v) values (N'a'), (N'b')");
        return simulation;
    }

    [TestMethod]
    public void Restore_BringsBackTheRowsAsBackedUp()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak' with init");
        _ = simulation.ExecuteNonQuery("use d; update t set v = N'changed'; delete t where id = 2; insert t (v) values (N'late')");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual("1:a,2:b", simulation.ExecuteScalar("use d; select string_agg(concat(id, ':', v), ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void Restore_PutsTheIdentityBackWhereTheBackupLeftIt()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak' with init");
        _ = simulation.ExecuteNonQuery("use d; insert t (v) values (N'c'), (N'd')");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual(3, simulation.ExecuteScalar("use d; insert t (v) values (N'e'); select scope_identity()").ToInvariantString() switch { var s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) });
    }

    [TestMethod]
    public void Restore_PositionsSequencesAndRowversion()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d");
        _ = simulation.ExecuteNonQuery("""
            use d;
            create sequence q start with 10;
            create table r (id int, rv rowversion);
            insert r (id) values (1);
            select next value for q;
            """);
        var dbts = simulation.ExecuteScalar("use d; select @@dbts");
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; use d; select next value for q; insert r (id) values (2)");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual(11L, simulation.ExecuteScalar("use d; select convert(bigint, next value for q)"));
        CollectionAssert.AreEqual((byte[])dbts!, (byte[])simulation.ExecuteScalar("use d; select @@dbts")!);
    }

    [TestMethod]
    public void Restore_ToANewName_CreatesTheDatabase_UnderTheSameObjectIds()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("""
            backup database d to disk = N'd.bak';
            restore database d2 from disk = N'd.bak' with move N'd' to N'/var/opt/mssql/data/d2.mdf', move N'd_log' to N'/var/opt/mssql/data/d2_log.ldf';
            """);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d2.dbo.t"));
        AreEqual(simulation.ExecuteScalar("select object_id('d.dbo.t')"), simulation.ExecuteScalar("select object_id('d2.dbo.t')"));
        AreEqual("d|/var/opt/mssql/data/d2.mdf,d_log|/var/opt/mssql/data/d2_log.ldf",
            simulation.ExecuteScalar("select string_agg(concat(name, '|', physical_name), ',') within group (order by file_id) from d2.sys.database_files"));
    }

    [TestMethod]
    public void Restore_AfterTheDatabaseWasDropped_RecreatesIt()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; drop database d");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak'");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
    }

    [TestMethod]
    public void Restore_OverADatabaseWhoseDefinitionsChanged_RebuildsThem()
    {
        var simulation = WithDatabase();
        simulation.ExecuteBatches("use d", "create view vt as select id from t");
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'");
        simulation.ExecuteBatches("use d", "drop view vt", "alter table t add extra int", "create table added (a int)");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.dbo.vt"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.sys.columns where object_id = object_id('d.dbo.t')"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('d.dbo.added')"));
    }

    [TestMethod]
    public void Restore_DropsTheStatisticsQueriesCreatedSince()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; use d; create statistics s_v on t (v)");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from d.sys.stats where name = 's_v'"));
    }

    [TestMethod]
    public void Backup_IsAPointInTimeCopy_LaterWritesDontReachIt()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; use d; insert t (v) values (N'after')");
        _ = simulation.ExecuteNonQuery("restore database d2 from disk = N'd.bak' with move N'd' to N'/d2.mdf', move N'd_log' to N'/d2.ldf'");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d2.dbo.t"));
    }

    [TestMethod]
    public void Backup_WaitsForAnOpenWriteTransaction_AndCopiesOnlyCommittedRows()
    {
        var simulation = WithDatabase();
        using var writer = simulation.CreateOpenConnection();
        _ = writer.CreateCommand("use d; begin tran; insert t (v) values (N'uncommitted')").ExecuteNonQuery();
        var backup = Task.Run(() => simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'"), TestContext.CancellationToken);
        IsFalse(backup.Wait(200, TestContext.CancellationToken));
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
        backup.Wait(TestContext.CancellationToken);
        _ = simulation.ExecuteNonQuery("restore database d2 from disk = N'd.bak' with move N'd' to N'/d2.mdf', move N'd_log' to N'/d2.ldf'");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d2.dbo.t"));
    }

    [TestMethod]
    public void NoInit_AppendsASet_WhichFileSelects_AndInitStartsOver()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak' with init; use d; insert t (v) values (N'c'); use master; backup database d to disk = N'd.bak' with noinit");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with file = 2, replace");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with file = 1, replace");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak' with init");
        _ = simulation.AssertSqlError("restore database d from disk = N'd.bak' with file = 2, replace", 3287);
    }

    [TestMethod]
    public void Restore_BringsBackTheBackedUpRecoveryModelAndUserAccess()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; alter database d set single_user with rollback immediate; alter database d set recovery simple");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual("FULL MULTI_USER ONLINE", simulation.ExecuteScalar("select concat(recovery_model_desc, ' ', user_access_desc, ' ', state_desc) from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void BackupAndRestore_SendTheirProgressAndSummaryMessages()
    {
        var simulation = WithDatabase();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand("backup database d to disk = N'd.bak' with init, stats = 25").ExecuteNonQuery();
        HasCount(7, messages);
        CollectionAssert.AreEqual(new[] { "3211:25 percent processed.", "3211:50 percent processed.", "3211:75 percent processed.", "3211:100 percent processed." }, messages.Take(4).ToArray());
        StartsWith("4035:Processed ", messages[4]);
        EndsWith(" pages for database 'd', file 'd' on file 1.", messages[4]);
        AreEqual("4035:Processed 2 pages for database 'd', file 'd_log' on file 1.", messages[5]);
        MatchesRegex(@"^3014:BACKUP DATABASE successfully processed \d+ pages in \d+\.\d{3} seconds \(\d+\.\d{3} MB/sec\)\.$", messages[6]);

        messages.Clear();
        _ = connection.CreateCommand("restore database d from disk = N'd.bak' with replace").ExecuteNonQuery();
        HasCount(3, messages);
        MatchesRegex(@"^3014:RESTORE DATABASE successfully processed \d+ pages in ", messages[2]);
    }

    [TestMethod]
    public void BareStats_ReportsEveryTenPercent()
    {
        var simulation = WithDatabase();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var percents = new List<string>();
        connection.InfoMessage += (_, e) => percents.AddRange(e.Errors.Where(error => error.Number == 3211).Select(error => error.Message));
        _ = connection.CreateCommand("backup database d to disk = N'd.bak' with stats").ExecuteNonQuery();
        HasCount(10, percents);
    }

    [TestMethod]
    [DataRow("restore database d from disk = N'missing.bak'", 3201, "Cannot open backup device '/var/opt/mssql/data/missing.bak'. Operating system error 2(The system cannot find the file specified.).")]
    [DataRow("backup database tempdb to disk = N'x.bak'", 3147, "Backup and restore operations are not allowed on database tempdb.")]
    [DataRow("restore database tempdb from disk = N'x.bak'", 3147, "Backup and restore operations are not allowed on database tempdb.")]
    [DataRow("restore database master from disk = N'x.bak'", 3108, null)]
    [DataRow("backup database nosuch to disk = N'x.bak'", 911, "Database 'nosuch' does not exist. Make sure that the name is entered correctly.")]
    [DataRow("backup database d to nosuchdevice", 3206, null)]
    [DataRow("backup database d to disk = N''", 3044, "Invalid zero-length device name. Reissue the BACKUP statement with a valid device name.")]
    [DataRow("backup database d to disk = N'x.bak' with stats = 0", 3250, "The value '0' is not within range for the STATS parameter.")]
    [DataRow("restore database d from disk = N'x.bak' with stats = 101", 3250, "The value '101' is not within range for the STATS parameter.")]
    [DataRow("restore database d from disk = N'x.bak' with file = 0", 3250, "The value '0' is not within range for the FILE parameter.")]
    [DataRow("begin tran; backup database d to disk = N'x.bak'", 3021, "Cannot perform a backup or restore operation within a transaction.")]
    [DataRow("use d; restore database d from disk = N'x.bak' with replace", 3102, null)]
    public void Refusal_IsRealsError_FollowedByMsg3013(string sql, int number, string? message)
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'x.bak'");
        var error = simulation.AssertSqlError(sql, number);
        if (message is not null)
            AreEqual(message, error.Errors[0].Message);
        AreEqual(3013, error.Errors[1].Number);
        AreEqual(sql.Contains("backup", StringComparison.Ordinal) ? "BACKUP DATABASE is terminating abnormally." : "RESTORE DATABASE is terminating abnormally.", error.Errors[1].Message);
    }

    [TestMethod]
    [DataRow("backup database d to disk = N'x.bak' with init, noinit", "Option 'init' conflicts with option(s) 'noinit'. Remove the conflicting option and reissue the statement.")]
    [DataRow("backup database d to disk = N'x.bak' with checksum, no_checksum", "Option 'checksum' conflicts with option(s) 'no_checksum'. Remove the conflicting option and reissue the statement.")]
    public void ConflictingOptions_AreMsg3031_Alone(string sql, string message)
    {
        var error = WithDatabase().AssertSqlError(sql, 3031);
        AreEqual(message, error.Errors[0].Message);
        AreEqual(1, error.Errors.Count);
    }

    [TestMethod]
    public void UnknownOption_IsMsg155() =>
        new Simulation().AssertSqlError("restore database d from disk = N'x.bak' with bogusoption", 155, "'bogusoption' is not a recognized RESTORE option.");

    [TestMethod]
    public void InTransaction_LeavesTheTransactionOpen()
    {
        var simulation = WithDatabase();
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; backup database d to disk = N'x.bak'").ExecuteNonQuery());
        AreEqual(1, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Device_TooShortForAMediaHeader_IsAnEmptyVolume()
    {
        var files = new Dictionary<string, byte[]> { ["short.bak"] = new byte[100], ["junk.bak"] = [.. Enumerable.Repeat((byte)0x41, 5000)] };
        var simulation = new Simulation { OpenBackupFile = (path, _) => files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes) : null };
        _ = simulation.ExecuteNonQuery("create database d");
        AreEqual("The volume on device '/var/opt/mssql/data/short.bak' is empty.", simulation.AssertSqlError("restore database d from disk = N'short.bak' with replace", 3254).Errors[0].Message);
        AreEqual("The media family on device '/var/opt/mssql/data/junk.bak' is incorrectly formed. SQL Server cannot process this media family.",
            simulation.AssertSqlError("restore database d from disk = N'junk.bak' with replace", 3241).Errors[0].Message);
    }

    [TestMethod]
    public void OpenBackupFile_ReceivesThePathAsSpelled_ForWritingAndReading()
    {
        var written = new Dictionary<string, MemoryStream>();
        var simulation = new Simulation
        {
            OpenBackupFile = (path, access) => access == FileAccess.Write
                ? written[path] = new MemoryStream()
                : written.TryGetValue(path, out var stream) ? new MemoryStream(stream.ToArray()) : null,
        };
        simulation.ExecuteBatches("create database d", "use d; create table t (a int); insert t values (7)");
        _ = simulation.ExecuteNonQuery(@"backup database d to disk = N'C:\temp\d.bak'");
        IsGreaterThanOrEqualTo(4096, written[@"C:\temp\d.bak"].ToArray().Length);
        _ = simulation.ExecuteNonQuery(@"use d; delete t; use master; restore database d from disk = N'C:\temp\d.bak' with replace");
        AreEqual(7, simulation.ExecuteScalar("select a from d.dbo.t"));
    }

    [TestMethod]
    public void OpenBackupFile_GivingNoStreamToWrite_IsAMissingPath()
    {
        var simulation = new Simulation { OpenBackupFile = (_, _) => null };
        _ = simulation.ExecuteNonQuery("create database d");
        AreEqual(@"Cannot open backup device 'C:\nodir\d.bak'. Operating system error 3(The system cannot find the path specified.).",
            simulation.AssertSqlError(@"backup database d to disk = N'C:\nodir\d.bak'", 3201).Errors[0].Message);
    }

    /// <summary>
    /// The host's file callback refusing with an IO exception reads as the
    /// operating-system error real reports for the same failure, writing or
    /// reading.
    /// </summary>
    [TestMethod]
    [DataRow(typeof(FileNotFoundException), "2(The system cannot find the file specified.)")]
    [DataRow(typeof(DirectoryNotFoundException), "3(The system cannot find the path specified.)")]
    [DataRow(typeof(UnauthorizedAccessException), "5(Access is denied.)")]
    [DataRow(typeof(IOException), "5(Access is denied.)")]
    public void OpenBackupFile_Throwing_IsTheOperatingSystemError(Type exception, string osError)
    {
        var simulation = new Simulation { OpenBackupFile = (_, _) => throw (Exception)Activator.CreateInstance(exception)! };
        _ = simulation.ExecuteNonQuery("create database d");
        AreEqual($"Cannot open backup device '/b/d.bak'. Operating system error {osError}.",
            simulation.AssertSqlError("backup database d to disk = N'/b/d.bak'", 3201).Errors[0].Message);
        AreEqual($"Cannot open backup device '/b/d.bak'. Operating system error {osError}.",
            simulation.AssertSqlError("restore database d from disk = N'/b/d.bak' with replace", 3201).Errors[0].Message);
    }

    [TestMethod]
    public void Restore_WaitsForASessionUsingTheDatabaseToLeave()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; use d; delete t");
        var other = simulation.CreateOpenConnection();
        _ = other.CreateCommand("use d").ExecuteNonQuery();
        var restore = Task.Run(() => simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace"), TestContext.CancellationToken);
        IsFalse(restore.Wait(300, TestContext.CancellationToken));
        other.Dispose();
        restore.Wait(TestContext.CancellationToken);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
    }

    [TestMethod]
    public void SingleUser_SetFromMaster_KeepsOtherSessionsOut_UntilTheSettersRestore()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; use d; delete t");
        using var setter = simulation.CreateOpenConnection();
        _ = setter.CreateCommand("use master; alter database d set single_user with rollback immediate").ExecuteNonQuery();
        AreEqual("Database 'd' is already open and can only have one user at a time.", simulation.AssertSqlError("use d", 924).Errors[0].Message);
        _ = simulation.AssertSqlError("select count(*) from d.dbo.t", 924);
        AreEqual("SINGLE_USER", simulation.ExecuteScalar("select user_access_desc from sys.databases where name = 'd'"));
        _ = setter.CreateCommand("alter database d set recovery simple; restore database d from disk = N'd.bak' with replace").ExecuteNonQuery();
        AreEqual(2, simulation.ExecuteScalar("use d; select count(*) from t"));
    }

    [TestMethod]
    public void SingleUser_AfterTheSetterLeaves_AdmitsTheFirstSessionOnly()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("alter database d set single_user");
        using var first = simulation.CreateOpenConnection();
        _ = first.CreateCommand("use d").ExecuteNonQuery();
        _ = simulation.AssertSqlError("use d", 924);
        first.Close();
        AreEqual(2, simulation.ExecuteScalar("use d; select count(*) from t"));
    }

    [TestMethod]
    public void MultiUser_FromASessionOtherThanTheSingleUser_IsMsg5064()
    {
        var simulation = WithDatabase();
        using var setter = simulation.CreateOpenConnection();
        _ = setter.CreateCommand("alter database d set single_user").ExecuteNonQuery();
        var error = simulation.AssertSqlError("alter database d set multi_user", 5064);
        AreEqual("Changes to the state or options of database 'd' cannot be made at this time. The database is in single-user mode, and a user is currently connected to it.", error.Errors[0].Message);
        AreEqual(5069, error.Errors[1].Number);
        _ = setter.CreateCommand("alter database d set multi_user").ExecuteNonQuery();
        AreEqual(2, simulation.ExecuteScalar("use d; select count(*) from t"));
    }

    [TestMethod]
    public void Restore_WithoutReplace_OverAnotherDatabase_IsMsg3154()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; create database other");
        AreEqual("The backup set holds a backup of a database other than the existing 'other' database.",
            simulation.AssertSqlError("restore database other from disk = N'd.bak'", 3154).Errors[0].Message);
    }

    [TestMethod]
    public void Restore_WithoutReplace_OverItselfInFullRecovery_IsMsg3159_AndInSimpleSucceeds()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'");
        _ = simulation.AssertSqlError("restore database d from disk = N'd.bak'", 3159);
        _ = simulation.ExecuteNonQuery("alter database d set recovery simple; restore database d from disk = N'd.bak'");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
    }

    [TestMethod]
    public void Restore_ToANewName_OverAnotherDatabasesFiles_IsRefusedPerFile()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'");
        var error = simulation.AssertSqlError("restore database d2 from disk = N'd.bak'", 1834);
        CollectionAssert.AreEqual(new[] { 1834, 3156, 1834, 3156, 3119, 3013 }, error.Errors.Select(entry => entry.Number).ToArray());
        AreEqual("The file '/var/opt/mssql/data/d.mdf' cannot be overwritten.  It is being used by database 'd'.", error.Errors[0].Message);
        AreEqual("File 'd' cannot be restored to '/var/opt/mssql/data/d.mdf'. Use WITH MOVE to identify a valid location for the file.", error.Errors[1].Message);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select db_id('d2')"));
    }

    [TestMethod]
    public void Move_NamingNoFileOfTheSet_IsMsg3234() =>
        WithDatabase().AssertSqlError("backup database d to disk = N'd.bak'; restore database d2 from disk = N'd.bak' with move N'nosuch' to N'/x.mdf'", 3234,
            "Logical file 'nosuch' is not part of database 'd2'. Use RESTORE FILELISTONLY to list the logical file names.");

    [TestMethod]
    public void BackupLog_UnderSimpleRecovery_IsMsg4208()
    {
        var error = WithDatabase().AssertSqlError("alter database d set recovery simple; backup log d to disk = N'l.bak'", 4208);
        AreEqual("BACKUP LOG is terminating abnormally.", error.Errors[^1].Message);
    }

    [TestMethod]
    [DataRow("backup database d to disk = N'a.bak', disk = N'b.bak'")]
    [DataRow("backup database d to url = N'https://x/y.bak'")]
    [DataRow("restore rewindonly from disk = N'x.bak'")]
    public void UnmodeledForm_IsNotSupported(string sql) =>
        _ = Throws<NotSupportedException>(() => WithDatabase().ExecuteNonQuery(sql));

    [TestMethod]
    public void Backup_ByALoginWithoutPermission_IsMsg262_AndItsRestoreMsg3110()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("create login l with password = 'Xx!12345678'; create user l for login l; use d; create user l for login l; use master; backup database d to disk = N'd.bak'");
        AreEqual("BACKUP DATABASE permission denied in database 'd'.",
            simulation.AssertSqlError("execute as login = 'l'; backup database d to disk = N'x.bak'", 262).Errors[0].Message);
        AreEqual("User does not have permission to RESTORE database 'd'.",
            simulation.AssertSqlError("execute as login = 'l'; restore database d from disk = N'd.bak' with replace", 3110).Errors[0].Message);
        _ = simulation.ExecuteNonQuery("use d; alter role db_backupoperator add member l; use master; execute as login = 'l'; backup database d to disk = N'x.bak'; revert");
    }

    [TestMethod]
    public void SingleUser_WithRollbackImmediate_EndsTheOtherSessionsUsingTheDatabase()
    {
        var simulation = WithDatabase();
        using var other = simulation.CreateOpenConnection();
        _ = other.CreateCommand("use d; begin tran; insert t (v) values (N'rolled back')").ExecuteNonQuery();
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}:{error.State}"));
        _ = connection.CreateCommand("alter database d set single_user with rollback immediate").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "5060:2", "5060:1" }, messages);
        _ = Throws<SimulatedSqlException>(() => other.CreateCommand("select 1").ExecuteScalar());
        AreEqual(2, connection.CreateCommand("select count(*) from d.dbo.t").ExecuteScalar());
        AreEqual("SINGLE_USER", simulation.ExecuteScalar("select user_access_desc from sys.databases where name = 'd'"));
    }

    [TestMethod]
    public void SingleUser_WithRollbackImmediate_HoldsTheDatabaseInTransitionWhileItSettlesTheEndedSessions()
    {
        var simulation = WithDatabase();
        using var victim = simulation.CreateOpenConnection();
        _ = victim.CreateCommand("use d").ExecuteNonQuery();
        using var setter = simulation.CreateOpenConnection();
        var elapsed = Stopwatch.StartNew();
        var alter = Task.Run(() => setter.CreateCommand("alter database d set single_user with rollback immediate").ExecuteNonQuery(), TestContext.CancellationToken);
        while (simulation.ExecuteScalar("select count(*) from sys.dm_exec_sessions where database_id = db_id('d')") is not 0)
            Thread.Sleep(5);
        AreEqual("Database 'd' is in transition. Try the statement later.", simulation.AssertSqlError("use d", 952).Errors[0].Message);
        _ = simulation.AssertSqlError("select count(*) from d.dbo.t", 952);
        AreEqual("MULTI_USER", simulation.ExecuteScalar("select user_access_desc from sys.databases where name = 'd'"));
        alter.Wait(TestContext.CancellationToken);
        IsGreaterThanOrEqualTo(2900, elapsed.ElapsedMilliseconds);
        AreEqual("SINGLE_USER", simulation.ExecuteScalar("select user_access_desc from sys.databases where name = 'd'"));
    }

    /// <summary>
    /// <c>RESTRICTED_USER</c>'s termination clause ends every session in the
    /// way, a sysadmin's included, and settles as <c>SINGLE_USER</c>'s does
    /// (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void RestrictedUser_WithRollbackImmediate_EndsTheOtherSessionsAndSettles()
    {
        var simulation = WithDatabase();
        using var victim = simulation.CreateOpenConnection();
        _ = victim.CreateCommand("use d").ExecuteNonQuery();
        var elapsed = Stopwatch.StartNew();
        _ = simulation.ExecuteNonQuery("alter database d set restricted_user with rollback immediate");
        IsGreaterThanOrEqualTo(2900, elapsed.ElapsedMilliseconds);
        _ = Throws<SimulatedSqlException>(() => victim.CreateCommand("select 1").ExecuteScalar());
        AreEqual("RESTRICTED_USER", simulation.ExecuteScalar("select user_access_desc from sys.databases where name = 'd'"));
    }

    /// <summary>
    /// With <c>AFTER n</c> the database is in transition from the start, and
    /// the wait runs its full length though its one session leaves on its own
    /// (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SingleUser_WithRollbackAfter_IsInTransitionThroughItsWholeWait()
    {
        var simulation = WithDatabase();
        var victim = simulation.CreateOpenConnection();
        _ = victim.CreateCommand("use d").ExecuteNonQuery();
        using var setter = simulation.CreateOpenConnection();
        // A thread of its own, timed from when the statement starts, so a
        // thread pool starved by the parallel run doesn't eat the bound.
        var elapsed = new Stopwatch();
        var alter = Task.Factory.StartNew(() =>
        {
            elapsed.Start();
            return setter.CreateCommand("alter database d set single_user with rollback after 2 seconds").ExecuteNonQuery();
        }, TestContext.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        while (!InTransition(simulation))
            Thread.Sleep(5);
        IsLessThan(2000, elapsed.ElapsedMilliseconds);
        victim.Dispose();
        IsTrue(InTransition(simulation));
        alter.Wait(TestContext.CancellationToken);
        IsGreaterThanOrEqualTo(4900, elapsed.ElapsedMilliseconds);
        AreEqual("SINGLE_USER", simulation.ExecuteScalar("select user_access_desc from sys.databases where name = 'd'"));
    }

    /// <summary>
    /// A cross-database read the single user's runs left in the plan cache
    /// still meets the slot when another session runs the same text, as its
    /// first run would.
    /// </summary>
    [TestMethod]
    public void SingleUser_RefusesAReadAnotherSessionsPlanWouldReplay()
    {
        var simulation = WithDatabase();
        using var holder = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("use master; alter database d set single_user").ExecuteNonQuery();
        for (var i = 0; i < 3; i++)
            AreEqual(2, holder.CreateCommand("select count(*) from d.dbo.t").ExecuteScalar());
        using var other = simulation.CreateOpenConnection();
        _ = other.CreateCommand("use master").ExecuteNonQuery();
        AreEqual(924, Throws<SimulatedSqlException>(() => other.CreateCommand("select count(*) from d.dbo.t").ExecuteScalar()).Number);
    }

    private static bool InTransition(Simulation simulation)
    {
        try
        {
            _ = simulation.ExecuteScalar("select count(*) from d.sys.objects");
            return false;
        }
        catch (SimulatedSqlException e) when (e.Number == 952)
        {
            return true;
        }
    }

    [TestMethod]
    public void SingleUser_WithRollbackImmediate_EndingNoSession_DoesntSettle()
    {
        var simulation = WithDatabase();
        var elapsed = Stopwatch.StartNew();
        _ = simulation.ExecuteNonQuery("alter database d set single_user with rollback immediate");
        // The settle would take RollbackSettleMillis, three seconds.
        IsLessThan(2500, elapsed.ElapsedMilliseconds);
    }

    [TestMethod]
    public void SingleUser_WithNoWait_WhileAnotherSessionUsesTheDatabase_IsMsg5070()
    {
        var simulation = WithDatabase();
        using var other = simulation.CreateOpenConnection();
        _ = other.CreateCommand("use d").ExecuteNonQuery();
        var error = simulation.AssertSqlError("alter database d set single_user with no_wait", 5070);
        AreEqual(5069, error.Errors[1].Number);
    }

    [TestMethod]
    public void TheRestoreHarnessSequence_RestoresWhileOtherSessionsAreKilled()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'c:\\temp\\d-automated.bak' with noformat, init, name = N'Automated Database Backup', skip, norewind, nounload, stats = 10");
        using var app = simulation.CreateOpenConnection();
        _ = app.CreateCommand("use d; update t set v = N'dirty'").ExecuteNonQuery();
        _ = simulation.ExecuteNonQuery("""
            declare @kill nvarchar(max) = '';
            select @kill = @kill + 'KILL ' + convert(varchar(5), session_id) + ';' from sys.dm_exec_sessions
            where database_id = db_id('d') and session_id <> @@spid and is_user_process = 1;
            exec (@kill);
            """);
        _ = simulation.ExecuteNonQuery("""
            alter database [d] set single_user with rollback immediate;
            alter database [d] set recovery simple;
            restore database [d] from disk = N'c:\temp\d-automated.bak' with file = 1, replace, nounload, stats = 5;
            """);
        AreEqual("ONLINE", simulation.ExecuteScalar("select state_desc from sys.databases where name = 'd'"));
        _ = simulation.ExecuteNonQuery("alter database [d] set multi_user");
        _ = Throws<SimulatedSqlException>(() => app.CreateCommand("select 1").ExecuteScalar());
        AreEqual("a,b", simulation.ExecuteScalar("use d; select string_agg(v, ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void Rebuild_ReproducesTheCatalog()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database src",
            "use src",
            "create schema app",
            "create role reader",
            "create login lu with password = 'Xx!12345678'",
            "create user u for login lu with default_schema = app",
            "alter role reader add member u",
            "create type app.code from nvarchar(10) not null",
            "create xml schema collection app.xs as N'<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"e\" type=\"xsd:int\"/></xsd:schema>'",
            "create type app.rows as table (id int primary key, label nvarchar(20) default (N'none'), check (id > 0), index ix_label unique nonclustered (label desc, id))",
            "create sequence app.numbers as bigint start with 100 increment by 5",
            """
            create table app.parent (
                id int identity(10, 2) not null constraint pk_parent primary key,
                code app.code,
                amount decimal(9, 2) not null constraint df_amount default ((0)),
                doubled as (amount * 2) persisted,
                doc xml (content app.xs),
                guid uniqueidentifier rowguidcol not null default (newid()),
                constraint uq_parent unique nonclustered (code desc),
                constraint ck_amount check (amount >= 0))
            """,
            "create table app.child (id int not null, parent_id int null, note varchar(max) collate Latin1_General_BIN2 sparse null, seq bigint not null default (next value for app.numbers))",
            "alter table app.child add constraint pk_child primary key nonclustered (id)",
            "alter table app.child with nocheck add constraint fk_child_parent foreign key (parent_id) references app.parent (id) on delete set null",
            "alter table app.child nocheck constraint fk_child_parent",
            "create clustered index cx_child on app.child (parent_id, id desc)",
            "create index ix_child_note on app.child (id) include (note) where parent_id is not null with (fillfactor = 80)",
            "create statistics st_seq on app.child (seq) with norecompute",
            "insert app.parent (code, amount) values (N'a', 1.5), (N'b', 2)",
            "insert app.child (id, parent_id, note) values (1, 10, replicate(convert(varchar(max), 'n'), 9000)), (2, null, null)",
            "create view app.parents with schemabinding as select id, code, amount from app.parent",
            "create unique clustered index ux_parents on app.parents (id)",
            "create function app.twice(@x int) returns int with schemabinding as begin return @x * 2 end",
            "create function app.list() returns table as return select id from app.parent",
            "create function app.multi() returns @r table (id int primary key) as begin insert @r select id from app.parent; return end",
            "create procedure app.touch @rows app.rows readonly as select count(*) from @rows",
            "set quoted_identifier off",
            "create procedure noschema as select \"literal\"",
            "set quoted_identifier on",
            "create trigger app.parent_insert on app.parent after insert as set nocount on",
            "disable trigger app.parent_insert on app.parent",
            "create trigger audit on database for create_table as set nocount on",
            "create synonym app.p for app.parent",
            "grant select on schema::app to reader",
            "deny delete on app.parent to u",
            "exec sp_addextendedproperty 'MS_Description', N'the parent', 'schema', 'app', 'table', 'parent', 'column', 'amount'",
            "alter database src set allow_snapshot_isolation on",
            "alter database src set compatibility_level = 140");
        _ = simulation.ExecuteNonQuery("""
            backup database src to disk = N'src.bak';
            restore database dst from disk = N'src.bak' with move N'src' to N'/dst.mdf', move N'src_log' to N'/dst_log.ldf';
            """);
        AreEqual(Catalog(simulation, "src"), Catalog(simulation, "dst"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from dst.app.parents"));
        AreEqual(9000L, simulation.ExecuteScalar("select datalength(note) from dst.app.child where id = 1"));
        AreEqual("literal", simulation.ExecuteScalar("exec dst.dbo.noschema"));
        AreEqual(14, simulation.ExecuteScalar("insert dst.app.parent (code, amount) values (N'c', 3); select max(id) from dst.app.parent"));
        AreEqual(110L, simulation.ExecuteScalar("insert dst.app.child (id) values (3); select seq from dst.app.child where id = 3"));

        // Restored again over itself, in place this time.
        _ = simulation.ExecuteNonQuery("update dst.app.parent set amount = 9; alter table dst.app.child check constraint fk_child_parent; use dst; exec sp_dropextendedproperty 'MS_Description', 'schema', 'app', 'table', 'parent', 'column', 'amount'");
        _ = simulation.ExecuteNonQuery("restore database dst from disk = N'src.bak' with replace, move N'src' to N'/dst.mdf', move N'src_log' to N'/dst_log.ldf'");
        AreEqual(Catalog(simulation, "src"), Catalog(simulation, "dst"));
        AreEqual(3.5m, simulation.ExecuteScalar("select sum(amount) from dst.app.parent"));
    }

    /// <summary>The rows of the catalog views a restore must reproduce, for <paramref name="database"/>.</summary>
    private static string Catalog(Simulation simulation, string database)
    {
        string[] queries =
        [
            "select s.name, o.name, o.object_id, o.type, o.parent_object_id, o.create_date, o.modify_date, o.is_ms_shipped from {0}.sys.objects o join {0}.sys.schemas s on s.schema_id = o.schema_id order by o.object_id",
            "select object_id, name, column_id, system_type_id, user_type_id, max_length, precision, scale, collation_name, is_nullable, is_identity, is_computed, is_sparse, is_rowguidcol, xml_collection_id from {0}.sys.columns order by object_id, column_id",
            "select object_id, name, index_id, type, is_unique, is_primary_key, is_unique_constraint, has_filter, filter_definition, fill_factor, is_disabled from {0}.sys.indexes order by object_id, index_id",
            "select object_id, index_id, index_column_id, column_id, key_ordinal, is_descending_key, is_included_column from {0}.sys.index_columns order by 1, 2, 3",
            "select object_id, definition, uses_ansi_nulls, uses_quoted_identifier, is_schema_bound from {0}.sys.sql_modules order by 1",
            "select name, object_id, parent_column_id, definition, is_system_named from {0}.sys.default_constraints order by object_id",
            "select name, object_id, parent_column_id, definition, is_not_trusted, is_disabled, is_system_named from {0}.sys.check_constraints order by object_id",
            "select name, object_id, is_disabled, is_not_trusted, delete_referential_action, update_referential_action, is_system_named from {0}.sys.foreign_keys order by object_id",
            "select object_id, stats_id, name, auto_created, user_created, no_recompute, has_filter from {0}.sys.stats order by 1, 2",
            "select name, principal_id, type, default_schema_name, sid from {0}.sys.database_principals order by principal_id",
            "select role_principal_id, member_principal_id from {0}.sys.database_role_members order by 1, 2",
            "select class, major_id, minor_id, grantee_principal_id, permission_name, state from {0}.sys.database_permissions order by 1, 2, 3, 4, 5",
            "select class, major_id, minor_id, name, convert(nvarchar(100), value) from {0}.sys.extended_properties order by 1, 2, 3, 4",
            "select name, user_type_id, system_type_id, is_table_type, is_nullable from {0}.sys.types where is_user_defined = 1 order by 2",
            "select name, xml_collection_id from {0}.sys.xml_schema_collections order by 2",
            "select name, schema_id, principal_id from {0}.sys.schemas order by 2",
            "select object_id, last_value, seed_value, increment_value from {0}.sys.identity_columns order by 1",
            "select name, convert(bigint, current_value), convert(bigint, increment) from {0}.sys.sequences",
            "select name, is_disabled, is_instead_of_trigger, parent_class from {0}.sys.triggers order by name",
            "select name, base_object_name from {0}.sys.synonyms",
            "select compatibility_level, snapshot_isolation_state, recovery_model_desc, user_access_desc from sys.databases where name = '{0}'",
        ];
        var text = new StringBuilder();
        using var connection = simulation.CreateOpenConnection();
        foreach (var query in queries)
        {
            using var reader = connection.CreateCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, query, database)).ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    _ = text.Append(Render(reader.GetValue(i))).Append('|');
                _ = text.AppendLine();
            }
            _ = text.AppendLine("--");
        }
        return text.ToString();

        static string Render(object value) => value switch
        {
            byte[] bytes => Convert.ToHexString(bytes),
            DateTime instant => instant.Ticks.ToInvariantString(),
            _ => value.ToInvariantString(),
        };
    }
}
