namespace SqlServerSimulator.Storage;

/// <summary>
/// One standalone statistics object created by <c>CREATE STATISTICS</c> — the
/// column-list statistic real SQL Server's optimizer keeps outside any index.
/// </summary>
/// <remarks>
/// <para>
/// The simulator models the <em>declaration</em>, not the histogram: it makes
/// no cardinality estimates, so a statistic changes nothing about how a query
/// runs. What it does carry is catalog identity — <c>sys.stats</c> /
/// <c>sys.stats_columns</c> rows with <c>user_created = 1</c>, which is what
/// DacFx re-exports and SSMS scripts, and what a bacpac's
/// <c>SqlStatistic</c> elements round-trip through.
/// </para>
/// <para>
/// <see cref="StatsId"/> comes from the same per-table sequence index ids draw
/// from, continuing past the highest one in use at creation — the numbering
/// real reports, where an index-backed statistic shares its index's id and a
/// standalone one takes the next free slot.
/// </para>
/// </remarks>
internal sealed class UserStatistic(string name, int statsId, int[] columnFullOrdinals, bool noRecompute, DateTime createDate)
{
    public readonly string Name = name;

    /// <summary>Per-table id, unique against every index id on the table.</summary>
    public readonly int StatsId = statsId;

    /// <summary>
    /// The statistic's columns in declared order, as
    /// <see cref="HeapTable.Columns"/> indices. Order is load-bearing: the
    /// leading column is the one the histogram would describe.
    /// </summary>
    public readonly int[] ColumnFullOrdinals = columnFullOrdinals;

    /// <summary>Whether <c>WITH NORECOMPUTE</c> was declared.</summary>
    public readonly bool NoRecompute = noRecompute;

    public readonly DateTime CreateDate = createDate;
}
