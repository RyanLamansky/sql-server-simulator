namespace SqlServerSimulator.Storage;

/// <summary>
/// What an index's <c>WITH</c> clause recorded, for an index or the index
/// behind a PRIMARY KEY / UNIQUE constraint: <c>IGNORE_DUP_KEY</c>, which
/// changes how a duplicate key is handled, and <c>FILLFACTOR</c> /
/// <c>PAD_INDEX</c>, which only the catalog reports. The latter two are null
/// when the clause didn't name them, so an <c>ALTER INDEX … REBUILD</c>
/// keeps what it leaves out.
/// </summary>
internal readonly struct IndexOptions(
    bool ignoreDupKey,
    byte? fillFactor,
    bool? padIndex,
    bool dropExisting = false,
    int? compressionDelay = null,
    bool? columnstoreArchive = null,
    bool? allowRowLocks = null,
    bool? allowPageLocks = null,
    bool? optimizeForSequentialKey = null,
    Schemas.DataSpaceClause? dataSpace = null,
    bool? statisticsNoRecompute = null,
    bool statisticsOnly = false,
    int? bucketCount = null,
    bool isHash = false)
{
    public readonly bool IgnoreDupKey = ignoreDupKey;

    /// <summary>1 to 100 when given; <c>sys.indexes.fill_factor</c> reads 0 otherwise.</summary>
    public readonly byte? FillFactor = fillFactor;

    public readonly bool? PadIndex = padIndex;

    /// <summary><c>DROP_EXISTING = ON</c>: a <c>CREATE INDEX</c> replaces the index of that name.</summary>
    public readonly bool DropExisting = dropExisting;

    /// <summary>A columnstore index's <c>COMPRESSION_DELAY</c> in minutes, when given.</summary>
    public readonly int? CompressionDelay = compressionDelay;

    /// <summary>
    /// A columnstore index's <c>DATA_COMPRESSION</c> when given: true for
    /// <c>COLUMNSTORE_ARCHIVE</c>, false for <c>COLUMNSTORE</c>.
    /// </summary>
    public readonly bool? ColumnstoreArchive = columnstoreArchive;

    /// <summary><c>ALLOW_ROW_LOCKS</c>, when given; only the catalog reports it.</summary>
    public readonly bool? AllowRowLocks = allowRowLocks;

    /// <summary><c>ALLOW_PAGE_LOCKS</c>, when given; only the catalog reports it.</summary>
    public readonly bool? AllowPageLocks = allowPageLocks;

    /// <summary><c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c>, when given; only the catalog reports it.</summary>
    public readonly bool? OptimizeForSequentialKey = optimizeForSequentialKey;

    /// <summary><c>STATISTICS_NORECOMPUTE</c>, when given: <c>sys.stats.no_recompute</c> of the index's statistic.</summary>
    public readonly bool? StatisticsNoRecompute = statisticsNoRecompute;

    /// <summary>
    /// <c>STATISTICS_ONLY = n</c>, undocumented and any value alike: the index
    /// is hypothetical — a catalog entry and a statistic, never built.
    /// </summary>
    public readonly bool StatisticsOnly = statisticsOnly;

    /// <summary>
    /// A memory-optimized hash index's <c>BUCKET_COUNT</c> as written, when
    /// given; real rounds it up to a power of two.
    /// </summary>
    public readonly int? BucketCount = bucketCount;

    /// <summary>The index or key was declared <c>HASH</c>.</summary>
    public readonly bool IsHash = isHash;

    /// <summary>
    /// The bucket count a hash index declared with <paramref name="options"/>
    /// keeps: its <c>BUCKET_COUNT</c> rounded up to a power of two (probed
    /// 2026-10-02 against SQL Server 2025: 100 keeps 128), or 0 for any other
    /// index.
    /// </summary>
    public static int BucketCountFor(IndexOptions options) =>
        options.IsHash && options.BucketCount is int count ? (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count) : 0;

    /// <summary>These options declared <c>HASH</c>.</summary>
    public IndexOptions AsHash() =>
        new(this.IgnoreDupKey, this.FillFactor, this.PadIndex, this.DropExisting, this.CompressionDelay, this.ColumnstoreArchive,
            this.AllowRowLocks, this.AllowPageLocks, this.OptimizeForSequentialKey, this.DataSpace, this.StatisticsNoRecompute, this.StatisticsOnly, this.BucketCount, isHash: true);

    /// <summary>The <c>ON</c> placement clause written after the options, if any.</summary>
    public readonly Schemas.DataSpaceClause? DataSpace = dataSpace;

    /// <summary>These options with <paramref name="clause"/> as the placement.</summary>
    public IndexOptions WithDataSpace(Schemas.DataSpaceClause? clause) =>
        new(this.IgnoreDupKey, this.FillFactor, this.PadIndex, this.DropExisting, this.CompressionDelay, this.ColumnstoreArchive,
            this.AllowRowLocks, this.AllowPageLocks, this.OptimizeForSequentialKey, clause, this.StatisticsNoRecompute, this.StatisticsOnly, this.BucketCount, this.IsHash);
}
