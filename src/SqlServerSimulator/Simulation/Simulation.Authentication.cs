using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Validates SQL-authentication credentials against <see cref="Logins"/>.
    /// An empty registry accepts anything (the zero-configuration default the
    /// TDS endpoint and the whole test corpus rely on); once
    /// <c>CREATE LOGIN</c> has populated it, the name must resolve and the
    /// password must verify. Shared by the in-process connection-string login
    /// path; the TDS endpoint keeps its own equivalent that reads the
    /// wire-de-obfuscated password.
    /// </summary>
    internal bool ValidateLoginCredentials(string userName, string password) =>
        this.Logins.IsEmpty
        || (this.Logins.TryGetValue(userName, out var login) && PasswordHash.Verify(password, login.PasswordHash));

    /// <summary>
    /// Resolves the database user a login runs as in <paramref name="target"/>,
    /// returning <see langword="false"/> (a Msg 4060 connect refusal, Msg 916 on
    /// a mid-session <c>USE</c>) when the login has no access. Resolution order
    /// (probe-confirmed against SQL Server 2025, PROBE_NOTES_HARDENING bundle 1):
    /// <list type="number">
    /// <item>An <b>empty login registry</b> is the zero-configuration dev mode:
    /// any credentials map to <c>dbo</c> everywhere. This is the honest
    /// "no authentication configured ⇒ open" default and the back-compat
    /// invariant the whole no-login test corpus rides on.</item>
    /// <item>A <b>sysadmin-member login</b> (<c>sa</c>, or any login added to the
    /// <c>sysadmin</c> fixed server role) → <c>dbo</c> in every database,
    /// overriding any explicit <c>FOR LOGIN</c> mapping (probe6 N3). The dbo
    /// effective principal then bypasses every check, including explicit
    /// DENY (N3b).</item>
    /// <item>An explicit mapped user (<c>CREATE USER … FOR LOGIN</c>) in the
    /// target database → that (restricted) user.</item>
    /// <item><c>guest</c> where it is accessible (<c>master</c> / <c>tempdb</c> /
    /// <c>msdb</c>, aligned with <c>HAS_DBACCESS</c>; not <c>model</c>, not user
    /// databases) → the <c>guest</c> principal (id 2, a genuinely restricted
    /// principal whose effective rights flow through the normal checker).</item>
    /// <item>Otherwise <b>refuse</b> — the login cannot open this database.</item>
    /// </list>
    /// There is no permissive <c>dbo</c> fallback for an authenticated login once
    /// the registry is non-empty: an unmapped login lands on <c>guest</c> where
    /// accessible or is refused, matching real SQL Server.
    /// </summary>
    internal static bool TryMapLoginToDatabaseUser(Simulation simulation, Database target, string loginName, out DatabasePrincipal principal)
    {
        // Empty login registry => open dev mode: any credentials, dbo everywhere
        // (the zero-configuration back-compat invariant).
        if (simulation.Logins.IsEmpty)
        {
            principal = target.Principals["dbo"];
            return true;
        }

        if (simulation.IsLoginSysadmin(loginName))
        {
            principal = target.Principals["dbo"];
            return true;
        }

        foreach (var candidate in target.Principals.Values)
        {
            if (candidate.LoginName is { } linked && target.Collation.Equals(linked, loginName))
            {
                principal = candidate;
                return true;
            }
        }

        // An unmapped login runs as guest where guest is accessible, else the
        // database refuses the connection (Msg 4060 / 916). No dbo fallback.
        if (IsGuestAccessible(target))
        {
            principal = target.Principals["guest"];
            return true;
        }

        principal = null!;
        return false;
    }

    /// <summary>
    /// Whether the seeded <c>guest</c> principal is accessible in
    /// <paramref name="target"/> — the databases <c>HAS_DBACCESS</c> reports
    /// <c>1</c> for guest: <c>master</c> / <c>tempdb</c> / <c>msdb</c>. Guest is
    /// inaccessible in the <c>model</c> template and in every user database, so
    /// an unmapped login is refused there.
    /// </summary>
    private static bool IsGuestAccessible(Database target) =>
        SystemDatabaseNames.Contains(target.Name)
        && !BuiltInToken.Comparer.Equals(target.Name, ModelDatabaseName);

    /// <summary>
    /// Builds the connect-time <see cref="SessionSecurityContext"/> for an
    /// authenticated login: the base frame runs as the resolved database user
    /// (<c>CURRENT_USER</c>) while <c>SYSTEM_USER</c> / <c>ORIGINAL_LOGIN()</c>
    /// report the login name.
    /// </summary>
    internal static SessionSecurityContext BuildAuthenticatedSecurityContext(DatabasePrincipal principal, string loginName) =>
        new(new SecurityPrincipalFrame(principal.PrincipalId, principal.Name, loginName), loginName);
}
