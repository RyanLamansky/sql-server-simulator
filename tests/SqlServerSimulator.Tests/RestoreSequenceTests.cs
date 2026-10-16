using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Restore sequences: <c>NORECOVERY</c> and <c>STANDBY</c>, differential and
/// log sets applied to a restoring database, the recovery that ends the
/// sequence, point-in-time stops and marks, and tail-log backups, probed
/// 2026-10-16 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class RestoreSequenceTests
{
    /// <summary>
    /// A simulation holding <c>d</c> in FULL recovery, its table <c>t</c>
    /// holding 1, then backed up full, 2, log 1, 3, differential, 4, log 2, 5,
    /// log 3.
    /// </summary>
    private static Simulation WithChain()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "alter database d set recovery full",
            """
            create table d.dbo.t (id int primary key, v varchar(20));
            insert d.dbo.t values (1, 'one');
            backup database d to disk = N'/b/full.bak';
            insert d.dbo.t values (2, 'two');
            backup log d to disk = N'/b/log1.trn';
            insert d.dbo.t values (3, 'three');
            backup database d to disk = N'/b/diff.bak' with differential;
            insert d.dbo.t values (4, 'four');
            backup log d to disk = N'/b/log2.trn';
            insert d.dbo.t values (5, 'five');
            backup log d to disk = N'/b/log3.trn';
            """);
        return simulation;
    }

    private static string? Rows(Simulation simulation, string database = "d") =>
        simulation.ExecuteScalar($"select string_agg(cast(id as varchar(10)), ',') within group (order by id) from {database}.dbo.t") as string;

    private static string? State(Simulation simulation, string database = "d") =>
        simulation.ExecuteScalar($"select concat(state_desc, ':', cast(is_read_only as int), cast(is_in_standby as int)) from sys.databases where name = '{database}'") as string;

    private static List<string> Messages(Simulation simulation, string sql)
    {
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void FullDifferentialAndLogs_RestoreInSequence_ToANewDatabase()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d2 from disk = N'/b/full.bak' with norecovery, move N'd' to N'/x/d2.mdf', move N'd_log' to N'/x/d2.ldf'");
        AreEqual("RESTORING:00", State(simulation, "d2"));
        _ = simulation.AssertSqlError("select * from d2.dbo.t", 927);
        _ = simulation.ExecuteNonQuery("restore log d2 from disk = N'/b/log1.trn' with norecovery; restore database d2 from disk = N'/b/diff.bak' with norecovery; restore log d2 from disk = N'/b/log2.trn' with norecovery");
        AreEqual("RESTORING:00", State(simulation, "d2"));
        _ = simulation.ExecuteNonQuery("restore log d2 from disk = N'/b/log3.trn' with recovery");
        AreEqual("ONLINE:00", State(simulation, "d2"));
        AreEqual("1,2,3,4,5", Rows(simulation, "d2"));
        AreEqual("D0L0I0L0L1", simulation.ExecuteScalar("select string_agg(concat(restore_type, cast(recovery as int)), '') within group (order by restore_history_id) from msdb.dbo.restorehistory where destination_database_name = 'd2'"));
    }

    [TestMethod]
    public void LogRestore_ReportsNoDataPagesThenItsLogPages()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace");
        var messages = Messages(simulation, "restore log d from disk = N'/b/log1.trn'");
        HasCount(3, messages);
        AreEqual("4035:Processed 0 pages for database 'd', file 'd' on file 1.", messages[0]);
        MatchesRegex(@"^4035:Processed \d+ pages for database 'd', file 'd_log' on file 1\.$", messages[1]);
        MatchesRegex(@"^3014:RESTORE LOG successfully processed \d+ pages in ", messages[2]);
        AreEqual("1,2", Rows(simulation));
    }

    [TestMethod]
    public void LogOutOfSequence_IsMsg4305OrMsg4326()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace");
        var tooRecent = simulation.AssertSqlError("restore log d from disk = N'/b/log2.trn' with norecovery", 4305);
        MatchesRegex(@"^The log in this backup set begins at LSN \d+, which is too recent to apply to the database\. An earlier log backup that includes LSN \d+ can be restored\.$", tooRecent.Errors[0].Message);
        AreEqual("RESTORE LOG is terminating abnormally.", tooRecent.Errors[^1].Message);
        // The last log restored applies again, to no effect.
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log1.trn' with norecovery; restore log d from disk = N'/b/log1.trn' with norecovery");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/diff.bak' with norecovery");
        var tooEarly = simulation.AssertSqlError("restore log d from disk = N'/b/log1.trn' with norecovery", 4326);
        MatchesRegex(@"^The log in this backup set terminates at LSN \d+, which is too early to apply to the database\. A more recent log backup that includes LSN \d+ can be restored\.$", tooEarly.Errors[0].Message);
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log2.trn'");
        AreEqual("1,2,3,4", Rows(simulation));
    }

    [TestMethod]
    public void DifferentialOrLog_OntoADatabaseNoSequenceHolds_IsMsg3117()
    {
        var simulation = WithChain();
        var error = simulation.AssertSqlError("restore database d from disk = N'/b/diff.bak'", 3117);
        AreEqual(("The log or differential backup cannot be restored because no files are ready to rollforward.", "RESTORE DATABASE is terminating abnormally."), (error.Errors[0].Message, error.Errors[^1].Message));
        _ = simulation.AssertSqlError("restore log d from disk = N'/b/log1.trn'", 3117);
    }

    [TestMethod]
    public void Differential_OutOfSequence_IsMsg3136()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/full2.bak'; backup database d to disk = N'/b/diff2.bak' with differential");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace");
        var otherBase = simulation.AssertSqlError("restore database d from disk = N'/b/diff2.bak' with norecovery", 3136);
        AreEqual(1, otherBase.Errors[0].State);
        AreEqual("This differential backup cannot be restored because the database has not been restored to the correct earlier state.", otherBase.Errors[0].Message);
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log1.trn' with norecovery; restore log d from disk = N'/b/log2.trn' with norecovery");
        AreEqual(3, simulation.AssertSqlError("restore database d from disk = N'/b/diff.bak' with norecovery", 3136).Errors[0].State);
    }

    [TestMethod]
    public void SetOfAnotherDatabase_IsMsg3154()
    {
        var simulation = WithChain();
        simulation.ExecuteBatches("create database e", "backup database e to disk = N'/b/e.bak'; backup database e to disk = N'/b/e.dif' with differential; backup log e to disk = N'/b/e.trn'");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace");
        foreach (var sql in (string[])["restore database d from disk = N'/b/e.dif' with norecovery", "restore log d from disk = N'/b/e.trn' with norecovery", "restore database d from disk = N'/b/e.bak' with norecovery"])
            AreEqual(4, simulation.AssertSqlError(sql, 3154).Errors[0].State);
    }

    [TestMethod]
    public void RecoveryAlone_RecoversARestoringDatabase()
    {
        var simulation = WithChain();
        _ = simulation.AssertSqlError("restore database d with recovery", 3153);
        _ = simulation.AssertSqlError("restore database nosuch with recovery", 3118);
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace; restore log d from disk = N'/b/log1.trn' with norecovery");
        var messages = Messages(simulation, "restore log d with recovery");
        HasCount(1, messages);
        MatchesRegex(@"^3014:RESTORE LOG successfully processed 0 pages in \d+\.\d{3} seconds \(0\.000 MB/sec\)\.$", messages[0]);
        AreEqual("ONLINE:00", State(simulation));
        AreEqual("1,2", Rows(simulation));
        _ = simulation.ExecuteNonQuery("insert d.dbo.t values (9, 'nine'); backup log d to disk = N'/b/after.trn'");
    }

    [TestMethod]
    public void RestoringDatabase_RefusesBackupAndAlterDatabase()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace");
        var backup = simulation.AssertSqlError("backup database d to disk = N'/b/x.bak'", 927);
        AreEqual((2, "BACKUP DATABASE is terminating abnormally."), (backup.Errors[0].State, backup.Errors[^1].Message));
        AreEqual(4, simulation.AssertSqlError("backup log d to disk = N'/b/x.trn' with no_truncate", 927).Errors[0].State);
        var alter = simulation.AssertSqlError("alter database d set read_only", 5052);
        AreEqual(("ALTER DATABASE is not permitted while a database is in the Restoring state.", 5069), (alter.Errors[0].Message, alter.Errors[^1].Number));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select last_log_backup_lsn from sys.database_recovery_status where database_id = db_id('d')"));
        _ = simulation.ExecuteNonQuery("drop database d");
    }

    [TestMethod]
    public void StopAt_LandsBetweenTransactions()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("""
            insert d.dbo.t values (6, 'six');
            waitfor delay '00:00:00.050';
            declare @between datetime2 = sysdatetime();
            waitfor delay '00:00:00.050';
            insert d.dbo.t values (7, 'seven');
            backup log d to disk = N'/b/log4.trn';
            restore database d from disk = N'/b/full.bak' with norecovery, replace;
            restore database d from disk = N'/b/diff.bak' with norecovery;
            restore log d from disk = N'/b/log2.trn' with norecovery;
            restore log d from disk = N'/b/log3.trn' with norecovery;
            restore log d from disk = N'/b/log4.trn' with stopat = @between;
            """);
        AreEqual("1,2,3,4,5,6", Rows(simulation));
        AreEqual("ONLINE:00", State(simulation));
        AreEqual("L1", simulation.ExecuteScalar("select concat(restore_type, cast(recovery as int)) from msdb.dbo.restorehistory where stop_at is not null"));
    }

    [TestMethod]
    public void StopAt_WithNoRecovery_ContinuesToALaterStopInTheSameLog()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("""
            insert d.dbo.t values (6, 'six');
            waitfor delay '00:00:00.050';
            declare @first datetime2 = sysdatetime();
            waitfor delay '00:00:00.050';
            insert d.dbo.t values (7, 'seven');
            waitfor delay '00:00:00.050';
            declare @second datetime2 = sysdatetime();
            waitfor delay '00:00:00.050';
            insert d.dbo.t values (8, 'eight');
            backup log d to disk = N'/b/log4.trn';
            restore database d from disk = N'/b/full.bak' with norecovery, replace;
            restore log d from disk = N'/b/log1.trn' with norecovery;
            restore log d from disk = N'/b/log2.trn' with norecovery;
            restore log d from disk = N'/b/log3.trn' with norecovery;
            restore log d from disk = N'/b/log4.trn' with norecovery, stopat = @first;
            restore log d from disk = N'/b/log4.trn' with norecovery, stopat = @second;
            restore database d with recovery;
            """);
        AreEqual("1,2,3,4,5,6,7", Rows(simulation));
        AreEqual(1, simulation.ExecuteScalar("""
            select iif(first_recovery_fork_guid is not null and first_recovery_fork_guid <> recovery_fork_guid and family_guid = first_recovery_fork_guid and fork_point_lsn is not null, 1, 0)
            from sys.database_recovery_status where database_id = db_id('d')
            """));
    }

    [TestMethod]
    public void StopAt_PastTheLog_LeavesTheDatabaseRestoring()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace");
        var messages = Messages(simulation, "restore log d from disk = N'/b/log1.trn' with stopat = '2999-01-01'");
        AreEqual("4322:This backup set contains records that were logged before the designated point in time. The database is being left in the restoring state so that more roll forward can be performed.", messages[^2]);
        AreEqual("RESTORING:00", State(simulation));
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log2.trn' with stopat = '2999-01-01'; restore database d with recovery");
        AreEqual("1,2,3,4", Rows(simulation));
    }

    [TestMethod]
    public void StopAt_OnAFullRestore_LeavesItRestoringOrIsTooEarly()
    {
        var simulation = WithChain();
        var tooEarly = simulation.AssertSqlError("restore database d from disk = N'/b/full.bak' with replace, stopat = '2000-01-01'", 4338);
        AreEqual("The STOPAT clause specifies a point too early to allow this backup set to be restored. Choose a different stop point or use RESTORE DATABASE WITH RECOVERY to recover at the current point.", tooEarly.Errors[0].Message);
        AreEqual("ONLINE:00", State(simulation));
        var past = simulation.AssertSqlError("restore database d from disk = N'/b/full.bak' with replace, stopat = '2999-01-01'", 3013);
        AreEqual("3013,4035,4035,4322:2", string.Join(",", past.Errors.Select(error => error.Number)) + ":" + past.Errors[^1].State);
        AreEqual("RESTORING:00", State(simulation));
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log1.trn' with stopat = '2999-01-01'");
    }

    [TestMethod]
    public void StopAt_OverTheDatabaseWithoutReplace_NeedsTheLogBackupsToReachIt()
    {
        var simulation = WithChain();
        var error = simulation.AssertSqlError("restore database d from disk = N'/b/full.bak' with stopat = '2999-01-01'", 3173);
        StartsWith("The STOPAT clause provided with this RESTORE statement indicates that the tail of the log contains changes that must be backed up to reach the target point in time. The tail of the log for the database \"d\" has not been backed up.", error.Errors[0].Message);
        _ = simulation.AssertSqlError("restore database d from disk = N'/b/full.bak'", 3159);
        // A point the log backups reach overwrites the log, with NORECOVERY quietly.
        var messages = Messages(simulation, "declare @at datetime2 = sysdatetime(); waitfor delay '00:00:00.020'; backup log d to disk = N'/b/log4.trn'; restore database d from disk = N'/b/full.bak' with stopat = @at, norecovery");
        MatchesRegex("^3014:RESTORE DATABASE successfully processed", messages[^1]);
        AreEqual("RESTORING:00", State(simulation));
    }

    [TestMethod]
    public void StopAt_BeforeWhereTheDatabaseStandsInsideTheLog_IsMsg4335()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/full2.bak'; insert d.dbo.t values (6, 'six'); backup log d to disk = N'/b/log4.trn'");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full2.bak' with norecovery, replace");
        var error = simulation.AssertSqlError("restore log d from disk = N'/b/log4.trn' with stopat = '2000-01-01'", 4335);
        AreEqual("The specified STOPAT time is too early. All or part of the database is already rolled forward beyond that point.", error.Errors[0].Message);
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log4.trn'");
        AreEqual("1,2,3,4,5,6", Rows(simulation));
    }

    [TestMethod]
    [DataRow("'garbage'", 1)]
    [DataRow("''", 2)]
    [DataRow("@missing", 2)]
    public void StopAt_ThatIsNoTime_IsMsg3217(string value, int state)
    {
        var error = WithChain().AssertSqlError($"declare @missing datetime; restore log d from disk = N'/b/log1.trn' with stopat = {value}", 3217);
        AreEqual(("Invalid value specified for STOPAT parameter.", state), (error.Errors[0].Message, error.Errors[0].State));
    }

    [TestMethod]
    public void StopAt_ANumber_IsMsg206() =>
        WithChain().AssertSqlError("restore log d from disk = N'/b/log1.trn' with stopat = 12", 206, "Operand type clash: int is incompatible with datetime");

    [TestMethod]
    [DataRow("recovery, norecovery", "norecovery", "recovery")]
    [DataRow("norecovery, standby = N'/b/u.dat'", "norecovery", "standby")]
    [DataRow("standby = N'/b/u.dat', recovery", "recovery", "standby")]
    [DataRow("stopatmark = 'a', stopbeforemark = 'b'", "stopatmark", "stopbeforemark, stopbeforemark")]
    [DataRow("stopbeforemark = 'b', stopat = '2026-01-01'", "stopat", "stopbeforemark")]
    public void ConflictingOptions_AreMsg3031(string with, string option, string conflicting) =>
        WithChain().AssertSqlError($"restore database d from disk = N'/b/full.bak' with {with}", 3031,
            $"Option '{option}' conflicts with option(s) '{conflicting}'. Remove the conflicting option and reissue the statement.");

    [TestMethod]
    public void Marks_StopAtOrBeforeTheMarkedTransaction()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("""
            insert d.dbo.t values (6, 'six');
            begin tran m1 with mark 'the first';
            insert d.dbo.t values (7, 'seven');
            commit;
            begin tran rolled with mark;
            insert d.dbo.t values (8, 'eight');
            rollback;
            insert d.dbo.t values (9, 'nine');
            backup log d to disk = N'/b/log4.trn';
            """);
        AreEqual("d|m1|the first|sa", simulation.ExecuteScalar("select string_agg(concat(database_name, '|', mark_name, '|', description, '|', user_name), ',') from msdb.dbo.logmarkhistory"));
        const string sequence = """
            restore database d from disk = N'/b/diff.bak' with norecovery, replace;
            restore log d from disk = N'/b/log2.trn' with norecovery;
            restore log d from disk = N'/b/log3.trn' with norecovery;
            """;
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace;" + sequence + "restore log d from disk = N'/b/log4.trn' with stopatmark = 'm1'");
        AreEqual("1,2,3,4,5,6,7", Rows(simulation));
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace;" + sequence + "restore log d from disk = N'/b/log4.trn' with stopbeforemark = 'm1'");
        AreEqual("1,2,3,4,5,6", Rows(simulation));
        AreEqual("m1:0,m1:1", simulation.ExecuteScalar("select string_agg(concat(stop_at_mark_name, ':', cast(stop_before as int), iif(stop_at is null, '?', '')), ',') within group (order by restore_history_id) from msdb.dbo.restorehistory where stop_at_mark_name is not null"));

        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace;" + sequence);
        var messages = Messages(simulation, "restore log d from disk = N'/b/log4.trn' with stopatmark = 'm1' after '2999-01-01'");
        AreEqual("4329:This log file contains records logged before the designated mark. The database is being left in the Restoring state so you can apply another log file.", messages[^2]);
        AreEqual("RESTORING:00", State(simulation));
    }

    [TestMethod]
    public void TailLogBackup_LeavesTheDatabaseRestoringForItsOwnSequence()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("insert d.dbo.t values (6, 'six'); backup log d to disk = N'/b/tail.trn' with norecovery");
        AreEqual("RESTORING:00", State(simulation));
        AreEqual("768:1", simulation.ExecuteScalar("select top (1) concat(flags, ':', cast(is_force_offline as int)) from msdb.dbo.backupset order by backup_set_id desc"));
        _ = simulation.ExecuteNonQuery("""
            restore database d from disk = N'/b/full.bak' with norecovery;
            restore database d from disk = N'/b/diff.bak' with norecovery;
            restore log d from disk = N'/b/log2.trn' with norecovery;
            restore log d from disk = N'/b/log3.trn' with norecovery;
            restore log d from disk = N'/b/tail.trn';
            """);
        AreEqual("1,2,3,4,5,6", Rows(simulation));
    }

    [TestMethod]
    public void NoTruncate_OfAnOnlineDatabase_BacksUpAsCopyOnly()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("backup log d to disk = N'/b/nt.trn' with no_truncate; backup log d to disk = N'/b/nt2.trn'");
        AreEqual("1536:1,512:0", simulation.ExecuteScalar("select string_agg(concat(flags, ':', cast(is_copy_only as int)), ',') within group (order by backup_set_id) from (select top (2) * from msdb.dbo.backupset order by backup_set_id desc) b"));
        AreEqual(1, simulation.ExecuteScalar("select count(distinct first_lsn) from (select top (2) first_lsn from msdb.dbo.backupset order by backup_set_id desc) b"));
    }

    [TestMethod]
    public void Standby_OpensReadOnlyBetweenRestores()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with standby = N'/b/undo.dat', replace");
        AreEqual("ONLINE:11", State(simulation));
        AreEqual("1|1|1", simulation.ExecuteScalar("select concat(cast(databasepropertyex('d', 'IsInStandBy') as int), '|', (select file_exists from sys.dm_os_file_exists('/b/undo.dat')), '|', (select count(*) from d.dbo.t))"));
        _ = simulation.AssertSqlError("insert d.dbo.t values (99, 'x')", 3906);
        _ = simulation.ExecuteNonQuery("restore log d from disk = N'/b/log1.trn' with standby = N'/b/undo.dat'");
        AreEqual("1,2", Rows(simulation));
        _ = simulation.ExecuteNonQuery("restore database d with recovery");
        AreEqual("ONLINE:00", State(simulation));
        AreEqual((byte)0, simulation.ExecuteScalar("select file_exists from sys.dm_os_file_exists('/b/undo.dat')"));
        _ = simulation.ExecuteNonQuery("insert d.dbo.t values (99, 'x')");
    }

    [TestMethod]
    public void StopAt_AcrossADefinitionChange_ReplaysBackFromTheLogsEnd()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("""
            create table d.dbo.u (k int identity primary key, v int);
            insert d.dbo.u (v) values (10), (20);
            waitfor delay '00:00:00.050';
            declare @between datetime2 = sysdatetime();
            waitfor delay '00:00:00.050';
            update d.dbo.u set v = 21 where k = 2;
            delete d.dbo.t where id = 1;
            truncate table d.dbo.t;
            backup log d to disk = N'/b/log4.trn';
            restore database d from disk = N'/b/full.bak' with norecovery, replace;
            restore log d from disk = N'/b/log1.trn' with norecovery;
            restore log d from disk = N'/b/log2.trn' with norecovery;
            restore log d from disk = N'/b/log3.trn' with norecovery;
            restore log d from disk = N'/b/log4.trn' with stopat = @between;
            """);
        AreEqual("1,2,3,4,5", Rows(simulation));
        AreEqual("1:10,2:20|3", simulation.ExecuteScalar("select concat((select string_agg(concat(k, ':', v), ',') within group (order by k) from d.dbo.u), '|', ident_current('d.dbo.u') + 1)"));
    }

    [TestMethod]
    public void StopAt_BeforeADefinitionChange_ReplaysForward()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("""
            update d.dbo.t set v = 'uno' where id = 1;
            delete d.dbo.t where id = 5;
            waitfor delay '00:00:00.050';
            declare @between datetime2 = sysdatetime();
            waitfor delay '00:00:00.050';
            create table d.dbo.u (k int);
            insert d.dbo.t values (6, 'six');
            backup log d to disk = N'/b/log4.trn';
            restore database d from disk = N'/b/full.bak' with norecovery, replace;
            restore database d from disk = N'/b/diff.bak' with norecovery;
            restore log d from disk = N'/b/log2.trn' with norecovery;
            restore log d from disk = N'/b/log3.trn' with norecovery;
            restore log d from disk = N'/b/log4.trn' with stopat = @between;
            """);
        AreEqual("1,2,3,4", Rows(simulation));
        AreEqual("uno|0", simulation.ExecuteScalar("select concat((select v from d.dbo.t where id = 1), '|', count(*)) from d.sys.tables where name = 'u'"));
    }

    [TestMethod]
    public void RestoreSequence_WritesRealsErrorLogLines()
    {
        var simulation = WithChain();
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with norecovery, replace; restore database d from disk = N'/b/diff.bak' with norecovery; restore log d from disk = N'/b/log2.trn' with norecovery; restore database d with recovery");
        var lines = simulation.ExecuteScalar("""
            create table #l (LogDate datetime, ProcessInfo nvarchar(100), Text nvarchar(4000));
            insert #l exec sp_readerrorlog 0;
            select string_agg(left(Text, 30), '|') from #l where Text like 'Database changes were restored%' or Text like 'Log was restored%' or Text like 'Restore is complete%' or Text like 'RESTORE DATABASE successfully processed 0%'
            """) as string;
        AreEqual("Database changes were restored|Log was restored. Database: d,|Restore is complete on databas|RESTORE DATABASE successfully ", lines);
    }
}
