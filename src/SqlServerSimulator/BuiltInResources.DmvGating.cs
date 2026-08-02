using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    // DMVs a restricted session may read only with the matching VIEW …STATE
    // permission (probe-confirmed SQL Server 2025, 2026-07-21). Server-scope DMVs
    // raise Msg 300 (VIEW SERVER PERFORMANCE STATE, covered by VIEW SERVER STATE);
    // database-scope DMVs raise Msg 262 (VIEW DATABASE PERFORMANCE STATE, covered
    // by VIEW DATABASE STATE at db scope or a server VIEW …STATE cross-scope);
    // sys.dm_exec_sessions self-filters to the own session without VIEW SERVER
    // STATE. sys.dm_os_host_info / sys.fn_helpcollations /
    // sys.dm_db_xtp_table_memory_stats are ungated (probe: readable by guest).
    private static readonly (string Key, DmvGateKind Kind)[] GatedDmvs =
    [
        ("sys.dm_db_partition_stats", DmvGateKind.DatabaseState),
        ("sys.dm_exec_sessions", DmvGateKind.SessionSelfFilter),
        ("sys.dm_hadr_cluster", DmvGateKind.ServerState),
        ("sys.dm_hadr_database_replica_states", DmvGateKind.DatabaseState),
        ("sys.dm_os_waiting_tasks", DmvGateKind.ServerState),
        ("sys.dm_tran_active_snapshot_database_transactions", DmvGateKind.ServerState),
        ("sys.dm_tran_locks", DmvGateKind.ServerState),
        ("sys.dm_tran_version_store", DmvGateKind.ServerState),
        ("sys.dm_tran_version_store_space_usage", DmvGateKind.ServerState),
    ];

    /// <summary>
    /// Stamps each gated DMV with its server-state gate kind. Called once at
    /// catalog-view build time; a view not listed keeps <c>DmvGate == null</c> and
    /// is readable by everyone.
    /// </summary>
    private static void ApplyDmvGating(Dictionary<string, CatalogView> views)
    {
        foreach (var (key, kind) in GatedDmvs)
        {
            if (views.TryGetValue(key, out var view))
                view.DmvGate = kind;
        }
    }

    /// <summary>
    /// Enforces a gated DMV's server-state permission for a restricted session.
    /// Returns <paramref name="rows"/> untouched — zero added cost — for a
    /// <c>dbo</c> / sysadmin session (the overwhelming common case) or an ungated
    /// view. A server / database DMV throws Msg 300 / 262 eagerly on denial; a
    /// permitted read (or <c>sys.dm_exec_sessions</c> without VIEW SERVER STATE,
    /// self-filtered to the own session) returns the row sequence.
    /// </summary>
    internal static IEnumerable<SqlValue[]> ApplyDmvGate(CatalogView view, BatchContext batch, IEnumerable<SqlValue[]> rows)
    {
        if (view.DmvGate is not { } kind || batch.Connection.Security.EffectiveIsDbo)
            return rows;

        var simulation = batch.Connection.Simulation;
        var login = batch.Connection.Security.Effective.LoginName;
        var databaseName = batch.CurrentDatabase.Name;
        switch (kind)
        {
            case DmvGateKind.ServerState:
                if (!simulation.HoldsServerPermission(login, Permission.ViewServerPerformanceState))
                    throw SimulatedSqlException.ServerStatePermissionDenied("VIEW SERVER PERFORMANCE STATE", databaseName);
                return rows;
            case DmvGateKind.DatabaseState:
                if (!HoldsDatabaseState(batch, simulation, login))
                    throw SimulatedSqlException.DatabasePermissionDenied("VIEW DATABASE PERFORMANCE STATE", databaseName);
                return rows;
            default: // SessionSelfFilter
                return simulation.HoldsServerPermission(login, Permission.ViewServerState)
                    ? rows
                    : FilterToOwnSession(batch, rows);
        }
    }

    // A database VIEW DATABASE PERFORMANCE STATE requirement is met by that
    // permission (or VIEW DATABASE STATE, which covers it) at database scope, OR
    // by a covering server permission (VIEW SERVER PERFORMANCE STATE, itself
    // covered by VIEW SERVER STATE) — probe: a server VIEW SERVER STATE grant
    // opens sys.dm_db_partition_stats cross-scope.
    private static bool HoldsDatabaseState(BatchContext batch, Simulation simulation, string login) =>
        PermissionChecker.IsGranted(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId,
            Permission.ViewDatabasePerformanceState, PermissionChecker.ClassDatabase, 0, 0)
        || simulation.HoldsServerPermission(login, Permission.ViewServerPerformanceState);

    // sys.dm_exec_sessions column 0 is session_id (smallint); a restricted
    // session without VIEW SERVER STATE sees only its own SPID's row.
    private static IEnumerable<SqlValue[]> FilterToOwnSession(BatchContext batch, IEnumerable<SqlValue[]> rows)
    {
        var spid = (short)batch.Connection.Spid;
        foreach (var row in rows)
        {
            var idCell = row[0];
            if (!idCell.IsNull && idCell.AsInt16 == spid)
                yield return row;
        }
    }
}
