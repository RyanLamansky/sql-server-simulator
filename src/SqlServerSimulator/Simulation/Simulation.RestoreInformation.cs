using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Backup;

namespace SqlServerSimulator;

// The RESTORE forms that only read a device — VERIFYONLY, HEADERONLY,
// FILELISTONLY and LABELONLY — probed 2026-10-16 against SQL Server 2025.
partial class Simulation
{
    /// <summary>
    /// <c>RESTORE { VERIFYONLY | HEADERONLY | FILELISTONLY | LABELONLY } FROM
    /// devices [WITH …]</c>, entered on <c>RESTORE</c>; false, the cursor
    /// restored, for every other <c>RESTORE</c>.
    /// </summary>
    /// <remarks>
    /// An option that would change a database is Msg 3032 state 2, with no
    /// Msg 3013 after it, and <c>STATS</c> is one for all but
    /// <c>VERIFYONLY</c>; a device that can't be read raises what
    /// <c>RESTORE DATABASE</c> raises, Msg 3013 naming the statement (<c>VERIFY
    /// DATABASE</c>, <c>RESTORE HEADERONLY</c>, <c>RESTORE FILELIST</c>,
    /// <c>RESTORE LABELONLY</c>), and <c>FILE = n</c> past the last set Msg
    /// 4038. Only <c>VERIFYONLY</c> refuses a transaction (Msg 3021).
    /// </remarks>
    private bool TryParseRestoreInformation(ParserContext context, BatchContext batch, out List<SimulatedStatementOutcome> outcomes)
    {
        outcomes = [];
        var checkpoint = context.SaveCheckpoint();
        var form = context.GetNextRequired() is Name { Value: var word } ? word.ToUpperInvariant() : null;
        if (form is not ("VERIFYONLY" or "HEADERONLY" or "FILELISTONLY" or "LABELONLY"))
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }
        context.RestoreCheckpoint(checkpoint);
        RefuseUnbuiltStatement(context, verb: null);
        _ = context.GetNextRequired();
        var verb = form switch
        {
            "FILELISTONLY" => "RESTORE FILELIST",
            "VERIFYONLY" => "VERIFY DATABASE",
            _ => "RESTORE " + form,
        };
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var devices = ParseBackupDevices(context, batch, verb);
        var refused = new List<string>();
        var options = ParseBackupOptions(context, batch, "RESTORE", verb, refused, takesStats: form == "VERIFYONLY");
        if (batch.IsSkipping)
            return true;
        if (refused.Count > 0)
            throw SimulatedSqlException.RestoreOptionNotSupported(string.Join(", ", refused));
        if (form == "VERIFYONLY" && batch.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.BackupInTransaction(verb);
        var device = SingleDevice(devices, verb);
        var media = this.ReadMediaForRestore(batch, device, verb, options.MediaName);
        switch (form)
        {
            case "FILELISTONLY":
                {
                    var set = SetAt(media, options.File, device, verb);
                    var rows = new List<SqlValue[]>();
                    foreach (var file in set.Files)
                        rows.Add(FileListOnlyRow(set, file));
                    outcomes.Add(new SimulatedSqlResultSet(FileListOnlySchema, FileListOnlyColumnNames, rows));
                    batch.Connection.LastStatementRowCount = rows.Count;
                    break;
                }
            case "HEADERONLY":
                {
                    var rows = new List<SqlValue[]>();
                    for (var position = 1; position <= media.Sets.Count; position++)
                    {
                        if (options.FileWritten && position != options.File)
                            continue;
                        rows.Add(HeaderOnlyRow(media.Sets[position - 1], position));
                    }
                    if (options.FileWritten && options.File > media.Sets.Count)
                        throw SimulatedSqlException.BackupFileIdNotFound(options.File, device.Display, verb);
                    outcomes.Add(new SimulatedSqlResultSet(HeaderOnlySchema, HeaderOnlyColumnNames, rows));
                    batch.Connection.LastStatementRowCount = rows.Count;
                    break;
                }
            case "LABELONLY":
                outcomes.Add(new SimulatedSqlResultSet(LabelOnlySchema, LabelOnlyColumnNames, [LabelOnlyRow(media)]));
                batch.Connection.LastStatementRowCount = 1;
                break;
            default:
                {
                    var set = SetAt(media, options.File, device, verb);
                    if (options.Checksum && !set.HasChecksums)
                        throw SimulatedSqlException.RestoreChecksumUnavailable(verb);
                    var messages = batch.Connection.PendingMessages;
                    if (options.Stats is { } step)
                    {
                        for (var percent = step; percent < 100; percent += step)
                            messages.Enqueue(SimulatedSqlException.BackupPercentProcessedMessage(batch, percent));
                        messages.Enqueue(SimulatedSqlException.BackupPercentProcessedMessage(batch, 100));
                    }
                    messages.Enqueue(SimulatedSqlException.BackupSetValidMessage(batch, options.File));
                    if (options.LoadHistory)
                        this.RecordRestoreHistory(batch, media, set, device.Display, options.File, databaseName: null, 'V', replace: false, physicalNames: null);
                    batch.Connection.LastStatementRowCount = 0;
                    break;
                }
        }
        return true;
    }

