using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// <c>DBCC DBINFO [ ( [ database ] ) ] [ WITH TABLERESULTS, NO_INFOMSGS ]</c>:
    /// the database's boot page, which only <c>TABLERESULTS</c> returns — as
    /// real's 108 rows of <c>ParentObject</c> / <c>Object</c> / <c>Field</c> /
    /// <c>VALUE</c> — where without it real writes the dump nowhere a client
    /// reads and sends Msg 2528 alone. A database id is Msg 2560, a caller
    /// outside <c>sysadmin</c> Msg 2571 (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    /// <remarks>
    /// The fields that follow the simulator's state are the database's name,
    /// id, compatibility level, collation, creation date, family and Service
    /// Broker GUIDs and <c>dbi_dbccLastKnownGood</c>, the last <c>DBCC CHECKDB</c> it passed;
    /// the log sequence numbers, status words and other engine internals are
    /// real's values for a fresh database, and the structure address a
    /// stand-in.
    /// </remarks>
    private static List<SimulatedStatementOutcome> RunDbccDbInfo(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.TableResults);
        dbcc.RequireArgumentCount(0, 1);
        RequireDbccSysadmin(batch, "dbinfo", 14);
        var argument = dbcc.Arguments.Count == 1 ? dbcc.Arguments[0] : null;
        if (argument is not null && argument.Kind != DbccArgumentKind.Name && argument.Evaluate(batch) is { IsNull: false } value && !SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        var database = ResolveDbccDatabase(batch, argument, 1);
        if (!dbcc.Has(DbccOptions.TableResults))
            return DbccCompleted(batch, dbcc, []);

        var simulation = batch.Connection.Simulation;
        var databaseId = DatabaseIdOf(simulation, database);
        var address = string.Create(CultureInfo.InvariantCulture, $"DBINFO @0x{0x0000001101900000L + (databaseId * 0x1000L):X16}");
        var created = BuiltInResources.SysDatabasesCreateDate.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var lastKnownGood = (database.LastGoodCheckDbUtc ?? new DateTime(1900, 1, 1)).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var collationId = Collation.TryGetMetrics(database.CollationName, out var metrics) ? metrics.CollationId : 0;
        var family = database.RecoveryForkGuid.ToString();
        var sawForkGuid = false;
        var name = NVarcharSqlType.Get(255, database.Collation, Coercibility.CoercibleDefault);
        var rows = new List<SqlValue[]>(BootPageTemplate.Length);
        foreach (var (parent, obj, field, template) in BootPageTemplate)
        {
            var reported = field switch
            {
                "dbi_cmptlevel" => ((int)database.CompatibilityLevel).ToString(CultureInfo.InvariantCulture),
                "dbi_collation" => collationId.ToString(CultureInfo.InvariantCulture),
                "dbi_crdate" or "dbi_modDate" => created,
                "dbi_dbccLastKnownGood" => lastKnownGood,
                "dbi_dbid" => databaseId.ToString(CultureInfo.InvariantCulture),
                "dbi_dbname" => database.Name,
                "dbi_familyGUID" => family,
                "dbi_svcBrokerGUID" => BuiltInResources.SysDatabasesBrokerGuid.ToString(),
                // The recovery fork stack's first entry is the family.
                "m_guid" when !sawForkGuid => family,
                _ => template,
            };
            if (field == "m_guid")
                sawForkGuid = true;
            rows.Add([
                SqlValue.FromNVarchar(name, parent ?? address),
                SqlValue.FromNVarchar(name, obj ?? address),
                SqlValue.FromNVarchar(name, field),
                SqlValue.FromNVarchar(name, reported),
            ]);
        }
        return DbccCompleted(batch, dbcc, DbccRows(batch, [name, name, name, name], DbInfoColumnNames, rows));
    }

    private static readonly string[] DbInfoColumnNames = ["ParentObject", "Object", "Field", "VALUE"];

    /// <summary>
    /// Real's boot-page rows for a fresh SQL Server 2025 database, in its
    /// order; a null parent or object is the structure's address (probed
    /// 2026-10-16).
    /// </summary>
    private static readonly (string? Parent, string? Object, string Field, string Value)[] BootPageTemplate =
    [
        ("DBINFO STRUCTURE:", null, "dbi_version", "998"),
        ("DBINFO STRUCTURE:", null, "dbi_createVersion", "998"),
        ("DBINFO STRUCTURE:", null, "dbi_SEVersion", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dvSplitPoint", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_dbbackupLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_LastLogBackupTime", "1900-01-01 00:00:00.000"),
        ("DBINFO STRUCTURE:", null, "dbi_nextseqnum", "1900-01-01 00:00:00.000"),
        ("DBINFO STRUCTURE:", null, "dbi_status", "0x00010000"),
        ("DBINFO STRUCTURE:", null, "dbi_crdate", "2026-10-16 05:23:38.540"),
        ("DBINFO STRUCTURE:", null, "dbi_dbname", "zzL_db"),
        ("DBINFO STRUCTURE:", null, "dbi_dbid", "9"),
        ("DBINFO STRUCTURE:", null, "dbi_cmptlevel", "170"),
        ("DBINFO STRUCTURE:", null, "dbi_masterfixups", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_maxDbTimestamp", "18000"),
        ("DBINFO STRUCTURE:", null, "dbi_dbbackupLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_RebuildLogs", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_differentialBaseLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_RestoreFlags", "0x0000"),
        ("DBINFO STRUCTURE:", null, "dbi_checkptLSN", "1769:3928:1 (0x000006e9:00000f58:0001)"),
        ("DBINFO STRUCTURE:", null, "dbi_dbccFlags", "2"),
        ("DBINFO STRUCTURE:", null, "dbi_COWLastLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_DirtyPageLSN", "1769:3928:1 (0x000006e9:00000f58:0001)"),
        ("DBINFO STRUCTURE:", null, "dbi_RecoveryFlags", "0x00000000"),
        ("DBINFO STRUCTURE:", null, "dbi_lastxact", "0x7c2"),
        ("DBINFO STRUCTURE:", null, "dbi_collation", "872468488"),
        ("DBINFO STRUCTURE:", null, "dbi_relstat", "0x61000010"),
        ("DBINFO STRUCTURE:", null, "dbi_PartitionDbFlags", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_familyGUID", "da290cd1-6384-48a0-a50c-060eed23df34"),
        ("DBINFO STRUCTURE:", null, "dbi_maxLogSpaceUsed", "8380416"),
        ("dbi_recoveryForkNameStack", "entry 0", "hex (dec)", "0x00000000:00000000:0000 (0:0:0)"),
        (null, "dbi_recoveryForkNameStack", "m_guid", "da290cd1-6384-48a0-a50c-060eed23df34"),
        ("dbi_recoveryForkNameStack", "entry 1", "hex (dec)", "0x00000000:00000000:0000 (0:0:0)"),
        (null, "dbi_recoveryForkNameStack", "m_guid", "00000000-0000-0000-0000-000000000000"),
        ("DBINFO STRUCTURE:", null, "dbi_differentialBaseGuid", "00000000-0000-0000-0000-000000000000"),
        ("DBINFO STRUCTURE:", null, "dbi_firstSysIndexes", "0001:00000014"),
        ("DBINFO STRUCTURE:", null, "dbi_dynamicFileAllocationNewFileID", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_oldestBackupXactLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_lastLogBackupOldestActiveXactLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_lastLogBackupCheckpointLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_versionChangeLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_mdUpgStat", "0x0004"),
        ("DBINFO STRUCTURE:", null, "dbi_category", "0x0000000000000000"),
        ("DBINFO STRUCTURE:", null, "dbi_safetySequence", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbMirrorId", "00000000-0000-0000-0000-000000000000"),
        ("DBINFO STRUCTURE:", null, "dbi_pageUndoLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_pageUndoState", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_disabledSequence", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmRedoLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmOldestXactLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_CloneCpuCount", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_CloneMemorySize", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_updSysCatalog", "1900-01-01 00:00:00.000"),
        ("DBINFO STRUCTURE:", null, "dbi_LogBackupChainOrigin", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_dbccLastKnownGood", "1900-01-01 00:00:00.000"),
        ("DBINFO STRUCTURE:", null, "dbi_roleSequence", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmHardenedLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_localState", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_safety", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_modDate", "2026-10-16 05:23:38.540"),
        ("DBINFO STRUCTURE:", null, "dbi_verRDB", "285216737"),
        ("DBINFO STRUCTURE:", null, "dbi_delayedDurabilityOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_transactionFlags", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_stalePageDetectionOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_inMemorydbOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_optimizedLockingOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_autoIndexCompactionUserOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_svcBrokerGUID", "e8386ac2-29f9-4562-bf5d-d0a61c3af6ce"),
        ("DBINFO STRUCTURE:", null, "dbi_svcBrokerOptions", "0x00000001"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmLogZeroOutstanding", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmLastGoodRoleSequence", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmRedoQueue", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmRedoQueueType", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_rmidRegistryValueDeleted", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbmConnectionTimeout", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_AuIdNext", "1099511628028"),
        ("DBINFO STRUCTURE:", null, "dbi_MinSkipLsn", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_commitTsOfcheckptLSN", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_dbEmptyVersionState", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_CurrentGeneration", "0"),
        ("dbi_EncryptionHistory", "Scan 0", "hex (dec)", "0x00000000:00000000:0000 (0:0:0)"),
        (null, "dbi_EncryptionHistory", "EncryptionScanInfo:ScanId", "0"),
        ("dbi_EncryptionHistory", "Scan 1", "hex (dec)", "0x00000000:00000000:0000 (0:0:0)"),
        (null, "dbi_EncryptionHistory", "EncryptionScanInfo:ScanId", "0"),
        ("dbi_EncryptionHistory", "Scan 2", "hex (dec)", "0x00000000:00000000:0000 (0:0:0)"),
        (null, "dbi_EncryptionHistory", "EncryptionScanInfo:ScanId", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_latestVersioningUpgradeLSN", "18:352:69 (0x00000012:00000160:0045)"),
        ("DBINFO STRUCTURE:", null, "dbi_PendingRestoreOutcomesId", "00000000-0000-0000-0000-000000000000"),
        ("DBINFO STRUCTURE:", null, "dbi_ContainmentState", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_hkRecoveryLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_hkLogTruncationLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_hkCompatibilityMode", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_hkRootFile", "00000000-0000-0000-0000-000000000000"),
        ("DBINFO STRUCTURE:", null, "dbi_hkRootFileWatermark", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_hkTrimLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_hkUpgradeLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_hkUndeployLSN", "0:0:0 (0x00000000:00000000:0000)"),
        ("DBINFO STRUCTURE:", null, "dbi_databaseMaxSizeBytes", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_pvsRowsetIdRegular", "72057594042908672"),
        ("DBINFO STRUCTURE:", null, "dbi_pvsRowsetIdLongTerm", "72057594042974208"),
        ("DBINFO STRUCTURE:", null, "dbi_flavorModelDB", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_verDbOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_logReplicaQuorumHardenedBlock", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_status_2", "0x00000000"),
        ("DBINFO STRUCTURE:", null, "dbi_startOfPredictedActivity", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_endOfPredictedActivity", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_isProactiveActivation", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_metadataVersioningOption", "0"),
        ("DBINFO STRUCTURE:", null, "dbi_sourceTimeTravelTimestamp", "1900-01-01 00:00:00.000"),
    ];
}
