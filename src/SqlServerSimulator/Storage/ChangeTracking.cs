using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>What one write did to a tracked row, as <c>SYS_CHANGE_OPERATION</c> spells it.</summary>
internal enum ChangeTrackingOperation : byte
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// The unit of <c>CHANGE_RETENTION</c>, numbered as
/// <c>sys.change_tracking_databases.retention_period_units</c> reports it.
/// </summary>
internal enum ChangeRetentionUnit : byte
{
    Minutes = 1,
    Hours = 2,
    Days = 3,
}

/// <summary>
/// A database's change tracking settings, as <c>ALTER DATABASE … SET
/// CHANGE_TRACKING</c> left them. Retention is recorded and reported but
/// never enforced: nothing is cleaned up, so a table's minimum valid version
/// moves only when tracking restarts or the table is truncated.
/// </summary>
internal sealed class DatabaseChangeTracking
{
    // A fresh setting keeps two days with cleanup on (probed 2026-09-27
    // against SQL Server 2025).
    public int RetentionPeriod = 2;

    public ChangeRetentionUnit RetentionUnit = ChangeRetentionUnit.Days;

    public bool AutoCleanup = true;
}

/// <summary>
/// One tracked table's committed change history: per primary-key value, the
/// transactions that changed that row, oldest first. <c>CHANGETABLE</c>
/// folds the entries past a caller's last synchronized version into the net
/// change it reports.
/// </summary>
/// <remarks>
/// A new instance starts every tracking period, so <c>DISABLE</c> then
/// <c>ENABLE</c> forgets the history, as real does. Writes reach here only
/// when their transaction commits (see <see cref="PendingRowChange"/>); the
/// store is shared by every session, so each touch takes its lock.
/// </remarks>
internal sealed class TableChangeTracking(bool trackColumnsUpdated, long minValidVersion)
{
    /// <summary><c>TRACK_COLUMNS_UPDATED</c>: whether an update records the columns it set.</summary>
    public readonly bool TrackColumnsUpdated = trackColumnsUpdated;

    /// <summary>
    /// The version tracking began at, which <c>sys.change_tracking_tables</c>
    /// reports as both <c>begin_version</c> and <c>min_valid_version</c>;
    /// truncation moves it to the version current at the time.
    /// </summary>
    public long MinValidVersion = minValidVersion;

    private readonly Dictionary<SqlValueKey, List<CommittedRowChange>> rows = [];

    /// <summary>
    /// Folds one committed write into its row's history: a second write to
    /// the same row in the same transaction merges into that transaction's
    /// entry rather than starting a new one.
    /// </summary>
    public void Apply(PendingRowChange change, long version)
    {
        lock (this.rows)
        {
            var key = new SqlValueKey(change.Key);
            if (!this.rows.TryGetValue(key, out var history))
                this.rows[key] = history = [];
            if (history.Count > 0 && history[^1].Version == version)
                history[^1].Merge(change);
            else
                history.Add(new CommittedRowChange(version, change));
        }
    }

    /// <summary>Forgets every committed change, as a truncation does.</summary>
    public void Reset(long minValidVersion)
    {
        lock (this.rows)
        {
            this.rows.Clear();
            this.MinValidVersion = minValidVersion;
        }
    }

    /// <summary>
    /// The net change per row since <paramref name="lastSyncVersion"/> (every
    /// change when null), as <c>CHANGETABLE(CHANGES …)</c> reports it.
    /// </summary>
    /// <remarks>
    /// The rules, all probed 2026-09-27 against SQL Server 2025: the row's
    /// last change in range is a delete → <c>D</c>; else its first change in
    /// range is an insert → <c>I</c>; else <c>U</c>, which is also what a
    /// delete followed by a re-insert nets to. The creation version is the
    /// latest in-range insert's, and the columns mask survives only a pure
    /// run of column-recording updates.
    /// </remarks>
    public List<NetRowChange> NetChanges(long? lastSyncVersion)
    {
        var result = new List<NetRowChange>();
        lock (this.rows)
        {
            foreach (var history in this.rows.Values)
            {
                var first = 0;
                if (lastSyncVersion is { } since)
                {
                    while (first < history.Count && history[first].Version <= since)
                        first++;
                }
                if (first == history.Count)
                    continue;

                var last = history[^1];
                long? creation = null;
                List<int>? columns = [];
                for (var i = first; i < history.Count; i++)
                {
                    var entry = history[i];
                    if (entry.Inserted)
                        creation = entry.Version;
                    if (entry.Columns is null || columns is null)
                    {
                        columns = null;
                        continue;
                    }
                    foreach (var columnId in entry.Columns)
                    {
                        if (!columns.Contains(columnId))
                            columns.Add(columnId);
                    }
                }

                var operation = last.Last == ChangeTrackingOperation.Delete ? ChangeTrackingOperation.Delete
                    : history[first].First == ChangeTrackingOperation.Insert ? ChangeTrackingOperation.Insert
                    : ChangeTrackingOperation.Update;
                // A key written again in another spelling its collation calls
                // equal keeps the spelling the row's tracking began with (probed
                // 2026-10-04 against SQL Server 2025).
                result.Add(new NetRowChange(history[0].Key, last.Version, creation, operation,
                    operation == ChangeTrackingOperation.Update && this.TrackColumnsUpdated ? columns : null, last.Context));
            }
        }
        return result;
    }

