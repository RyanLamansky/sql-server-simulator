using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE LOGIN name WITH PASSWORD = '…' [MUST_CHANGE]
    /// [, option …]</c>. Only the SQL-authentication clear-text-password form
    /// is modeled: the name and the PWDENCRYPT-format hash of the password
    /// land in <see cref="Logins"/>, which the TDS endpoint enforces once
    /// non-empty. The option tail (MUST_CHANGE / CHECK_POLICY /
    /// CHECK_EXPIRATION / DEFAULT_DATABASE / DEFAULT_LANGUAGE / SID /
    /// CREDENTIAL) parses-and-discards; the <c>FROM</c> forms (WINDOWS /
    /// CERTIFICATE / ASYMMETRIC KEY / EXTERNAL PROVIDER) and the hashed-
    /// password form (<c>PASSWORD = 0x… HASHED</c>) raise
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    /// <remarks>
    /// A pre-existing login name raises Msg 15025. Wording is docs-derived,
    /// not probe-confirmed — the reference instance's login lacks the server
    /// permission to reach the duplicate check (it reports Msg 15247 first).
    /// </remarks>
    internal static bool TryParseCreateLogin(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ParseLoginName(context);
        var password = ParseLoginPasswordClause(context, required: true);
        ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;

        // Login DDL is server-scope: a restricted session needs ALTER ANY
        // LOGIN. CREATE reports Msg 15247 (probe-confirmed); ALTER / DROP
        // report the same 15151 as a missing login, leaking nothing.
        RequireCreateLoginPermission(context);
        if (password!.Length > PasswordHash.MaxClearTextChars)
            throw SimulatedSqlException.PasswordEncryptionInvalidValue();
        var simulation = context.Batch.Connection.Simulation;
        var utcNow = context.Batch.CurrentStatement.UtcNow;
        var login = new ServerLogin(simulation.AllocatePrincipalId(), name, PasswordHash.EncryptLegacy(password), utcNow, utcNow);
        if (!simulation.Logins.TryAdd(name, login))
            throw SimulatedSqlException.ServerPrincipalAlreadyExists(name);
        // CREATE LOGIN auto-seeds a server-scope CONNECT SQL grant (class 100,
        // grantor sa) — probe6 N4b.
        lock (simulation.ServerPermissions)
            simulation.ServerPermissions.Add(new ServerPermission(login.PrincipalId, 1, "CONNECT SQL", "COSQ", PermissionState.Grant));
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER LOGIN name { WITH PASSWORD = '…' [option …] | ENABLE |
    /// DISABLE | WITH &lt;other options&gt; }</c>. A password change replaces
    /// the <see cref="Logins"/> entry wholesale (entries are immutable) and
    /// stamps the password-last-set time <c>LOGINPROPERTY</c> reports; every
    /// other form parses-and-discards after the existence check. A missing
    /// login raises Msg 15151 with the probe-confirmed "Cannot alter the
    /// login" wording.
    /// </summary>
    internal static bool TryParseAlterLogin(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ParseLoginName(context);
        var password = ParseLoginPasswordClause(context, required: false);
        ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;

        var simulation = context.Batch.Connection.Simulation;
        if (!simulation.Logins.TryGetValue(name, out var existing)
            || !HoldsLoginDdlPermission(context, name))
        {
            throw SimulatedSqlException.CannotAlterOrDropLogin("alter", name);
        }
        if (password is not null)
        {
            if (password.Length > PasswordHash.MaxClearTextChars)
                throw SimulatedSqlException.PasswordEncryptionInvalidValue();
            simulation.Logins[name] = new ServerLogin(
                existing.PrincipalId, existing.Name, PasswordHash.EncryptLegacy(password), existing.CreateDate,
                context.Batch.CurrentStatement.UtcNow);
        }
        return true;
    }

    /// <summary>
    /// Parses <c>DROP LOGIN name</c>. Real SQL Server's DROP LOGIN grammar
    /// has no <c>IF EXISTS</c> clause (probe-confirmed: <c>DROP LOGIN IF
    /// EXISTS x</c> is Msg 156 near 'IF'), which falls out naturally here —
    /// <c>IF</c> isn't a <see cref="Name"/>, so it routes to the generic
    /// syntax error. A missing login raises Msg 15151 with the
    /// probe-confirmed "Cannot drop the login" wording.
    /// </summary>
    internal static bool TryParseDropLogin(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ParseLoginName(context);
        context.MoveNextOptional();
        return context.Batch.IsSkipping
            || (HoldsLoginDdlPermission(context, name)
                && context.Batch.Connection.Simulation.Logins.TryRemove(name, out _)
                ? true
                : throw SimulatedSqlException.CannotAlterOrDropLogin("drop", name));
    }

    /// <summary>
    /// Whether the session may run login DDL against <paramref name="name"/>:
    /// dbo / sysadmin always, else the server-wide <c>ALTER ANY LOGIN</c> or an
    /// <c>ALTER ON LOGIN::&lt;name&gt;</c> grant on that specific login.
    /// </summary>
    private static bool HoldsLoginDdlPermission(ParserContext context, string name)
    {
        var security = context.Connection.Security;
        if (security.EffectiveIsDbo)
            return true;
        var simulation = context.Batch.Connection.Simulation;
        return simulation.TryResolveServerPrincipalId(name, out var targetId)
            && simulation.HoldsServerPrincipalPermission(
                security.Effective.LoginName, targetId, Permission.Alter, Permission.AlterAnyLogin);
    }

    /// <summary>
    /// The <c>CREATE LOGIN</c> gate, which has no per-login target: a
    /// restricted session needs the server-wide <c>ALTER ANY LOGIN</c>, else
    /// Msg 15247 (probe-confirmed — real reports the generic
    /// permission wording, not the 15151 family the ALTER / DROP forms use).
    /// </summary>
    private static void RequireCreateLoginPermission(ParserContext context)
    {
        var security = context.Connection.Security;
        if (security.EffectiveIsDbo)
            return;
        if (!context.Batch.Connection.Simulation.HoldsServerPermission(security.Effective.LoginName, Permission.AlterAnyLogin))
            throw SimulatedSqlException.UserDoesNotHavePermission();
    }

    /// <summary>
    /// Requires the current token to be a login name and returns it without
    /// advancing. A reserved keyword here gets real SQL Server's Msg 156
    /// keyword-flavored rejection — probe-confirmed via <c>DROP LOGIN IF
    /// EXISTS x</c>, which real's IF-EXISTS-less DROP LOGIN grammar rejects
    /// with Msg 156 near 'IF'.
    /// </summary>
    private static string ParseLoginName(ParserContext context) => context.Token switch
    {
        Name nameToken => nameToken.Value,
        ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// With the cursor on the token after the login name, extracts the
    /// clear-text password from a <c>WITH PASSWORD = '…'</c> clause and
    /// leaves the cursor on the token after the password literal (the
    /// caller's <see cref="ConsumeToStatementBoundary"/> discards the option
    /// tail). Returns null when <paramref name="required"/> is false and the
    /// clause is absent (ALTER LOGIN's ENABLE / DISABLE / other-option
    /// forms). Rejects the unmodeled CREATE forms: <c>FROM</c> sources and
    /// <c>PASSWORD = 0x… HASHED</c>.
    /// </summary>
    private static string? ParseLoginPasswordClause(ParserContext context, bool required)
    {
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            throw new NotSupportedException("Only SQL-authentication logins (CREATE LOGIN name WITH PASSWORD = '…') are modeled; Windows, certificate, asymmetric-key, and external-provider logins are not.");
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
        {
            return required ? throw SimulatedSqlException.SyntaxErrorNear(context) : null;
        }

        context.MoveNextRequired();
        if (context.Token is not UnquotedString passwordWord
            || !passwordWord.Span.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
        {
            return required ? throw SimulatedSqlException.SyntaxErrorNear(context) : null;
        }
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Literal { Value: var passwordValue } || !SqlType.IsStringCategory(passwordValue.Type))
            throw new NotSupportedException("Only the clear-text password form (PASSWORD = '…') is modeled; the hashed-password form (PASSWORD = 0x… HASHED) is not.");
        context.MoveNextOptional();
        return passwordValue.AsString;
    }
}
