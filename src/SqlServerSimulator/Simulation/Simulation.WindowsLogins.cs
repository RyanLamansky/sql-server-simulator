using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Windows principals SQL Server on Linux is installed with, the well-known
// accounts its PAL resolves without a domain, and the procedures over them:
// xp_logininfo, sp_grantlogin, sp_denylogin and sp_revokelogin.
partial class Simulation
{
    /// <summary>The <c>principal_id</c> of the installed <c>BUILTIN\Administrators</c> group login.</summary>
    internal const int AdministratorsPrincipalId = 259;

    /// <summary>The <c>principal_id</c> of the installed <c>NT AUTHORITY\NETWORK SERVICE</c> login.</summary>
    internal const int NetworkServicePrincipalId = 260;

    /// <summary>The <c>principal_id</c> of the installed <c>NT AUTHORITY\SYSTEM</c> login.</summary>
    internal const int SystemPrincipalId = 261;

    private static readonly byte[] AdministratorsSid = Convert.FromHexString("01020000000000052000000020020000");
    private static readonly byte[] NetworkServiceSid = Convert.FromHexString("010100000000000514000000");
    private static readonly byte[] LocalSystemSid = Convert.FromHexString("010100000000000512000000");
    private static readonly byte[] EveryoneSid = Convert.FromHexString("010100000000000100000000");
    private static readonly byte[] AuthenticatedUsersSid = Convert.FromHexString("01010000000000050B000000");
    private static readonly byte[] ServiceSid = Convert.FromHexString("010100000000000506000000");
    private static readonly byte[] ThisOrganizationSid = Convert.FromHexString("01010000000000050F000000");

    /// <summary>
    /// The Windows logins real's Linux install carries — <c>BUILTIN\Administrators</c>
    /// (a group, in <c>sysadmin</c>), <c>NT AUTHORITY\NETWORK SERVICE</c> (in
    /// <c>sysadmin</c>) and <c>NT AUTHORITY\SYSTEM</c> — with their principal ids
    /// and SIDs, default database <c>master</c> and language <c>us_english</c>
    /// (probed 2026-10-17 against SQL Server 2025). They sit in
    /// <see cref="Logins"/> from construction; no password verifies one.
    /// </summary>
    internal static readonly (int PrincipalId, string Name, string TypeCode, byte[] Sid)[] InstalledWindowsLogins =
    [
        (AdministratorsPrincipalId, @"BUILTIN\Administrators", "G", AdministratorsSid),
        (NetworkServicePrincipalId, @"NT AUTHORITY\NETWORK SERVICE", "U", NetworkServiceSid),
        (SystemPrincipalId, @"NT AUTHORITY\SYSTEM", "U", LocalSystemSid),
    ];

    /// <summary>
    /// The Windows accounts the Linux server knows without a domain: each one's
    /// name, SID, whether it is a group, and whether a name lookup finds it —
    /// the <c>NT AUTHORITY</c> accounts do (<c>SUSER_SID</c>, <c>xp_logininfo</c>,
    /// <c>CREATE LOGIN … FROM WINDOWS</c>), while <c>\Everyone</c> and the
    /// <c>BUILTIN</c> groups are found only by SID (<c>SUSER_SNAME</c>), so
    /// <c>BUILTIN\Administrators</c> is reachable by name only through its login
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static readonly (string Name, byte[] Sid, bool IsGroup, bool ResolvesByName)[] WellKnownWindowsAccounts =
    [
        (@"NT AUTHORITY\SYSTEM", LocalSystemSid, false, true),
        (@"NT AUTHORITY\LOCAL SERVICE", Convert.FromHexString("010100000000000513000000"), false, true),
        (@"NT AUTHORITY\NETWORK SERVICE", NetworkServiceSid, false, true),
        (@"NT AUTHORITY\Authenticated Users", AuthenticatedUsersSid, true, true),
        (@"NT AUTHORITY\NETWORK", Convert.FromHexString("010100000000000502000000"), true, true),
        (@"NT AUTHORITY\SERVICE", ServiceSid, true, true),
        (@"NT AUTHORITY\This Organization", ThisOrganizationSid, true, true),
        (@"\Everyone", EveryoneSid, true, false),
        (@"BUILTIN\Administrators", AdministratorsSid, true, false),
        (@"BUILTIN\Users", Convert.FromHexString("01020000000000052000000021020000"), true, false),
        (@"BUILTIN\Guests", Convert.FromHexString("01020000000000052000000022020000"), true, false),
    ];

