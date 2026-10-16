using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Backup;

namespace SqlServerSimulator;

// The history msdb keeps of each backup and restore, in the rows SQL Server
// 2025 writes (probed 2026-10-16): a backupmediaset and backupmediafamily row
// per media set, which every set appended to it shares; a backupset row per
// set, with a backupfile row per database file and a backupfilegroup row per
// filegroup; and a restorehistory row per restore, with restorefile and
// restorefilegroup rows beside it.
partial class Simulation
{
    /// <summary>The software that wrote a backup, as the history and <c>LABELONLY</c> name it.</summary>
    internal const string BackupSoftwareName = "Microsoft SQL Server";

    /// <summary>Microsoft's vendor id in the Tape Format, every set's <c>software_vendor_id</c>.</summary>
    internal const int BackupSoftwareVendorId = 4608;

    /// <summary>Records <paramref name="set"/>, just written at <paramref name="position"/> of <paramref name="media"/>, in msdb.</summary>
    private void RecordBackupHistory(BatchContext batch, BackupMedia media, BackupSet set, string devicePath, int position)
    {
        var mediaSetId = this.MediaSetHistoryId(batch, media, devicePath);
        var backupSetId = this.InsertMsdbRow(batch, "backupset", row =>
        {
            row["backup_set_uuid"] = SqlValue.FromGuid(set.BackupSetGuid);
            row["media_set_id"] = SqlValue.FromInt32(mediaSetId);
            foreach (var column in (ReadOnlySpan<string>)["first_family_number", "last_family_number", "catalog_family_number"])
                row[column] = SqlValue.FromByte(1);
            foreach (var column in (ReadOnlySpan<string>)["first_media_number", "last_media_number", "catalog_media_number"])
                row[column] = SqlValue.FromInt16(1);
            row["position"] = SqlValue.FromInt32(position);
            row["software_vendor_id"] = SqlValue.FromInt32(BackupSoftwareVendorId);
            row["name"] = Text(set.Name);
            row["description"] = Text(set.Description);
            row["user_name"] = SqlValue.FromNVarchar(set.UserName);
            row["software_major_version"] = SqlValue.FromByte(17);
            row["software_minor_version"] = SqlValue.FromByte(0);
            row["software_build_version"] = SqlValue.FromInt16(4065);
            row["time_zone"] = SqlValue.FromInt16(0);
            row["mtf_minor_version"] = SqlValue.FromByte(0);
            row["first_lsn"] = Lsn(set.FirstLsnBlock);
            row["last_lsn"] = Lsn(set.LastLsnBlock);
            row["checkpoint_lsn"] = Lsn(set.CheckpointLsnBlock);
            row["database_backup_lsn"] = set.DatabaseBackupLsnBlock == 0 ? SqlValue.FromDecimal(LsnType, 0) : Lsn(set.DatabaseBackupLsnBlock);
            row["database_creation_date"] = SqlValue.FromDateTime(BuiltInResources.SysDatabasesCreateDate);
            row["backup_start_date"] = SqlValue.FromDateTime(WholeSeconds(set.StartUtc));
            row["backup_finish_date"] = SqlValue.FromDateTime(WholeSeconds(set.FinishUtc));
            row["type"] = SqlValue.FromString(CharSqlType.Get(1, Collation.Baseline, Coercibility.Implicit), set.Type switch
            {
                BackupSet.LogType => "L",
                BackupSet.DifferentialType => "I",
                _ => "D",
            });
            row["sort_order"] = SqlValue.FromInt16(52);
            row["code_page"] = SqlValue.FromInt16(0);
            row["compatibility_level"] = SqlValue.FromByte(set.CompatibilityLevel);
            row["database_version"] = SqlValue.FromInt32(998);
            row["backup_size"] = SqlValue.FromDecimal(SizeType, set.BackupSize);
            row["database_name"] = SqlValue.FromNVarchar(set.DatabaseName);
            row["server_name"] = SqlValue.FromNVarchar(Network.TdsSession.ServerName);
            row["machine_name"] = SqlValue.FromNVarchar(Network.TdsSession.ServerName);
            row["flags"] = SqlValue.FromInt32(set.Flags);
            row["unicode_locale"] = SqlValue.FromInt32(1033);
            row["unicode_compare_style"] = SqlValue.FromInt32(196609);
            row["collation_name"] = SqlValue.FromNVarchar(set.Collation);
            row["is_password_protected"] = SqlValue.FromBoolean(false);
            row["recovery_model"] = SqlValue.FromNVarchar(set.RecoveryModel);
            foreach (var column in (ReadOnlySpan<string>)["has_bulk_logged_data", "is_snapshot", "is_damaged", "has_incomplete_metadata"])
                row[column] = SqlValue.FromBoolean(false);
            row["is_force_offline"] = SqlValue.FromBoolean(set.IsForceOffline);
            row["is_readonly"] = SqlValue.FromBoolean(set.IsReadOnly);
            row["is_single_user"] = SqlValue.FromBoolean(set.IsSingleUser);
            row["has_backup_checksums"] = SqlValue.FromBoolean(set.HasChecksums);
            row["begins_log_chain"] = SqlValue.FromBoolean(set.BeginsLogChain);
            row["is_copy_only"] = SqlValue.FromBoolean(set.IsCopyOnly);
            row["first_recovery_fork_guid"] = SqlValue.FromGuid(set.RecoveryForkGuid);
            row["last_recovery_fork_guid"] = SqlValue.FromGuid(set.RecoveryForkGuid);
            row["database_guid"] = SqlValue.FromGuid(set.DatabaseGuid);
            row["family_guid"] = SqlValue.FromGuid(set.RecoveryForkGuid);
            if (set.DifferentialBaseBlock is { } differentialBase)
            {
                row["differential_base_lsn"] = Lsn(differentialBase);
                row["differential_base_guid"] = SqlValue.FromGuid(set.DifferentialBaseGuid!.Value);
            }
            row["compressed_backup_size"] = SqlValue.FromDecimal(SizeType, set.BackupSize);
            if (set.Type == BackupSet.LogType)
                row["last_valid_restore_time"] = SqlValue.FromDateTime(WholeSeconds(set.FinishUtc));
        });
        var filePages = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (logicalName, pages) in set.FilePages)
            filePages[logicalName] = pages;
        var filegroups = new SortedDictionary<int, string>();
        foreach (var file in set.Files)
        {
            var pages = filePages.GetValueOrDefault(file.LogicalName);
            // A data file reports the pages of its own extents, a log file the log it carried.
            var backedUp = file.IsLog ? pages : set.Type == BackupSet.LogType ? 0 : 16;
            _ = this.InsertMsdbRow(batch, "backupfile", row =>
            {
                row["backup_set_id"] = SqlValue.FromInt32(backupSetId);
                row["first_family_number"] = SqlValue.FromByte(1);
                row["first_media_number"] = SqlValue.FromInt16(1);
                if (!file.IsLog)
                {
                    row["filegroup_name"] = Text(file.FileGroupName);
                    row["page_size"] = SqlValue.FromInt32(8192);
                }
                row["file_number"] = SqlValue.FromDecimal(FileNumberType, file.FileId);
                row["backed_up_page_count"] = SqlValue.FromDecimal(FileNumberType, backedUp);
                row["file_type"] = SqlValue.FromString(CharSqlType.Get(1, Collation.Baseline, Coercibility.Implicit), file.IsLog ? "L" : "D");
                row["source_file_block_size"] = SqlValue.FromDecimal(FileNumberType, 4096);
                row["file_size"] = SqlValue.FromDecimal(SizeType, file.SizeBytes);
                row["logical_name"] = SqlValue.FromNVarchar(file.LogicalName);
                row["physical_drive"] = SqlValue.FromNVarchar(@"C:\");
                row["physical_name"] = SqlValue.FromNVarchar(file.PhysicalName);
                row["state"] = SqlValue.FromByte(0);
                row["state_desc"] = SqlValue.FromNVarchar("ONLINE");
                row["create_lsn"] = SqlValue.FromDecimal(LsnType, 0);
                row["file_guid"] = SqlValue.FromGuid(file.UniqueId);
                if (set.Type == BackupSet.DifferentialType)
                    row["differential_base_lsn"] = file.IsLog ? SqlValue.FromDecimal(LsnType, 0) : Lsn(set.DifferentialBaseBlock!.Value);
                // A log file's last page is half written (probed 2026-10-16 against SQL Server 2025).
                row["backup_size"] = SqlValue.FromDecimal(SizeType, file.IsLog ? (backedUp * 8192L) - 4096 : backedUp * 8192L);
                row["is_readonly"] = SqlValue.FromBoolean(false);
                row["is_present"] = SqlValue.FromBoolean(true);
            });
            if (!file.IsLog && file.FileGroupName is { } filegroup)
                filegroups[file.FileGroupId] = filegroup;
        }
        foreach (var (id, name) in filegroups)
        {
            _ = this.InsertMsdbRow(batch, "backupfilegroup", row =>
            {
                row["backup_set_id"] = SqlValue.FromInt32(backupSetId);
                row["name"] = SqlValue.FromNVarchar(name);
                row["filegroup_id"] = SqlValue.FromInt32(id);
                row["type"] = SqlValue.FromString(CharSqlType.Get(2, Collation.Baseline, Coercibility.Implicit), "FG");
                row["type_desc"] = SqlValue.FromNVarchar("ROWS_FILEGROUP");
                row["is_default"] = SqlValue.FromBoolean(id == Database.PrimaryFilegroupId);
                row["is_readonly"] = SqlValue.FromBoolean(false);
            });
        }
    }

