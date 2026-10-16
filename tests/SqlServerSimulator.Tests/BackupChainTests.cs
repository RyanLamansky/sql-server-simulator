using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Differential and log backups, the differential base and log chain they
/// move, msdb's history of them, and the <c>RESTORE</c> forms that only read
/// a device, probed 2026-10-16 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BackupChainTests
{
    /// <summary>A simulation holding database <c>d</c> in FULL recovery with one table.</summary>
    private static Simulation WithDatabase()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create database d", "alter database d set recovery full; use d; create table t (id int); insert t values (1)");
        return simulation;
    }

    /// <summary>What the catalog says of <c>d</c>'s chain: whether a log backup LSN, a differential base and a log backup time are set.</summary>
    private static string? Chain(Simulation simulation) => simulation.ExecuteScalar("""
        select concat(iif(rs.last_log_backup_lsn is null, 0, 1), iif(mf.differential_base_lsn is null, 0, 1), iif(ls.log_backup_time = '1900-01-01', 0, 1))
        from sys.database_recovery_status rs
        join sys.master_files mf on mf.database_id = rs.database_id and mf.file_id = 1
        cross apply sys.dm_db_log_stats(rs.database_id) ls
        where rs.database_id = db_id('d')
        """) as string;

    [TestMethod]
    public void LogBackup_BeforeAnyFullBackup_IsMsg4214()
    {
        var error = WithDatabase().AssertSqlError("backup log d to disk = N'/b/d.trn'", 4214);
        AreEqual(("BACKUP LOG cannot be performed because there is no current database backup.", "BACKUP LOG is terminating abnormally."), (error.Errors[0].Message, error.Errors[^1].Message));
    }

    [TestMethod]
    public void Differential_BeforeAnyFullBackup_IsMsg3035() =>
        WithDatabase().AssertSqlError("backup database d to disk = N'/b/d.dif' with differential", 3035,
            "Cannot perform a differential backup for database \"d\", because a current database backup does not exist. Perform a full database backup by reissuing BACKUP DATABASE, omitting the WITH DIFFERENTIAL option.");

    [TestMethod]
    public void Differential_OfMaster_IsMsg3024() =>
        new Simulation().AssertSqlError("backup database master to disk = N'/b/m.bak' with differential", 3024,
            "You can only perform a full backup of the master database. Use BACKUP DATABASE to back up the entire master database.");

    [TestMethod]
    public void Chain_MovesAsRealsDoes()
    {
        var simulation = WithDatabase();
        AreEqual("000", Chain(simulation));
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/co.bak' with copy_only");
        AreEqual("000", Chain(simulation));
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/full.bak'");
        AreEqual("111", Chain(simulation));
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/d.dif' with differential; backup log d to disk = N'/b/d.trn'");
        _ = simulation.ExecuteNonQuery("alter database d set recovery simple; alter database d set recovery full");
        AreEqual("010", Chain(simulation));
        _ = simulation.AssertSqlError("backup log d to disk = N'/b/d2.trn'", 4214);
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/d2.dif' with differential; backup log d to disk = N'/b/d3.trn'");
        AreEqual("111", Chain(simulation));
    }

    [TestMethod]
    public void History_RecordsEachSetAsRealDoes()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("""
            backup database d to disk = N'/b/co.bak' with copy_only;
            backup database d to disk = N'/b/full.bak' with checksum, name = N'nightly';
            backup database d to disk = N'/b/d.dif' with differential;
            backup log d to disk = N'/b/d.trn';
            backup log d to disk = N'/b/co.trn' with copy_only;
            backup database d to disk = N'/b/full.bak';
            """);
        AreEqual(
            "D,1,0,0,1536,0,1,0|D,0,0,1,528,0,1,0|I,0,0,0,2560,1,1,0|L,0,1,0,512,0,1,1|L,1,0,0,1536,0,1,1|D,0,0,0,512,0,2,0",
            simulation.ExecuteScalar("""
                select string_agg(concat_ws(',', type, cast(is_copy_only as int), cast(begins_log_chain as int), cast(has_backup_checksums as int), flags,
                    iif(differential_base_lsn is null, 0, 1), position, iif(last_valid_restore_time is null, 0, 1)), '|') within group (order by backup_set_id)
                from msdb.dbo.backupset where database_name = 'd'
                """));
        // The two full backups to one device share its media set, and every set has one.
        AreEqual("1|6", simulation.ExecuteScalar("""
            select concat(
                (select count(distinct bs.media_set_id) from msdb.dbo.backupset bs join msdb.dbo.backupmediafamily f on f.media_set_id = bs.media_set_id where f.physical_device_name = N'/b/full.bak'), '|',
                (select count(*) from msdb.dbo.backupset bs join msdb.dbo.backupmediaset m on m.media_set_id = bs.media_set_id where m.software_name = N'Microsoft SQL Server'))
            """));
        AreEqual("nightly|D:16:131072,L:2:12288", simulation.ExecuteScalar("""
            select concat(max(bs.name), '|', string_agg(concat(bf.file_type, ':', bf.backed_up_page_count, ':', bf.backup_size), ',') within group (order by bf.file_number))
            from msdb.dbo.backupset bs join msdb.dbo.backupfile bf on bf.backup_set_id = bs.backup_set_id where bs.name = N'nightly'
            """));
        AreEqual("PRIMARY|FG|1", simulation.ExecuteScalar("select top 1 concat(name, '|', type, '|', cast(is_default as int)) from msdb.dbo.backupfilegroup"));
    }

    [TestMethod]
    public void LogChain_LinksEachSetToTheLast()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("""
            backup database d to disk = N'/b/full.bak';
            backup database d to disk = N'/b/d.dif' with differential;
            backup log d to disk = N'/b/l1.trn';
            backup log d to disk = N'/b/l2.trn';
            """);
        // A log backup starts where the chain stood: the full backup's first LSN, then the last log backup's last.
        AreEqual("1|1|1|1", simulation.ExecuteScalar("""
            select concat(
                iif((select first_lsn from msdb.dbo.backupset where position = 1 and type = 'L' and backup_set_id = (select min(backup_set_id) from msdb.dbo.backupset where type = 'L'))
                    = (select first_lsn from msdb.dbo.backupset where type = 'D'), 1, 0), '|',
                iif((select first_lsn from msdb.dbo.backupset where backup_set_id = (select max(backup_set_id) from msdb.dbo.backupset where type = 'L'))
                    = (select last_lsn from msdb.dbo.backupset where backup_set_id = (select min(backup_set_id) from msdb.dbo.backupset where type = 'L')), 1, 0), '|',
                iif((select differential_base_lsn from msdb.dbo.backupset where type = 'I') = (select checkpoint_lsn from msdb.dbo.backupset where type = 'D'), 1, 0), '|',
                iif((select last_log_backup_lsn from sys.database_recovery_status where database_id = db_id('d'))
                    = (select last_lsn from msdb.dbo.backupset where backup_set_id = (select max(backup_set_id) from msdb.dbo.backupset)), 1, 0))
            """));
    }

    [TestMethod]
    public void Backups_ReportTheirKindsSummaries()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/full.bak'");
        using var connection = simulation.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand("backup database d to disk = N'/b/d.dif' with differential").ExecuteNonQuery();
        _ = connection.CreateCommand("backup log d to disk = N'/b/d.trn'").ExecuteNonQuery();
        var shapes = messages.ConvertAll(static message => System.Text.RegularExpressions.Regex.Replace(message, "[0-9.]+ (pages|seconds|MB/sec)", "n $1"));
        CollectionAssert.AreEqual(new[]
        {
            "4035:Processed n pages for database 'd', file 'd' on file 1.",
            "4035:Processed n pages for database 'd', file 'd_log' on file 1.",
            "3014:BACKUP DATABASE WITH DIFFERENTIAL successfully processed n pages in n seconds (n MB/sec).",
            "4035:Processed n pages for database 'd', file 'd_log' on file 1.",
            "3014:BACKUP LOG successfully processed n pages in n seconds (n MB/sec).",
        }, shapes);
    }

    [TestMethod]
    public void VerifyOnly_ReadsOneSet()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("""
            backup database d to disk = N'/b/m.bak' with checksum;
            backup database d to disk = N'/b/m.bak' with differential;
            """);
        using var connection = simulation.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand("restore verifyonly from disk = N'/b/m.bak' with checksum").ExecuteNonQuery();
        _ = connection.CreateCommand("restore verifyonly from disk = N'/b/m.bak' with file = 2, stats = 50").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[]
        {
            "3262:The backup set on file 1 is valid.",
            "3211:50 percent processed.",
            "3211:100 percent processed.",
            "3262:The backup set on file 2 is valid.",
        }, messages);
    }

    [TestMethod]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with file = 3", 4038, "Cannot find file ID 3 on device '/b/m.bak'.", "VERIFY DATABASE is terminating abnormally.")]
    [DataRow("restore filelistonly from disk = N'/b/m.bak' with file = 3", 4038, "Cannot find file ID 3 on device '/b/m.bak'.", "RESTORE FILELIST is terminating abnormally.")]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with file = 2, checksum", 3187, "RESTORE WITH CHECKSUM cannot be specified because the backup set does not contain checksum information.", "VERIFY DATABASE is terminating abnormally.")]
    [DataRow("restore labelonly from disk = N'/b/nope.bak'", 3201, "Cannot open backup device '/b/nope.bak'. Operating system error 2(The system cannot find the file specified.).", "RESTORE LABELONLY is terminating abnormally.")]
    [DataRow("begin tran; restore verifyonly from disk = N'/b/m.bak'", 3021, "Cannot perform a backup or restore operation within a transaction.", "VERIFY DATABASE is terminating abnormally.")]
    public void InformationForms_Refusals(string sql, int number, string message, string terminating)
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/m.bak' with checksum; backup database d to disk = N'/b/m.bak' with differential");
        var error = simulation.AssertSqlError(sql, number);
        AreEqual((message, terminating), (error.Errors[0].Message, error.Errors[^1].Message));
    }

    [TestMethod]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with replace", "replace")]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with recovery", "recovery")]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with norecovery", "norecovery")]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with restricted_user", "restricted_user")]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with keep_replication", "keep_replication")]
    [DataRow("restore verifyonly from disk = N'/b/m.bak' with enable_broker", "broker_option")]
    [DataRow("restore headeronly from disk = N'/b/m.bak' with replace", "replace")]
    [DataRow("restore filelistonly from disk = N'/b/m.bak' with stats = 10", "stats, stats=")]
    public void InformationForms_RefuseOptionsThatChangeADatabase(string sql, string options)
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/m.bak'");
        var error = simulation.AssertSqlError(sql, 3032);
        AreEqual((1, (byte)2, $"One or more of the options ({options}) are not supported for this statement. Review the documentation for supported options."),
            (error.Errors.Count, error.State, error.Errors[0].Message));
    }

    [TestMethod]
    public void HeaderOnly_ListsEverySetOrTheOneNamed()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("""
            backup database d to disk = N'/b/m.bak' with name = N'full set', description = N'the full one', checksum;
            backup database d to disk = N'/b/m.bak' with differential;
            backup log d to disk = N'/b/m.bak';
            """);
        AreEqual("full set|the full one|1|1|Database|528|1,|5|2|Database Differential|2560|0,|5|2|Database Differential|2560|0,|2|3|Transaction Log|512|0",
            simulation.ExecuteScalar("""
                create table #h (BackupName nvarchar(128), BackupDescription nvarchar(255), BackupType tinyint, ExpirationDate datetime, Compressed tinyint, Position smallint,
                    DeviceType tinyint, UserName nvarchar(128), ServerName nvarchar(128), DatabaseName nvarchar(128), DatabaseVersion int, DatabaseCreationDate datetime,
                    BackupSize bigint, FirstLSN numeric(25, 0), LastLSN numeric(25, 0), CheckpointLSN numeric(25, 0), DatabaseBackupLSN numeric(25, 0), BackupStartDate datetime,
                    BackupFinishDate datetime, SortOrder smallint, CodePage smallint, UnicodeLocaleId int, UnicodeComparisonStyle int, CompatibilityLevel tinyint,
                    SoftwareVendorId int, SoftwareVersionMajor int, SoftwareVersionMinor int, SoftwareVersionBuild int, MachineName nvarchar(128), Flags int,
                    BindingID uniqueidentifier, RecoveryForkID uniqueidentifier, Collation nvarchar(128), FamilyGUID uniqueidentifier, HasBulkLoggedData bit, IsSnapshot bit,
                    IsReadOnly bit, IsSingleUser bit, HasBackupChecksums bit, IsDamaged bit, BeginsLogChain bit, HasIncompleteMetaData bit, IsForceOffline bit, IsCopyOnly bit,
                    FirstRecoveryForkID uniqueidentifier, ForkPointLSN numeric(25, 0), RecoveryModel nvarchar(60), DifferentialBaseLSN numeric(25, 0),
                    DifferentialBaseGUID uniqueidentifier, BackupTypeDescription nvarchar(128), BackupSetGUID uniqueidentifier, CompressedBackupSize bigint, Containment tinyint,
                    KeyAlgorithm nvarchar(32), EncryptorThumbprint varbinary(20), EncryptorType nvarchar(32), LastValidRestoreTime datetime, TimeZone smallint, CompressionAlgorithm nvarchar(32));
                insert #h exec ('restore headeronly from disk = N''/b/m.bak''');
                insert #h exec ('restore headeronly from disk = N''/b/m.bak'' with file = 2');
                select string_agg(concat_ws('|', BackupName, BackupDescription, BackupType, Position, BackupTypeDescription, Flags, cast(HasBackupChecksums as int)), ',|') within group (order by Position) from #h
                """));
    }

    [TestMethod]
    public void FileListOnly_AndLabelOnly_DescribeTheMedia()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/m.bak' with medianame = N'm1', mediadescription = N'media one'");
        using var connection = simulation.CreateOpenConnection();
        using (var reader = connection.CreateCommand("restore filelistonly from disk = N'/b/m.bak'").ExecuteReader())
        {
            var rows = new List<string>();
            while (reader.Read())
                rows.Add($"{reader["LogicalName"]}|{reader["PhysicalName"]}|{reader["Type"]}|{reader["FileGroupName"]}|{reader["Size"]}|{reader["MaxSize"]}|{reader["FileId"]}|{reader["IsPresent"]}");
            CollectionAssert.AreEqual(new[]
            {
                "d|/var/opt/mssql/data/d.mdf|D|PRIMARY|8388608|35184372080640|1|True",
                "d_log|/var/opt/mssql/data/d_log.ldf|L||8388608|2199023255552|2|True",
            }, rows);
        }
        using (var reader = connection.CreateCommand("restore labelonly from disk = N'/b/m.bak'").ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual("m1|media one|1|1|Microsoft SQL Server|4608|1",
                $"{reader["MediaName"]}|{reader["MediaDescription"]}|{reader["FamilyCount"]}|{reader["MediaSequenceNumber"]}|{reader["SoftwareName"]}|{reader["SoftwareVendorId"]}|{reader["MirrorCount"]}");
        }
    }

    [TestMethod]
    public void Restore_ContinuesTheChainFromItsSet_AndIsRecorded()
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/full.bak'; alter database d set recovery simple; alter database d set recovery full");
        AreEqual("010", Chain(simulation));
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'/b/full.bak' with replace; restore verifyonly from disk = N'/b/full.bak' with loadhistory");
        AreEqual("111", Chain(simulation));
        AreEqual("D:d:1,V:d:0", simulation.ExecuteScalar("select string_agg(concat(restore_type, ':', destination_database_name, ':', cast([replace] as int)), ',') within group (order by restore_history_id) from msdb.dbo.restorehistory"));
        AreEqual("2|PRIMARY", simulation.ExecuteScalar("select concat((select count(*) from msdb.dbo.restorefile), '|', (select max(filegroup_name) from msdb.dbo.restorefilegroup))"));
        AreEqual(0, simulation.ExecuteScalar("backup log d to disk = N'/b/after.trn'; select cast(begins_log_chain as int) from msdb.dbo.backupset where type = 'L'"));
    }

    [TestMethod]
    [DataRow("restore database nosuch from disk = N'/b/d.dif'", "RESTORE DATABASE")]
    [DataRow("restore log nosuch from disk = N'/b/d.dif'", "RESTORE LOG")]
    public void DifferentialOrLogRestore_OfAMissingDatabase_IsMsg3118(string sql, string verb)
    {
        var simulation = WithDatabase();
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'/b/full.bak'; backup database d to disk = N'/b/d.dif' with differential");
        var error = simulation.AssertSqlError(sql, 3118);
        AreEqual(("The database \"nosuch\" does not exist. RESTORE can only create a database when restoring either a full backup or a file backup of the primary file.", $"{verb} is terminating abnormally."),
            (error.Errors[0].Message, error.Errors[^1].Message));
    }

    [TestMethod]
    public void PartitionedDatabase_BacksUpAndRestores()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database p",
            """
            use p;
            create partition function pf (int) as range right for values (10, 20);
            create partition scheme ps as partition pf all to ([PRIMARY]);
            create table t (k int not null constraint pk primary key, v int) on ps (k);
            create index ix on t (v) on ps (k);
            insert t values (1, 1), (15, 2), (25, 3);
            backup database p to disk = N'/b/p.bak';
            use master;
            drop database p;
            restore database p from disk = N'/b/p.bak';
            """);
        AreEqual("pf:3:1|10,20|ix:ps:1,ix:ps:1,ix:ps:1,pk:ps:1,pk:ps:1,pk:ps:1", simulation.ExecuteScalar("""
            use p;
            select concat(
                (select concat(name, ':', fanout, ':', cast(boundary_value_on_right as int)) from sys.partition_functions), '|',
                (select string_agg(cast(value as varchar(10)), ',') within group (order by boundary_id) from sys.partition_range_values), '|',
                (select string_agg(concat(i.name, ':', ds.name, ':', p.rows), ',') within group (order by i.name, p.partition_number)
                 from sys.indexes i join sys.data_spaces ds on ds.data_space_id = i.data_space_id
                 join sys.partitions p on p.object_id = i.object_id and p.index_id = i.index_id where i.object_id = object_id('t')))
            """));
    }
}
