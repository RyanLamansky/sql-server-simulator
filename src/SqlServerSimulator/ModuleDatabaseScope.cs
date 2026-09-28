namespace SqlServerSimulator;

/// <summary>
/// The database a module body binds and runs in: its own, whatever database
/// the session is in. Probed 2026-09-28 against SQL Server 2025 for procedures,
/// scalar and table-valued functions, views and triggers reached through a
/// three-part name or a synonym: unqualified names, <c>DB_NAME()</c>,
/// <c>OBJECT_ID</c> / <c>OBJECT_NAME(@@PROCID)</c>, the catalog views and
/// dynamic SQL the body runs all read the module's database, while
/// <c>@@ROWCOUNT</c>, <c>SCOPE_IDENTITY()</c>, the transaction and
/// <c>#temp</c> tables stay the session's. Not a <c>USE</c>: the caller
/// resumes in its own database when the body returns, and no message is sent.
/// </summary>
/// <remarks>
/// A login that doesn't bypass permission checks also runs as its user in the
/// module's database for the body's duration — <c>USER_NAME()</c> reads that
/// user (or <c>guest</c>), and the body's same-database references are chained
/// or checked from there — and a login with no user there is Msg 916. The
/// analysis-only callers (a view body parsed for its columns, an indexed view
/// maintained under a write) switch the database alone.
/// </remarks>
internal readonly struct ModuleDatabaseScope
{
    private readonly SimulatedDbConnection? connection;
    private readonly Database? enteredFrom;
    private readonly int impersonationDepth;

    private ModuleDatabaseScope(SimulatedDbConnection connection, Database enteredFrom, int impersonationDepth)
    {
        this.connection = connection;
        this.enteredFrom = enteredFrom;
        this.impersonationDepth = impersonationDepth;
    }

    /// <summary>
    /// Switches <paramref name="connection"/> into <paramref name="database"/>
    /// until <see cref="Exit"/>; a no-op scope when the session is already
    /// there. <paramref name="bindsIdentity"/> false switches the database
    /// alone, for a body that is only parsed.
    /// </summary>
    public static ModuleDatabaseScope Enter(SimulatedDbConnection connection, Database database, bool bindsIdentity = true)
    {
        var current = connection.CurrentDatabase;
        if (ReferenceEquals(current, database))
            return default;
        var security = connection.Security;
        var depth = security.ImpersonationDepth;
        if (bindsIdentity && !PermissionEnforcement.Bypasses(connection, database))
        {
            // Resolved while the session's database is still current, which is
            // where a database-scoped token was minted.
            var effective = security.Effective;
            var principal = PermissionEnforcement.ResolveCrossDatabasePrincipal(connection, database);
            security.Push(new SecurityPrincipalFrame(principal.PrincipalId, principal.Name, effective.LoginName, effective.IsDatabaseScoped));
        }
        connection.CurrentDatabase = database;
        return new(connection, current, depth);
    }

    /// <summary>Returns the session to the database, and the identity, it entered from.</summary>
    public void Exit()
    {
        if (this.connection is null)
            return;
        this.connection.CurrentDatabase = this.enteredFrom!;
        this.connection.Security.RevertTo(this.impersonationDepth);
    }

    /// <summary>
    /// Runs each step of <paramref name="source"/> — an inlined body's lazily
    /// produced rows — inside <paramref name="database"/>, stepping back out
    /// before each row reaches the referencing statement, whose own
    /// expressions evaluate between steps in the session's database.
    /// </summary>
    public static IEnumerable<T> Enumerate<T>(SimulatedDbConnection connection, Database database, IEnumerable<T> source)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            T current;
            var scope = Enter(connection, database);
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
                current = enumerator.Current;
            }
            finally
            {
                scope.Exit();
            }
            yield return current;
        }
    }
}
