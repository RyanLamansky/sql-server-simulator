namespace SqlServerSimulator;

/// <summary>
/// One file of a <see cref="Database"/>: the row <c>sys.database_files</c> /
/// <c>sys.master_files</c> / <c>sp_helpfile</c> / <c>FILEPROPERTY</c> report.
/// There is no physical file behind it — the fields are catalog state, which
/// <c>CREATE DATABASE</c>'s file list and <c>ALTER DATABASE … ADD | MODIFY |
/// REMOVE FILE</c> maintain. Sizes are 8 KB pages, rounded up to a whole
/// 64 KB extent as real rounds them (probed 2026-09-27 against SQL Server
/// 2025).
/// </summary>
internal sealed class DatabaseFile(int fileId, bool isLog, string name, string physicalName, int dataSpaceId, int sizePages, int maxSizePages, int growth, bool isPercentGrowth, bool isContainer = false)
{
    /// <summary>
    /// A container of the <c>MEMORY_OPTIMIZED_DATA</c> filegroup: a directory
    /// real reports as a <c>FILESTREAM</c> file (<c>type</c> 2), numbered from
    /// 65537, with no size, growth or ceiling, and which the size-reporting
    /// surfaces (<c>sp_helpfile</c>, <c>FILE_ID</c>, <c>FILEPROPERTY</c>) leave
    /// out (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    public readonly bool IsContainer = isContainer;

    /// <summary>1 for the primary data file, 2 for the primary log file, then the lowest id no file holds.</summary>
    public readonly int FileId = fileId;

    /// <summary>A log file (<c>type</c> 1) rather than a rows file (<c>type</c> 0).</summary>
    public readonly bool IsLog = isLog;

    /// <summary>The logical name, unique within the database.</summary>
    public string Name = name;

    /// <summary>The operating-system path, unique across the instance.</summary>
    public string PhysicalName = physicalName;

    /// <summary>The filegroup's <c>data_space_id</c>; 0 for a log file.</summary>
    public int DataSpaceId = dataSpaceId;

    /// <summary>
    /// The size a <c>SIZE</c> clause last set. The primary data file reports
    /// the larger of this and the pages its data occupies
    /// (<see cref="BuiltInResources.FileSizePages"/>), since every row lands there.
    /// </summary>
    public int SizePages = sizePages;

    /// <summary>The ceiling in pages; -1 for unlimited, which a log file reports as its 2 TB cap.</summary>
    public int MaxSizePages = maxSizePages;

    /// <summary>The growth increment: pages, or a percentage when <see cref="IsPercentGrowth"/>.</summary>
    public int Growth = growth;

    /// <summary>Whether <see cref="Growth"/> is a percentage.</summary>
    public bool IsPercentGrowth = isPercentGrowth;

    /// <summary>The primary data file (1) or the primary log file (2), neither of which can be removed.</summary>
    public bool IsPrimary => this.FileId is 1 or 2;
}