    /// <summary>
    /// The <c>media_set_id</c> of <paramref name="media"/>'s history row: the
    /// one its earlier sets recorded, or a new <c>backupmediaset</c> row with
    /// its one <c>backupmediafamily</c> row.
    /// </summary>
    private int MediaSetHistoryId(BatchContext batch, BackupMedia media, string devicePath)
    {
        var table = this.MsdbTable("backupmediaset");
        var uuid = MsdbOrdinal(table, "media_uuid");
        var id = MsdbOrdinal(table, "media_set_id");
        foreach (var row in table.Heap.EnumerateRows())
        {
            var values = RowDecoder.DecodeRow(table.StoredColumns, row, table.Heap);
            if (!values[uuid].IsNull && values[uuid].AsGuid == media.MediaSetGuid)
                return values[id].AsInt32;
        }
        var mediaSetId = this.InsertMsdbRow(batch, "backupmediaset", row =>
        {
            row["media_uuid"] = SqlValue.FromGuid(media.MediaSetGuid);
            row["media_family_count"] = SqlValue.FromByte(1);
            row["name"] = Text(media.MediaName);
            row["description"] = Text(media.MediaDescription);
            row["software_name"] = SqlValue.FromNVarchar(BackupSoftwareName);
            row["software_vendor_id"] = SqlValue.FromInt32(BackupSoftwareVendorId);
            row["MTF_major_version"] = SqlValue.FromByte(1);
            row["mirror_count"] = SqlValue.FromByte(1);
            row["is_password_protected"] = SqlValue.FromBoolean(false);
            row["is_compressed"] = SqlValue.FromBoolean(false);
        });
        _ = this.InsertMsdbRow(batch, "backupmediafamily", row =>
        {
            row["media_set_id"] = SqlValue.FromInt32(mediaSetId);
            row["family_sequence_number"] = SqlValue.FromByte(1);
            row["media_family_id"] = SqlValue.FromGuid(media.MediaFamilyGuid);
            row["media_count"] = SqlValue.FromInt32(1);
            row["physical_device_name"] = SqlValue.FromNVarchar(devicePath);
            row["device_type"] = SqlValue.FromByte(2);
            row["physical_block_size"] = SqlValue.FromInt32(4096);
            row["mirror"] = SqlValue.FromByte(0);
        });
        return mediaSetId;
    }

