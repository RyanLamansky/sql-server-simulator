using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SESSION_CONTEXT(N'key')</c>: reads a value previously stored by
/// <c>sp_set_session_context</c> on this session. Real SQL Server returns
/// <c>sql_variant</c> (type-preserving); the simulator has no
/// <c>sql_variant</c>, so the stored value surfaces as
/// <see cref="SqlType.NVarchar"/> — the same proxy <see cref="ServerProperty"/>
/// uses. A missing key returns NULL; a NULL key raises Msg 8116
/// (probe-confirmed). Keys are case-sensitive — see
/// <see cref="SimulatedDbConnection.SessionContext"/>.
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
        return runtime.Batch.Connection.SessionContext.TryGetValue(key, out var entry)
            ? entry.Value.CoerceTo(SqlType.NVarchar)
            : SqlValue.Null(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

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
/// attributes. Real SQL Server projects <c>sql_variant</c>; the simulator
/// surfaces values as <see cref="SqlType.NVarchar"/> (same proxy as
/// <see cref="ServerProperty"/>). The in-process connection has no real
/// network identity, so transport-shaped properties report fixed
/// placeholder constants (probe-confirmed <c>net_transport = 'TCP'</c>,
/// <c>protocol_type = 'TSQL'</c>); unknown property → NULL.
/// </summary>
internal sealed class ConnectionProperty : Expression
{
    private static readonly FrozenDictionary<string, string?> Properties = new Dictionary<string, string?>
    {
        ["net_transport"] = "TCP",
        ["protocol_type"] = "TSQL",
        ["auth_scheme"] = "SQL",
        ["physical_net_transport"] = "TCP",
        ["local_net_address"] = null,
        ["local_tcp_port"] = null,
        ["client_net_address"] = null,
        ["sni_consumer_node"] = null,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

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
            return SqlValue.Null(SqlType.NVarchar);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        return Properties.TryGetValue(name, out var value) && value is not null
            ? SqlValue.FromNVarchar(value)
            : SqlValue.Null(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"CONNECTIONPROPERTY({this.nameArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>CURRENT_TRANSACTION_ID()</c>: returns a <c>bigint</c> transaction
/// identifier. Apps use it for logging / correlation, not correctness; the
/// simulator approximates it with the database's monotonic commit counter
/// (<see cref="Database.CurrentTransactionCommitId"/>) — a plausible,
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
        SqlValue.FromInt64(runtime.Batch.CurrentDatabase.CurrentTransactionCommitId);

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
