using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SESSION_CONTEXT(N'key')</c>: reads a value previously stored by
/// <c>sp_set_session_context</c> on this session. Like real SQL Server, the
/// result is <c>sql_variant</c> (<see cref="SqlType.SqlVariant"/>) preserving
/// the stored value's base type — an <c>int</c> stored round-trips as <c>int</c>,
/// an <c>nvarchar</c> as <c>nvarchar</c>. A missing key returns a NULL
/// <c>sql_variant</c>; a NULL key raises Msg 8116 (probe-confirmed). Keys are
/// case-sensitive — see <see cref="SimulatedDbConnection.SessionContext"/>.
/// </summary>
internal sealed class SessionContext : Expression
{
    private readonly Expression keyArg;

    public SessionContext(ParserContext context)
    {
        this.keyArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var k = this.keyArg.Run(runtime);
        if (k.IsNull)
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex: 1, "session_context");
        var key = k.CoerceTo(SqlType.NVarchar).AsString;
        return runtime.Batch.Connection.SessionContext.TryGetValue(key, out var entry) && !entry.Value.IsNull
            ? SqlValue.FromVariant(entry.Value)
            : SqlValue.Null(SqlType.SqlVariant);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() => $"SESSION_CONTEXT({this.keyArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>CONTEXT_INFO()</c>: returns the session's 128-byte context-info
/// buffer, or NULL when <c>SET CONTEXT_INFO</c> hasn't run. The buffer is
/// always exactly 128 bytes once set (SQL Server right-pads / truncates),
/// so <c>DATALENGTH(CONTEXT_INFO())</c> is 128. Result type is
/// <see cref="SqlType.Varbinary"/>.
/// </summary>
internal sealed class ContextInfoFunction : Expression
{
    public ContextInfoFunction(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("context_info", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        runtime.Batch.Connection.ContextInfo is { } bytes
            ? SqlValue.FromVarbinary(bytes)
            : SqlValue.Null(SqlType.Varbinary);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varbinary;

    internal override string DebugDisplay() => "CONTEXT_INFO()";
}

/// <summary>
/// SQL <c>CONNECTIONPROPERTY('property')</c>: returns connection-level
/// attributes. Like real SQL Server, the result is <c>sql_variant</c>
/// (<see cref="SqlType.SqlVariant"/>); the modeled properties carry an inner
/// <c>nvarchar</c> base type (probe-confirmed — the port / address properties
/// real types as <c>smallint</c> / <c>nvarchar</c> are unmodeled and return
/// NULL). The in-process connection has no real network identity, so
/// transport-shaped properties report fixed placeholder constants
/// (probe-confirmed <c>net_transport = 'TCP'</c>, <c>protocol_type = 'TSQL'</c>);
/// an unknown or unmodeled property → NULL <c>sql_variant</c>.
/// </summary>
internal sealed class ConnectionProperty : Expression
{
    private readonly Expression nameArg;

    public ConnectionProperty(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var n = this.nameArg.Run(runtime);
        if (n.IsNull)
            return SqlValue.Null(SqlType.SqlVariant);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument. The null-valued modeled
        // properties (local_net_address / local_tcp_port / client_net_address /
        // sni_consumer_node) fall to the default arm — same NULL sql_variant
        // an unknown name yields.
        if (name.Length > 32)
            return SqlValue.Null(SqlType.SqlVariant);
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "AUTH_SCHEME" => SqlValue.FromVariant(SqlValue.FromNVarchar("SQL")),
            "NET_TRANSPORT" => SqlValue.FromVariant(SqlValue.FromNVarchar("TCP")),
            "PHYSICAL_NET_TRANSPORT" => SqlValue.FromVariant(SqlValue.FromNVarchar("TCP")),
            "PROTOCOL_TYPE" => SqlValue.FromVariant(SqlValue.FromNVarchar("TSQL")),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() => $"CONNECTIONPROPERTY({this.nameArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>CURRENT_TRANSACTION_ID()</c>: returns a <c>bigint</c> transaction
/// identifier. Apps use it for logging / correlation, not correctness; the
/// simulator approximates it with the instance's monotonic commit counter
/// (<see cref="Simulation.CurrentTransactionCommitId"/>) — a plausible,
/// increasing value rather than a stable per-transaction id.
/// </summary>
internal sealed class CurrentTransactionId : Expression
{
    public CurrentTransactionId(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("current_transaction_id", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt64(runtime.Batch.Connection.Simulation.CurrentTransactionCommitId);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.BigInt;

    internal override string DebugDisplay() => "CURRENT_TRANSACTION_ID()";
}

/// <summary>
/// SQL <c>CURRENT_REQUEST_ID()</c>: returns the <c>int</c> request id within
/// the session. The simulator doesn't multiplex requests per session, so it
/// reports <c>0</c> (probe-confirmed value for a single-request session).
/// </summary>
internal sealed class CurrentRequestId : Expression
{
    public CurrentRequestId(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("current_request_id", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromInt32(0);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "CURRENT_REQUEST_ID()";
}