    /// <summary>
    /// Records a restore of <paramref name="set"/> into
    /// <paramref name="databaseName"/> — <c>restore_type</c> D for a database,
    /// L for a log, V for a <c>VERIFYONLY … LOADHISTORY</c> — with the set's
    /// own history when msdb holds none of it yet.
    /// </summary>
    private void RecordRestoreHistory(BatchContext batch, BackupMedia media, BackupSet set, string devicePath, int position, string? databaseName, char type, bool replace, Dictionary<string, string>? physicalNames,
        bool recovered = true, DateTime? stopAt = null, string? markName = null, bool? stopBefore = null)
    {
        var backupSetId = this.BackupSetHistoryId(set.BackupSetGuid);
        if (backupSetId < 0)
        {
            this.RecordBackupHistory(batch, media, set, devicePath, position);
            backupSetId = this.BackupSetHistoryId(set.BackupSetGuid);
        }
        var restoreId = this.InsertMsdbRow(batch, "restorehistory", row =>
        {
            row["restore_date"] = SqlValue.FromDateTime(batch.CurrentStatement.UtcNow);
            row["destination_database_name"] = Text(databaseName ?? set.DatabaseName);
            row["user_name"] = SqlValue.FromNVarchar(batch.Connection.Security.Effective.LoginName);
            row["backup_set_id"] = SqlValue.FromInt32(backupSetId);
            row["restore_type"] = SqlValue.FromString(CharSqlType.Get(1, Collation.Baseline, Coercibility.Implicit), type.ToString());
            row["replace"] = SqlValue.FromBoolean(replace);
            row["recovery"] = SqlValue.FromBoolean(recovered);
            row["restart"] = SqlValue.FromBoolean(false);
            row["device_count"] = SqlValue.FromByte(1);
            if (stopAt is { } at)
                row["stop_at"] = SqlValue.FromDateTime(at);
            if (markName is not null)
                row["stop_at_mark_name"] = SqlValue.FromNVarchar(markName);
            if (stopBefore is { } before)
                row["stop_before"] = SqlValue.FromBoolean(before);
        });
        if (type == 'V')
            return;
        var filegroups = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in set.Files)
        {
            var path = physicalNames?.GetValueOrDefault(file.LogicalName) ?? file.PhysicalName;
            _ = this.InsertMsdbRow(batch, "restorefile", row =>
            {
                row["restore_history_id"] = SqlValue.FromInt32(restoreId);
                row["file_number"] = SqlValue.FromDecimal(FileNumberType, file.FileId);
                row["destination_phys_drive"] = SqlValue.FromNVarchar(@"C:\");
                row["destination_phys_name"] = SqlValue.FromNVarchar(path);
            });
            if (file.FileGroupName is { } filegroup)
                _ = filegroups.Add(filegroup);
        }
        foreach (var filegroup in filegroups)
        {
            _ = this.InsertMsdbRow(batch, "restorefilegroup", row =>
            {
                row["restore_history_id"] = SqlValue.FromInt32(restoreId);
                row["filegroup_name"] = SqlValue.FromNVarchar(filegroup);
            });
        }
    }

