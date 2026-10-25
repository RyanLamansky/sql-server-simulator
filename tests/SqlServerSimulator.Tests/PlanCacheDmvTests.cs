using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The plan-cache DMVs: which batches and modules <c>sys.dm_exec_cached_plans</c>
/// lists and under which <c>objtype</c>, the statement statistics of
/// <c>sys.dm_exec_query_stats</c>, the module call statistics, the plan-handle
/// functions and what clears the cache. Probed against SQL Server 2025
/// (2026-10-15).
/// </summary>
[TestClass]
public sealed class PlanCacheDmvTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Table = "create table t (a int primary key, b varchar(10)); insert t values (1, 'x'), (2, 'y')";

    /// <summary>A simulation over a two-row table <c>t</c> whose own batches have been cleared from the cache.</summary>
    private static Simulation WithTable(params ReadOnlySpan<string> modules)
    {
        var sim = new Simulation();
        sim.ExecuteBatches([Table, .. modules, "dbcc freeproccache with no_infomsgs"]);
        return sim;
    }

    /// <summary>The <c>objtype</c> and <c>usecounts</c> of each cached plan whose text is <paramref name="text"/>.</summary>
    private static List<(string ObjectType, int UseCount)> Plans(Simulation sim, string text)
    {
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("""
            select cp.objtype, cp.usecounts from sys.dm_exec_cached_plans cp
            cross apply sys.dm_exec_sql_text(cp.plan_handle) st
            where st.text = @text order by cp.objtype
            """, ("@text", text));
        using var reader = command.ExecuteReader();
        return [.. reader.EnumerateRecords().Select(record => (record.GetString(0), record.GetInt32(1)))];
    }

    /// <summary>Each statement row of the cached plan whose text is <paramref name="text"/>: its offsets, executions and rows, in offset order.</summary>
    private static List<(int Start, int End, long Executions, long Rows)> Statements(Simulation sim, string text)
    {
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("""
            select qs.statement_start_offset, qs.statement_end_offset, qs.execution_count, qs.total_rows
            from sys.dm_exec_query_stats qs cross apply sys.dm_exec_sql_text(qs.plan_handle) st
            where st.text = @text order by qs.statement_start_offset
            """, ("@text", text));
        using var reader = command.ExecuteReader();
        return [.. reader.EnumerateRecords().Select(record => (record.GetInt32(0), record.GetInt32(1), record.GetInt64(2), record.GetInt64(3)))];
    }

    [TestMethod]
    public void AdHocBatch_IsOnePlan_CountingEachUse_WithItsStatementsPlacedByByteOffset()
    {
        const string batch = "select b from t where b <> 'q'; select count(*) from t";
        var sim = WithTable();
        _ = sim.ExecuteScalar(batch);
        _ = sim.ExecuteScalar(batch);
        CollectionAssert.AreEqual(new[] { ("Adhoc", 2) }, Plans(sim, batch));
        CollectionAssert.AreEqual(new[] { (0, 58, 2L, 4L), (64, 106, 2L, 2L) }, Statements(sim, batch));
        AreEqual("Compiled Plan", sim.ExecuteScalar("select cacheobjtype from sys.dm_exec_cached_plans where objtype = 'Adhoc'"));
    }

    /// <summary>
    /// An ad hoc plan used once leaves once 1,024 newer ad hoc plans have
    /// arrived, and one a batch used again stays: real's cache under memory
    /// pressure removes first an ad hoc plan no reference raised from the zero
    /// current cost it starts at.
    /// </summary>
    [TestMethod]
    public void AdHocPlanUsedOnce_LeavesFirst()
    {
        const string once = "select b from t where b <> 'once'";
        const string again = "select b from t where b <> 'again'";
        var sim = WithTable();
        _ = sim.ExecuteScalar(once);
        _ = sim.ExecuteScalar(again);
        _ = sim.ExecuteScalar(again);
        using (var connection = sim.CreateOpenConnection())
        {
            for (var i = 0; i < 1024; i++)
            {
                using var command = connection.CreateCommand($"select b from t where b <> 'other {i.ToInvariantString()}'");
                _ = command.ExecuteScalar();
            }
        }
        IsEmpty(Plans(sim, once));
        CollectionAssert.AreEqual(new[] { ("Adhoc", 2) }, Plans(sim, again));
    }

    // A statement simple parameterization rewrites runs under a prepared plan
    // of the rewritten text, each ad hoc text a shell plan of its own whose
    // compile counts one use of the prepared plan.
    [TestMethod]
    public void SimpleParameterizedStatement_RunsUnderPreparedPlan_BehindAnAdHocShellPerText()
    {
        var sim = WithTable();
        _ = sim.ExecuteScalar("select b from t where a = 1");
        _ = sim.ExecuteScalar("select b from t where a = 1");
        _ = sim.ExecuteScalar("select b from t where a = 2");
        CollectionAssert.AreEqual(new[] { ("Adhoc", 2) }, Plans(sim, "select b from t where a = 1"));
        CollectionAssert.AreEqual(new[] { ("Adhoc", 1) }, Plans(sim, "select b from t where a = 2"));
        const string prepared = "(@1 tinyint)SELECT [b] FROM [t] WHERE [a]=@1";
        CollectionAssert.AreEqual(new[] { ("Prepared", 2) }, Plans(sim, prepared));
        CollectionAssert.AreEqual(new[] { (24, 86, 3L, 3L) }, Statements(sim, prepared));
        IsEmpty(Statements(sim, "select b from t where a = 1"));
    }

    // The prepared plan declares its parameters in number order, where Query
    // Store's text puts an UPDATE's WHERE clause's first, and its statement
    // starts past the whole declaration, a numeric(p,s)'s parentheses
    // included (probed 2026-10-15 against SQL Server 2025).
    [TestMethod]
    public void SimpleParameterizedWrite_PreparedPlanDeclaresInNumberOrder()
    {
        var sim = WithTable();
        _ = sim.ExecuteNonQuery("update t set b = 'z' where a = 1");
        _ = sim.ExecuteNonQuery("delete from t where a = 1.5");
        CollectionAssert.AreEqual(new[] { (58, 130, 1L, 1L) }, Statements(sim, "(@1 varchar(8000),@2 tinyint)UPDATE [t] set [b] = @1  WHERE [a]=@2"));
        CollectionAssert.AreEqual(new[] { (34, 80, 1L, 0L) }, Statements(sim, "(@1 numeric(2,1))DELETE [t]  WHERE [a]=@1"));
    }

    // A parameterized command reaches real as sp_executesql, a prepared plan
    // whose text is its declaration in parentheses followed by the batch.
    [TestMethod]
    public void ParameterizedCommand_IsPreparedPlan_ItsStatementsPastTheDeclaration()
    {
        var sim = WithTable();
        using (var connection = sim.CreateOpenConnection())
        {
            for (var value = 1; value <= 2; value++)
            {
                using var command = connection.CreateCommand("select b from t where a = @a", ("@a", value));
                _ = command.ExecuteScalar();
            }
        }
        const string text = "(@a int)select b from t where a = @a";
        CollectionAssert.AreEqual(new[] { ("Prepared", 2) }, Plans(sim, text));
        CollectionAssert.AreEqual(new[] { (16, 70, 2L, 2L) }, Statements(sim, text));
    }

    [TestMethod]
    public void DynamicSql_ExecString_IsAdHoc_ExecuteSqlPreparedOnlyWithDeclarations()
    {
        var sim = WithTable();
        _ = sim.ExecuteNonQuery("""
            exec sp_executesql N'select b from t where b <> @x', N'@x varchar(5)', 'q';
            exec sp_executesql N'select a from t where b <> ''q''';
            exec ('select a, b from t where b <> ''q''')
            """);
        CollectionAssert.AreEqual(new[] { ("Prepared", 1) }, Plans(sim, "(@x varchar(5))select b from t where b <> @x"));
        CollectionAssert.AreEqual(new[] { ("Adhoc", 1) }, Plans(sim, "select a from t where b <> 'q'"));
        CollectionAssert.AreEqual(new[] { ("Adhoc", 1) }, Plans(sim, "select a, b from t where b <> 'q'"));
    }

    // Real compiles no plan to keep for a batch of statements that read no
    // table — a constant SELECT, DECLARE, a variable's SET, PRINT, EXEC — nor
    // for one declaring a table variable, while one of SET options and
    // transaction statements alone is kept.
    [TestMethod]
    [DataRow("select 1", false)]
    [DataRow("declare @x int = 1; set @x = 2; select @x", false)]
    [DataRow("print 'x'", false)]
    [DataRow("exec p", false)]
    [DataRow("declare @t table (a int); insert @t select a from t", false)]
    [DataRow("set nocount on", true)]
    [DataRow("begin tran; commit", true)]
    [DataRow("set xact_abort off; select 5", false)]
    [DataRow("select a from t; select 1", true)]
    public void BatchCaching_FollowsWhetherRealCompilesAPlanToKeep(string batch, bool cached)
    {
        var sim = WithTable("create procedure p as select 1");
        _ = sim.ExecuteNonQuery(batch);
        HasCount(cached ? 1 : 0, Plans(sim, batch));
    }

    [TestMethod]
    public void Procedure_IsProcPlan_WithCallStatistics_AndItsStatementsPlacedInTheCreateText()
    {
        const string definition = "create procedure p1 as select * from t where a = 1; select count(*) from t;";
        var sim = WithTable(definition);
        _ = sim.ExecuteNonQuery("exec p1; exec p1");
        CollectionAssert.AreEqual(new[] { ("Proc", 2) }, Plans(sim, definition));
        CollectionAssert.AreEqual(new[] { (46, 98, 2L, 2L), (104, 146, 2L, 2L) }, Statements(sim, definition));
        using var reader = sim.ExecuteReader("""
            select ps.type, ps.type_desc, ps.execution_count, st.dbid, st.objectid, st.number, st.encrypted, st.text
            from sys.dm_exec_procedure_stats ps cross apply sys.dm_exec_sql_text(ps.sql_handle) st
            where ps.object_id = object_id('p1') and ps.database_id = db_id()
            """);
        IsTrue(reader.Read());
        AreEqual("P ", reader.GetString(0));
        AreEqual("SQL_STORED_PROCEDURE", reader.GetString(1));
        AreEqual(2L, reader.GetInt64(2));
        AreEqual((short)5, reader.GetInt16(3));
        AreEqual(sim.ExecuteScalar("select object_id('p1')"), reader.GetInt32(4));
        AreEqual((short)1, reader.GetInt16(5));
        IsFalse(reader.GetBoolean(6));
        AreEqual(definition, reader.GetString(7));
    }

    // Neither a procedure created WITH RECOMPILE nor EXEC … WITH RECOMPILE
    // leaves a plan.
    [TestMethod]
    public void ProcedureCompiledEveryCall_KeepsNoPlan()
    {
        var sim = WithTable("create procedure p1 as select * from t", "create procedure p2 with recompile as select * from t");
        _ = sim.ExecuteNonQuery("exec p1 with recompile; exec p2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_cached_plans where objtype = 'Proc'"));
    }

    [TestMethod]
    public void Trigger_IsTriggerPlan_WithTriggerStatistics()
    {
        var sim = WithTable("create trigger tr on t after update as select count(*) from t");
        _ = sim.ExecuteNonQuery("update t set b = b where a = 1; update t set b = b where a = 2");
        AreEqual("Trigger", sim.ExecuteScalar("select objtype from sys.dm_exec_cached_plans cp cross apply sys.dm_exec_sql_text(cp.plan_handle) st where st.objectid = object_id('tr')"));
        AreEqual(2L, sim.ExecuteScalar("select execution_count from sys.dm_exec_trigger_stats where object_id = object_id('tr') and type = 'TR' and type_desc = 'SQL_TRIGGER'"));
    }

    // A scalar function the optimizer doesn't inline (a WHILE loop bars it)
    // runs under a plan of its own, used once per calling statement and
    // executed once per row; an inlined one keeps none.
    [TestMethod]
    public void ScalarFunction_NotInlined_CountsUsePerStatementAndExecutionPerCall()
    {
        var sim = WithTable(
            "create function f2(@x int) returns int as begin declare @r int = 0; while @r < @x set @r += 1; return (select count(*) from t where a > @r) end",
            "create function f3(@x int) returns int as begin return @x + 1 end");
        _ = sim.ExecuteNonQuery("select dbo.f2(0), dbo.f3(a) from t");
        AreEqual(1, sim.ExecuteScalar("select cp.usecounts from sys.dm_exec_cached_plans cp cross apply sys.dm_exec_sql_text(cp.plan_handle) st where st.objectid = object_id('f2')"));
        AreEqual(2L, sim.ExecuteScalar("select execution_count from sys.dm_exec_function_stats where object_id = object_id('f2') and type = 'FN'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_cached_plans cp cross apply sys.dm_exec_sql_text(cp.plan_handle) st where st.objectid = object_id('f3')"));
    }

    // A batch's plan handle names the database it compiled in, its SQL handle
    // none; both resolve its text.
    [TestMethod]
    public void SqlText_PlanHandleNamesTheDatabase_SqlHandleDoesNot()
    {
        const string batch = "select a from t where b <> 'q'";
        var sim = WithTable();
        _ = sim.ExecuteScalar(batch);
        AreEqual("5|", sim.ExecuteScalar($"select concat(st.dbid, '|', st.objectid) from sys.dm_exec_cached_plans cp cross apply sys.dm_exec_sql_text(cp.plan_handle) st where st.text = '{batch.Replace("'", "''", StringComparison.Ordinal)}'"));
        AreEqual(1, sim.ExecuteScalar($"select count(*) from sys.dm_exec_query_stats qs cross apply sys.dm_exec_sql_text(qs.sql_handle) st where st.text = '{batch.Replace("'", "''", StringComparison.Ordinal)}' and st.dbid is null"));
    }

    [TestMethod]
    public void PlanAttributes_ListTheCacheKeyThenTheExecutionContexts()
    {
        const string batch = "select a from t where b <> 'q'";
        var sim = WithTable();
        _ = sim.ExecuteScalar(batch);
        _ = sim.ExecuteScalar(batch);
        using var reader = sim.ExecuteReader("""
            declare @h varbinary(64) = (select plan_handle from sys.dm_exec_cached_plans cp cross apply sys.dm_exec_sql_text(cp.plan_handle) st where st.text = 'select a from t where b <> ''q''');
            select attribute, value, is_cache_key from sys.dm_exec_plan_attributes(@h)
            """);
        var attributes = reader.EnumerateRecords().Select(record => (record.GetString(0), record.GetValue(1), record.GetBoolean(2))).ToList();
        HasCount(29, attributes);
        AreEqual(("set_options", 251, true), attributes[0]);
        AreEqual(("dbid", 5, true), attributes[2]);
        AreEqual(("dbid_execute", 0, true), attributes[3]);
        AreEqual(("date_first", (byte)7, true), attributes[7]);
        AreEqual(("hits_exec_context", 1, false), attributes[20]);
        AreEqual("sql_handle", attributes[28].Item1);
    }

    [TestMethod]
    public void QueryPlan_DescribesEachStatementTheBatchRan()
    {
        var sim = WithTable();
        _ = sim.ExecuteScalar("select b from t where b <> 'q'; select count(*) from t");
        var plan = (string)sim.ExecuteScalar("""
            select cast(qp.query_plan as nvarchar(max)) from sys.dm_exec_cached_plans cp
            cross apply sys.dm_exec_query_plan(cp.plan_handle) qp
            where qp.dbid = 5 and qp.objectid is null and cp.objtype = 'Adhoc' and cast(qp.query_plan as nvarchar(max)) like '%count%'
            """)!;
        StartsWith("<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"", plan);
        Contains("<StmtSimple StatementText=\"select b from t where b &lt;&gt; 'q'\" StatementId=\"1\"", plan);
        Contains("<StmtSimple StatementText=\"select count(*) from t\" StatementId=\"2\"", plan);
        Contains("RetrievedFromCache=\"true\"", plan);
    }

    // Offsets 0 and -1 (each one's DEFAULT) are the whole batch, a statement's
    // own offsets its plan alone, and offsets placing no statement a NULL plan.
    [TestMethod]
    [DataRow("default, default", "StatementId=\"2\"")]
    [DataRow("64, 106", "<StmtSimple StatementText=\"select count(*) from t\" StatementId=\"1\"")]
    [DataRow("64, -1", "<StmtSimple StatementText=\"select count(*) from t\" StatementId=\"1\"")]
    [DataRow("2, 10", null)]
    public void TextQueryPlan_PicksTheStatementItsOffsetsPlace(string offsets, string? expected)
    {
        var sim = WithTable();
        _ = sim.ExecuteScalar("select b from t where b <> 'q'; select count(*) from t");
        var plan = sim.ExecuteScalar($"""
            declare @h varbinary(64) = (select plan_handle from sys.dm_exec_cached_plans where objtype = 'Adhoc' and usecounts = 1 and plan_handle in (select plan_handle from sys.dm_exec_query_stats where statement_start_offset = 64));
            select query_plan from sys.dm_exec_text_query_plan(@h, {offsets})
            """);
        if (expected is null)
            AreEqual(DBNull.Value, plan);
        else
            Contains(expected, (string)plan!);
    }

    [TestMethod]
    [DataRow("sys.dm_exec_query_plan(0x0600)", 569, "The handle that was passed to dm_exec_query_plan was invalid.")]
    [DataRow("sys.dm_exec_text_query_plan(0x06, 0, -1)", 569, "The handle that was passed to dm_exec_text_query_plan was invalid.")]
    [DataRow("sys.dm_exec_plan_attributes(0x06)", 569, "The handle that was passed to dm_exec_plan_attributes was invalid.")]
    [DataRow("sys.dm_exec_query_plan('abc')", 257, "Implicit conversion from data type varchar to varbinary is not allowed. Use the CONVERT function to run this query.")]
    public void PlanHandleFunctions_RefuseWhatIsNoHandle(string call, int number, string message)
        => new Simulation().AssertSqlError($"select * from {call}", number, message);

    [TestMethod]
    [DataRow("sys.dm_exec_query_plan(null)")]
    [DataRow("sys.dm_exec_query_plan(0x06000500AAAAAAAAAAAAAAAA0C00000001000000000000000000000000000000000000000000000000000000)")]
    [DataRow("sys.dm_exec_plan_attributes(null)")]
    [DataRow("sys.dm_exec_text_query_plan(0x020000004445E51F3A81FA399429361D61044E46C32805940000000000000000000000000000000000000000, 0, -1)")]
    public void PlanHandleFunctions_AnswerNothingForAHandleNamingNoPlan(string call)
        => AreEqual(0, new Simulation().ExecuteScalar($"select count(*) from {call}"));

    [TestMethod]
    public void FreeProcCache_ByPlanHandle_RemovesThatPlanAlone()
    {
        var sim = WithTable("create procedure p1 as select * from t");
        _ = sim.ExecuteNonQuery("exec p1; select a from t where b <> 'q'");
        _ = sim.ExecuteNonQuery("""
            declare @h varbinary(64) = (select plan_handle from sys.dm_exec_procedure_stats where object_id = object_id('p1'));
            dbcc freeproccache (@h) with no_infomsgs
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_cached_plans where objtype = 'Proc'"));
        HasCount(1, Plans(sim, "exec p1; select a from t where b <> 'q'"));
    }

    [TestMethod]
    public void FreeProcCache_BySqlHandle_RemovesTheModulesPlan()
    {
        var sim = WithTable("create procedure p1 as select * from t");
        _ = sim.ExecuteNonQuery("exec p1");
        _ = sim.ExecuteNonQuery("""
            declare @h varbinary(64) = (select sql_handle from sys.dm_exec_procedure_stats where object_id = object_id('p1'));
            dbcc freeproccache (@h) with no_infomsgs
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_procedure_stats"));
    }

    [TestMethod]
    public void ClearProcedureCache_RemovesTheDatabasesPlans()
    {
        var sim = WithTable();
        _ = sim.ExecuteScalar("select a from t where b <> 'q'");
        _ = sim.ExecuteNonQuery("alter database scoped configuration clear procedure_cache");
        IsEmpty(Plans(sim, "select a from t where b <> 'q'"));
    }

    // sp_recompile, ALTER and DROP each remove a procedure's plan at once.
    [TestMethod]
    [DataRow("exec sp_recompile 'p1'")]
    [DataRow("alter procedure p1 as select b from t")]
    [DataRow("drop procedure p1")]
    public void ProcedurePlan_GoesWithItsRecompileOrRedefinition(string change)
    {
        var sim = WithTable("create procedure p1 as select * from t");
        _ = sim.ExecuteNonQuery("exec p1");
        _ = sim.ExecuteNonQuery(change);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_cached_plans where objtype = 'Proc'"));
    }

    // A change to a table the body reads recompiles its statements as they
    // next run: the plan and its uses stay, the statements start their counts
    // over under the next plan generation.
    [TestMethod]
    public void ProcedureRecompiledForASchemaChange_KeepsItsPlan_StartsItsStatementsOver()
    {
        var sim = WithTable("create procedure p1 as select * from t");
        _ = sim.ExecuteNonQuery("exec p1; exec p1");
        _ = sim.ExecuteNonQuery("alter table t add c int null");
        _ = sim.ExecuteNonQuery("exec p1");
        AreEqual(3, sim.ExecuteScalar("select usecounts from sys.dm_exec_cached_plans where objtype = 'Proc'"));
        AreEqual("2|1", sim.ExecuteScalar("select concat(qs.plan_generation_num, '|', qs.execution_count) from sys.dm_exec_query_stats qs join sys.dm_exec_procedure_stats ps on ps.plan_handle = qs.plan_handle"));
    }

    [TestMethod]
    public void DroppedDatabase_TakesItsPlans()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create database other", "use other", "create table t (a int)", "select a from t where a <> 7", "use master");
        HasCount(1, Plans(sim, "select a from t where a <> 7"));
        _ = sim.ExecuteNonQuery("drop database other");
        IsEmpty(Plans(sim, "select a from t where a <> 7"));
    }

    [TestMethod]
    [DataRow("select count(*) from sys.dm_exec_cached_plans")]
    [DataRow("select count(*) from sys.dm_exec_query_stats")]
    [DataRow("select count(*) from sys.dm_exec_procedure_stats")]
    [DataRow("select count(*) from sys.dm_exec_trigger_stats")]
    [DataRow("select count(*) from sys.dm_exec_function_stats")]
    [DataRow("select count(*) from sys.dm_exec_query_optimizer_info")]
    [DataRow("select count(*) from sys.dm_exec_query_plan(0x06)")]
    [DataRow("select count(*) from sys.dm_exec_text_query_plan(0x06, 0, -1)")]
    [DataRow("select count(*) from sys.dm_exec_plan_attributes(0x06)")]
    public void RestrictedLogin_WithoutServerPerformanceState_Raises300(string query)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'P@ss1word'");
        var ex = sim.AssertSqlError("use master; execute as login = 'lo'; " + query, 300);
        AreEqual(297, ex.Errors[1].Number);
    }

    [TestMethod]
    public void OptimizerInfo_CountsTheCachedStatementsAmongRealsCounters()
    {
        var sim = WithTable();
        _ = sim.ExecuteNonQuery("update t set b = b where a <> 7; select a from t where b <> 'q'");
        using var reader = sim.ExecuteReader("select counter, occurrence from sys.dm_exec_query_optimizer_info");
        var counters = reader.EnumerateRecords().ToDictionary(record => record.GetString(0), record => record.GetInt64(1));
        HasCount(39, counters);
        IsGreaterThanOrEqualTo(2L, counters["optimizations"]);
        AreEqual(1L, counters["update stmt"]);
    }
}
