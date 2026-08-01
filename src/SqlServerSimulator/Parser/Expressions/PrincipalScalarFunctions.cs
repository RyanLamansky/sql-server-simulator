using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Placeholder identity values for the surfaces the simulator doesn't yet
/// resolve per-session. <see cref="CurrentLogin"/> (<c>dbo</c>) is the fixed
/// server-login name a couple of login-lookup scalars still compare against;
/// the session-aware identity scalars instead read
/// <c>SimulatedDbConnection.Security</c>.
/// </summary>
internal static class PrincipalPlaceholders
{
    public const string CurrentLogin = "dbo";
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
            return SqlValue.FromString(SqlType.SystemName, runtime.Batch.Connection.Security.Effective.DatabasePrincipalName);
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var id = ScalarArguments.CoerceToInt(idValue);
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
            return SqlValue.FromString(SqlType.SystemName, runtime.Batch.Connection.Security.Effective.LoginName);
        var argValue = this.arg.Run(runtime);
        return argValue.IsNull
            ? SqlValue.Null(SqlType.SystemName)
            : SqlValue.FromString(SqlType.SystemName, runtime.Batch.Connection.Security.Effective.LoginName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.arg is null
        ? (this.isSidVariant ? "SUSER_SNAME()" : "SUSER_NAME()")
        : $"{(this.isSidVariant ? "SUSER_SNAME" : "SUSER_NAME")}({this.arg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>SUSER_SID([login [, Param2]])</c>: returns the binary SID for a
/// server login — the calling session's login with no argument. Mirrors the
/// <c>sys.server_principals</c> sid surface: <c>sa</c> is the well-known
/// single byte <c>0x01</c>, registry logins (<c>CREATE LOGIN</c>) get their
/// deterministic 16-byte synthetic sid, and an unknown name returns NULL.
/// The no-argument form returns <c>0x01</c>, matching the
/// <c>sys.dm_exec_sessions.security_id</c> placeholder for the simulator's
/// fixed session principal. The optional <c>Param2</c> (real's
/// skip-name-validation flag) parses and is ignored. Result type is
/// <c>varbinary(85)</c>.
/// </summary>
internal sealed class SUserSid : Expression
{
    private readonly Expression? loginArg;

    public SUserSid(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.loginArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            _ = Parse(context);
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.loginArg is null)
            return SqlValue.FromVarbinary([0x01]);
        var nameValue = this.loginArg.Run(runtime);
        if (nameValue.IsNull)
            return SqlValue.Null(SqlType.Varbinary);
        var name = nameValue.CoerceTo(SqlType.SystemName).AsString;
        return Collation.Baseline.Equals(name, "sa")
            ? SqlValue.FromVarbinary([0x01])
            : runtime.Batch.Connection.Simulation.Logins.ContainsKey(name)
                ? SqlValue.FromVarbinary(BuiltInResources.DeriveLoginSid(name))
                : SqlValue.Null(SqlType.Varbinary);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varbinary;

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => true;

    internal override string DebugDisplay() => this.loginArg is null ? "SUSER_SID()" : $"SUSER_SID({this.loginArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>SID_BINARY(name)</c>: resolves a Windows / Entra-ID principal
/// name to its binary SID. Probe-confirmed against SQL Server 2025: it
/// returns NULL even for existing SQL-auth logins (<c>sid_binary(N'sa')</c>
/// is NULL) — it only resolves directory principals, which the simulator
/// never hosts — so a constant NULL <c>varbinary(85)</c> is faithful for
/// every input the simulator can see. The argument is still parsed and
/// evaluated (one required argument). SSMS's Select-Top-1000
/// server-properties batch calls it on the service's Windows group name.
/// </summary>
internal sealed class SidBinary : Expression
{
    private readonly Expression arg;

    public SidBinary(ParserContext context)
    {
        this.arg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        _ = this.arg.Run(runtime);
        return SqlValue.Null(SqlType.Varbinary);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varbinary;

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => true;

    internal override string DebugDisplay() => $"SID_BINARY({this.arg.DebugDisplay()})";
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
        SqlValue.FromString(SqlType.SystemName, runtime.Batch.Connection.Security.OriginalLoginName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => "ORIGINAL_LOGIN()";
}

/// <summary>
/// SQL <c>HOST_NAME()</c>: returns the workstation name of the connecting
/// client — the connection string's <c>Workstation ID</c> keyword in-process,
/// LOGIN7's <c>HostName</c> field over the TDS endpoint, and the empty string
/// when neither supplied one (the common pool-default observed on real SQL
/// Server). Result type is <see cref="SqlType.NVarchar"/>.
/// </summary>
internal sealed class HostName : Expression
{
    public HostName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("host_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(runtime.Batch.Connection.ClientHostName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => "HOST_NAME()";
}

/// <summary>
/// SQL <c>APP_NAME()</c>: returns the application name the client reported —
/// the connection string's <c>Application Name</c> keyword in-process,
/// LOGIN7's <c>AppName</c> field over the TDS endpoint, and the empty string
/// when neither supplied one. Result type is <see cref="SqlType.NVarchar"/>.
/// </summary>
internal sealed class AppName : Expression
{
    public AppName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("app_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(runtime.Batch.Connection.ClientApplicationName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => "APP_NAME()";
}

/// <summary>
/// Backs the parens-less identity keywords <c>CURRENT_USER</c>,
/// <c>SESSION_USER</c>, bare <c>USER</c> (the effective database user), and
/// <c>SYSTEM_USER</c> (the effective login, <c>isLogin</c>). All read the
/// session's effective security frame; an unimpersonated in-process session
/// reports <c>dbo</c>. Result type is <see cref="SqlType.SystemName"/> (sysname).
/// Wired through <see cref="Expression.Parse"/>'s reserved-keyword switch rather
/// than <c>ResolveBuiltIn</c> because the SQL grammar permits no parens.
/// </summary>
internal sealed class CurrentPrincipalKeyword(string keywordText, bool isLogin = false) : Expression
{
    private readonly string keywordText = keywordText;

    // SYSTEM_USER reports the effective login (like SUSER_SNAME); CURRENT_USER /
    // SESSION_USER / USER report the effective database user.
    private readonly bool isLogin = isLogin;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var effective = runtime.Batch.Connection.Security.Effective;
        return SqlValue.FromString(SqlType.SystemName, this.isLogin ? effective.LoginName : effective.DatabasePrincipalName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.keywordText;
}
