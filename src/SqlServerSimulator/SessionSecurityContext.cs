namespace SqlServerSimulator;

/// <summary>
/// One layer of a session's identity: the database principal a statement runs
/// as, plus the server login that identity reports through
/// <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c>. The base frame is the connect-time
/// principal; <c>EXECUTE AS</c> and module <c>WITH EXECUTE AS</c> push additional
/// frames.
/// </summary>
internal readonly struct SecurityPrincipalFrame(int databasePrincipalId, string databasePrincipalName, string loginName, bool isDatabaseScoped = false, ExecuteAsGuard guard = default)
{
    /// <summary>What a <c>REVERT</c> must satisfy to pop this frame.</summary>
    public readonly ExecuteAsGuard Guard = guard;

    /// <summary><c>sys.database_principals.principal_id</c> of the effective database user.</summary>
    public readonly int DatabasePrincipalId = databasePrincipalId;

    /// <summary>The effective database-user name (<c>CURRENT_USER</c> / <c>USER_NAME()</c>).</summary>
    public readonly string DatabasePrincipalName = databasePrincipalName;

    /// <summary>The login this frame reports through <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c> — a login name or a WITHOUT-LOGIN SID string.</summary>
    public readonly string LoginName = loginName;

    /// <summary>
    /// Whether this identity exists only inside one database — an
    /// <c>EXECUTE AS USER</c> frame, any of a module's <c>WITH EXECUTE AS</c>
    /// frames (<c>OWNER</c> / <c>SELF</c> included, though both resolve to
    /// <c>dbo</c>), or an activated application role. Such an identity carries no
    /// server principal, so out of an ordinary database it cannot reach another
    /// one at all: every cross-database reference raises Msg 916 naming
    /// <see cref="LoginName"/> (probe-confirmed against SQL Server 2025 — the
    /// refusal stands even when the session's own login is <c>sa</c>). Out of a
    /// <see cref="Database.Trustworthy"/> database the token is accepted and the
    /// frame's own login answers in the target, so a <c>WITHOUT LOGIN</c> user
    /// (whose <see cref="LoginName"/> is a SID) is still refused. A login-based
    /// frame (connect-time or <c>EXECUTE AS LOGIN</c>) is false and maps into
    /// the target database normally.
    /// </summary>
    public readonly bool IsDatabaseScoped = isDatabaseScoped;
}

/// <summary>
/// The conditions an <c>EXECUTE AS</c> statement attached to its frame:
/// the database it ran in (a <c>REVERT</c> elsewhere is Msg 15199), and
/// <c>WITH NO REVERT</c> (Msg 15196) or <c>WITH COOKIE INTO</c> (only a
/// <c>REVERT WITH COOKIE</c> presenting it pops the frame, Msg 15591
/// otherwise). A module's <c>WITH EXECUTE AS</c> frame carries
/// <see cref="Module"/>, which a <c>REVERT</c> in its body leaves alone
/// (probed 2026-10-04 against SQL Server 2025).
/// </summary>
internal readonly struct ExecuteAsGuard(string? databaseName = null, bool noRevert = false, byte[]? cookie = null, bool module = false)
{
    public readonly string? DatabaseName = databaseName;
    public readonly bool NoRevert = noRevert;
    public readonly byte[]? Cookie = cookie;
    public readonly bool Module = module;
}

/// <summary>
/// A connection's security identity: the original server login, the base
/// database principal, and an impersonation stack. Lives on
/// <see cref="SimulatedDbConnection"/> (session scope). An unauthenticated
/// in-process connection uses <see cref="CreateDefault"/> — <c>dbo</c> as
/// login, database user, and original login everywhere — so existing consumers
/// see identical identity-scalar output.
/// </summary>
/// <remarks>
/// This is the read every enforcement gate starts from: the identity scalars
/// (<c>CURRENT_USER</c> / <c>SYSTEM_USER</c> / <c>ORIGINAL_LOGIN()</c> /
/// <c>USER_ID()</c> / …) report the effective frame under <c>EXECUTE AS</c>,
/// and the permission checker, the catalog-view metadata filters and the DMV
/// gates all short-circuit on <see cref="EffectiveIsDbo"/> before allocating.
/// An <c>sp_setapprole</c> activation is the one mutation that replaces the
/// <em>base</em> frame rather than pushing onto the impersonation stack.
/// </remarks>
internal sealed class SessionSecurityContext(SecurityPrincipalFrame baseFrame, string originalLoginName)
{
    private readonly List<SecurityPrincipalFrame> impersonation = [];

    /// <summary>The session's original login, before any impersonation — the value <c>ORIGINAL_LOGIN()</c> reports.</summary>
    public readonly string OriginalLoginName = originalLoginName;

