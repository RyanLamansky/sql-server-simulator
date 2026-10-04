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
        var password = ParseLoginPasswordClause(context, required: true, out var passwordStart, out var passwordEnd);
        var options = ConsumeLoginOptions(context);
        if (context.Batch.IsSkipping)
            return true;
        RecordServerSecurityUndo(context.Batch);

        // Login DDL is server-scope: a restricted session needs ALTER ANY
        // LOGIN. CREATE reports Msg 15247 (probe-confirmed); ALTER / DROP
        // report the same 15151 as a missing login, leaking nothing.
        RequireCreateLoginPermission(context);
        if (password!.Length > PasswordHash.MaxClearTextChars)
            throw SimulatedSqlException.PasswordEncryptionInvalidValue();
        var simulation = context.Batch.Connection.Simulation;
        // The name collides with any *server principal*, not just a previously
        // created login: `sa`, `public` and the fixed server roles are all
        // resolvable names that Logins doesn't hold, so checking that
        // dictionary alone would let CREATE LOGIN [sa] through and leave the
        // catalog views projecting two of it.
        // A backslash names a Windows principal, which a SQL login's name can't (probed 2026-09-30).
        if (name.Contains('\\', StringComparison.Ordinal))
            throw SimulatedSqlException.InvalidLoginNameCharacters(name);
        if (simulation.TryResolveServerPrincipalId(name, out _) || simulation.Logins.ContainsKey(name))
            throw SimulatedSqlException.ServerPrincipalAlreadyExists(name);
        ValidateLoginDefaults(simulation, options.DefaultDatabase, options.DefaultLanguage);
        var checkPolicy = options.CheckPolicy ?? true;
        if (options.CheckExpiration == true && !checkPolicy)
            throw SimulatedSqlException.CheckExpirationNeedsPolicy();
        if (checkPolicy)
            ValidateLoginPassword(name, password);
        var utcNow = context.Batch.CurrentStatement.UtcNow;
        var login = new ServerLogin(simulation.AllocatePrincipalId(), name, PasswordHash.EncryptLegacy(password), utcNow, utcNow,
            defaultDatabase: options.DefaultDatabase ?? "master", defaultLanguage: options.DefaultLanguage ?? "us_english",
            isPolicyChecked: checkPolicy, isExpirationChecked: options.CheckExpiration ?? false);
        if (!simulation.Logins.TryAdd(name, login))
            throw SimulatedSqlException.ServerPrincipalAlreadyExists(name);
        // CREATE LOGIN auto-seeds a server-scope CONNECT SQL grant (class 100,
        // grantor sa) — probe6 N4b.
        lock (simulation.ServerPermissions)
            simulation.ServerPermissions.Add(new ServerPermission(login.PrincipalId, 1, "CONNECT SQL", "COSQ", PermissionState.Grant));
        RecordServerDdlEvent(context, "CREATE_LOGIN", databaseName: null, name, passwordStart, passwordEnd);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER LOGIN name { WITH PASSWORD = '…' [option …] | ENABLE |
    /// DISABLE | WITH &lt;other options&gt; }</c>. A password change replaces
    /// the <see cref="Logins"/> entry wholesale (entries are immutable) and
    /// stamps the password-last-set time <c>LOGINPROPERTY</c> reports, and
    /// <c>ENABLE</c> / <c>DISABLE</c> replace it with the flag flipped; the
    /// other <c>WITH</c> options parse and discard after the existence check,
    /// as every form does for <c>sa</c>. A missing
    /// login raises Msg 15151 with the probe-confirmed "Cannot alter the
    /// login" wording.
    /// </summary>
    internal static bool TryParseAlterLogin(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ParseLoginName(context);
        var password = ParseLoginPasswordClause(context, required: false, out var passwordStart, out var passwordEnd);
        bool? disable = password is null && context.Token is UnquotedString { Value: var word }
            ? word.Equals("DISABLE", StringComparison.OrdinalIgnoreCase) ? true
                : word.Equals("ENABLE", StringComparison.OrdinalIgnoreCase) ? false : null
            : null;
        var options = ConsumeLoginOptions(context);
        if (context.Batch.IsSkipping)
            return true;
        RecordServerSecurityUndo(context.Batch);

        var simulation = context.Batch.Connection.Simulation;
        if (!HoldsLoginDdlPermission(context, name))
            throw SimulatedSqlException.CannotAlterOrDropLogin("alter", name);
        if (!simulation.Logins.TryGetValue(name, out var existing))
        {
            // `sa` is a fixed login, and the catalog views synthesize its row
            // rather than holding it in Logins — that registry has to stay
            // empty in a simulation nobody created a login in, because the TDS
            // endpoint reads an empty registry as "accept any credentials".
            // So it resolves by name here, the way EXECUTE AS, the GRANT
            // family, sp_addsrvrolemember and the server-role paths already
            // resolve it. Real accepts ALTER LOGIN [sa] (probe-confirmed via
            // DEFAULT_LANGUAGE), and every option but PASSWORD parses and
            // discards, so there is nothing to record.
            if (!BuiltInToken.Comparer.Equals(name, "sa"))
            {
                // A role — public or a server role — isn't a login ALTER LOGIN takes (probed 2026-09-30).
                throw simulation.TryResolveServerPrincipalId(name, out _)
                    ? SimulatedSqlException.CannotUseSpecialPrincipal(name)
                    : SimulatedSqlException.CannotAlterOrDropLogin("alter", name);
            }
            if (password is not null)
            {
                // sa is CHECK_POLICY ON, so a password real's policy refuses
                // is refused before the unmodeled recording (probed
                // 2026-09-30 against SQL Server 2025, a too-short password).
                if (password.Length > PasswordHash.MaxClearTextChars)
                    throw SimulatedSqlException.PasswordEncryptionInvalidValue();
                ValidateLoginPassword(name, password);
                throw new NotSupportedException(
                    "ALTER LOGIN [sa] WITH PASSWORD is not modeled: recording a password for sa "
                    + "would mean adding it to the login registry, which switches the TDS endpoint "
                    + "from accepting any credentials to enforcing them.");
            }
            RecordServerDdlEvent(context, "ALTER_LOGIN", databaseName: null, name, passwordStart, passwordEnd);
            return true;
        }
        // An OLD_PASSWORD that isn't the login's is refused as a login the caller can't alter.
        if (options.OldPassword is { } oldPassword && !PasswordHash.Verify(oldPassword, existing.PasswordHash))
            throw SimulatedSqlException.CannotAlterOrDropLogin("alter", name);
        ValidateLoginDefaults(simulation, options.DefaultDatabase, options.DefaultLanguage);
        var policyChecked = options.CheckPolicy ?? existing.IsPolicyChecked;
        if ((options.CheckExpiration ?? existing.IsExpirationChecked) && !policyChecked)
            throw SimulatedSqlException.CheckExpirationNeedsPolicy();
        if (password is not null && password.Length > PasswordHash.MaxClearTextChars)
            throw SimulatedSqlException.PasswordEncryptionInvalidValue();
        if (password is not null && policyChecked)
            ValidateLoginPassword(name, password);
        simulation.Logins[name] = existing.With(
            passwordHash: password is null ? null : PasswordHash.EncryptLegacy(password),
            passwordLastSetTime: password is null ? null : context.Batch.CurrentStatement.UtcNow,
            isDisabled: disable,
            defaultDatabase: options.DefaultDatabase,
            defaultLanguage: options.DefaultLanguage,
            isPolicyChecked: options.CheckPolicy,
            isExpirationChecked: options.CheckExpiration);
        RecordServerDdlEvent(context, "ALTER_LOGIN", databaseName: null, name, passwordStart, passwordEnd);
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
        if (context.Batch.IsSkipping)
            return true;
        if (!HoldsLoginDdlPermission(context, name))
            throw SimulatedSqlException.CannotAlterOrDropLogin("drop", name);
        var simulation = context.Batch.Connection.Simulation;
        // sa is a login DROP LOGIN can't take (probed 2026-09-30).
        if (BuiltInToken.Comparer.Equals(name, "sa"))
            throw SimulatedSqlException.CannotUseSpecialPrincipal(name);
        if (simulation.Logins.ContainsKey(name))
        {
            foreach (var (_, database) in simulation.Databases)
            {
                if (database.Collation.Equals(database.OwnerLoginName, name))
                    throw SimulatedSqlException.LoginOwnsDatabases(name);
            }
        }
        RecordServerSecurityUndo(context.Batch);
        if (!context.Batch.Connection.Simulation.Logins.TryRemove(name, out _))
            throw SimulatedSqlException.CannotAlterOrDropLogin("drop", name);
        RecordServerDdlEvent(context, "DROP_LOGIN", databaseName: null, name);
        // Real's DROP LOGIN runs two internal procedures whose return statuses
        // reach the client on their own, and sends no DONE of its own (probed
        // 2026-09-28 against SQL Server 2025).
        if (context.Batch.Connection.FramesEveryStatement)
            (context.Batch.PendingTriggerOutcomes ??= []).AddRange([new SimulatedReturnStatus(0), new SimulatedReturnStatus(0)]);
        return true;
    }

    /// <summary>The options of a <c>CREATE LOGIN</c> / <c>ALTER LOGIN</c> the simulator keeps, null for one not written.</summary>
    private readonly struct LoginOptions(string? defaultDatabase, string? defaultLanguage, bool? checkPolicy, bool? checkExpiration, string? oldPassword)
    {
        public readonly string? OldPassword = oldPassword;
        public readonly string? DefaultDatabase = defaultDatabase;
        public readonly string? DefaultLanguage = defaultLanguage;
        public readonly bool? CheckPolicy = checkPolicy;
        public readonly bool? CheckExpiration = checkExpiration;
    }

    /// <summary>
    /// Reads the rest of a <c>CREATE LOGIN</c> / <c>ALTER LOGIN</c> statement, keeping
    /// the <c>DEFAULT_DATABASE</c> and <c>DEFAULT_LANGUAGE</c> values as written and the
    /// <c>CHECK_POLICY</c> / <c>CHECK_EXPIRATION</c> switches, and discarding every other option.
    /// </summary>
    private static LoginOptions ConsumeLoginOptions(ParserContext context)
    {
        string? database = null, language = null, oldPassword = null;
        bool? checkPolicy = null, checkExpiration = null;
        while (!IsStatementBoundary(context.Token))
        {
            if (context.Token is UnquotedString { Value: var option })
            {
                var isDatabase = option.Equals("DEFAULT_DATABASE", StringComparison.OrdinalIgnoreCase);
                var isLanguage = option.Equals("DEFAULT_LANGUAGE", StringComparison.OrdinalIgnoreCase);
                var isPolicy = option.Equals("CHECK_POLICY", StringComparison.OrdinalIgnoreCase);
                var isExpiration = option.Equals("CHECK_EXPIRATION", StringComparison.OrdinalIgnoreCase);
                var isOldPassword = option.Equals("OLD_PASSWORD", StringComparison.OrdinalIgnoreCase);
                if ((isDatabase || isLanguage || isPolicy || isExpiration || isOldPassword) && context.GetNextOptional() is Operator { Character: '=' })
                {
                    var value = context.GetNextOptional();
                    var written = value switch
                    {
                        Literal { Value: var literal } => literal.AsString,
                        Name name => name.Value,
                        ReservedKeyword keyword => keyword.Source.ToString(),
                        _ => null,
                    };
                    if (written is not null && isDatabase)
                        database = written;
                    else if (written is not null && isLanguage)
                        language = written;
                    else if (written is not null && isOldPassword)
                        oldPassword = written;
                    else if (written is not null && isPolicy)
                        checkPolicy = written.Equals("ON", StringComparison.OrdinalIgnoreCase);
                    else if (written is not null)
                        checkExpiration = written.Equals("ON", StringComparison.OrdinalIgnoreCase);
                }
            }
            context.MoveNextOptional();
        }
        return new LoginOptions(database, language, checkPolicy, checkExpiration, oldPassword);
    }

    /// <summary>
    /// The password policy a login with <c>CHECK_POLICY</c> on meets (probed 2026-09-30
    /// against SQL Server 2025): at least 8 characters (Msg 33062), then three of the
    /// four character sets — upper, lower, digit, symbol — without the login's own name
    /// in it (Msg 33064).
    /// </summary>
    private static void ValidateLoginPassword(string loginName, string password)
    {
        if (password.Length < 8)
            throw SimulatedSqlException.PasswordTooShort();
        var sets = 0;
        sets += password.Any(char.IsUpper) ? 1 : 0;
        sets += password.Any(char.IsLower) ? 1 : 0;
        sets += password.Any(char.IsDigit) ? 1 : 0;
        sets += password.Any(static c => !char.IsLetterOrDigit(c)) ? 1 : 0;
        if (sets < 3 || password.Contains(loginName, StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.PasswordNotComplex();
    }

    /// <summary>
    /// The refusals a login's default database or language earns: Msg 15010 for a
    /// database the instance doesn't have and Msg 15033 for a language it doesn't
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    private static void ValidateLoginDefaults(Simulation simulation, string? database, string? language)
    {
        if (database is not null && !simulation.Databases.EnumerateValues().Any(candidate => BuiltInToken.Comparer.Equals(candidate.Name, database)))
            throw SimulatedSqlException.DefaultDatabaseDoesNotExist(database);
        if (language is not null && Language.Find(language) is null)
            throw SimulatedSqlException.NotAnOfficialLanguageName(language);
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
    /// restricted session needs the server-wide <c>CREATE LOGIN</c> (which
    /// <c>ALTER ANY LOGIN</c> covers), else Msg 15247 (probe-confirmed — real
    /// reports the generic permission wording, not the 15151 family the ALTER
    /// / DROP forms use; probed 2026-09-29 against SQL Server 2025 that the
    /// creator gets no ALTER on the login it made).
    /// </summary>
    private static void RequireCreateLoginPermission(ParserContext context)
    {
        var security = context.Connection.Security;
        if (security.EffectiveIsDbo)
            return;
        if (!context.Batch.Connection.Simulation.HoldsServerPermission(security.Effective.LoginName, Permission.CreateLogin))
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
    private static string? ParseLoginPasswordClause(ParserContext context, bool required, out int literalStart, out int literalEnd)
    {
        literalStart = literalEnd = -1;
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
        // A DDL event's CommandText masks the literal (probed 2026-09-28
        // against SQL Server 2025).
        literalStart = context.Token.StartIndex;
        literalEnd = context.Token.EndIndex;
        context.MoveNextOptional();
        return passwordValue.AsString;
    }
}
