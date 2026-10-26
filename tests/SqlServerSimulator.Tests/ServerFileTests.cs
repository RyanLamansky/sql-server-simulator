using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The simulated server's disk as <c>sys.dm_os_file_exists</c>,
/// <c>xp_fileexist</c>, <c>xp_dirtree</c>, <c>xp_create_subdir</c> and
/// <c>xp_delete_file</c> see it, probed 2026-10-16 against SQL Server 2025 on
/// Linux.
/// </summary>
[TestClass]
public sealed class ServerFileTests
{
    private static string? FileExists(Simulation simulation, string path) =>
        simulation.ExecuteScalar($"select concat(file_exists, '|', file_is_a_directory, '|', parent_directory_exists) from sys.dm_os_file_exists(N'{path}')") as string;

    [TestMethod]
    [DataRow("/var/opt/mssql/data", "0|1|1")]
    [DataRow("/var/opt/mssql/data/", "0|1|1")]
    [DataRow("/VAR/opt/mssql/DATA", "0|1|1")]
    [DataRow("/var/opt/mssql/data/master.mdf", "1|0|1")]
    [DataRow("/var/opt/mssql/data/nope", "0|0|1")]
    [DataRow("/var/opt/mssql/data/nope/x", "0|0|0")]
    [DataRow("/var/opt/mssql/data/./nope/../", "0|1|1")]
    [DataRow("relative", "0|0|0")]
    [DataRow("/", "0|1|1")]
    [DataRow(@"C:\temp", "0|0|1")]
    public void FileExists_ReadsTheServersDisk(string path, string expected) =>
        AreEqual(expected, FileExists(new Simulation(), path));

    [TestMethod]
    [DataRow("''")]
    [DataRow("null")]
    public void FileExists_OfNothing_IsMsg2561(string path) =>
        new Simulation().AssertSqlError($"select * from sys.dm_os_file_exists({path})", 2561, "Parameter 0 is incorrect for this statement.");

    [TestMethod]
    public void CreateSubdir_CreatesEveryDirectoryOnTheWay()
    {
        var simulation = new Simulation();
        AreEqual(0, simulation.ExecuteScalar("declare @rc int; exec @rc = xp_create_subdir N'/var/opt/mssql/data/fs/a/b'; select @rc"));
        AreEqual("0|1|1", FileExists(simulation, "/var/opt/mssql/data/fs/a/b"));
        AreEqual("0|1|1", FileExists(simulation, "/var/opt/mssql/data/fs/A"));
        AreEqual(0, simulation.ExecuteScalar("declare @rc int; exec @rc = master.dbo.xp_create_subdir N'/var/opt/mssql/data/fs/a'; select @rc"));
    }