    /// <summary>The <c>backup_set_id</c> msdb gave the set <paramref name="guid"/> names, -1 for none.</summary>
    private int BackupSetHistoryId(Guid guid)
    {
        var table = this.MsdbTable("backupset");
        var uuid = MsdbOrdinal(table, "backup_set_uuid");
        var id = MsdbOrdinal(table, "backup_set_id");
        foreach (var row in table.Heap.EnumerateRows())
        {
            var values = RowDecoder.DecodeRow(table.StoredColumns, row, table.Heap);
            if (values[uuid].AsGuid == guid)
                return values[id].AsInt32;
        }
        return -1;
    }

    /// <summary>The ordinal of msdb table <paramref name="table"/>'s column <paramref name="column"/>, whose columns are all stored.</summary>
    private static int MsdbOrdinal(HeapTable table, string column) =>
        Array.FindIndex(table.StoredColumns, stored => stored.Name == column) is var ordinal and >= 0 ? ordinal : throw new InvalidOperationException(column);

    private HeapTable MsdbTable(string name) => this.Databases[MsdbDatabaseName].Schemas[Database.DefaultSchemaName].HeapTables[name];

    /// <summary>
    /// Inserts a row into msdb's <paramref name="tableName"/>, the cells
    /// <paramref name="fill"/> names set, the identity column drawn and every
    /// other cell NULL; returns the identity drawn, or 0 for a table without one.
    /// </summary>
    private int InsertMsdbRow(BatchContext batch, string tableName, Action<MsdbRow> fill)
    {
        var table = this.MsdbTable(tableName);
        var (values, identity) = MsdbRowValues(table, fill);
        _ = InsertRow(batch, table, RowEncoder.EncodeRow(table.StoredColumns, values, table.Heap), undoLog: null);
        return identity;
    }

