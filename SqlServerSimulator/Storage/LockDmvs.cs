using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Row generators for the <c>sys.dm_tran_locks</c> and
/// <c>sys.dm_os_waiting_tasks</c> dynamic management views — phase-2
/// observability surface for the lock manager's state. Both views
/// project from live <see cref="LockManager"/> state at iteration time:
/// every successful Acquire appends a hold; every wait sets
/// <see cref="SimulatedDbConnection.WaitingOnResource"/> + <c>WaitingForMode</c>.
/// Neither DMV takes the manager's gate during enumeration — concurrent
/// acquires / releases may shift the result between rows, but the
/// per-resource snapshot stays consistent because <see cref="LockResource.Holders"/>
/// is read field-by-field and a torn row is not possible.
/// </summary>
internal static class LockDmvs
{
    /// <summary>
    /// Maps a <see cref="LockMode"/> to the wire-level abbreviation real
    /// SQL Server reports (<c>S</c>, <c>X</c>, <c>U</c>, <c>IS</c>,
    /// <c>IX</c>, <c>SIX</c>, <c>Sch-S</c>, <c>Sch-M</c>).
    /// </summary>
    internal static string ModeAbbreviation(LockMode mode) => mode switch
    {
        LockMode.SchemaStability => "Sch-S",
        LockMode.SchemaModification => "Sch-M",
        LockMode.IntentShared => "IS",
        LockMode.IntentExclusive => "IX",
        LockMode.SharedIntentExclusive => "SIX",
        LockMode.Shared => "S",
        LockMode.Update => "U",
        LockMode.Exclusive => "X",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Yields one row per granted or waiting lock across every schema
    /// object + every per-row LockResource in the simulator. Walks
    /// schemas / heap tables / views / functions / procedures / sequences
    /// / table types / triggers; for each holder appends a <c>GRANT</c>
    /// row, for each waiter (resolved via the connection registry's
    /// <see cref="SimulatedDbConnection.WaitingOnResource"/>) appends a
    /// <c>WAIT</c> row.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranLocks(BatchContext batch, Database database)
    {
        var sim = batch.Connection.Simulation;
        var dbId = SqlValue.FromInt32(1);
        var grantStatus = SqlValue.FromNVarchar("GRANT");
        var waitStatus = SqlValue.FromNVarchar("WAIT");
        var objectType = SqlValue.FromNVarchar("OBJECT");
        var ridType = SqlValue.FromNVarchar("RID");

        var waitsByResource = SnapshotWaiters(sim);

        foreach (var schema in database.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, t.Name, t.ObjectId, t.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
                foreach (var row in EmitRowsForResource(objectType, dbId, t.Name, t.ObjectId, t.TableDataLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
                foreach (var kv in t.RowLocks)
                {
                    var desc = $"{kv.Key.PageIndex}:{kv.Key.SlotIndex}";
                    foreach (var row in EmitRowsForResource(ridType, dbId, desc, t.ObjectId, kv.Value, waitsByResource, grantStatus, waitStatus))
                        yield return row;
                }
            }
            foreach (var v in schema.Views.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, v.Name, v.ObjectId, v.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var f in schema.Functions.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, f.Name, f.ObjectId, f.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var p in schema.Procedures.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, p.Name, p.ObjectId, p.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var s in schema.Sequences.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, s.Name, s.ObjectId, s.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var tt in schema.TableTypes.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, tt.Name, tt.ObjectId, tt.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var tr in schema.Triggers.Values)
            {
                foreach (var row in EmitRowsForResource(objectType, dbId, tr.Name, tr.ObjectId, tr.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
        }
    }

    /// <summary>
    /// Yields one row per currently-waiting connection.
    /// <c>blocking_session_id</c> picks one of the conflicting holders'
    /// SPIDs (real SQL Server may show multiple rows when many holders
    /// block one waiter; the simulator collapses to one for clarity).
    /// <c>wait_type</c> is <c>LCK_M_&lt;mode&gt;</c> matching SQL Server's
    /// convention.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmOsWaitingTasks(BatchContext batch, Database database)
    {
        _ = database;
        var sim = batch.Connection.Simulation;
        foreach (var conn in sim.SnapshotConnections())
        {
            if (conn.WaitingOnResource is not { } resource || conn.WaitingForMode is not { } mode)
                continue;
            var blockerSpid = FindFirstBlocker(resource, conn);
            yield return new SqlValue[]
            {
                SqlValue.FromInt16((short)conn.Spid),
                SqlValue.FromNVarchar($"LCK_M_{ModeAbbreviation(mode).Replace("-", "_", StringComparison.Ordinal)}"),
                SqlValue.FromNVarchar(DescribeResource(sim, resource)),
                blockerSpid is int bSpid ? SqlValue.FromInt16((short)bSpid) : SqlValue.Null(SqlType.SmallInt),
            };
        }
    }

    /// <summary>
    /// Reverse-lookup map: for each connection currently waiting, find
    /// the resource it's blocked on. Used by
    /// <see cref="EnumerateDmTranLocks"/> to emit WAIT rows alongside the
    /// GRANT rows for that same resource.
    /// </summary>
    private static Dictionary<LockResource, List<SimulatedDbConnection>> SnapshotWaiters(Simulation sim)
    {
        var map = new Dictionary<LockResource, List<SimulatedDbConnection>>(ReferenceEqualityComparer.Instance);
        foreach (var conn in sim.SnapshotConnections())
        {
            if (conn.WaitingOnResource is not { } resource)
                continue;
            if (!map.TryGetValue(resource, out var list))
                map[resource] = list = [];
            list.Add(conn);
        }
        return map;
    }

    private static IEnumerable<SqlValue[]> EmitRowsForResource(
        SqlValue typeVal,
        SqlValue dbIdVal,
        string description,
        int entityId,
        LockResource resource,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitersByResource,
        SqlValue grantStatus,
        SqlValue waitStatus)
    {
        // Empty-resource fast path: nothing held or waiting → no rows.
        if (resource.Holders.Count == 0 && !waitersByResource.ContainsKey(resource))
            yield break;
        var descVal = SqlValue.FromNVarchar(description);
        var entityVal = SqlValue.FromInt64(entityId);
        // GRANT rows from current holders.
        foreach (var hold in resource.Holders)
        {
            yield return new SqlValue[]
            {
                typeVal,
                dbIdVal,
                descVal,
                entityVal,
                SqlValue.FromNVarchar(ModeAbbreviation(hold.Mode)),
                grantStatus,
                SqlValue.FromInt32(hold.Owner.Spid),
            };
        }
        // WAIT rows from connections blocked on this resource.
        if (waitersByResource.TryGetValue(resource, out var waiters))
        {
            foreach (var waiter in waiters)
            {
                if (waiter.WaitingForMode is not { } waitMode)
                    continue;
                yield return new SqlValue[]
                {
                    typeVal,
                    dbIdVal,
                    descVal,
                    entityVal,
                    SqlValue.FromNVarchar(ModeAbbreviation(waitMode)),
                    waitStatus,
                    SqlValue.FromInt32(waiter.Spid),
                };
            }
        }
    }

    /// <summary>
    /// Returns the SPID of one connection currently holding
    /// <paramref name="resource"/> with a mode incompatible with
    /// <paramref name="waiter"/>'s wait — that's the blocker
    /// <c>sys.dm_os_waiting_tasks</c> attributes the wait to. Returns
    /// <c>null</c> when nothing blocks (a race against grant — the
    /// waiter's about to unblock).
    /// </summary>
    private static int? FindFirstBlocker(LockResource resource, SimulatedDbConnection waiter)
    {
        if (waiter.WaitingForMode is not { } mode)
            return null;
        foreach (var hold in resource.Holders)
        {
            if (ReferenceEquals(hold.Owner, waiter))
                continue;
            if (LockManager.IsCompatible(hold.Mode, mode))
                continue;
            return hold.Owner.Spid;
        }
        return null;
    }

    /// <summary>
    /// Walks all schemas + heap tables to find the object / RID associated
    /// with <paramref name="resource"/>; falls back to a generic
    /// description when no association matches (rare — the resource is
    /// usually a SchemaLock or a HeapTable's row-lock dict entry).
    /// </summary>
    private static string DescribeResource(Simulation sim, LockResource resource)
    {
        foreach (var db in sim.Databases.Values)
        {
            foreach (var schema in db.Schemas.Values)
            {
                foreach (var t in schema.HeapTables.Values)
                {
                    if (ReferenceEquals(t.SchemaLock, resource))
                        return $"OBJECT: {t.Name}";
                    if (ReferenceEquals(t.TableDataLock, resource))
                        return $"OBJECT (data): {t.Name}";
                    foreach (var kv in t.RowLocks)
                    {
                        if (ReferenceEquals(kv.Value, resource))
                            return $"RID: {t.Name} {kv.Key.PageIndex}:{kv.Key.SlotIndex}";
                    }
                }
            }
        }
        return "(unknown)";
    }
}
