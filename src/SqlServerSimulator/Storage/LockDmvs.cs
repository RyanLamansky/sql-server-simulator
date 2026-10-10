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
        LockMode.IntentUpdate => "IU",
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
    /// Yields one row per granted or waiting lock across the server — every
    /// database's schema objects, row locks, key locks and application locks —
    /// then the <c>DATABASE</c> rows: each session's shared lock on its current
    /// database, on the databases of the execution contexts it is nested in,
    /// and on every database it holds or awaits a lock in, master and tempdb
    /// excepted (probed 2026-10-07 against SQL Server 2025). Sessions sharing a
    /// transaction share one lock workspace, whose database locks real lists
    /// once each, under the first session still in it.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranLocks(BatchContext batch, Database database)
    {
        _ = database;
        var sim = batch.Connection.Simulation;
        var connections = sim.SnapshotConnections();
        var workspaces = new Dictionary<int, int>();
        var databaseLocks = new SortedSet<(int Spid, int DatabaseId)>();
        foreach (var connection in connections)
        {
            if (connection.State != System.Data.ConnectionState.Open)
                continue;
            var workspace = WorkspaceSpid(connection);
            workspaces[connection.Spid] = workspace;
            AddDatabaseLock(databaseLocks, workspace, connection.CurrentDatabase);
            foreach (var enclosing in connection.EnclosingDatabases)
                AddDatabaseLock(databaseLocks, workspace, enclosing);
            if (connection.CurrentTransaction is { } transaction)
            {
                foreach (var touched in transaction.TouchedDatabases)
                    AddDatabaseLock(databaseLocks, workspace, touched);
            }
        }

        var ordered = new List<Database>();
        foreach (var (_, each) in sim.Databases)
            ordered.Add(each);
        ordered.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        foreach (var each in ordered)
        {
            foreach (var row in EnumerateDatabaseLocks(batch, each))
            {
                if (row[6].AsInt32 is var spid && workspaces.TryGetValue(spid, out var workspace))
                    AddDatabaseLock(databaseLocks, workspace, each);
                yield return row;
            }
            if (each.Name == Simulation.TempdbDatabaseName)
            {
                foreach (var row in EnumerateTempTableLocks(batch, each, connections))
                    yield return row;
            }
        }

        var databaseType = SqlValue.FromNVarchar("DATABASE");
        var shared = SqlValue.FromNVarchar(ModeAbbreviation(LockMode.Shared));
        var grant = SqlValue.FromNVarchar("GRANT");
        var noEntity = SqlValue.FromInt64(0);
        foreach (var (spid, databaseId) in databaseLocks)
            yield return [databaseType, SqlValue.FromInt32(databaseId), Description(""), noEntity, shared, grant, SqlValue.FromInt32(spid)];

        static void AddDatabaseLock(SortedSet<(int, int)> locks, int spid, Database held)
        {
            if (held.Name is not (Simulation.MasterDatabaseName or Simulation.TempdbDatabaseName))
                _ = locks.Add((spid, held.Id));
        }
    }

    /// <summary>
    /// The rows of <c>sys.dm_tran_locks</c> itself: <see cref="EnumerateDmTranLocks"/>'s,
    /// with the <c>resource_subtype</c> column real carries second, empty for
    /// every resource the simulator locks (probed 2026-10-07 against SQL Server
    /// 2025, a <c>DATABASE</c> row's included).
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranLocksView(BatchContext batch, Database database)
    {
        var noSubtype = SqlValue.FromNVarchar("");
        foreach (var row in EnumerateDmTranLocks(batch, database))
            yield return [row[0], noSubtype, row[1], row[2], row[3], row[4], row[5], row[6]];
    }

    /// <summary>
    /// The session a <c>DATABASE</c> lock of <paramref name="connection"/> is
    /// listed under: its own, or for a session in a transaction other sessions
    /// share — bound through <c>sp_bindsession</c>, or a loopback server's
    /// session enlisted in it — the first session still in it, whose workspace
    /// holds the database locks of all of them (probed 2026-10-07 against SQL
    /// Server 2025: a bound session's database lock shows under the session
    /// that began the transaction, and under its own once that one has left).
    /// </summary>
    private static int WorkspaceSpid(SimulatedDbConnection connection) =>
        connection.CurrentTransaction?.FirstMember(connection) is { } first ? first.Spid : connection.Spid;

    /// <summary>
    /// The lock rows of <paramref name="table"/>: its object lock — the schema
    /// and data locks are one resource, real's object lock — then its row and
    /// key locks.
    /// </summary>
    private static IEnumerable<SqlValue[]> EmitTableLocks(
        BatchContext batch, HeapTable table, SqlValue dbId,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitsByResource, List<(SimulatedDbConnection Waiter, LockResource Resource, string Key)>? keyWaits)
    {
        var locks = batch.Connection.Simulation.LockManager;
        var grantStatus = SqlValue.FromNVarchar("GRANT");
        var waitStatus = SqlValue.FromNVarchar("WAIT");
        // Real reports a key lock as resource_type KEY described by a hash of
        // the index key (KeyLockGroup.Describe).
        var keyType = SqlValue.FromNVarchar("KEY");
        foreach (var row in EmitRowsForResource(locks, SqlValue.FromNVarchar("OBJECT"), dbId, string.Empty, table.ObjectId, table.TableDataLock, waitsByResource, grantStatus, waitStatus, FoldObjectModes(locks, table.TableDataLock)))
            yield return row;
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? pagesFolded = null;
        if (!table.PageLocks.IsEmptyLockFree())
        {
            foreach (var row in EmitPageLocks(locks, table, dbId, waitsByResource, grantStatus, waitStatus))
                yield return row;
            pagesFolded = FoldRowLocksUnderPageLocks(locks, table);
        }
        var keyFolded = FoldRowLocksIntoKeyLocks(locks, table);
        var folded = FoldConvertedRowLocks(locks, table, pagesFolded is null ? keyFolded : keyFolded is null ? pagesFolded : Merged(keyFolded, pagesFolded));
        foreach (var row in EmitRowLocks(batch, table, SqlValue.FromNVarchar("RID"), keyType, dbId, waitsByResource, keyWaits, grantStatus, waitStatus, folded))
            yield return row;
        foreach (var (_, group) in table.KeyLockGroups)
        {
            foreach (var row in EmitRowsForResource(locks, keyType, dbId, group.Describe(null), table.ObjectId, group.Infinity, waitsByResource, grantStatus, waitStatus, folded))
                yield return row;
            foreach (var kv in group.Anchors)
            {
                foreach (var row in EmitRowsForResource(locks, keyType, dbId, group.Describe(kv.Key), table.ObjectId, kv.Value, waitsByResource, grantStatus, waitStatus, folded))
                    yield return row;
            }
        }
    }

    /// <summary>
    /// The lock rows of the temp tables, which real lists in tempdb: every
    /// open session's local ones and the global ones.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateTempTableLocks(BatchContext batch, Database tempdb, SimulatedDbConnection[] connections)
    {
        var dbId = SqlValue.FromInt32(tempdb.Id);
        var waitsByResource = SnapshotWaiters(batch.Connection.Simulation, out var keyWaits);
        foreach (var connection in connections)
        {
            if (connection.State != System.Data.ConnectionState.Open)
                continue;
            foreach (var (_, table) in connection.TempTables)
            {
                foreach (var row in EmitTableLocks(batch, table, dbId, waitsByResource, keyWaits))
                    yield return row;
            }
        }
        foreach (var (_, table) in batch.Connection.Simulation.GlobalTempTables)
        {
            foreach (var row in EmitTableLocks(batch, table, dbId, waitsByResource, keyWaits))
                yield return row;
        }
    }

    /// <summary>The lock rows of <paramref name="database"/>'s resources.</summary>
    private static IEnumerable<SqlValue[]> EnumerateDatabaseLocks(BatchContext batch, Database database)
    {
        var sim = batch.Connection.Simulation;
        var locks = sim.LockManager;
        var dbId = SqlValue.FromInt32(database.Id);
        // Real describes an OBJECT resource by no text at all, not by name —
        // the object is resource_associated_entity_id (probed 2026-09-30
        // against SQL Server 2025).
        var objectDescription = string.Empty;
        var grantStatus = SqlValue.FromNVarchar("GRANT");
        var waitStatus = SqlValue.FromNVarchar("WAIT");
        var objectType = SqlValue.FromNVarchar("OBJECT");

        var waitsByResource = SnapshotWaiters(sim, out var keyWaits);

        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, t) in schema.HeapTables)
            {
                foreach (var row in EmitTableLocks(batch, t, dbId, waitsByResource, keyWaits))
                    yield return row;
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

    /// <summary>
    /// A <c>resource_description</c> value as real's view holds it: blank-padded
    /// to the column's 256 characters whatever the resource type (probed
    /// 2026-10-07 against SQL Server 2025, every row's <c>DATALENGTH</c> 512).
    /// </summary>
    private static SqlValue Description(string text) => SqlValue.FromNVarchar(text.PadRight(256));

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
                SqlValue.FromNVarchar(conn.WaitRecord?.WaitingOnKey is { } key && resource.OwningTable is { } keyed ? $"KEY: {keyed.Name} {key}" : DescribeResource(sim, resource)),
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
            if (conn.WaitRecord?.WaitingOnKey is { } key)
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

        // A key a session deleted and inserted again is two rows here, at two
        // addresses, and one key lock on real, whose index keeps the key in
        // place: each key a session holds shows once per index.
        var shown = new HashSet<(KeyLockGroup? Group, string Description, int Spid, string Mode, bool Waiting)>();
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
            {
                if (rowGroup is null || shown.Add((rowGroup, description, row[6].AsInt32, row[4].AsString, row[5].AsString == "WAIT")))
                    yield return row;
            }
            foreach (var hold in holders)
            {
                if (hold.Mode != LockMode.Exclusive)
                    continue;
                var spid = SqlValue.FromInt32(hold.Owner.Acting.Spid);
                foreach (var (group, key) in IndexKeysWritten(batch, table, rowGroup, address, resource, hold.Owner, live))
                {
                    if (!shown.Add((group, key, hold.Owner.Acting.Spid, ModeAbbreviation(LockMode.Exclusive), false)))
                        continue;
                    yield return
                    [
                        keyType,
                        dbIdVal,
                        Description(key),
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
                Description(key),
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
    private static List<(KeyLockGroup Group, string Description)> IndexKeysWritten(
        BatchContext batch, HeapTable table, KeyLockGroup? rowGroup, (int PageIndex, int SlotIndex) address, LockResource resource, SessionToken owner, byte[]? live)
    {
        var keys = new List<(KeyLockGroup Group, string Description)>();
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
            if (!keys.Contains((group, description)))
                keys.Add((group, description));
        }
    }

    // An image's key tuple in group, NULL components included, as an anchor
    // on it is described.
    private static string DescribeImageKey(HeapTable table, KeyLockGroup group, byte[] image)
    {
        var components = new SqlValue[group.Ordinals.Length];
        for (var i = 0; i < components.Length; i++)
            components[i] = RowDecoder.DecodeColumn(table.StoredColumns, image, group.Ordinals[i], table.Heap);
        return group.Describe(new Parser.SqlValueKey(components));
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
        var descVal = Description(description);
        var entityVal = SqlValue.FromInt64(entityId);
        // GRANT rows from current holders. A U its holder has since taken X
        // over is real's lock converted, reported as the X alone.
        var holders = locks.HoldersOf(resource);
        foreach (var hold in holders)
        {
            var mode = hold.Mode;
            if (mode == LockMode.Update && HoldsExclusive(locks, resource, hold.Owner))
                continue;
            // A statement's Sch-S is real's compile-time lock, which real's
            // view shows only while nothing else of the session's stands on the
            // object: an executing read's IS, a write's IX and a redefinition's
            // Sch-M, granted or waiting, each show alone.
            if (mode == LockMode.SchemaStability && HoldsOrSeeksAnotherMode(holders, waitersByResource, resource, hold.Owner))
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
                SqlValue.FromInt32(hold.Owner.Acting.Spid),
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

    private static bool HoldsOrSeeksAnotherMode(LockResource.Hold[] holders, Dictionary<LockResource, List<SimulatedDbConnection>> waitersByResource, LockResource resource, SessionToken owner)
    {
        foreach (var hold in holders)
        {
            if (hold.Mode != LockMode.SchemaStability && ReferenceEquals(hold.Owner, owner))
                return true;
        }
        if (waitersByResource.TryGetValue(resource, out var waiters))
        {
            foreach (var waiter in waiters)
            {
                if (ReferenceEquals(waiter.LockOwner, owner))
                    return true;
            }
        }
        return false;
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
    /// <paramref name="waiter"/>'s wait, or failing that of the request queued
    /// ahead of it that it waits behind — that's the blocker
    /// <c>sys.dm_os_waiting_tasks</c> attributes the wait to, as real names a
    /// Sch-M still waiting for the Sch-S request queued behind it (probed
    /// 2026-10-07 against SQL Server 2025). Returns <c>null</c> when nothing
    /// blocks (a race against grant — the waiter's about to unblock).
    /// </summary>
    internal static int? FindFirstBlocker(LockResource resource, SimulatedDbConnection waiter)
    {
        if (waiter.WaitingForMode is not { } mode)
            return null;
        var locks = waiter.Simulation.LockManager;
        foreach (var hold in locks.HoldersOf(resource))
        {
            if (ReferenceEquals(hold.Owner, waiter.LockOwner))
                continue;
            if (LockManager.IsCompatible(hold.Mode, mode))
                continue;
            return hold.Owner.Acting.Spid;
        }
        foreach (var (owner, waiting) in locks.QueuedAheadOf(resource, waiter.LockOwner))
        {
            if (!LockManager.IsCompatible(waiting, mode))
                return owner.Acting.Spid;
        }
        return null;
    }

    /// <summary>
    /// The page locks of <paramref name="table"/>, a <c>PAGLOCK</c> read's or
    /// write's S, U and X, in page order, described as real describes a page,
    /// <c>file:page</c>. The intent locks a row lock takes on its page while
    /// such a lock is held stay out, as every other row lock's page intent
    /// does here (see <c>docs/claude/locking.md</c>).
    /// </summary>
    private static IEnumerable<SqlValue[]> EmitPageLocks(
        LockManager locks, HeapTable table, SqlValue dbId, Dictionary<LockResource, List<SimulatedDbConnection>> waitsByResource, SqlValue grantStatus, SqlValue waitStatus)
    {
        var pageType = SqlValue.FromNVarchar("PAGE");
        var pages = table.PageLocks.ToArray();
        Array.Sort(pages, static (a, b) => a.Key.CompareTo(b.Key));
        foreach (var (page, resource) in pages)
        {
            if (resource.Holders.Count == 0 && !waitsByResource.ContainsKey(resource))
                continue;
            Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? intents = null;
            foreach (var hold in locks.HoldersOf(resource))
            {
                if (hold.Mode is LockMode.IntentShared or LockMode.IntentUpdate or LockMode.IntentExclusive)
                    (intents ??= [])[(resource, hold.Owner, hold.Mode)] = null;
            }
            foreach (var row in EmitRowsForResource(locks, pageType, dbId, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1:{FirstPageId + page}"), table.ObjectId, resource, waitsByResource, grantStatus, waitStatus, intents))
                yield return row;
        }
    }

    /// <summary>The page id the first page of a table's layout is described by.</summary>
    private const int FirstPageId = 8;

    /// <summary>
    /// A <c>PAGLOCK</c> write's row X under its own X on the row's page, which
    /// real takes in place of the row's: the view shows the page's alone
    /// (probed 2026-10-09 against SQL Server 2025). Null when nothing folds.
    /// </summary>
    private static Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? FoldRowLocksUnderPageLocks(LockManager locks, HeapTable table)
    {
        RealPageLayout? layout = null;
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded = null;
        foreach (var (address, rowLock) in table.RowLocks)
        {
            if (rowLock.Holders.Count == 0)
                continue;
            foreach (var hold in locks.HoldersOf(rowLock))
            {
                if (hold.Mode != LockMode.Exclusive)
                    continue;
                layout ??= RealPageLayout.For(table);
                // A row the write deleted is on no page of the layout any
                // longer; it was on one the write locked.
                if (table.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex)
                    ? HoldsAnyPage(locks, table, hold.Owner)
                    : HoldsPage(locks, table, layout.PageOf(address), hold.Owner) || HoldsPage(locks, table, layout.InsertPageOf(address), hold.Owner))
                {
                    (folded ??= [])[(rowLock, hold.Owner, LockMode.Exclusive)] = null;
                }
            }
        }
        return folded;

        static bool HoldsPage(LockManager locks, HeapTable table, int page, SessionToken owner) =>
            table.PageLocks.TryGetValue(page, out var pageLock) && locks.IsHeldBy(pageLock, LockMode.Exclusive, owner);

        static bool HoldsAnyPage(LockManager locks, HeapTable table, SessionToken owner)
        {
            foreach (var (_, pageLock) in table.PageLocks)
            {
                if (locks.IsHeldBy(pageLock, LockMode.Exclusive, owner))
                    return true;
            }
            return false;
        }
    }

    private static Dictionary<(LockResource, SessionToken, LockMode), LockMode?> Merged(
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?> into, Dictionary<(LockResource, SessionToken, LockMode), LockMode?> from)
    {
        foreach (var (key, value) in from)
            into[key] = value;
        return into;
    }

    /// <summary>
    /// Real holds one lock per session on an object, converting it as the
    /// session asks for more — <c>IS</c> under <c>IX</c> or <c>S</c> stays the
    /// stronger, <c>S</c> beside <c>IX</c> becomes <c>SIX</c>, and an <c>X</c>
    /// takes in everything (probed 2026-10-09 against SQL Server 2025: a
    /// transaction's REPEATABLE READ read and update of one table hold
    /// <c>OBJECT IX</c>, a held <c>TABLOCK</c> read and an update
    /// <c>OBJECT SIX</c>) — where the lock manager here keeps each mode as a
    /// hold of its own, which are compatible with the same requests as the
    /// converted one. So the view folds a session's data-mode holds on the
    /// object into that one mode. Null when nothing folds.
    /// </summary>
    private static Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? FoldObjectModes(LockManager locks, LockResource resource)
    {
        if (resource.Holders.Count < 2)
            return null;
        var holders = locks.HoldersOf(resource);
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded = null;
        for (var i = 0; i < holders.Length; i++)
        {
            var owner = holders[i].Owner;
            if (!IsFoldableObjectMode(holders[i].Mode) || !FirstOfOwner(holders, i))
                continue;
            var has = 0;
            var count = 0;
            for (var j = i; j < holders.Length; j++)
            {
                if (ReferenceEquals(holders[j].Owner, owner) && IsFoldableObjectMode(holders[j].Mode))
                {
                    has |= 1 << (int)holders[j].Mode;
                    count++;
                }
            }
            if (count < 2)
                continue;
            static bool Has(int set, LockMode mode) => (set & (1 << (int)mode)) != 0;
            LockMode combined;
            if (Has(has, LockMode.Exclusive))
                combined = LockMode.Exclusive;
            else if (Has(has, LockMode.SharedIntentExclusive) || (Has(has, LockMode.Shared) && Has(has, LockMode.IntentExclusive)))
                combined = LockMode.SharedIntentExclusive;
            else if (Has(has, LockMode.Update) && !Has(has, LockMode.IntentExclusive))
                combined = LockMode.Update;
            else if (Has(has, LockMode.Shared))
                combined = LockMode.Shared;
            else if (Has(has, LockMode.IntentExclusive) && !Has(has, LockMode.Update))
                combined = LockMode.IntentExclusive;
            else
                continue;
            var shown = false;
            for (var j = i; j < holders.Length; j++)
            {
                if (!ReferenceEquals(holders[j].Owner, owner) || !IsFoldableObjectMode(holders[j].Mode))
                    continue;
                // The first hold of the owner shows the converted mode; the rest go.
                (folded ??= [])[(resource, owner, holders[j].Mode)] = shown ? null : combined;
                shown = true;
            }
        }
        return folded;

        static bool IsFoldableObjectMode(LockMode mode) =>
            mode is LockMode.IntentShared or LockMode.IntentExclusive or LockMode.SharedIntentExclusive or LockMode.Shared or LockMode.Update or LockMode.Exclusive;

        static bool FirstOfOwner(LockResource.Hold[] holders, int index)
        {
            for (var k = 0; k < index; k++)
            {
                if (ReferenceEquals(holders[k].Owner, holders[index].Owner) && IsFoldableObjectMode(holders[k].Mode))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Real converts a session's lock on a row to the stronger mode it asks for
    /// next — a <c>REPEATABLE READ</c> read's S on a row its transaction then
    /// updates becomes the one X — where here each mode is a hold of its own,
    /// so the view leaves out an S or U beside the same owner's U or X on the
    /// row (probed 2026-10-09 against SQL Server 2025: a reader's twenty
    /// <c>KEY S</c> read nineteen beside the X its transaction's update of one
    /// of their rows took). <paramref name="folded"/> gains the entries.
    /// </summary>
    private static Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? FoldConvertedRowLocks(
        LockManager locks, HeapTable table, Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded)
    {
        if (Volatile.Read(ref table.ActiveDataWriters) == 0 && Volatile.Read(ref table.ActiveUpdateLocks) == 0)
            return folded;
        foreach (var (_, rowLock) in table.RowLocks)
        {
            var holds = locks.HoldersOf(rowLock);
            if (holds.Length < 2)
                continue;
            foreach (var weaker in holds)
            {
                if (weaker.Mode is not (LockMode.Shared or LockMode.Update))
                    continue;
                foreach (var stronger in holds)
                {
                    if (ReferenceEquals(stronger.Owner, weaker.Owner)
                        && (stronger.Mode == LockMode.Exclusive || (stronger.Mode == LockMode.Update && weaker.Mode == LockMode.Shared)))
                    {
                        (folded ??= [])[(rowLock, weaker.Owner, weaker.Mode)] = null;
                        break;
                    }
                }
            }
        }
        return folded;
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
        // A name's lock is real's key lock on the name's catalog row.
        if (resource.Definition is { } definition)
            return $"KEY: {definition.Name}";
        foreach (var (_, db) in sim.Databases)
        {
            foreach (var (_, schema) in db.Schemas)
            {
                foreach (var (_, t) in schema.HeapTables)
                {
                    if (ReferenceEquals(t.TableDataLock, resource))
                        return $"OBJECT: {t.Name}";
                    if (resource.RowAddress is { } address && ReferenceEquals(resource.OwningTable, t))
                    {
                        // A clustered table's row lock is real's lock on its key.
                        return KeyLockGroup.RowGroupOf(t) is { } rowGroup
                            && (t.Heap.ReadLiveRow(address.PageIndex, address.SlotIndex) ?? PreImageOf(t, address, sim.LockManager.HoldersOf(resource))) is { } image
                            ? $"KEY: {t.Name} {DescribeImageKey(t, rowGroup, image)}"
                            : $"RID: {t.Name} {address.PageIndex}:{address.SlotIndex}";
                    }
                    if (resource.KeyGroup is { } group && ReferenceEquals(group.Table, t))
                    {
                        return $"KEY: {t.Name} {group.Describe(resource.AnchorKey)}";
                    }
                }
                foreach (var other in schema.SchemaObjects())
                {
                    if (ReferenceEquals(other.SchemaLock, resource))
                        return $"OBJECT: {other.Name}";
                }
            }
        }
        return "(unknown)";
    }
}
