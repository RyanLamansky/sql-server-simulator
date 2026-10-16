using SqlServerSimulator.Network;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CURRENT_TIMEZONE()</c> and <c>CURRENT_TIMEZONE_ID()</c>: the
/// server's time zone, which is UTC because the simulated server's clock is.
/// Both are <c>nvarchar(256)</c> in the database's collation, answering
/// <c>(UTC) Coordinated Universal Time</c> and <c>UTC</c> as a Linux server
/// on UTC does (probed 2026-10-15 against SQL Server 2025).
/// </summary>
internal sealed class CurrentTimeZone : Expression
{
    private readonly bool id;

    public CurrentTimeZone(ParserContext context, bool id)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments(id ? "current_timezone_id" : "current_timezone", 0);
        this.id = id;
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(ResultType(runtime.Batch), this.id ? "UTC" : "(UTC) Coordinated Universal Time");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType(batch);

    private static NVarcharSqlType ResultType(BatchContext batch) =>
        NVarcharSqlType.Get(256, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    internal override string DebugDisplay() => this.id ? "CURRENT_TIMEZONE_ID()" : "CURRENT_TIMEZONE()";

    internal override void Describe(NodeShape shape) => shape.Local(this.id);
}

/// <summary>
/// SQL <c>HOST_ID()</c>: the client's process id as <c>char(10)</c>, padded,
/// never NULL — the value <c>sys.dm_exec_sessions.host_process_id</c> and
/// <c>sysprocesses.hostprocess</c> report for the session: the process id a
/// TDS client's login sent, and the host process for an in-process session
/// (probed 2026-10-15 against SQL Server 2025).
/// </summary>
internal sealed class HostId : Expression
{
    public HostId(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("host_id", 0);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var processId = runtime.Batch.Connection.ClientProcessId;
        return SqlValue.FromChar(ResultType(runtime.Batch), processId.ToString(System.Globalization.CultureInfo.InvariantCulture).PadRight(10));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType(batch);

    private static CharSqlType ResultType(BatchContext batch) =>
        CharSqlType.Get(10, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "HOST_ID()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// SQL <c>PUBLISHINGSERVERNAME()</c>: the server a replicated database's
/// publisher runs on, which for a database that isn't replicated is the
/// server itself — <c>@@SERVERNAME</c>, as <c>nvarchar(256)</c> (probed
/// 2026-10-15 against SQL Server 2025).
/// </summary>
internal sealed class PublishingServerName : Expression
{
    public PublishingServerName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("publishingservername", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(ResultType(runtime.Batch), TdsSession.ServerName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType(batch);

    private static NVarcharSqlType ResultType(BatchContext batch) =>
        NVarcharSqlType.Get(256, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    internal override string DebugDisplay() => "PUBLISHINGSERVERNAME()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// SQL <c>FILEPROPERTYEX(file_name, 'property')</c>: the Azure storage
/// properties of a database file (<c>AccountType</c>, <c>BlobTier</c>,
/// <c>IsInferredTier</c>, <c>IsPageBlob</c>), which a box server's files
/// don't have — so a <c>sql_variant</c> NULL for every argument, a known
/// file and property included (probed 2026-10-15 against SQL Server 2025).
/// Both arguments are still evaluated, so one that raises raises.
/// </summary>
internal sealed class FilePropertyEx : Expression
{
    private readonly Expression nameArg;
    private readonly Expression propertyArg;

    public FilePropertyEx(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("filepropertyex", 2);
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
        {
            throw context.Token is Tokens.Operator { Character: ')' }
                ? SimulatedSqlException.FunctionRequiresNArguments("filepropertyex", 2)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("filepropertyex", 2);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        _ = this.nameArg.Run(runtime);
        _ = this.propertyArg.Run(runtime);
        return SqlValue.Null(SqlType.SqlVariant);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() =>
        $"FILEPROPERTYEX({this.nameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.nameArg).Child(this.propertyArg);
}
