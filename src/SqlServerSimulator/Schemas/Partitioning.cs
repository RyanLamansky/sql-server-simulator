using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A <c>CREATE PARTITION FUNCTION</c>: a database-scoped (not schema-scoped)
/// mapping from one value of <see cref="ParameterType"/> to a 1-based
/// partition number, by where the value falls among the sorted
/// <see cref="Boundaries"/>. It isn't an object, so it takes no object id and
/// never shows in <c>sys.objects</c>; its id comes from the per-database
/// counter <see cref="Database.AllocatePartitionFunctionId"/>.
/// </summary>
internal sealed class PartitionFunction(string name, int functionId, SqlType parameterType, int? declaredMaxLength, bool boundaryOnRight, SqlValue[] boundaries, DateTime createDate)
{
    public readonly string Name = name;

    public readonly int FunctionId = functionId;

    /// <summary>The parameter type, a string one carrying its collation.</summary>
    public readonly SqlType ParameterType = parameterType;

    /// <summary>The declared width, the <c>max_length</c> <c>sys.partition_parameters</c> reports alongside the type.</summary>
    public readonly int? DeclaredMaxLength = declaredMaxLength;

    /// <summary><c>RANGE RIGHT</c>: a boundary value belongs to the partition on its right.</summary>
    public readonly bool BoundaryOnRight = boundaryOnRight;

    /// <summary>
    /// The boundary values in ascending order, each of <see cref="ParameterType"/>;
    /// a NULL boundary sorts first. Replaced whole by <c>SPLIT</c> /
    /// <c>MERGE RANGE</c>, so a reader holding the array sees one consistent list.
    /// </summary>
    public SqlValue[] Boundaries = boundaries;

    public readonly DateTime CreateDate = createDate;

    public DateTime ModifyDate = createDate;

    /// <summary>The partition count, one more than the boundary count.</summary>
    public int Fanout => this.Boundaries.Length + 1;

    /// <summary>
    /// The 1-based partition <paramref name="value"/> (already of
    /// <see cref="ParameterType"/>) lands in: one past the count of boundaries
    /// below it for <c>RANGE LEFT</c>, or at or below it for <c>RANGE RIGHT</c>,
    /// with NULL ordered below every value.
    /// </summary>
    public int PartitionOf(SqlValue value)
    {
        if (!value.IsNull && value.Type != this.ParameterType)
            value = value.CoerceTo(this.ParameterType);
        var boundaries = this.Boundaries;
        int low = 0, high = boundaries.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            var comparison = Compare(boundaries[middle], value);
            if (comparison < 0 || (comparison == 0 && this.BoundaryOnRight))
                low = middle + 1;
            else
                high = middle;
        }
        return low + 1;
    }

    /// <summary>Orders two values of the parameter type, NULL lowest.</summary>
    public static int Compare(SqlValue left, SqlValue right) =>
        left.IsNull ? (right.IsNull ? 0 : -1) : right.IsNull ? 1 : left.CompareTo(right);
}

/// <summary>
/// A <c>CREATE PARTITION SCHEME</c>: a data space mapping each partition of
/// <see cref="Function"/> to a filegroup. Shares the <c>sys.data_spaces</c>
/// name space with the filegroups, but takes its id from the separate
/// per-database counter <see cref="Database.AllocatePartitionSchemeId"/>.
/// </summary>
internal sealed class PartitionScheme(string name, int dataSpaceId, PartitionFunction function, List<int> destinations, int? nextUsed)
{
    public readonly string Name = name;

    public readonly int DataSpaceId = dataSpaceId;

    public readonly PartitionFunction Function = function;

    /// <summary>One filegroup <c>data_space_id</c> per partition, in partition order.</summary>
    public readonly List<int> Destinations = destinations;

    /// <summary>
    /// The <c>NEXT USED</c> filegroup the next <c>SPLIT RANGE</c> places its
    /// new partition on, or null when none is marked; listed as one more
    /// destination past the partitions in <c>sys.destination_data_spaces</c>.
    /// </summary>
    public int? NextUsed = nextUsed;
}

/// <summary>
/// Where a table's base rows or one of its indexes is placed when that place
/// is a partition scheme: the scheme plus the column whose value
/// <see cref="PartitionScheme.Function"/> maps to a partition. Held by
/// reference to the column object, so a rename carries through.
/// </summary>
internal sealed class PartitionPlacement(PartitionScheme scheme, HeapColumn column)
{
    public readonly PartitionScheme Scheme = scheme;

    public readonly HeapColumn Column = column;

    /// <summary>The partition count, the scheme's function's fanout.</summary>
    public int Fanout => this.Scheme.Function.Fanout;

    /// <summary>
    /// The storage ordinal <see cref="Column"/> sits at in
    /// <paramref name="table"/>'s rows, which a dropped column shifts.
    /// </summary>
    public int StorageOrdinal(HeapTable table) => table.StorageOrdinals[Array.IndexOf(table.Columns, this.Column)];

    /// <summary>The 1-based partition one encoded row of <paramref name="table"/> belongs to.</summary>
    public int PartitionOfRow(HeapTable table, int storageOrdinal, byte[] row) =>
        this.Scheme.Function.PartitionOf(RowDecoder.DecodeColumn(table.StoredColumns, row, storageOrdinal, table.Heap));

    /// <summary>
    /// Each partition's live row count and the number of heap pages holding
    /// at least one of its rows, indexed by partition number less one. Rows
    /// aren't stored apart by partition, so this reads every row.
    /// </summary>
    public (long[] Rows, long[] Pages) Census(HeapTable table)
    {
        var fanout = this.Fanout;
        var rows = new long[fanout];
        var pages = new long[fanout];
        var lastPage = new int[fanout];
        Array.Fill(lastPage, -1);
        var storageOrdinal = this.StorageOrdinal(table);
        foreach (var (pageIndex, _, bytes) in table.Heap.EnumerateRowsWithAddress())
        {
            var partition = this.PartitionOfRow(table, storageOrdinal, bytes) - 1;
            rows[partition]++;
            if (lastPage[partition] != pageIndex)
            {
                lastPage[partition] = pageIndex;
                pages[partition]++;
            }
        }
        return (rows, pages);
    }
}

/// <summary>
/// An <c>ON name</c> or <c>ON scheme(column [, …])</c> placement clause as a
/// <c>CREATE TABLE</c>, <c>CREATE INDEX</c> or key constraint wrote it,
/// resolved against the database only when the statement runs.
/// </summary>
internal sealed class DataSpaceClause(string name, List<string>? columns)
{
    public readonly string Name = name;

    /// <summary>The parenthesized partition column list, null when none was written.</summary>
    public readonly List<string>? Columns = columns;
}