    /// <summary>The media a restore reads from <paramref name="device"/>, refusing one it can't read or whose name isn't <paramref name="mediaName"/>.</summary>
    private BackupMedia ReadMediaForRestore(BatchContext batch, BackupDeviceTarget device, string verb, string? mediaName)
    {
        var bytes = this.ReadBackupDevice(device.Path, out var osError);
        if (bytes is null)
        {
            this.LogBackupMediaFailure(batch, device.Path, osError, create: false);
            throw SimulatedSqlException.CannotOpenBackupDevice(device.Display, osError, verb);
        }
        var media = BackupMedia.TryRead(bytes, out var read, out var writtenBy) switch
        {
            BackupMedia.ReadOutcome.Read => read!,
            BackupMedia.ReadOutcome.Empty => throw SimulatedSqlException.BackupVolumeEmpty(device.Display, verb),
            BackupMedia.ReadOutcome.OtherVersion => throw SimulatedSqlException.BackupFromIncompatibleVersion(writtenBy!, BackupMedia.SimulatorVersion, verb),
            _ => throw SimulatedSqlException.MediaFamilyIncorrectlyFormed(device.Display, verb),
        };
        if (mediaName is not null)
            RefuseMediaNameMismatch(media, mediaName, device, verb);
        return media;
    }

    /// <summary>The set at <c>FILE = <paramref name="file"/></c>, Msg 4038 past the last.</summary>
    private static BackupSet SetAt(BackupMedia media, int file, BackupDeviceTarget device, string verb) =>
        file <= media.Sets.Count ? media.Sets[file - 1] : throw SimulatedSqlException.BackupFileIdNotFound(file, device.Display, verb);

    private static SqlValue Lsn25(long block) => SqlValue.FromDecimal(Lsn25Type, DatabaseBackupChain.Lsn(block));

    private static readonly SqlType Lsn25Type = SqlType.GetDecimal(25, 0);

    private static SqlValue NText(int length, string? value) =>
        value is null ? SqlValue.Null(NVarcharSqlType.Get(length, Collation.Baseline, Coercibility.CoercibleDefault)) : SqlValue.FromString(NVarcharSqlType.Get(length, Collation.Baseline, Coercibility.CoercibleDefault), value);

