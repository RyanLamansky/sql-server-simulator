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
    internal static IEnumerable<LockRow> EnumerateDmTranLocks(BatchContext batch, Database database)
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
                if (workspaces.TryGetValue(row.Spid, out var workspace))
                    AddDatabaseLock(databaseLocks, workspace, each);
                yield return row;
            }
            if (each.Name == Simulation.TempdbDatabaseName)
            {
                foreach (var row in EnumerateTempTableLocks(batch, each, connections))
                    yield return row;
            }
        }

        foreach (var (spid, databaseId) in databaseLocks)
            yield return new LockRow("DATABASE", databaseId, "", 0, 0, 0, LockMode.Shared, waiting: false, spid, owner: null, resource: null);

        static void AddDatabaseLock(SortedSet<(int, int)> locks, int spid, Database held)
        {
            if (held.Name is not (Simulation.MasterDatabaseName or Simulation.TempdbDatabaseName))
                _ = locks.Add((spid, held.Id));
        }
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
    private static IEnumerable<LockRow> EmitTableLocks(
        BatchContext batch, HeapTable table, int dbId,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitsByResource, List<(SimulatedDbConnection Waiter, LockResource Resource, string Key, int IndexId)>? keyWaits)
    {
        var locks = batch.Connection.Simulation.LockManager;
        foreach (var row in EmitRowsForResource(locks, "OBJECT", dbId, string.Empty, table.ObjectId, table.ObjectId, 0, table.TableDataLock, waitsByResource, FoldObjectModes(locks, table.TableDataLock)))
            yield return row;
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? pagesFolded = null;
        if (!table.PageLocks.IsEmptyLockFree())
        {
            foreach (var row in EmitPageLocks(locks, table, dbId, waitsByResource))
                yield return row;
            pagesFolded = FoldRowLocksUnderPageLocks(locks, table);
        }
        var keyFolded = FoldRowLocksIntoKeyLocks(locks, table);
        var folded = FoldConvertedRowLocks(locks, table, pagesFolded is null ? keyFolded : keyFolded is null ? pagesFolded : Merged(keyFolded, pagesFolded));
        foreach (var row in EmitRowLocks(batch, table, dbId, waitsByResource, keyWaits, folded))
            yield return row;
        foreach (var (_, group) in table.KeyLockGroups)
        {
            var hobt = HobtOf(table, group.IndexId);
            foreach (var row in EmitRowsForResource(locks, "KEY", dbId, group.Describe(null), hobt, table.ObjectId, group.IndexId, group.Infinity, waitsByResource, folded))
                yield return row;
            foreach (var kv in group.Anchors)
            {
                foreach (var row in EmitRowsForResource(locks, "KEY", dbId, group.Describe(kv.Key), hobt, table.ObjectId, group.IndexId, kv.Value, waitsByResource, folded))
                    yield return row;
            }
        }
    }

    /// <summary>
    /// The <c>hobt_id</c> of one of <paramref name="table"/>'s rowsets — the
    /// heap's for index 0 — which a <c>KEY</c>, <c>RID</c> or <c>PAGE</c>
    /// lock names as its <c>resource_associated_entity_id</c>, as
    /// <c>sys.partitions</c> reports it (probed 2026-10-16 against SQL Server
    /// 2025). A partitioned index's rows report its first partition's.
    /// </summary>
    private static long HobtOf(HeapTable table, int indexId) => PartitionCensus.UnpartitionedId(table, indexId);

    /// <summary>The index id of <paramref name="table"/>'s base rowset: its clustered index's, or the heap's 0.</summary>
    private static int BaseIndexIdOf(HeapTable table) => KeyLockGroup.RowGroupOf(table) is { } rows ? rows.IndexId : 0;

    /// <summary>
    /// The lock rows of the temp tables, which real lists in tempdb: every
    /// open session's local ones and the global ones.
    /// </summary>
    private static IEnumerable<LockRow> EnumerateTempTableLocks(BatchContext batch, Database tempdb, SimulatedDbConnection[] connections)
    {
        var dbId = tempdb.Id;
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
    private static IEnumerable<LockRow> EnumerateDatabaseLocks(BatchContext batch, Database database)
    {
        var sim = batch.Connection.Simulation;
        var locks = sim.LockManager;
        var dbId = database.Id;

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
                foreach (var row in EmitObjectLocks(locks, dbId, v, waitsByResource))
                    yield return row;
            }
            foreach (var (_, f) in schema.Functions)
            {
                foreach (var row in EmitObjectLocks(locks, dbId, f, waitsByResource))
                    yield return row;
            }
            foreach (var (_, p) in schema.Procedures)
            {
                foreach (var row in EmitObjectLocks(locks, dbId, p, waitsByResource))
                    yield return row;
            }
            foreach (var (_, s) in schema.Sequences)
            {
                foreach (var row in EmitObjectLocks(locks, dbId, s, waitsByResource))
                    yield return row;
            }
            foreach (var (_, tt) in schema.TableTypes)
            {
                foreach (var row in EmitRowsForResource(locks, "OBJECT", dbId, string.Empty, tt.ObjectId, tt.ObjectId, 0, tt.SchemaLock, waitsByResource))
                    yield return row;
            }
            foreach (var (_, tr) in schema.Triggers)
            {
                foreach (var row in EmitObjectLocks(locks, dbId, tr, waitsByResource))
                    yield return row;
            }
        }

        // Application locks (sp_getapplock family).
        List<((int PrincipalId, string Resource) Key, LockResource Resource)> appLocks;
        lock (database.ApplicationLocks)
        {
            appLocks = new(database.ApplicationLocks.Count);
            foreach (var kv in database.ApplicationLocks)
                appLocks.Add((kv.Key, kv.Value));
        }

        foreach (var (key, resource) in appLocks)
        {
            foreach (var row in EmitRowsForResource(locks, "APPLICATION", dbId, ApplicationDescription(key.PrincipalId, key.Resource), entityId: 0, objectId: 0, indexId: 0, resource, waitsByResource))
                yield return row;
        }
    }

    // Real describes an OBJECT resource by no text at all, not by name — the
    // object is resource_associated_entity_id (probed 2026-09-30 against SQL
    // Server 2025).
    private static IEnumerable<LockRow> EmitObjectLocks(LockManager locks, int dbId, Schemas.SchemaObject module, Dictionary<LockResource, List<SimulatedDbConnection>> waitsByResource) =>
        EmitRowsForResource(locks, "OBJECT", dbId, string.Empty, module.ObjectId, module.ObjectId, 0, module.SchemaLock, waitsByResource);

    /// <summary>
    /// An application lock's <c>resource_description</c>,
    /// <c>&lt;principal-id&gt;:[&lt;name&gt;]:(&lt;hash&gt;)</c>; the hash is FNV-1a
    /// over the name here, so it won't byte-match real's undocumented hash —
    /// the id and bracketed name do.
    /// </summary>
    private static string ApplicationDescription(int principalId, string name) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{principalId}:[{name}]:({Fnv1a32(name):x8})");

    /// <summary>
    /// One lock <see cref="EnumerateDmTranLocks"/> lists: a granted hold or a
    /// waiting request on one resource.
    /// </summary>
    internal readonly struct LockRow(string type, int databaseId, string description, long entityId, int objectId, int indexId, LockMode mode, bool waiting, int spid, SessionToken? owner, LockResource? resource)
    {
        /// <summary>The <c>resource_type</c>: <c>OBJECT</c>, <c>PAGE</c>, <c>RID</c>, <c>KEY</c>, <c>APPLICATION</c> or <c>DATABASE</c>.</summary>
        public readonly string Type = type;

        public readonly int DatabaseId = databaseId;

        /// <summary>The <c>resource_description</c>, unpadded.</summary>
        public readonly string Description = description;

        /// <summary>The <c>resource_associated_entity_id</c>: an object's id, a rowset's <c>hobt_id</c>, or 0.</summary>
        public readonly long EntityId = entityId;

        /// <summary>The object the resource belongs to, 0 for none; <c>sp_lock</c>'s <c>ObjId</c>.</summary>
        public readonly int ObjectId = objectId;

        /// <summary>The index of <see cref="ObjectId"/> the resource belongs to; <c>sp_lock</c>'s <c>IndId</c>.</summary>
        public readonly int IndexId = indexId;

        public readonly LockMode Mode = mode;

        /// <summary>A request still waiting (<c>WAIT</c>) rather than a granted hold (<c>GRANT</c>).</summary>
        public readonly bool Waiting = waiting;

        public readonly int Spid = spid;

        /// <summary>The token holding or requesting the lock; null for a <c>DATABASE</c> lock.</summary>
        public readonly SessionToken? Owner = owner;

        /// <summary>The lock manager's resource; null for a <c>DATABASE</c> lock.</summary>
        public readonly LockResource? Resource = resource;
    }

    /// <summary>
    /// The rows of <c>sys.dm_tran_locks</c> itself: each of
    /// <see cref="EnumerateDmTranLocks"/>'s locks in real's nineteen columns.
    /// The owner columns name the holder's transaction, a <c>DATABASE</c>
    /// lock's shared workspace, and a session-owned application lock's
    /// session; <c>request_lifetime</c> marks a lock its transaction holds to
    /// its end (probed 2026-10-16 against SQL Server 2025). An object or
    /// database lock lists once, on lock partition 0, as on a server with
    /// fewer than 16 schedulers, whose locks real doesn't partition. The
    /// address columns are stand-ins, consistent with
    /// <c>sys.dm_os_waiting_tasks.resource_address</c>.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranLocksView(BatchContext batch, Database database)
    {
        var sim = batch.Connection.Simulation;
        var connections = new Dictionary<int, SimulatedDbConnection>();
        foreach (var connection in sim.SnapshotConnections())
            connections[connection.Spid] = connection;
        var noSubtype = SqlValue.FromNVarchar("");
        var lockType = SqlValue.FromNVarchar("LOCK");
        var grant = SqlValue.FromNVarchar("GRANT");
        var wait = SqlValue.FromNVarchar("WAIT");
        var noPartition = SqlValue.Null(SqlType.Int32);
        var zero = SqlValue.FromInt32(0);
        var one = SqlValue.FromInt16(1);
        var noGuid = SqlValue.FromGuid(Guid.Empty);
        foreach (var row in EnumerateDmTranLocks(batch, database))
        {
            _ = connections.TryGetValue(row.Spid, out var holder);
            var (ownerType, ownerId, lockspace) = OwnerOf(row, holder);
            yield return
            [
                SqlValue.FromNVarchar(row.Type), noSubtype, SqlValue.FromInt32(row.DatabaseId), SqlValue.FromNVarchar(row.Description.PadRight(256)),
                SqlValue.FromInt64(row.EntityId), row.Type is "OBJECT" or "DATABASE" ? zero : noPartition, SqlValue.FromNVarchar(ModeAbbreviation(row.Mode)),
                lockType, row.Waiting ? wait : grant, one, SqlValue.FromInt32(row.Waiting ? 0 : TransactionLifetime(row, holder)),
                SqlValue.FromInt32(row.Spid), zero, zero, SqlValue.FromNVarchar(ownerType), SqlValue.FromInt64(ownerId), noGuid,
                SqlValue.FromNVarchar(lockspace), SqlValue.FromVarbinary(LockOwnerAddress(row)),
            ];
        }
    }

    /// <summary>The <c>request_lifetime</c> of a lock its transaction holds to its end (probed 2026-10-16 against SQL Server 2025).</summary>
    private const int TransactionLifetimeFlag = 0x2000000;

    /// <summary>
    /// <see cref="TransactionLifetimeFlag"/> for a lock its holder's open
    /// transaction keeps to its end — every lock but a schema-stability one
    /// while the session sits between statements, and while it runs one, the
    /// locks a read-committed read wouldn't let go of at once — else 0, as a
    /// statement's own locks, a database's shared workspace lock and an
    /// application lock read on real.
    /// </summary>
    private static int TransactionLifetime(LockRow row, SimulatedDbConnection? holder)
    {
        if (holder is null || holder.OpenTransactionCount == 0 || row.Type is "DATABASE" or "APPLICATION" || row.Mode == LockMode.SchemaStability)
            return 0;
        var running = holder.Session.CurrentExecutingThreadId is not null || holder.WaitingOnResource is not null;
        if (!running)
            return TransactionLifetimeFlag;
        var keepsReads = holder.SessionIsolationLevel is System.Data.IsolationLevel.RepeatableRead or System.Data.IsolationLevel.Serializable;
        return keepsReads || row.Mode is not (LockMode.IntentShared or LockMode.Shared) ? TransactionLifetimeFlag : 0;
    }

    /// <summary>
    /// The <c>request_owner_type</c>, <c>request_owner_id</c> and
    /// <c>request_owner_lockspace_id</c> of <paramref name="row"/>: a database
    /// lock's shared transaction workspace, a session-owned application lock's
    /// session, and otherwise the holder's transaction, by its id — a running
    /// statement's autocommit one outside a user transaction.
    /// </summary>
    private static (string Type, long Id, string Lockspace) OwnerOf(LockRow row, SimulatedDbConnection? holder)
    {
        var workspace = LockspaceAddress(row.Spid);
        if (row.Type == "DATABASE")
            return ("SHARED_TRANSACTION_WORKSPACE", 0, workspace + ":0:0");
        if (row.Type == "APPLICATION" && holder is not null && row.Resource is { } resource)
        {
            foreach (var hold in holder.SessionAppLocks.ToArray())
            {
                if (ReferenceEquals(hold.LockResource, resource))
                    return ("SESSION", 0, workspace + ":0:0");
            }
        }
        var transactionId = holder?.CurrentTransaction?.TransactionId
            ?? (holder is not null && BuiltInResources.RunsAutocommitTransaction(holder) ? holder.RunningStatementTransactionId() : 0);
        return ("TRANSACTION", transactionId, workspace + ":1:1");
    }

    /// <summary>A stand-in <c>request_owner_lockspace_id</c> address for a session's lock workspace.</summary>
    private static string LockspaceAddress(int spid) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{0x0000060000000000L + (spid * 0x10000L):X16}");

    /// <summary>
    /// A stand-in <c>lock_owner_address</c>, distinct per resource, session
    /// and mode, and the same as the waiting task's
    /// <c>resource_address</c> for a request still waiting.
    /// </summary>
    private static byte[] LockOwnerAddress(LockRow row)
    {
        var resourceHash = row.Resource is { } resource ? (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(resource) : (uint)row.DatabaseId;
        var address = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(address, 0x0000070000000000L
            + ((long)(resourceHash & 0xFFFFF) << 20) + ((row.Spid & 0x3FF) << 10) + (((int)row.Mode & 0xF) << 1) + (row.Waiting ? 1 : 0));
        return address;
    }

    /// <summary>A stand-in for the address of a lock resource's block, which real's wait descriptions name as <c>id=lock…</c>.</summary>
    private static string LockBlockId(LockResource? resource) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"lock{0xC00000000L + (((resource is null ? 0u : (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(resource)) & 0xFFFFFFF) * 0x40):x}");

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
    /// The lock <paramref name="waiter"/> waits for, as <see cref="EnumerateDmTranLocks"/>
    /// lists it; null when it waits for none
    /// (or was granted while the view looked).
    /// </summary>
    private static LockRow? WaitedLock(BatchContext batch, SimulatedDbConnection waiter)
    {
        if (waiter.WaitingOnResource is not { } resource)
            return null;
        foreach (var row in EnumerateDmTranLocks(batch, batch.CurrentDatabase))
        {
            if (row.Waiting && row.Spid == waiter.Spid && ReferenceEquals(row.Resource, resource))
                return row;
        }
        return null;
    }

    /// <summary>
    /// The <c>wait_resource</c> of a request waiting on a lock, as
    /// <c>sys.dm_exec_requests</c> reports it — or, with
    /// <paramref name="sysprocesses"/>, as <c>sysprocesses.waitresource</c>
    /// abbreviates it: <c>KEY: db:hobt (hash)</c>, <c>RID: db:file:page:slot</c>,
    /// <c>PAGE: db:file:page </c> (<c>PAG:</c>), <c>OBJECT: db:object:partition </c>
    /// (<c>TAB:</c>) and <c>APPLICATION: db:principal:[name]:(hash)</c>
    /// (<c>APP:</c>), the page and object forms with real's trailing space in
    /// the request view (probed 2026-10-16 against SQL Server 2025). Empty
    /// when the request waits on no lock.
    /// </summary>
    internal static string WaitResource(BatchContext batch, SimulatedDbConnection waiter, bool sysprocesses = false)
    {
        if (WaitedLock(batch, waiter) is not { } row)
            return "";
        var trail = sysprocesses ? "" : " ";
        return row.Type switch
        {
            "APPLICATION" => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(sysprocesses ? "APP" : "APPLICATION")}: {row.DatabaseId}:{row.Description}"),
            "KEY" => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"KEY: {row.DatabaseId}:{row.EntityId} {row.Description}"),
            "OBJECT" => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(sysprocesses ? "TAB" : "OBJECT")}: {row.DatabaseId}:{row.EntityId}:0{trail}"),
            "PAGE" => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(sysprocesses ? "PAG" : "PAGE")}: {row.DatabaseId}:{row.Description}{trail}"),
            "RID" => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RID: {row.DatabaseId}:{row.Description}"),
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{row.Type}: {row.DatabaseId}:{row.Description}"),
        };
    }

    /// <summary>
    /// The <c>resource_description</c> <c>sys.dm_os_waiting_tasks</c> gives a
    /// lock wait, in real's own words (probed 2026-10-16 against SQL Server
    /// 2025): <c>keylock hobtid=… dbid=… id=lock… mode=X associatedObjectId=…</c>,
    /// and the <c>ridlock</c>, <c>pagelock</c>, <c>objectlock</c> and
    /// <c>applicationlock</c> forms beside it, <c>mode</c> naming the blocking
    /// hold's mode. Null when the waiter waits on no lock.
    /// </summary>
    internal static string? WaitingTaskDescription(BatchContext batch, SimulatedDbConnection waiter)
    {
        if (WaitedLock(batch, waiter) is not { } row)
            return null;
        var blocking = waiter.WaitingOnResource is { } resource && FindFirstBlockingHold(resource, waiter) is var (_, blockingMode) ? ModeAbbreviation(blockingMode) : ModeAbbreviation(row.Mode);
        var id = LockBlockId(row.Resource);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        switch (row.Type)
        {
            case "APPLICATION":
                // <principal>:[<name>]:(<hash>) reads as hash=<name><hash>.
                var colon = row.Description.IndexOf(':', StringComparison.Ordinal);
                var close = row.Description.LastIndexOf("]:(", StringComparison.Ordinal);
                var name = row.Description[(colon + 2)..close];
                var hash = row.Description[(close + 3)..^1];
                return string.Create(invariant, $"applicationlock hash={name}{hash} databasePrincipalId={row.Description[..colon]} dbid={row.DatabaseId} id={id} mode={blocking}");
            case "KEY":
                return string.Create(invariant, $"keylock hobtid={row.EntityId} dbid={row.DatabaseId} id={id} mode={blocking} associatedObjectId={row.EntityId}");
            case "OBJECT":
                return string.Create(invariant, $"objectlock lockPartition=0 objid={row.EntityId} subresource=FULL dbid={row.DatabaseId} id={id} mode={blocking} associatedObjectId={row.EntityId}");
            case "PAGE":
                var page = row.Description.Split(':');
                return string.Create(invariant, $"pagelock fileid={page[0]} pageid={page[1]} dbid={row.DatabaseId} subresource=FULL id={id} mode={blocking} associatedObjectId={row.EntityId}");
            case "RID":
                var rid = row.Description.Split(':');
                return string.Create(invariant, $"ridlock fileid={rid[0]} pageid={rid[1]} dbid={row.DatabaseId} id={id} mode={blocking} associatedObjectId={row.EntityId}");
            default:
                return null;
        }
    }

    /// <summary>
    /// The <c>resource_address</c> <c>sys.dm_os_waiting_tasks</c> gives a lock
    /// wait: the waiting request's own <c>lock_owner_address</c> in
    /// <c>sys.dm_tran_locks</c>. Null when the waiter waits on no lock.
    /// </summary>
    internal static byte[]? WaitingTaskResourceAddress(BatchContext batch, SimulatedDbConnection waiter) =>
        WaitedLock(batch, waiter) is { } row ? LockOwnerAddress(row) : null;

    /// <summary>
    /// Reverse-lookup map: for each connection currently waiting, find
    /// the resource it's blocked on. Used by
    /// <see cref="EnumerateDmTranLocks"/> to emit WAIT rows alongside the
    /// GRANT rows for that same resource. A wait for a key another row
    /// carries (<see cref="SessionToken.WaitingOnKey"/>) goes to
    /// <paramref name="keyWaits"/> instead, reported on the key.
    /// </summary>
    private static Dictionary<LockResource, List<SimulatedDbConnection>> SnapshotWaiters(Simulation sim, out List<(SimulatedDbConnection Waiter, LockResource Resource, string Key, int IndexId)>? keyWaits)
    {
        var map = new Dictionary<LockResource, List<SimulatedDbConnection>>(ReferenceEqualityComparer.Instance);
        keyWaits = null;
        foreach (var conn in sim.SnapshotConnections())
        {
            if (conn.WaitingOnResource is not { } resource)
                continue;
            if (conn.WaitRecord is { WaitingOnKey: { } key } record)
            {
                (keyWaits ??= []).Add((conn, resource, key, record.WaitingOnKeyIndexId));
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
    private static IEnumerable<LockRow> EmitRowLocks(
        BatchContext batch,
        HeapTable table,
        int dbId,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitersByResource,
        List<(SimulatedDbConnection Waiter, LockResource Resource, string Key, int IndexId)>? keyWaits,
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded)
    {
        // A row of a clustered table is its clustered key, which is what real
        // locks and reports as KEY; only a heap's row is a RID.
        var locks = batch.Connection.Simulation.LockManager;
        var rowGroup = KeyLockGroup.RowGroupOf(table);
        var rowType = rowGroup is null ? "RID" : "KEY";
        var rowIndexId = rowGroup?.IndexId ?? 0;
        var rowHobt = HobtOf(table, rowIndexId);
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
                : RidDescription(table, address);
            foreach (var row in EmitRowsForResource(locks, rowType, dbId, description, rowHobt, table.ObjectId, rowIndexId, resource, waitersByResource, folded))
            {
                if (rowGroup is null || shown.Add((rowGroup, description, row.Spid, ModeAbbreviation(row.Mode), row.Waiting)))
                    yield return row;
            }
            foreach (var hold in holders)
            {
                if (hold.Mode != LockMode.Exclusive)
                    continue;
                foreach (var (group, key) in IndexKeysWritten(batch, table, rowGroup, address, resource, hold.Owner, live))
                {
                    if (!shown.Add((group, key, hold.Owner.Acting.Spid, ModeAbbreviation(LockMode.Exclusive), false)))
                        continue;
                    yield return new LockRow("KEY", dbId, key, HobtOf(table, group.IndexId), table.ObjectId, group.IndexId, LockMode.Exclusive, waiting: false, hold.Owner.Acting.Spid, hold.Owner, resource);
                }
            }
        }

        if (keyWaits is null)
            yield break;
        foreach (var (waiter, resource, key, keyIndexId) in keyWaits)
        {
            if (!ReferenceEquals(resource.OwningTable, table) || waiter.WaitingForMode is not { } waitMode)
                continue;
            yield return new LockRow("KEY", dbId, key, HobtOf(table, keyIndexId), table.ObjectId, keyIndexId, waitMode, waiting: true, waiter.Spid, waiter.LockOwner, resource);
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

    private static IEnumerable<LockRow> EmitRowsForResource(
        LockManager locks,
        string type,
        int dbId,
        string description,
        long entityId,
        int objectId,
        int indexId,
        LockResource resource,
        Dictionary<LockResource, List<SimulatedDbConnection>> waitersByResource,
        Dictionary<(LockResource, SessionToken, LockMode), LockMode?>? folded = null)
    {
        // Empty-resource fast path: nothing held or waiting → no rows.
        if (resource.Holders.Count == 0 && !waitersByResource.ContainsKey(resource))
            yield break;
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
            yield return new LockRow(type, dbId, description, entityId, objectId, indexId, mode, waiting: false, hold.Owner.Acting.Spid, hold.Owner, resource);
        }
        // WAIT rows from connections blocked on this resource.
        if (waitersByResource.TryGetValue(resource, out var waiters))
        {
            foreach (var waiter in waiters)
            {
                if (waiter.WaitingForMode is not { } waitMode)
                    continue;
                yield return new LockRow(type, dbId, description, entityId, objectId, indexId, waitMode, waiting: true, waiter.Spid, waiter.LockOwner, resource);
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
    internal static int? FindFirstBlocker(LockResource resource, SimulatedDbConnection waiter) =>
        FindFirstBlockingHold(resource, waiter) is var (spid, _) ? spid : null;

    /// <summary>
    /// <see cref="FindFirstBlocker"/>'s session with the mode of the hold or
    /// queued request that blocks, which real's wait description names.
    /// </summary>
    internal static (int Spid, LockMode Mode)? FindFirstBlockingHold(LockResource resource, SimulatedDbConnection waiter)
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
            return (hold.Owner.Acting.Spid, hold.Mode);
        }
        foreach (var (owner, waiting) in locks.QueuedAheadOf(resource, waiter.LockOwner))
        {
            if (!LockManager.IsCompatible(waiting, mode))
                return (owner.Acting.Spid, waiting);
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
    private static IEnumerable<LockRow> EmitPageLocks(
        LockManager locks, HeapTable table, int dbId, Dictionary<LockResource, List<SimulatedDbConnection>> waitsByResource)
    {
        var pages = table.PageLocks.ToArray();
        Array.Sort(pages, static (a, b) => a.Key.CompareTo(b.Key));
        var indexId = BaseIndexIdOf(table);
        var hobt = HobtOf(table, indexId);
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
            foreach (var row in EmitRowsForResource(locks, "PAGE", dbId, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1:{FirstPageId + page}"), hobt, table.ObjectId, indexId, resource, waitsByResource, intents))
                yield return row;
        }
    }

    /// <summary>
    /// A heap row's <c>RID</c> description, <c>file:page:slot</c>, on the
    /// page real's layout puts it on, numbered as the <c>PAGE</c> locks
    /// number theirs (probed 2026-10-16 against SQL Server 2025:
    /// <c>1:552:1</c> beside its page's <c>1:552</c>).
    /// </summary>
    private static string RidDescription(HeapTable table, (int PageIndex, int SlotIndex) address) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1:{FirstPageId + RealPageLayout.For(table).PageOf(address)}:{address.SlotIndex}");

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
}
