using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Row generators for the <c>sys.dm_tran_locks</c> and
/// <c>sys.dm_os_waiting_tasks</c> dynamic management views — phase-2
/// observability surface for the lock manager's state. Both views
/// project from live <see cref="LockManager"/> state at iteration time:
/// every successful Acquire appends a hold; every wait sets
/// <see cref="SimulatedDbConnection.WaitingOnResource"/> + <c>WaitingForMode</c>.
/// Neither DMV holds the manager's gate across the enumeration — concurrent
/// acquires / releases may shift the result between rows — but each
/// resource's holders are copied under it (<see cref="LockManager.HoldersOf"/>),
/// since a list another session appends to mid-copy can throw or hand back a
/// half-written hold. That a blocked
/// session appears at all rests on the acquirer keeping its registration set
/// for the whole wait rather than per wait slice, so a waiter re-checking its
/// conflict can't read as idle here.
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
        LockMode.RangeSharedShared => "RangeS-S",
        LockMode.RangeSharedUpdate => "RangeS-U",
        LockMode.RangeExclusiveExclusive => "RangeX-X",
        LockMode.RangeInsertNull => "RangeI-N",
        _ => mode.ToString(),
    };

    /// <summary>
    /// The <c>wait_type</c> real reports for a lock wait in
    /// <paramref name="mode"/>: <c>LCK_M_</c> and the mode, the schema and
    /// range modes in real's own abbreviations (probed 2026-10-01 against
    /// SQL Server 2025: <c>LCK_M_SCH_M</c>, <c>LCK_M_RS_S</c>,
    /// <c>LCK_M_RS_U</c>, <c>LCK_M_RIn_NL</c>).
    /// </summary>
    internal static string WaitType(LockMode mode) => mode switch
    {
        LockMode.SchemaStability => "LCK_M_SCH_S",
        LockMode.SchemaModification => "LCK_M_SCH_M",
        LockMode.RangeSharedShared => "LCK_M_RS_S",
        LockMode.RangeSharedUpdate => "LCK_M_RS_U",
        LockMode.RangeExclusiveExclusive => "LCK_M_RX_X",
        LockMode.RangeInsertNull => "LCK_M_RIn_NL",
        _ => "LCK_M_" + ModeAbbreviation(mode),
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
        var locks = sim.LockManager;
        var dbId = SqlValue.FromInt32(database.Id);
        // Real describes an OBJECT resource by 256 spaces, not by name — the
        // object is resource_associated_entity_id (probed 2026-09-30 against
        // SQL Server 2025).
        var objectDescription = new string(' ', 256);
        var grantStatus = SqlValue.FromNVarchar("GRANT");
        var waitStatus = SqlValue.FromNVarchar("WAIT");
        var objectType = SqlValue.FromNVarchar("OBJECT");
        var ridType = SqlValue.FromNVarchar("RID");
        // Real reports a key lock as resource_type KEY with a hash of the
        // anchoring index key; the simulator prints the key itself (see
        // KeyLockGroup.Describe), so the type matches and the description
        // doesn't, save the infinity anchor's.
        var keyType = SqlValue.FromNVarchar("KEY");

        var waitsByResource = SnapshotWaiters(sim, out var keyWaits);

        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, t) in schema.HeapTables)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, t.ObjectId, t.SchemaLock, waitsByResource, grantStatus, waitStatus, SchemaLocksBesideDataLock(locks, t)))
                    yield return row;
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, t.ObjectId, t.TableDataLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
                var folded = FoldRowLocksIntoKeyLocks(locks, t);
                foreach (var row in EmitRowLocks(batch, t, ridType, keyType, dbId, waitsByResource, keyWaits, grantStatus, waitStatus, folded))
                    yield return row;
                foreach (var (_, group) in t.KeyLockGroups)
                {
                    foreach (var row in EmitRowsForResource(locks, keyType, dbId, KeyLockGroup.Describe(null), t.ObjectId, group.Infinity, waitsByResource, grantStatus, waitStatus, folded))
                        yield return row;
                    foreach (var kv in group.Anchors)
                    {
                        foreach (var row in EmitRowsForResource(locks, keyType, dbId, KeyLockGroup.Describe(kv.Key), t.ObjectId, kv.Value, waitsByResource, grantStatus, waitStatus, folded))
                            yield return row;
                    }
                }
            }
            foreach (var (_, v) in schema.Views)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, v.ObjectId, v.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var (_, f) in schema.Functions)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, f.ObjectId, f.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var (_, p) in schema.Procedures)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, p.ObjectId, p.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var (_, s) in schema.Sequences)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, s.ObjectId, s.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var (_, tt) in schema.TableTypes)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, tt.ObjectId, tt.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
            foreach (var (_, tr) in schema.Triggers)
            {
                foreach (var row in EmitRowsForResource(locks, objectType, dbId, objectDescription, tr.ObjectId, tr.SchemaLock, waitsByResource, grantStatus, waitStatus))
                    yield return row;
            }
        }

        // Application locks (sp_getapplock family). resource_description
        // follows the probe-confirmed shape `<principal-id>:[<name>]:(<hash>)`;
        // the 8-hex hash is FNV-1a over the name here, so it won't byte-match
        // real SQL Server's undocumented hash — the id and bracketed name do.
        var applicationType = SqlValue.FromNVarchar("APPLICATION");
        List<((int PrincipalId, string Resource) Key, LockResource Resource)> appLocks;
        lock (database.ApplicationLocks)
        {
            appLocks = new(database.ApplicationLocks.Count);
            foreach (var kv in database.ApplicationLocks)
                appLocks.Add((kv.Key, kv.Value));
        }

        foreach (var (key, resource) in appLocks)
        {
            var description = $"{key.PrincipalId}:[{key.Resource}]:({Fnv1a32(key.Resource):x8})";
            foreach (var row in EmitRowsForResource(locks, applicationType, dbId, description, entityId: 0, resource, waitsByResource, grantStatus, waitStatus))
                yield return row;
        }
    }

    // 32-bit FNV-1a over the resource name's UTF-16 code units, for the
    // hash slot of an APPLICATION resource_description.
    private static uint Fnv1a32(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
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
                SqlValue.FromNVarchar(WaitType(mode)),
                SqlValue.FromNVarchar(conn.Session.WaitingOnKey is { } key && resource.OwningTable is { } keyed ? $"KEY: {keyed.Name} {key}" : DescribeResource(sim, resource)),
                blockerSpid is int bSpid ? SqlValue.FromInt16((short)bSpid) : SqlValue.Null(SqlType.SmallInt),
            };
        }
    }

    /// <summary>
    /// Reverse-lookup map: for each connection currently waiting, find
    /// the resource it's blocked on. Used by
    /// <see cref="EnumerateDmTranLocks"/> to emit WAIT rows alongside the
    /// GRANT rows for that same resource. A wait for a key another row
    /// carries (<see cref="SessionToken.WaitingOnKey"/>) goes to
    /// <paramref name="keyWaits"/> instead, reported on the key.
    /// </summary>
    private static Dictionary<LockResource, List<SimulatedDbConnection>> SnapshotWaiters(Simulation sim, out List<(SimulatedDbConnection Waiter, LockResource Resource, string Key)>? keyWaits)
    {
        var map = new Dictionary<LockResource, List<SimulatedDbConnection>>(ReferenceEqualityComparer.Instance);
        keyWaits = null;
        foreach (var conn in sim.SnapshotConnections())
        {
            if (conn.WaitingOnResource is not { } resource)
                continue;
            if (conn.Session.WaitingOnKey is { } key)
            {
                (keyWaits ??= []).Add((conn, resource, key));
                continue;
            }
            if (!map.TryGetValue(resource, out var list))
                map[resource] = list = [];
            list.Add(conn);
        }
        return map;
    }

    /// <summary>
    /// The row locks of <paramref name="table"/> — a clustered table's as
    /// <c>KEY</c> resources described by the clustered key, a heap's as
    /// <c>RID</c> — and the index key locks real takes beside a written row,
    /// which the simulator folds into the row's X:
    /// <list type="bullet">
    /// <item>an inserted row's key in every index it entered;</item>
    /// <item>a deleted row's key in every index it left;</item>
    /// <item>an updated row's old and new key in every index whose row the
    /// update changed — key, <c>INCLUDE</c> or clustered-key columns — the
    /// clustered key included when it moved;</item>
    /// <item>a filtered index only for the images its filter admits.</item>
    /// </list>
    /// Probed 2026-10-01 against SQL Server 2025 over heaps and clustered
    /// tables with unique, non-unique, filtered and <c>INCLUDE</c> indexes.
    /// A locking read's X locks only its row. A row a session deleted left
    /// the row-lock dictionary with its slot and is found through
    /// <see cref="HeapTable.SupersededKeyImages"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EmitRowLocks(
        BatchContext batch,
        HeapTable table,
        SqlValue ridType,
        SqlValue keyType,
        SqlValue dbIdVal,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitersByResource,
        List<(SimulatedDbConnection Waiter, LockResource Resource, string Key)>? keyWaits,
        SqlValue grantStatus,
        SqlValue waitStatus,
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded)
    {
        // A row of a clustered table is its clustered key, which is what real
        // locks and reports as KEY; only a heap's row is a RID.
        var locks = batch.Connection.Simulation.LockManager;
        var rowGroup = KeyLockGroup.RowGroupOf(table);
        var rowType = rowGroup is null ? ridType : keyType;
        var rows = new List<((int PageIndex, int SlotIndex) Address, LockResource Lock)>();
        foreach (var kv in table.RowLocks)
            rows.Add((kv.Key, kv.Value));
        if (!table.SupersededKeyImages.IsEmptyLockFree())
        {
            HashSet<LockResource>? seen = null;
            foreach (var (_, images) in table.SupersededKeyImages)
            {
                foreach (var (address, (_, resource)) in images)
                {
                    if (!(table.RowLocks.TryGetValue(address, out var live) && ReferenceEquals(live, resource))
                        && (seen ??= new(ReferenceEqualityComparer.Instance)).Add(resource))
                    {
                        rows.Add((address, resource));
                    }
                }
            }
        }

        foreach (var (address, resource) in rows)
        {
            var holders = locks.HoldersOf(resource);
            if (holders.Length == 0 && !waitersByResource.ContainsKey(resource))
                continue;
            var live = table.Heap.ReadLiveRow(address.PageIndex, address.SlotIndex);
            var description = rowGroup is not null && (live ?? PreImageOf(table, address, holders)) is { } image
                ? DescribeImageKey(table, rowGroup, image)
                : $"{address.PageIndex}:{address.SlotIndex}";
            foreach (var row in EmitRowsForResource(locks, rowType, dbIdVal, description, table.ObjectId, resource, waitersByResource, grantStatus, waitStatus, folded))
                yield return row;
            foreach (var hold in holders)
            {
                if (hold.Mode != LockMode.Exclusive)
                    continue;
                var spid = SqlValue.FromInt32(hold.Owner.Spid);
                foreach (var key in IndexKeysWritten(batch, table, rowGroup, address, resource, hold.Owner, live))
                {
                    yield return
                    [
                        keyType,
                        dbIdVal,
                        SqlValue.FromNVarchar(key),
                        SqlValue.FromInt64(table.ObjectId),
                        SqlValue.FromNVarchar(ModeAbbreviation(LockMode.Exclusive)),
                        grantStatus,
                        spid,
                    ];
                }
            }
        }

        if (keyWaits is null)
            yield break;
        foreach (var (waiter, resource, key) in keyWaits)
        {
            if (!ReferenceEquals(resource.OwningTable, table) || waiter.WaitingForMode is not { } waitMode)
                continue;
            yield return
            [
                keyType,
                dbIdVal,
                SqlValue.FromNVarchar(key),
                SqlValue.FromInt64(table.ObjectId),
                SqlValue.FromNVarchar(ModeAbbreviation(waitMode)),
                waitStatus,
                SqlValue.FromInt32(waiter.Spid),
            ];
        }
    }

    // The image a holder of the row's lock superseded, for a row whose slot no
    // longer holds a live one.
    private static byte[]? PreImageOf(HeapTable table, (int PageIndex, int SlotIndex) address, LockResource.Hold[] holders)
    {
        foreach (var hold in holders)
        {
            if (table.SupersededKeyImages.TryGetValue(hold.Owner, out var images) && images.TryGetValue(address, out var entry))
                return entry.Image;
        }
        return null;
    }

    /// <summary>
    /// The index keys <paramref name="owner"/>'s write of the row at
    /// <paramref name="address"/> locks beside the row itself, described as
    /// <see cref="KeyLockGroup.Describe"/> describes an anchor (see
    /// <see cref="EmitRowLocks"/>). A key the session holds a key lock on
    /// already is reported by that lock's own row.
    /// </summary>
    private static List<string> IndexKeysWritten(
        BatchContext batch, HeapTable table, KeyLockGroup? rowGroup, (int PageIndex, int SlotIndex) address, LockResource resource, SessionToken owner, byte[]? live)
    {
        var keys = new List<string>();
        var locks = batch.Connection.Simulation.LockManager;
        var pre = table.SupersededKeyImages.TryGetValue(owner, out var images) && images.TryGetValue(address, out var entry) ? entry.Image : null;
        var inserted = ReferenceEquals(resource.InsertedBy, owner);
        if (pre is null && !inserted)
            return keys;

        // A moved clustered key leaves its old key locked beside the new one,
        // which the row lock itself reports.
        if (rowGroup is not null && pre is not null && live is not null
            && DescribeImageKey(table, rowGroup, pre) is var oldKey && oldKey != DescribeImageKey(table, rowGroup, live))
        {
            AddKey(rowGroup, pre, oldKey, filter: null);
        }

        foreach (var constraint in table.KeyConstraints)
        {
            if (!constraint.IsDisabled && !constraint.IsClustered)
                AddIndexKeys(constraint, filter: null);
        }
        foreach (var index in table.Indexes)
        {
            if (!index.IsDisabled && !index.IsClustered)
                AddIndexKeys(index, index.Filter);
        }
        return keys;

        void AddIndexKeys(object indexOwner, Parser.BooleanExpression? filter)
        {
            if (KeyLockGroup.For(table, indexOwner) is not { } group)
                return;
            var changed = pre is not null && live is not null && group.RowChanges(pre, live);
            if (live is not null && (inserted || changed))
                AddKey(group, live, DescribeImageKey(table, group, live), filter);
            if (pre is not null && (live is null || changed))
                AddKey(group, pre, DescribeImageKey(table, group, pre), filter);
        }

        void AddKey(KeyLockGroup group, byte[] image, string description, Parser.BooleanExpression? filter)
        {
            if (filter is not null && Simulation.EvaluateIndexFilter(filter, table, Simulation.DecodeFullRow(table, image), batch) != true)
                return;
            if (group.TryReadKey(image, out var anchorKey) && group.Find(anchorKey) is { } anchor)
            {
                foreach (var hold in locks.HoldersOf(anchor))
                {
                    if (ReferenceEquals(hold.Owner, owner))
                        return;
                }
            }
            if (!keys.Contains(description))
                keys.Add(description);
        }
    }

    // An image's key tuple in group, NULL components included, as an anchor
    // on it is described.
    private static string DescribeImageKey(HeapTable table, KeyLockGroup group, byte[] image)
    {
        var components = new SqlValue[group.Ordinals.Length];
        for (var i = 0; i < components.Length; i++)
            components[i] = RowDecoder.DecodeColumn(table.StoredColumns, image, group.Ordinals[i], table.Heap);
        return KeyLockGroup.Describe(new Parser.SqlValueKey(components));
    }

    private static IEnumerable<SqlValue[]> EmitRowsForResource(
        LockManager locks,
        SqlValue typeVal,
        SqlValue dbIdVal,
        string description,
        int entityId,
        LockResource resource,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitersByResource,
        SqlValue grantStatus,
        SqlValue waitStatus,
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded = null)
    {
        // Empty-resource fast path: nothing held or waiting → no rows.
        if (resource.Holders.Count == 0 && !waitersByResource.ContainsKey(resource))
            yield break;
        var descVal = SqlValue.FromNVarchar(description);
        var entityVal = SqlValue.FromInt64(entityId);
        // GRANT rows from current holders. A U its holder has since taken X
        // over is real's lock converted, reported as the X alone.
        foreach (var hold in locks.HoldersOf(resource))
        {
            var mode = hold.Mode;
            if (mode == LockMode.Update && HoldsExclusive(locks, resource, hold.Owner))
                continue;
            if (folded is not null && folded.TryGetValue((resource, hold.Owner, mode), out var reported))
            {
                if (reported is not { } shown)
                    continue;
                mode = shown;
            }
            yield return new SqlValue[]
            {
                typeVal,
                dbIdVal,
                descVal,
                entityVal,
                SqlValue.FromNVarchar(ModeAbbreviation(mode)),
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

    // Real's object lock is one resource; the simulator's two meet in a
    // statement redefining the table (BatchContext.AcquireTableRedefinitionLock),
    // whose Sch-M then shows once, on the data lock that outlives the statement.
    private static Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? SchemaLocksBesideDataLock(LockManager locks, HeapTable table)
    {
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded = null;
        foreach (var hold in locks.HoldersOf(table.TableDataLock))
        {
            if (hold.Mode == LockMode.SchemaModification)
                (folded ??= [])[(table.SchemaLock, hold.Owner, LockMode.SchemaModification)] = null;
        }
        return folded;
    }

    private static bool HoldsExclusive(LockManager locks, LockResource resource, SessionToken owner)
    {
        foreach (var hold in locks.HoldersOf(resource))
        {
            if (hold.Mode == LockMode.Exclusive && ReferenceEquals(hold.Owner, owner))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the SPID of one connection currently holding
    /// <paramref name="resource"/> with a mode incompatible with
    /// <paramref name="waiter"/>'s wait — that's the blocker
    /// <c>sys.dm_os_waiting_tasks</c> attributes the wait to. Returns
    /// <c>null</c> when nothing blocks (a race against grant — the
    /// waiter's about to unblock).
    /// </summary>
    internal static int? FindFirstBlocker(LockResource resource, SimulatedDbConnection waiter)
    {
        if (waiter.WaitingForMode is not { } mode)
            return null;
        foreach (var hold in waiter.Simulation.LockManager.HoldersOf(resource))
        {
            if (ReferenceEquals(hold.Owner, waiter.Session))
                continue;
            if (LockManager.IsCompatible(hold.Mode, mode))
                continue;
            return hold.Owner.Spid;
        }
        return null;
    }

    /// <summary>
    /// Real holds one lock per key per session, so a row lock and a key-range
    /// lock one session holds on the same clustered key show as one row there,
    /// in the mode that combines them — a SERIALIZABLE UPDATE's updated key
    /// reads <c>RangeX-X</c>, an <c>UPDLOCK</c> read's <c>RangeS-U</c>. Here
    /// they are two resources, so the view folds them: per (resource, owner,
    /// mode) hold, null to leave the row lock out and a mode to report the key
    /// lock in instead. Null when nothing folds.
    /// </summary>
    private static Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? FoldRowLocksIntoKeyLocks(LockManager locks, HeapTable table)
    {
        if (KeyLockGroup.ClusteredOwner(table) is not { } owner
            || !table.KeyLockGroups.TryGetValue(owner, out var group)
            || Volatile.Read(ref group.Holds) == 0)
        {
            return null;
        }

        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded = null;
        // A row the session deleted left the row-lock dictionary with its slot,
        // but real's key lock stays, converted to RangeX-X where a range was.
        foreach (var (deleter, images) in table.SupersededKeyImages)
        {
            foreach (var (address, (image, _)) in images)
            {
                if (!table.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex) || !group.TryReadKey(image, out var key) || group.Find(key) is not { } anchor)
                    continue;
                foreach (var keyHold in locks.HoldersOf(anchor))
                {
                    if (ReferenceEquals(keyHold.Owner, deleter) && keyHold.Mode is LockMode.RangeSharedShared or LockMode.RangeSharedUpdate)
                        (folded ??= [])[(anchor, deleter, keyHold.Mode)] = LockMode.RangeExclusiveExclusive;
                }
            }
        }
        foreach (var (address, rowLock) in table.RowLocks)
        {
            foreach (var rowHold in locks.HoldersOf(rowLock))
            {
                var image = table.Heap.ReadSlotBytes(address.PageIndex, address.SlotIndex);
                if (image is null && table.SupersededKeyImages.TryGetValue(rowHold.Owner, out var superseded) && superseded.TryGetValue(address, out var entry))
                    image = entry.Image;
                if (image is null || !group.TryReadKey(image, out var key) || group.Find(key) is not { } anchor)
                    continue;
                foreach (var keyHold in locks.HoldersOf(anchor))
                {
                    if (!ReferenceEquals(keyHold.Owner, rowHold.Owner) || keyHold.Mode is not (LockMode.RangeSharedShared or LockMode.RangeSharedUpdate or LockMode.RangeExclusiveExclusive))
                        continue;
                    folded ??= [];
                    folded[(rowLock, rowHold.Owner, rowHold.Mode)] = null;
                    var combined = (keyHold.Mode, rowHold.Mode) switch
                    {
                        (_, LockMode.Exclusive) => LockMode.RangeExclusiveExclusive,
                        (LockMode.RangeSharedShared, LockMode.Update) => LockMode.RangeSharedUpdate,
                        _ => keyHold.Mode,
                    };
                    var slot = (anchor, keyHold.Owner, keyHold.Mode);
                    if (combined != keyHold.Mode && (!folded.TryGetValue(slot, out var already) || already != LockMode.RangeExclusiveExclusive))
                        folded[slot] = combined;
                    break;
                }
            }
        }
        return folded;
    }

    /// <summary>
    /// Walks all schemas + heap tables to find the object / RID associated
    /// with <paramref name="resource"/>; falls back to a generic
    /// description when no association matches (rare — the resource is
    /// usually a SchemaLock or a HeapTable's row-lock dict entry).
    /// </summary>
    internal static string DescribeResource(Simulation sim, LockResource resource)
    {
        foreach (var db in sim.Databases.Values)
        {
            foreach (var (_, schema) in db.Schemas)
            {
                foreach (var (_, t) in schema.HeapTables)
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
                    if (resource.KeyGroup is { } group && ReferenceEquals(group.Table, t))
                    {
                        return $"KEY: {t.Name} {KeyLockGroup.Describe(resource.AnchorKey)}";
                    }
                }
            }
        }
        return "(unknown)";
    }
}