    /// <summary>One <c>HEADERONLY</c> row, a set's header.</summary>
    private static SqlValue[] HeaderOnlyRow(BackupSet set, int position)
    {
        var server = Network.TdsSession.ServerName;
        var noGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        return
        [
            NText(128, set.Name),
            NText(255, set.Description),
            SqlValue.FromByte(set.Type),
            SqlValue.Null(SqlType.DateTime),
            SqlValue.FromByte(0),
            SqlValue.FromInt16((short)position),
            SqlValue.FromByte(2),
            NText(128, set.UserName),
            NText(128, server),
            NText(128, set.DatabaseName),
            SqlValue.FromInt32(998),
            SqlValue.FromDateTime(BuiltInResources.SysDatabasesCreateDate),
            SqlValue.FromInt64(set.BackupSize),
            Lsn25(set.FirstLsnBlock),
            Lsn25(set.LastLsnBlock),
            Lsn25(set.CheckpointLsnBlock),
            set.DatabaseBackupLsnBlock == 0 ? SqlValue.FromDecimal(Lsn25Type, 0) : Lsn25(set.DatabaseBackupLsnBlock),
            SqlValue.FromDateTime(WholeSeconds(set.StartUtc)),
            SqlValue.FromDateTime(WholeSeconds(set.FinishUtc)),
            SqlValue.FromInt16(52),
            SqlValue.FromInt16(0),
            SqlValue.FromInt32(1033),
            SqlValue.FromInt32(196609),
            SqlValue.FromByte(set.CompatibilityLevel),
            SqlValue.FromInt32(BackupSoftwareVendorId),
            SqlValue.FromInt32(17),
            SqlValue.FromInt32(0),
            SqlValue.FromInt32(4065),
            NText(128, server),
            SqlValue.FromInt32(set.Flags),
            SqlValue.FromGuid(set.DatabaseGuid),
            SqlValue.FromGuid(set.RecoveryForkGuid),
            NText(128, set.Collation),
            SqlValue.FromGuid(set.RecoveryForkGuid),
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(set.IsReadOnly),
            SqlValue.FromBoolean(set.IsSingleUser),
            SqlValue.FromBoolean(set.HasChecksums),
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(set.BeginsLogChain),
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(set.IsForceOffline),
            SqlValue.FromBoolean(set.IsCopyOnly),
            SqlValue.FromGuid(set.RecoveryForkGuid),
            SqlValue.Null(Lsn25Type),
            NText(60, set.RecoveryModel),
            set.DifferentialBaseBlock is { } differentialBase ? Lsn25(differentialBase) : SqlValue.Null(Lsn25Type),
            set.DifferentialBaseGuid is { } differentialGuid ? SqlValue.FromGuid(differentialGuid) : noGuid,
            NText(128, set.Type switch
            {
                BackupSet.LogType => "Transaction Log",
                BackupSet.DifferentialType => "Database Differential",
                _ => "Database",
            }),
            SqlValue.FromGuid(set.BackupSetGuid),
            SqlValue.FromInt64(set.BackupSize),
            SqlValue.FromByte(0),
            NText(32, null),
            SqlValue.Null(VarbinarySqlType.Get(20)),
            NText(32, null),
            set.Type == BackupSet.LogType ? SqlValue.FromDateTime(WholeSeconds(set.FinishUtc)) : SqlValue.Null(SqlType.DateTime),
            SqlValue.FromInt16(0),
            NText(32, null),
        ];
    }

    private static readonly SqlType[] HeaderOnlySchema =
    [
        NVar(128), NVar(255), SqlType.TinyInt, SqlType.DateTime, SqlType.TinyInt, SqlType.SmallInt, SqlType.TinyInt, NVar(128), NVar(128), NVar(128),
        SqlType.Int32, SqlType.DateTime, SqlType.BigInt, Lsn25Type, Lsn25Type, Lsn25Type, Lsn25Type, SqlType.DateTime, SqlType.DateTime, SqlType.SmallInt,
        SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.TinyInt, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, NVar(128), SqlType.Int32,
        SqlType.UniqueIdentifier, SqlType.UniqueIdentifier, NVar(128), SqlType.UniqueIdentifier, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit,
        SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.UniqueIdentifier, Lsn25Type, NVar(60), Lsn25Type, SqlType.UniqueIdentifier, NVar(128),
        SqlType.UniqueIdentifier, SqlType.BigInt, SqlType.TinyInt, NVar(32), VarbinarySqlType.Get(20), NVar(32), SqlType.DateTime, SqlType.SmallInt, NVar(32),
    ];

    private static readonly string[] HeaderOnlyColumnNames =
    [
        "BackupName", "BackupDescription", "BackupType", "ExpirationDate", "Compressed", "Position", "DeviceType", "UserName", "ServerName", "DatabaseName",
        "DatabaseVersion", "DatabaseCreationDate", "BackupSize", "FirstLSN", "LastLSN", "CheckpointLSN", "DatabaseBackupLSN", "BackupStartDate", "BackupFinishDate", "SortOrder",
        "CodePage", "UnicodeLocaleId", "UnicodeComparisonStyle", "CompatibilityLevel", "SoftwareVendorId", "SoftwareVersionMajor", "SoftwareVersionMinor", "SoftwareVersionBuild", "MachineName", "Flags",
        "BindingID", "RecoveryForkID", "Collation", "FamilyGUID", "HasBulkLoggedData", "IsSnapshot", "IsReadOnly", "IsSingleUser", "HasBackupChecksums", "IsDamaged",
        "BeginsLogChain", "HasIncompleteMetaData", "IsForceOffline", "IsCopyOnly", "FirstRecoveryForkID", "ForkPointLSN", "RecoveryModel", "DifferentialBaseLSN", "DifferentialBaseGUID", "BackupTypeDescription",
        "BackupSetGUID", "CompressedBackupSize", "Containment", "KeyAlgorithm", "EncryptorThumbprint", "EncryptorType", "LastValidRestoreTime", "TimeZone", "CompressionAlgorithm",
    ];

