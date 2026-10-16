using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>tempdb</c>'s files as SQL Server 2025's Linux setup lays them out —
/// eight data files in <c>PRIMARY</c> and the log as file 2, 8 MB each and
/// growing by 64 MB — and every surface reporting files agreeing on them
/// (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class TempdbFileLayoutTests
{
    [TestMethod]
    public void DatabaseFiles_ListEightDataFilesAndTheLog()
        => AreEqual(
            "1|0|tempdev|/var/opt/mssql/data/tempdb.mdf|1024|-1|8192,2|1|templog|/var/opt/mssql/data/templog.ldf|1024|-1|8192,"
            + "3|0|tempdev2|/var/opt/mssql/data/tempdb2.ndf|1024|-1|8192,4|0|tempdev3|/var/opt/mssql/data/tempdb3.ndf|1024|-1|8192,"
            + "5|0|tempdev4|/var/opt/mssql/data/tempdb4.ndf|1024|-1|8192,6|0|tempdev5|/var/opt/mssql/data/tempdb5.ndf|1024|-1|8192,"
            + "7|0|tempdev6|/var/opt/mssql/data/tempdb6.ndf|1024|-1|8192,8|0|tempdev7|/var/opt/mssql/data/tempdb7.ndf|1024|-1|8192,"
            + "9|0|tempdev8|/var/opt/mssql/data/tempdb8.ndf|1024|-1|8192",
            new Simulation().ExecuteScalar("""
                select string_agg(concat_ws('|', file_id, type, name, physical_name, size, max_size, growth), ',') within group (order by file_id)
                from tempdb.sys.database_files
                """));

    [TestMethod]
    public void MasterFilesAndSysaltfiles_AgreeWithDatabaseFiles()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            select count(*) from (
                select file_id, name, physical_name, size, max_size, growth from tempdb.sys.database_files
                except select file_id, name, physical_name, size, max_size, growth from sys.master_files where database_id = 2
                union all
                select fileid, name, filename, size, maxsize, growth from sys.sysaltfiles where dbid = 2
                except select file_id, name, physical_name, size, max_size, growth from tempdb.sys.database_files) d
            """));

    [TestMethod]
    public void PrimaryFilegroup_GrowsAllFiles()
        => AreEqual("PRIMARY|1|0", new Simulation().ExecuteScalar(
            "select concat_ws('|', t.name, cast(t.is_autogrow_all_files as int), cast(m.is_autogrow_all_files as int)) from tempdb.sys.filegroups t cross join master.sys.filegroups m"));

    [TestMethod]
    public void SpHelpfile_ReportsTheLogUnlimited()
        => AreEqual("templog|8192 KB|Unlimited|65536 KB", new Simulation().ExecuteBatchesScalar(
            "create table #f (name sysname, fileid smallint, filename nvarchar(260), filegroup sysname null, size nvarchar(18), maxsize nvarchar(18), growth nvarchar(18), usage varchar(9))",
            "insert #f exec tempdb..sp_helpfile",
            "select concat_ws('|', name, size, maxsize, growth) from #f where fileid = 2"));

    [TestMethod]
    public void SpHelpdb_SumsEveryFile()
        => AreEqual("     72.00 MB", new Simulation().ExecuteBatchesScalar(
            "create table #d (name sysname, db_size nvarchar(13), owner sysname null, dbid smallint, created nvarchar(11), status nvarchar(600), compatibility_level tinyint)",
            "insert #d exec sp_helpdb",
            "select db_size from #d where name = 'tempdb'"));

    [TestMethod]
    public void FileSpaceUsage_ReportsEachDataFileWithTempdbCounters()
        => AreEqual("1,3,4,5,6,7,8,9|8|8", new Simulation().ExecuteScalar("""
            use tempdb;
            select concat_ws('|', string_agg(file_id, ',') within group (order by file_id), count(version_store_reserved_page_count), count(user_object_reserved_page_count))
            from sys.dm_db_file_space_usage
            """));

    [TestMethod]
    public void VirtualFileStats_ListsEveryFile()
        => AreEqual(9, new Simulation().ExecuteScalar("select count(*) from sys.dm_io_virtual_file_stats(2, null)"));

    [TestMethod]
    public void PerformanceCounters_SumTheDataFiles()
        => AreEqual(65536L, new Simulation().ExecuteScalar(
            "select cntr_value from sys.dm_os_performance_counters where instance_name = 'tempdb' and counter_name = 'Data File(s) Size (KB)'"));

    [TestMethod]
    public void DbccShowFileStats_ListsTheDataFiles()
        => AreEqual("1:128:tempdev,3:128:tempdev2,4:128:tempdev3,5:128:tempdev4,6:128:tempdev5,7:128:tempdev6,8:128:tempdev7,9:128:tempdev8", new Simulation().ExecuteBatchesScalar(
            "create table #s (fileid int, filegroup int, total bigint, used bigint, name nvarchar(128), filename nvarchar(260))",
            "use tempdb; insert #s exec('dbcc showfilestats')",
            "select string_agg(concat(fileid, ':', total, ':', name), ',') within group (order by fileid) from #s"));

    [TestMethod]
    public void DbccShowFileStats_LogFileReturnsNoRows()
        => AreEqual(0, new Simulation().ExecuteBatchesScalar(
            "create table #s (fileid int, filegroup int, total bigint, used bigint, name nvarchar(128), filename nvarchar(260))",
            "insert #s exec('dbcc showfilestats(2)')",
            "select count(*) from #s"));

    [TestMethod]
    public void DbccShowFileStats_StringArgument_Msg2560()
        => new Simulation().AssertSqlError("dbcc showfilestats('x')", 2560);

    [TestMethod]
    public void SystemDatabases_KeepTheirInstallGrowth()
        => AreEqual("1:1:10:1:-1,1:2:10:1:-1,3:2:8192:0:-1,4:1:10:1:-1,4:2:10:1:268435456", new Simulation().ExecuteScalar("""
            select string_agg(concat_ws(':', database_id, file_id, growth, cast(is_percent_growth as int), max_size), ',') within group (order by database_id, file_id)
            from sys.master_files where (database_id in (1, 4)) or (database_id = 3 and file_id = 2)
            """));
}
