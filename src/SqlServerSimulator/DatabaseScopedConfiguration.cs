using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// The value grammar an <c>ALTER DATABASE SCOPED CONFIGURATION SET</c> option
/// takes, which also fixes the base type <c>sys.database_scoped_configurations</c>
/// reports its value in.
/// </summary>
internal enum ScopedConfigurationKind : byte
{
    /// <summary><c>ON</c> / <c>OFF</c>, reported as <c>bit</c>.</summary>
    Bit,

    /// <summary><c>MAXDOP</c>'s 0–32767, reported as <c>int</c>.</summary>
    DegreeOfParallelism,

    /// <summary><c>OFF</c> / <c>WHEN_SUPPORTED</c> / <c>FAIL_UNSUPPORTED</c>, reported as <c>nvarchar</c>.</summary>
    Elevate,

    /// <summary>0–71582 minutes, reported as <c>int</c>.</summary>
    Minutes,

    /// <summary>1 or 2, reported as <c>smallint</c>.</summary>
    FullTextIndexVersion,

    /// <summary>A string or <c>OFF</c>, reported as <c>nvarchar</c>.</summary>
    LedgerEndpoint,
}

/// <summary>
/// One option <c>sys.database_scoped_configurations</c> lists, as SQL Server
/// 2025 lists it (probed 2026-09-27): its <c>configuration_id</c>, name, value
/// grammar and default, and — for an option only the primary replica may
/// carry — the state and the name real's Msg 12110 reports for a
/// <c>FOR SECONDARY</c> attempt, which isn't always the option's own.
/// </summary>
internal sealed class ScopedConfigurationOption(int id, string name, ScopedConfigurationKind kind, string defaultValue, byte primaryOnlyState = 0, string? primaryOnlyName = null)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public readonly ScopedConfigurationKind Kind = kind;
    public readonly string DefaultValue = defaultValue;

    /// <summary>Msg 12110's state for a <c>FOR SECONDARY</c> set; 0 when the secondary may carry its own value.</summary>
    public readonly byte PrimaryOnlyState = primaryOnlyState;

    /// <summary>The option name Msg 12110 reports.</summary>
    public readonly string PrimaryOnlyName = primaryOnlyName ?? name;
}

