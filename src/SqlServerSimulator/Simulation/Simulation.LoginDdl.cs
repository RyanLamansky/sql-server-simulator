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
    /// non-empty. The option tail is read by <see cref="ConsumeLoginOptions"/>
    /// (<c>CREDENTIAL</c> maps the login to a credential, Msg 15151 for a
    /// missing one); <c>FROM WINDOWS</c> is refused as the Linux server refuses
    /// it (<see cref="TryParseCreateWindowsLogin"/>), and the other <c>FROM</c> forms
    /// (CERTIFICATE / ASYMMETRIC KEY / EXTERNAL PROVIDER) and the hashed-
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
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            if (context.GetNextRequired() is not UnquotedString { Value: var source } || !source.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Only SQL-authentication and Windows logins (CREATE LOGIN name WITH PASSWORD = '…' / FROM WINDOWS) are modeled; certificate, asymmetric-key, and external-provider logins are not.");
            context.MoveNextOptional();
            return TryParseCreateWindowsLogin(context, name);
        }
        var password = ParsePasswordClauseHere(context, required: true, out var passwordStart, out var passwordEnd);
        var options = ConsumeLoginOptions(context, takesSid: true);
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
        // A SID is sixteen bytes no server principal's matches, checked ahead
        // of the name and the password (probed 2026-10-16 against SQL Server
        // 2025).
        if (options.Sid is { } sid)
        {
            if (sid.Length != 16)
                throw SimulatedSqlException.LoginSidWrongLength();
            if (!sid.AsSpan().ContainsAnyExcept((byte)0) || simulation.ServerPrincipalSidInUse(sid))
                throw SimulatedSqlException.LoginSidInUse();
        }
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
        int? credentialId = options.Credential is { } credential ? simulation.ResolveCredentialId(credential) : null;
        var utcNow = context.Batch.CurrentStatement.UtcNow;
        var login = new ServerLogin(simulation.AllocatePrincipalId(), name, PasswordHash.EncryptLegacy(password), utcNow, utcNow,
            defaultDatabase: options.DefaultDatabase ?? "master", defaultLanguage: options.DefaultLanguage ?? simulation.DefaultLoginLanguage.Name,
            isPolicyChecked: checkPolicy, isExpirationChecked: options.CheckExpiration ?? false, sid: options.Sid);
        if (!simulation.Logins.TryAdd(name, login))
            throw SimulatedSqlException.ServerPrincipalAlreadyExists(name);
        if (options.Sid is not null)
            simulation.RelinkUsersBySid(login);
        simulation.MapLoginCredential(login.PrincipalId, credentialId);
        // CREATE LOGIN auto-seeds a server-scope CONNECT SQL grant (class 100,
        // grantor sa) — probe6 N4b.
        lock (simulation.ServerPermissions)
            simulation.ServerPermissions.Add(new ServerPermission(login.PrincipalId, 1, "CONNECT SQL", "COSQ", PermissionState.Grant));
        RecordServerDdlEvent(context, "CREATE_LOGIN", databaseName: null, name, passwordStart, passwordEnd);
        return true;
    }

    /// <summary>
    /// <c>CREATE LOGIN name FROM WINDOWS [WITH DEFAULT_DATABASE = …, DEFAULT_LANGUAGE = …]</c>,
    /// cursor past <c>WINDOWS</c>. The Linux server makes no Windows login, so
    /// once the caller's <c>ALTER ANY LOGIN</c> and the default database and
    /// language are checked the statement is refused as
    /// <see cref="WindowsLoginCreationRefusal"/> describes.
    /// </summary>
    private static bool TryParseCreateWindowsLogin(ParserContext context, string name)
    {
        // The WITH list takes DEFAULT_DATABASE and DEFAULT_LANGUAGE alone: any
        // other option is a syntax error at its value (probed 2026-10-17
        // against SQL Server 2025).
        string? database = null, language = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            do
            {
                if (context.GetNextRequired() is not UnquotedString { Value: var option })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var isDatabase = option.Equals("DEFAULT_DATABASE", StringComparison.OrdinalIgnoreCase);
                var isLanguage = option.Equals("DEFAULT_LANGUAGE", StringComparison.OrdinalIgnoreCase);
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var value = context.GetNextRequired();
                if (value is ReservedKeyword keyword)
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
                if (value is not Name written || !(isDatabase || isLanguage))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (isDatabase)
                    database = written.Value;
                else
                    language = written.Value;
            }
            while (context.GetNextOptional() is Operator { Character: ',' });
        }
        if (context.Batch.IsSkipping)
            return true;
        RequireCreateLoginPermission(context);
        var simulation = context.Batch.Connection.Simulation;
        ValidateLoginDefaults(simulation, database, language);
        throw simulation.WindowsLoginCreationRefusal(name);
    }

    /// <summary>Whether a server principal — a login, <c>sa</c>, <c>public</c> or a server role — holds a SID matching <paramref name="sid"/>.</summary>
    private bool ServerPrincipalSidInUse(byte[] sid)
    {
        if (SidsMatch(sid, [0x01]) || SidsMatch(sid, [0x02]))
            return true;
        foreach (var (roleId, _) in FixedServerRoles)
        {
            if (SidsMatch(sid, [(byte)roleId]))
                return true;
        }
        foreach (var (_, login) in this.Logins)
        {
            if (SidsMatch(sid, login.Sid))
                return true;
        }
        foreach (var (_, role) in this.ServerRoles)
        {
            if (SidsMatch(sid, BuiltInResources.DeriveLoginSid(role.Name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Maps every orphaned database user whose SID is <paramref name="login"/>'s
    /// to it: a login made again with a dropped one's SID gets that login's
    /// users back, whatever its name (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private void RelinkUsersBySid(ServerLogin login)
    {
        foreach (var (_, database) in this.Databases)
        {
            foreach (var (_, user) in database.Principals)
            {
                if (user.TypeCode != "S" || user.LoginSid is not { } userSid || !SidsMatch(userSid, login.Sid))
                    continue;
                if (user.LoginName is { } linked && this.Logins.TryGetValue(linked, out var current) && current.PrincipalId == user.LoginPrincipalId)
                    continue;
                user.LoginName = login.Name;
                user.LoginPrincipalId = login.PrincipalId;
            }
        }
    }

    /// <summary>
    /// Whether two SIDs are the same as SQL Server compares them, as
    /// <c>varbinary</c>: the shorter padded with zero bytes, so <c>0x03</c>
    /// and sixteen bytes leading with it match (probed 2026-10-16 against SQL
    /// Server 2025: a SID of <c>0x03</c> and fifteen zeros is
    /// <c>sysadmin</c>'s, in use).
    /// </summary>
    internal static bool SidsMatch(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var common = Math.Min(first.Length, second.Length);
        return first[..common].SequenceEqual(second[..common])
            && !first[common..].ContainsAnyExcept((byte)0) && !second[common..].ContainsAnyExcept((byte)0);
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
            // rather than holding it in Logins — that registry has to hold no
            // SQL login in a simulation nobody created one in, because the TDS
            // endpoint reads that as "accept any credentials".
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
            AlterLoginCredential(simulation, principalId: 1, options);
            RecordServerDdlEvent(context, "ALTER_LOGIN", databaseName: null, name, passwordStart, passwordEnd);
            return true;
        }
        // A Windows login takes no password and no password policy (probed
        // 2026-10-17 against SQL Server 2025).
        if (existing.IsWindows)
        {
            var refused = password is not null ? "PASSWORD"
                : options.CheckPolicy is not null ? "CHECK_POLICY"
                : options.CheckExpiration is not null ? "CHECK_EXPIRATION"
                : null;
            if (refused is not null)
                throw SimulatedSqlException.OptionNotForWindowsLogin(refused);
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
        AlterLoginCredential(simulation, existing.PrincipalId, options);
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
            // Nor one owning a server role (probed 2026-10-21 against SQL
            // Server 2025).
            if (simulation.TryResolveServerPrincipalId(name, out var loginId) && simulation.OwnsServerRole(loginId))
                throw SimulatedSqlException.ServerPrincipalOwnsServerRoles();
        }
        RecordServerSecurityUndo(context.Batch);
        if (!context.Batch.Connection.Simulation.Logins.TryRemove(name, out var dropped))
            throw MissingLoginDropRefusal(name);
        simulation.MapLoginCredential(dropped.PrincipalId, credentialId: null);
        RecordServerDdlEvent(context, "DROP_LOGIN", databaseName: null, name);
        // Real's DROP LOGIN runs two internal procedures whose return statuses
        // reach the client on their own, and sends no DONE of its own (probed
        // 2026-09-28 against SQL Server 2025) — at batch level only: inside a
        // procedure, dynamic SQL or sp_droplogin it sends nothing (probed
        // 2026-10-17).
        if (context.Batch.Connection.FramesEveryStatement && context.Batch.CallerBatch is null)
            (context.Batch.PendingTriggerOutcomes ??= []).AddRange([new SimulatedReturnStatus(0), new SimulatedReturnStatus(0)]);
        return true;
    }

    /// <summary>The options of a <c>CREATE LOGIN</c> / <c>ALTER LOGIN</c> the simulator keeps, null for one not written.</summary>
    private readonly struct LoginOptions(string? defaultDatabase, string? defaultLanguage, bool? checkPolicy, bool? checkExpiration, string? oldPassword, string? credential, bool noCredential, byte[]? sid)
    {
        /// <summary>The binary literal a <c>SID =</c> gives, before its checks.</summary>
        public readonly byte[]? Sid = sid;

        /// <summary>The credential a <c>CREDENTIAL = name</c> maps the login to.</summary>
        public readonly string? Credential = credential;

        /// <summary><c>NO CREDENTIAL</c>, which clears the login's mapping.</summary>
        public readonly bool NoCredential = noCredential;

        public readonly string? OldPassword = oldPassword;
        public readonly string? DefaultDatabase = defaultDatabase;
        public readonly string? DefaultLanguage = defaultLanguage;
        public readonly bool? CheckPolicy = checkPolicy;
        public readonly bool? CheckExpiration = checkExpiration;
    }

    /// <summary>
    /// Reads the rest of a <c>CREATE LOGIN</c> / <c>ALTER LOGIN</c> statement, keeping
    /// the <c>DEFAULT_DATABASE</c> and <c>DEFAULT_LANGUAGE</c> values as written, the
    /// <c>CHECK_POLICY</c> / <c>CHECK_EXPIRATION</c> switches and the <c>CREDENTIAL</c> /
    /// <c>NO CREDENTIAL</c> mapping, and discarding every other option.
    /// </summary>
    private static LoginOptions ConsumeLoginOptions(ParserContext context, bool takesSid = false)
    {
        string? database = null, language = null, oldPassword = null, credential = null;
        byte[]? sid = null;
        bool? checkPolicy = null, checkExpiration = null;
        var noCredential = false;
        while (!IsStatementBoundary(context.Token))
        {
            if (context.Token is UnquotedString { Value: var option })
            {
                if (option.Equals("CREDENTIAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.GetNextOptional() is not Operator { Character: '=' })
                        continue;
                    if (context.GetNextOptional() is Name credentialName)
                    {
                        credential = credentialName.Value;
                        context.MoveNextOptional();
                    }
                    continue;
                }
                // SID takes a binary literal, once, and only from CREATE LOGIN:
                // anything else is a syntax error at it (probed 2026-10-16
                // against SQL Server 2025).
                if (option.Equals("SID", StringComparison.OrdinalIgnoreCase))
                {
                    if (!takesSid)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextOptional() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextOptional() is not Literal { Value: { IsNull: false } literal } || literal.Type is not VarbinarySqlType)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    // A second SID is named by its bytes read as UTF-16, a
                    // zero character shown as a period.
                    if (sid is not null)
                        throw SimulatedSqlException.SyntaxErrorNear(System.Text.Encoding.Unicode.GetString(literal.AsBytes).Replace('\0', '.'), 1);
                    sid = literal.AsBytes;
                    context.MoveNextOptional();
                    continue;
                }
                if (option.Equals("NO", StringComparison.OrdinalIgnoreCase)
                    && context.GetNextOptional() is UnquotedString { Span: var noWhat } && noWhat.Equals("CREDENTIAL", StringComparison.OrdinalIgnoreCase))
                {
                    noCredential = true;
                    context.MoveNextOptional();
                    continue;
                }
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
        return new LoginOptions(database, language, checkPolicy, checkExpiration, oldPassword, credential, noCredential, sid);
    }

    /// <summary>
    /// Applies an <c>ALTER LOGIN</c>'s <c>CREDENTIAL = name</c> or <c>NO
    /// CREDENTIAL</c> to the login <paramref name="principalId"/>; a name that
    /// is no credential is Msg 15151 (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static void AlterLoginCredential(Simulation simulation, int principalId, LoginOptions options)
    {
        if (options.Credential is { } credential)
            simulation.MapLoginCredential(principalId, simulation.ResolveCredentialId(credential));
        else if (options.NoCredential)
            simulation.MapLoginCredential(principalId, credentialId: null);
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
    /// caller's <see cref="ConsumeLoginOptions"/> reads the option tail). Returns null when <paramref name="required"/> is false and the
    /// clause is absent (ALTER LOGIN's ENABLE / DISABLE / other-option
    /// forms). Rejects the unmodeled CREATE forms: <c>FROM</c> sources and
    /// <c>PASSWORD = 0x… HASHED</c>.
    /// </summary>
    private static string? ParseLoginPasswordClause(ParserContext context, bool required, out int literalStart, out int literalEnd)
    {
        context.MoveNextRequired();
        return ParsePasswordClauseHere(context, required, out literalStart, out literalEnd);
    }

    /// <summary><see cref="ParseLoginPasswordClause"/> with the cursor already on the token after the login name.</summary>
    private static string? ParsePasswordClauseHere(ParserContext context, bool required, out int literalStart, out int literalEnd)
    {
        literalStart = literalEnd = -1;
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            throw new NotSupportedException("Only SQL-authentication and Windows logins (CREATE LOGIN name WITH PASSWORD = '…' / FROM WINDOWS) are modeled; certificate, asymmetric-key, and external-provider logins are not.");
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