    /// <summary>
    /// A committing marked transaction's <c>logmarkhistory</c> row for each
    /// database it wrote to, written as the commit lands — outside any
    /// statement, so straight into the heap (probed 2026-10-16 against SQL
    /// Server 2025: a mark whose transaction rolled back or wrote nothing
    /// leaves no row).
    /// </summary>
    internal void RecordLogMarks(SimulatedDbTransaction transaction)
    {
        var table = this.MsdbTable("logmarkhistory");
        foreach (var database in transaction.UndoLog.WrittenDatabases())
        {
            if (BuiltInToken.EqualsAny(database.Name, TempdbDatabaseName, MsdbDatabaseName))
                continue;
            var (values, _) = MsdbRowValues(table, row =>
            {
                row["database_name"] = SqlValue.FromNVarchar(database.Name);
                row["mark_name"] = SqlValue.FromNVarchar(transaction.MarkName ?? "");
                row["description"] = Text(transaction.MarkDescription);
                row["user_name"] = SqlValue.FromNVarchar(transaction.Owner.Security.Effective.LoginName);
                row["lsn"] = Lsn(database.BackupChain.LogBlock + 1);
                row["mark_time"] = SqlValue.FromDateTime(transaction.MarkUtc);
            });
            _ = table.Heap.Insert(RowEncoder.EncodeRow(table.StoredColumns, values, table.Heap));
        }
    }

    /// <summary>The stored values of a row <paramref name="fill"/> names cells of, with the identity it drew (0 for none).</summary>
    private static (SqlValue[] Values, int Identity) MsdbRowValues(HeapTable table, Action<MsdbRow> fill)
    {
        var row = new MsdbRow(table);
        fill(row);
        var identity = 0;
        var values = new SqlValue[table.StoredColumns.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var column = table.StoredColumns[i];
            if (column.Identity is { } state)
            {
                identity = (int)state.GenerateNext(column.Type);
                values[i] = SqlValue.FromInt32(identity);
            }
            else
            {
                values[i] = row.Values[i] is { } value ? value.CoerceTo(column.Type) : SqlValue.Null(column.Type);
            }
        }
        return (values, identity);
    }

    /// <summary>The cells of one msdb row an <see cref="InsertMsdbRow"/> fills, by column name.</summary>
    private sealed class MsdbRow(HeapTable table)
    {
        public readonly SqlValue?[] Values = new SqlValue?[table.StoredColumns.Length];

        public SqlValue this[string column]
        {
            set => this.Values[MsdbOrdinal(table, column)] = value;
        }
    }

    private static readonly SqlType LsnType = SqlType.GetDecimal(25, 0);

    private static readonly SqlType SizeType = SqlType.GetDecimal(20, 0);

    private static readonly SqlType FileNumberType = SqlType.GetDecimal(10, 0);

    private static SqlValue Lsn(long block) => SqlValue.FromDecimal(LsnType, DatabaseBackupChain.Lsn(block));

    private static SqlValue Text(string? value) => value is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(value);

    /// <summary>A time cut to the second, as msdb's backup history records it.</summary>
    private static DateTime WholeSeconds(DateTime value) => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);
}
