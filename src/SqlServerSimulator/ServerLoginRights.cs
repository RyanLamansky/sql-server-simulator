namespace SqlServerSimulator;

/// <summary>
/// The server permissions a database-scope check may draw on: the login
/// behind a session's effective identity, whose SERVER-class grants — its own,
/// its server roles', a fixed role's — imply permissions in every database
/// (<c>CONTROL SERVER</c> everything, <c>ALTER ANY DATABASE</c> each
/// database's <c>ALTER</c>, <c>VIEW ANY DEFINITION</c> its <c>VIEW
/// DEFINITION</c>, <c>SELECT ALL USER SECURABLES</c> every user object's
/// <c>SELECT</c>). <see langword="default"/> draws on nothing, which is what
/// an identity minted inside one database carries (probed 2026-09-29 against
/// SQL Server 2025: a <c>CONTROL SERVER</c> login's user reached through
/// <c>EXECUTE AS USER</c> reads nothing it isn't granted in the database).
/// </summary>
/// <remarks>
/// Consulted only once a restricted principal's database grants have come up
/// short and no database <c>DENY</c> reached the request — a database
/// <c>DENY</c> binds even a <c>CONTROL SERVER</c> grantee (probe-confirmed),
/// and a <c>sysadmin</c> never gets here, being <c>dbo</c>.
/// </remarks>
internal readonly struct ServerLoginRights(Simulation simulation, string login)
{
    private readonly Simulation? simulation = simulation;
    private readonly string? login = login;

    /// <summary>The rights of <paramref name="connection"/>'s effective identity: none for a database-scoped one, or in the empty-registry dev mode where every session is <c>dbo</c> anyway.</summary>
    public static ServerLoginRights For(SimulatedDbConnection connection)
    {
        var effective = connection.Security.Effective;
        return effective.IsDatabaseScoped || connection.Simulation.Logins.IsEmpty
            ? default
            : new(connection.Simulation, effective.LoginName);
    }

    /// <summary>The rights of the login <paramref name="loginName"/> — the authenticator a <c>TRUSTWORTHY</c> crossing asks about.</summary>
    public static ServerLoginRights ForLogin(Simulation simulation, string loginName) =>
        simulation.Logins.IsEmpty ? default : new(simulation, loginName);

    /// <summary>Whether the login holds the SERVER-class <paramref name="permission"/>.</summary>
    public bool Holds(Permission permission) =>
        this.simulation is { } server && server.HoldsServerPermission(this.login!, permission);

    /// <summary>
    /// Whether a server permission implies <paramref name="permission"/> on a
    /// securable of <paramref name="securableClass"/> in any database: the
    /// request's database-scope covering chain, each link answered by its
    /// server parent (<c>ServerParent</c>), plus <c>SELECT ALL
    /// USER SECURABLES</c> for a <c>SELECT</c>.
    /// </summary>
    public bool Implies(Permission permission, byte securableClass)
    {
        if (this.simulation is null || permission == Permission.Other)
            return false;
        if (permission == Permission.Select
            && securableClass is PermissionChecker.ClassObject or PermissionChecker.ClassSchema or PermissionChecker.ClassDatabase
            && this.Holds(Permission.SelectAllUserSecurables))
        {
            return true;
        }
        foreach (var link in permission.CoveringChain(PermissionChecker.ClassDatabase))
        {
            if (this.Holds(link.ServerParent))
                return true;
        }
        return false;
    }
}
