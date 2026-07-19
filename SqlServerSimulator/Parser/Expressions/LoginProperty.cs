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
/// Like real SQL Server, every property projects as <c>sql_variant</c>
/// (<see cref="SqlType.SqlVariant"/>) with a per-property inner base type:
/// <c>datetime</c> for the time properties, <c>int</c> for the counters /
/// boolean flags, <c>nvarchar</c> for the name properties (probe-confirmed
/// against SQL Server 2025). <c>PasswordHash</c> is <c>varbinary</c> in real
/// but surfaces as a NULL <c>sql_variant</c> here (a low-privilege login sees
/// NULL for the hash on the live server too, and the simulator stores none).
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
    private static readonly DateTime PasswordLastSetSeed = new(2020, 1, 1);

    /// <summary>SQL Server's "never" datetime sentinel for the lockout/bad-password times.</summary>
    private static readonly DateTime NeverSentinel = new(1900, 1, 1);

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
            return SqlValue.Null(SqlType.SqlVariant);

        var login = loginValue.CoerceTo(SqlType.NVarchar).AsString;
        // The fixed simulated login resolves with seeded constants; a
        // CREATE LOGIN-registered login resolves with its actual create /
        // password-set stamps; every other name behaves like a nonexistent
        // login (all properties NULL).
        var registered = runtime.Batch.Connection.Simulation.Logins.TryGetValue(login, out var serverLogin);
        if (!registered && !BuiltInToken.Comparer.Equals(login, PrincipalPlaceholders.CurrentLogin))
            return SqlValue.Null(SqlType.SqlVariant);

        var property = propertyValue.CoerceTo(SqlType.NVarchar).AsString;
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        // Each property carries its probed inner base type; the null-valued
        // properties (DaysUntilExpiration / PasswordHash / PasswordHashAlgorithm
        // and any unknown name) surface as a NULL sql_variant.
        var inner = upper switch
        {
            "BADPASSWORDCOUNT" => SqlValue.FromInt32(0),
            "BADPASSWORDTIME" => SqlValue.FromDateTime(NeverSentinel),
            "DEFAULTDATABASE" => SqlValue.FromNVarchar(runtime.Batch.CurrentDatabase.Name),
            "DEFAULTLANGUAGE" => SqlValue.FromNVarchar("us_english"),
            "HISTORYLENGTH" => SqlValue.FromInt32(0),
            "ISEXPIRED" => SqlValue.FromInt32(0),
            "ISLOCKED" => SqlValue.FromInt32(0),
            "ISMUSTCHANGE" => SqlValue.FromInt32(0),
            "LOCKOUTTIME" => SqlValue.FromDateTime(NeverSentinel),
            "PASSWORDLASTSETTIME" => SqlValue.FromDateTime(registered ? serverLogin!.PasswordLastSetTime : PasswordLastSetSeed),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
        return inner.Type is SqlVariantSqlType ? inner : SqlValue.FromVariant(inner);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() => $"LOGINPROPERTY({this.loginArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
