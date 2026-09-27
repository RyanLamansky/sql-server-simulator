using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A database's files and filegroups: <c>CREATE DATABASE</c>'s file list,
/// <c>ALTER DATABASE … ADD | MODIFY | REMOVE FILE</c> and <c>MODIFY
/// FILEGROUP</c>, as <c>sys.database_files</c>, <c>sys.master_files</c>,
/// <c>sys.filegroups</c>, <c>FILEPROPERTY</c>, <c>FILEGROUPPROPERTY</c> and
/// <c>sp_helpfile</c> report them. Probed 2026-09-27 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DatabaseFileTests
{
    private const string Path = "/var/opt/mssql/data/";

    private static string Files(string where = "1 = 1") =>
        $"select string_agg(concat(file_id, ':', name, ':', data_space_id, ':', size, ':', max_size, ':', growth, ':', is_percent_growth), ' ') within group (order by file_id) from sys.database_files where {where}";

    [TestMethod]
    public void Fresh_TwoFiles()
        => AreEqual("1:simulated:1:1024:-1:8192:0 2:simulated_log:0:1024:268435456:8192:0", new Simulation().ExecuteScalar(Files()));

    // A 1 MB growth first, since a ceiling under the file's growth is refused.
    [TestMethod]
    [DataRow("size = 16MB", "1:simulated:1:2048:-1:128:0")]
    [DataRow("size = 21000KB", "1:simulated:1:2632:-1:128:0")]
    [DataRow("size = 20", "1:simulated:1:2560:-1:128:0")]
    [DataRow("size = 16 mb", "1:simulated:1:2048:-1:128:0")]
    [DataRow("maxsize = 100MB, filegrowth = 10%", "1:simulated:1:1024:12800:10:1")]
    [DataRow("filegrowth = 63KB", "1:simulated:1:1024:-1:8:0")]
    [DataRow("filegrowth = 70KB", "1:simulated:1:1024:-1:16:0")]
    [DataRow("filegrowth = 0", "1:simulated:1:1024:-1:0:0")]
    [DataRow("size = 50MB, maxsize = 40MB", "1:simulated:1:6400:6400:128:0")]
    [DataRow("newname = data1, size = 9MB", "1:data1:1:1152:-1:128:0")]
    public void ModifyFile_DataFile(string options, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            alter database current modify file (name = simulated, filegrowth = 1MB);
            alter database current modify file (name = simulated, {options});
            {Files("file_id = 1")}
            """));

    [TestMethod]
    public void ModifyFile_LogFile_UnlimitedReadsAsTheCap()
        => AreEqual("2:simulated_log:0:1152:268435456:128:0", new Simulation().ExecuteScalar($"""
            alter database current modify file (name = simulated_log, size = 9MB, maxsize = 100MB);
            alter database current modify file (name = 'simulated_log', maxsize = unlimited, filegrowth = 1MB);
            {Files("file_id = 2")}
            """));

    // Statement-level refusals: the batch carries on.
    [TestMethod]
    [DataRow("modify file (name = simulated, size = 8MB)", 5039, 1)]
    [DataRow("modify file (name = simulated, size = 1)", 5039, 1)]
    [DataRow("modify file (name = simulated, size = 0)", 5039, 1)]
    [DataRow("modify file (name = nosuch, size = 20MB)", 5041, 1)]
    [DataRow("modify file (name = simulated)", 5038, 1)]
    [DataRow("modify file (name = simulated, maxsize = 4MB)", 5040, 1)]
    [DataRow("modify file (name = simulated, size = 60MB, maxsize = 4MB)", 5040, 1)]
    [DataRow("modify file (name = simulated, maxsize = 10MB)", 5169, 3)]
    [DataRow("modify file (name = simulated, maxsize = 100MB, filegrowth = 200MB)", 5169, 1)]
    [DataRow("modify file (name = simulated, newname = simulated_log)", 1828, 3)]
    [DataRow("modify file (name = simulated, newname = SIMULATED)", 1828, 3)]
    [DataRow("modify file (name = simulated, filename = '/var/opt/mssql/data/simulated_log.ldf')", 12106, 1)]
    [DataRow("modify file (name = simulated, offline)", 5077, 2)]
    [DataRow("add file (name = simulated_log, filename = '/var/opt/mssql/data/x.ndf')", 1828, 4)]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/x.ndf'), (name = X, filename = '/var/opt/mssql/data/y.ndf')", 1828, 9)]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/x.ndf') to filegroup nosuch", 1921, 4)]
    [DataRow("add filegroup g; alter database current add log file (name = x, filename = '/var/opt/mssql/data/x.ldf') to filegroup g", 5087, 1)]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/x.ndf', size = 100KB)", 5174, 1)]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/x.ndf', size = 2MB, maxsize = 1MB)", 5103, 1)]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/x.ndf', size = 1MB, maxsize = 2MB, filegrowth = 3MB)", 5169, 2)]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/simulated.mdf')", 5009, 1)]
    [DataRow("remove file simulated", 5020, 1)]
    [DataRow("remove file simulated_log", 5020, 1)]
    [DataRow("remove file nosuch", 5009, 9)]
    [DataRow("modify filegroup nosuch default", 5014, 2)]
    [DataRow("modify filegroup [primary] read_only", 5047, 1)]
    [DataRow("modify filegroup [primary] name = p2", 5012, 1)]
    [DataRow("modify filegroup [primary] default", 5045, 3)]
    [DataRow("modify filegroup [primary] autogrow_single_file", 5045, 4)]
    public void StatementRefusals(string clause, int number, int state)
    {
        var sim = new Simulation();
        var ex = sim.AssertSqlError($"alter database current {clause}; create table t (a int)", number);
        AreEqual((byte)state, ex.State);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void AddFile_RefusalIsFollowedByMsg5009()
    {
        var ex = new Simulation().AssertSqlError($"alter database current add file (name = x, filename = '{Path}x.ndf', size = 100KB)", 5174);
        AreEqual((5009, (byte)8), (ex.Errors[1].Number, ex.Errors[1].State));
        ex = new Simulation().AssertSqlError($"alter database current add file (name = x, filename = '{Path}simulated.mdf')", 5009);
        AreEqual(5170, ex.Errors[1].Number);
    }

    // Compile-time refusals: nothing in the batch runs.
    [TestMethod]
    [DataRow("modify file (size = 10MB)", 1036, 16, 3)]
    [DataRow("add file (name = x)", 1036, 15, 1)]
    [DataRow("add file (filename = '/var/opt/mssql/data/x.ndf')", 1036, 16, 3)]
    [DataRow("modify file (name = x, size = 10MB, size = 11MB)", 153, 15, 1)]
    [DataRow("modify file (name = x, size = 10PB)", 153, 15, 1)]
    [DataRow("modify file (name = x, bogus = 10)", 153, 15, 1)]
    [DataRow("modify file (name = x, maxsize = 10%)", 153, 15, 1)]
    [DataRow("modify file (name = x, maxsize = 16TB)", 1842, 16, 1)]
    [DataRow("modify file (name = x, size = 4.5)", 102, 15, 1)]
    [DataRow("modify file (name = x, size = 99999999999)", 102, 15, 1)]
    [DataRow("modify file (name = x, size = 1MB), (name = y, size = 1MB)", 102, 15, 1)]
    [DataRow("modify filegroup [primary] bogus", 102, 15, 6)]
    public void CompileTimeRefusals(string clause, int number, int @class, int state)
    {
        var sim = new Simulation();
        var ex = sim.AssertSqlError($"create table t (a int); alter database current {clause}", number);
        AreEqual(((byte)@class, (byte)state), (ex.Class, ex.State));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void AddFile_TakesTheLowestFreeId_AndPrimaryWhateverTheDefault()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            alter database current add filegroup fg1;
            alter database current add file (name = d3, filename = '{Path}d3.ndf');
            alter database current add file (name = d4, filename = '{Path}d4.ndf', size = 1MB, maxsize = 10MB, filegrowth = 1MB) to filegroup fg1;
            alter database current add log file (name = l5, filename = '{Path}l5.ldf', size = 600KB);
            alter database current add file (name = d6, filename = '{Path}d6.ndf', size = 512KB, maxsize = 513KB, filegrowth = 10%);
            alter database current remove file l5;
            alter database current modify filegroup fg1 default;
            alter database current add file (name = d5, filename = '{Path}d5.ndf', size = 1MB)
            """);
        AreEqual("3:d3:1:1024:-1:8192:0 4:d4:2:128:1280:128:0 5:d5:1:128:-1:8192:0 6:d6:1:64:72:10:1", sim.ExecuteScalar(Files("file_id > 2")));
        AreEqual(
            $"3|{Path}d3.ndf 4|{Path}d4.ndf 5|{Path}d5.ndf 6|{Path}d6.ndf",
            sim.ExecuteScalar("select string_agg(concat(file_id, '|', physical_name), ' ') within group (order by file_id) from sys.master_files where database_id = db_id() and file_id > 2"));
    }

    [TestMethod]
    public void FileScalars()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            alter database current add file (name = d3, filename = '{Path}d3.ndf', size = 1MB);
            alter database current add log file (name = l4, filename = '{Path}l4.ldf', size = 1MB) to filegroup [primary]
            """);
        AreEqual(
            "d3|8|0|0|3|d3 l4|12|0|1|4|l4",
            sim.ExecuteScalar("select string_agg(concat(name, '|', fileproperty(name, 'SpaceUsed'), '|', fileproperty(name, 'IsPrimaryFile'), '|', fileproperty(name, 'IsLogFile'), '|', file_id(name), '|', file_name(file_id)), ' ') within group (order by file_id) from sys.database_files where file_id > 2"));
    }

    [TestMethod]
    public void RemoveFile()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            alter database current add filegroup fg1;
            alter database current add file (name = d3, filename = '{Path}d3.ndf', size = 1MB) to filegroup fg1;
            alter database current add file (name = d4, filename = '{Path}d4.ndf', size = 1MB);
            alter database current modify filegroup fg1 default
            """);
        _ = sim.AssertSqlError("alter database current remove file d3", 5031);
        AreEqual((byte)7, sim.AssertSqlError("alter database current remove filegroup fg1", 5042).State);
        _ = sim.ExecuteNonQuery("""
            alter database current remove file d4;
            alter database current modify filegroup [primary] default;
            alter database current remove file d3;
            alter database current remove filegroup fg1
            """);
        AreEqual("1:simulated:1:1024:-1:8192:0 2:simulated_log:0:1024:268435456:8192:0", sim.ExecuteScalar(Files()));
    }

    [TestMethod]
    public void Filegroups_DefaultReadOnlyAutogrowAndName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            alter database current add filegroup fg1;
            alter database current add file (name = d3, filename = '{Path}d3.ndf', size = 1MB) to filegroup fg1;
            alter database current add filegroup emp;
            alter database current modify filegroup fg1 default;
            alter database current modify filegroup fg1 readonly;
            alter database current modify filegroup fg1 autogrow_all_files
            """);
        AreEqual(
            "PRIMARY:0:0:0 fg1:1:1:1 emp:0:0:0",
            sim.ExecuteScalar("select string_agg(concat(name, ':', is_default, ':', is_read_only, ':', is_autogrow_all_files), ' ') within group (order by data_space_id) from sys.filegroups"));
        AreEqual("1|0|1|1|1", sim.ExecuteScalar("""
            select concat(filegroupproperty('fg1', 'IsDefault'), '|', filegroupproperty('PRIMARY', 'IsDefault'), '|', filegroupproperty('fg1', 'IsReadOnly'),
                '|', fileproperty('d3', 'IsReadOnly'), '|', (select is_read_only from sys.database_files where name = 'd3'))
            """));
        AreEqual((byte)1, sim.AssertSqlError("alter database current modify filegroup fg1 read_only", 5045).State);
        AreEqual((byte)3, sim.AssertSqlError("alter database current modify file (name = d3, size = 2MB)", 5048).State);
        _ = sim.AssertSqlError($"alter database current add file (name = d4, filename = '{Path}d4.ndf') to filegroup fg1", 5048);
        _ = sim.AssertSqlError("alter database current remove file d3", 5055);
        _ = sim.AssertSqlError("alter database current modify filegroup emp default", 5050);
        _ = sim.AssertSqlError("alter database current modify filegroup fg1 name = emp", 5035);
        _ = sim.ExecuteNonQuery("alter database current modify filegroup fg1 read_write; alter database current modify filegroup emp name = emp2; alter database current modify filegroup fg1 name = FG1");
        AreEqual("PRIMARY FG1 emp2", sim.ExecuteScalar("select string_agg(name, ' ') within group (order by data_space_id) from sys.filegroups"));
    }

    [TestMethod]
    public void Messages()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        var log = new List<string>();
        connection.InfoMessage += (_, e) => log.Add($"{e.Errors[0].Number}: {e.Message}");
        _ = connection.CreateCommand($"""
            alter database current add filegroup fg1;
            alter database current add file (name = d3, filename = '{Path}d3.ndf', size = 1MB) to filegroup fg1;
            alter database current modify file (name = d3, newname = d4, filename = '{Path}d4.ndf');
            alter database current modify filegroup fg1 read_only;
            alter database current modify filegroup fg1 name = fg2;
            alter database current modify filegroup fg2 read_write;
            alter database current remove file d4
            """).ExecuteNonQuery();
        CollectionAssert.AreEqual(
            new[]
            {
                "5018: The file \"d3\" has been modified in the system catalog. The new path will be used the next time the database is started.",
                "5021: The file name 'd4' has been set.",
                "5046: The filegroup property 'READ_ONLY' has been set.",
                "5021: The filegroup name 'fg2' has been set.",
                "5046: The filegroup property 'READ_WRITE' has been set.",
                "5044: The file 'd4' has been removed.",
            },
            log);
    }

    [TestMethod]
    public void ReadOnlyDatabase()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"alter database current add file (name = d3, filename = '{Path}d3.ndf', size = 1MB); alter database current set read_only");
        AreEqual((byte)4, sim.AssertSqlError("alter database current modify file (name = d3, size = 2MB)", 5004).State);
        AreEqual((byte)1, sim.AssertSqlError($"alter database current add file (name = d4, filename = '{Path}d4.ndf')", 5004).State);
        AreEqual((byte)3, sim.AssertSqlError("alter database current remove file d3", 5004).State);
        AreEqual(1, sim.AssertSqlError("alter database current add filegroup g", 3906).Errors.Count);
        AreEqual(1, sim.AssertSqlError("alter database current modify filegroup [primary] default", 3906).Errors.Count);
    }

    [TestMethod]
    [DataRow("modify file (name = x, size = 10MB)")]
    [DataRow("add file (name = x, filename = '/var/opt/mssql/data/x.ndf')")]
    [DataRow("remove file x")]
    [DataRow("modify filegroup x default")]
    [DataRow("add filegroup x")]
    [DataRow("remove filegroup x")]
    public void MissingDatabase_IsMsg911(string clause)
        => _ = new Simulation().AssertSqlError($"alter database nosuch {clause}", 911);

    [TestMethod]
    public void CreateDatabase_FileList()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create database c on primary (name = p1, filename = '{Path}c_p1.mdf', size = 10MB, maxsize = 50MB, filegrowth = 5MB), (name = p2, filename = '{Path}c_p2.ndf'),
                filegroup g1 (name = g1a, filename = '{Path}c_g1a.ndf', size = 2MB), filegroup g2 default (name = g2a, filename = '{Path}c_g2a.ndf')
                log on (name = lg, filename = '{Path}c_lg.ldf', size = 3MB), (name = lg2, filename = '{Path}c_lg2.ldf', size = 2MB)
            """);
        AreEqual(
            "1:p1:1:1280:6400:640:0 2:lg:0:384:268435456:8192:0 3:p2:1:1024:-1:8192:0 4:g1a:2:256:-1:8192:0 5:g2a:3:1024:-1:8192:0 6:lg2:0:256:268435456:8192:0",
            sim.ExecuteScalar(Files().Replace("sys.database_files", "c.sys.database_files", StringComparison.Ordinal)));
        AreEqual("PRIMARY:0 g1:0 g2:1", sim.ExecuteScalar("select string_agg(concat(name, ':', is_default), ' ') within group (order by data_space_id) from c.sys.filegroups"));
    }

    [TestMethod]
    [DataRow("size = 1MB", "1:q1:1:1024:-1:8192:0 2:c_log:0:1024:268435456:8192:0")]
    [DataRow("size = 1MB, maxsize = 2MB", "1:q1:1:1024:1024:8192:0 2:c_log:0:1024:268435456:8192:0")]
    [DataRow("size = 9MB, filegrowth = 10%", "1:q1:1:1152:-1:10:1 2:c_log:0:1024:268435456:8192:0")]
    public void CreateDatabase_PrimaryFloorsAtModelsSize(string options, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create database c on (name = q1, filename = '{Path}c_q1.mdf', {options})");
        AreEqual(expected, sim.ExecuteScalar(Files().Replace("sys.database_files", "c.sys.database_files", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("log on (name = l, filename = '/var/opt/mssql/data/c_l.ldf')", 188, 1)]
    [DataRow("on (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf'), (name = R1, filename = '/var/opt/mssql/data/c_r2.ndf')", 1828, 5)]
    [DataRow("on (name = r1)", 1036, 1)]
    [DataRow("on (filename = '/var/opt/mssql/data/c_r1.mdf')", 1036, 2)]
    [DataRow("on (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf', size = 5MB, maxsize = 2MB)", 5103, 1)]
    [DataRow("on (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf', size = 5MB, filegrowth = 10MB, maxsize = 6MB)", 5169, 2)]
    [DataRow("on (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf'), (name = r2, filename = '/var/opt/mssql/data/c_r2.ndf', size = 100KB)", 5174, 1)]
    [DataRow("on (name = r1, filename = '/var/opt/mssql/data/simulated.mdf')", 5170, 4)]
    [DataRow("on filegroup g1 (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf')", 102, 1)]
    [DataRow("on primary (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf'), filegroup g1 (name = a, filename = '/var/opt/mssql/data/c_a.ndf'), filegroup g1 (name = b, filename = '/var/opt/mssql/data/c_b.ndf')", 5035, 1)]
    [DataRow("on primary (name = r1, filename = '/var/opt/mssql/data/c_r1.mdf'), filegroup [primary] (name = a, filename = '/var/opt/mssql/data/c_a.ndf')", 5035, 1)]
    public void CreateDatabase_Refusals(string clause, int number, int state)
    {
        var sim = new Simulation();
        AreEqual((byte)state, sim.AssertSqlError($"create database c {clause}", number).State);
        IsTrue(sim.ExecuteScalar("select db_id('c')") is DBNull);
    }

    [TestMethod]
    public void CreateDatabase_RenamedDatabaseStillHoldsItsPaths()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database a; alter database a modify name = b");
        var ex = sim.AssertSqlError("create database a", 5170);
        AreEqual((1802, (byte)4), (ex.Errors[1].Number, ex.Errors[1].State));
        AreEqual("a|a_log", sim.ExecuteScalar("select string_agg(name, '|') within group (order by file_id) from b.sys.database_files"));
    }

    [TestMethod]
    public void HelpFile_AndDatabaseSize()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"alter database current add file (name = h1, filename = '{Path}h1.ndf', size = 1MB, maxsize = 10MB, filegrowth = 10%)");
        using var reader = sim.ExecuteReader("exec sp_helpfile h1");
        IsTrue(reader.Read());
        AreEqual($"h1|{Path}h1.ndf|PRIMARY|1024 KB|10240 KB|10%|data only", string.Join('|', Enumerable.Range(0, 7).Select(i => reader.GetValue(i).ToString()!.TrimEnd())));
        AreEqual(17408, sim.ExecuteScalar("declare @t table (n sysname, s int, r varchar(254)); insert @t exec sp_databases; select s from @t where n = db_name()"));
    }
}