/// <summary>
/// A database's scoped configuration: the primary and secondary value of
/// every option <c>sys.database_scoped_configurations</c> lists, as the text
/// real reports (<c>"1"</c>, <c>"WHEN_SUPPORTED"</c>), a null secondary
/// meaning the primary's value applies. Only
/// <c>VERBOSE_TRUNCATION_WARNINGS</c>, <c>PREVIEW_FEATURES</c> and <c>TSQL_SCALAR_UDF_INLINING</c> drive behavior; the rest are recorded
/// for the catalog.
/// </summary>
internal sealed class DatabaseScopedConfiguration
{
    /// <summary>Every option, in <c>configuration_id</c> order.</summary>
    public static readonly ScopedConfigurationOption[] Options =
    [
        new(1, "MAXDOP", ScopedConfigurationKind.DegreeOfParallelism, "0"),
        new(2, "LEGACY_CARDINALITY_ESTIMATION", ScopedConfigurationKind.Bit, "0"),
        new(3, "PARAMETER_SNIFFING", ScopedConfigurationKind.Bit, "1"),
        new(4, "QUERY_OPTIMIZER_HOTFIXES", ScopedConfigurationKind.Bit, "0"),
        new(6, "IDENTITY_CACHE", ScopedConfigurationKind.Bit, "1", primaryOnlyState: 1),
        new(7, "INTERLEAVED_EXECUTION_TVF", ScopedConfigurationKind.Bit, "1"),
        new(8, "BATCH_MODE_MEMORY_GRANT_FEEDBACK", ScopedConfigurationKind.Bit, "1"),
        new(9, "BATCH_MODE_ADAPTIVE_JOINS", ScopedConfigurationKind.Bit, "1"),
        new(10, "TSQL_SCALAR_UDF_INLINING", ScopedConfigurationKind.Bit, "1"),
        new(11, "ELEVATE_ONLINE", ScopedConfigurationKind.Elevate, "OFF", primaryOnlyState: 2),
        new(12, "ELEVATE_RESUMABLE", ScopedConfigurationKind.Elevate, "OFF", primaryOnlyState: 3),
        new(13, "OPTIMIZE_FOR_AD_HOC_WORKLOADS", ScopedConfigurationKind.Bit, "0"),
        new(14, "XTP_PROCEDURE_EXECUTION_STATISTICS", ScopedConfigurationKind.Bit, "0"),
        new(15, "XTP_QUERY_EXECUTION_STATISTICS", ScopedConfigurationKind.Bit, "0"),
        new(16, "ROW_MODE_MEMORY_GRANT_FEEDBACK", ScopedConfigurationKind.Bit, "1"),
        new(17, "ISOLATE_SECURITY_POLICY_CARDINALITY", ScopedConfigurationKind.Bit, "0"),
        new(18, "BATCH_MODE_ON_ROWSTORE", ScopedConfigurationKind.Bit, "1"),
        new(19, "DEFERRED_COMPILATION_TV", ScopedConfigurationKind.Bit, "1"),
        new(20, "ACCELERATED_PLAN_FORCING", ScopedConfigurationKind.Bit, "1"),
        new(21, "GLOBAL_TEMPORARY_TABLE_AUTO_DROP", ScopedConfigurationKind.Bit, "1", primaryOnlyState: 4, primaryOnlyName: "DISABLE_GLOBAL_TEMP_TABLE_AUTODROP"),
        new(22, "LIGHTWEIGHT_QUERY_PROFILING", ScopedConfigurationKind.Bit, "1"),
        new(23, "VERBOSE_TRUNCATION_WARNINGS", ScopedConfigurationKind.Bit, "1"),
        new(24, "LAST_QUERY_PLAN_STATS", ScopedConfigurationKind.Bit, "0"),
        new(25, "PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES", ScopedConfigurationKind.Minutes, "1440", primaryOnlyState: 5, primaryOnlyName: "AUTO_ABORT_PAUSED_INDEX"),
        new(27, "EXEC_QUERY_STATS_FOR_SCALAR_FUNCTIONS", ScopedConfigurationKind.Bit, "1"),
        new(28, "PARAMETER_SENSITIVE_PLAN_OPTIMIZATION", ScopedConfigurationKind.Bit, "1"),
        new(29, "ASYNC_STATS_UPDATE_WAIT_AT_LOW_PRIORITY", ScopedConfigurationKind.Bit, "0"),
        new(31, "CE_FEEDBACK", ScopedConfigurationKind.Bit, "1"),
        new(33, "MEMORY_GRANT_FEEDBACK_PERSISTENCE", ScopedConfigurationKind.Bit, "1"),
        new(34, "MEMORY_GRANT_FEEDBACK_PERCENTILE_GRANT", ScopedConfigurationKind.Bit, "1"),
        new(35, "OPTIMIZED_PLAN_FORCING", ScopedConfigurationKind.Bit, "1"),
        new(37, "DOP_FEEDBACK", ScopedConfigurationKind.Bit, "1"),
        new(38, "LEDGER_DIGEST_STORAGE_ENDPOINT", ScopedConfigurationKind.LedgerEndpoint, "OFF"),
        new(39, "FORCE_SHOWPLAN_RUNTIME_PARAMETER_COLLECTION", ScopedConfigurationKind.Bit, "0"),
        new(40, "READABLE_SECONDARY_TEMPORARY_STATS_AUTO_CREATE", ScopedConfigurationKind.Bit, "1"),
        new(41, "READABLE_SECONDARY_TEMPORARY_STATS_AUTO_UPDATE", ScopedConfigurationKind.Bit, "1"),
        new(42, "OPTIMIZED_SP_EXECUTESQL", ScopedConfigurationKind.Bit, "0"),
        new(44, "FULLTEXT_INDEX_VERSION", ScopedConfigurationKind.FullTextIndexVersion, "2"),
        new(46, "CE_FEEDBACK_FOR_EXPRESSIONS", ScopedConfigurationKind.Bit, "1"),
        new(47, "OPTIONAL_PARAMETER_OPTIMIZATION", ScopedConfigurationKind.Bit, "1"),
        new(48, "PREVIEW_FEATURES", ScopedConfigurationKind.Bit, "0", primaryOnlyState: 6),
    ];

