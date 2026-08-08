using System.Data.Common;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.Extensions;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the size / session / database-and-principal metadata procs —
/// <c>sp_spaceused</c>, <c>sp_who</c> / <c>sp_who2</c>, <c>sp_helpdb</c>,
/// <c>sp_helpfile</c>, <c>sp_helpstats</c>, <c>sp_helprotect</c>,
/// <c>sp_helptrigger</c>, <c>sp_helpuser</c>, <c>sp_MSforeachtable</c> and
/// <c>sp_MSforeachdb</c>. Every asserted shape and wording is probe-confirmed
/// against SQL Server 2025 (2026-07-31 / 2026-08-01).
/// </summary>
[TestClass]
public sealed class SystemInfoProcTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int ThreadStartTimeoutMs = 5000;

    // One result set: its column names plus its rows as ordinal-keyed values.
    private sealed record ProcSet(string[] Names, List<object?[]> Rows);

    private static (List<ProcSet> Sets, List<SimulatedError> Errors) Run(
        DbConnection connection, string commandText)
    {
        var errors = new List<SimulatedError>();
        var simulated = (SimulatedDbConnection)connection;
        void Collect(object? sender, SimulatedInfoMessageEventArgs e) => errors.AddRange(e.Errors);
        simulated.InfoMessage += Collect;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            using var reader = command.ExecuteReader();
            var sets = new List<ProcSet>();
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                var names = new string[reader.FieldCount];
                for (var i = 0; i < names.Length; i++)
                    names[i] = reader.GetName(i);
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var values = new object?[reader.FieldCount];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(values);
                }

                sets.Add(new ProcSet(names, rows));
            }
            while (reader.NextResult());
            return (sets, errors);
        }
        finally
        {
            simulated.InfoMessage -= Collect;
        }
    }

    private static (List<ProcSet> Sets, List<SimulatedError> Errors) Run(Simulation simulation, string commandText)
    {
        using var connection = simulation.CreateOpenConnection();
        return Run(connection, commandText);
    }

    private static List<ProcSet> Sets(Simulation simulation, string commandText) => Run(simulation, commandText).Sets;

    private static string[] ColumnNames(Simulation simulation, string commandText) => Sets(simulation, commandText)[0].Names;

    // ===== sp_spaceused =====

    private static Simulation SpaceFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.sized (id int identity primary key, payload nvarchar(200) null, v int null);
            create index ix_sized_v on dbo.sized(v);
            insert dbo.sized (payload, v) values ('a', 1), ('b', 2), ('c', 3)
            """);
        return sim;
    }

    [TestMethod]
    public void SpaceUsed_ObjectForm_ColumnNames()
        => CollectionAssert.AreEqual(
            new[] { "name", "rows", "reserved", "data", "index_size", "unused" },
            ColumnNames(SpaceFixture(), "exec sp_spaceused 'dbo.sized'"));

    [TestMethod]
    public void SpaceUsed_ObjectForm_ReportsLiveRowCountAndKilobytes()
    {
        var row = Sets(SpaceFixture(), "exec sp_spaceused 'dbo.sized'")[0].Rows[0];
        AreEqual("sized", ((string)row[0]!).TrimEnd());
        // CONVERT(char(20), …) left-aligns the count in a fixed-width cell.
        AreEqual("3", ((string)row[1]!).TrimEnd());
        // One data page for the heap plus the nonclustered index's partition,
        // so reserved is twice data and nothing is unused.
        AreEqual("16 KB", row[2]);
        AreEqual("8 KB", row[3]);
        AreEqual("8 KB", row[4]);
        AreEqual("0 KB", row[5]);
    }

    [TestMethod]
    public void SpaceUsed_ObjectForm_TracksInsertsWithinTheSameBatch()
    {
        var sim = SpaceFixture();
        _ = sim.ExecuteNonQuery("create table dbo.empty (id int)");
        AreEqual("0", ((string)Sets(sim, "exec sp_spaceused 'dbo.empty'")[0].Rows[0][1]!).TrimEnd());
        _ = sim.ExecuteNonQuery("insert dbo.empty values (1), (2)");
        AreEqual("2", ((string)Sets(sim, "exec sp_spaceused 'dbo.empty'")[0].Rows[0][1]!).TrimEnd());
    }

    [TestMethod]
    public void SpaceUsed_ObjectForm_AgreesWithPartitionStats()
    {
        var sim = SpaceFixture();
        var reserved = (string)Sets(sim, "exec sp_spaceused 'dbo.sized'")[0].Rows[0][2]!;
        var pages = sim.ExecuteScalar<long>(
            "select sum(reserved_page_count) from sys.dm_db_partition_stats where object_id = object_id('dbo.sized')");
        AreEqual($"{pages * 8} KB", reserved);
    }

    [TestMethod]
    public void SpaceUsed_DatabaseForm_EmitsSummaryThenDetailSets()
    {
        var sets = Sets(SpaceFixture(), "exec sp_spaceused");
        HasCount(2, sets);
        CollectionAssert.AreEqual(new[] { "database_name", "database_size", "unallocated space" }, sets[0].Names);
        CollectionAssert.AreEqual(new[] { "reserved", "data", "index_size", "unused" }, sets[1].Names);
        AreEqual("simulated", sets[0].Rows[0][0]);
        Assert.EndsWith(" MB", (string)sets[0].Rows[0][1]!);
        Assert.EndsWith(" KB", (string)sets[1].Rows[0][0]!);
    }

    [TestMethod]
    public void SpaceUsed_OneResultSet_FusesBothIntoSevenColumns()
    {
        var sets = Sets(SpaceFixture(), "exec sp_spaceused @oneresultset = 1");
        HasCount(1, sets);
        CollectionAssert.AreEqual(
            new[] { "database_name", "database_size", "unallocated space", "reserved", "data", "index_size", "unused" },
            sets[0].Names);
    }

    [TestMethod]
    public void SpaceUsed_IncludeXtpStorage_AddsThreeNullColumns()
    {
        var set = Sets(SpaceFixture(), "exec sp_spaceused @oneresultset = 1, @include_total_xtp_storage = 1")[0];
        CollectionAssert.AreEqual(
            new[]
            {
                "database_name", "database_size", "unallocated space", "reserved", "data", "index_size", "unused",
                "xtp_precreated", "xtp_used", "xtp_pending_truncation",
            },
            set.Names);
        IsNull(set.Rows[0][7]);
        IsNull(set.Rows[0][8]);
        IsNull(set.Rows[0][9]);
    }

    [TestMethod]
    public void SpaceUsed_View_ReportsTheNoAllocationShape()
    {
        var sim = SpaceFixture();
        sim.ExecuteBatches("create view dbo.v_sized as select id from dbo.sized");
        var row = Sets(sim, "exec sp_spaceused 'dbo.v_sized'")[0].Rows[0];
        AreEqual("v_sized", row[0]);
        IsNull(row[1]);
        IsNull(row[2]);
        IsNull(row[3]);
        AreEqual("0 KB", row[4]);
        AreEqual("0 KB", row[5]);
    }

    [TestMethod]
    public void SpaceUsed_UpdateUsageTrue_IsAcceptedAndPrintsASpace()
    {
        var (sets, errors) = Run(SpaceFixture(), "exec sp_spaceused 'dbo.sized', 'true'");
        HasCount(1, sets);
        AreEqual(" ", errors[0].Message);
    }

    [TestMethod]
    public void SpaceUsed_UnknownObject_Raises15009()
        => SpaceFixture().AssertSqlError("exec sp_spaceused 'dbo.nosuch'", 15009,
            "The object 'dbo.nosuch' does not exist in database 'simulated' or is invalid for this operation.");

    [TestMethod]
    public void SpaceUsed_Procedure_Raises15234()
    {
        var sim = SpaceFixture();
        sim.ExecuteBatches("create procedure dbo.p_sized as select 1");
        sim.AssertSqlError("exec sp_spaceused 'dbo.p_sized'", 15234, "Objects of this type have no space allocated.");
    }

    [TestMethod]
    public void SpaceUsed_OtherDatabaseQualifier_Raises15250()
        => _ = SpaceFixture().AssertSqlError("exec sp_spaceused 'master.dbo.sized'", 15250);

    [TestMethod]
    public void SpaceUsed_BadUpdateUsage_Raises15143Lowercased()
        => SpaceFixture().AssertSqlError("exec sp_spaceused @updateusage = 'Bogus'", 15143,
            "'bogus' is not a valid option for the @updateusage parameter. Enter either 'true' or 'false'.");

    [TestMethod]
    public void SpaceUsed_BadMode_Raises14822()
        => SpaceFixture().AssertSqlError("exec sp_spaceused @mode = 'bogus'", 14822,
            "'bogus' is not a valid option for the @mode parameter. Enter  'ALL', 'LOCAL_ONLY' or 'REMOTE_ONLY'.");

    [TestMethod]
    public void SpaceUsed_RemoteOnly_Raises14821()
        => SpaceFixture().AssertSqlError("exec sp_spaceused @mode = 'REMOTE_ONLY'", 14821,
            "Cannot execute in REMOTE_ONLY mode since remote part does not exist or is invalid for this operation.");

    // ===== sp_who / sp_who2 =====

    [TestMethod]
    public void Who_ColumnNames()
        => CollectionAssert.AreEqual(
            new[] { "spid", "ecid", "status", "loginame", "hostname", "blk", "dbname", "cmd", "request_id" },
            ColumnNames(new Simulation(), "exec sp_who"));

    [TestMethod]
    public void Who2_ColumnNames()
        => CollectionAssert.AreEqual(
            new[]
            {
                "SPID", "Status", "Login", "HostName", "BlkBy", "DBName", "Command",
                "CPUTime", "DiskIO", "LastBatch", "ProgramName", "SPID", "REQUESTID",
            },
            ColumnNames(new Simulation(), "exec sp_who2"));

    [TestMethod]
    public void Who_ReportsTheObservingSessionAsRunnableSelect()
    {
        var row = Sets(new Simulation(), "exec sp_who")[0].Rows[0];
        AreEqual((short)51, row[0]);
        AreEqual((short)0, row[1]);
        AreEqual("runnable", ((string)row[2]!).TrimEnd());
        AreEqual("dbo", row[3]);
        AreEqual("0", ((string)row[5]!).TrimEnd());
        AreEqual("simulated", row[6]);
        AreEqual("SELECT", ((string)row[7]!).TrimEnd());
        AreEqual(0, row[8]);
    }

    [TestMethod]
    public void Who_ListsEveryOpenConnectionInSpidOrder()
    {
        var sim = new Simulation();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        var rows = Run(first, "exec sp_who").Sets[0].Rows;
        HasCount(2, rows);
        AreEqual((short)51, rows[0][0]);
        AreEqual((short)52, rows[1][0]);
        // Only the session running sp_who has a statement in flight.
        AreEqual("runnable", ((string)rows[0][2]!).TrimEnd());
        AreEqual("sleeping", ((string)rows[1][2]!).TrimEnd());
        AreEqual("AWAITING COMMAND", ((string)rows[1][7]!).TrimEnd());
    }

    [TestMethod]
    public void Who_SessionFollowsUseDatabase()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("use master").ExecuteNonQuery();
        AreEqual("master", Run(connection, "exec sp_who").Sets[0].Rows[0][6]);
    }

    [TestMethod]
    public void Who_SpidArgument_SelectsOneSession()
    {
        var sim = new Simulation();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        HasCount(1, Run(first, "exec sp_who 52").Sets[0].Rows);
        AreEqual((short)52, Run(first, "exec sp_who 52").Sets[0].Rows[0][0]);
    }

    [TestMethod]
    public void Who_Active_DropsIdleSessions()
    {
        var sim = new Simulation();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        HasCount(2, Run(first, "exec sp_who").Sets[0].Rows);
        HasCount(1, Run(first, "exec sp_who 'active'").Sets[0].Rows);
    }

    [TestMethod]
    public void Who_KnownLogin_Filters()
        => HasCount(1, Sets(new Simulation(), "exec sp_who 'dbo'")[0].Rows);

    [TestMethod]
    public void Who_UnknownLogin_Raises15007()
        => new Simulation().AssertSqlError("exec sp_who 'nosuchlogin'", 15007,
            "'nosuchlogin' is not a valid login or you do not have permission.");

    [TestMethod]
    public void Who2_UnknownLogin_Raises15007()
        => _ = new Simulation().AssertSqlError("exec sp_who2 'nosuchlogin'", 15007);

    [TestMethod]
    public void Who2_UppercasesEveryStatusButSleeping()
    {
        var sim = new Simulation();
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        var rows = Run(first, "exec sp_who2").Sets[0].Rows;
        AreEqual("RUNNABLE", ((string)rows[0][1]!).TrimEnd());
        AreEqual("sleeping", ((string)rows[1][1]!).TrimEnd());
    }

    [TestMethod]
    public void Who2_ReportsIdlePlaceholdersForUnmeteredColumns()
    {
        var row = Sets(new Simulation(), "exec sp_who2")[0].Rows[0];
        AreEqual("51", ((string)row[0]!).TrimEnd());
        AreEqual("  .", ((string)row[3]!).TrimEnd());  // HostName
        AreEqual("  .", ((string)row[4]!).TrimEnd());  // BlkBy
        AreEqual("0", row[7]);                          // CPUTime
        AreEqual("0", row[8]);                          // DiskIO
        AreEqual("", row[10]);                          // ProgramName
        AreEqual("51", ((string)row[11]!).TrimEnd());   // the repeated SPID
        AreEqual("0", ((string)row[12]!).TrimEnd());    // REQUESTID
        AreEqual(14, ((string)row[9]!).Length);         // LastBatch: MM/DD hh:mm:ss
    }

    [TestMethod]
    public async Task Who_BlockedSession_ReportsSuspendedWithTheBlockerSpid()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        using var writer = sim.CreateOpenConnection();
        using var reader = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        var writerSpid = (short)writer.CreateCommand("select @@spid").ExecuteScalar()!;
        var readerSpid = (short)reader.CreateCommand("select @@spid").ExecuteScalar()!;

        _ = writer.CreateCommand("begin tran; insert t values (42)").ExecuteNonQuery();

        var readerStarted = new ManualResetEventSlim();
        var readerTask = Task.Run(() =>
        {
            readerStarted.Set();
            _ = reader.CreateCommand("select count(*) from t").ExecuteScalar();
        }, TestContext.CancellationToken);

        IsTrue(readerStarted.Wait(ThreadStartTimeoutMs, TestContext.CancellationToken));

        // Polled rather than slept on: the reader shows up as suspended only
        // once its thread actually reaches the conflicting lock.
        var who = await PollUntil(
            () => Run(observer, "exec sp_who").Sets[0].Rows,
            rows => rows.Find(r => (short)r[0]! == readerSpid) is { } row && ((string)row[2]!).TrimEnd() == "suspended",
            TestContext.CancellationToken);
        var blocked = who.Find(r => (short)r[0]! == readerSpid)!;
        AreEqual("suspended", ((string)blocked[2]!).TrimEnd());
        AreEqual(writerSpid.ToString(CultureInfo.InvariantCulture), ((string)blocked[5]!).TrimEnd());

        var who2 = Run(observer, "exec sp_who2").Sets[0].Rows;
        var blocked2 = who2.Find(r => ((string)r[0]!).TrimEnd() == readerSpid.ToString(CultureInfo.InvariantCulture))!;
        AreEqual("SUSPENDED", ((string)blocked2[1]!).TrimEnd());
        AreEqual(writerSpid.ToString(CultureInfo.InvariantCulture), ((string)blocked2[4]!).TrimEnd());

        _ = writer.CreateCommand("commit tran").ExecuteNonQuery();
        await readerTask;
    }

    // ===== sp_helpdb =====

    [TestMethod]
    public void HelpDb_ColumnNames()
        => CollectionAssert.AreEqual(
            new[] { "name", "db_size", "owner", "dbid", "created", "status", "compatibility_level" },
            ColumnNames(new Simulation(), "exec sp_helpdb"));

    [TestMethod]
    public void HelpDb_NoArgument_ListsAccessibleDatabasesByNameAndSkipsModel()
    {
        var (sets, errors) = Run(new Simulation(), "exec sp_helpdb");
        var names = sets[0].Rows.ConvertAll(r => (string)r[0]!);
        CollectionAssert.AreEqual(new[] { "master", "msdb", "simulated", "tempdb" }, names);
        AreEqual(15622, errors[0].Number);
        AreEqual("No permission to access database 'model'.", errors[0].Message);
    }

    [TestMethod]
    public void HelpDb_RowReportsLiveDatabaseState()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set compatibility_level = 160");
        var row = Sets(sim, "exec sp_helpdb 'simulated'")[0].Rows[0];
        AreEqual("simulated", row[0]);
        Assert.EndsWith(" MB", (string)row[1]!);
        AreEqual("dbo", row[2]);
        AreEqual((short)5, row[3]);
        AreEqual(11, ((string)row[4]!).Length);
        Assert.Contains("Collation=SQL_Latin1_General_CP1_CI_AS", (string)row[5]!);
        AreEqual((byte)160, row[6]);
    }

    [TestMethod]
    public void HelpDb_StatusStringTracksRecursiveTriggers()
    {
        var sim = new Simulation();
        Assert.DoesNotContain("IsRecursiveTriggersEnabled", (string)Sets(sim, "exec sp_helpdb 'simulated'")[0].Rows[0][5]!);
        _ = sim.ExecuteNonQuery("alter database simulated set recursive_triggers on");
        Assert.Contains("IsRecursiveTriggersEnabled", (string)Sets(sim, "exec sp_helpdb 'simulated'")[0].Rows[0][5]!);
    }

    [TestMethod]
    public void HelpDb_SingleDatabase_AppendsTheFileSet()
    {
        var sets = Sets(new Simulation(), "exec sp_helpdb 'simulated'");
        HasCount(2, sets);
        CollectionAssert.AreEqual(
            new[] { "name", "fileid", "filename", "filegroup", "size", "maxsize", "growth", "usage" }, sets[1].Names);
        var files = sets[1].Rows;
        AreEqual("simulated_Data", files[0][0]);
        AreEqual((short)1, files[0][1]);
        AreEqual("PRIMARY", files[0][3]);
        AreEqual("Unlimited", files[0][5]);
        AreEqual("65536 KB", files[0][6]);
        AreEqual("data only", files[0][7]);
        AreEqual("simulated_Log", files[1][0]);
        AreEqual((short)2, files[1][1]);
        IsNull(files[1][3]);
        AreEqual("log only", files[1][7]);
    }

    [TestMethod]
    public void HelpDb_FileSizeAgreesWithDatabaseFiles()
    {
        var sim = new Simulation();
        var pages = sim.ExecuteScalar<int>("select size from sys.database_files where file_id = 1");
        AreEqual($"{pages * 8} KB", Sets(sim, "exec sp_helpdb 'simulated'")[1].Rows[0][4]);
    }

    [TestMethod]
    public void HelpDb_UnknownDatabase_Raises15010()
        => new Simulation().AssertSqlError("exec sp_helpdb 'nosuchdb'", 15010,
            "The database 'nosuchdb' does not exist. Supply a valid database name. To see available databases, use sys.databases.");

    [TestMethod]
    public void HelpDb_NamedArgumentSelectsTheOneDatabase()
        => HasCount(1, Sets(new Simulation(), "exec sp_helpdb @dbname = 'simulated'")[0].Rows);

    // ===== sp_helpfile =====

    [TestMethod]
    public void HelpFile_NoArgument_ListsBothFilesWithTheFileId()
    {
        var sets = Sets(new Simulation(), "exec sp_helpfile");
        HasCount(1, sets);
        CollectionAssert.AreEqual(
            new[] { "name", "fileid", "filename", "filegroup", "size", "maxsize", "growth", "usage" }, sets[0].Names);
        CollectionAssert.AreEqual(
            new[] { "simulated_Data", "simulated_Log" }, sets[0].Rows.ConvertAll(r => (string)r[0]!));
        AreEqual((short)1, sets[0].Rows[0][1]);
        AreEqual((short)2, sets[0].Rows[1][1]);
    }

    [TestMethod]
    public void HelpFile_NamedFile_DropsTheFileIdColumn()
    {
        var sets = Sets(new Simulation(), "exec sp_helpfile @filename = 'simulated_Log'");
        CollectionAssert.AreEqual(
            new[] { "name", "filename", "filegroup", "size", "maxsize", "growth", "usage" }, sets[0].Names);
        var row = sets[0].Rows.Single();
        AreEqual("simulated_Log", row[0]);
        // nvarchar(260), so the path carries no padding.
        AreEqual("/var/opt/mssql/data/simulated_log.ldf", row[1]);
        IsNull(row[2]);
        AreEqual("log only", row[6]);
    }

    [TestMethod]
    public void HelpFile_AgreesWithSpHelpDbsAppendedSet()
        => AreEqual(
            Sets(new Simulation(), "exec sp_helpdb 'simulated'")[1].Rows[0][4],
            Sets(new Simulation(), "exec sp_helpfile")[0].Rows[0][4]);

    [TestMethod]
    public void HelpFile_UnknownFile_Raises15325()
        => new Simulation().AssertSqlError("exec sp_helpfile 'nope'", 15325,
            "The current database does not contain a file named 'nope'.");

    // ===== sp_helpstats =====

    private static Simulation StatsFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.st (a int not null constraint pk_st primary key, b int, c int);
            create index ix_st_b on dbo.st (b desc);
            create table dbo.st_bare (a int)
            """);
        return sim;
    }

    [TestMethod]
    public void HelpStats_AllForm_ListsIndexBackedStatisticsWithUndirectedKeys()
    {
        var sets = Sets(StatsFixture(), "exec sp_helpstats 'dbo.st', 'ALL'");
        CollectionAssert.AreEqual(new[] { "statistics_name", "statistics_keys" }, sets[0].Names);
        CollectionAssert.AreEqual(new[] { "ix_st_b", "pk_st" }, sets[0].Rows.ConvertAll(r => (string)r[0]!));
        // index_col() reports the column name alone — no sp_helpindex "(-)".
        AreEqual("b", sets[0].Rows[0][1]);
    }

    [TestMethod]
    public void HelpStats_DefaultStatsForm_ReportsNoneAndYieldsNoResultSet()
    {
        // Only index-backed statistics are modeled, so the STATS form — which
        // real filters to statistics that are not indexes — always lands here.
        var (sets, errors) = Run(StatsFixture(), "exec sp_helpstats 'dbo.st'");
        Assert.IsEmpty(sets);
        AreEqual(15574, errors[0].Number);
        AreEqual("This object does not have any statistics.", errors[0].Message);
    }

    [TestMethod]
    public void HelpStats_AllForm_WithoutIndexes_Raises15575()
    {
        var (sets, errors) = Run(StatsFixture(), "exec sp_helpstats 'dbo.st_bare', 'ALL'");
        Assert.IsEmpty(sets);
        AreEqual(15575, errors[0].Number);
        AreEqual("This object does not have any statistics or indexes.", errors[0].Message);
    }

    [TestMethod]
    public void HelpStats_ResultsArgumentIsTruncatedToItsDeclaredWidth()
    {
        // @results is nvarchar(5), so 'statsZZZ' is compared as 'stats'.
        var (_, errors) = Run(StatsFixture(), "exec sp_helpstats 'dbo.st', 'statsZZZ'");
        AreEqual(15574, errors[0].Number);
    }

    [TestMethod]
    public void HelpStats_UnknownResultsOption_ReportsInvalidOption()
    {
        var (sets, errors) = Run(StatsFixture(), "exec sp_helpstats 'dbo.st', 'ALLXX'");
        Assert.IsEmpty(sets);
        AreEqual(50000, errors[0].Number);
        AreEqual("Invalid option: ALLXX", errors[0].Message);
    }

    [TestMethod]
    public void HelpStats_UnknownObject_Raises15009()
        => StatsFixture().AssertSqlError("exec sp_helpstats 'dbo.nope'", 15009,
            "The object 'dbo.nope' does not exist in database 'simulated' or is invalid for this operation.");

    // ===== sp_helprotect =====

    // One report row as pipe-joined text; the char(10) ProtectType cell is
    // trimmed so the comparison reads as the report does.
    private static string ProtectRowText(object?[] row) =>
        string.Join("|", Array.ConvertAll(row, c => ((string)c!).TrimEnd()));

    private static Simulation ProtectFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.pt (id int, a int, b int);
            create user pu1 without login;
            create user pu2 without login;
            grant select on dbo.pt to pu1;
            grant update (b) on dbo.pt to pu1;
            deny delete on dbo.pt to pu1;
            grant create table to pu1;
            grant select on dbo.pt to pu2 with grant option
            """);
        return sim;
    }

    [TestMethod]
    public void HelpProtect_ReportsObjectRowsThenStatementRows()
    {
        var sets = Sets(ProtectFixture(), "exec sp_helprotect");
        CollectionAssert.AreEqual(
            new[] { "Owner", "Object", "Grantee", "Grantor", "ProtectType", "Action", "Column" }, sets[0].Names);
        var rendered = sets[0].Rows.ConvertAll(ProtectRowText);
        CollectionAssert.AreEqual(
            new[]
            {
                "dbo|pt|pu1|dbo|Deny|Delete|.",
                "dbo|pt|pu1|dbo|Grant|Select|(All+New)",
                "dbo|pt|pu1|dbo|Grant|Update|b",
                "dbo|pt|pu2|dbo|Grant_WGO|Select|(All+New)",
                ".|.|pu1|dbo|Grant|CONNECT|.",
                ".|.|pu1|dbo|Grant|Create Table|.",
                ".|.|pu2|dbo|Grant|CONNECT|.",
            },
            rendered);
    }

    [TestMethod]
    public void HelpProtect_NameFiltersToTheObject()
        => HasCount(4, Sets(ProtectFixture(), "exec sp_helprotect 'dbo.pt'")[0].Rows);

    [TestMethod]
    public void HelpProtect_StatementNameFiltersToThePermission()
    {
        var row = Sets(ProtectFixture(), "exec sp_helprotect 'CREATE TABLE'")[0].Rows.Single();
        AreEqual(".", row[0]);
        AreEqual("Create Table", row[5]);
    }

    [TestMethod]
    public void HelpProtect_PermissionAreaSelectsTheStatementRowsOnly()
    {
        var rows = Sets(ProtectFixture(), "exec sp_helprotect null, null, null, 's'")[0].Rows;
        HasCount(3, rows);
        CollectionAssert.AreEqual(new[] { ".", ".", "." }, rows.ConvertAll(r => (string)r[1]!));
    }

    [TestMethod]
    public void HelpProtect_ColumnWidthsTrackTheReportedRows()
    {
        // Real types the report through substring(col, 1, max(datalength(col))),
        // so each width is twice the longest value's character count, and
        // ProtectType stays the temp table's char(10).
        var row = Sets(ProtectFixture(), "exec sp_helprotect 'CREATE TABLE'")[0].Rows.Single();
        AreEqual(1, ((string)row[0]!).Length);      // '.' → nvarchar(2)
        AreEqual("Grant     ", row[4]);             // char(10)
        AreEqual("Create Table", row[5]);           // nvarchar(24)
    }

    [TestMethod]
    public void HelpProtect_ObjectLevelGrantExpandsAcrossTheColumnsItStillCovers()
    {
        var sim = ProtectFixture();
        _ = sim.ExecuteNonQuery("""
            grant select on dbo.pt to pu2;
            grant select (id) on dbo.pt to pu2;
            deny select (b) on dbo.pt to pu2
            """);
        var rendered = Sets(sim, "exec sp_helprotect 'dbo.pt', 'pu2'")[0].Rows.ConvertAll(ProtectRowText);
        CollectionAssert.AreEqual(
            new[]
            {
                "dbo|pt|pu2|dbo|Deny|Select|b",
                "dbo|pt|pu2|dbo|Grant|Select|(New)",
                "dbo|pt|pu2|dbo|Grant|Select|id",
                "dbo|pt|pu2|dbo|Grant|Select|a",
            },
            rendered);
    }

    [TestMethod]
    public void HelpProtect_SchemaScopeGrantIsNotReported()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create role pr1; grant select on schema::dbo to pr1");
        sim.AssertSqlError("exec sp_helprotect null, 'pr1'", 15330,
            "There are no matching rows on which to report.");
    }

    [TestMethod]
    public void HelpProtect_NoMatchingRows_Raises15330()
        => ProtectFixture().AssertSqlError("exec sp_helprotect null, 'nosuchuser'", 15330,
            "There are no matching rows on which to report.");

    [TestMethod]
    public void HelpProtect_UnrecognizedPermissionArea_Raises15300()
        => ProtectFixture().AssertSqlError("exec sp_helprotect null, null, null, 'x'", 15300,
            "No recognized letter is contained in the parameter value for General Permission Type (X). Valid letters are in this set: o,s .");

    [TestMethod]
    public void HelpProtect_DatabaseQualifiedName_Raises15302()
        => ProtectFixture().AssertSqlError("exec sp_helprotect 'simulated.dbo.pt'", 15302,
            "Database_Name should not be used to qualify owner.object for the parameter into this procedure.");

    // ===== sp_helptrigger =====

    private static Simulation TriggerFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.trg_t (id int primary key)",
            "create view dbo.trg_v as select id from dbo.trg_t",
            "create trigger dbo.tr_after on dbo.trg_t after insert, delete as set nocount on",
            "create trigger dbo.tr_upd on dbo.trg_t after update as set nocount on",
            "create trigger dbo.tr_io on dbo.trg_v instead of update as set nocount on");
        return sim;
    }

    [TestMethod]
    public void HelpTrigger_ColumnNames()
        => CollectionAssert.AreEqual(
            new[]
            {
                "trigger_name", "trigger_owner", "isupdate", "isdelete", "isinsert",
                "isafter", "isinsteadof", "trigger_schema",
            },
            ColumnNames(TriggerFixture(), "exec sp_helptrigger 'dbo.trg_t'"));

    [TestMethod]
    public void HelpTrigger_ReportsEachAttachedTriggersActionFlags()
    {
        var rows = Sets(TriggerFixture(), "exec sp_helptrigger 'dbo.trg_t'")[0].Rows;
        HasCount(2, rows);
        CollectionAssert.AreEqual(new object?[] { "tr_after", "dbo", 0, 1, 1, 1, 0, "dbo" }, rows[0]);
        CollectionAssert.AreEqual(new object?[] { "tr_upd", "dbo", 1, 0, 0, 1, 0, "dbo" }, rows[1]);
    }

    [TestMethod]
    public void HelpTrigger_InsteadOfOnAView_ReportsIsInsteadOf()
    {
        var row = Sets(TriggerFixture(), "exec sp_helptrigger 'dbo.trg_v'")[0].Rows[0];
        CollectionAssert.AreEqual(new object?[] { "tr_io", "dbo", 1, 0, 0, 0, 1, "dbo" }, row);
    }

    [TestMethod]
    public void HelpTrigger_TypeArgument_Filters()
    {
        var sim = TriggerFixture();
        HasCount(1, Sets(sim, "exec sp_helptrigger 'dbo.trg_t', 'update'")[0].Rows);
        HasCount(1, Sets(sim, "exec sp_helptrigger 'dbo.trg_t', 'INSERT'")[0].Rows);
        HasCount(1, Sets(sim, "exec sp_helptrigger 'dbo.trg_t', 'delete'")[0].Rows);
    }

    [TestMethod]
    public void HelpTrigger_BadType_Raises15305()
        => TriggerFixture().AssertSqlError("exec sp_helptrigger 'dbo.trg_t', 'bogus'", 15305,
            "The @TriggerType parameter value must be 'insert', 'update', or 'delete'.");

    [TestMethod]
    public void HelpTrigger_UnknownObject_Raises15009()
        => _ = TriggerFixture().AssertSqlError("exec sp_helptrigger 'dbo.nosuch'", 15009);

    [TestMethod]
    public void HelpTrigger_Procedure_Raises15009()
    {
        var sim = TriggerFixture();
        sim.ExecuteBatches("create procedure dbo.p_trg as select 1");
        _ = sim.AssertSqlError("exec sp_helptrigger 'dbo.p_trg'", 15009);
    }

    // ===== sp_helpuser =====

    [TestMethod]
    public void HelpUser_ColumnNames()
        => CollectionAssert.AreEqual(
            new[] { "UserName", "RoleName", "LoginName", "DefDBName", "DefSchemaName", "UserID", "SID" },
            ColumnNames(new Simulation(), "exec sp_helpuser"));

    [TestMethod]
    public void HelpUser_NoArgument_ListsUsersNotRoles()
    {
        var names = Sets(new Simulation(), "exec sp_helpuser")[0].Rows.ConvertAll(r => (string)r[0]!);
        CollectionAssert.AreEqual(new[] { "dbo", "guest", "INFORMATION_SCHEMA", "sys" }, names);
    }

    [TestMethod]
    public void HelpUser_RoleMembership_ReplacesThePublicPlaceholder()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u_helper without login",
            "alter role db_datareader add member u_helper");
        var rows = Sets(sim, "exec sp_helpuser 'u_helper'")[0].Rows;
        HasCount(1, rows);
        AreEqual("u_helper", rows[0][0]);
        AreEqual("db_datareader", rows[0][1]);
    }

    [TestMethod]
    public void HelpUser_MappedLogin_ReportsLoginAndDefaultDatabase()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create login helperlogin with password = 'P@ss1word'",
            "create user u_mapped for login helperlogin");
        var row = Sets(sim, "exec sp_helpuser 'u_mapped'")[0].Rows[0];
        AreEqual("helperlogin", row[2]);
        AreEqual("master", row[3]);
    }

    [TestMethod]
    public void HelpUser_UserWithoutLogin_ReportsNullLoginAndDatabase()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create user u_bare without login");
        var row = Sets(sim, "exec sp_helpuser 'u_bare'")[0].Rows[0];
        IsNull(row[2]);
        IsNull(row[3]);
        // Neither a per-user default schema nor a SID is modeled — the same
        // NULLs sys.database_principals reports.
        IsNull(row[4]);
        IsNull(row[6]);
        AreEqual("u_bare", row[0]);
    }

    [TestMethod]
    public void HelpUser_RoleName_ReportsTheMembershipSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u_r1 without login",
            "create user u_r2 without login",
            "alter role db_datawriter add member u_r1",
            "alter role db_datawriter add member u_r2");
        var set = Sets(sim, "exec sp_helpuser 'db_datawriter'")[0];
        CollectionAssert.AreEqual(new[] { "Role_name", "Role_id", "Users_in_role", "Userid" }, set.Names);
        HasCount(2, set.Rows);
        AreEqual("db_datawriter", set.Rows[0][0]);
        CollectionAssert.AreEquivalent(
            new[] { "u_r1", "u_r2" }, set.Rows.ConvertAll(r => (string)r[2]!));
    }

    [TestMethod]
    public void HelpUser_UnknownName_Raises15198()
        => new Simulation().AssertSqlError("exec sp_helpuser 'nosuchuser'", 15198,
            "The name supplied (nosuchuser) is not a user, role, or aliased login.");

    // ===== sp_MSforeachtable =====

    private static Simulation ForEachFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.fe_a (id int);
            create table dbo.fe_b (id int);
            insert dbo.fe_a values (1), (2);
            insert dbo.fe_b values (3)
            """);
        return sim;
    }

    [TestMethod]
    public void ForEachTable_SubstitutesTheBracketedTwoPartName()
    {
        var sets = Sets(ForEachFixture(), "exec sp_MSforeachtable 'select ''?'' as t'");
        CollectionAssert.AreEquivalent(
            new[] { "[dbo].[fe_a]", "[dbo].[fe_b]" }, sets.ConvertAll(s => (string)s.Rows[0][0]!));
    }

    [TestMethod]
    public void ForEachTable_RunsTheCommandOncePerTable()
    {
        var sets = Sets(ForEachFixture(), "exec sp_MSforeachtable 'select count(*) as n from ?'");
        HasCount(2, sets);
        CollectionAssert.AreEquivalent(new[] { 2, 1 }, sets.ConvertAll(s => (int)s.Rows[0][0]!));
    }

    [TestMethod]
    public void ForEachTable_WhereAndFiltersTheTableList()
    {
        var sets = Sets(ForEachFixture(),
            "exec sp_MSforeachtable @command1 = 'select count(*) as n from ?', @whereand = 'and o.name = ''fe_a'''");
        HasCount(1, sets);
        AreEqual(2, sets[0].Rows[0][0]);
    }

    [TestMethod]
    public void ForEachTable_ReplaceCharOverride()
        => AreEqual("[dbo].[fe_a]", Sets(ForEachFixture(),
            "exec sp_MSforeachtable @command1 = 'select ''$'' as t', @replacechar = '$', @whereand = 'and o.name = ''fe_a'''")[0]
            .Rows[0][0]);

    [TestMethod]
    public void ForEachTable_RunsAllThreeCommandsPerTable()
    {
        var sets = Sets(ForEachFixture(),
            """
            exec sp_MSforeachtable
                @command1 = 'select 1 as c from ? where 1 = 0',
                @command2 = 'select 2 as c from ? where 1 = 0',
                @command3 = 'select 3 as c from ? where 1 = 0',
                @whereand = 'and o.name = ''fe_a'''
            """);
        CollectionAssert.AreEqual(new[] { "c", "c", "c" }, sets.ConvertAll(s => s.Names[0]));
        HasCount(3, sets);
    }

    [TestMethod]
    public void ForEachTable_PreAndPostCommandsRunOnce()
    {
        var sim = ForEachFixture();
        var sets = Sets(sim,
            """
            exec sp_MSforeachtable
                @command1 = 'select count(*) as n from ?',
                @precommand = 'select ''pre'' as tag',
                @postcommand = 'select ''post'' as tag'
            """);
        HasCount(4, sets);
        AreEqual("pre", sets[0].Rows[0][0]);
        AreEqual("post", sets[3].Rows[0][0]);
    }

    [TestMethod]
    public void ForEachTable_CommandMutatesEveryTable()
    {
        var sim = ForEachFixture();
        _ = Sets(sim, "exec sp_MSforeachtable 'delete from ?'");
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.fe_a"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.fe_b"));
    }

    // ===== sp_MSforeachdb =====

    // The accessible databases in database_id order — model is left out
    // because HAS_DBACCESS reports 0 for it, the same filter sp_helpdb applies.
    private static readonly string[] ForEachDbNames = ["master", "tempdb", "msdb", "simulated"];

    [TestMethod]
    public void ForEachDb_RunsTheCommandOncePerAccessibleDatabaseInIdOrder()
        => CollectionAssert.AreEqual(ForEachDbNames, Sets(new Simulation(),
            "exec sp_MSforeachdb 'select ''?'' as d'").ConvertAll(s => (string)s.Rows[0][0]!));

    [TestMethod]
    public void ForEachDb_DoesNotSwitchDatabaseContextOnItsOwn()
    {
        // Probe-confirmed: the proc leaves the session's database alone, so a
        // command reading DB_NAME() reports the caller's every time.
        var names = Sets(new Simulation(), "exec sp_MSforeachdb 'select db_name() as ctx'")
            .ConvertAll(s => (string)s.Rows[0][0]!);
        CollectionAssert.AreEqual(new[] { "simulated", "simulated", "simulated", "simulated" }, names);
    }

    [TestMethod]
    public void ForEachDb_UseCommandScopesToTheOneCommandAndLeavesTheSessionPut()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        var names = Run(connection, "exec sp_MSforeachdb 'use [?]; select db_name() as ctx'")
            .Sets.ConvertAll(s => (string)s.Rows[0][0]!);
        CollectionAssert.AreEqual(ForEachDbNames, names);
        AreEqual("simulated", Run(connection, "select db_name()").Sets[0].Rows[0][0]);
    }

    [TestMethod]
    public void ForEachDb_BareReplaceCharIsQuoteNamed()
        => CollectionAssert.AreEqual(
            ForEachDbNames,
            Sets(new Simulation(), "exec sp_MSforeachdb 'select 1 as ?'").ConvertAll(s => s.Names[0]));

    [TestMethod]
    public void ForEachDb_ReplaceCharOverrideAndPrePostCommandsRunOnce()
    {
        var sets = Sets(new Simulation(),
            """
            exec sp_MSforeachdb
                @command1 = 'select ''$'' as d',
                @replacechar = '$',
                @precommand = 'select ''pre'' as tag',
                @postcommand = 'select ''post'' as tag'
            """);
        HasCount(ForEachDbNames.Length + 2, sets);
        AreEqual("pre", sets[0].Rows[0][0]);
        AreEqual("master", sets[1].Rows[0][0]);
        AreEqual("post", sets[^1].Rows[0][0]);
    }

    [TestMethod]
    public void ForEachDb_RunsAllThreeCommandsPerDatabase()
    {
        var sets = Sets(new Simulation(),
            """
            exec sp_MSforeachdb
                @command1 = 'select 1 as c',
                @command2 = 'select 2 as c',
                @command3 = 'select 3 as c'
            """);
        HasCount(ForEachDbNames.Length * 3, sets);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, sets.GetRange(0, 3).ConvertAll(s => (int)s.Rows[0][0]!));
    }
}
