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
    /// <paramref name="count"/> arguments: fewer is Msg 313 and more Msg 8144,
    /// both state 3 as real raises them for the <c>sys.dm_exec_*</c> functions.
    /// </summary>
    private static Expression[] ParseSystemFunctionArguments(ParserContext context, string functionName, int count)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var arguments = new Expression[count];
        for (var i = 0; ; i++)
        {
            context.MoveNextRequired();
            var argument = Expression.Parse(context);
            if (i < count)
                arguments[i] = argument;
            if (context.Token is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (i < count - 1)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, 3);
            if (i >= count)
                throw SimulatedSqlException.TooManyArgumentsToFunction(functionName, 3);
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
            if (connection.Session.BatchText is not { } text
                || !BuiltInResources.SqlHandleOf(text).AsSpan().SequenceEqual(handle.AsSpan(0, BuiltInResources.SqlHandleLength)))
            {
                continue;
            }
            return [RowEncoder.EncodeRow(SqlTextSchema, [
                SqlValue.Null(SqlType.SmallInt), SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.SmallInt),
                SqlValue.FromBoolean(false), SqlValue.FromNVarchar(text),
            ])];
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
}