    /// <summary>
    /// The latest committed change to the row whose key is
    /// <paramref name="key"/>, as <c>CHANGETABLE(VERSION …)</c> reports it;
    /// null when the row hasn't changed since tracking began.
    /// </summary>
    public (long Version, byte[]? Context)? LatestChange(SqlValueKey key)
    {
        lock (this.rows)
        {
            return this.rows.TryGetValue(key, out var history) && history.Count > 0
                ? (history[^1].Version, history[^1].Context)
                : null;
        }
    }

    /// <summary>
    /// The column ids an <c>UPDATE</c> setting <paramref name="setOrdinals"/>
    /// records, or null for "every column" — which is what an update setting
    /// a key column, or every column outside the key, records (probed
    /// 2026-09-27 against SQL Server 2025). The ids follow the <c>SET</c>
    /// list's order, then any <c>rowversion</c> column the write stamps.
    /// </summary>
    public int[]? UpdatedColumns(HeapTable table, int[] keyOrdinals, IReadOnlyList<int> setOrdinals)
    {
        if (!this.TrackColumnsUpdated)
            return null;
        var ids = new List<int>(setOrdinals.Count + 1);
        foreach (var ordinal in setOrdinals)
        {
            if (Array.IndexOf(keyOrdinals, ordinal) >= 0)
                return null;
            if (!ids.Contains(table.Columns[ordinal].ColumnId))
                ids.Add(table.Columns[ordinal].ColumnId);
        }
        foreach (var column in table.Columns)
        {
            if (column.Type == SqlType.RowVersion && !ids.Contains(column.ColumnId))
                ids.Add(column.ColumnId);
        }
        return ids.Count >= table.Columns.Length - keyOrdinals.Length ? null : [.. ids];
    }

