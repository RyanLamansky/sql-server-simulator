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
/// <see cref="Expression.MetadataNameType"/>.
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
            return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), runtime.Batch.Connection.Security.Effective.DatabasePrincipalName);
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var id = ScalarArguments.CoerceToInt(idValue);
        foreach (var (_, principal) in runtime.Batch.CurrentDatabase.Principals)
        {
            if (principal.PrincipalId == id)
                return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), principal.Name);
        }
        return SqlValue.Null(MetadataNameType(runtime.Batch));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.idArg is not null)
            _ = AssignmentRules.ArgumentType(this.idArg, SqlType.Int32, batch, resolveColumnType);
        return MetadataNameType(batch);
    }

    internal override string DebugDisplay() => this.idArg is null ? "USER_NAME()" : $"USER_NAME({this.idArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg);
}

/// <summary>
/// SQL <c>SUSER_NAME([id])</c> / <c>SUSER_SNAME([sid])</c>: returns the
/// server-login name for the given id/sid — the <c>sys.server_principals</c>
/// row carrying it, else NULL — or the calling login when called with no
/// argument. The id converts to <c>int</c> and the sid to <c>varbinary</c> as
/// an assignment would (probed 2026-09-25 against SQL Server 2025:
/// <c>SUSER_NAME(1)</c> and <c>SUSER_SNAME(0x01)</c> are <c>sa</c>,
/// <c>SUSER_NAME(2)</c> is <c>public</c>). Result type is
/// <see cref="Expression.MetadataNameType"/>.
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
            return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), runtime.Batch.Connection.Security.Effective.LoginName);
        var argValue = this.arg.Run(runtime);
        if (argValue.IsNull)
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var sid = this.isSidVariant ? argValue.CoerceTo(SqlType.Varbinary).AsBytes : null;
        var id = this.isSidVariant ? 0 : StringScalars.CoerceLengthArgument(argValue);
        foreach (var row in BuiltInResources.EnumerateSysServerPrincipals(runtime.Batch, runtime.Batch.CurrentDatabase))
        {
            if (sid is not null ? !row[2].IsNull && row[2].AsBytes.AsSpan().SequenceEqual(sid) : row[1].AsInt32 == id)
                return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), row[0].AsString);
        }
        if (sid is not null && WellKnownWindowsAccount(sid) is { } account)
            return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), account);
        return SqlValue.Null(MetadataNameType(runtime.Batch));
    }

    /// <summary>
    /// The built-in Windows accounts <c>SUSER_SNAME</c> names by SID though no
    /// login maps them (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    private static string? WellKnownWindowsAccount(byte[] sid) => Convert.ToHexString(sid) switch
    {
        "010100000000000512000000" => @"NT AUTHORITY\SYSTEM",
        "01020000000000052000000020020000" => @"BUILTIN\Administrators",
        "01020000000000052000000021020000" => @"BUILTIN\Users",
        _ => null,
    };

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.arg is not null)
            _ = AssignmentRules.ArgumentType(this.arg, this.isSidVariant ? SqlType.Varbinary : SqlType.Int32, batch, resolveColumnType);
        return MetadataNameType(batch);
    }

    internal override string DebugDisplay() => this.arg is null
        ? (this.isSidVariant ? "SUSER_SNAME()" : "SUSER_NAME()")
        : $"{(this.isSidVariant ? "SUSER_SNAME" : "SUSER_NAME")}({this.arg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.isSidVariant).Child(this.arg);
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

    internal override bool ResultIsNullable(NullabilityContext context) => true;

    internal override string DebugDisplay() => this.loginArg is null ? "SUSER_SID()" : $"SUSER_SID({this.loginArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.loginArg);
}

