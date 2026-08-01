using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Row generators for the phase-3 MVCC observability DMVs:
/// <c>sys.dm_tran_version_store</c>, <c>sys.dm_tran_version_store_space_usage</c>,
/// and <c>sys.dm_tran_active_snapshot_database_transactions</c>. All three
/// project from live per-database state (the per-table
/// <see cref="HeapTable.RowVersions"/> dicts and
/// <see cref="Simulation.ActiveSnapshotTxs"/>) at iteration time — no
/// caching. The version-store DMV walks every committed
/// <see cref="HistoricalVersion"/>; pending HVs (those marked with
/// <see cref="VersionStore.PendingXmax"/>) are excluded since they don't
/// represent a finalized version yet.
/// </summary>
internal static class VersionStoreDmvs
{
    /// <summary>
    /// Yields one row per finalized <see cref="HistoricalVersion"/> across
    /// every per-table chain in the current database. The
    /// <c>version_sequence_num</c> column synthesizes per-tx sub-sequence
    /// (1, 2, 3 …) by grouping HVs in result order whose <c>Xmax</c>
    /// matches; real SQL Server's version_sequence_num is the in-tx
    /// version index assigned at write time. The synthesis matches the
    /// observable behavior — same tx → contiguous numbering — without
    /// adding per-HV storage.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranVersionStore(BatchContext batch, Database database)
    {
        _ = batch;
        var dbId = SqlValue.FromInt16(1);
        var zeroByte = SqlValue.FromByte(0);
        var zeroSmallInt = SqlValue.FromInt16(0);
        var nullVarbinary = SqlValue.Null(VarbinarySqlType.MaxForm);
        var perTxCounter = new Dictionary<long, int>();
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.RowVersions.IsEmpty)
                    continue;
                var rowsetId = SqlValue.FromInt64(table.ObjectId);
                foreach (var kv in table.RowVersions)
                {
                    for (var hv = kv.Value.Head; hv is not null; hv = hv.Next)
                    {
                        if (hv.Xmax == VersionStore.PendingXmax)
                            continue;
                        _ = perTxCounter.TryGetValue(hv.Xmax, out var seq);
                        seq++;
                        perTxCounter[hv.Xmax] = seq;
                        var payloadLen = (short)Math.Min(hv.Payload.Length, short.MaxValue);
                        yield return
                        [
                            SqlValue.FromInt64(hv.Xmax),
                            SqlValue.FromInt64(seq),
                            dbId,
                            rowsetId,
                            zeroByte,
                            SqlValue.FromInt16(payloadLen),
                            SqlValue.FromInt16(payloadLen),
                            SqlValue.FromVarbinary(hv.Payload),
                            zeroSmallInt,
                            nullVarbinary,
                        ];
                    }
                }
            }
        }
    }

    /// <summary>
    /// One row per database aggregating finalized-HV payload bytes:
    /// <c>reserved_space_kb</c> = ceil(total_bytes / 1024) and
    /// <c>reserved_page_count</c> = ceil(total_bytes / 8192). Real SQL
    /// Server reports the version-store's *allocated* page count (which
    /// rounds up to whole pages and grows in 64KB chunks); the simulator's
    /// HV payloads aren't backed by pages, so this is a sizing
    /// approximation rather than the underlying buffer-pool figure. Empty
    /// chains yield a zero row so the DMV is never row-empty for a
    /// versioning-enabled database — matching real SQL Server's posture.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranVersionStoreSpaceUsage(BatchContext batch, Database database)
    {
        _ = batch;
        long totalBytes = 0;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.RowVersions.IsEmpty)
                    continue;
                foreach (var kv in table.RowVersions)
                {
                    for (var hv = kv.Value.Head; hv is not null; hv = hv.Next)
                    {
                        if (hv.Xmax == VersionStore.PendingXmax)
                            continue;
                        totalBytes += hv.Payload.Length;
                    }
                }
            }
        }
        yield return
        [
            SqlValue.FromInt32(1),
            SqlValue.FromInt64((totalBytes + 8191) / 8192),
            SqlValue.FromInt64((totalBytes + 1023) / 1024),
        ];
    }

    /// <summary>
    /// One row per active SNAPSHOT-isolation transaction.
    /// <see cref="Simulation.ActiveSnapshotTxs"/> is the canonical
    /// registry — every SI tx that allocated a snapshot Xid via
    /// <see cref="BatchContext.ResolveSnapshotXidForRead"/> is in here
    /// until Commit / Rollback / Dispose. RCSI per-statement snapshots
    /// don't appear (their lifetime is sub-statement and they aren't
    /// tracked; matching real SQL Server's posture for this DMV).
    /// <c>max_version_chain_traversed</c> / <c>average_version_chain_traversed</c>
    /// / <c>elapsed_time_seconds</c> are zero since the simulator doesn't
    /// instrument those metrics.
    /// </summary>
    internal static IEnumerable<SqlValue[]> EnumerateDmTranActiveSnapshotDatabaseTransactions(BatchContext batch, Database database)
    {
        _ = database;
        var trueBit = SqlValue.FromBoolean(true);
        var nullBigInt = SqlValue.Null(SqlType.BigInt);
        var zeroInt = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        var zeroFloat = SqlValue.FromDouble(0);
        foreach (var kv in batch.Connection.Simulation.ActiveSnapshotTxs)
        {
            var tx = kv.Key;
            if (tx.SnapshotXid is not { } xid)
                continue;
            yield return
            [
                SqlValue.FromInt64(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tx)),
                SqlValue.FromInt64(xid),
                nullBigInt,
                SqlValue.FromInt32(tx.Connection.Spid),
                trueBit,
                nullBigInt,
                zeroInt,
                zeroFloat,
                zeroBig,
            ];
        }
    }
}
