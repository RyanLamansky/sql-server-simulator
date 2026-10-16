using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// The plan-handle TVFs over Simulation.CachedPlans: sys.dm_exec_plan_attributes,
// sys.dm_exec_query_plan and sys.dm_exec_text_query_plan. Shapes, refusals and
// argument handling probed 2026-10-15 against SQL Server 2025.
partial class Selection
{
    private static readonly NVarcharSqlType PlanAttributeNameType = NVarcharSqlType.Get(128, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);

    private static readonly SqlType[] PlanAttributesSchema = [PlanAttributeNameType, SqlType.SqlVariant, SqlType.Bit];

    private static readonly string[] PlanAttributesColumnNames = ["attribute", "value", "is_cache_key"];

    private static readonly SqlType[] QueryPlanSchema = [SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.Bit, SqlType.Xml];

    private static readonly SqlType[] TextQueryPlanSchema =
    [
        SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.Bit, NVarcharSqlType.Get(-1, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit),
    ];

    private static readonly string[] QueryPlanColumnNames = ["dbid", "objectid", "number", "encrypted", "query_plan"];

    private static readonly bool[] QueryPlanNullability = [true, true, true, false, true];

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_plan_attributes(plan_handle)</c>: the
    /// attributes of the cached plan a plan handle names — the cache-key
    /// attributes the plan compiled under, then its execution-context
    /// counters and its SQL handle. A handle naming no plan, and a NULL one,
    /// answer no rows; one shorter than a plan handle is Msg 569 state 10.
    /// </summary>
    public static Selection ParsePlanAttributes(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(PlanAttributesSchema, PlanAttributesColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumeratePlanAttributes(arguments[0], batch, outerResolver))
        {
            ColumnNullability = [false, true, false],
        };
    }

    private static List<byte[]> EnumeratePlanAttributes(Expression handleExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        if (PlanOfHandle(handleExpr, batch, outerResolver, "dm_exec_plan_attributes", 10) is not { } plan)
            return [];
        int useCount;
        lock (plan)
            useCount = plan.UseCount;
        var key = plan.Key;
        static SqlValue Int(int value) => SqlValue.FromVariant(SqlValue.FromInt32(value));
        (string Name, SqlValue Value, bool IsCacheKey)[] attributes =
        [
            ("set_options", Int(key.SetOptions), true),
            ("objectid", Int(plan.ObjectId), true),
            ("dbid", Int(key.DatabaseId), true),
            ("dbid_execute", Int(plan.IsBatch ? 0 : key.DatabaseId), true),
            ("user_id", Int(plan.UserId), true),
            ("language_id", SqlValue.FromVariant(SqlValue.FromInt16(key.LanguageId)), true),
            ("date_format", SqlValue.FromVariant(SqlValue.FromInt16(key.DateFormat)), true),
            ("date_first", SqlValue.FromVariant(SqlValue.FromByte(key.DateFirst)), true),
            ("compat_level", SqlValue.FromVariant(SqlValue.FromByte((byte)plan.CompatibilityLevel)), true),
            ("status", Int(1024), true),
            ("required_cursor_options", Int(0), true),
            ("acceptable_cursor_options", Int(0), true),
            ("merge_action_type", SqlValue.FromVariant(SqlValue.FromInt16(0)), true),
            ("is_replication_specific", Int(0), true),
            ("optional_spid", Int(0), true),
            ("optional_clr_trigger_dbid", Int(0), true),
            ("optional_clr_trigger_objid", Int(0), true),
            ("parent_plan_handle", SqlValue.Null(SqlType.SqlVariant), true),
            // One execution context, made at the plan's first use and reused at every later one.
            ("inuse_exec_context", Int(0), false),
            ("free_exec_context", Int(1), false),
            ("hits_exec_context", Int(Math.Max(useCount - 1, 0)), false),
            ("misses_exec_context", Int(1), false),
            ("removed_exec_context", Int(0), false),
            ("inuse_cursors", Int(0), false),
            ("free_cursors", Int(0), false),
            ("hits_cursors", Int(0), false),
            ("misses_cursors", Int(0), false),
            ("removed_cursors", Int(0), false),
            ("sql_handle", SqlValue.FromVariant(SqlValue.FromVarbinary(plan.SqlHandle)), false),
        ];
        return [.. attributes.Select(attribute => RowEncoder.EncodeRow(PlanAttributesSchema,
            [SqlValue.FromNVarchar(PlanAttributeNameType, attribute.Name), attribute.Value, SqlValue.FromBoolean(attribute.IsCacheKey)]))];
    }

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_query_plan(plan_handle)</c>: the
    /// ShowPlan XML of the cached plan a plan handle names, one statement
    /// element per statement the plan has run, each with real's attributes
    /// and an empty <c>QueryPlan</c>, the simulator having no optimizer whose
    /// operators it could describe. A handle naming no plan, and a NULL one,
    /// answer no rows; one shorter than a plan handle is Msg 569 state 5.
    /// </summary>
    public static Selection ParseQueryPlan(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(QueryPlanSchema, QueryPlanColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateQueryPlan(arguments[0], null, null, batch, outerResolver, text: false))
        {
            ColumnNullability = QueryPlanNullability,
        };
    }

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_text_query_plan(plan_handle,
    /// statement_start_offset, statement_end_offset)</c>: the plan
    /// <c>sys.dm_exec_query_plan</c> reports as text — the whole batch's for
    /// offsets 0 and -1 (each offset's <c>DEFAULT</c>), one statement's for its
    /// own start offset with its end offset or -1, and a NULL plan for offsets
    /// placing no statement.
    /// </summary>
    public static Selection ParseTextQueryPlan(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 3, acceptsDefault: true);
        return new Selection(TextQueryPlanSchema, QueryPlanColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateQueryPlan(arguments[0]!, arguments[1], arguments[2], batch, outerResolver, text: true))
        {
            ColumnNullability = QueryPlanNullability,
        };
    }

