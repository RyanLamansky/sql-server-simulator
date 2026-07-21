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
    /// returning <see langword="false"/> (a Msg 4060 connect refusal) when the
    /// login has no access. Resolution order:
    /// <list type="number">
    /// <item>An explicit mapped user (<c>CREATE USER … FOR LOGIN</c>) in the
    /// target database.</item>
    /// <item><c>sa</c> maps to <c>dbo</c> everywhere.</item>
    /// <item>A login that participates in the user-mapping model anywhere (has
    /// at least one <c>FOR LOGIN</c> user) but not here: <c>guest</c> in
    /// <c>master</c>, else refused.</item>
    /// <item>Otherwise <c>dbo</c> — the permissive back-compat default so a
    /// login with no mappings behaves exactly as the pre-identity endpoint did
    /// (any credentials, full access). This deliberately diverges from real SQL
    /// Server's guest/4060 behavior for unmapped logins; the strict path
    /// engages only once a login is mapping-managed.</item>
    /// </list>
    /// </summary>
    internal static bool TryMapLoginToDatabaseUser(Simulation simulation, Database target, string loginName, out DatabasePrincipal principal)
    {
        foreach (var candidate in target.Principals.Values)
        {
            if (candidate.LoginName is { } linked && target.Collation.Equals(linked, loginName))
            {
                principal = candidate;
                return true;
            }
        }

        if (BuiltInToken.Comparer.Equals(loginName, "sa"))
        {
            principal = target.Principals["dbo"];
            return true;
        }

        if (LoginHasAnyMappedUser(simulation, loginName))
        {
            if (BuiltInToken.Comparer.Equals(target.Name, MasterDatabaseName))
            {
                principal = target.Principals["guest"];
                return true;
            }
            principal = null!;
            return false;
        }

        principal = target.Principals["dbo"];
        return true;
    }

    /// <summary>
    /// True when <paramref name="loginName"/> has a <c>CREATE USER … FOR LOGIN</c>
    /// mapping in any database — the flag that flips a login from the permissive
    /// unmapped default to the strict mapped-user / guest / 4060 semantics.
    /// </summary>
    private static bool LoginHasAnyMappedUser(Simulation simulation, string loginName)
    {
        lock (simulation.Databases)
        {
            foreach (var database in simulation.Databases.Values)
            {
                foreach (var principal in database.Principals.Values)
                {
                    if (principal.LoginName is { } linked && database.Collation.Equals(linked, loginName))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the connect-time <see cref="SessionSecurityContext"/> for an
    /// authenticated login: the base frame runs as the resolved database user
    /// (<c>CURRENT_USER</c>) while <c>SYSTEM_USER</c> / <c>ORIGINAL_LOGIN()</c>
    /// report the login name.
    /// </summary>
    internal static SessionSecurityContext BuildAuthenticatedSecurityContext(DatabasePrincipal principal, string loginName) =>
        new(new SecurityPrincipalFrame(principal.PrincipalId, principal.Name, loginName), loginName);
}
