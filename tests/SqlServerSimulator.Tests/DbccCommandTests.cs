using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The DBCC maintenance and session commands and <c>CHECKPOINT</c>, probed
/// 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DbccCommandTests
{
    private const string Completed = "DBCC execution completed. If DBCC printed error messages, contact your system administrator.";

    /// <summary>What a batch sent: its messages as <c>number: text</c> and its result sets' rows as <c>a | b</c>, each set headed by its column names.</summary>
    private static (List<string> Messages, List<string> Rows) Run(SimulatedDbConnection connection, string sql)
    {
        var messages = new List<string>();
        void Collect(object? sender, SimulatedInfoMessageEventArgs e)
        {
            foreach (var error in e.Errors)
                messages.Add($"{error.Number}: {error.Message}");
        }
        connection.InfoMessage += Collect;
        var rows = new List<string>();
        try
        {
            using var reader = connection.CreateCommand(sql).ExecuteReader();
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
                while (reader.Read())
                    rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
            while (reader.NextResult());
        }
        finally
        {
            connection.InfoMessage -= Collect;
        }
        return (messages, rows);
    }

    private static (List<string> Messages, List<string> Rows) Run(string sql)
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        return Run(connection, sql);
    }

    private static SimulatedDbConnection Open(Simulation simulation) => (SimulatedDbConnection)simulation.CreateOpenConnection();

    // ---- CHECKPOINT ----

    [TestMethod]
    public void Checkpoint_RunsSilentlyAndResetsRowCount()
    {
        var (messages, rows) = Run("select 1 union select 2; checkpoint 5; select @@rowcount");
        IsEmpty(messages);
        AreEqual("0", rows[^1]);
    }

    [TestMethod]
    public void Checkpoint_DurationMustBeAPositiveIntegerLiteral()
    {
        var simulation = new Simulation();
        simulation.ValidateSyntaxError("checkpoint 0", "0");
        simulation.ValidateSyntaxError("checkpoint 5.0", "5.0");
        simulation.ValidateSyntaxError("checkpoint 2147483648", "2147483648");
        simulation.ValidateSyntaxError("declare @d int = 3; checkpoint @d", "@d");
    }

    [TestMethod]
    public void Checkpoint_WithoutOwnership_IsMsg3505()
        => new Simulation().AssertSqlError(
            "create user u without login; execute as user = 'u'; checkpoint",
            3505,
            "Only the owner of database \"simulated\" or someone with relevant permissions can run the CHECKPOINT statement.");

    [TestMethod]
    public void Checkpoint_BackupOperatorMayRunIt()
        => IsNull(new Simulation().ExecuteScalar("create user u without login; exec sp_addrolemember 'db_backupoperator', 'u'; execute as user = 'u'; checkpoint"));

    // ---- The cache commands ----

    [TestMethod]
    public void FreeProcCache_ReportsCompletionUnlessSilenced()
    {
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("dbcc freeproccache").Messages);
        IsEmpty(Run("dbcc freeproccache with no_infomsgs").Messages);
        IsEmpty(Run("dbcc freeproccache ('default') with no_infomsgs").Messages);
        IsEmpty(Run("dbcc freeproccache (0x0600050000000000000000000000000000000000000000000000000000000000000000000000000000000000) with no_infomsgs").Messages);
    }

    [TestMethod]
    public void FreeProcCache_LeavesRowCountAlone()
        => AreEqual("2", Run("select 1 union select 2; dbcc freeproccache with no_infomsgs; select @@rowcount").Rows[^1]);

    [TestMethod]
    public void FreeProcCache_RejectsBadArguments()
    {
        var simulation = new Simulation();
        AreEqual(9, simulation.AssertSqlError("dbcc freeproccache ('nosuchpool')", 2560).State);
        AreEqual(110, simulation.AssertSqlError("dbcc freeproccache (0x01)", 2560).State);
        AreEqual(9, simulation.AssertSqlError("dbcc freeproccache (1)", 2560).State);
        _ = simulation.AssertSqlError("dbcc freeproccache ('default', 1)", 2583);
        _ = simulation.AssertSqlError("dbcc freeproccache with tableresults", 2532);
    }

    [TestMethod]
    public void CacheCommands_TakeSysadmin()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create user u without login");
        foreach (var (command, state, name) in new[] { ("dbcc freeproccache", 9, "freeproccache"), ("dbcc dropcleanbuffers", 16, "dropcleanbuffers"), ("dbcc freesystemcache('ALL')", 11, "freesystemcache"), ("dbcc freesessioncache", 1, "freesessioncache") })
        {
            var ex = simulation.AssertSqlError($"execute as user = 'u'; {command}", 2571);
            AreEqual(state, ex.State);
            AreEqual($"User 'u' does not have permission to run DBCC {name}.", ex.Errors[0].Message);
        }
    }

    [TestMethod]
    public void FreeSystemCache_KnowsTheStoreNames()
    {
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("dbcc freesystemcache ('all')").Messages);
        IsEmpty(Run("dbcc freesystemcache ('SQL Plans') with mark_in_use_for_removal, no_infomsgs").Messages);
        IsEmpty(Run("dbcc freesystemcache ('simulated') with no_infomsgs").Messages);
        IsEmpty(Run("dbcc freesystemcache ('ObjPerm - simulated', 'default') with no_infomsgs").Messages);
        var simulation = new Simulation();
        AreEqual("Parameter 1 is incorrect for this DBCC statement.", simulation.AssertSqlError("dbcc freesystemcache ('nosuch')", 2560).Errors[0].Message);
        AreEqual("Parameter 2 is incorrect for this DBCC statement.", simulation.AssertSqlError("dbcc freesystemcache ('ALL', 'nosuchpool')", 2560).Errors[0].Message);
        _ = simulation.AssertSqlError("dbcc freesystemcache", 2583);
    }

    [TestMethod]
    public void DropCleanBuffersAndFreeSessionCache_OnlyReport()
    {
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("dbcc dropcleanbuffers").Messages);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("dbcc freesessioncache").Messages);
        IsEmpty(Run("dbcc dropcleanbuffers with no_infomsgs").Messages);
        _ = new Simulation().AssertSqlError("dbcc freesessioncache ('x')", 2583);
    }

    // ---- The statement's shape ----

    [TestMethod]
    public void UnknownSubcommand_IsMsg2526()
        => AreEqual(3, new Simulation().AssertSqlError("dbcc nosuchcommand (1)", 2526).State);

    [TestMethod]
    public void UnbuiltSubcommand_IsNotSupported()
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("dbcc page (0, 1, 0, 0)"));

    [TestMethod]
    public void UnrecognizedWithOption_StopsTheBatchBeforeItRuns()
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError("create table ran (id int); dbcc checkdb with foo", 195);
        AreEqual(4, ex.State);
        AreEqual("'foo' is not a recognized option.", ex.Errors[0].Message);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 'ran'"));
    }

    [TestMethod]
    public void UnderFmtOnly_DbccNeitherRunsNorReports()
        => IsEmpty(Run("set fmtonly on; dbcc useroptions; dbcc freeproccache; set fmtonly off").Rows);

    // ---- USEROPTIONS ----

    [TestMethod]
    public void UserOptions_ListsTheDefaultSession()
    {
        var (messages, rows) = Run("dbcc useroptions");
        CollectionAssert.AreEqual(
            new[]
            {
                "Set Option | Value",
                "textsize | -1", "language | us_english", "dateformat | mdy", "datefirst | 7", "lock_timeout | -1",
                "quoted_identifier | SET", "ansi_null_dflt_on | SET", "ansi_warnings | SET", "ansi_padding | SET", "ansi_nulls | SET",
                "concat_null_yields_null | SET", "isolation level | read committed",
            },
            rows);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);
    }

    [TestMethod]
    public void UserOptions_ReadsTheSessionsSettingsInRealsOrder()
    {
        var (_, rows) = Run("""
            set nocount on; set xact_abort on; set textsize 100; set arithabort on; set arithignore on; set numeric_roundabort on;
            set cursor_close_on_commit on; set ansi_null_dflt_off on; set rowcount 5; set forceplan on; set statistics io on;
            set remote_proc_transactions on; set dateformat dmy; set datefirst 3; set lock_timeout 500;
            set transaction isolation level serializable;
            dbcc useroptions with no_infomsgs
            """);
        CollectionAssert.AreEqual(
            new[]
            {
                "Set Option | Value",
                "textsize | 100", "rowcount | 5", "language | us_english", "dateformat | dmy", "datefirst | 3", "statistics io | SET",
                "lock_timeout | 500", "quoted_identifier | SET", "arithabort | SET", "arithignore | SET", "numeric_roundabort | SET",
                "nocount | SET", "forceplan | SET", "remote_proc_transactions | SET", "ansi_null_dflt_off | SET", "xact_abort | SET",
                "ansi_warnings | SET", "ansi_padding | SET", "ansi_nulls | SET", "concat_null_yields_null | SET",
                "cursor_close_on_commit | SET", "isolation level | serializable",
            },
            rows);
    }

    [TestMethod]
    public void UserOptions_ListsAnsiDefaultsWhileAllSevenAreOn()
        => Contains("ansi_defaults | SET", Run("set ansi_defaults on; dbcc useroptions; set implicit_transactions off; set cursor_close_on_commit off").Rows);

    [TestMethod]
    public void UserOptions_NamesReadCommittedSnapshot()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database current set read_committed_snapshot on");
        using var connection = Open(simulation);
        AreEqual("isolation level | read committed snapshot", Run(connection, "dbcc useroptions").Rows[^1]);
    }

    [TestMethod]
    public void UserOptions_SetsRowCountToItsRows()
        => AreEqual("12", Run("dbcc useroptions with no_infomsgs; select @@rowcount").Rows[^1]);

    [TestMethod]
    public void UserOptions_TakesNoArgumentsOrTableResults()
    {
        _ = new Simulation().AssertSqlError("dbcc useroptions (1)", 2583);
        _ = new Simulation().AssertSqlError("dbcc useroptions with tableresults", 2532);
    }

    // ---- OPENTRAN ----

    [TestMethod]
    public void OpenTran_WithNoWritingTransaction()
    {
        var expected = new[] { "7969: No active open transactions.", "2528: " + Completed };
        CollectionAssert.AreEqual(expected, Run("dbcc opentran").Messages);
        CollectionAssert.AreEqual(expected, Run("create table t (id int); insert t values (1); begin tran; select * from t with (holdlock); dbcc opentran; commit").Messages);
        IsEmpty(Run("dbcc opentran with no_infomsgs").Messages);
    }

    [TestMethod]
    public void OpenTran_ReportsTheOldestWritingTransaction()
    {
        using var connection = Open(new Simulation());
        var (messages, _) = Run(connection, "create table t (id int); begin tran x; insert t values (1); dbcc opentran; commit");
        AreEqual("7968: Transaction information for database 'simulated'.", messages[0]);
        AreEqual("7970: \nOldest active transaction:", messages[1]);
        AreEqual($"7971:     SPID (server process ID): {connection.CreateCommand("select @@spid").ExecuteScalar()}", messages[2]);
        AreEqual("7972:     UID (user ID) : -1", messages[3]);
        AreEqual("7974:     Name          : x", messages[4]);
        StartsWith("7975:     LSN           : (", messages[5]);
        StartsWith("7977:     Start time    : ", messages[6]);
        AreEqual("7978:     SID           : 0x01", messages[7]);
        AreEqual("2528: " + Completed, messages[8]);
    }

    [TestMethod]
    public void OpenTran_TableResultsNameTheColumnForTheDatabase()
    {
        var (_, rows) = Run("create table t (id int); begin tran; update t set id = 2; dbcc opentran with tableresults, no_infomsgs; commit");
        AreEqual("simulated | OPENTRAN", rows[0]);
        CollectionAssert.AreEqual(
            new[] { "OLDACT_SPID", "OLDACT_UID", "OLDACT_NAME", "OLDACT_RECOVERYUNITID", "OLDACT_LSN", "OLDACT_STARTTIME", "OLDACT_SID" },
            rows.Skip(1).Select(row => row.Split(" | ")[0]).ToArray());
        AreEqual("OLDACT_NAME | user_transaction", rows[3]);
    }

    [TestMethod]
    public void OpenTran_NamesImplicitTransactions()
        => Contains("7974:     Name          : implicit_transaction",
            Run("create table t (id int); set implicit_transactions on; insert t values (1); dbcc opentran; commit; set implicit_transactions off").Messages);

    [TestMethod]
    public void OpenTran_CountsDdlAndTempdbWrites()
    {
        Contains("7974:     Name          : user_transaction", Run("begin tran; create table t (id int); dbcc opentran; commit").Messages);
        Contains("7968: Transaction information for database 'tempdb'.", Run("create table #t (id int); begin tran; insert #t values (1); dbcc opentran(tempdb); commit").Messages);
    }

    [TestMethod]
    public void OpenTran_ResolvesItsDatabaseArgument()
    {
        var simulation = new Simulation();
        AreEqual(5, simulation.AssertSqlError("dbcc opentran ('nosuchdb')", 2520).State);
        _ = simulation.AssertSqlError("dbcc opentran (999)", 2521);
        _ = simulation.AssertSqlError("dbcc opentran (-5)", 2560);
    }

    // ---- SQLPERF, LOGINFO ----

    [TestMethod]
    public void SqlPerfLogSpace_ListsEveryDatabase()
    {
        var (_, rows) = Run("dbcc sqlperf(logspace) with no_infomsgs");
        AreEqual("Database Name | Log Size (MB) | Log Space Used (%) | Status", rows[0]);
        StartsWith("master | ", rows[1]);
        StartsWith("tempdb | ", rows[2]);
        Contains("simulated | 7.9921875 | ", rows[5]);
    }

    [TestMethod]
    public void SqlPerf_ClearFormsAndErrors()
    {
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("dbcc sqlperf('sys.dm_os_wait_stats', clear)").Messages);
        var simulation = new Simulation();
        AreEqual(12, simulation.AssertSqlError("dbcc sqlperf(foo)", 2526).State);
        AreEqual(15, simulation.AssertSqlError("dbcc sqlperf('sys.dm_os_wait_stats')", 2526).State);
        _ = simulation.AssertSqlError("dbcc sqlperf", 2583);
        _ = simulation.AssertSqlError("dbcc sqlperf(logspace) with tableresults", 2532);
        _ = simulation.AssertSqlError("create user u without login; execute as user = 'u'; dbcc sqlperf(logspace)", 297);
    }

    [TestMethod]
    public void LogInfo_CutsTheLogIntoFourVirtualFiles()
    {
        var (_, rows) = Run("dbcc loginfo with no_infomsgs");
        AreEqual("RecoveryUnitId | FileId | FileSize | StartOffset | FSeqNo | Status | Parity | CreateLSN", rows[0]);
        HasCount(5, rows);
        StartsWith("0 | 2 | 2031616 | 8192 | ", rows[1]);
        AreEqual("0 | 2 | 2285568 | 6103040 | 0 | 0 | 0 | 0", rows[4]);
    }

    // ---- TRACEON / TRACEOFF / TRACESTATUS ----

    [TestMethod]
    public void TraceStatus_ListsTheSessionsFlags()
    {
        using var connection = Open(new Simulation());
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run(connection, "dbcc tracestatus").Messages);
        IsEmpty(Run(connection, "dbcc tracestatus").Rows);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run(connection, "dbcc traceon(3604, 3605)").Messages);
        CollectionAssert.AreEqual(new[] { "TraceFlag | Status | Global | Session", "3604 | 1 | 0 | 1", "3605 | 1 | 0 | 1" }, Run(connection, "dbcc tracestatus").Rows);
        CollectionAssert.AreEqual(new[] { "TraceFlag | Status | Global | Session", "3606 | 0 | 0 | 0" }, Run(connection, "dbcc tracestatus(3606)").Rows);
        _ = Run(connection, "dbcc traceoff(3604, 3605)");
        IsEmpty(Run(connection, "dbcc tracestatus(-1)").Rows);
    }

    [TestMethod]
    public void TraceOn_MinusOneTurnsAFlagOnServerWide()
    {
        var simulation = new Simulation();
        using (var first = Open(simulation))
            _ = Run(first, "dbcc traceon(3604, -1)");
        using var second = Open(simulation);
        CollectionAssert.AreEqual(new[] { "TraceFlag | Status | Global | Session", "3604 | 1 | 1 | 0" }, Run(second, "dbcc tracestatus").Rows);
    }

    [TestMethod]
    public void TraceOn_RefusesFlagsOutOfRange()
    {
        var simulation = new Simulation();
        AreEqual(17, simulation.AssertSqlError("dbcc traceon(-5)", 2560).State);
        AreEqual(17, simulation.AssertSqlError("dbcc traceon(32767)", 2560).State);
        AreEqual("Parameter 2 is incorrect for this DBCC statement.", simulation.AssertSqlError("dbcc traceon(3604, 1234567)", 2560).Errors[0].Message);
        AreEqual(30, simulation.AssertSqlError("dbcc traceoff(99999)", 2560).State);
        AreEqual(9, simulation.AssertSqlError("dbcc traceon('3604')", 2560).State);
        AreEqual("User 'u' does not have permission to run DBCC TRACEON.",
            simulation.AssertSqlError("create user u without login; execute as user = 'u'; dbcc traceon(3604)", 2571).Errors[0].Message);
    }

    // ---- HELP ----

    [TestMethod]
    public void Help_PrintsASubcommandsSyntax()
        => CollectionAssert.AreEqual(
            new[]
            {
                "0: dbcc CheckIdent \n(\n    'table_name'\n    [ , { NORESEED\n        | { RESEED [ , new_reseed_value ] }\n    } ]\n)\n    [ WITH NO_INFOMSGS ]\n",
                "2528: " + Completed,
            },
            Run("dbcc help('CheckIdent')").Messages);

    [TestMethod]
    public void Help_QuestionMarkListsTheDocumentedSubcommands()
    {
        var messages = Run("dbcc help('?') with no_infomsgs").Messages;
        HasCount(34, messages);
        AreEqual("0: checkalloc\n", messages[0]);
        AreEqual("0: useroptions\n", messages[^1]);
    }

    [TestMethod]
    public void Help_UnknownStatements()
    {
        var simulation = new Simulation();
        AreEqual(1, simulation.AssertSqlError("dbcc help('page')", 8987).State);
        AreEqual("No help available for DBCC statement 'nosuch'.", simulation.AssertSqlError("dbcc help('nosuch')", 8987).Errors[0].Message);
        AreEqual(2, simulation.AssertSqlError("dbcc help('nosuch')", 8987).State);
        _ = simulation.AssertSqlError("dbcc help", 2583);
        _ = simulation.AssertSqlError("dbcc help(1)", 2560);
    }

    // ---- The consistency checks ----

    [TestMethod]
    public void CheckDb_ReportsAHealthyDatabase()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create schema s");
        using var connection = Open(simulation);
        var (messages, _) = Run(connection, "create table s.t (id int primary key); create table [a b] (id int); insert [a b] values (1); dbcc checkdb");
        AreEqual("2536: DBCC results for 'simulated'.", messages[0]);
        AreEqual(8, messages.Count(m => m.StartsWith("8997: Service Broker Msg ", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            new[]
            {
                "2536: DBCC results for 's.t'.",
                "2593: There are 0 rows in 0 pages for object \"s.t\".",
                "2536: DBCC results for 'a b'.",
                "2593: There are 1 rows in 1 pages for object \"a b\".",
                "8989: CHECKDB found 0 allocation errors and 0 consistency errors in database 'simulated'.",
                "2528: " + Completed,
            },
            messages.Skip(9).ToArray());
    }

    [TestMethod]
    public void CheckDb_Options()
    {
        CollectionAssert.AreEqual(
            new[] { "2536: DBCC results for 'simulated'.", "8989: CHECKDB found 0 allocation errors and 0 consistency errors in database 'simulated'.", "2528: " + Completed },
            Run("create table t (id int); dbcc checkdb with physical_only").Messages);
        IsEmpty(Run("dbcc checkdb(0, noindex) with no_infomsgs, tablock, data_purity, maxdop = 1").Messages);
        CollectionAssert.AreEqual(
            new[] { "5281: Estimated TEMPDB space (in KB) needed for CHECKDB on database simulated = 2435." },
            Run("dbcc checkdb with estimateonly, no_infomsgs").Messages);
        Contains("7966: Warning: NO_INDEX option of checkdb being used. Checks on non-system indexes will be skipped.", Run("dbcc checkdb('simulated', noindex)").Messages);
        AreEqual(5, new Simulation().AssertSqlError("dbcc checkdb with physical_only, extended_logical_checks", 2532).State);
        _ = new Simulation().AssertSqlError("dbcc checkdb with count_rows", 2532);
        _ = new Simulation().AssertSqlError("dbcc checkdb(0, 1)", 2560);
        _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("dbcc checkdb(0, repair_rebuild)"));
    }

    [TestMethod]
    public void CheckDb_TableResultsReturnTheLinesAsRows()
    {
        var (messages, rows) = Run("create table t (id int primary key); insert t values (1); dbcc checkdb with tableresults");
        HasCount(11, rows);
        StartsWith("8997 | 10 | 1 | Service Broker Msg 9675, State 1: Message Types analyzed: 14. | NULL | 0 | ", rows[1]);
        StartsWith("2593 | 10 | 1 | There are 1 rows in 1 pages for object \"t\". | NULL | 0 | ", rows[9]);
        EndsWith(" | NULL | 0 | 5 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1", rows[10]);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);
    }

    [TestMethod]
    public void CheckTable_ReportsTheTable()
    {
        CollectionAssert.AreEqual(
            new[] { "2536: DBCC results for 't'.", "2593: There are 2 rows in 1 pages for object \"t\".", "2528: " + Completed },
            Run("create table t (id int primary key, v varchar(10)); insert t values (1, 'a'), (2, 'b'); dbcc checktable(t)").Messages);
        IsEmpty(Run("create table t (id int); dbcc checktable('t') with no_infomsgs").Messages);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("create table t (id int); dbcc checktable(t) with physical_only").Messages);
        StartsWith("2536: DBCC results for '#t___", Run("create table #t (id int); dbcc checktable(#t)").Messages[0]);
    }

    [TestMethod]
    public void CheckTable_Errors()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int)", "create view v as select id from t", "create user u without login");
        AreEqual("Cannot find a table or object with the name \"nosuch\". Check the system catalog.", simulation.AssertSqlError("dbcc checktable(nosuch)", 2501).Errors[0].Message);
        StartsWith("Unable to process object ID ", simulation.AssertSqlError("dbcc checktable(v)", 5239).Errors[0].Message);
        _ = simulation.AssertSqlError("dbcc checktable(12345)", 2573);
        AreEqual("User 'u' does not have permission to run DBCC checktable for object 't'.",
            simulation.AssertSqlError("execute as user = 'u'; dbcc checktable(t)", 2557).Errors[0].Message);
    }

    [TestMethod]
    public void CheckAllocCatalogAndFilegroup()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "2536: DBCC results for 'simulated'.",
                "2538: File 1. The number of extents = 1, used pages = 1, and reserved pages = 8.",
                "8915:            File 1 (number of mixed extents = 0, mixed pages = 0).",
                "2539: The total number of extents = 1, used pages = 1, and reserved pages = 8 in this database.",
                "8918:        (number of mixed extents = 0, mixed pages = 0) in this database.",
                "8989: CHECKALLOC found 0 allocation errors and 0 consistency errors in database 'simulated'.",
                "2528: " + Completed,
            },
            Run("create table t (id int); insert t values (1); dbcc checkalloc").Messages);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("dbcc checkcatalog").Messages);
        _ = new Simulation().AssertSqlError("dbcc checkcatalog with tableresults", 2532);
        CollectionAssert.AreEqual(
            new[]
            {
                "2536: DBCC results for 'simulated'.",
                "2536: DBCC results for 't'.",
                "2593: There are 1 rows in 1 pages for object \"t\".",
                "8989: CHECKFILEGROUP found 0 allocation errors and 0 consistency errors in database 'simulated'.",
                "2528: " + Completed,
            },
            Run("create table t (id int primary key); insert t values (1); dbcc checkfilegroup('PRIMARY')").Messages);
        AreEqual("The filegroup \"nosuch\" is not part of database \"simulated\".", new Simulation().AssertSqlError("dbcc checkfilegroup('nosuch')", 3027).Errors[0].Message);
    }

    [TestMethod]
    public void DatabaseChecks_TakeDbOwner()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create user u without login");
        foreach (var command in new[] { "checkdb", "checkalloc", "checkcatalog", "checkfilegroup", "checkconstraints", "opentran" })
        {
            AreEqual($"User 'u' does not have permission to run DBCC {command} for database 'simulated'.",
                simulation.AssertSqlError($"execute as user = 'u'; dbcc {command}", 7983).Errors[0].Message);
        }
    }

    [TestMethod]
    public void DatabaseArguments_Resolve()
    {
        var simulation = new Simulation();
        AreEqual(5, simulation.AssertSqlError("dbcc checkdb('nosuchdb')", 2520).State);
        AreEqual(5, simulation.AssertSqlError("dbcc checkalloc(nosuchdb)", 2520).State);
        _ = simulation.AssertSqlError("dbcc checkdb(999)", 2521);
        _ = simulation.AssertSqlError("dbcc checkdb(null)", 2560);
        IsEmpty(Run("dbcc checkdb(master) with no_infomsgs").Messages);
        IsEmpty(Run("dbcc checkdb(1) with no_infomsgs").Messages);
    }

    // ---- CHECKCONSTRAINTS ----

    [TestMethod]
    public void CheckConstraints_SkipsDisabledConstraintsUnlessAsked()
    {
        const string Setup = "create table t (id int primary key, v int constraint ck_v check (v > 0)); alter table t nocheck constraint ck_v; insert t values (2, -1), (3, -5);";
        var (messages, rows) = Run(Setup + "dbcc checkconstraints");
        IsEmpty(rows);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);
        CollectionAssert.AreEqual(
            new[] { "Table | Constraint | Where", "[dbo].[t] | [ck_v] | [v] = '-1'", "[dbo].[t] | [ck_v] | [v] = '-5'" },
            Run(Setup + "dbcc checkconstraints(t) with all_constraints").Rows);
        CollectionAssert.AreEqual(
            new[] { "Table | Constraint | Where", "[dbo].[t] | [ck_v] | [v] = '-1'", "[dbo].[t] | [ck_v] | [v] = '-5'" },
            Run(Setup + "dbcc checkconstraints(ck_v)").Rows);
    }

    [TestMethod]
    public void CheckConstraints_ChecksAnEnabledUntrustedConstraint()
        => CollectionAssert.AreEqual(
            new[] { "Table | Constraint | Where", "[dbo].[t] | [ck_v] | [v] = '-1'" },
            Run("create table t (id int primary key, v int constraint ck_v check (v > 0)); alter table t nocheck constraint ck_v; insert t values (2, -1); alter table t check constraint ck_v; dbcc checkconstraints").Rows);

    [TestMethod]
    public void CheckConstraints_ReportsForeignKeyOrphans()
        => CollectionAssert.AreEqual(
            new[] { "Table | Constraint | Where", "[dbo].[c] | [fk_c] | [x] = '1' AND [y] = '2'" },
            Run("""
                create table p (a int, b int, primary key (a, b));
                create table c (id int primary key, x int, y int, constraint fk_c foreign key (x, y) references p(a, b));
                alter table c nocheck constraint fk_c; insert c values (10, 1, 2), (11, 3, null);
                dbcc checkconstraints(c) with all_constraints
                """).Rows);

    [TestMethod]
    public void CheckConstraints_ReportsEachRowAgainstItsFirstViolatedConstraint()
        => CollectionAssert.AreEqual(
            new[] { "Table | Constraint | Where", "[dbo].[t] | [ck_a] | [v] = '-1'", "[dbo].[t] | [ck_z] | [x] = '-2'", "[dbo].[t] | [ck_z] | [x] = '-3'" },
            Run("""
                create table t (id int primary key, v int, x int, constraint ck_z check (x > 0), constraint ck_a check (v > 0));
                alter table t nocheck constraint all; insert t values (1, -1, 1), (2, 1, -2), (3, -3, -3);
                dbcc checkconstraints(t) with all_constraints
                """).Rows);

    [TestMethod]
    public void CheckConstraints_ForeignKeysAndChecksAreSeparateGroups()
        => CollectionAssert.AreEqual(
            new[] { "Table | Constraint | Where", "[dbo].[c] | [fk_c] | [pid] = '5'", "[dbo].[c] | [fk_c] | [pid] = '6'", "[dbo].[c] | [ck_c] | [v] = '-1'" },
            Run("""
                create table p (id int primary key);
                create table c (id int primary key, pid int constraint fk_c foreign key references p(id), v int constraint ck_c check (v > 0));
                alter table c nocheck constraint all; insert c values (1, 5, -1), (2, 6, 1);
                dbcc checkconstraints(c) with all_constraints
                """).Rows);

    [TestMethod]
    public void CheckConstraints_RendersValuesAsRealDoes()
    {
        var rows = Run("""
            create table t (a int, dt datetime, f float, m money, b varbinary(4), c char(5), s nvarchar(10),
                constraint ck check (a is null and dt is null and f is null and m is null and b is null and c is null and s is null));
            alter table t nocheck constraint ck;
            insert t values (1, '2020-01-02 03:04:05.123', 1.5e10, 12.3456, 0x0A0B, 'xy', N'it''s'), (null, null, null, null, null, null, null);
            insert t (a) values (2);
            dbcc checkconstraints(t) with all_constraints
            """).Rows;
        CollectionAssert.AreEqual(
            new[]
            {
                "Table | Constraint | Where",
                "[dbo].[t] | [ck] | NULL",
                "[dbo].[t] | [ck] | [a] = '1' AND [dt] = '2020-01-02 03:04:05.123' AND [f] = '1.5e+010' AND [m] = '12.35' AND [b] = '0x0A0B' AND [c] = 'xy' AND [s] = 'it''s'",
            },
            rows);
    }

    [TestMethod]
    public void CheckConstraints_SortsDistinctTextAndCapsAt200()
    {
        const string Setup = "create table t (id int primary key, v int constraint ck_v check (v > 0)); alter table t nocheck constraint ck_v; insert t select value, -value from generate_series(1, 205); insert t values (1000, -1);";
        var rows = Run(Setup + "dbcc checkconstraints(t) with all_constraints").Rows;
        HasCount(201, rows);
        AreEqual("[dbo].[t] | [ck_v] | [v] = '-1'", rows[1]);
        AreEqual("[dbo].[t] | [ck_v] | [v] = '-10'", rows[2]);
        HasCount(206, Run(Setup + "dbcc checkconstraints(t) with all_constraints, all_errormsgs").Rows);
    }

    [TestMethod]
    public void CheckConstraints_SizesItsNameColumnsToTheirValues()
    {
        using var connection = Open(new Simulation());
        _ = connection.CreateCommand("create table longer_name (id int, w int constraint ck_long_name check (w > 0)); alter table longer_name nocheck constraint all; insert longer_name values (1, -1)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("dbcc checkconstraints with all_constraints, no_infomsgs").ExecuteReader();
        var schema = reader.GetSchemaTable()!;
        AreEqual(20, schema.Rows[0]["ColumnSize"]);
        AreEqual(15, schema.Rows[1]["ColumnSize"]);
    }

    [TestMethod]
    public void CheckConstraints_ResetsRowCount()
        => AreEqual("0", Run("create table t (id int check (id > 0)); insert t values (1); select 1 union select 2; dbcc checkconstraints(t); select @@rowcount").Rows[^1]);

    [TestMethod]
    public void CheckConstraints_Errors()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int)", "create view v as select id from t");
        _ = simulation.AssertSqlError("dbcc checkconstraints(nosuch)", 2501);
        _ = simulation.AssertSqlError("create table #t (v int check (v > 0)); dbcc checkconstraints(#t)", 2501);
        _ = simulation.AssertSqlError("dbcc checkconstraints(v)", 5239);
        _ = simulation.AssertSqlError("dbcc checkconstraints(t, t)", 2583);
        _ = simulation.AssertSqlError("dbcc checkconstraints with tableresults", 2532);
    }

    // ---- Table maintenance ----

    [TestMethod]
    public void UpdateUsageAndCleanTable_OnlyReport()
    {
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("create table t (id int constraint pk primary key); dbcc updateusage(0, 't', 'pk') with count_rows").Messages);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run("create table t (id int, v varchar(max)); dbcc cleantable(0, 't')").Messages);
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int constraint pk primary key)");
        _ = simulation.AssertSqlError("dbcc updateusage", 2583);
        _ = simulation.AssertSqlError("dbcc updateusage(0, nosuch)", 2501);
        AreEqual(8, simulation.AssertSqlError("dbcc updateusage(0, 't', 'nosuch')", 7999).State);
        _ = simulation.AssertSqlError("dbcc cleantable(0)", 2583);
        AreEqual("The DBCC permission was denied on the object 't', database 'simulated', schema 'dbo'.",
            simulation.AssertSqlError("create user u without login; execute as user = 'u'; dbcc cleantable(0, t)", 229).Errors[0].Message);
    }

    [TestMethod]
    public void DbReindex_ReadsTheTablesRows()
    {
        AreEqual("4", Run("create table t (id int primary key); insert t values (1), (2), (3), (4); select 1; dbcc dbreindex(t, '', 80) with no_infomsgs; select @@rowcount").Rows[^1]);
        AreEqual("Could not find any index named 'nosuch' for table 't'.", new Simulation().AssertSqlError("create table t (id int primary key); dbcc dbreindex('t', 'nosuch')", 7999).Errors[0].Message);
    }

    [TestMethod]
    public void IndexDefrag_ReportsTheIndexes()
    {
        CollectionAssert.AreEqual(
            new[] { "Index Name | Pages Scanned | Pages Moved | Pages Removed", "pk | 1 | 0 | 0", "ix | 1 | 0 | 0" },
            Run("create table t (id int constraint pk primary key, v int); create index ix on t(v); insert t values (1, 1); dbcc indexdefrag(0, t)").Rows);
        CollectionAssert.AreEqual(
            new[] { "Pages Scanned | Pages Moved | Pages Removed", "0 | 0 | 0" },
            Run("create table t (id int constraint pk primary key); dbcc indexdefrag(0, 't', 'pk')").Rows);
        CollectionAssert.AreEqual(new[] { "Index Name | Pages Scanned | Pages Moved | Pages Removed", "NULL | 0 | 0 | 0" }, Run("create table t (id int); dbcc indexdefrag(0, t)").Rows);
        IsEmpty(Run("create table t (id int primary key); dbcc indexdefrag(0, t) with no_infomsgs").Rows);
        _ = new Simulation().AssertSqlError("create table t (id int primary key); begin tran; dbcc indexdefrag(0, t)", 8920);
        AreEqual(8, new Simulation().AssertSqlError("create table t (id int constraint pk primary key); dbcc indexdefrag(0, 't', 'nosuch')", 7999).State);
    }

    // ---- The pre-existing subcommands' messages ----

    [TestMethod]
    public void ShrinkDatabase_ReportsTheSkippedFiles()
        => CollectionAssert.AreEqual(
            new[]
            {
                "5201: DBCC SHRINKDATABASE: File ID 1 of database ID 5 was skipped because the file does not have enough free space to reclaim.",
                "5201: DBCC SHRINKDATABASE: File ID 2 of database ID 5 was skipped because the file does not have enough free space to reclaim.",
                "2528: " + Completed,
            },
            Run("dbcc shrinkdatabase(0)").Messages);

    [TestMethod]
    public void ShrinkFile_NoInfoMessagesSilencesTheRow()
    {
        var (messages, rows) = Run("dbcc shrinkfile(1)");
        HasCount(2, rows);
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, messages);
        IsEmpty(Run("dbcc shrinkfile(1) with no_infomsgs").Rows);
    }

    [TestMethod]
    public void TraceOnAndCheckIdent_LeaveRowCountAlone()
    {
        AreEqual("2", Run("select 1 union select 2; dbcc traceon(3604); select @@rowcount").Rows[^1]);
        AreEqual("2", Run("create table t (id int identity); select 1 union select 2; dbcc checkident(t) with no_infomsgs; select @@rowcount").Rows[^1]);
    }

    [TestMethod]
    public void CheckTable_UnderExecuteAsUserIsMsg916UnlessTabLock()
    {
        const string Setup = "create table t (id int primary key); create user u without login; alter role db_owner add member u; execute as user = 'u'; ";
        var error = new Simulation().AssertSqlError(Setup + "dbcc checktable(t) with no_infomsgs; select 'unreached'", 916);
        MatchesRegex(new System.Text.RegularExpressions.Regex(@"^The server principal ""S-1-9-3-\d+-\d+-\d+-\d+"" is not able to access the database ""simulated"" under the current security context\.$"), error.Errors[0].Message);
        AreEqual(2, error.Errors[0].State);
        AreEqual("ran", new Simulation().ExecuteScalar(Setup + "dbcc checktable(t) with tablock, no_infomsgs; dbcc checkdb with no_infomsgs; select 'ran'"));
        AreEqual(916, new Simulation().ExecuteScalar(Setup + "begin try dbcc checktable(t); end try begin catch select error_number(); end catch"));
    }

    /// <summary>FLUSHAUTHCACHE, PINTABLE and UNPINTABLE (probed 2026-09-30 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("dbcc flushauthcache")]
    [DataRow("dbcc pintable(1, 1)")]
    [DataRow("dbcc unpintable(999, 99999)")]
    public void FlushAuthCacheAndPinTable_CompleteQuietly(string command)
    {
        CollectionAssert.AreEqual(new[] { "2528: " + Completed }, Run(command).Messages);
    }

    [TestMethod]
    [DataRow("dbcc flushauthcache(1)", 2583)]
    [DataRow("dbcc flushauthcache with no_infomsgs", 2532)]
    [DataRow("dbcc pintable(1)", 2583)]
    [DataRow("dbcc pintable(1, 1, 1)", 2583)]
    [DataRow("dbcc pintable('a', 'b')", 2560)]
    [DataRow("dbcc unpintable(1, 1) with no_infomsgs", 2532)]
    public void FlushAuthCacheAndPinTable_RefuseBadShapes(string command, int number)
        => _ = new Simulation().AssertSqlError(command, number);

    [TestMethod]
    [DataRow("dbcc flushauthcache", "flushauthcache")]
    [DataRow("dbcc pintable(1, 1)", "pintable")]
    [DataRow("dbcc unpintable(1, 1)", "unpintable")]
    public void FlushAuthCacheAndPinTable_TakeSysadmin(string command, string name)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create user u without login");
        var ex = simulation.AssertSqlError($"execute as user = 'u'; {command}", 2571);
        AreEqual($"User 'u' does not have permission to run DBCC {name}.", ex.Errors[0].Message);
    }
}
