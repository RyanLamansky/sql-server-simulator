using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    private static readonly SqlType[] SqlTextSchema =
    [
        SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.Bit, SqlType.NVarcharMax,
    ];

    private static readonly string[] SqlTextColumnNames = ["dbid", "objectid", "number", "encrypted", "text"];

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
    private static Expression[] ParseSystemFunctionArguments(ParserContext context, string functionName, int count)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var arguments = new Expression[count];
        for (var i = 0; ; i++)
        {
            context.MoveNextRequired();
            if (i == 0 && context.Token is Operator { Character: ')' })
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, 3).PinLine(12);
            var argument = Expression.Parse(context);
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
    /// out. An unknown handle, and a NULL one, answer no rows; one too short,
    /// or of a type real doesn't resolve, is Msg 569, and a statement handle
    /// Msg 12413. Probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    public static Selection ParseSqlText(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(SqlTextSchema, SqlTextColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateSqlText(arguments[0], batch, outerResolver));
    }

    private static List<byte[]> EnumerateSqlText(Expression handleExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        BuiltInResources.DemandServerPerformanceState(batch, policyWording: true);
        var value = handleExpr.Run(new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch));
        if (value.IsNull)
            return [];
        var handle = value.CoerceTo(SqlType.VarbinaryMax).AsBytes;
        if (handle.Length < BuiltInResources.SqlHandleLength)
            throw SimulatedSqlException.InvalidSqlTextHandle();
        switch (handle[0])
        {
            case 0 or 1 or 2 or 5 or 6 or 7:
                break;
            case 9:
                throw SimulatedSqlException.StatementSqlHandleNotProcessed();
            default:
                throw SimulatedSqlException.InvalidSqlTextHandle();
        }

        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
        {
            if (Matches(connection.Session.BatchText, handle))
                return SqlTextRow(connection.Session.BatchText!);
            // A MARS request parked between its statements is running too.
            foreach (var parked in connection.ParkedRequests())
            {
                if (Matches(parked.BatchText, handle))
                    return SqlTextRow(parked.BatchText!);
            }
        }
        return [];

        static bool Matches(string? text, byte[] handle) =>
            text is not null && BuiltInResources.SqlHandleOf(text).AsSpan().SequenceEqual(handle.AsSpan(0, BuiltInResources.SqlHandleLength));

        static List<byte[]> SqlTextRow(string text) =>
            [RowEncoder.EncodeRow(SqlTextSchema, [
                SqlValue.Null(SqlType.SmallInt), SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.SmallInt),
                SqlValue.FromBoolean(false), SqlValue.FromNVarchar(text),
            ])];
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
        if (session.IsNull || request.IsNull || request.CoerceTo(SqlType.Int32).AsInt32 != 0)
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
                connection.Session.BatchText is { } text ? SqlValue.FromNVarchar(text) : SqlValue.Null(SqlType.NVarcharMax),
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
    /// The session's local cursors and cursor variables are reached only for
    /// the querying session; another session shows its global ones.
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
            // A session's local cursors and cursor variables live on the batch
            // running, which only the querying session's own reach sees.
            if (connection == batch.Connection)
            {
                foreach (var (name, cursor) in batch.LocalCursors)
                    Add(cursor, name);
                foreach (var (name, cursor) in batch.CursorVariables)
                    Add(cursor, "@" + name);
            }
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
        SqlValue.FromNVarchar(name),
        SqlValue.FromNVarchar(cursor.DmvProperties),
        SqlValue.FromVarbinary(BuiltInResources.SqlHandleOf(cursor.DeclaringText)),
        SqlValue.FromInt32(cursor.DeclaringStart * 2),
        SqlValue.FromInt32(cursor.DeclaringEnd * 2),
        SqlValue.FromInt64(1),
        SqlValue.FromDateTime(cursor.CreationTime),
        SqlValue.FromBoolean(cursor.IsOpen),
        SqlValue.FromBoolean(false),
        SqlValue.FromBoolean(false),
        SqlValue.FromInt32(cursor.FetchStatus),
        SqlValue.FromInt32(cursor.FetchBufferSize),
        SqlValue.FromInt32(cursor.FetchBufferStart),
        SqlValue.FromInt32(1),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        cursor.StatementIdentity is { } identity ? SqlValue.FromVarbinary(identity.Handle) : SqlValue.Null(VarbinarySqlType.Get(64)),
        cursor.StatementIdentity is { } context ? SqlValue.FromInt64(context.ContextSettingsId) : SqlValue.Null(SqlType.BigInt),
    ];
}