/// <summary>
/// SQL <c>SID_BINARY(sid)</c>: converts a SID in its string form
/// (<c>S-1-5-32-544</c>, case-insensitive, the authority decimal or
/// <c>0x</c> hex) to the binary one — revision, subauthority count, the
/// 48-bit big-endian authority, then each subauthority little-endian. Any
/// other input, a login name such as <c>sa</c> or a binary value included,
/// is NULL (probed 2026-07-10 and 2026-09-30 against SQL Server 2025). SMO
/// and SSMS's server-properties batch call it on the service's Windows
/// group SID.
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
        var value = this.arg.Run(runtime);
        return value.IsNull || value.Type.Category != SqlTypeCategory.String || Parse(value.AsString) is not { } sid
            ? SqlValue.Null(SqlType.Varbinary)
            : SqlValue.FromVarbinary(sid);
    }

    /// <summary>The binary form of a string SID, or null when the text isn't one.</summary>
    internal static byte[]? Parse(string text)
    {
        var parts = text.Split('-');
        if (parts.Length is < 3 or > 18 || !parts[0].Equals("S", StringComparison.OrdinalIgnoreCase)
            || !byte.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision))
        {
            return null;
        }
        var authorityText = parts[2];
        var hex = authorityText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? authorityText[2..] : authorityText, hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var authority) || authority > 0xFFFF_FFFF_FFFF)
        {
            return null;
        }
        var sid = new byte[8 + (4 * (parts.Length - 3))];
        sid[0] = revision;
        sid[1] = (byte)(parts.Length - 3);
        for (var i = 0; i < 6; i++)
            sid[2 + i] = (byte)(authority >> (8 * (5 - i)));
        for (var i = 3; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var subAuthority))
                return null;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sid.AsSpan(8 + (4 * (i - 3))), subAuthority);
        }
        return sid;
    }

    /// <summary>
    /// The name takes a string or a binary, but not a legacy LOB, a
    /// <c>sql_variant</c> or a bare <c>NULL</c> (Msg 8116, probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (IsUntypedNullLiteral(this.arg))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 1, "sid_binary");
        _ = StringScalars.RequireStringArgument(this.arg, this.arg.GetSqlType(batch, resolveColumnType), "sid_binary", 1, acceptsBinary: true, acceptsLegacyLob: false);
        return SqlType.Varbinary;
    }

    internal override bool ResultIsNullable(NullabilityContext context) => true;

    internal override string DebugDisplay() => $"SID_BINARY({this.arg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.arg);
}

/// <summary>
/// SQL <c>ORIGINAL_LOGIN()</c>: returns the login the session connected as,
/// unchanged by any <c>EXECUTE AS</c> impersonation. Unlike the other
/// principal-name scalars its result is <c>nvarchar(4000)</c> (probed
/// 2026-09-25 against SQL Server 2025), in the database's collation at
/// coercible-default.
/// </summary>
internal sealed class OriginalLogin : Expression
{
    public OriginalLogin(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("original_login", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(ResultType(runtime.Batch), runtime.Batch.Connection.Security.OriginalLoginName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType(batch);

    private static NVarcharSqlType ResultType(BatchContext batch) =>
        NVarcharSqlType.Get(4000, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    internal override string DebugDisplay() => "ORIGINAL_LOGIN()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// SQL <c>HOST_NAME()</c>: returns the workstation name of the connecting
/// client — the connection string's <c>Workstation ID</c> keyword in-process,
/// LOGIN7's <c>HostName</c> field over the TDS endpoint, and the empty string
/// when neither supplied one (the common pool-default observed on real SQL
/// Server). Result type is <c>nvarchar(128)</c> (probed 2026-10-02 against SQL
/// Server 2025, through a computed column's <c>max_length</c>).
/// </summary>
internal sealed class HostName : Expression
{
    public HostName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("host_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), runtime.Batch.Connection.ClientHostName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => MetadataNameType(batch);

    internal override string DebugDisplay() => "HOST_NAME()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// SQL <c>APP_NAME()</c>: returns the application name the client reported —
/// the connection string's <c>Application Name</c> keyword in-process,
/// LOGIN7's <c>AppName</c> field over the TDS endpoint, and the empty string
/// when neither supplied one. Result type is <c>nvarchar(128)</c>, as
/// <see cref="HostName"/>'s.
/// </summary>
internal sealed class AppName : Expression
{
    public AppName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("app_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), runtime.Batch.Connection.ClientApplicationName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => MetadataNameType(batch);

    internal override string DebugDisplay() => "APP_NAME()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs the parens-less identity keywords <c>CURRENT_USER</c>,
/// <c>SESSION_USER</c>, bare <c>USER</c> (the effective database user), and
/// <c>SYSTEM_USER</c> (the effective login, <c>isLogin</c>). All read the
/// session's effective security frame; an unimpersonated in-process session
/// reports <c>dbo</c>. Result type is <see cref="Expression.MetadataNameType"/>.
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
        return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), this.isLogin ? effective.LoginName : effective.DatabasePrincipalName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => MetadataNameType(batch);

    internal override string DebugDisplay() => this.keywordText;

    internal override void Describe(NodeShape shape) => shape.Local(this.keywordText).Local(this.isLogin);
}
