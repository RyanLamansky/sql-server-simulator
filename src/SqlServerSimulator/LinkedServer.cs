namespace SqlServerSimulator;

/// <summary>
/// An active linked-server entry on a <see cref="Simulation"/>: a name +
/// the <see cref="Simulation"/> instance four-part-name references (and
/// remote-query round-trips) are routed to. Created by
/// <c>sp_addlinkedserver</c> after the parent <see cref="Simulation"/> has
/// registered the target via
/// <see cref="Simulation.AddRemoteSimulation(string, Simulation)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads (<c>SELECT</c>, <c>JOIN</c>) traversing a
/// <c>linkedserver.db.schema.t</c> reference take a pooled
/// <see cref="SimulatedDbConnection"/> on <see cref="Target"/> and execute
/// the remote portion as a <c>SELECT * FROM [db].[schema].[t]</c>
/// statement through the remote's full parser / planner / lock-manager
/// pipeline — matching real SQL Server's remote-query semantics for the
/// modeled subset.
/// </para>
/// <para>
/// Writes through a four-part name or an <c>OPENQUERY</c> target run
/// against a local stand-in of the remote table and are then replayed on
/// <see cref="Target"/> as parameterized statements inside one remote
/// transaction (<c>Simulation.RemoteDml</c>).
/// </para>
/// </remarks>
internal sealed class LinkedServer(string name, Simulation target, string srvProduct, string provider, string? dataSource, string? location, string? providerString, string? catalog, DateTime createDate)
{
    public readonly string Name = name;

    public readonly Simulation Target = target;

    /// <summary>
    /// The <c>@srvproduct</c> arg from <c>sp_addlinkedserver</c>. Surfaced
    /// via <c>sys.servers.product</c>; no behavioral effect.
    /// </summary>
    public readonly string SrvProduct = srvProduct;

    /// <summary>
    /// The <c>@provider</c> arg from <c>sp_addlinkedserver</c>. Surfaced
    /// via <c>sys.servers.provider</c>; no behavioral effect.
    /// </summary>
    public readonly string Provider = provider;

    /// <summary>
    /// The <c>@datasrc</c> arg from <c>sp_addlinkedserver</c> when
    /// supplied. Surfaced via <c>sys.servers.data_source</c>; no
    /// behavioral effect (the routing key is <see cref="Name"/>, never
    /// this).
    /// </summary>
    public readonly string? DataSource = dataSource;

    /// <summary>The <c>@location</c> arg, surfaced via <c>sys.servers.location</c>.</summary>
    public readonly string? Location = location;

    /// <summary>The <c>@provstr</c> arg, surfaced via <c>sys.servers.provider_string</c>.</summary>
    public readonly string? ProviderString = providerString;

    /// <summary>The <c>@catalog</c> arg, surfaced via <c>sys.servers.catalog</c>.</summary>
    public readonly string? Catalog = catalog;

    /// <summary>
    /// A session of the server in <paramref name="database"/>, or where the
    /// provider's session starts when none is named
    /// (<see cref="SessionDatabaseName"/>): the <c>@catalog</c> database, else
    /// the login's default database — <c>master</c>, as real's <c>sa</c> login
    /// has it (probed 2026-09-28 against SQL Server 2025, where
    /// <c>DB_NAME()</c> inside <c>EXEC … AT</c> and <c>OPENQUERY</c> reads the
    /// catalog when one is set). The session comes from the provider's pool
    /// when one is idle there, reset as a pooled connection is — its
    /// <c>@@SPID</c> kept, everything else it held gone — so an
    /// <c>EXEC … AT</c>, an <c>OPENQUERY</c> and a four-part read meet the
    /// same <c>@@SPID</c> call after call, across the caller's batches and
    /// sessions (probed 2026-10-17 against SQL Server 2025). Disposing the
    /// lease hands it back (<see cref="ReleaseSession"/>).
    /// </summary>
    public RemoteSession OpenSession(string? database)
    {
        // A server mapping no login lets no one in (probed 2026-10-05 against
        // SQL Server 2025).
        if (this.Logins.Count == 0)
            throw SimulatedSqlException.NoLoginMapping();
        var connection = this.TakeIdleSession();
        if (connection is null)
        {
            connection = this.Target.CreateDbConnection();
            connection.Open();
        }
        connection.ChangeDatabase(database ?? this.SessionDatabaseName);
        return new RemoteSession(this, connection);
    }

    /// <summary>
    /// The sessions the provider's pool holds between uses, sleeping on the
    /// server as real's do; guarded by itself.
    /// </summary>
    private readonly List<SimulatedDbConnection> idleSessions = [];

    /// <summary>
    /// An idle pooled session, reset onto a fresh connection under its
    /// <c>@@SPID</c> as the TDS endpoint's <c>sp_reset_connection</c> resets
    /// one, its logon triggers firing as a pooled login's; null when none is
    /// idle.
    /// </summary>
    private SimulatedDbConnection? TakeIdleSession()
    {
        SimulatedDbConnection? idle = null;
        lock (this.idleSessions)
        {
            while (idle is null && this.idleSessions.Count > 0)
            {
                idle = this.idleSessions[^1];
                this.idleSessions.RemoveAt(this.idleSessions.Count - 1);
                if (idle.State != System.Data.ConnectionState.Open)
                    idle = null;
            }
        }
        if (idle is null)
            return null;
        var spid = idle.Spid;
        this.Target.BeginSessionReset(spid);
        try
        {
            idle.ReplacedByReset = true;
            idle.Dispose();
            var fresh = new SimulatedDbConnection(this.Target, spid);
            fresh.OpenSession();
            this.Target.FireLogonTriggers(fresh, isPooled: true);
            return fresh;
        }
        finally
        {
            this.Target.EndSessionReset(spid);
        }
    }