    /// <summary>
    /// The <c>configuration_id</c> real lists for user databases only: the
    /// four system databases report every other row (probed 2026-09-27).
    /// </summary>
    public const int UserDatabaseOnlyId = 48;

    private static readonly int VerboseTruncationWarningsIndex = IndexOf("VERBOSE_TRUNCATION_WARNINGS");

    private readonly string[] primary = [.. Options.Select(option => option.DefaultValue)];

    private readonly string?[] secondary = new string?[Options.Length];

    /// <summary>The option named <paramref name="name"/> (case-insensitive), or -1.</summary>
    public static int IndexOf(string name)
    {
        for (var i = 0; i < Options.Length; i++)
        {
            if (Options[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The primary value of the option at <paramref name="index"/>.</summary>
    public string Primary(int index) => this.primary[index];

    /// <summary>The secondary value of the option at <paramref name="index"/>, or null when the primary's applies.</summary>
    public string? Secondary(int index) => this.secondary[index];

    /// <summary>Sets the primary value, or with <paramref name="forSecondary"/> the secondary one (null restoring <c>PRIMARY</c>).</summary>
    public void Set(int index, bool forSecondary, string? value)
    {
        lock (this.primary)
        {
            if (forSecondary)
                this.secondary[index] = value;
            else
                this.primary[index] = value!;
        }
    }

    /// <summary>
    /// Takes every value <paramref name="source"/> carries — a new database
    /// starts from <c>model</c>'s configuration, as real's does (probed
    /// 2026-09-27).
    /// </summary>
    public void CopyFrom(DatabaseScopedConfiguration source)
    {
        lock (source.primary)
        {
            source.primary.CopyTo(this.primary, 0);
            source.secondary.CopyTo(this.secondary, 0);
        }
    }

    /// <summary>
    /// <c>VERBOSE_TRUNCATION_WARNINGS</c>, which with a compatibility level of
    /// 150 or more selects Msg 2628 over Msg 8152 for string truncation.
    /// </summary>
    public bool VerboseTruncationWarnings => this.primary[VerboseTruncationWarningsIndex] == "1";

    private static readonly int PreviewFeaturesIndex = IndexOf("PREVIEW_FEATURES");

    private static readonly int TsqlScalarUdfInliningIndex = IndexOf("TSQL_SCALAR_UDF_INLINING");

    /// <summary>
    /// <c>TSQL_SCALAR_UDF_INLINING</c>, without which the optimizer inlines no
    /// scalar function into a query compiled in the database.
    /// </summary>
    public bool TsqlScalarUdfInlining => this.primary[TsqlScalarUdfInliningIndex] == "1";

    /// <summary>
    /// <c>PREVIEW_FEATURES</c>, which admits SQL Server 2025's preview
    /// surface: vector indexes, <c>VECTOR_SEARCH</c> and the <c>float16</c>
    /// vector base type.
    /// </summary>
    public bool PreviewFeatures => this.primary[PreviewFeaturesIndex] == "1";

    /// <summary>
    /// The <c>sql_variant</c> inner value <c>sys.database_scoped_configurations</c>
    /// reports for <paramref name="value"/>: <c>bit</c>, <c>int</c>,
    /// <c>smallint</c>, or <c>nvarchar</c> sized as real sizes each column
    /// (<c>SQL_VARIANT_PROPERTY(value, 'MaxLength')</c> 32 and 2004).
    /// </summary>
    public static SqlValue ToSqlValue(ScopedConfigurationKind kind, string value) => kind switch
    {
        ScopedConfigurationKind.Bit => SqlValue.FromBoolean(value == "1"),
        ScopedConfigurationKind.DegreeOfParallelism or ScopedConfigurationKind.Minutes => SqlValue.FromInt32(int.Parse(value, CultureInfo.InvariantCulture)),
        ScopedConfigurationKind.FullTextIndexVersion => SqlValue.FromInt16(short.Parse(value, CultureInfo.InvariantCulture)),
        ScopedConfigurationKind.Elevate => SqlValue.FromNVarchar(NVarcharSqlType.Get(16, Collation.Baseline, Coercibility.Implicit), value),
        _ => SqlValue.FromNVarchar(NVarcharSqlType.Get(1002, Collation.Baseline, Coercibility.Implicit), value),
    };
}
