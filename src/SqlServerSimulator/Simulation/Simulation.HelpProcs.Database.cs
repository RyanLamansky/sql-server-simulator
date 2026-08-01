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

    // sp_helpfile's own shape, which sp_helpdb's one-argument form appends:
    // name sysname, fileid smallint, filename nchar(260), filegroup
    // nvarchar(128), size / maxsize / growth nvarchar(18), usage varchar(9).
    private static readonly NCharSqlType HelpFilePathType =
        NCharSqlType.Get(260, Collation.Baseline, Coercibility.Implicit);

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

        var (databaseName, _) = ParseHelpArgs(arguments, "sp_helpdb", "dbname");
        var simulation = batch.Connection.Simulation;
        Database? single = null;
        if (databaseName is not null
            && !simulation.Databases.TryGetValue(databaseName, out single))
        {
            throw SimulatedSqlException.HelpDatabaseDoesNotExist(databaseName);
        }

        var owner = SqlValue.FromSystemName("dbo");
        var rows = new List<SqlValue[]>();
        foreach (var (database, id) in DbId.DatabasesWithIds(simulation))
        {
            if (single is not null && !ReferenceEquals(database, single))
                continue;
            if (!HasDbAccess.IsAccessible(database))
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
        if (single is null || !HasDbAccess.IsAccessible(single))
            yield break;
        batch.AppendPrintMessage(" ");
        yield return HelpFileResultSet(single);
    }

    // `str(sum(size) / 128, 10, 2) + ' MB'` over the database's files — the two
    // synthetic files sys.database_files reports.
    private static string HelpDbSize(Database database)
    {
        long pages = BuiltInResources.ComputeDataFileSizePages(database) + BuiltInResources.LogFileSizePages;
        return (pages / 128m).ToString("F2", CultureInfo.InvariantCulture) + " MB";
    }

    // `convert(nvarchar(11), crdate)` — style-0 datetime text
    // (`Mon dd yyyy hh:mmAM`) cut to its first 11 characters, so the day is
    // space-padded to two columns (`Apr  8 2003`) and the time is gone.
    private static string HelpDbCreated(DateTime createDate) =>
        createDate.ToString("MMM", CultureInfo.InvariantCulture)
        + createDate.Day.ToString(CultureInfo.InvariantCulture).PadLeft(3)
        + " " + createDate.Year.ToString(CultureInfo.InvariantCulture);

    // Real's fixed clause order: the five always-present properties, then the
    // two the SUSPECT check gates, then the boolean properties in declaration
    // order. Only the flags DATABASEPROPERTYEX reports as 1 for a simulator
    // database appear, so the string tracks live state rather than a canned
    // list.
    private static string HelpDbOptionString(Database database)
    {
        var text = new StringBuilder()
            .Append("Status=ONLINE, Updateability=READ_WRITE, UserAccess=MULTI_USER, Recovery=FULL, Version=0")
            .Append(", Collation=").Append(database.CollationName)
            .Append(", SQLSortOrder=").Append(Collation.SqlServerSortOrders.TryGetValue(database.CollationName, out var sortOrder)
                ? sortOrder.OrderNumber.ToString(CultureInfo.InvariantCulture)
                : "0");
        if (database.RecursiveTriggers)
            _ = text.Append(", IsRecursiveTriggersEnabled");
        return text.ToString();
    }

    private static SimulatedSqlResultSet HelpFileResultSet(Database database)
    {
        var primary = SqlValue.FromSystemName("PRIMARY");
        var nullFilegroup = SqlValue.Null(SqlType.SystemName);
        var unlimited = SqlValue.FromString(HelpFileSizeType, "Unlimited");
        var growth = SqlValue.FromString(HelpFileSizeType,
            BuiltInResources.FileGrowthKilobytes.ToString(CultureInfo.InvariantCulture) + " KB");
        var dataUsage = SqlValue.FromString(HelpFileUsageType, "data only");
        var logUsage = SqlValue.FromString(HelpFileUsageType, "log only");
        return new SimulatedSqlResultSet(SpHelpFileSchema, SpHelpFileColumnNames,
        [
            [
                SqlValue.FromSystemName(database.Name + "_Data"),
                SqlValue.FromInt16(1),
                SqlValue.FromString(HelpFilePathType, BuiltInResources.DataFilePath(database.Name)),
                primary,
                SqlValue.FromString(HelpFileSizeType, HelpFileKilobytes(BuiltInResources.ComputeDataFileSizePages(database))),
                unlimited,
                growth,
                dataUsage,
            ],
            [
                SqlValue.FromSystemName(database.Name + "_Log"),
                SqlValue.FromInt16(2),
                SqlValue.FromString(HelpFilePathType, BuiltInResources.LogFilePath(database.Name)),
                nullFilegroup,
                SqlValue.FromString(HelpFileSizeType, HelpFileKilobytes(BuiltInResources.LogFileSizePages)),
                unlimited,
                growth,
                logUsage,
            ],
        ]);
    }

    private static string HelpFileKilobytes(long pages) =>
        (pages * 8).ToString(CultureInfo.InvariantCulture) + " KB";
}
