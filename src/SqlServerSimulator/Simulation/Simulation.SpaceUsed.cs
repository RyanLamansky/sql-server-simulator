using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_spaceused — the size report for one object or for the whole database.
// Result-set shapes, column types, wording and the KB / MB rendering are
// probe-confirmed against SQL Server 2025 (2026-07-31); the arithmetic mirrors
// the shipped procedure's own body (read back through OBJECT_DEFINITION on the
// reference instance) applied to the simulator's page model instead of real's
// allocation metadata.
partial class Simulation
{
    // Every size cell is `LTRIM(STR(<pages> * 8, 15, 0) + ' KB')` or the MB
    // equivalent, which real's temp tables and final projections type as
    // varchar(18). The name column is the temp table's own nvarchar(128)
    // (nullable), not sysname, and the row count is CONVERT(char(20), …).
    private static readonly VarcharSqlType SpaceSizeType =
        VarcharSqlType.Get(18, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType SpaceNameType =
        NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);

    private static readonly CharSqlType SpaceRowCountType =
        CharSqlType.Get(20, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] SpaceUsedObjectSchema =
        [SpaceNameType, SpaceRowCountType, SpaceSizeType, SpaceSizeType, SpaceSizeType, SpaceSizeType];

    private static readonly string[] SpaceUsedObjectColumnNames =
        ["name", "rows", "reserved", "data", "index_size", "unused"];

    // The no-allocation shape real emits for a view with no partition-stats
    // row: rows / reserved / data are bare NULLs (typed int), only the two
    // trailing cells carry a string.
    private static readonly SqlType[] SpaceUsedNoAllocationSchema =
        [SpaceNameType, SqlType.Int32, SqlType.Int32, SqlType.Int32, SpaceSizeType, SpaceSizeType];

    private static readonly SqlType[] SpaceUsedDatabaseSummarySchema =
        [SpaceNameType, SpaceSizeType, SpaceSizeType];

    private static readonly string[] SpaceUsedDatabaseSummaryColumnNames =
        ["database_name", "database_size", "unallocated space"];

    private static readonly SqlType[] SpaceUsedDatabaseDetailSchema =
        [SpaceSizeType, SpaceSizeType, SpaceSizeType, SpaceSizeType];

    private static readonly string[] SpaceUsedDatabaseDetailColumnNames =
        ["reserved", "data", "index_size", "unused"];

    private static readonly SqlType[] SpaceUsedOneResultSetSchema =
    [
        SpaceNameType, SpaceSizeType, SpaceSizeType,
        SpaceSizeType, SpaceSizeType, SpaceSizeType, SpaceSizeType,
    ];

    private static readonly string[] SpaceUsedOneResultSetColumnNames =
    [
        "database_name", "database_size", "unallocated space",
        "reserved", "data", "index_size", "unused",
    ];

    private static readonly SqlType[] SpaceUsedXtpSchema =
    [
        SpaceNameType, SpaceSizeType, SpaceSizeType, SpaceSizeType, SpaceSizeType,
        SpaceSizeType, SpaceSizeType, SpaceSizeType, SpaceSizeType, SpaceSizeType,
    ];

    private static readonly string[] SpaceUsedXtpColumnNames =
    [
        "database_name", "database_size", "unallocated space",
        "reserved", "data", "index_size", "unused",
        "xtp_precreated", "xtp_used", "xtp_pending_truncation",
    ];