    /// <summary>
    /// The groups in a Windows login's token, in the order <c>sys.login_token</c>
    /// lists them — <c>NT AUTHORITY\SYSTEM</c>'s names <c>BUILTIN\Administrators</c>
    /// twice, as real's does (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    internal static byte[][] WindowsTokenGroups(int principalId) => principalId switch
    {
        NetworkServicePrincipalId => [EveryoneSid, AuthenticatedUsersSid, ServiceSid, ThisOrganizationSid],
        SystemPrincipalId => [AdministratorsSid, EveryoneSid, AuthenticatedUsersSid, AdministratorsSid],
        _ => [],
    };

    /// <summary>Adds the installed Windows logins to <see cref="Logins"/>; run once, from the constructor.</summary>
    private void SeedWindowsLogins()
    {
        foreach (var (principalId, name, typeCode, sid) in InstalledWindowsLogins)
            this.Logins[name] = new ServerLogin(principalId, name, [], this.SeedDate, this.SeedDate, sid: sid, typeCode: typeCode);
    }

    /// <summary>
    /// Whether no SQL-authentication login exists — the zero-configuration
    /// mode in which both front doors accept any credentials and every session
    /// is <c>dbo</c>. The installed Windows logins don't count: they are there
    /// from the start, and nothing authenticates as one.
    /// </summary>
    internal bool HasNoSqlLogins()
    {
        foreach (var (_, login) in this.Logins)
        {
            if (!login.IsWindows)
                return false;
        }
        return true;
    }

    /// <summary>The name of the well-known Windows account with <paramref name="sid"/>, which <c>SUSER_SNAME</c> answers though no login maps it.</summary>
    internal static string? WellKnownWindowsAccountName(byte[] sid)
    {
        foreach (var (name, accountSid, _, _) in WellKnownWindowsAccounts)
        {
            if (sid.AsSpan().SequenceEqual(accountSid))
                return name;
        }
        return null;
    }

    /// <summary>The well-known Windows account a name lookup finds for <paramref name="name"/> (case and trailing spaces ignored), if any.</summary>
    private static bool TryResolveWindowsAccount(string name, out byte[] sid, out bool isGroup)
    {
        var trimmed = name.TrimEnd(' ');
        foreach (var (accountName, accountSid, accountIsGroup, resolvesByName) in WellKnownWindowsAccounts)
        {
            if (resolvesByName && BuiltInToken.Equals(accountName, trimmed))
            {
                sid = accountSid;
                isGroup = accountIsGroup;
                return true;
            }
        }
        sid = [];
        isGroup = false;
        return false;
    }

    /// <summary>The SID <c>SUSER_SID</c> answers for a name no login holds: a well-known account's, else null.</summary>
    internal static byte[]? WindowsAccountSid(string name) => TryResolveWindowsAccount(name, out var sid, out _) ? sid : null;

    /// <summary>The login holding <paramref name="sid"/>, if any.</summary>
    internal ServerLogin? LoginWithSid(byte[] sid)
    {
        foreach (var (_, login) in this.Logins)
        {
            if (login.Sid.AsSpan().SequenceEqual(sid))
                return login;
        }
        return null;
    }

    /// <summary>
    /// Adds to <paramref name="closure"/> the group logins a Windows login in it
    /// belongs to through its token — <c>BUILTIN\Administrators</c> for
    /// <c>NT AUTHORITY\SYSTEM</c>, which is what makes that login a
    /// <c>sysadmin</c> (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private void AddWindowsGroupLogins(HashSet<int> closure)
    {
        if (!closure.Contains(SystemPrincipalId) && !closure.Contains(NetworkServicePrincipalId))
            return;
        foreach (var memberId in (int[])[SystemPrincipalId, NetworkServicePrincipalId])
        {
            if (!closure.Contains(memberId))
                continue;
            foreach (var groupSid in WindowsTokenGroups(memberId))
            {
                if (this.LoginWithSid(groupSid) is { } group)
                    _ = closure.Add(group.PrincipalId);
            }
        }
    }

    /// <summary>Whether <paramref name="loginName"/> names a Windows login (type <c>U</c>) rather than a SQL login or a group.</summary>
    internal bool IsWindowsUserLogin(string loginName) =>
        this.Logins.TryGetValue(loginName, out var login) && login.TypeCode == "U";

