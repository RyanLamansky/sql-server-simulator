namespace SqlServerSimulator;

/// <summary>
/// One layer of a session's identity: the database principal a statement runs
/// as, plus the server login that identity reports through
/// <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c>. The base frame is the connect-time
/// principal; <c>EXECUTE AS</c> and module <c>WITH EXECUTE AS</c> push additional
/// frames.
/// </summary>
internal readonly struct SecurityPrincipalFrame(int databasePrincipalId, string databasePrincipalName, string loginName)
{
    /// <summary><c>sys.database_principals.principal_id</c> of the effective database user.</summary>
    public readonly int DatabasePrincipalId = databasePrincipalId;

    /// <summary>The effective database-user name (<c>CURRENT_USER</c> / <c>USER_NAME()</c>).</summary>
    public readonly string DatabasePrincipalName = databasePrincipalName;

    /// <summary>The login this frame reports through <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c> — a login name or a WITHOUT-LOGIN SID string.</summary>
    public readonly string LoginName = loginName;
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
/// The simulator does not enforce permissions in this stage; the context exists
/// so the identity scalars (<c>CURRENT_USER</c> / <c>SYSTEM_USER</c> /
/// <c>ORIGINAL_LOGIN()</c> / <c>USER_ID()</c> / …) report the truth under
/// <c>EXECUTE AS</c>, and so a future enforcement stage reads the effective
/// principal id and the <see cref="EffectiveIsDbo"/> bypass here.
/// </remarks>
internal sealed class SessionSecurityContext(SecurityPrincipalFrame baseFrame, string originalLoginName)
{
    private readonly List<SecurityPrincipalFrame> impersonation = [];

    /// <summary>The session's original login, before any impersonation — the value <c>ORIGINAL_LOGIN()</c> reports.</summary>
    public readonly string OriginalLoginName = originalLoginName;

    /// <summary>The dbo-everywhere identity for an unauthenticated in-process connection.</summary>
    public static SessionSecurityContext CreateDefault() =>
        new(new SecurityPrincipalFrame(Database.DboPrincipalId, "dbo", "dbo"), "dbo");

    /// <summary>The frame every statement runs as: the top impersonation frame, or the base identity.</summary>
    public SecurityPrincipalFrame Effective =>
        this.impersonation.Count > 0 ? this.impersonation[^1] : baseFrame;

    /// <summary>True while at least one <c>EXECUTE AS</c> / module frame is active.</summary>
    public bool IsImpersonating => this.impersonation.Count > 0;

    /// <summary>True when the effective database principal is <c>dbo</c> — the bypass a future enforcement stage short-circuits on, and the "unrestricted, may USE" gate today.</summary>
    public bool EffectiveIsDbo => this.Effective.DatabasePrincipalId == Database.DboPrincipalId;

    /// <summary>Current impersonation-stack depth, captured on module entry so the matching exit can unwind exactly its own frames.</summary>
    public int ImpersonationDepth => this.impersonation.Count;

    /// <summary>Pushes one impersonation frame (<c>EXECUTE AS</c> or a module's <c>WITH EXECUTE AS</c>).</summary>
    public void Push(SecurityPrincipalFrame frame) => this.impersonation.Add(frame);

    /// <summary>Pops one impersonation frame; a stray <c>REVERT</c> at the base identity is a silent no-op (probe-confirmed).</summary>
    public void Revert()
    {
        if (this.impersonation.Count > 0)
            this.impersonation.RemoveAt(this.impersonation.Count - 1);
    }

    /// <summary>Unwinds the stack back to <paramref name="depth"/> frames — the module-exit revert that survives a body that left frames pushed.</summary>
    public void RevertTo(int depth)
    {
        while (this.impersonation.Count > depth)
            this.impersonation.RemoveAt(this.impersonation.Count - 1);
    }
}
