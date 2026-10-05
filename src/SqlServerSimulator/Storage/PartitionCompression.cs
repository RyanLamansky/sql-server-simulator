namespace SqlServerSimulator.Storage;

/// <summary>
/// One <c>DATA_COMPRESSION = level ON PARTITIONS (…)</c> clause: the level
/// (0 <c>NONE</c>, 1 <c>ROW</c>, 2 <c>PAGE</c>) and the partition numbers and
/// <c>n TO m</c> ranges it lists, a single number as a range of one.
/// </summary>
internal readonly struct PartitionCompressionClause(byte level, List<(long Low, long High)> ranges)
{
    public readonly byte Level = level;
    public readonly List<(long Low, long High)> Ranges = ranges;
}

/// <summary>
/// The <c>DATA_COMPRESSION</c> of each partition of a partitioned rowset — the
/// heap, an index or a key constraint — once its partitions differ: a list
/// indexed by partition number less one that overrides the rowset's own level,
/// null while every partition shares it (probed 2026-10-05 against SQL Server
/// 2025). A <c>SPLIT RANGE</c> gives the new partition the level of the one it
/// split, and a <c>MERGE RANGE</c> keeps the surviving partition's.
/// </summary>
internal static class PartitionCompression
{
    /// <summary>The level of <paramref name="partition"/>: its own when the rowset keeps one per partition, else the rowset's.</summary>
    public static byte LevelOf(byte level, List<byte>? partitions, int partition) =>
        partitions is not null && partition >= 1 && partition <= partitions.Count ? partitions[partition - 1] : level;

    /// <summary>
    /// The per-partition levels once <paramref name="clauses"/> apply over
    /// <paramref name="fanout"/> partitions: each listed partition takes its
    /// clause's level and the rest keep what they had. A number outside the
    /// partitions is <paramref name="outOfRange"/>'s refusal (Msg 7722), a
    /// reversed range Msg 7728, and a partition listed twice Msg 7711.
    /// </summary>
    public static List<byte> Apply(byte level, List<byte>? existing, int fanout, List<PartitionCompressionClause> clauses, Func<long, SimulatedSqlException> outOfRange)
    {
        var result = new List<byte>(fanout);
        for (var i = 1; i <= fanout; i++)
            result.Add(LevelOf(level, existing, i));
        var listed = new bool[fanout];
        foreach (var clause in clauses)
        {
            foreach (var (low, high) in clause.Ranges)
            {
                if (low < 1 || low > fanout)
                    throw outOfRange(low);
                if (high < 1 || high > fanout)
                    throw outOfRange(high);
                if (low > high)
                    throw SimulatedSqlException.InvalidPartitionRange(low, high);
                for (var number = low; number <= high; number++)
                {
                    if (listed[number - 1])
                        throw SimulatedSqlException.DataCompressionSpecifiedTwice();
                    listed[number - 1] = true;
                    result[(int)number - 1] = clause.Level;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Follows a <c>SPLIT RANGE</c> or <c>MERGE RANGE</c> through
    /// <paramref name="partitions"/>: a split inserts at <paramref name="slot"/>
    /// a copy of the level of <paramref name="splitPartition"/> (0-based, as it
    /// stood), and a merge removes <paramref name="slot"/>.
    /// </summary>
    public static void Reshape(List<byte>? partitions, bool split, int slot, int splitPartition)
    {
        if (partitions is null || slot < 0 || slot > partitions.Count)
            return;
        if (split)
            partitions.Insert(slot, partitions[Math.Clamp(splitPartition, 0, partitions.Count - 1)]);
        else if (slot < partitions.Count)
            partitions.RemoveAt(slot);
    }
}