    private static List<byte[]> EnumerateQueryPlan(Expression handleExpr, Expression? startExpr, Expression? endExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, bool text)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var functionName = text ? "dm_exec_text_query_plan" : "dm_exec_query_plan";
        if (PlanOfHandle(handleExpr, batch, outerResolver, functionName, 5) is not { } plan)
            return [];

        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var start = OffsetArgument(startExpr, runtime, 0);
        var end = OffsetArgument(endExpr, runtime, -1);
        CachedStatementStats[] statements;
        lock (plan)
            statements = [.. plan.Statements.OrderBy(static statement => statement.StartOffset)];
        if (!(start == 0 && end == -1))
            statements = [.. statements.Where(statement => statement.StartOffset == start && (end == -1 || statement.EndOffset == end)).Take(1)];

        var document = ShowPlanOf(plan, statements);
        var planValue = document is null
            ? SqlValue.Null(text ? TextQueryPlanSchema[4] : SqlType.Xml)
            : text ? SqlValue.FromNVarchar((NVarcharSqlType)TextQueryPlanSchema[4], document) : SqlValue.FromXml(document);
        return
        [
            RowEncoder.EncodeRow(text ? TextQueryPlanSchema : QueryPlanSchema,
            [
                SqlValue.FromInt16(plan.Key.DatabaseId),
                plan.Module is null ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(plan.ObjectId),
                plan.Module is null ? SqlValue.Null(SqlType.SmallInt) : SqlValue.FromInt16(plan.Number),
                SqlValue.FromBoolean(plan.Module is not null && plan.Text is null),
                planValue,
            ]),
        ];

