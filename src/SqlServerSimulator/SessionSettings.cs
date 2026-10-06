using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// The session state a MARS request works on a copy of: its <c>SET</c>
/// options, <c>CONTEXT_INFO</c>, database context, <c>@@IDENTITY</c> /
/// <c>SCOPE_IDENTITY()</c>, <c>@@ROWCOUNT</c>, <c>@@ERROR</c> and
/// <c>SESSION_CONTEXT</c>. A request copies the
/// session's published settings as it begins executing and publishes its copy
/// once its response has gone out, so a request never sees what another one
/// still running changed, and the last to finish wins — a reader finishing
/// after a second request's <c>SET DATEFORMAT</c> puts the session's format
/// back. <c>SESSION_CONTEXT</c> alone merges: a request publishes only the
/// keys it set (all probed 2026-10-06 against SQL Server 2025). Temp tables,
/// cursors and the transaction stay shared. An instance is never changed once
/// another request or session can see it, so a published one can be read
/// from another session.
/// </summary>
internal sealed class SessionSettings
{
    private SessionSettings(SimulatedDbConnection connection) => this.Refill(connection);

    /// <summary>
    /// Takes <paramref name="connection"/>'s settings again, into an instance
    /// nothing else holds: what lets an in-process reader's start settings,
    /// which no other command needed, serve the next reader's.
    /// </summary>
    public void Refill(SimulatedDbConnection connection)
    {
        this.Options = new SimulatedDbConnection.SessionOptionScope(connection);
        this.QuotedIdentifiers = connection.QuotedIdentifiers;
        this.ParseOnly = connection.ParseOnly;
        this.NoCount = connection.NoCount;
        this.TextSize = connection.TextSize;
        this.ContextInfo = connection.ContextInfo;
        this.Database = connection.CurrentDatabase;
        this.LastIdentity = connection.LastIdentity;
        this.ScopeIdentity = connection.ScopeIdentity;
        this.RowCount = connection.LastStatementRowCount;
        this.ErrorNumber = connection.LastErrorNumber;
        this.SessionContext = connection.SessionContext.Count == 0 ? null : new(connection.SessionContext, SessionContextKeyComparer.Instance);
    }

    private SessionSettings(SessionSettings settings, Dictionary<string, (SqlValue Value, bool ReadOnly)>? sessionContext)
    {
        this.Options = settings.Options;
        this.QuotedIdentifiers = settings.QuotedIdentifiers;
        this.ParseOnly = settings.ParseOnly;
        this.NoCount = settings.NoCount;
        this.TextSize = settings.TextSize;
        this.ContextInfo = settings.ContextInfo;
        this.Database = settings.Database;
        this.LastIdentity = settings.LastIdentity;
        this.ScopeIdentity = settings.ScopeIdentity;
        this.RowCount = settings.RowCount;
        this.ErrorNumber = settings.ErrorNumber;
        this.SessionContext = sessionContext;
    }

    // Assigned only as an instance is built or refilled, never once another
    // request or session may read it.
    public SimulatedDbConnection.SessionOptionScope Options;
    public bool QuotedIdentifiers;
    public bool ParseOnly;
    public bool NoCount;
    public int TextSize;
    public byte[]? ContextInfo;
    public Database Database = null!;
    public Int128? LastIdentity;
    public Int128? ScopeIdentity;
    public int RowCount;
    public int ErrorNumber;

    /// <summary>The keys <c>SESSION_CONTEXT</c> reads, null for none.</summary>
    public Dictionary<string, (SqlValue Value, bool ReadOnly)>? SessionContext;

    /// <summary>The settings <paramref name="connection"/> is working with now.</summary>
    public static SessionSettings Capture(SimulatedDbConnection connection) => new(connection);

    /// <summary>
    /// Makes these the settings <paramref name="connection"/> works with. A
    /// database dropped since they were taken leaves the session where it is.
    /// </summary>
    public void ApplyTo(SimulatedDbConnection connection)
    {
        this.Options.Restore(connection);
        connection.QuotedIdentifiers = this.QuotedIdentifiers;
        connection.ParseOnly = this.ParseOnly;
        connection.NoCount = this.NoCount;
        connection.TextSize = this.TextSize;
        connection.ContextInfo = this.ContextInfo;
        connection.LastIdentity = this.LastIdentity;
        connection.ScopeIdentity = this.ScopeIdentity;
        connection.LastStatementRowCount = this.RowCount;
        connection.LastErrorNumber = this.ErrorNumber;
        connection.SessionContext.Clear();
        if (this.SessionContext is { } keys)
        {
            foreach (var (key, entry) in keys)
                connection.SessionContext[key] = entry;
        }

        var database = this.Database;
        if (ReferenceEquals(connection.CurrentDatabase, database)
            || !connection.Simulation.Databases.TryGetValue(database.Name, out var live)
            || !ReferenceEquals(live, database))
        {
            return;
        }
        if (!PermissionEnforcement.Bypasses(connection, database))
            connection.Security.RebindBaseFrameToDatabaseUser(PermissionEnforcement.ResolveCrossDatabasePrincipal(connection, database));
        connection.CurrentDatabase = database;
    }

    /// <summary>
    /// What the session publishes when a request that began from
    /// <paramref name="start"/> finishes with these settings while
    /// <paramref name="published"/> is the session's: these, with the
    /// <c>SESSION_CONTEXT</c> keys the request set laid over the published ones.
    /// </summary>
    public SessionSettings PublishOver(SessionSettings published, SessionSettings start)
    {
        Dictionary<string, (SqlValue Value, bool ReadOnly)>? merged = null;
        if (this.SessionContext is { } keys)
        {
            foreach (var (key, entry) in keys)
            {
                if (start.SessionContext is { } started && started.TryGetValue(key, out var before) && before.Equals(entry))
                    continue;
                merged ??= published.SessionContext is { } shared ? new(shared, SessionContextKeyComparer.Instance) : new(SessionContextKeyComparer.Instance);
                merged[key] = entry;
            }
        }
        return new(this, merged ?? published.SessionContext);
    }
}
