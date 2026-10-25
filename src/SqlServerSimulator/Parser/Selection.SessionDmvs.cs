using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    private static readonly SqlType[] SqlTextSchema =
    [
        SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.Bit, SqlType.NVarcharMax,
    ];

    private static readonly string[] SqlTextColumnNames = ["dbid", "objectid", "number", "encrypted", "text"];

    // Only encrypted is NOT NULL (probed 2026-10-16 against SQL Server 2025).
    private static readonly bool[] SqlTextNullability = [true, true, true, false, true];

    internal static readonly SqlType[] InputBufferSchema =
    [
        NVarcharSqlType.Get(256, Collation.Baseline, Coercibility.Implicit), SqlType.SmallInt, SqlType.NVarcharMax,
    ];

    private static readonly string[] InputBufferColumnNames = ["event_type", "parameters", "event_info"];

    /// <summary>
    /// Parses a system TVF's parenthesized argument list, which takes exactly
    /// <paramref name="count"/> arguments: fewer — none at all included — is
    /// Msg 313 and more Msg 8144, both state 3 and at line 12 of the
    /// function's own definition, as real raises them for the
    /// <c>sys.dm_exec_*</c> functions (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    private static Expression[] ParseSystemFunctionArguments(ParserContext context, string functionName, int count) =>
        ParseSystemFunctionArguments(context, functionName, count, acceptsDefault: false)!;

    /// <summary>
    /// <see cref="ParseSystemFunctionArguments(ParserContext, string, int)"/>,
    /// where <paramref name="acceptsDefault"/> lets an argument after the
    /// first be <c>DEFAULT</c>, which leaves its slot null.
    /// </summary>
    private static Expression?[] ParseSystemFunctionArguments(ParserContext context, string functionName, int count, bool acceptsDefault)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var arguments = new Expression?[count];
        for (var i = 0; ; i++)
        {
            context.MoveNextRequired();
            if (i == 0 && context.Token is Operator { Character: ')' })
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, 3).PinLine(12);
            Expression? argument;
            if (acceptsDefault && i > 0 && context.Token is ReservedKeyword { Keyword: Keyword.Default })
            {
                argument = null;
                context.MoveNextRequired();
            }
            else
            {
                argument = Expression.Parse(context);
            }
            if (i < count)
                arguments[i] = argument;
            if (context.Token is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (i < count - 1)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, 3).PinLine(12);
            if (i >= count)
                throw SimulatedSqlException.TooManyArgumentsToFunction(functionName, 3).PinLine(12);
            break;
        }
        context.MoveNextOptional();
        return arguments;
    }

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_sql_text(handle)</c>: the text of
    /// the command a SQL handle names, found among the live sessions'
    /// current or most recent commands — the handles
    /// <c>sys.dm_exec_requests</c> and <c>sys.dm_exec_connections</c> hand
    /// out — and the cached plans' batches; the definition of the module a
    /// module's SQL handle names; and the text of the cached plan a plan
    /// handle names, with its database (probed 2026-10-15 against SQL Server
    /// 2025: a batch's SQL handle reports no database, its plan handle the
    /// one it compiled in). An unknown handle, and a NULL one, answer no rows;
    /// one too short, or of a type real doesn't resolve, is Msg 569, and a
    /// statement handle Msg 12413. Probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    public static Selection ParseSqlText(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(SqlTextSchema, SqlTextColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateSqlText(arguments[0], batch, outerResolver))
        {
            ColumnNullability = SqlTextNullability,
        };
    }

    private static List<byte[]> EnumerateSqlText(Expression handleExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: true);
        var value = handleExpr.Run(new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch));
        if (value.IsNull)
            return [];
        var handle = value.CoerceTo(SqlType.VarbinaryMax).AsBytes;
        if (handle.Length == LegacySqlHandleLength && handle[0] == 1)
            return LegacySqlTextRow(batch.Connection.Simulation, handle);
        if (handle.Length < BuiltInResources.SqlHandleLength)
            throw SimulatedSqlException.InvalidSqlTextHandle();
        switch (handle[0])
        {
            case 0 or 1 or 2 or 7:
                break;
            // A module's handle naming no database is malformed (probed
            // 2026-10-15 against SQL Server 2025); one naming a database
            // or object no longer there answers nothing.
            case 3 when handle[2] != 0 || handle[3] != 0:
                return ModuleSqlTextRow(batch.Connection.Simulation, handle);
            case 5 or 6:
                return batch.Connection.Simulation.HandleIndexFor(batch).FindPlan(handle) is { } plan
                    ? [RowEncoder.EncodeRow(SqlTextSchema, [
                        SqlValue.FromInt16(plan.Key.DatabaseId),
                        plan.Module is null ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(plan.ObjectId),
                        plan.Module is null ? SqlValue.Null(SqlType.SmallInt) : SqlValue.FromInt16(plan.Number),
                        SqlValue.FromBoolean(plan.Module is not null && plan.Text is null),
                        plan.Text is null ? SqlValue.Null(SqlType.NVarcharMax) : SqlValue.FromNVarchar(plan.Text),
                    ])]
                    : [];
            case 9:
                throw SimulatedSqlException.StatementSqlHandleNotProcessed();
            default:
                throw SimulatedSqlException.InvalidSqlTextHandle();
        }

        // A running batch's text answers ahead of a cached one's: a session's,
        // the prepared text simple parameterization runs one of its statements
        // under, or a MARS request's parked between its statements — none for
        // a batch carrying a secret.
        var index = batch.Connection.Simulation.HandleIndexFor(batch);
        if (index.FindRunning(handle, out var running))
            return running is null ? [] : SqlTextRow(running);
        return index.FindBatchPlan(handle) is { } cached ? SqlTextRow(cached.Text!) : [];

        static List<byte[]> SqlTextRow(string text) =>
            [RowEncoder.EncodeRow(SqlTextSchema, [
                SqlValue.Null(SqlType.SmallInt), SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.SmallInt),
                SqlValue.FromBoolean(false), SqlValue.FromNVarchar(text),
            ])];
    }

    /// <summary>The length of the batch handle <c>sysprocesses.sql_handle</c> carries.</summary>
    private const int LegacySqlHandleLength = 20;

    /// <summary>
    /// The <c>sys.dm_exec_sql_text</c> row of a twenty-byte batch handle, as
    /// <c>sysprocesses.sql_handle</c> carries one: the batch whose object id
    /// it holds, among the live sessions' commands and the cached batches,
    /// reported in the database the handle names (probed 2026-10-16 against
    /// SQL Server 2025, where the 64-byte handle reports none).
    /// </summary>
    private static List<byte[]> LegacySqlTextRow(Simulation simulation, byte[] handle)
    {
        var databaseId = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(handle.AsSpan(2));
        var objectId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(handle.AsSpan(4));
        string? found = null;
        foreach (var connection in simulation.SnapshotConnections())
        {
            if (connection.Session.BatchText is not { } text || simulation.HidesBatchText(text))
                continue;
            var reported = simulation.ReportedStatement(connection, text, connection.Session.StatementStartIndex, connection.CurrentDatabase).Text;
            if (BuiltInResources.AdHocObjectIdOf(reported) == objectId)
            {
                found = reported;
                break;
            }
            if (BuiltInResources.AdHocObjectIdOf(text) == objectId)
            {
                found = text;
                break;
            }
        }
        if (found is null)
        {
            foreach (var plan in simulation.LiveCachedPlans())
            {
                if (plan.IsBatch && plan.Text is { } text && BuiltInResources.AdHocObjectIdOf(text) == objectId)
                {
                    found = text;
                    break;
                }
            }
        }
        return found is null ? [] : [RowEncoder.EncodeRow(SqlTextSchema, [
            SqlValue.FromInt16(databaseId), SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.SmallInt),
            SqlValue.FromBoolean(false), SqlValue.FromNVarchar(found),
        ])];
    }

    /// <summary>
    /// The <c>sys.dm_exec_sql_text</c> row of the module a module's SQL handle
    /// names by its database and object id: its definition, NULL for an
    /// encrypted one, and the procedure number 1 for a procedure, 0 for
    /// another module; no row for a module no database holds.
    /// </summary>
    private static List<byte[]> ModuleSqlTextRow(Simulation simulation, byte[] handle)
    {
        var databaseId = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(handle.AsSpan(2));
        var objectId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(handle.AsSpan(4));
        if (Expressions.DbId.DatabaseWithId(simulation, databaseId) is not { } database)
            return [];
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var module in schema.SchemaObjects())
            {
                if (module.ObjectId != objectId || !SchemaObject.IsSqlModule(module) || module is View)
                    continue;
                if (!handle.AsSpan(0, BuiltInResources.SqlHandleLength).SequenceEqual(CachedPlan.ModuleSqlHandle(databaseId, module)))
                    return [];
                return [RowEncoder.EncodeRow(SqlTextSchema, [
                    SqlValue.FromInt16(databaseId),
                    SqlValue.FromInt32(objectId),
                    SqlValue.FromInt16(module is Procedure ? (short)1 : (short)0),
                    SqlValue.FromBoolean(module.DefinitionText is null),
                    module.DefinitionText is null ? SqlValue.Null(SqlType.NVarcharMax) : SqlValue.FromNVarchar(module.DefinitionText),
                ])];
            }
        }
        return [];
    }

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_input_buffer(session_id, request_id)</c>:
    /// the last command a session sent, as a language event. A session id no
    /// session holds, a request id other than 0, and a NULL answer no rows.
    /// Probed 2026-09-25 against SQL Server 2025, which gates it with Msg 300.
    /// </summary>
    public static Selection ParseInputBuffer(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 2);
        return new Selection(InputBufferSchema, InputBufferColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateInputBuffer(arguments[0], arguments[1], batch, outerResolver));
    }

    private static List<byte[]> EnumerateInputBuffer(Expression sessionExpr, Expression requestExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: false);
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var session = sessionExpr.Run(runtime);
        var request = requestExpr.Run(runtime);
        // A NULL request id reads the session's buffer as 0 does (probed
        // 2026-10-16 against SQL Server 2025).
        if (session.IsNull || (!request.IsNull && request.CoerceTo(SqlType.Int32).AsInt32 != 0))
            return [];
        return InputBufferOf(batch.Connection.Simulation, session.CoerceTo(SqlType.Int32).AsInt32) is { } row
            ? [RowEncoder.EncodeRow(InputBufferSchema, row)]
            : [];
    }

    /// <summary>
    /// The input-buffer row <c>sys.dm_exec_input_buffer</c> and
    /// <c>DBCC INPUTBUFFER</c> report for a session, or null when no live
    /// session holds <paramref name="spid"/>.
    /// </summary>
    internal static SqlValue[]? InputBufferOf(Simulation simulation, int spid)
    {
        foreach (var connection in simulation.SnapshotConnections())
        {
            if (connection.Spid != spid)
                continue;
            return
            [
                SqlValue.FromNVarchar("Language Event"),
                SqlValue.FromInt16(0),
                // A batch carrying a secret leaves the input buffer NULL.
                connection.Session.BatchText is { } text && !simulation.HidesBatchText(text) ? SqlValue.FromNVarchar(text) : SqlValue.Null(SqlType.NVarcharMax),
            ];
        }
        return null;
    }

    private static readonly SqlType[] ExecCursorsSchema =
    [
        SqlType.Int32, SqlType.Int32,
        NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit),
        VarbinarySqlType.Get(64), SqlType.Int32, SqlType.Int32, SqlType.BigInt, SqlType.DateTime,
        SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32,
        SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, VarbinarySqlType.Get(64), SqlType.BigInt,
    ];

    private static readonly string[] ExecCursorsColumnNames =
    [
        "session_id", "cursor_id", "name", "properties", "sql_handle", "statement_start_offset", "statement_end_offset",
        "plan_generation_num", "creation_time", "is_open", "is_async_population", "is_close_on_commit", "fetch_status",
        "fetch_buffer_size", "fetch_buffer_start", "ansi_position", "worker_time", "reads", "writes", "dormant_duration",
        "statement_sql_handle", "statement_context_id",
    ];

    private static readonly bool[] ExecCursorsNullability =
    [
        false, false, true, false, true, false, false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, true, true,
    ];

    /// <summary>
    /// Built-in system TVF <c>sys.dm_exec_cursors(session_id)</c>: one row per
    /// cursor a session has declared, open or not — every session's for 0 or
    /// NULL, none for a session id no session holds. A session without
    /// <c>VIEW SERVER STATE</c> sees only its own. Probed 2026-09-30 against
    /// SQL Server 2025, whose column shapes and per-state values the rows
    /// follow; the cost columns (<c>worker_time</c>, <c>reads</c>,
    /// <c>writes</c>, <c>dormant_duration</c>) read 0 and
    /// <c>plan_generation_num</c> 1, a cursor's plan never recompiling here.
    /// Another session's local cursors and cursor variables are listed while
    /// its batch holds them, as real lists them.
    /// </summary>
    public static Selection ParseExecCursors(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(ExecCursorsSchema, ExecCursorsColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateExecCursors(arguments[0], batch, outerResolver))
        {
            ColumnNullability = ExecCursorsNullability,
        };
    }

    private static List<byte[]> EnumerateExecCursors(Expression sessionExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var session = sessionExpr.Run(new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch));
        var spid = session.IsNull ? 0 : session.CoerceTo(SqlType.Int32).AsInt32;
        var security = batch.Connection.Security;
        var seesAll = security.EffectiveIsDbo || batch.Connection.Simulation.HoldsServerPermission(security.Effective.LoginName, Permission.ViewServerState);
        var rows = new List<(int Spid, int Handle, byte[] Row)>();
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
        {
            if ((spid != 0 && connection.Spid != spid) || (!seesAll && connection != batch.Connection))
                continue;
            var seen = new HashSet<Cursor>(ReferenceEqualityComparer.Instance);
            void Add(Cursor? cursor, string name)
            {
                if (cursor is not null && seen.Add(cursor))
                    rows.Add((connection.Spid, cursor.Handle, RowEncoder.EncodeRow(ExecCursorsSchema, ExecCursorsRow(connection.Spid, cursor, cursor.OriginVariable ?? name))));
            }
            foreach (var (cursor, name) in connection.FrameCursors)
                Add(cursor, name);
            foreach (var (name, cursor) in connection.Cursors)
                Add(cursor, name);
        }
        rows.Sort(static (a, b) => a.Spid != b.Spid ? a.Spid.CompareTo(b.Spid) : a.Handle.CompareTo(b.Handle));
        return rows.ConvertAll(static row => row.Row);
    }

    private static SqlValue[] ExecCursorsRow(int spid, Cursor cursor, string name) =>
    [
        SqlValue.FromInt32(spid),
        SqlValue.FromInt32(cursor.Handle),
        cursor.IsApiCursor ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(name),
        SqlValue.FromNVarchar(cursor.DmvProperties),
        SqlValue.FromVarbinary(BuiltInResources.SqlHandleOf(cursor.DeclaringText)),
        SqlValue.FromInt32(cursor.DeclaringStart * 2),
        SqlValue.FromInt32(cursor.DeclaringEnd * 2),
        SqlValue.FromInt64(1),
        SqlValue.FromDateTime(cursor.CreationTime),
        SqlValue.FromBoolean(cursor.IsOpen),
        SqlValue.FromBoolean(false),
        SqlValue.FromBoolean(false),
        SqlValue.FromInt32(cursor.ApiFetchBuffer is null ? cursor.FetchStatus : 0),
        SqlValue.FromInt32(cursor.ApiFetchBuffer is { } apiBuffer ? apiBuffer.Size : cursor.FetchBufferSize),
        SqlValue.FromInt32(cursor.ApiFetchBuffer is { } apiStart ? apiStart.Start : cursor.FetchBufferStart),
        SqlValue.FromInt32(1),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        cursor.StatementIdentity is { } identity ? SqlValue.FromVarbinary(identity.Handle) : SqlValue.Null(VarbinarySqlType.Get(64)),
        cursor.StatementIdentity is { } context ? SqlValue.FromInt64(context.ContextSettingsId) : SqlValue.Null(SqlType.BigInt),
    ];
}