    /// <summary>One <c>FILELISTONLY</c> row: a file of the set, the database's differential base on a data file.</summary>
    private static SqlValue[] FileListOnlyRow(BackupSet set, BackupSetFile file)
    {
        var zero = SqlValue.FromDecimal(Lsn25Type, 0);
        long backedUp = 0;
        if (!file.IsLog && set.Type != BackupSet.LogType)
        {
            foreach (var (logicalName, pages) in set.FilePages)
            {
                if (string.Equals(logicalName, file.LogicalName, StringComparison.OrdinalIgnoreCase))
                    backedUp = pages * 8192L;
            }
        }
        return
        [
            NText(128, file.LogicalName),
            NText(260, file.PhysicalName),
            SqlValue.FromString(NCharSqlType.Get(1, Collation.Baseline, Coercibility.CoercibleDefault), file.IsLog ? "L" : "D"),
            NText(128, file.FileGroupName),
            SqlValue.FromInt64(file.SizeBytes),
            SqlValue.FromInt64(file.MaxSizeBytes),
            SqlValue.FromInt64(file.FileId),
            zero,
            zero,
            SqlValue.FromGuid(file.UniqueId),
            zero,
            zero,
            SqlValue.FromInt64(backedUp),
            SqlValue.FromInt32(4096),
            SqlValue.FromInt32(file.FileGroupId),
            SqlValue.Null(SqlType.UniqueIdentifier),
            file.IsLog || set.FileDifferentialBaseBlock is not { } differentialBase ? zero : Lsn25(differentialBase),
            SqlValue.FromGuid(file.IsLog ? Guid.Empty : set.FileDifferentialBaseGuid ?? Guid.Empty),
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(true),
            SqlValue.Null(VarbinarySqlType.Get(20)),
            NText(336, null),
        ];
    }

    private static readonly SqlType[] FileListOnlySchema =
    [
        NVar(128), NVar(260), NCharSqlType.Get(1, Collation.Baseline, Coercibility.CoercibleDefault), NVar(128), SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, Lsn25Type, Lsn25Type, SqlType.UniqueIdentifier,
        Lsn25Type, Lsn25Type, SqlType.BigInt, SqlType.Int32, SqlType.Int32, SqlType.UniqueIdentifier, Lsn25Type, SqlType.UniqueIdentifier, SqlType.Bit, SqlType.Bit,
        VarbinarySqlType.Get(20), NVar(336),
    ];

    private static readonly string[] FileListOnlyColumnNames =
    [
        "LogicalName", "PhysicalName", "Type", "FileGroupName", "Size", "MaxSize", "FileId", "CreateLSN", "DropLSN", "UniqueId",
        "ReadOnlyLSN", "ReadWriteLSN", "BackupSizeInBytes", "SourceBlockSize", "FileGroupId", "LogGroupGUID", "DifferentialBaseLSN", "DifferentialBaseGUID", "IsReadOnly", "IsPresent",
        "TDEThumbprint", "SnapshotUrl",
    ];

    /// <summary>The one <c>LABELONLY</c> row, the media's header.</summary>
    private static SqlValue[] LabelOnlyRow(BackupMedia media) =>
    [
        NText(128, media.MediaName),
        SqlValue.FromGuid(media.MediaSetGuid),
        SqlValue.FromInt32(1),
        SqlValue.FromInt32(1),
        SqlValue.FromGuid(media.MediaFamilyGuid),
        SqlValue.FromInt32(1),
        SqlValue.FromByte(0),
        NText(255, media.MediaDescription),
        NText(64, BackupSoftwareName),
        SqlValue.FromInt32(BackupSoftwareVendorId),
        SqlValue.FromDateTime(WholeSeconds(media.MediaDateUtc)),
        SqlValue.FromInt32(1),
        SqlValue.FromBoolean(false),
    ];

    private static readonly SqlType[] LabelOnlySchema =
    [
        NVar(128), SqlType.UniqueIdentifier, SqlType.Int32, SqlType.Int32, SqlType.UniqueIdentifier, SqlType.Int32, SqlType.TinyInt, NVar(255), NVar(64), SqlType.Int32,
        SqlType.DateTime, SqlType.Int32, SqlType.Bit,
    ];

    private static readonly string[] LabelOnlyColumnNames =
    [
        "MediaName", "MediaSetId", "FamilyCount", "FamilySequenceNumber", "MediaFamilyId", "MediaSequenceNumber", "MediaLabelPresent", "MediaDescription", "SoftwareName", "SoftwareVendorId",
        "MediaDate", "MirrorCount", "IsCompressed",
    ];

    private static NVarcharSqlType NVar(int length) => NVarcharSqlType.Get(length, Collation.Baseline, Coercibility.CoercibleDefault);
}
