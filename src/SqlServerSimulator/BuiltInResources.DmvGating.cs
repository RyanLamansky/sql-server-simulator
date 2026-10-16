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
    // STATE. sys.dm_exec_connections needs the same permission as a server
    // DMV but refuses with Msg 371, naming the external policy action too.
    // sys.dm_os_host_info / sys.fn_helpcollations /
    // sys.dm_db_xtp_table_memory_stats are ungated (probe: readable by guest).
    private static readonly (string Key, DmvGateKind Kind)[] GatedDmvs =
    [
        ("sys.dm_db_file_space_usage", DmvGateKind.ServerState),
        ("sys.dm_db_partition_stats", DmvGateKind.DatabaseState),
        ("sys.dm_db_session_space_usage", DmvGateKind.ServerState),
        ("sys.dm_db_task_space_usage", DmvGateKind.ServerState),
        ("sys.dm_exec_cached_plans", DmvGateKind.ServerState),
        ("sys.dm_exec_connections", DmvGateKind.ServerStatePolicy),
        ("sys.dm_exec_function_stats", DmvGateKind.ServerState),
        ("sys.dm_exec_procedure_stats", DmvGateKind.ServerState),
        ("sys.dm_exec_query_memory_grants", DmvGateKind.ServerState),
        ("sys.dm_exec_query_optimizer_info", DmvGateKind.ServerState),
        ("sys.dm_exec_query_stats", DmvGateKind.ServerState),
        ("sys.dm_exec_requests", DmvGateKind.SessionSelfFilter),
        ("sys.dm_exec_sessions", DmvGateKind.SessionSelfFilter),
        ("sys.dm_exec_trigger_stats", DmvGateKind.ServerState),
        ("sys.dm_hadr_cluster", DmvGateKind.ServerState),
        ("sys.dm_hadr_database_replica_states", DmvGateKind.DatabaseState),
        ("sys.dm_database_encryption_keys", DmvGateKind.ServerSecurityState),
        ("sys.dm_os_process_memory", DmvGateKind.ServerState),
        ("sys.dm_os_sys_info", DmvGateKind.ServerState),
        ("sys.dm_os_server_diagnostics_log_configurations", DmvGateKind.ServerState),
        ("sys.dm_os_waiting_tasks", DmvGateKind.ServerState),
        ("sys.dm_tran_active_snapshot_database_transactions", DmvGateKind.ServerState),
        ("sys.dm_tran_active_transactions", DmvGateKind.ServerState),
        ("sys.dm_tran_current_transaction", DmvGateKind.ServerState),
        ("sys.dm_tran_locks", DmvGateKind.ServerState),
        ("sys.dm_tran_persistent_version_store_stats", DmvGateKind.DatabaseState),
        ("sys.dm_tran_session_transactions", DmvGateKind.ServerState),
        ("sys.dm_tran_version_store", DmvGateKind.ServerState),
        ("sys.dm_tran_version_store_space_usage", DmvGateKind.ServerState),
        ("sys.sysaltfiles", DmvGateKind.ControlServer),
        ("sys.sysprocesses", DmvGateKind.SessionSelfFilter),
        ("sysaltfiles", DmvGateKind.ControlServer),
        ("sysprocesses", DmvGateKind.SessionSelfFilter),
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
                RejectUntrustedImpersonation(batch);
                if (!simulation.HoldsServerPermission(login, Permission.ViewServerPerformanceState))
                    throw ServerStateDenied("VIEW SERVER PERFORMANCE STATE");
                return rows;
            case DmvGateKind.ServerSecurityState:
                RejectUntrustedImpersonation(batch);
                if (!simulation.HoldsServerPermission(login, Permission.ViewServerSecurityState))
                    throw ServerStateDenied("VIEW SERVER SECURITY STATE");
                return rows;
            case DmvGateKind.ServerStatePolicy:
                if (!simulation.HoldsServerPermission(login, Permission.ViewServerPerformanceState))
                    throw SimulatedSqlException.ServerStatePolicyDenied();
                return rows;
            case DmvGateKind.DatabaseState:
                if (!HoldsDatabaseState(batch, simulation, login))
                {
                    throw SimulatedSqlException.Aggregate(
                        [SimulatedSqlException.DatabasePermissionDenied("VIEW DATABASE PERFORMANCE STATE", databaseName, aborts: false), SimulatedSqlException.UserLacksPermissionToPerformAction()]);
                }
                return rows;
            // Real reads sysaltfiles through its resource database, where only
            // CONTROL SERVER holds SELECT (probed 2026-10-15 against SQL
            // Server 2025: VIEW SERVER STATE, VIEW ANY DEFINITION and ALTER
            // ANY DATABASE don't).
            case DmvGateKind.ControlServer:
                if (!simulation.HoldsServerPermission(login, Permission.ControlServer))
                    throw SimulatedSqlException.PermissionDenied("SELECT", view.Name, "mssqlsystemresource", "sys");
                return rows;
            default: // SessionSelfFilter
                return simulation.HoldsServerPermission(login, Permission.ViewServerState)
                    ? rows
                    : FilterToOwnSession(batch, rows);
        }
    }

    /// <summary>
    /// The server-state gate for a system TVF, which has no catalog-view
    /// descriptor to hang it on: a restricted session without
    /// <c>VIEW SERVER PERFORMANCE STATE</c> raises Msg 371 when
    /// <paramref name="policyWording"/> is set and Msg 300 otherwise.
    /// </summary>
    internal static void DemandServerPerformanceState(BatchContext batch, bool policyWording)
    {
        var security = batch.Connection.Security;
        if (security.EffectiveIsDbo)
            return;
        if (!policyWording)
            RejectUntrustedImpersonation(batch);
        if (batch.Connection.Simulation.HoldsServerPermission(security.Effective.LoginName, Permission.ViewServerPerformanceState))
            return;
        throw policyWording ? SimulatedSqlException.ServerStatePolicyDenied() : ServerStateDenied("VIEW SERVER PERFORMANCE STATE");
    }

    /// <summary>Msg 300, naming <c>master</c>, and the Msg 297 real sends after it.</summary>
    private static SimulatedSqlException ServerStateDenied(string permission) =>
        SimulatedSqlException.Aggregate([SimulatedSqlException.ServerStatePermissionDenied(permission, "master"), SimulatedSqlException.UserLacksPermissionToPerformAction()]);

    /// <summary>
    /// Msg 15562 for an <c>EXECUTE AS USER</c> context reading server state
    /// from a database that isn't <c>TRUSTWORTHY</c>, ahead of any permission
    /// check: real refuses before asking what the user's login holds (probed
    /// 2026-10-15 against SQL Server 2025). The policy-worded gates (Msg 371)
    /// ask the permission alone.
    /// </summary>
    private static void RejectUntrustedImpersonation(BatchContext batch)
    {
        if (batch.Connection.Security.Effective.IsDatabaseScoped && !batch.CurrentDatabase.Trustworthy)
            throw SimulatedSqlException.ModuleNotTrusted();
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

    // sys.dm_exec_sessions' and sys.dm_exec_requests' column 0 is session_id
    // (smallint); a restricted session without VIEW SERVER STATE sees only
    // its own SPID's row.
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
