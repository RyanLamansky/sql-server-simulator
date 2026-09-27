using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_helpdb — the database list (no argument) or one database's detail plus its
// file allocation (one argument, which appends sp_helpfile's result set).
// Column names, types, ordering, the option-string vocabulary and the KB / MB
// rendering are probe-confirmed against SQL Server 2025 (2026-07-31); the
// option string is assembled in real's own clause order from the same
// DATABASEPROPERTYEX values the scalar function serves.
partial class Simulation
{
    // Real's #spdbdesc temp table declares these widths, and they become the
    // result-set types: dbsize nvarchar(13), created nvarchar(11) (a datetime
    // truncated to its `Mon dd yyyy` prefix), dbdesc nvarchar(600).
    private static readonly NVarcharSqlType HelpDbSizeType =
        NVarcharSqlType.Get(13, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType HelpDbCreatedType =
        NVarcharSqlType.Get(11, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType HelpDbStatusType =
        NVarcharSqlType.Get(600, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] SpHelpDbSchema =
    [
        SqlType.SystemName, HelpDbSizeType, SqlType.SystemName, SqlType.SmallInt,
        HelpDbCreatedType, HelpDbStatusType, SqlType.TinyInt,
    ];

    private static readonly string[] SpHelpDbColumnNames =
        ["name", "db_size", "owner", "dbid", "created", "status", "compatibility_level"];

    // sp_helpfile's no-argument shape, which sp_helpdb's one-argument form
    // appends: name sysname, fileid smallint, filename nvarchar(260) (the
    // sysfiles column's own width), filegroup nvarchar(128), size / maxsize /
    // growth nvarchar(18), usage varchar(9).
    private static readonly NVarcharSqlType HelpFilePathType =
        NVarcharSqlType.Get(260, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType HelpFileSizeType =
        NVarcharSqlType.Get(18, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType HelpFileUsageType =
        VarcharSqlType.Get(9, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] SpHelpFileSchema =
    [
        SqlType.SystemName, SqlType.SmallInt, HelpFilePathType, SqlType.SystemName,
        HelpFileSizeType, HelpFileSizeType, HelpFileSizeType, HelpFileUsageType,
    ];

    private static readonly string[] SpHelpFileColumnNames =
        ["name", "fileid", "filename", "filegroup", "size", "maxsize", "growth", "usage"];

    // sp_helpfile @filename's shape — real's second SELECT, which drops the
    // fileid column (the caller already named the file) and keeps the rest.
    private static readonly SqlType[] SpHelpFileNamedSchema =
    [
        SqlType.SystemName, HelpFilePathType, SqlType.SystemName,
        HelpFileSizeType, HelpFileSizeType, HelpFileSizeType, HelpFileUsageType,
    ];

    private static readonly string[] SpHelpFileNamedColumnNames =
        ["name", "filename", "filegroup", "size", "maxsize", "growth", "usage"];

    /// <summary>
    /// Handles <c>EXEC sp_helpdb [@dbname]</c>. Without an argument: one row
    /// per database — <c>name sysname</c>, <c>db_size nvarchar(13)</c>,
    /// <c>owner sysname</c>, <c>dbid smallint</c>, <c>created nvarchar(11)</c>,
    /// <c>status nvarchar(600)</c>, <c>compatibility_level tinyint</c> — sorted
    /// by name. With one: that database's row, then real's single-space PRINT,
    /// then the <c>sp_helpfile</c> result set for its two files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A database that <c>HAS_DBACCESS</c> reports 0 for is dropped from the
    /// report, with the severity-10 Msg 15622 raised in its place — real's
    /// per-database access check, which here excludes <c>model</c> (the
    /// restricted template) from the no-argument listing.
    /// </para>
    /// <para>
    /// An unknown <c>@dbname</c> is Msg 15010. <c>db_size</c> and the file
    /// sizes read the same synthetic two-file model <c>sys.database_files</c> /
    /// <c>sys.master_files</c> project, so the three surfaces agree; the file
    /// growth (64 MB) and unlimited max size are those views' values too.
    /// <c>owner</c> is <c>dbo</c>: the simulator has no per-database owner
    /// principal, and <c>dbo</c> is the identity every unauthenticated session
    /// already runs as.
    /// </para>
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpDb(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (databaseName, _) = ParseHelpArgs(arguments, "sp_helpdb", firstName: "dbname");
        var simulation = batch.Connection.Simulation;
        Database? single = null;
        if (databaseName is not null
            && !simulation.Databases.TryGetValue(databaseName, out single))
        {
            throw SimulatedSqlException.HelpDatabaseDoesNotExist(databaseName);
        }

        // SUSER_SNAME of the owner_sid sys.databases reports, which is sa's.
        var owner = SqlValue.FromSystemName("sa");
        var rows = new List<SqlValue[]>();
        foreach (var (database, id) in DbId.DatabasesWithIds(simulation))
        {
            if (single is not null && !ReferenceEquals(database, single))
                continue;
            if (!HasDbAccess.IsAccessible(batch.Connection, database))
            {
                batch.AppendInfoError(@class: 10, state: 1, number: 15622,
                    message: $"No permission to access database '{database.Name}'.");
                continue;
            }

            rows.Add([
                SqlValue.FromSystemName(database.Name),
                SqlValue.FromString(HelpDbSizeType, HelpDbSize(database)),
                owner,
                SqlValue.FromInt16(id),
                SqlValue.FromString(HelpDbCreatedType, HelpDbCreated(BuiltInResources.SysDatabasesCreateDate)),
                SqlValue.FromString(HelpDbStatusType, HelpDbOptionString(database)),
                SqlValue.FromByte((byte)database.CompatibilityLevel),
            ]);
        }

        rows.Sort(ByFirstCell);
        yield return new SimulatedSqlResultSet(SpHelpDbSchema, SpHelpDbColumnNames, rows);

        // The single-database form follows the summary with a bare PRINT and
        // the target database's own sp_helpfile output.
        if (single is null || !HasDbAccess.IsAccessible(batch.Connection, single))
            yield break;
        yield return HelpBlankLine(batch, "sp_helpdb", 193);
        yield return HelpFileResultSet(single);
    }

    // `str(sum(size) / 128, 10, 2) + ' MB'` over the database's files,
    // right-aligned in str's ten columns.
    private static string HelpDbSize(Database database)
    {
        var pages = BuiltInResources.TotalFileSizePages(database);
        return (pages / 128m).ToString("F2", CultureInfo.InvariantCulture).PadLeft(10) + " MB";
    }

    // `convert(nvarchar(11), crdate)` — style-0 datetime text
    // (`Mon dd yyyy hh:mmAM`) cut to its first 11 characters, so the day is
    // space-padded to two columns (`Apr  8 2003`) and the time is gone.
    private static string HelpDbCreated(DateTime createDate) =>
        createDate.ToString("MMM", CultureInfo.InvariantCulture)
        + createDate.Day.ToString(CultureInfo.InvariantCulture).PadLeft(3)
        + " " + createDate.Year.ToString(CultureInfo.InvariantCulture);

    // Real's fixed clause order: the five always-present properties, then the
    // two the SUSPECT check gates, then each boolean property DATABASEPROPERTYEX
    // reports as 1, in real's order (probed 2026-09-26 against SQL Server 2025),
    // so the string tracks live state rather than a canned list.
    private static string HelpDbOptionString(Database database)
    {
        var text = new StringBuilder()
            .Append("Status=ONLINE, Updateability=").Append(database.IsReadOnly ? "READ_ONLY" : "READ_WRITE")
            .Append(", UserAccess=").Append(database.UserAccess switch
            {
                1 => "SINGLE_USER",
                2 => "RESTRICTED_USER",
                _ => "MULTI_USER",
            })
            .Append(", Recovery=").Append(database.RecoveryModel switch
            {
                RecoveryModel.Simple => "SIMPLE",
                RecoveryModel.BulkLogged => "BULK_LOGGED",
                _ => "FULL",
            })
            .Append(", Version=998")
            .Append(", Collation=").Append(database.CollationName)
            .Append(", SQLSortOrder=").Append(Collation.SqlServerSortOrders.TryGetValue(database.CollationName, out var sortOrder)
                ? sortOrder.OrderNumber.ToString(CultureInfo.InvariantCulture)
                : "0");
        var switches = database.Switches;
        void Flag(bool on, string name)
        {
            if (on)
                _ = text.Append(", ").Append(name);
        }
        Flag((switches & DatabaseSwitches.AutoClose) != 0, "IsAutoClose");
        Flag((switches & DatabaseSwitches.AutoShrink) != 0, "IsAutoShrink");
        Flag(database.PageVerify == 1, "IsTornPageDetectionEnabled");
        Flag((switches & DatabaseSwitches.AnsiNullDefault) != 0, "IsAnsiNullDefault");
        Flag((switches & DatabaseSwitches.AnsiNulls) != 0, "IsAnsiNullsEnabled");
        Flag((switches & DatabaseSwitches.AnsiPadding) != 0, "IsAnsiPaddingEnabled");
        Flag((switches & DatabaseSwitches.AnsiWarnings) != 0, "IsAnsiWarningsEnabled");
        Flag((switches & DatabaseSwitches.ArithAbort) != 0, "IsArithmeticAbortEnabled");
        Flag((switches & DatabaseSwitches.AutoCreateStatistics) != 0, "IsAutoCreateStatistics");
        Flag((switches & DatabaseSwitches.AutoUpdateStatistics) != 0, "IsAutoUpdateStatistics");
        Flag((switches & DatabaseSwitches.CursorCloseOnCommit) != 0, "IsCloseCursorsOnCommitEnabled");
        Flag(true, "IsFullTextEnabled");
        Flag((switches & DatabaseSwitches.LocalCursorDefault) != 0, "IsLocalCursorsDefault");
        Flag((switches & DatabaseSwitches.ConcatNullYieldsNull) != 0, "IsNullConcat");
        Flag((switches & DatabaseSwitches.NumericRoundAbort) != 0, "IsNumericRoundAbortEnabled");
        Flag((switches & DatabaseSwitches.QuotedIdentifier) != 0, "IsQuotedIdentifiersEnabled");
        Flag(database.RecursiveTriggers, "IsRecursiveTriggersEnabled");
        return text.ToString();
    }

    /// <summary>
    /// Handles <c>EXEC sp_helpfile [@filename]</c>. Without an argument: one
    /// row per file of the current database, ordered by <c>fileid</c>. With
    /// one: that file's row through real's <em>second</em> SELECT, which omits
    /// the <c>fileid</c> column. A name no file in the current database
    /// carries → Msg 15325.
    /// </summary>
    /// <remarks>
    /// Rows come from <see cref="Database.Files"/>, as <c>sys.database_files</c>'
    /// do. Name matching is trailing-space insensitive, as <c>FILE_ID</c>'s is.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpFile(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (fileName, _) = ParseHelpArgs(arguments, "sp_helpfile", firstName: "filename");
        var database = batch.CurrentDatabase;
        if (fileName is null)
        {
            yield return HelpFileResultSet(database);
            yield break;
        }

        var fileId = HelpFileIdOf(database, fileName)
            ?? throw SimulatedSqlException.HelpFileDoesNotExist(fileName);
        yield return HelpFileResultSet(database, fileId);
    }

    // real's `file_id(@filename)`; null when no file carries the name.
    private static short? HelpFileIdOf(Database database, string fileName) =>
        database.FindFile(fileName.TrimEnd(' ')) is { } file ? (short)file.FileId : null;

    // The file report, in fileid order. A null fileId yields every file with
    // the fileid column; a non-null one yields that file alone through the
    // narrower shape. A data file's unlimited maxsize reads "Unlimited", a log
    // file's its 2 TB cap in KB, and a percentage growth "<n>%" (probed
    // 2026-09-27 against SQL Server 2025).
    private static SimulatedSqlResultSet HelpFileResultSet(Database database, short? fileId = null)
    {
        var nullFilegroup = SqlValue.Null(SqlType.SystemName);
        var rows = new List<SqlValue[]>();
        foreach (var file in database.FilesInOrder())
        {
            if (fileId is not null && file.FileId != fileId)
                continue;
            var filegroup = file.IsLog ? nullFilegroup
                : database.Filegroups.FirstOrDefault(entry => entry.Value == file.DataSpaceId).Key is { } filegroupName
                    ? SqlValue.FromSystemName(filegroupName)
                    : nullFilegroup;
            var maxSize = BuiltInResources.ReportedMaxSizePages(file);
            SqlValue[] row =
            [
                SqlValue.FromSystemName(file.Name),
                SqlValue.FromInt16((short)file.FileId),
                SqlValue.FromString(HelpFilePathType, file.PhysicalName),
                filegroup,
                SqlValue.FromString(HelpFileSizeType, HelpFileKilobytes(BuiltInResources.FileSizePages(database, file))),
                SqlValue.FromString(HelpFileSizeType, maxSize == -1 ? "Unlimited" : HelpFileKilobytes(maxSize)),
                SqlValue.FromString(HelpFileSizeType, file.IsPercentGrowth
                    ? file.Growth.ToString(CultureInfo.InvariantCulture) + "%"
                    : HelpFileKilobytes(file.Growth)),
                SqlValue.FromString(HelpFileUsageType, file.IsLog ? "log only" : "data only"),
            ];
            rows.Add(fileId is null ? row : WithoutFileId(row));
        }
        return fileId is null
            ? new SimulatedSqlResultSet(SpHelpFileSchema, SpHelpFileColumnNames, rows)
            : new SimulatedSqlResultSet(SpHelpFileNamedSchema, SpHelpFileNamedColumnNames, rows);
    }

    // The named form's row: the same cells minus the fileid at ordinal 1.
    private static SqlValue[] WithoutFileId(SqlValue[] row) =>
        [row[0], row[2], row[3], row[4], row[5], row[6], row[7]];

    private static string HelpFileKilobytes(long pages) =>
        (pages * 8).ToString(CultureInfo.InvariantCulture) + " KB";
}
