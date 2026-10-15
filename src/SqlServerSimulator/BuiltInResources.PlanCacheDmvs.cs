using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The plan-cache DMVs over Simulation.CachedPlans: the cached plans, their
// statements' statistics, the module call statistics and the optimizer's
// counters. Shapes and object ids probed 2026-10-15 against SQL Server 2025;
// see docs/claude/plan-cache.md#the-plan-cache-dmvs.
internal static partial class BuiltInResources
{
    private static void RegisterPlanCacheDmvs(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        var planTypeName = NVarcharSqlType.Get(50, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);
        var objectTypeName = NVarcharSqlType.Get(20, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);
        Sys("dm_exec_cached_plans",
        [
            new("bucketid", SqlType.Int32, null, false),
            new("refcounts", SqlType.Int32, null, false),
            new("usecounts", SqlType.Int32, null, false),
            new("size_in_bytes", SqlType.Int32, null, false),
            new("memory_object_address", SqlType.Varbinary, 8, false),
            new("cacheobjtype", planTypeName, 50, false),
            new("objtype", objectTypeName, 20, false),
            new("plan_handle", SqlType.Varbinary, 64, false),
            new("pool_id", SqlType.Int32, null, false),
            new("parent_plan_handle", SqlType.Varbinary, 64, true),
        ], (batch, _) => CachedPlanRows(batch, planTypeName, objectTypeName));

        HeapColumn[] queryStatsColumns =
        [
            new("sql_handle", SqlType.Varbinary, 64, false),
            new("statement_start_offset", SqlType.Int32, null, false),
            new("statement_end_offset", SqlType.Int32, null, false),
            new("plan_generation_num", SqlType.BigInt, null, true),
            new("plan_handle", SqlType.Varbinary, 64, false),
            new("creation_time", SqlType.DateTime, null, true),
            new("last_execution_time", SqlType.DateTime, null, true),
            new("execution_count", SqlType.BigInt, null, false),
            .. MetricColumns("worker_time", nullable: false),
            .. MetricColumns("physical_reads", nullable: false),
            .. MetricColumns("logical_writes", nullable: false),
            .. MetricColumns("logical_reads", nullable: false),
            .. MetricColumns("clr_time", nullable: false),
            .. MetricColumns("elapsed_time", nullable: false),
            new("query_hash", SqlType.GetBinary(8), 8, true),
            new("query_plan_hash", SqlType.GetBinary(8), 8, true),
            .. MetricColumns("rows", nullable: true),
            new("statement_sql_handle", SqlType.Varbinary, 64, true),
            new("statement_context_id", SqlType.BigInt, null, true),
            .. MetricColumns("dop", nullable: true),
            .. MetricColumns("grant_kb", nullable: true),
            .. MetricColumns("used_grant_kb", nullable: true),
            .. MetricColumns("ideal_grant_kb", nullable: true),
            .. MetricColumns("reserved_threads", nullable: true),
            .. MetricColumns("used_threads", nullable: true),
            .. MetricColumns("columnstore_segment_reads", nullable: true),
            .. MetricColumns("columnstore_segment_skips", nullable: true),
            .. MetricColumns("spills", nullable: true),
            .. MetricColumns("num_physical_reads", nullable: false),
            .. MetricColumns("page_server_reads", nullable: false),
            .. MetricColumns("num_page_server_reads", nullable: false),
        ];
        Sys("dm_exec_query_stats", queryStatsColumns, static (batch, _) => QueryStatsRows(batch));

        var moduleTypeCode = CharSqlType.Get(2, Collation.Get("Latin1_General_CI_AS_KS_WS"), Coercibility.Implicit);
        var moduleTypeDesc = NVarcharSqlType.Get(60, Collation.Get("Latin1_General_CI_AS_KS_WS"), Coercibility.Implicit);
        HeapColumn[] ModuleStatsColumns(bool spills) =>
        [
            new("database_id", SqlType.Int32, null, false),
            new("object_id", SqlType.Int32, null, false),
            new("type", moduleTypeCode, 2, true),
            new("type_desc", moduleTypeDesc, 60, true),
            new("sql_handle", SqlType.Varbinary, 64, false),
            new("plan_handle", SqlType.Varbinary, 64, false),
            new("cached_time", SqlType.DateTime, null, true),
            new("last_execution_time", SqlType.DateTime, null, true),
            new("execution_count", SqlType.BigInt, null, false),
            .. MetricColumns("worker_time", nullable: false),
            .. MetricColumns("physical_reads", nullable: false),
            .. MetricColumns("logical_writes", nullable: false),
            .. MetricColumns("logical_reads", nullable: false),
            .. MetricColumns("elapsed_time", nullable: false),
            .. spills ? MetricColumns("spills", nullable: true) : [],
            .. MetricColumns("num_physical_reads", nullable: false),
            .. MetricColumns("page_server_reads", nullable: false),
            .. MetricColumns("num_page_server_reads", nullable: false),
        ];
        Sys("dm_exec_procedure_stats", ModuleStatsColumns(spills: true),
            (batch, _) => ModuleStatsRows(batch, static plan => plan.Module is Procedure, "P ", "SQL_STORED_PROCEDURE", spills: true, moduleTypeCode, moduleTypeDesc));
        Sys("dm_exec_trigger_stats", ModuleStatsColumns(spills: true),
            (batch, _) => ModuleStatsRows(batch, static plan => plan.Module is Trigger, "TR", "SQL_TRIGGER", spills: true, moduleTypeCode, moduleTypeDesc));
        Sys("dm_exec_function_stats", ModuleStatsColumns(spills: false),
            (batch, _) => ModuleStatsRows(batch, static plan => plan.Module is ScalarFunction, "FN", "SQL_SCALAR_FUNCTION", spills: false, moduleTypeCode, moduleTypeDesc));

        var counterName = NVarcharSqlType.Get(4000, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);
        Sys("dm_exec_query_optimizer_info",
        [
            new("counter", counterName, 4000, false),
            new("occurrence", SqlType.BigInt, null, false),
            new("value", SqlType.Float, null, true),
        ], (batch, _) => OptimizerInfoRows(batch, counterName));
    }

