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
/// Reads (<c>SELECT</c>, <c>JOIN</c>, catalog views) traversing a
/// <c>linkedserver.db.schema.t</c> reference open a fresh
/// <see cref="SimulatedDbConnection"/> on <see cref="Target"/> and execute
/// the remote portion as a <c>SELECT * FROM [db].[schema].[t]</c>
/// statement through the remote's full parser / planner / lock-manager
/// pipeline — matching real SQL Server's remote-query semantics for the
/// modeled subset.
/// </para>
/// <para>
/// Writes through a four-part name (<c>INSERT</c>, <c>UPDATE</c>,
/// <c>DELETE</c>, <c>MERGE</c> targeting <c>linkedserver.db.schema.t</c>)
/// raise <see cref="NotSupportedException"/> at parse time. Lock-
/// manager and undo-log coordination across <see cref="Simulation"/>
/// boundaries isn't modeled; this mirrors the existing
/// <see cref="System.Data.IsolationLevel"/>-related deferral for
/// <c>BEGIN DISTRIBUTED TRANSACTION</c>.
/// </para>
/// </remarks>
internal sealed class LinkedServer(string name, Simulation target, string srvProduct, string provider, string? dataSource)
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
}
