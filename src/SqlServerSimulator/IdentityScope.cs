namespace SqlServerSimulator;

/// <summary>The kind of module body an <see cref="IdentityScope"/> brackets, which decides what it gives back on exit.</summary>
internal enum IdentityScopeKind
{
    /// <summary>A procedure or dynamic-SQL batch: its INSERTs set <c>@@IDENTITY</c> as the caller's would.</summary>
    Procedure,

    /// <summary>A trigger: <c>@@IDENTITY</c> reads the body's last identity only when it produced one.</summary>
    Trigger,

    /// <summary>A function: its INSERTs never reach <c>@@IDENTITY</c>.</summary>
    Function,
}

/// <summary>
/// The <c>SCOPE_IDENTITY()</c> scope a module body or dynamic-SQL batch runs in:
/// the body starts at NULL and the caller reads its own value again when it
/// returns. Probed 2026-09-28 against SQL Server 2025: after <c>EXEC p</c>, a
/// dynamic-SQL <c>INSERT</c> or a trigger's, the caller's
/// <c>SCOPE_IDENTITY()</c> is what its own last INSERT set, while
/// <c>@@IDENTITY</c> reads the body's — save that a trigger producing no
/// identity value leaves the firing statement's in place (an INSERT into a
/// table without one clears it anywhere else), and a function's INSERT into
/// its return table touches neither.
/// </summary>
internal readonly struct IdentityScope
{
    private readonly SimulatedDbConnection connection;
    private readonly decimal? callerScopeIdentity;
    private readonly decimal? callerIdentity;
    private readonly long callerGenerations;

    private IdentityScope(SimulatedDbConnection connection)
    {
        this.connection = connection;
        this.callerScopeIdentity = connection.ScopeIdentity;
        this.callerIdentity = connection.LastIdentity;
        this.callerGenerations = connection.IdentityGenerations;
    }

    /// <summary>Opens a body's scope, whose <c>SCOPE_IDENTITY()</c> starts at NULL.</summary>
    public static IdentityScope Enter(SimulatedDbConnection connection)
    {
        var scope = new IdentityScope(connection);
        connection.ScopeIdentity = null;
        return scope;
    }

    /// <summary>Closes the scope, handing the caller back its own <c>SCOPE_IDENTITY()</c>.</summary>
    public void Exit(IdentityScopeKind kind)
    {
        this.connection.ScopeIdentity = this.callerScopeIdentity;
        if (kind == IdentityScopeKind.Function
            || (kind == IdentityScopeKind.Trigger && this.connection.IdentityGenerations == this.callerGenerations))
        {
            this.connection.LastIdentity = this.callerIdentity;
        }
    }
}
