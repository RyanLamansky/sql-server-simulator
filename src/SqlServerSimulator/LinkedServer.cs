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
/// <c>linkedserver.db.schema.t</c> reference open a fresh
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
    /// Opens a fresh session of the server in <paramref name="database"/>, or
    /// where the provider's session starts when none is named
    /// (<see cref="SessionDatabaseName"/>): the
    /// <c>@catalog</c> database, else the login's default database —
    /// <c>master</c>, as real's <c>sa</c> login has it (probed 2026-09-28
    /// against SQL Server 2025, where <c>DB_NAME()</c> inside <c>EXEC … AT</c>
    /// and <c>OPENQUERY</c> reads the catalog when one is set).
    /// </summary>
    public SimulatedDbConnection OpenSession(string? database)
    {
        var connection = this.Target.CreateDbConnection();
        connection.Open();
        connection.ChangeDatabase(database ?? this.SessionDatabaseName);
        return connection;
    }

    /// <summary>
    /// The database a fresh session of the server starts in, which a name
    /// omitting its database segment (<c>srv..dbo.t</c>) reads too.
    /// </summary>
    public string SessionDatabaseName => this.Catalog ?? Simulation.MasterDatabaseName;

    /// <summary>When <c>sp_addlinkedserver</c> ran, surfaced via <c>sys.servers.modify_date</c>.</summary>
    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// A <c>SQL Server</c> product: real enables remote login and RPC out for
    /// it, where another product gets neither (probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    public bool IsSqlServerProduct => string.Equals(this.SrvProduct, "SQL Server", StringComparison.OrdinalIgnoreCase);

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