    /// <summary>
    /// <c>IS_MEMBER</c> of a Windows group name (one holding a backslash): NULL
    /// unless the session runs as a Windows login and the name resolves — to a
    /// login or a well-known account — and otherwise whether the group is in
    /// the login's token. A SQL login's session answers NULL for every Windows
    /// name (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    internal int? WindowsGroupMembership(string loginName, string groupName)
    {
        if (!this.Logins.TryGetValue(loginName, out var login) || login.TypeCode != "U")
            return null;
        byte[] groupSid;
        if (this.Logins.TryGetValue(groupName.TrimEnd(' '), out var groupLogin))
            groupSid = groupLogin.Sid;
        else if (!TryResolveWindowsAccount(groupName, out groupSid, out _))
            return null;
        foreach (var sid in WindowsTokenGroups(login.PrincipalId))
        {
            if (sid.AsSpan().SequenceEqual(groupSid))
                return 1;
        }
        return 0;
    }

    /// <summary>
    /// The refusal <c>CREATE LOGIN … FROM WINDOWS</c> meets, ahead of anything it
    /// would create: no Windows login can be made on the Linux server, so a name
    /// without a <c>domain\</c> part is Msg 15407, one a login holds Msg 15025
    /// (state 2), a well-known account no login holds Msg 15151 state 4, and
    /// anything else — <c>BUILTIN\Administrators</c> included, which the lookup
    /// can't find — Msg 15401 (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    internal SimulatedSqlException WindowsLoginCreationRefusal(string name)
    {
        if (!name.Contains('\\', StringComparison.Ordinal))
            return SimulatedSqlException.InvalidWindowsName(name, level: 16);
        if (TryResolveWindowsAccount(name, out _, out _))
        {
            return this.Logins.ContainsKey(name.TrimEnd(' '))
                ? SimulatedSqlException.ServerPrincipalAlreadyExists(name, state: 2)
                : SimulatedSqlException.CannotCreateLogin(name);
        }
        return SimulatedSqlException.WindowsPrincipalNotFound(name, level: 16);
    }

    /// <summary>
    /// <c>DROP LOGIN</c> of a name no login holds: one that names a Windows
    /// principal the lookup can't find is Msg 15401, everything else Msg 15151
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MissingLoginDropRefusal(string name) =>
        name.Contains('\\', StringComparison.Ordinal) && !TryResolveWindowsAccount(name, out _, out _)
            ? SimulatedSqlException.WindowsPrincipalNotFound(name, level: 16)
            : SimulatedSqlException.CannotAlterOrDropLogin("drop", name);

    private static readonly SystemProcedureParameter[] XpLoginInfoParameters =
    [
        new("acctname", SqlType.SystemName, 128, SqlValue.Null(SqlType.SystemName)),
        new("option", SqlType.Varchar, 10, SqlValue.Null(SqlType.Varchar)),
        new("privilege", SqlType.Varchar, 10, SqlValue.Null(SqlType.Varchar)),
    ];

    private static readonly VarcharSqlType XpLoginInfoCodeType = VarcharSqlType.Get(8, Collation.Baseline, Coercibility.Implicit);

    private static readonly string[] XpLoginInfoColumnNames = ["account name", "type", "privilege", "mapped login name", "permission path"];