        static int OffsetArgument(Expression? argument, RuntimeContext runtime, int byDefault) =>
            argument?.Run(runtime) is { IsNull: false } value ? value.CoerceTo(SqlType.Int32).AsInt32 : byDefault;
    }

    /// <summary>The ShowPlan XML document of <paramref name="statements"/> of <paramref name="plan"/>, null for none.</summary>
    private static string? ShowPlanOf(CachedPlan plan, CachedStatementStats[] statements)
    {
        if (statements.Length == 0 || plan.Text is not { } planText)
            return null;
        var xml = Simulation.OpenShowPlan();
        for (var i = 0; i < statements.Length; i++)
        {
            var statement = statements[i];
            var from = Math.Min(statement.StartOffset / 2, planText.Length);
            var through = Math.Min((statement.EndOffset / 2) + 1, planText.Length);
            _ = Simulation.AppendShowPlanStatement(xml, planText[from..Math.Max(from, through)], i + 1, statement.Shape, statement.StatementSqlHandle,
                statement.ContextSettingsId, plan.Module?.ObjectId ?? 0, statement.ParameterizationType, retrievedFromCache: true, plan.CompatibilityLevel, plan.Key.SetOptions);
        }
        return xml.Append(Simulation.ShowPlanClose).ToString();
    }

    private static readonly SqlType[] QueryStatisticsXmlSchema =
    [
        SqlType.SmallInt, SqlType.Int32, VarbinarySqlType.Get(64), VarbinarySqlType.Get(64), SqlType.Xml, SqlType.Int32, SqlType.Int32,
    ];

    private static readonly string[] QueryStatisticsXmlColumnNames =
        ["session_id", "request_id", "sql_handle", "plan_handle", "query_plan", "statement_start_offset", "statement_end_offset"];

    private static readonly bool[] QueryStatisticsXmlNullability = [false, false, true, true, true, false, false];

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_query_statistics_xml(session_id)</c>:
    /// a row per request the session is running a query in, with the
    /// request's handles and statement offsets as <c>sys.dm_exec_requests</c>
    /// reports them and the live ShowPlan XML of the statement
    /// (<c>Simulation.LiveQueryPlan</c>). A request in a statement with no
    /// query plan, a session running nothing, and a NULL id answer no rows
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static Selection ParseQueryStatisticsXml(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(QueryStatisticsXmlSchema, QueryStatisticsXmlColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateQueryStatisticsXml(arguments[0], batch, outerResolver))
        {
            ColumnNullability = QueryStatisticsXmlNullability,
        };
    }

    private static List<byte[]> EnumerateQueryStatisticsXml(Expression sessionExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var value = sessionExpr.Run(new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch));
        if (value.IsNull)
            return [];
        var sessionId = value.CoerceTo(SqlType.Int32).AsInt32;
        var simulation = batch.Connection.Simulation;
        SimulatedDbConnection? session = null;
        foreach (var connection in simulation.SnapshotConnections())
        {
            if (connection.Spid == sessionId)
                session = connection;
        }
        var rows = new List<byte[]>();
        if (session?.Session.BatchText is not { } batchText)
            return rows;
        foreach (var request in BuiltInResources.RequestRows(batch))
        {
            if (request[0].AsInt16 != sessionId || request[3].AsString is "sleeping")
                continue;
            if (simulation.LiveQueryPlan(session, batchText, session.Session.StatementStartIndex, session.CurrentDatabase) is not { } document)
                continue;
            rows.Add(RowEncoder.EncodeRow(QueryStatisticsXmlSchema,
            [
                request[0], request[1], request[5], request[8], SqlValue.FromXml(document),
                request[6].IsNull ? SqlValue.FromInt32(0) : request[6], request[7].IsNull ? SqlValue.FromInt32(-1) : request[7],
            ]));
            break;
        }
        return rows;
    }

    /// <summary>
    /// The cached plan a plan-handle argument names, or null for a NULL handle
    /// and one naming no plan. A string is refused as real refuses converting
    /// one to <c>varbinary</c> implicitly (Msg 257), and a handle shorter than
    /// a plan handle is Msg 569 in <paramref name="functionName"/>'s state.
    /// </summary>
    private static CachedPlan? PlanOfHandle(Expression handleExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, string functionName, byte invalidState)
    {
        var value = handleExpr.Run(new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch));
        if (value.IsNull)
            return null;
        if (value.Type.Category == SqlTypeCategory.String)
            throw SimulatedSqlException.ImplicitConversionNotAllowed(SimulatedSqlException.FamilyRootName(value.Type), "varbinary");
        var handle = value.CoerceTo(SqlType.VarbinaryMax).AsBytes;
        if (handle.Length < BuiltInResources.SqlHandleLength)
            throw SimulatedSqlException.InvalidDmvHandle(functionName, invalidState);
        if (handle[0] is not (5 or 6))
            return null;
        foreach (var plan in batch.Connection.Simulation.LiveCachedPlans())
        {
            if (handle.AsSpan(0, BuiltInResources.SqlHandleLength).SequenceEqual(plan.PlanHandle))
                return plan;
        }
        return null;
    }
}