    /// <summary>
    /// The dbo-everywhere identity for an unauthenticated in-process
    /// connection: the <c>sa</c> login, which is what a default connection to
    /// a real server runs as and what <c>SUSER_SNAME()</c> /
    /// <c>SYSTEM_USER</c> / <c>ORIGINAL_LOGIN()</c> then report.
    /// </summary>
    public static SessionSecurityContext CreateDefault() =>
        new(new SecurityPrincipalFrame(Database.DboPrincipalId, "dbo", "sa"), "sa");

    /// <summary>The frame every statement runs as: the top impersonation frame, or the base identity.</summary>
    public SecurityPrincipalFrame Effective
    {
        get
        {
#if DEBUG
            PlanCacheCaptureAudit.NotePrincipalRead(this);
#endif
            return this.impersonation.Count > 0 ? this.impersonation[^1] : baseFrame;
        }
    }

    /// <summary>
    /// Bumped by every change of the effective principal — an impersonation
    /// frame pushed or popped, an application role set or unset, the base frame
    /// rebound — so a cached derivation of it (a batch's default schema) can
    /// tell it is stale without reading <see cref="Effective"/>.
    /// </summary>
    public int Generation;

    /// <summary>
    /// The effective principal's default schema in <paramref name="database"/>:
    /// the schema an unqualified name searches before <c>dbo</c> and an
    /// unqualified <c>CREATE</c> lands in. <c>dbo</c>'s is always <c>dbo</c>,
    /// <c>guest</c>'s <c>guest</c>, and a user's or application role's its
    /// declared <c>DEFAULT_SCHEMA</c> as written — possibly naming no schema —
    /// or <c>dbo</c> when it declared none (probed 2026-10-04 against SQL
    /// Server 2025, a <c>db_owner</c> member keeping its own).
    /// </summary>
    public string EffectiveDefaultSchemaName(Database database)
    {
        var effective = this.Effective;
        return effective.DatabasePrincipalId switch
        {
            Database.DboPrincipalId => Database.DefaultSchemaName,
            Database.GuestPrincipalId => "guest",
            _ => database.Principals.TryGetValue(effective.DatabasePrincipalName, out var principal)
                && principal.PrincipalId == effective.DatabasePrincipalId
                && principal.DefaultSchemaName is { } declared
                    ? declared
                    : Database.DefaultSchemaName,
        };
    }

    /// <summary>True when the effective database principal is <c>dbo</c> — the same-database enforcement bypass. A reference that crosses a database boundary asks the boundary-aware form instead, since a <c>dbo</c> frame can be database-scoped.</summary>
    public bool EffectiveIsDbo => this.Effective.DatabasePrincipalId == Database.DboPrincipalId;

    /// <summary>Current impersonation-stack depth, captured on module entry so the matching exit can unwind exactly its own frames.</summary>
    public int ImpersonationDepth => this.impersonation.Count;

    /// <summary>
    /// How many frames at the bottom of the stack a batch's own <c>EXECUTE
    /// AS</c> pushed outside any module: the session's context, which each
    /// of its MARS requests runs in, where the frames above it — a module's,
    /// or an <c>EXECUTE AS</c> a module body ran — belong to the request whose
    /// call pushed them (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    public int SessionFrames;

    /// <summary>
    /// Takes the frames above <see cref="SessionFrames"/> off the stack for a
    /// request stepping aside inside a module call, null when there are none.
    /// </summary>
    public SecurityPrincipalFrame[]? TakeRequestFrames()
    {
        var count = this.impersonation.Count - this.SessionFrames;
        if (count <= 0)
            return null;
        var frames = this.impersonation.GetRange(this.SessionFrames, count).ToArray();
        this.impersonation.RemoveRange(this.SessionFrames, count);
        this.Generation++;
        return frames;
    }

    /// <summary>Puts back the frames <see cref="TakeRequestFrames"/> took, for the request resuming.</summary>
    public void RestoreRequestFrames(SecurityPrincipalFrame[] frames)
    {
        this.impersonation.AddRange(frames);
        this.Generation++;
    }

    /// <summary>
    /// The active application role's name, or <see langword="null"/> when none
    /// is set. An <c>sp_setapprole</c> activation replaces the session's
    /// database principal wholesale (the login stays, so <c>SYSTEM_USER</c> /
    /// <c>ORIGINAL_LOGIN()</c> are unchanged) and pins the session to its
    /// database until <c>sp_unsetapprole</c> — <c>USE</c> raises Msg 505 while
    /// it is set, and there is no cookie-less way back.
    /// </summary>
    public string? ApplicationRoleName;

    /// <summary>The cookie <c>sp_setapprole … @fCreateCookie = 1</c> handed out — the only token <c>sp_unsetapprole</c> accepts. Null when the activation created none.</summary>
    public byte[]? ApplicationRoleCookie;

    /// <summary>The frame to restore when the application role is unset — the base identity captured at activation.</summary>
    private SecurityPrincipalFrame preApplicationRoleFrame;