    /// <summary>
    /// <c>xp_logininfo [@acctname [, @option [, @privilege OUTPUT]]]</c>: what the
    /// server knows of a Windows account, as SQL Server 2025 on Linux answers it
    /// (probed 2026-10-17):
    /// <list type="bullet">
    /// <item>With no account, every Windows login in name order — its type
    /// (<c>user</c> / <c>group</c>), <c>admin</c> for a <c>sysadmin</c> member and
    /// <c>user</c> otherwise, its own name mapped and no permission path; an
    /// option or <c>@privilege</c> with it is Msg 15600.</item>
    /// <item>With one, a row per login granting it access — each group login whose
    /// group is in its token, path the group, then its own — the first alone
    /// unless <c>@option</c> is <c>all</c>; the account and mapped names echo the
    /// argument. A well-known account no login grants has no rows; one the lookup
    /// can't find is Msg 15404 state 11 (<c>0x54f</c>), and a name with no
    /// <c>domain\</c> part state 10 (<c>0xffff0002</c>).</item>
    /// <item><c>members</c> lists a group's members, none on this server; a user
    /// login is Msg 15404 state 5 (<c>0x8ac</c>) and a group login the lookup can't
    /// find state 3.</item>
    /// <item>Any <c>@privilege</c> argument suppresses the rows and takes the first
    /// row's privilege (NULL with none), and needs <c>@option = 'all'</c>.</item>
    /// </list>
    /// Msg 15404 ends the batch and rolls the transaction back, as under
    /// <c>XACT_ABORT</c>; an option other than <c>all</c> / <c>members</c> is Msg 15600
    /// with return code 1. A caller outside <c>sysadmin</c> is refused
    /// <c>EXECUTE</c> (Msg 229).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpLoginInfo(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("xp_logininfo", calledAs, arguments, XpLoginInfoParameters);
        if (!batch.Connection.Security.EffectiveIsDbo && !IsSysadminSession(batch))
            throw SimulatedSqlException.PermissionDenied("EXECUTE", "xp_logininfo", "mssqlsystemresource", "sys").PinLine(1);
        var simulation = batch.Connection.Simulation;
        var option = values[1].IsNull ? null : values[1].AsString.TrimEnd(' ');
        var privilegeArgument = ArgumentFor(arguments, 2, "privilege") is { IsDefault: false } supplied ? supplied : (ProcArgument?)null;
        var all = option is not null && option.Equals("all", StringComparison.OrdinalIgnoreCase);
        var members = option is not null && option.Equals("members", StringComparison.OrdinalIgnoreCase);
        if ((option is not null && !all && !members) || (privilegeArgument is not null && !all) || (values[0].IsNull && (option is not null || privilegeArgument is not null)))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption("xp_logininfo"), 18);

        var rows = new List<SqlValue[]>();
        static SqlValue[] Row(string account, bool isGroup, bool admin, string mapped, string? path) =>
        [
            SqlValue.FromSystemName(account),
            SqlValue.FromVarchar(XpLoginInfoCodeType, isGroup ? "group" : "user"),
            SqlValue.FromVarchar(XpLoginInfoCodeType, admin ? "admin" : "user"),
            SqlValue.FromSystemName(mapped),
            path is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(path),
        ];
        if (values[0].IsNull)
        {
            var logins = new List<ServerLogin>();
            foreach (var (_, login) in simulation.Logins)
            {
                if (login.IsWindows)
                    logins.Add(login);
            }
            logins.Sort((a, b) => simulation.ServerCollation.Compare(a.Name, b.Name));
            foreach (var login in logins)
                rows.Add(Row(login.Name, login.TypeCode == "G", simulation.IsServerPrincipalInRole(login.PrincipalId, SysadminRoleId, throughWindowsGroups: false), login.Name, null));
        }
        else
        {
            var account = values[0].AsString;
            var login = simulation.Logins.TryGetValue(account.TrimEnd(' '), out var found) && found.IsWindows ? found : null;
            if (members)
            {
                if (login is not null)
                    throw XpLoginInfoLookupFailure(account, login.TypeCode == "G" ? (byte)3 : (byte)5, login.TypeCode == "G" ? "0x54f" : "0x8ac", 43);
            }
            else
            {
                if (!account.Contains('\\', StringComparison.Ordinal))
                    throw XpLoginInfoLookupFailure(account, 10, "0xffff0002", 64);
                if (!TryResolveWindowsAccount(account, out _, out var isGroup))
                    throw XpLoginInfoLookupFailure(account, 11, "0x54f", 64);
                if (login is not null)
                {
                    var granting = new List<ServerLogin>();
                    foreach (var groupSid in WindowsTokenGroups(login.PrincipalId))
                    {
                        if (simulation.LoginWithSid(groupSid) is { } group && !granting.Contains(group))
                            granting.Add(group);
                    }
                    foreach (var group in granting)
                        rows.Add(Row(account, isGroup, simulation.IsServerPrincipalInRole(group.PrincipalId, SysadminRoleId, throughWindowsGroups: false), account, group.Name));
                    rows.Add(Row(account, isGroup, simulation.IsServerPrincipalInRole(login.PrincipalId, SysadminRoleId, throughWindowsGroups: false), account, null));
                }
                if (!all && rows.Count > 1)
                    rows.RemoveRange(1, rows.Count - 1);
            }
        }
        if (privilegeArgument is { } privilege)
        {
            if (privilege.OutputSlot is { } slot)
                slot.Value = rows.Count == 0 ? SqlValue.Null(slot.DeclaredType) : rows[0][2].CoerceTo(slot.DeclaredType);
            yield break;
        }
        yield return new SimulatedSqlResultSet(
            [SqlType.SystemName, XpLoginInfoCodeType, XpLoginInfoCodeType, SqlType.SystemName, SqlType.SystemName], XpLoginInfoColumnNames, rows);
    }

    /// <summary>Msg 15404 from <c>xp_logininfo</c>'s <paramref name="line"/>, which ends the batch as under <c>XACT_ABORT</c>.</summary>
    private static SimulatedSqlException XpLoginInfoLookupFailure(string account, byte state, string code, int line) =>
        AtProcedureLine(SimulatedSqlException.WindowsPrincipalLookupFailed(account, state, code), "xp_logininfo", line);

    private static readonly SystemProcedureParameter[] WindowsLoginNameParameter =
    [
        new("loginame", SqlType.SystemName, 128),
    ];

    /// <summary>
    /// <c>sp_grantlogin @loginame</c>: <c>GRANT CONNECT SQL</c> to a Windows login,
    /// else a <c>CREATE LOGIN … FROM WINDOWS</c> for it, which the Linux server
    /// can't do — a well-known account's statement fails with its Msg 15151, and
    /// every such name then meets the procedure's own Msg 15401 at class 11 from
    /// its line 49. Ahead of that, a caller without <c>ALTER ANY LOGIN</c> is Msg
    /// 15247 (line 13), a transaction Msg 15002 (line 21) and a name with no
    /// <c>domain\</c> part Msg 15407 at class 11 (line 28); a failure returns 1
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpGrantLogin(BatchContext batch, string calledAs) =>
        this.InvokeWindowsLoginAccess(batch, calledAs, "sp_grantlogin", deny: false, transactionLine: 21, invalidNameLine: 28, notFoundLine: 49);

    /// <summary>
    /// <c>sp_denylogin @loginame</c>: <c>DENY CONNECT SQL</c> to a Windows login,
    /// creating it first as <see cref="InvokeSpGrantLogin"/> does — with the same
    /// refusals from lines 13, 25, 32 and 46 (probed 2026-10-17 against SQL
    /// Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDenyLogin(BatchContext batch, string calledAs) =>
        this.InvokeWindowsLoginAccess(batch, calledAs, "sp_denylogin", deny: true, transactionLine: 25, invalidNameLine: 32, notFoundLine: 46);

    private IEnumerable<SimulatedStatementOutcome> InvokeWindowsLoginAccess(
        BatchContext batch, string calledAs, string procedure, bool deny, int transactionLine, int invalidNameLine, int notFoundLine)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, WindowsLoginNameParameter);
        var effective = batch.Connection.Security.Effective;
        if (!batch.Connection.Security.EffectiveIsDbo
            && (effective.IsDatabaseScoped || !this.HoldsServerPermission(effective.LoginName, Permission.AlterAnyLogin)))
        {
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 13);
        }
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction(procedure), transactionLine);
        var name = values[0].IsNull ? null : values[0].AsString;
        if (name is not null && !name.Contains('\\', StringComparison.Ordinal))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidWindowsName(name, level: 11), invalidNameLine);
        // Past those checks the work runs in a transaction of the procedure's
        // own: a failed CREATE LOGIN rolls it back, as the procedure's Msg 15401
        // does ahead of itself (probed 2026-10-17 against SQL Server 2025).
        foreach (var outcome in InProcedureTransaction(batch, Body()))
            yield return outcome;

        IEnumerable<SimulatedStatementOutcome> Body()
        {
            if (name is not null && this.Logins.TryGetValue(name.TrimEnd(' '), out var login) && login.IsWindows)
            {
                // The server-scope statement runs in master whatever database the call is made from.
                var statement = this.RunProcedureStatement(batch, (deny ? "deny" : "grant") + " connect sql to " + QuoteIdentifier(login.Name));
                foreach (var produced in ModuleDatabaseScope.Enumerate(batch.Connection, this.Databases[MasterDatabaseName], statement))
                    yield return produced;
                yield break;
            }
            if (name is not null && TryResolveWindowsAccount(name, out _, out _))
            {
                foreach (var produced in this.RunProcedureStatement(batch, "create login " + QuoteIdentifier(name) + " from windows"))
                    yield return produced;
            }
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.WindowsPrincipalNotFound(name ?? "(null)", level: 11), notFoundLine);
        }
    }

    /// <summary>
    /// <c>sp_revokelogin @loginame</c>: <c>DROP LOGIN</c>, whose own refusals —
    /// Msg 15401 for a name the lookup can't find, Msg 15151 for one no login
    /// holds or the caller can't drop — come back from it at line 1, with
    /// return code 1. A transaction is Msg 15002 from line 13, a NULL name
    /// <c>sp_validname</c>'s Msg 15004, and a name with no <c>domain\</c> part
    /// Msg 15407 at class 11 from line 25 (probed 2026-10-17 against SQL
    /// Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpRevokeLogin(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_revokelogin", calledAs, arguments, WindowsLoginNameParameter);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction("sp_revokelogin"), 13);
        var name = RequireValidName(values[0]);
        if (!name.Contains('\\', StringComparison.Ordinal))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidWindowsName(name, level: 11), 25);
        // Its DROP LOGIN opens no implicit transaction (probed 2026-10-17
        // against SQL Server 2025).
        foreach (var outcome in WithoutImplicitTransaction(batch.Connection, this.RunProcedureStatement(batch, "drop login " + QuoteIdentifier(name))))
            yield return outcome;
    }
}