    /// <summary>The four columns a measure reports: its total, last, minimum and maximum.</summary>
    private static HeapColumn[] MetricColumns(string measure, bool nullable) =>
    [
        new("total_" + measure, SqlType.BigInt, null, nullable),
        new("last_" + measure, SqlType.BigInt, null, nullable),
        new("min_" + measure, SqlType.BigInt, null, nullable),
        new("max_" + measure, SqlType.BigInt, null, nullable),
    ];

    private static SqlValue[] Metric(in PlanCacheMetric metric) =>
    [
        SqlValue.FromInt64(metric.Total), SqlValue.FromInt64(metric.Last), SqlValue.FromInt64(metric.Min), SqlValue.FromInt64(metric.Max),
    ];

    /// <summary>A measure the simulator doesn't have, reported as real reports one that never moved: zeros.</summary>
    private static readonly SqlValue[] ZeroMetric = [SqlValue.FromInt64(0), SqlValue.FromInt64(0), SqlValue.FromInt64(0), SqlValue.FromInt64(0)];

    /// <summary>
    /// <c>sys.dm_exec_cached_plans</c>: one row per plan. The bucket, size and
    /// memory address are the simulator's own stand-ins, each derived from the
    /// plan so a plan reports the same values every read.
    /// </summary>
    private static IEnumerable<SqlValue[]> CachedPlanRows(Parser.BatchContext batch, NVarcharSqlType planTypeName, NVarcharSqlType objectTypeName)
    {
        foreach (var plan in batch.Connection.Simulation.LiveCachedPlans())
        {
            int useCount;
            int statements;
            lock (plan)
            {
                useCount = plan.UseCount;
                statements = plan.Statements.Count;
            }
            var address = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(address,
                0x0000000C00000060L | (((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(plan.PlanHandle.AsSpan(8)) & 0xFFFFFF) << 8));
            yield return
            [
                SqlValue.FromInt32((int)((uint)HashCode.Combine(plan.Key) % 40009)),
                SqlValue.FromInt32(2),
                SqlValue.FromInt32(useCount),
                SqlValue.FromInt32((16384 * (plan.IsBatch ? 1 : 2)) + (8192 * Math.Max(statements - 1, 0))),
                SqlValue.FromVarbinary(address),
                SqlValue.FromNVarchar(planTypeName, "Compiled Plan"),
                SqlValue.FromNVarchar(objectTypeName, plan.Key.Kind switch
                {
                    CachedPlanKind.Adhoc => "Adhoc",
                    CachedPlanKind.Prepared => "Prepared",
                    CachedPlanKind.Proc => "Proc",
                    _ => "Trigger",
                }),
                SqlValue.FromVarbinary(plan.PlanHandle),
                SqlValue.FromInt32(2),
                SqlValue.Null(SqlType.Varbinary),
            ];
        }
    }

    /// <summary><c>sys.dm_exec_query_stats</c>: one row per statement a cached plan has run.</summary>
    private static IEnumerable<SqlValue[]> QueryStatsRows(Parser.BatchContext batch)
    {
        foreach (var plan in batch.Connection.Simulation.LiveCachedPlans())
        {
            CachedStatementStats[] statements;
            lock (plan)
                statements = [.. plan.Statements];
            foreach (var statement in statements)
            {
                List<SqlValue> row;
                lock (plan)
                {
                    row =
                    [
                        SqlValue.FromVarbinary(plan.SqlHandle),
                        SqlValue.FromInt32(statement.StartOffset),
                        SqlValue.FromInt32(statement.EndOffset),
                        SqlValue.FromInt64(statement.PlanGeneration),
                        SqlValue.FromVarbinary(plan.PlanHandle),
                        SqlValue.FromDateTime(statement.CreationTime),
                        SqlValue.FromDateTime(statement.LastExecutionTime),
                        SqlValue.FromInt64(statement.ExecutionCount),
                        .. Metric(statement.WorkerTime),
                        .. ZeroMetric,
                        .. ZeroMetric,
                        .. Metric(statement.LogicalReads),
                        .. ZeroMetric,
                        .. Metric(statement.ElapsedTime),
                        SqlValue.FromBinary(SqlType.GetBinary(8), statement.Shape.QueryHash),
                        SqlValue.FromBinary(SqlType.GetBinary(8), statement.Shape.QueryPlanHash),
                        .. Metric(statement.Rows),
                        SqlValue.FromVarbinary(statement.StatementSqlHandle),
                        statement.ContextSettingsId is { } contextId ? SqlValue.FromInt64(contextId) : SqlValue.Null(SqlType.BigInt),
                        // Every statement runs serially: a degree of parallelism of 1 per execution.
                        SqlValue.FromInt64(statement.ExecutionCount), SqlValue.FromInt64(1), SqlValue.FromInt64(1), SqlValue.FromInt64(1),
                    ];
                }
                for (var i = 0; i < 11; i++)
                    row.AddRange(ZeroMetric);
                yield return [.. row];
            }
        }
    }

    /// <summary>
    /// <c>sys.dm_exec_procedure_stats</c>, <c>sys.dm_exec_trigger_stats</c> and
    /// <c>sys.dm_exec_function_stats</c>: one row per cached plan of the kind
    /// of module <paramref name="kind"/> picks that has run.
    /// </summary>
    private static IEnumerable<SqlValue[]> ModuleStatsRows(Parser.BatchContext batch, Func<CachedPlan, bool> kind, string typeCode, string typeDescription, bool spills, CharSqlType typeCodeType, NVarcharSqlType typeDescriptionType)
    {
        foreach (var plan in batch.Connection.Simulation.LiveCachedPlans())
        {
            if (!kind(plan))
                continue;
            List<SqlValue> row;
            lock (plan)
            {
                if (plan.ExecutionCount == 0)
                    continue;
                row =
                [
                    SqlValue.FromInt32(plan.Key.DatabaseId),
                    SqlValue.FromInt32(plan.ObjectId),
                    SqlValue.FromChar(typeCodeType, typeCode),
                    SqlValue.FromNVarchar(typeDescriptionType, typeDescription),
                    SqlValue.FromVarbinary(plan.SqlHandle),
                    SqlValue.FromVarbinary(plan.PlanHandle),
                    SqlValue.FromDateTime(plan.CachedTime),
                    SqlValue.FromDateTime(plan.LastExecutionTime),
                    SqlValue.FromInt64(plan.ExecutionCount),
                    .. Metric(plan.WorkerTime),
                    .. ZeroMetric,
                    .. ZeroMetric,
                    .. Metric(plan.LogicalReads),
                    .. Metric(plan.ElapsedTime),
                ];
            }
            if (spills)
                row.AddRange(ZeroMetric);
            for (var i = 0; i < 3; i++)
                row.AddRange(ZeroMetric);
            yield return [.. row];
        }
    }

    /// <summary>
    /// <c>sys.dm_exec_query_optimizer_info</c>'s counters, in real's order: the
    /// statements the cached plans hold count as the optimizations, each
    /// trivial or a full search's first stage by its shape, and by its kind of
    /// write; a counter nothing here moves reads 0 occurrences and a NULL value,
    /// as one real never moved does.
    /// </summary>
    private static SqlValue[][] OptimizerInfoRows(Parser.BatchContext batch, NVarcharSqlType counterName)
    {
        long optimizations = 0, trivial = 0, inserts = 0, deletes = 0, updates = 0, merges = 0;
        foreach (var plan in batch.Connection.Simulation.LiveCachedPlans())
        {
            lock (plan)
            {
                foreach (var statement in plan.Statements)
                {
                    optimizations++;
                    if (statement.Shape.IsTrivial)
                        trivial++;
                    switch (statement.Shape.StatementType)
                    {
                        case "DELETE":
                            deletes++;
                            break;
                        case "INSERT":
                            inserts++;
                            break;
                        case "MERGE":
                            merges++;
                            break;
                        case "UPDATE":
                            updates++;
                            break;
                    }
                }
            }
        }
        SqlValue[] Counter(string name, long occurrence, double? value) =>
        [
            SqlValue.FromNVarchar(counterName, name),
            SqlValue.FromInt64(occurrence),
            occurrence == 0 || value is null ? SqlValue.Null(SqlType.Float) : SqlValue.FromDouble(value.Value),
        ];
        return
        [
            Counter("optimizations", optimizations, 1),
            Counter("elapsed time", optimizations, 0),
            Counter("final cost", optimizations, 0),
            Counter("trivial plan", trivial, 1),
            Counter("tasks", 0, null),
            Counter("no plan", 0, null),
            Counter("search 0", 0, null),
            Counter("search 0 time", 0, null),
            Counter("search 0 tasks", 0, null),
            Counter("search 1", optimizations - trivial, 1),
            Counter("search 1 time", optimizations - trivial, 0),
            Counter("search 1 tasks", 0, null),
            Counter("search 2", 0, null),
            Counter("search 2 time", 0, null),
            Counter("search 2 tasks", 0, null),
            Counter("gain stage 0 to stage 1", 0, null),
            Counter("gain stage 1 to stage 2", 0, null),
            Counter("timeout", 0, null),
            Counter("memory limit exceeded", 0, null),
            Counter("insert stmt", inserts, 1),
            Counter("delete stmt", deletes, 1),
            Counter("update stmt", updates, 1),
            Counter("merge stmt", merges, 1),
            Counter("contains subquery", 0, null),
            Counter("unnest failed", 0, null),
            Counter("tables", 0, null),
            Counter("hints", 0, null),
            Counter("order hint", 0, null),
            Counter("join hint", 0, null),
            Counter("view reference", 0, null),
            Counter("remote query", 0, null),
            Counter("maximum DOP", optimizations, 1),
            Counter("maximum recursion level", 0, null),
            Counter("indexed views loaded", 0, null),
            Counter("indexed views matched", 0, null),
            Counter("indexed views used", 0, null),
            Counter("indexed views updated", 0, null),
            Counter("dynamic cursor request", 0, null),
            Counter("fast forward cursor request", 0, null),
        ];
    }
}
