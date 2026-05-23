using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared resolution for the principal-name scalar functions. The simulator
/// emulates a single fixed principal — <c>dbo</c> — across all
/// session/login/db-user surfaces, matching the placeholder approach used
/// by <see cref="SchemaName"/>'s no-arg form. Real SQL Server's separation
/// between login and db-user identity isn't modeled; every identity scalar
/// converges on <c>dbo</c>.
/// </summary>
internal static class PrincipalPlaceholders
{
    public const string CurrentLogin = "dbo";

    public const string CurrentUser = "dbo";

    public const string CurrentHost = "";

    public const string CurrentApplication = "";
}

/// <summary>
/// SQL <c>USER_NAME([id])</c>: returns the database user name for the
/// given <c>database_principal_id</c>, or the calling user's name when
/// called with no argument. The simulator looks the id up in
/// <see cref="Database.Principals"/> (seeded with <c>public</c>=0,
/// <c>dbo</c>=1, <c>guest</c>=2, <c>INFORMATION_SCHEMA</c>=3, <c>sys</c>=4);
/// unknown id returns NULL, NULL argument returns NULL. Result type is
/// <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class UserName : Expression
{
    private readonly Expression? idArg;

    public UserName(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.idArg is null)
            return SqlValue.FromString(SqlType.SystemName, PrincipalPlaceholders.CurrentUser);
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var principal in runtime.Batch.CurrentDatabase.Principals.Values)
        {
            if (principal.PrincipalId == id)
                return SqlValue.FromString(SqlType.SystemName, principal.Name);
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.idArg is null ? "USER_NAME()" : $"USER_NAME({this.idArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>SUSER_NAME([id])</c> / <c>SUSER_SNAME([sid])</c>: returns the
/// server-login name for the given id/sid, or the calling login when
/// called with no argument. The simulator emulates a single fixed login
/// (<c>dbo</c>); any id/sid input that isn't NULL produces the same
/// placeholder name. NULL argument returns NULL. Result type is
/// <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class SUserName : Expression
{
    private readonly bool isSidVariant;
    private readonly Expression? arg;

    public SUserName(ParserContext context, bool isSidVariant)
    {
        this.isSidVariant = isSidVariant;
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.arg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.arg is null)
            return SqlValue.FromString(SqlType.SystemName, PrincipalPlaceholders.CurrentLogin);
        var argValue = this.arg.Run(runtime);
        return argValue.IsNull
            ? SqlValue.Null(SqlType.SystemName)
            : SqlValue.FromString(SqlType.SystemName, PrincipalPlaceholders.CurrentLogin);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.arg is null
        ? (this.isSidVariant ? "SUSER_SNAME()" : "SUSER_NAME()")
        : $"{(this.isSidVariant ? "SUSER_SNAME" : "SUSER_NAME")}({this.arg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>ORIGINAL_LOGIN()</c>: returns the original login of the session
/// before any <c>EXECUTE AS</c> impersonation. The simulator doesn't model
/// impersonation, so this always returns the placeholder login
/// (<c>dbo</c>). Result type is <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class OriginalLogin : Expression
{
    public OriginalLogin(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("original_login", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromString(SqlType.SystemName, PrincipalPlaceholders.CurrentLogin);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => "ORIGINAL_LOGIN()";
}

/// <summary>
/// SQL <c>HOST_NAME()</c>: returns the workstation name of the connecting
/// client. The simulator doesn't carry a workstation identity on
/// <see cref="SimulatedDbConnection"/>, so this returns the empty string
/// — matching the common pool-default observed on real SQL Server.
/// Result type is <see cref="SqlType.NVarchar"/>.
/// </summary>
internal sealed class HostName : Expression
{
    public HostName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("host_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(PrincipalPlaceholders.CurrentHost);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => "HOST_NAME()";
}

/// <summary>
/// SQL <c>APP_NAME()</c>: returns the application name set in the
/// connection string. The simulator doesn't carry an application identity
/// on <see cref="SimulatedDbConnection"/>, so this returns the empty
/// string. Result type is <see cref="SqlType.NVarchar"/>.
/// </summary>
internal sealed class AppName : Expression
{
    public AppName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("app_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(PrincipalPlaceholders.CurrentApplication);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => "APP_NAME()";
}

/// <summary>
/// Backs the parens-less identity keywords <c>CURRENT_USER</c>,
/// <c>SESSION_USER</c>, <c>SYSTEM_USER</c>, and bare <c>USER</c>. All four
/// converge on the simulator's fixed-principal placeholder
/// (<see cref="PrincipalPlaceholders.CurrentUser"/>), matching how
/// <see cref="SchemaName"/>'s no-arg form returns <c>dbo</c>. Result type is
/// <see cref="SqlType.SystemName"/> (sysname). Wired through
/// <see cref="Expression.Parse"/>'s reserved-keyword switch rather than
/// <c>ResolveBuiltIn</c> because the SQL grammar permits no parens.
/// </summary>
internal sealed class CurrentPrincipalKeyword(string keywordText) : Expression
{
    private readonly string keywordText = keywordText;

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromString(SqlType.SystemName, PrincipalPlaceholders.CurrentUser);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.keywordText;
}