    /// <summary>
    /// Hands a session back to the provider's pool once its use ends, or
    /// closes it when it can't be reused: killed, or with a transaction still
    /// open.
    /// </summary>
    public void ReleaseSession(SimulatedDbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open || connection.CurrentTransaction is not null)
        {
            connection.Dispose();
            return;
        }
        lock (this.idleSessions)
            this.idleSessions.Add(connection);
    }

    /// <summary>
    /// The database a fresh session of the server starts in, which a name
    /// omitting its database segment (<c>srv..dbo.t</c>) reads too.
    /// </summary>
    public string SessionDatabaseName => this.Catalog ?? Simulation.MasterDatabaseName;

    /// <summary>When <c>sp_addlinkedserver</c> ran, surfaced via <c>sys.servers.modify_date</c>.</summary>
    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// <c>sp_serveroption … 'rpc out'</c>: whether <c>EXEC … AT</c> and a
    /// four-part procedure call may reach the server (Msg 7411 otherwise).
    /// Seeded on for a <c>SQL Server</c> product only.
    /// </summary>
    public bool RpcOut = srvProduct.Equals("SQL Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>sp_serveroption … 'data access'</c>: whether four-part names and
    /// <c>OPENQUERY</c> may read or write through the server (Msg 7411
    /// otherwise).
    /// </summary>
    public bool DataAccess = true;

    /// <summary>
    /// <c>sp_serveroption … 'remote proc transaction promotion'</c>: whether a
    /// remote procedure call inside a local transaction enlists the server in
    /// a distributed transaction.
    /// </summary>
    public bool RemoteProcTransactionPromotion = true;

    /// <summary><c>sp_serveroption … 'rpc'</c>: <c>sys.servers.is_remote_login_enabled</c>, seeded on for a <c>SQL Server</c> product.</summary>
    public bool RemoteLogin = srvProduct.Equals("SQL Server", StringComparison.OrdinalIgnoreCase);

    // The sp_serveroption settings only the catalog reports (probed
    // 2026-10-05 against SQL Server 2025): sys.servers' columns of the same
    // meaning, at real's defaults.

    /// <summary><c>collation compatible</c>.</summary>
    public bool CollationCompatible;

    /// <summary><c>use remote collation</c>, on by default.</summary>
    public bool UseRemoteCollation = true;

    /// <summary><c>collation name</c>, NULL until set.</summary>
    public string? CollationName;

    /// <summary><c>lazy schema validation</c>.</summary>
    public bool LazySchemaValidation;

    /// <summary><c>system</c>, which can be turned on but not off.</summary>
    public bool IsSystem;

    /// <summary><c>pub</c>, <c>sub</c> and <c>dist</c>: the replication roles.</summary>
    public bool Publisher, Subscriber, Distributor;

    /// <summary><c>connect timeout</c> and <c>query timeout</c> in seconds, 0 for the server's default.</summary>
    public int ConnectTimeout, QueryTimeout;

    /// <summary>
    /// The login mappings <c>sys.linked_logins</c> lists: <c>sp_addlinkedserver</c>
    /// adds the self-mapping for every login (principal 0), and
    /// <c>sp_addlinkedsrvlogin</c> / <c>sp_droplinkedsrvlogin</c> replace and
    /// remove entries. A server whose list is empty refuses every access with
    /// Msg 7416.
    /// </summary>
    public readonly List<LinkedLogin> Logins = [new LinkedLogin(0, usesSelf: true, remoteName: null, createDate)];

    /// <summary>
    /// A loopback: the server names the <see cref="Simulation"/> that defines
    /// it. Real refuses a distributed transaction over a loopback with Msg 3910
    /// where a remote server's coordinator refuses it with Msg 7391.
    /// </summary>
    public bool IsLoopback(Simulation owner) => ReferenceEquals(this.Target, owner);

    /// <summary>
    /// The provider name real's linked-server messages quote: every SQL Server
    /// provider name reports as the OLE DB driver SQL Server 2025 loads for it
    /// (probed 2026-09-28 for <c>SQLNCLI</c>, <c>SQLNCLI11</c>,
    /// <c>MSOLEDBSQL</c> and <c>MSOLEDBSQL19</c>).
    /// </summary>
    public string ProviderInMessages =>
        this.Provider.StartsWith("SQLNCLI", StringComparison.OrdinalIgnoreCase) || this.Provider.StartsWith("MSOLEDBSQL", StringComparison.OrdinalIgnoreCase)
            ? "MSOLEDBSQL19"
            : this.Provider;
}

/// <summary>
/// One row of <c>sys.linked_logins</c>: the local login a mapping is for
/// (principal 0 for every login), whether it connects as itself, else the
/// remote login it connects as.
/// </summary>
internal sealed class LinkedLogin(int localPrincipalId, bool usesSelf, string? remoteName, DateTime modifyDate)
{
    public readonly int LocalPrincipalId = localPrincipalId;
    public readonly bool UsesSelf = usesSelf;
    public readonly string? RemoteName = remoteName;
    public readonly DateTime ModifyDate = modifyDate;
}

/// <summary>
/// A session <see cref="LinkedServer.OpenSession"/> leased from the
/// provider's pool, handed back when disposed.
/// </summary>
internal readonly struct RemoteSession(LinkedServer server, SimulatedDbConnection connection) : IDisposable
{
    public readonly SimulatedDbConnection Connection = connection;

    public void Dispose() => server.ReleaseSession(this.Connection);
}