    /// <summary>
    /// Handles <c>EXEC sp_spaceused [@objname] [, @updateusage] [, @mode]
    /// [, @oneresultset] [, @include_total_xtp_storage]</c>. Without
    /// <c>@objname</c> the proc reports the database: a
    /// <c>database_name</c> / <c>database_size</c> / <c>unallocated space</c>
    /// summary set followed by a <c>reserved</c> / <c>data</c> /
    /// <c>index_size</c> / <c>unused</c> detail set — fused into one seven-column
    /// set by <c>@oneresultset = 1</c>, which <c>@include_total_xtp_storage = 1</c>
    /// extends with the three (always-NULL) <c>xtp_*</c> columns. With
    /// <c>@objname</c> it reports that object: <c>name</c> / <c>rows</c> plus
    /// the same four size cells.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Page counts come from the same per-(table, index) identities
    /// <c>sys.dm_db_partition_stats</c> projects, through
    /// <see cref="BuiltInResources.SpaceUsedTotals"/>, so the proc and the DMV
    /// can't disagree; the database-level file size is the one
    /// <c>sys.database_files</c> reports. Since the simulator's page model
    /// reserves exactly what it uses, <c>unused</c> is always <c>0 KB</c>, and
    /// a nonclustered index contributes its base table's page count the way
    /// the DMV already reports it.
    /// </para>
    /// <para>
    /// Error paths mirror real: a three-part name naming another database →
    /// Msg 15250; an unresolvable name → Msg 15009; an object kind with no
    /// storage (a procedure, a function, a constraint) → Msg 15234;
    /// <c>@updateusage</c> outside true/false → Msg 15143; <c>@mode</c> outside
    /// ALL / LOCAL_ONLY / REMOTE_ONLY → Msg 14822; <c>@mode = 'REMOTE_ONLY'</c>
    /// → Msg 14821 (no database is stretched). <c>@updateusage = 'true'</c> is
    /// accepted and emits real's trailing single-space PRINT; there are no
    /// stale usage counters to recompute.
    /// </para>
    /// <para>
    /// A view never has storage of its own here (an indexed view's rows are
    /// not materialized), so every view takes real's no-partition-stats
    /// branch: one row with NULL <c>rows</c> / <c>reserved</c> / <c>data</c>
    /// and <c>'0 KB'</c> for the other two.
    /// </para>
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSpaceUsed(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, updateUsage, mode, oneResultSet, includeXtp) = ParseSpSpaceUsedArgs(arguments);
        if (updateUsage is not null && !BuiltInToken.EqualsAny(updateUsage, "true", "false"))
        {
            // Real lower-cases @updateusage before validating it, so the
            // rejected value appears lower-cased in Msg 15143's text.
#pragma warning disable CA1308
            throw SimulatedSqlException.SpaceUsedUpdateUsageIsNotValid(updateUsage.ToLowerInvariant());
#pragma warning restore CA1308
        }

        if (!BuiltInToken.EqualsAny(mode, "ALL", "LOCAL_ONLY", "REMOTE_ONLY"))
            throw SimulatedSqlException.SpaceUsedModeIsNotValid(mode);

        // Nothing is stretched, so REMOTE_ONLY has no remote part to report.
        // Real raises the same message from two sites, distinguished by state:
        // 1 on the database form, 2 on the object form.
        if (BuiltInToken.Equals(mode, "REMOTE_ONLY"))
            throw SimulatedSqlException.SpaceUsedRemoteOnlyHasNoRemotePart(objectName is null ? (byte)1 : (byte)2);

        var database = batch.CurrentDatabase;
        var target = objectName is null ? null : ResolveHelpTarget(batch, "sp_spaceused", objectName);

        // DBCC UPDATEUSAGE has nothing to correct (the page counts are read
        // live), but real prints a single space after running it.
        if (updateUsage is not null && BuiltInToken.Equals(updateUsage, "true"))
            batch.AppendPrintMessage(" ");

        if (target is null)
        {
            foreach (var set in SpaceUsedDatabaseResultSets(database, oneResultSet, includeXtp))
                yield return set;
            yield break;
        }