    [TestMethod]
    [DataRow("exec @rc = xp_create_subdir N'relative/dir'", 22048, 15, "Error executing extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_create_subdir null", 22048, 15, "Error executing extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_create_subdir", 22048, 15, "Error executing extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_create_subdir N'/var/opt/mssql/data/master.mdf/sub'", 22048, 16, "xp_create_subdir() returned error 183, 'Cannot create a file when that file already exists.'")]
    [DataRow("exec @rc = xp_delete_file 0, N'/var/opt/mssql/data/nope', N'bak', N'2030-01-01T00:00:00'", 22049, 16, "xp_delete_file() returned error 2, 'The system cannot find the file specified.'")]
    [DataRow("exec @rc = xp_delete_file 0, N'/var/opt/mssql/data', N'bak', N'2030-01-01T00:00:00.1234567'", 22049, 15, "Error executing extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_delete_file 0, N'/var/opt/mssql/data/x.bak', null, N'2030-01-01T00:00:00'", 22049, 15, "Error executing extended stored procedure: Invalid Parameter")]
    public void FileProcedureRefusals_NeitherCatchNorSetAtAtError(string call, int number, int severity, string message)
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
        AreEqual(((byte)severity, (byte)1, 0, message), (error.Class, error.State, error.LineNumber, error.Message));
        AreEqual("0|1", simulation.ExecuteScalar("select concat(e, '|', rc) from r"));
    }

    [TestMethod]
    public void Backup_CreatesTheDirectoriesAboveItsDevice_AndShowsAsAFile()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d; backup database d to disk = N'/var/opt/mssql/data/fs/q/r/w.bak'");
        AreEqual("1|0|1", FileExists(simulation, "/var/opt/mssql/data/fs/q/r/w.bak"));
        AreEqual("1|0|1", FileExists(simulation, "/VAR/OPT/mssql/data/fs/Q/r/W.BAK/"));
        AreEqual("0|1|1", FileExists(simulation, "/var/opt/mssql/data/fs/q"));
    }

    [TestMethod]
    public void RelativeDevice_ResolvesUnderTheDefaultBackupDirectory()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d; backup database d to disk = N'rel.bak'");
        AreEqual("1|0|1", FileExists(simulation, "/var/opt/mssql/data/rel.bak"));
        simulation.AssertSqlError("restore verifyonly from disk = N'nope.bak'", 3201,
            "Cannot open backup device '/var/opt/mssql/data/nope.bak'. Operating system error 2(The system cannot find the file specified.).");
        simulation.AssertSqlError("restore headeronly from disk = N'/var/opt/mssql/data'", 3201,
            "Cannot open backup device '/var/opt/mssql/data'. Operating system error 5(Access is denied.).");
    }

    [TestMethod]
    public void FileExist_ReportsARowOrAFileFlag()
    {
        var simulation = new Simulation();
        AreEqual("0|1|1", Row(simulation, "exec master.dbo.xp_fileexist N'/var/opt/mssql/data'"));
        AreEqual("1|0|1", Row(simulation, "exec master.dbo.xp_fileexist N'/var/opt/mssql/data/master.mdf'"));
        AreEqual("0|0|0", Row(simulation, "exec master.dbo.xp_fileexist N'/var/opt/mssql/data/nope/x'"));
        AreEqual("1|0", simulation.ExecuteScalar("""
            declare @f int, @d int;
            exec xp_fileexist N'/var/opt/mssql/data/master.mdf', @f output;
            exec xp_fileexist N'/var/opt/mssql/data', @d output;
            select concat(@f, '|', @d)
            """));
    }

    [TestMethod]
    public void Dirtree_ListsDepthFirst_ToTheDepthAsked()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database d;
            exec xp_create_subdir N'/var/opt/mssql/data/fs/a/b/c';
            backup database d to disk = N'/var/opt/mssql/data/fs/a/b/x.bak';
            backup database d to disk = N'/var/opt/mssql/data/fs/q/w.bak';
            """);
        AreEqual("a:1,b:2,c:3,q:1", simulation.ExecuteScalar(Dirtree("N'/var/opt/mssql/data/fs'")));
        AreEqual("a:1,b:2,q:1", simulation.ExecuteScalar(Dirtree("N'/var/opt/mssql/data/fs', 2")));
        AreEqual("a:1:0,b:2:0,c:3:0,x.bak:3:1,q:1:0,w.bak:2:1", simulation.ExecuteScalar(DirtreeFiles("N'/var/opt/mssql/data/fs', 0, 1")));
        AreEqual("", simulation.ExecuteScalar(Dirtree("N'/var/opt/mssql/data/nope'")));
    }

    [TestMethod]
    public void DeleteFile_RemovesBackupsOlderThanTheDate_ByExtension()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database d;
            backup database d to disk = N'/var/opt/mssql/data/fs/x.bak';
            backup database d to disk = N'/var/opt/mssql/data/fs/y.trn';
            backup database d to disk = N'/var/opt/mssql/data/fs/sub/z.bak';
            """);
        AreEqual(0, simulation.ExecuteScalar("declare @rc int; exec @rc = xp_delete_file 0, N'/var/opt/mssql/data/fs', N'bak', N'2000-01-01T00:00:00'; select @rc"));
        AreEqual("sub:1:0,z.bak:2:1,x.bak:1:1,y.trn:1:1", simulation.ExecuteScalar(DirtreeFiles("N'/var/opt/mssql/data/fs', 0, 1")));
        _ = simulation.ExecuteNonQuery("declare @d nvarchar(19) = convert(nvarchar(19), dateadd(hour, 1, sysdatetime()), 126); exec xp_delete_file 0, N'/var/opt/mssql/data/fs', N'bak', @d");
        AreEqual("sub:1:0,z.bak:2:1,y.trn:1:1", simulation.ExecuteScalar(DirtreeFiles("N'/var/opt/mssql/data/fs', 0, 1")));
        _ = simulation.ExecuteNonQuery("exec xp_delete_file 0, N'/var/opt/mssql/data/fs', N'bak', N'2030-01-01', 1; exec xp_delete_file 0, N'/var/opt/mssql/data/fs/y.trn'");
        AreEqual("sub:1:0", simulation.ExecuteScalar(DirtreeFiles("N'/var/opt/mssql/data/fs', 0, 1")));
    }

    [TestMethod]
    public void FileProcedures_ShortOfSysadmin()
    {
        var opened = 0;
        var simulation = new Simulation { OpenBackupFile = (_, _) => { opened++; return null; } };
        _ = simulation.ExecuteNonQuery("create login low with password = 'Zq!8vWm#2pLr', check_policy = off");
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("use master; execute as login = 'low'").ExecuteNonQuery();
        var denied = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec master.dbo.xp_create_subdir N'/var/opt/mssql/data/x'").ExecuteNonQuery());
        AreEqual((229, "The EXECUTE permission was denied on the object 'xp_create_subdir', database 'mssqlsystemresource', schema 'sys'."), (denied.Number, denied.Errors[0].Message));
        AreEqual("0|0|0", Row(connection, "exec xp_fileexist N'/var/opt/mssql/data'"));
        AreEqual("0|0|0", Row(connection, "select * from sys.dm_os_file_exists(N'/var/opt/mssql/data')"));
        using var reader = connection.CreateCommand("exec xp_dirtree N'/var/opt/mssql/data', 1").ExecuteReader();
        IsFalse(reader.Read());
        AreEqual(0, opened);
    }

    [TestMethod]
    public void LogDirectory_HoldsTheDefaultTraceAndTheEventFiles()
        => AreEqual("1|1|1|1", new Simulation().ExecuteScalar("""
            declare @sessions int = (select count(*) from sys.dm_xe_sessions);
            create table #t (s nvarchar(260), d int, f int);
            insert #t exec xp_dirtree N'/var/opt/mssql/log', 1, 1;
            select concat((select file_exists from sys.dm_os_file_exists((select path from sys.traces where is_default = 1))), '|',
                (select count(*) from #t where s = N'log_1.trc' and f = 1), '|',
                (select count(*) from #t where s like N'system[_]health[_]0[_]%.xel' and f = 1), '|',
                (select count(*) from #t where s like N'HkEngineEventFile[_]0[_]%.xel' and f = 1))
            """));

    private static string Dirtree(string arguments) => $"""
        create table #t (s nvarchar(260), d int);
        insert #t exec xp_dirtree {arguments};
        select isnull(string_agg(concat(s, ':', d), ','), '') from #t
        """;

    private static string DirtreeFiles(string arguments) => $"""
        create table #t (n int identity, s nvarchar(260), d int, f int);
        insert #t (s, d, f) exec xp_dirtree {arguments};
        select isnull(string_agg(concat(s, ':', d, ':', f), ',') within group (order by n), '') from #t
        """;

    private static string Row(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        return Row(connection, sql);
    }

    private static string Row(System.Data.Common.DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        IsTrue(reader.Read());
        return string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)));
    }
}
