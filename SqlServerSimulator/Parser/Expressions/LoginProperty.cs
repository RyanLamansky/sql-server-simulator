using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LOGINPROPERTY(login_name, property_name)</c>: returns a property of
/// a SQL Server login. The simulator has no login model — every identity
/// scalar converges on the single fixed login (<c>dbo</c>, the same
/// placeholder <see cref="SUserName"/> reports), so only that login resolves;
/// any other login name behaves like a nonexistent login and returns NULL for
/// every property (probe-confirmed 2026-07-10: a nonexistent login yields NULL
/// across the board). NULL login / NULL property → NULL. An unrecognized
/// property name → NULL.
/// </summary>
/// <remarks>
/// <para>
/// Real SQL Server projects each property as <c>sql_variant</c> with a
/// per-property base type (<c>datetime</c> for the time properties, <c>int</c>
/// for the counters / boolean flags, <c>nvarchar</c> for the name properties,
/// <c>varbinary</c> for <c>PasswordHash</c>). The simulator doesn't model
/// <c>sql_variant</c>, so — following <see cref="ServerProperty"/> — every
/// value surfaces as <c>nvarchar</c>; callers casting to <c>int</c> /
/// <c>datetime</c> reach the value through implicit conversion.
/// </para>
/// <para>
/// For the fixed login the property values are plausible constants matching
/// the live probe's shape: <c>PasswordLastSetTime</c> is a fixed seed date;
/// the <c>BadPassword*</c> / lockout time sentinels are <c>1900-01-01</c>;
/// counts and <c>Is*</c> flags are <c>0</c>; <c>DaysUntilExpiration</c> /
/// <c>PasswordHash</c> / <c>PasswordHashAlgorithm</c> are NULL (a low-privilege
/// login sees NULL for the hash on the live server too, and the simulator
/// stores no login hash); <c>DefaultDatabase</c> is the session's current
/// database; <c>DefaultLanguage</c> is <c>us_english</c>. Property names are
/// case-insensitive.
/// </para>
/// <para>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/loginproperty-transact-sql
/// </para>
/// </remarks>
internal sealed class LoginProperty : Expression
{
    /// <summary>
    /// Fixed seed for <c>PasswordLastSetTime</c> — the simulator has no
    /// install/password-set event, so it reports a stable, plausible date the
    /// way seeded object metadata (create_date) does.
    /// </summary>
    private const string PasswordLastSetSeed = "2020-01-01 00:00:00.000";

    /// <summary>SQL Server's "never" datetime sentinel for the lockout/bad-password times.</summary>
    private const string NeverSentinel = "1900-01-01 00:00:00.000";

    private readonly Expression loginArg;
    private readonly Expression propertyArg;

    public LoginProperty(ParserContext context)
    {
        this.loginArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("LOGINPROPERTY", 2);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var loginValue = this.loginArg.Run(runtime);
        var propertyValue = this.propertyArg.Run(runtime);
        if (loginValue.IsNull || propertyValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);

        var login = loginValue.CoerceTo(SqlType.NVarchar).AsString;
        // Only the fixed simulated login resolves; every other name behaves
        // like a nonexistent login (all properties NULL).
        if (!BuiltInToken.Comparer.Equals(login, PrincipalPlaceholders.CurrentLogin))
            return SqlValue.Null(SqlType.NVarchar);

        var property = propertyValue.CoerceTo(SqlType.NVarchar).AsString;
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "BADPASSWORDCOUNT" => SqlValue.FromNVarchar("0"),
            "BADPASSWORDTIME" => SqlValue.FromNVarchar(NeverSentinel),
            "DAYSUNTILEXPIRATION" => SqlValue.Null(SqlType.NVarchar),
            "DEFAULTDATABASE" => SqlValue.FromNVarchar(runtime.Batch.CurrentDatabase.Name),
            "DEFAULTLANGUAGE" => SqlValue.FromNVarchar("us_english"),
            "HISTORYLENGTH" => SqlValue.FromNVarchar("0"),
            "ISEXPIRED" => SqlValue.FromNVarchar("0"),
            "ISLOCKED" => SqlValue.FromNVarchar("0"),
            "ISMUSTCHANGE" => SqlValue.FromNVarchar("0"),
            "LOCKOUTTIME" => SqlValue.FromNVarchar(NeverSentinel),
            "PASSWORDHASH" => SqlValue.Null(SqlType.NVarchar),
            "PASSWORDHASHALGORITHM" => SqlValue.Null(SqlType.NVarchar),
            "PASSWORDLASTSETTIME" => SqlValue.FromNVarchar(PasswordLastSetSeed),
            _ => SqlValue.Null(SqlType.NVarchar),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"LOGINPROPERTY({this.loginArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