    /// <summary>
    /// The primary key's positions in <see cref="HeapTable.Columns"/>, read
    /// afresh per statement since <c>ALTER TABLE</c> can move columns.
    /// </summary>
    public static int[] KeyOrdinals(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.Kind == KeyConstraintKind.PrimaryKey)
                return key.FullOrdinals;
        }
        return [];
    }

    /// <summary>The key columns of <paramref name="row"/>, a full-width row of the table.</summary>
    public static SqlValue[] KeyOf(SqlValue[] row, int[] keyOrdinals)
    {
        var key = new SqlValue[keyOrdinals.Length];
        for (var i = 0; i < key.Length; i++)
            key[i] = row[keyOrdinals[i]];
        return key;
    }

    /// <summary>
    /// Records one row written by the statement <paramref name="batch"/> runs.
    /// The change joins the statement's undo log, which is what drops it on
    /// rollback and publishes it, versioned, on commit.
    /// </summary>
    public void Record(BatchContext batch, HeapTable table, SqlValue[] key, ChangeTrackingOperation operation, int[]? columns) =>
        batch.CurrentUndoLog?.RecordChangeTracking(new PendingRowChange(this, batch.DatabaseFor(table), key, operation, columns, batch.CurrentStatement.ChangeTrackingContext));

    /// <summary>Records the insert or delete of <paramref name="row"/>, a full-width row of the table.</summary>
    public void RecordRow(BatchContext batch, HeapTable table, SqlValue[] row, ChangeTrackingOperation operation) =>
        this.Record(batch, table, KeyOf(row, KeyOrdinals(table)), operation, null);

    /// <summary>Whether an update setting <paramref name="setOrdinals"/> writes a key column, which makes each row it changes a key move.</summary>
    public static bool SetsKey(int[] keyOrdinals, IReadOnlyList<int> setOrdinals)
    {
        foreach (var ordinal in setOrdinals)
        {
            if (Array.IndexOf(keyOrdinals, ordinal) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Records an <c>UPDATE</c> of one row. A row whose key the update set —
    /// to a new value or to the one it had (probed 2026-10-04 against SQL
    /// Server 2025: <c>SET id = id</c> reports a new creation version) — is a
    /// delete of the old key and an insert of the new one, which the caller
    /// hands back through <paramref name="keyMoves"/> so a statement records
    /// every such delete ahead of every such insert — one row can move onto a
    /// key another row is leaving.
    /// </summary>
    public void RecordUpdate(BatchContext batch, HeapTable table, int[] keyOrdinals, SqlValue[] oldRow, SqlValue[] newRow, int[]? columns, bool setsKey, ref List<(SqlValue[] OldKey, SqlValue[] NewKey)>? keyMoves)
    {
        var oldKey = KeyOf(oldRow, keyOrdinals);
        var newKey = KeyOf(newRow, keyOrdinals);
        if (!setsKey && new SqlValueKey(oldKey).Equals(new SqlValueKey(newKey)))
            this.Record(batch, table, oldKey, ChangeTrackingOperation.Update, columns);
        else
            (keyMoves ??= []).Add((oldKey, newKey));
    }

    /// <summary>Records the key moves <see cref="RecordUpdate"/> collected, deletes first.</summary>
    public void RecordKeyMoves(BatchContext batch, HeapTable table, List<(SqlValue[] OldKey, SqlValue[] NewKey)>? keyMoves)
    {
        if (keyMoves is null)
            return;
        foreach (var (oldKey, _) in keyMoves)
            this.Record(batch, table, oldKey, ChangeTrackingOperation.Delete, null);
        foreach (var (_, newKey) in keyMoves)
            this.Record(batch, table, newKey, ChangeTrackingOperation.Insert, null);
    }
}

/// <summary>
/// A tracked write not yet committed: an entry in its transaction's undo log
/// until the commit gives it a version.
/// </summary>
internal sealed class PendingRowChange(TableChangeTracking tracking, Database database, SqlValue[] key, ChangeTrackingOperation operation, int[]? columns, byte[]? context)
{
    public readonly TableChangeTracking Tracking = tracking;

    /// <summary>The database whose version counter the commit draws from.</summary>
    public readonly Database Database = database;

    public readonly SqlValue[] Key = key;

    public readonly ChangeTrackingOperation Operation = operation;

    /// <summary>An update's column ids; null for every column, and for inserts and deletes.</summary>
    public readonly int[]? Columns = columns;

    /// <summary>The statement's <c>WITH CHANGE_TRACKING_CONTEXT</c> value.</summary>
    public readonly byte[]? Context = context;
}

/// <summary>
/// One transaction's changes to one row: the first and last operations it
/// performed, whether it inserted the row, the columns its updates set, and
/// the context of its last statement to touch the row (probed 2026-09-27
/// against SQL Server 2025, where a later context-less statement clears an
/// earlier one's).
/// </summary>
internal sealed class CommittedRowChange(long version, PendingRowChange change)
{
    public readonly long Version = version;

    public readonly ChangeTrackingOperation First = change.Operation;

    public SqlValue[] Key = change.Key;

    public ChangeTrackingOperation Last = change.Operation;

    public bool Inserted = change.Operation == ChangeTrackingOperation.Insert;

    /// <summary>Column ids in first-set order; null once any write in the entry covered every column.</summary>
    public List<int>? Columns = change.Operation == ChangeTrackingOperation.Update && change.Columns is { } columns ? [.. columns] : null;

    public byte[]? Context = change.Context;

    public void Merge(PendingRowChange change)
    {
        this.Key = change.Key;
        this.Last = change.Operation;
        this.Inserted |= change.Operation == ChangeTrackingOperation.Insert;
        this.Context = change.Context;
        if (this.Columns is null)
            return;
        if (change.Operation != ChangeTrackingOperation.Update || change.Columns is null)
        {
            this.Columns = null;
            return;
        }
        foreach (var columnId in change.Columns)
        {
            if (!this.Columns.Contains(columnId))
                this.Columns.Add(columnId);
        }
    }
}

/// <summary>One row of <c>CHANGETABLE(CHANGES …)</c> before projection.</summary>
internal readonly struct NetRowChange(SqlValue[] key, long version, long? creationVersion, ChangeTrackingOperation operation, List<int>? columns, byte[]? context)
{
    public readonly SqlValue[] Key = key;
    public readonly long Version = version;
    public readonly long? CreationVersion = creationVersion;
    public readonly ChangeTrackingOperation Operation = operation;
    public readonly List<int>? Columns = columns;
    public readonly byte[]? Context = context;
}