    /// <summary>True while an application role is active — the Msg 505 <c>USE</c> gate.</summary>
    public bool HasApplicationRole => this.ApplicationRoleName is not null;

    /// <summary>
    /// Activates an application role: swaps the base frame to the role's
    /// principal (keeping <paramref name="loginName"/> as the reported login)
    /// and records the cookie a later <c>sp_unsetapprole</c> must present.
    /// </summary>
    public void SetApplicationRole(string roleName, int rolePrincipalId, string loginName, byte[]? cookie)
    {
        this.preApplicationRoleFrame = baseFrame;
        baseFrame = new SecurityPrincipalFrame(rolePrincipalId, roleName, loginName, isDatabaseScoped: true);
        this.Generation++;
        this.ApplicationRoleName = roleName;
        this.ApplicationRoleCookie = cookie;
    }

    /// <summary>
    /// Deactivates the application role, restoring the pre-activation base
    /// frame. Returns false when no role is set or
    /// <paramref name="cookie"/> doesn't match the one issued — the Msg 15592
    /// case.
    /// </summary>
    public bool TryUnsetApplicationRole(byte[]? cookie)
    {
        if (this.ApplicationRoleName is null
            || this.ApplicationRoleCookie is not { } issued
            || cookie is null
            || !issued.AsSpan().SequenceEqual(cookie))
        {
            return false;
        }
        baseFrame = this.preApplicationRoleFrame;
        this.Generation++;
        this.ApplicationRoleName = null;
        this.ApplicationRoleCookie = null;
        return true;
    }

    /// <summary>
    /// Reseats the base frame on the database user <paramref name="principal"/>
    /// after the session switched databases — a login's identity is per
    /// database, so <c>USE other</c> makes <c>CURRENT_USER</c> the login's user
    /// in <c>other</c> while <c>SYSTEM_USER</c> / <c>ORIGINAL_LOGIN()</c> stay
    /// put (probe-confirmed). An application role refuses the switch outright.
    /// </summary>
    public void RebindBaseFrameToDatabaseUser(DatabasePrincipal principal)
    {
        // An impersonating session rebinds the frame in effect, keeping its
        // login and the guard its REVERT answers to (probed 2026-10-04 against
        // SQL Server 2025: EXECUTE AS USER then USE master answers guest).
        if (this.impersonation.Count > 0)
        {
            var top = this.impersonation[^1];
            this.impersonation[^1] = new SecurityPrincipalFrame(principal.PrincipalId, principal.Name, top.LoginName, top.IsDatabaseScoped, top.Guard);
            this.Generation++;
            return;
        }
        baseFrame = new SecurityPrincipalFrame(principal.PrincipalId, principal.Name, baseFrame.LoginName);
        this.Generation++;
    }

    /// <summary>Pushes one impersonation frame (<c>EXECUTE AS</c> or a module's <c>WITH EXECUTE AS</c>).</summary>
    public void Push(SecurityPrincipalFrame frame)
    {
        this.impersonation.Add(frame);
        this.Generation++;
    }

    /// <summary>
    /// Pops one impersonation frame for a <c>REVERT</c> run in
    /// <paramref name="currentDatabase"/> presenting <paramref name="cookie"/>,
    /// or raises the refusal its guard calls for. A stray <c>REVERT</c> at the
    /// base identity, and one reaching a module's own frame, is a silent no-op
    /// (probe-confirmed).
    /// </summary>
    public void Revert(string currentDatabase, byte[]? cookie)
    {
        if (this.impersonation.Count == 0)
            return;
        var guard = this.impersonation[^1].Guard;
        if (guard.Module)
            return;
        if (guard.DatabaseName is { } database && !BuiltInToken.Comparer.Equals(database, currentDatabase))
            throw SimulatedSqlException.RevertInAnotherDatabase();
        if (guard.NoRevert)
            throw SimulatedSqlException.RevertOfNonRevertibleContext();
        if (guard.Cookie is { } issued ? cookie is null || !issued.AsSpan().SequenceEqual(cookie) : cookie is not null)
            throw SimulatedSqlException.RevertNeedsMatchingCookie();
        this.impersonation.RemoveAt(this.impersonation.Count - 1);
        this.SessionFrames = Math.Min(this.SessionFrames, this.impersonation.Count);
        this.Generation++;
    }

    /// <summary>Unwinds the stack back to <paramref name="depth"/> frames — the module-exit revert that survives a body that left frames pushed.</summary>
    public void RevertTo(int depth)
    {
        while (this.impersonation.Count > depth)
        {
            this.impersonation.RemoveAt(this.impersonation.Count - 1);
            this.Generation++;
        }
        this.SessionFrames = Math.Min(this.SessionFrames, this.impersonation.Count);
    }
}
