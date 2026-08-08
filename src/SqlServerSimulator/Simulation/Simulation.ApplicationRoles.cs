using System.Security.Cryptography;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Application roles: a password-protected database principal a session
// activates with sp_setapprole, swapping its database identity wholesale for
// the role's. The login is untouched (SYSTEM_USER / ORIGINAL_LOGIN() keep
// reporting it), the session is pinned to the activating database until
// sp_unsetapprole, and the pre-activation user's own grants stop applying —
// only the role's own grants plus public. All probe-confirmed against SQL
// Server 2025.
partial class Simulation
{
    /// <summary><c>sys.database_principals.type</c> code for an application role.</summary>
    internal const string ApplicationRoleTypeCode = "A";

    /// <summary>
    /// Parses <c>CREATE APPLICATION ROLE name WITH PASSWORD = '…' [,
    /// DEFAULT_SCHEMA = schema]</c>. Cursor on entry: the <c>APPLICATION</c>
    /// word (the token after <c>CREATE</c>). The role lands in
    /// <see cref="Database.Principals"/> as type <c>A</c> /
    /// <c>APPLICATION_ROLE</c>, defaulting its schema to <c>dbo</c>.
    /// A duplicate name raises Msg 15023 like any other principal.
    /// </summary>
    internal static bool TryParseCreateApplicationRole(ParserContext context)
    {
        var name = ParseApplicationRoleHeader(context);
        var (password, defaultSchema) = ParseApplicationRoleOptions(context, requirePassword: true);
        if (context.Batch.IsSkipping)
            return true;
        context.CurrentDatabase.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, context.CurrentDatabase))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var database = context.CurrentDatabase;
        if (database.Principals.ContainsKey(name))
            throw SimulatedSqlException.PrincipalAlreadyExists(name);
        if (password!.Length > PasswordHash.MaxClearTextChars)
            throw SimulatedSqlException.PasswordEncryptionInvalidValue();

        database.Principals[name] = new DatabasePrincipal(
            database.AllocatePrincipalId(), name, ApplicationRoleTypeCode, "APPLICATION_ROLE",
            isFixedRole: false, context.Batch.CurrentStatement.UtcNow)
        {
            DefaultSchemaName = defaultSchema ?? Database.DefaultSchemaName,
            PasswordHash = PasswordHash.EncryptLegacy(password),
        };
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER APPLICATION ROLE name WITH { NAME = new | PASSWORD = '…'
    /// | DEFAULT_SCHEMA = schema } [, …]</c>. Cursor on entry: the
    /// <c>APPLICATION</c> word. A rename re-keys
    /// <see cref="Database.Principals"/>; the principal_id is preserved, so
    /// grants and role memberships follow the role.
    /// </summary>
    internal static bool TryParseAlterApplicationRole(ParserContext context)
    {
        var name = ParseApplicationRoleHeader(context);
        var (password, defaultSchema, newName) = ParseAlterApplicationRoleOptions(context);
        if (context.Batch.IsSkipping)
            return true;
        context.CurrentDatabase.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, context.CurrentDatabase))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var database = context.CurrentDatabase;
        if (!TryGetApplicationRole(database, name, out var role))
            throw SimulatedSqlException.CannotFindPrincipal(name);
        if (password is not null)
        {
            if (password.Length > PasswordHash.MaxClearTextChars)
                throw SimulatedSqlException.PasswordEncryptionInvalidValue();
            role.PasswordHash = PasswordHash.EncryptLegacy(password);
        }
        if (defaultSchema is not null)
            role.DefaultSchemaName = defaultSchema;
        if (newName is not null && !BuiltInToken.Comparer.Equals(newName, name))
        {
            if (database.Principals.ContainsKey(newName))
                throw SimulatedSqlException.PrincipalAlreadyExists(newName);
            var renamed = new DatabasePrincipal(
                role.PrincipalId, newName, ApplicationRoleTypeCode, "APPLICATION_ROLE",
                isFixedRole: false, role.CreateDate)
            {
                DefaultSchemaName = role.DefaultSchemaName,
                PasswordHash = role.PasswordHash,
            };
            _ = database.Principals.TryRemove(name, out _);
            database.Principals[newName] = renamed;
        }
        return true;
    }

    /// <summary>
    /// Parses <c>DROP APPLICATION ROLE name</c>. Cursor on entry: the
    /// <c>APPLICATION</c> word. Cascades the role's
    /// <see cref="Database.RoleMembers"/> entries the way
    /// <c>DROP ROLE</c> does.
    /// </summary>
    internal static bool TryParseDropApplicationRole(ParserContext context)
    {
        var name = ParseApplicationRoleHeader(context);
        if (context.Batch.IsSkipping)
            return true;
        context.CurrentDatabase.RejectWriteWhenReadOnly();

        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, context.CurrentDatabase))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var database = context.CurrentDatabase;
        if (!TryGetApplicationRole(database, name, out var role))
            throw SimulatedSqlException.CannotFindPrincipal(name);
        _ = database.Principals.TryRemove(name, out _);
        lock (database.RoleMembers)
            _ = database.RoleMembers.RemoveAll(m => m.RoleId == role.PrincipalId || m.MemberId == role.PrincipalId);
        return true;
    }

    /// <summary>Consumes <c>APPLICATION ROLE &lt;name&gt;</c> and returns the name, leaving the cursor on the token after it.</summary>
    private static string ParseApplicationRoleHeader(ParserContext context)
    {
        context.MoveNextRequired(); // consume APPLICATION
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Role })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return nameToken.Value;
    }

    /// <summary>
    /// Parses the <c>WITH PASSWORD = '…' [, DEFAULT_SCHEMA = s]</c> tail of
    /// <c>CREATE APPLICATION ROLE</c>. A missing PASSWORD when
    /// <paramref name="requirePassword"/> is a syntax error.
    /// </summary>
    private static (string? Password, string? DefaultSchema) ParseApplicationRoleOptions(ParserContext context, bool requirePassword)
    {
        var (password, defaultSchema, _) = ParseApplicationRoleOptionList(context, acceptName: false);
        return requirePassword && password is null
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : (password, defaultSchema);
    }

    /// <summary>Longest recognized <c>WITH</c>-option word (<c>DEFAULT_SCHEMA</c>) — the shared uppercase buffer's size.</summary>
    private const int LongestApplicationRoleOptionWord = 14;

    /// <summary>Parses <c>ALTER APPLICATION ROLE</c>'s option tail, which additionally accepts <c>NAME = new</c>.</summary>
    private static (string? Password, string? DefaultSchema, string? NewName) ParseAlterApplicationRoleOptions(ParserContext context) =>
        ParseApplicationRoleOptionList(context, acceptName: true);

    /// <summary>
    /// Parses the shared <c>WITH &lt;option&gt; [, &lt;option&gt;]</c> tail:
    /// <c>PASSWORD</c>, <c>DEFAULT_SCHEMA</c>, and (ALTER only) <c>NAME</c>.
    /// An unrecognized option is a syntax error rather than a silent discard,
    /// since each of the three carries meaning.
    /// </summary>
    private static (string? Password, string? DefaultSchema, string? NewName) ParseApplicationRoleOptionList(ParserContext context, bool acceptName)
    {
        string? password = null;
        string? defaultSchema = null;
        string? newName = null;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return (password, defaultSchema, newName);
        context.MoveNextRequired();
        // One buffer for the whole loop (CA2014): the three recognized option
        // words all fit, and an over-long word can't match any of them.
        Span<char> upperBuffer = stackalloc char[LongestApplicationRoleOptionWord];
        while (true)
        {
            var optionWord = context.Token switch
            {
                Name named => named.Value,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (optionWord.Length > upperBuffer.Length)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var upper = upperBuffer[..optionWord.Length];
            _ = optionWord.AsSpan().ToUpperInvariant(upper);
            switch (upper)
            {
                case "DEFAULT_SCHEMA":
                    defaultSchema = context.Token is Name schemaName
                        ? schemaName.Value
                        : throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case "NAME" when acceptName:
                    newName = context.Token is Name renamed
                        ? renamed.Value
                        : throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case "PASSWORD":
                    password = context.Token is Literal { Value: var passwordValue } && SqlType.IsStringCategory(passwordValue.Type)
                        ? passwordValue.AsString
                        : throw new NotSupportedException("Only the clear-text password form (PASSWORD = '…') is modeled for application roles.");
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                return (password, defaultSchema, newName);
            context.MoveNextRequired();
        }
    }

    /// <summary>Resolves a name to an application-role principal — a non-approle principal of that name reads as absent.</summary>
    private static bool TryGetApplicationRole(Database database, string name, out DatabasePrincipal role)
    {
        if (database.Principals.TryGetValue(name, out var found) && found.TypeCode == ApplicationRoleTypeCode)
        {
            role = found;
            return true;
        }
        role = null!;
        return false;
    }

    /// <summary>
    /// Handles <c>EXEC sp_setapprole @rolename, @password [, @fCreateCookie]
    /// [, @cookie OUTPUT]</c>. On success the session's database principal
    /// becomes the role (<c>USER_NAME()</c> / <c>CURRENT_USER</c> /
    /// <c>USER_ID()</c> follow it, the login does not) and the session is
    /// pinned to the current database until <c>sp_unsetapprole</c>.
    /// A wrong password or unknown role is Msg 15161; calling it twice is
    /// Msg 2762.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSetAppRole(BatchContext batch)
    {
        var args = ParseApplicationRoleProcArguments(batch, isUnset: false);
        if (batch.IsSkipping)
            yield break;

        var security = batch.Connection.Security;
        if (security.HasApplicationRole)
            throw SimulatedSqlException.SetApplicationRoleNotInvokedCorrectly();

        var roleName = TextArgument(args.HasRoleName, args.RoleName);
        var password = TextArgument(args.HasPassword, args.Password);
        if (!TryGetApplicationRole(batch.CurrentDatabase, roleName, out var role)
            || role.PasswordHash is not { } hash
            || !PasswordHash.Verify(password, hash))
        {
            throw SimulatedSqlException.CannotSetApplicationRole(roleName);
        }

        // The cookie is real's 50-byte opaque token. It is the only way back
        // out, so a caller that asks for none is pinned for the session.
        var wantsCookie = args.CookieSlot is not null
            || (args.HasCreateCookie && !args.CreateCookie.IsNull && args.CreateCookie.CoerceTo(SqlType.Bit).AsBoolean);
        var cookie = wantsCookie ? RandomNumberGenerator.GetBytes(ApplicationRoleCookieLength) : null;
        security.SetApplicationRole(role.Name, role.PrincipalId, security.Effective.LoginName, cookie);
        if (args.CookieSlot is { } slot)
            slot.Value = SqlValue.FromVarbinary(cookie!).CoerceTo(slot.DeclaredType);
    }

    /// <summary>
    /// Handles <c>EXEC sp_unsetapprole @cookie</c>. Restores the pre-activation
    /// database principal; no role set, or a cookie that doesn't match the one
    /// issued, raises Msg 15592.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpUnsetAppRole(BatchContext batch)
    {
        var args = ParseApplicationRoleProcArguments(batch, isUnset: true);
        if (batch.IsSkipping)
            yield break;

        var cookie = args.HasCookie && !args.Cookie.IsNull
            ? args.Cookie.CoerceTo(SqlType.Varbinary).AsBytes.ToArray()
            : null;
        if (!batch.Connection.Security.TryUnsetApplicationRole(cookie))
            throw SimulatedSqlException.CannotUnsetApplicationRole();
    }

    /// <summary>Length of the opaque <c>sp_setapprole</c> cookie, matching real's 50-byte <c>varbinary</c>.</summary>
    private const int ApplicationRoleCookieLength = 50;

    /// <summary>An omitted argument's text form — the empty string, which never matches a role name or password.</summary>
    private static string TextArgument(bool present, SqlValue value) =>
        present && !value.IsNull ? value.CoerceTo(SqlType.NVarchar).AsString : string.Empty;

    // Parsed sp_setapprole / sp_unsetapprole arguments. Presence flags are
    // distinct from NULL-ness: an omitted arg has no SqlValue at all, so
    // reading .IsNull off the default would fault.
    private struct ApplicationRoleProcArguments
    {
        public SqlValue RoleName;
        public bool HasRoleName;
        public SqlValue Password;
        public bool HasPassword;
        public SqlValue CreateCookie;
        public bool HasCreateCookie;

        /// <summary>The <c>@cookie OUTPUT</c> slot sp_setapprole writes the new cookie into.</summary>
        public VariableSlot? CookieSlot;

        /// <summary>The cookie value sp_unsetapprole was handed.</summary>
        public SqlValue Cookie;
        public bool HasCookie;
    }

    /// <summary>
    /// Binds positional / named EXEC arguments for <c>sp_setapprole</c>, whose
    /// positional order is (@rolename, @password, @fCreateCookie, @cookie).
    /// A <c>@cookie</c> arg with an OUTPUT slot is the write-back target;
    /// without one it is an input, which only <c>sp_unsetapprole</c> reads.
    /// </summary>
    private static ApplicationRoleProcArguments ParseApplicationRoleProcArguments(BatchContext batch, bool isUnset)
    {
        var procName = isUnset ? "sp_unsetapprole" : "sp_setapprole";
        var arguments = ParseExecArguments(batch.Parser, batch);
        var result = default(ApplicationRoleProcArguments);
        var positional = 0;
        foreach (var arg in arguments)
        {
            var parameterName = arg.Name ?? PositionalName(positional++);
            switch (parameterName)
            {
                case var n when BuiltInToken.Equals(n, "cookie"):
                    if (arg.OutputSlot is { } outputSlot)
                        result.CookieSlot = outputSlot;
                    else
                        (result.Cookie, result.HasCookie) = (arg.Value, true);
                    break;
                case var n when !isUnset && BuiltInToken.Equals(n, "fCreateCookie"):
                    (result.CreateCookie, result.HasCreateCookie) = (arg.Value, true);
                    break;
                case var n when !isUnset && BuiltInToken.Equals(n, "password"):
                    (result.Password, result.HasPassword) = (arg.Value, true);
                    break;
                case var n when !isUnset && BuiltInToken.Equals(n, "rolename"):
                    (result.RoleName, result.HasRoleName) = (arg.Value, true);
                    break;
                default:
                    throw SimulatedSqlException.InvalidProcedureParameters(procName);
            }
        }
        return result;

        string PositionalName(int index) => (isUnset, index) switch
        {
            (true, 0) => "cookie",
            (false, 0) => "rolename",
            (false, 1) => "password",
            (false, 2) => "fCreateCookie",
            (false, 3) => "cookie",
            _ => throw SimulatedSqlException.InvalidProcedureParameters(procName),
        };
    }
}
