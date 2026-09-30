using System.Security.Cryptography;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Query Store capture: which statements a store records and under what text,
/// the capture modes and states, runtime statistics, and the
/// <c>sp_query_store_*</c> procedures. Probed against SQL Server 2025
/// (2026-09-29).
/// </summary>
[TestClass]
public sealed class QueryStoreCaptureTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string CaptureAll = "alter database simulated set query_store (query_capture_mode = all)";

    private const string Table = "create table t (a int primary key, b int, c varchar(10)); insert t values (1, 2, 'x'), (3, 4, 'y')";

    /// <summary>A simulation whose store captures every query, over a two-row table <c>t</c>.</summary>
    private static Simulation CapturingAll()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CaptureAll);
        _ = sim.ExecuteNonQuery(Table);
        return sim;
    }

    /// <summary>Each captured text with its executions, summed over its runtime-statistics rows, in capture order.</summary>
    private static List<(string Text, long Executions)> Captured(Simulation sim)
    {
        var captured = new List<(string, long)>();
        using var reader = sim.ExecuteReader("""
            select qt.query_sql_text, isnull(sum(rs.count_executions), 0)
            from sys.query_store_query q
            join sys.query_store_query_text qt on qt.query_text_id = q.query_text_id
            left join sys.query_store_plan p on p.query_id = q.query_id
            left join sys.query_store_runtime_stats rs on rs.plan_id = p.plan_id
            where qt.query_sql_text not like '%query[_]store%' and qt.query_sql_text not like '%query[_]context%'
            group by q.query_id, qt.query_sql_text
            order by q.query_id
            """);
        while (reader.Read())
            captured.Add((reader.GetString(0), reader.GetInt64(1)));
        return captured;
    }

    private static long Executions(Simulation sim, string text) => Captured(sim).Where(c => c.Text == text).Sum(c => c.Executions);

    [TestMethod]
    public void All_AdHocSelect_IsCapturedAsWritten_WithEveryExecution()
    {
        var sim = CapturingAll();
        for (var i = 0; i < 3; i++)
            _ = sim.ExecuteScalar("select b from t;");
        AreEqual(3L, Executions(sim, "select b from t"));
        AreEqual((byte)0, sim.ExecuteScalar("select query_parameterization_type from sys.query_store_query q join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text = 'select b from t'"));
        AreEqual(2.0, sim.ExecuteScalar("""
            select rs.avg_rowcount from sys.query_store_runtime_stats rs
            join sys.query_store_plan p on p.plan_id = rs.plan_id
            join sys.query_store_query q on q.query_id = p.query_id
            join sys.query_store_query_text t on t.query_text_id = q.query_text_id
            where t.query_sql_text = 'select b from t'
            """));
    }

    [TestMethod]
    [DataRow("select * from t where a = 1", "(@1 tinyint)SELECT * FROM [t] WHERE [a]=@1")]
    [DataRow("select a as x, b + 1 y from t where a = 1 and b = 2", "(@1 tinyint,@2 tinyint)SELECT [a] [x],[b]+(1) [y] FROM [t] WHERE [a]=@1 AND [b]=@2")]
    [DataRow("select * from t where a between 1 and 3", "(@1 tinyint,@2 tinyint)SELECT * FROM [t] WHERE [a]>=@1 AND [a]<=@2")]
    [DataRow("select * from dbo.t x where x.a = -5 order by b desc, c", "(@1 smallint)SELECT * FROM [dbo].[t] [x] WHERE [x].[a]=@1 ORDER BY [b] DESC,[c] ASC")]
    [DataRow("select * from t with (nolock) where c = 'q' and a = 100000", "(@1 varchar(8000),@2 int)SELECT * FROM [t] WITH(nolock)  WHERE [c]=@1 AND [a]=@2")]
    [DataRow("select * from t where a = b + 1", "(@1 int)SELECT * FROM [t] WHERE [a]=([b]+@1)")]
    [DataRow("update t set b = 1, c = c where a = 2", "(@2 tinyint,@1 int)UPDATE [t] set [b] = @1,[c] = [c]  WHERE [a]=@2")]
    [DataRow("delete from t where a = 1.5", "(@1 numeric(2,1))DELETE [t]  WHERE [a]=@1")]
    [DataRow("insert into t (a, c) values (7, 'z')", "(@1 int,@2 varchar(8000))INSERT INTO [t]([a],[c]) values(@1,@2)")]
    [DataRow("insert t values (8, null, N'w'), (9, 1, 'v')", "(@1 int,@2 nvarchar(4000),@3 int,@4 int,@5 varchar(8000))INSERT INTO [t] values(@1,NULL,@2),(@3,@4,@5)")]
    [DataRow("select * from t where a <> 1", "select * from t where a <> 1")]
    [DataRow("select * from t where not a = 1", "select * from t where not a = 1")]
    [DataRow("select name from sys.objects where name = 't'", "select name from sys.objects where name = 't'")]
    [DataRow("select top 1 a from t where a > 0 order by a", "select top 1 a from t where a > 0 order by a")]
    public void SimpleParameterization_StoresRealsParameterizedText(string statement, string expected)
    {
        var sim = CapturingAll();
        _ = sim.ExecuteNonQuery(statement);
        Contains(expected, [.. Captured(sim).Select(c => c.Text)]);
    }

    [TestMethod]
    public void SimpleParameterization_LiteralVariants_ShareOneQuery()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select * from t where a = 1");
        _ = sim.ExecuteScalar("SELECT *   FROM t WHERE a=3");
        AreEqual(2L, Executions(sim, "(@1 tinyint)SELECT * FROM [t] WHERE [a]=@1"));
        AreEqual("Simple", sim.ExecuteScalar("select query_parameterization_type_desc from sys.query_store_query q join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text like '%SELECT * FROM%'"));
    }

    [TestMethod]
    public void Variables_PrefixTheirDeclarations_InOrderOfFirstUse()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteNonQuery("""
            declare @X int = (select count(*) from t);
            declare @s varchar(10) = 'x', @d decimal(5,2) = 1;
            select a from t where c = @s and b = @x;
            select a from t where @d > 0;
            """);
        List<string> texts = [.. Captured(sim).Select(c => c.Text)];
        Contains("(@X int)declare @X int = (select count(*) from t)", texts);
        Contains("(@s varchar(10),@X int)select a from t where c = @s and b = @x", texts);
        Contains("(@d decimal(5,2))select a from t where @d > 0", texts);
    }

    [TestMethod]
    public void ParameterizedCommand_IsUserParameterized_ReferencedParametersOnly()
    {
        var sim = CapturingAll();
        using var connection = sim.CreateOpenConnection();
        for (var i = 1; i <= 2; i++)
        {
            using var command = connection.CreateCommand("select b from t where a = @p", ("@p", i), ("@unused", 5));
            _ = command.ExecuteScalar();
        }
        AreEqual(2L, Executions(sim, "(@p int)select b from t where a = @p"));
        AreEqual("User", sim.ExecuteScalar("select query_parameterization_type_desc from sys.query_store_query q join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text like '(@p int)%'"));
    }

    [TestMethod]
    public void Uncaptured_StatementsWithoutAPlan()
    {
        var sim = CapturingAll();
        var before = Captured(sim).Count;
        _ = sim.ExecuteNonQuery("""
            declare @x int = 5;
            select @x + 1;
            select 1;
            set @x = 2;
            if @x > 1 print 'x';
            exec('select 1');
            create table u (a int);
            """);
        HasCount(before, Captured(sim));
    }

    [TestMethod]
    public void Conditions_SelectInto_Merge_AndTableVariables_AreCaptured()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteNonQuery("""
            if exists (select * from t where b > 100) print 'x';
            select a into #tmp from t;
            merge t using (select 1 a) s on t.a = s.a when matched then update set b = 9;
            declare @v table (x int); insert @v select a from t;
            """);
        List<string> texts = [.. Captured(sim).Select(c => c.Text)];
        Contains("if exists (select * from t where b > 100)", texts);
        Contains("select a into #tmp from t", texts);
        Contains("merge t using (select 1 a) s on t.a = s.a when matched then update set b = 9;", texts);
        Contains("insert @v select a from t", texts);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.query_store_query where batch_sql_handle is not null"));
    }

    [TestMethod]
    public void ProcedureBody_CarriesTheProcedure_AndNoDefaultSchema()
    {
        var sim = CapturingAll();
        sim.ExecuteBatches(
            "create proc p @v int as select b from t where a = @v",
            "exec p 1; exec p 3");
        AreEqual(2L, Executions(sim, "(@v int)select b from t where a = @v"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.query_store_query where object_id = object_id('p')"));
        AreEqual(-2, sim.ExecuteScalar("select c.default_schema_id from sys.query_store_query q join sys.query_context_settings c on c.context_settings_id = q.context_settings_id where q.object_id = object_id('p')"));
        _ = sim.ExecuteScalar("select b from t");
        AreEqual(1, sim.ExecuteScalar("select c.default_schema_id from sys.query_store_query q join sys.query_context_settings c on c.context_settings_id = q.context_settings_id join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text = 'select b from t'"));
    }

    [TestMethod]
    public void RuntimeError_RecordsAnException_CompileErrorNothing()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteNonQuery("begin try select 1 / (b - 2) from t; end try begin catch end catch");
        _ = sim.AssertSqlError("select a, nosuch from t", 207);
        using var reader = sim.ExecuteReader("select rs.execution_type, rs.execution_type_desc, rs.count_executions from sys.query_store_runtime_stats rs join sys.query_store_plan p on p.plan_id = rs.plan_id join sys.query_store_query q on q.query_id = p.query_id join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text = 'select 1 / (b - 2) from t'");
        IsTrue(reader.Read());
        AreEqual((byte)4, reader.GetByte(0));
        AreEqual("Exception", reader.GetString(1));
        AreEqual(1L, reader.GetInt64(2));
        IsFalse(Captured(sim).Exists(c => c.Text.Contains("nosuch", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Auto_CapturesAtTheThirtiethExecution()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Table);
        for (var i = 0; i < 29; i++)
            _ = sim.ExecuteScalar("select b from t");
        IsEmpty(Captured(sim));
        for (var i = 0; i < 13; i++)
            _ = sim.ExecuteScalar("select b from t");
        AreEqual(13L, Executions(sim, "select b from t"));
    }

    [TestMethod]
    public void Custom_CapturesAtItsExecutionCount()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store (query_capture_mode = custom, query_capture_policy = (execution_count = 3))");
        _ = sim.ExecuteNonQuery(Table);
        for (var i = 0; i < 8; i++)
            _ = sim.ExecuteScalar("select b, a from t");
        AreEqual(6L, Executions(sim, "select b, a from t"));
    }

    [TestMethod]
    public void None_KeepsCountingCapturedQueries_AndCapturesNoNewOnes()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteNonQuery("alter database simulated set query_store (query_capture_mode = none)");
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteScalar("select a from t");
        AreEqual(2L, Executions(sim, "select b from t"));
        IsFalse(Captured(sim).Exists(c => c.Text == "select a from t"));
    }

    [TestMethod]
    public void ReadOnlyAndOff_StopCapture_AndKeepWhatWasCaptured()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteNonQuery("alter database simulated set query_store (operation_mode = read_only)");
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteScalar("select a from t");
        AreEqual(1L, Executions(sim, "select b from t"));
        _ = sim.ExecuteNonQuery("alter database simulated set query_store = off");
        _ = sim.ExecuteScalar("select b from t");
        AreEqual(1L, Executions(sim, "select b from t"));
        AreEqual(1L, sim.ExecuteScalar("select current_storage_size_mb from sys.database_query_store_options"));
    }

    [TestMethod]
    public void OptionsBlockWithoutOn_LeavesAnOffStoreOff()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store = off");
        _ = sim.ExecuteNonQuery("alter database simulated set query_store (query_capture_mode = all)");
        AreEqual("OFF", sim.ExecuteScalar("select actual_state_desc from sys.database_query_store_options"));
        AreEqual("ALL", sim.ExecuteScalar("select query_capture_mode_desc from sys.database_query_store_options"));
    }

    [TestMethod]
    public void Clear_ForgetsEverything_AndRestartsIds()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteNonQuery("alter database simulated set query_store clear");
        AreEqual(0L, sim.ExecuteScalar("select current_storage_size_mb from sys.database_query_store_options"));
        _ = sim.ExecuteScalar("select a from t");
        AreEqual(1L, sim.ExecuteScalar("select min(query_id) from sys.query_store_query"));
    }

    [TestMethod]
    public void PlanCacheReplay_RecordsEveryExecution()
    {
        var sim = CapturingAll();
        using var connection = sim.CreateOpenConnection();
        for (var i = 0; i < 4; i++)
        {
            using var command = connection.CreateCommand("select a, b from t");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
            }
        }
        AreEqual(4L, Executions(sim, "select a, b from t"));
    }

    [TestMethod]
    public void StatementSqlHandle_IsTheMd5OfTheText()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        var expected = new byte[44];
        expected[0] = 9;
        MD5.HashData(Encoding.Unicode.GetBytes("select b from t")).CopyTo(expected, 2);
        CollectionAssert.AreEqual(expected, (byte[])sim.ExecuteScalar("select statement_sql_handle from sys.query_store_query_text where query_sql_text = 'select b from t'")!);
    }

    [TestMethod]
    public void ContextSettings_ReflectTheSessionsOptions()
    {
        var sim = CapturingAll();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("select b from t").ExecuteScalar();
        _ = connection.CreateCommand("set arithabort on; set dateformat dmy; select b from t").ExecuteScalar();
        using var reader = sim.ExecuteReader("select set_options, date_format, date_first, language_id from sys.query_context_settings order by context_settings_id");
        IsTrue(reader.Read());
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0xFB }, (byte[])reader.GetValue(0));
        AreEqual((short)1, reader.GetInt16(1));
        AreEqual((byte)7, reader.GetByte(2));
        AreEqual((short)0, reader.GetInt16(3));
        IsTrue(reader.Read());
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0x10, 0xFB }, (byte[])reader.GetValue(0));
        AreEqual((short)2, reader.GetInt16(1));
    }

    [TestMethod]
    public void Interval_IsAlignedToItsLength()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store (query_capture_mode = all, interval_length_minutes = 15)");
        _ = sim.ExecuteNonQuery(Table);
        using var reader = sim.ExecuteReader("select datediff(minute, start_time, end_time), datepart(minute, start_time) % 15, datepart(second, start_time) from sys.query_store_runtime_stats_interval");
        IsTrue(reader.Read());
        AreEqual(15, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
        AreEqual(0, reader.GetInt32(2));
    }

    [TestMethod]
    public void ForcePlan_MarksThePlan_AndUnforceClearsIt()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        var ids = "select q.query_id, p.plan_id from sys.query_store_query q join sys.query_store_plan p on p.query_id = q.query_id join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text = 'select b from t'";
        _ = sim.ExecuteNonQuery($"declare @q bigint, @p bigint; select @q = query_id, @p = plan_id from ({ids}) x; exec sp_query_store_force_plan @q, @p; exec sp_query_store_force_plan @q, @p");
        AreEqual("MANUAL", sim.ExecuteScalar("select plan_forcing_type_desc from sys.query_store_plan where is_forced_plan = 1"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.query_store_plan_forcing_locations"));
        _ = sim.ExecuteNonQuery($"declare @q bigint, @p bigint; select @q = query_id, @p = plan_id from ({ids}) x; exec sp_query_store_unforce_plan @q, @p");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.query_store_plan where is_forced_plan = 1"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.query_store_plan_forcing_locations"));
    }

    [TestMethod]
    public void Hints_SetReplaces_ClearRemoves()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteNonQuery("exec sp_query_store_set_hints 1, N'OPTION (MAXDOP 1)'");
        _ = sim.ExecuteNonQuery("exec sp_query_store_set_hints 1, N'OPTION (RECOMPILE, MAXDOP 2)'");
        using (var reader = sim.ExecuteReader("select query_hint_id, query_hint_text, source_desc from sys.query_store_query_hints"))
        {
            IsTrue(reader.Read());
            AreEqual(2L, reader.GetInt64(0));
            AreEqual("OPTION (RECOMPILE, MAXDOP 2)", reader.GetString(1));
            AreEqual("User", reader.GetString(2));
            IsFalse(reader.Read());
        }
        _ = sim.ExecuteNonQuery("exec sp_query_store_clear_hints 1; exec sp_query_store_clear_hints 1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.query_store_query_hints"));
    }

    [TestMethod]
    [DataRow("exec sp_query_store_set_hints 1, N'OPTION (BOGUS)'", "Incorrect syntax near 'BOGUS'.")]
    [DataRow("exec sp_query_store_set_hints 1, N'MAXDOP 1'", "Incorrect syntax near 'MAXDOP'.")]
    public void SetHints_RefusesAClauseThatIsNoOptionClause(string call, string message)
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        sim.AssertSqlError(call, 102, message);
    }

    [TestMethod]
    public void RemovePlan_KeepsTheQuery_ResetDropsStatistics_RemoveQueryDropsAll()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        _ = sim.ExecuteScalar("select a from t");
        _ = sim.ExecuteNonQuery("exec sp_query_store_reset_exec_stats 2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.query_store_runtime_stats where plan_id = 2"));
        _ = sim.ExecuteNonQuery("exec sp_query_store_remove_plan 2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.query_store_plan where plan_id = 2"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.query_store_query where query_id = 2"));
        _ = sim.ExecuteNonQuery("exec sp_query_store_remove_query 2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.query_store_query where query_id = 2"));
    }

    [TestMethod]
    [DataRow("exec sp_query_store_force_plan 99, 1", 12402, (byte)2)]
    [DataRow("exec sp_query_store_unforce_plan 99, 1", 12402, (byte)2)]
    [DataRow("exec sp_query_store_remove_query 99", 12402, (byte)1)]
    [DataRow("exec sp_query_store_set_hints 99, N'OPTION (MAXDOP 1)'", 12402, (byte)5)]
    [DataRow("exec sp_query_store_clear_hints 99", 12402, (byte)6)]
    [DataRow("exec sp_query_store_remove_plan 99", 12403, (byte)1)]
    [DataRow("exec sp_query_store_reset_exec_stats 99", 12403, (byte)2)]
    [DataRow("exec sp_query_store_force_plan 1, 99", 12406, (byte)1)]
    [DataRow("exec sp_query_store_consistency_check", 12427, (byte)3)]
    [DataRow("exec sp_query_store_flush_db 1", 8144, (byte)51)]
    [DataRow("exec sp_query_store_force_plan", 313, (byte)51)]
    [DataRow("exec sp_query_store_force_plan null, null", 214, (byte)51)]
    [DataRow("exec sp_query_store_clear_message_queues 1", 8144, (byte)51)]
    [DataRow("exec sp_query_store_remove_plan_feedback", 201, (byte)62)]
    [DataRow("exec sp_query_store_remove_plan_feedback null", 214, (byte)56)]
    [DataRow("exec sp_query_store_remove_plan_feedback 99, 1", 12469, (byte)1)]
    [DataRow("exec sp_query_store_remove_plan_feedback 5", 12467, (byte)1)]
    [DataRow("exec sp_query_store_remove_plan_feedback 2", 12468, (byte)1)]
    [DataRow("exec sp_query_store_remove_plan_feedback 4, 1, 1", 8144, (byte)120)]
    public void Procedures_RefuseWithRealsNumberAndState(string call, int number, byte state)
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        var error = sim.AssertSqlError(call, number);
        AreEqual(state, error.State);
        StartsWith("sp_query_store_", error.Procedure);
    }

    [TestMethod]
    [DataRow("exec sp_query_store_force_plan 1, 1", (byte)4)]
    [DataRow("exec sp_query_store_unforce_plan 1, 1", (byte)4)]
    [DataRow("exec sp_query_store_set_hints 1, N'OPTION (MAXDOP 1)'", (byte)6)]
    [DataRow("exec sp_query_store_clear_hints 1", (byte)7)]
    public void ForcingAndHints_WhileOff_Raise12405(string call, byte state)
    {
        var sim = CapturingAll();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store = off");
        AreEqual(state, sim.AssertSqlError(call, 12405).State);
        _ = sim.ExecuteNonQuery("exec sp_query_store_consistency_check; exec sp_query_store_flush_db; exec sp_query_store_remove_plan_feedback 4; exec sp_query_store_clear_message_queues");
    }

    [TestMethod]
    [DataRow("data_flush_interval_seconds = 59", 153, (byte)15, (byte)5)]
    [DataRow("interval_length_minutes = 7", 153, (byte)16, (byte)6)]
    [DataRow("query_capture_mode = custom, query_capture_policy = (execution_count = 0)", 12452, (byte)15, (byte)1)]
    [DataRow("query_capture_mode = custom, query_capture_policy = (total_execution_cpu_time_ms = 0)", 12452, (byte)15, (byte)2)]
    [DataRow("query_capture_mode = custom, query_capture_policy = (stale_capture_policy_threshold = 8 days)", 12453, (byte)16, (byte)1)]
    [DataRow("interval_length_minutes = 1, interval_length_minutes = 7", 12401, (byte)15, (byte)2)]
    [DataRow("max_storage_size_mb = 2147483648", 102, (byte)15, (byte)1)]
    public void OptionValues_AreCheckedAsTheBatchCompiles(string options, int number, byte @class, byte state)
    {
        var sim = new Simulation();
        var error = sim.AssertSqlError($"create table ran (a int); alter database simulated set query_store ({options})", number);
        AreEqual(@class, error.Class);
        AreEqual(state, error.State);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 'ran'"));
    }

    [TestMethod]
    public void TwoQueryStoreClauses_Raise12417()
        => new Simulation().AssertSqlError(
            "alter database simulated set query_store (interval_length_minutes = 5), query_store (interval_length_minutes = 3)",
            12417,
            "Only one Query Store option can be given in ALTER DATABASE statement.");

    [TestMethod]
    public void AcceptedEdgeValues_AreStored()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store (data_flush_interval_seconds = 60, max_storage_size_mb = 0, max_plans_per_query = 0, cleanup_policy = (stale_query_threshold_days = 0))");
        AreEqual(60L, sim.ExecuteScalar("select flush_interval_seconds from sys.database_query_store_options"));
        AreEqual(0L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
    }

    [TestMethod]
    public void QueryPlan_IsAShowPlanDocument()
    {
        var sim = CapturingAll();
        _ = sim.ExecuteScalar("select b from t");
        var plan = (string)sim.ExecuteScalar("select p.query_plan from sys.query_store_plan p join sys.query_store_query q on q.query_id = p.query_id join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text = 'select b from t'")!;
        StartsWith("<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"", plan);
        Contains("StatementText=\"select b from t\"", plan);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.query_store_plan p where try_convert(xml, p.query_plan) is not null and p.plan_id = 1"));
    }

    [TestMethod]
    public async Task LockWait_IsALockWait_AndNotCpu()
    {
        var sim = CapturingAll();
        using var holder = sim.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; update t set b = 5 where a = 1").ExecuteNonQuery();
        var blocked = Task.Run(() => sim.ExecuteScalar("select b from t where a = 1"), TestContext.CancellationToken);
        await Task.Delay(300, TestContext.CancellationToken);
        _ = holder.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(5, await blocked);
        using var reader = sim.ExecuteReader("""
            select w.wait_category_desc, w.total_query_wait_time_ms, rs.last_cpu_time, rs.last_duration
            from sys.query_store_wait_stats w join sys.query_store_runtime_stats rs on rs.plan_id = w.plan_id
            """);
        IsTrue(reader.Read());
        AreEqual("Lock", reader.GetString(0));
        IsGreaterThanOrEqualTo(200L, reader.GetInt64(1));
        IsLessThan(reader.GetInt64(3) - 150_000, reader.GetInt64(2));
    }

    /// <summary>
    /// A function body the optimizer doesn't inline records its statements under the function's object
    /// id (probed 2026-09-30 against SQL Server 2025): a WHILE body's <c>SET</c> reading a table, a
    /// multi-statement table-valued function's DML on its table variable, and a plain scalar function's
    /// body below compatibility level 150.
    /// </summary>
    [TestMethod]
    public void FunctionBodies_NotInlined_RecordUnderTheFunction()
    {
        var sim = CapturingAll();
        sim.ExecuteBatches(
            "create function f1 (@x int) returns int as begin declare @r int = 0; while @x > 0 begin set @r = @r + (select count(*) from t where a <= @x); set @x = @x - 1; end; return @r; end",
            "create function f2 (@x int) returns @t table (a int) as begin insert @t select a from t where a <= @x; return; end",
            "create function f3 (@x int) returns int as begin return (select max(a) from t where a = @x); end");
        _ = sim.ExecuteScalar("select dbo.f1(3)");
        _ = sim.ExecuteScalar("select count(*) from dbo.f2(3)");
        _ = sim.ExecuteScalar("select dbo.f3(1)");
        object? Module(string text) => sim.ExecuteScalar($"select object_name(object_id) from sys.query_store_query q join sys.query_store_query_text t on t.query_text_id = q.query_text_id where t.query_sql_text = '{text}'");
        AreEqual("f1", Module("(@r int,@x int)set @r = @r + (select count(*) from t where a <= @x)"));
        AreEqual("f2", Module("(@x int)insert @t select a from t where a <= @x"));
        IsNull(Module("return (select max(a) from t where a = @x)"));

        _ = sim.ExecuteNonQuery("alter database simulated set compatibility_level = 140");
        _ = sim.ExecuteScalar("select dbo.f3(1)");
        AreEqual("f3", Module("return (select max(a) from t where a = @x)"));
    }
}
