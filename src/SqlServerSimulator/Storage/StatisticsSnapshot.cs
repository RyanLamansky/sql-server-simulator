namespace SqlServerSimulator.Storage;

/// <summary>
/// What one statistic knows about its table's data, built when the statistic
/// is built or updated and read until it is built again — so it describes the
/// rows as they stood then, which is what <c>DBCC SHOW_STATISTICS</c>,
/// <c>STATS_DATE</c> and <c>sys.dm_db_stats_properties</c> report on real.
/// A statistic built over an empty table has none (<see cref="StatisticsState.Snapshot"/>
/// stays null), which real reports as a header of NULLs and no histogram.
/// </summary>
internal sealed class StatisticsSnapshot(
    DateTime updated,
    long rows,
    long unfilteredRows,
    HistogramStep[] histogram,
    DensityVectorRow[] densityVector,
    float headerDensity,
    float averageKeyLength,
    bool stringIndex,
    SqlType keyType,
    long modificationBase)
{
    /// <summary>When the statistic was built, in the server's local time.</summary>
    public readonly DateTime Updated = updated;

    /// <summary>The rows the statistic describes: the table's, or those its filter admits.</summary>
    public readonly long Rows = rows;

    /// <summary>The table's rows when the statistic was built, filter or not.</summary>
    public readonly long UnfilteredRows = unfilteredRows;

    public readonly HistogramStep[] Histogram = histogram;

    public readonly DensityVectorRow[] DensityVector = densityVector;

    /// <summary>The header's <c>Density</c>: the range rows' own density, 0 when every value is a step.</summary>
    public readonly float HeaderDensity = headerDensity;

    /// <summary>The average byte length of the statistic's key over the rows it describes.</summary>
    public readonly float AverageKeyLength = averageKeyLength;

    /// <summary>Whether the leading column is a string, which the header's <c>String Index</c> reports.</summary>
    public readonly bool StringIndex = stringIndex;

    /// <summary>The leading column's type, which <c>RANGE_HI_KEY</c> carries.</summary>
    public readonly SqlType KeyType = keyType;

    /// <summary>
    /// The modification count of the leading column when the statistic was
    /// built (see <see cref="HeapTable.ModificationCount"/>), against which
    /// <c>modification_counter</c> and the auto-update threshold measure.
    /// </summary>
    public readonly long ModificationBase = modificationBase;
}

/// <summary>One histogram step: its upper bound and the rows in and below it.</summary>
internal readonly struct HistogramStep(SqlValue rangeHighKey, float rangeRows, float equalRows, long distinctRangeRows, float averageRangeRows)
{
    public readonly SqlValue RangeHighKey = rangeHighKey;
    public readonly float RangeRows = rangeRows;
    public readonly float EqualRows = equalRows;
    public readonly long DistinctRangeRows = distinctRangeRows;
    public readonly float AverageRangeRows = averageRangeRows;
}

/// <summary>One density-vector row: a key prefix's density, average length and column list.</summary>
internal readonly struct DensityVectorRow(float allDensity, float averageLength, string columns)
{
    public readonly float AllDensity = allDensity;
    public readonly float AverageLength = averageLength;
    public readonly string Columns = columns;
}

/// <summary>
/// The mutable statistics state an index, key constraint or standalone
/// statistic carries: its <see cref="Snapshot"/> and the two options real
/// records per statistic that the catalog reports.
/// </summary>
internal sealed class StatisticsState
{
    /// <summary>The data the statistic was last built over, or null when that was an empty table.</summary>
    public StatisticsSnapshot? Snapshot;

    /// <summary><c>sys.stats.has_persisted_sample</c>: built under <c>PERSIST_SAMPLE_PERCENT = ON</c>.</summary>
    public bool HasPersistedSample;

    /// <summary><c>sys.stats.auto_drop</c>: set by <c>AUTO_DROP = ON</c>, and on every auto-created statistic.</summary>
    public bool AutoDrop;
}