        yield return target.Object switch
        {
            HeapTable table => SpaceUsedObjectResultSet(database, table),
            View view => SpaceUsedNoAllocationResultSet(view.Name),
            _ => throw SimulatedSqlException.SpaceUsedObjectHasNoSpace(),
        };
    }

    private static IEnumerable<SimulatedStatementOutcome> SpaceUsedDatabaseResultSets(
        Database database, bool oneResultSet, bool includeXtp)
    {
        var (reservedPages, usedPages, dataPages, _) = BuiltInResources.SpaceUsedTotals(database, only: null);
        long dataFilePages = BuiltInResources.ComputeDataFileSizePages(database);
        var databaseSize = SpaceMegabytes(dataFilePages + BuiltInResources.LogFileSizePages);
        var unallocated = SpaceMegabytes(Math.Max(0, dataFilePages - reservedPages));
        var name = SqlValue.FromString(SpaceNameType, database.Name);
        var reserved = SpaceKilobytes(reservedPages);
        var data = SpaceKilobytes(dataPages);
        var indexSize = SpaceKilobytes(Math.Max(0, usedPages - dataPages));
        var unused = SpaceKilobytes(Math.Max(0, reservedPages - usedPages));

        if (!oneResultSet)
        {
            yield return new SimulatedSqlResultSet(
                SpaceUsedDatabaseSummarySchema, SpaceUsedDatabaseSummaryColumnNames,
                [[name, databaseSize, unallocated]]);
            yield return new SimulatedSqlResultSet(
                SpaceUsedDatabaseDetailSchema, SpaceUsedDatabaseDetailColumnNames,
                [[reserved, data, indexSize, unused]]);
            yield break;
        }

        // The memory-optimized columns exist only in real's
        // @include_total_xtp_storage arm, and their source is a checkpoint-file
        // DMV with no simulator counterpart — so all three are NULL, which is
        // also what real reports for a database with no memory-optimized
        // filegroup.
        if (!includeXtp)
        {
            yield return new SimulatedSqlResultSet(
                SpaceUsedOneResultSetSchema, SpaceUsedOneResultSetColumnNames,
                [[name, databaseSize, unallocated, reserved, data, indexSize, unused]]);
            yield break;
        }

        var nullSize = SqlValue.Null(SpaceSizeType);
        yield return new SimulatedSqlResultSet(
            SpaceUsedXtpSchema, SpaceUsedXtpColumnNames,
            [[name, databaseSize, unallocated, reserved, data, indexSize, unused, nullSize, nullSize, nullSize]]);
    }

    private static SimulatedSqlResultSet SpaceUsedObjectResultSet(Database database, HeapTable table)
    {
        var (reservedPages, usedPages, dataPages, rowCount) = BuiltInResources.SpaceUsedTotals(database, table);
        return new SimulatedSqlResultSet(SpaceUsedObjectSchema, SpaceUsedObjectColumnNames,
        [
            [
                SqlValue.FromString(SpaceNameType, table.Name),
                SqlValue.FromString(SpaceRowCountType, rowCount.ToString(CultureInfo.InvariantCulture)),
                SpaceKilobytes(reservedPages),
                SpaceKilobytes(dataPages),
                SpaceKilobytes(Math.Max(0, usedPages - dataPages)),
                SpaceKilobytes(Math.Max(0, reservedPages - usedPages)),
            ],
        ]);
    }

    private static SimulatedSqlResultSet SpaceUsedNoAllocationResultSet(string name)
    {
        var zeroKb = SqlValue.FromString(SpaceSizeType, "0 KB");
        var nullInt = SqlValue.Null(SqlType.Int32);
        return new SimulatedSqlResultSet(SpaceUsedNoAllocationSchema, SpaceUsedObjectColumnNames,
            [[SqlValue.FromString(SpaceNameType, name), nullInt, nullInt, nullInt, zeroKb, zeroKb]]);
    }

    // `LTRIM(STR(<pages> * 8192 / 1024., 15, 0) + ' KB')` — 8 KB per page, no
    // fractional part.
    private static SqlValue SpaceKilobytes(long pages) =>
        SqlValue.FromString(SpaceSizeType, (pages * 8).ToString(CultureInfo.InvariantCulture) + " KB");

    // `LTRIM(STR(<pages> * 8192 / 1048576, 15, 2) + ' MB')` — 128 pages per MB,
    // always two decimals.
    private static SqlValue SpaceMegabytes(long pages) =>
        SqlValue.FromString(SpaceSizeType, (pages / 128m).ToString("F2", CultureInfo.InvariantCulture) + " MB");

    private static (string? ObjectName, string? UpdateUsage, string Mode, bool OneResultSet, bool IncludeXtp) ParseSpSpaceUsedArgs(
        List<ProcArgument> arguments)
    {
        string? objectName = null, updateUsage = null;
        var mode = "ALL";
        bool oneResultSet = false, includeXtp = false;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: objectName = CatalogStringArg(arg); break;
                    case 1: updateUsage = CatalogStringArg(arg); break;
                    case 2: mode = CatalogStringArg(arg) ?? mode; break;
                    case 3: oneResultSet = CatalogFlagArg(arg); break;
                    case 4: includeXtp = CatalogFlagArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_spaceused");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "objname"): objectName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "updateusage"): updateUsage = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "mode"): mode = CatalogStringArg(arg) ?? mode; break;
                case var n when BuiltInToken.Equals(n, "oneresultset"): oneResultSet = CatalogFlagArg(arg); break;
                case var n when BuiltInToken.Equals(n, "include_total_xtp_storage"): includeXtp = CatalogFlagArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_spaceused");
            }
        }

        return (objectName, updateUsage, mode, oneResultSet, includeXtp);
    }
}
