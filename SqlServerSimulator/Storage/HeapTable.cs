namespace SqlServerSimulator.Storage;

/// <summary>
/// A user table: schema is <see cref="HeapColumn"/>s typed in
/// <see cref="SqlType"/>; rows are stored in an 8KB-page <see cref="Heap"/>
/// whose page bytes are produced by <see cref="RowEncoder.EncodeRow"/>.
/// </summary>
internal sealed class HeapTable
{
    public HeapTable(string name, HeapColumn[] columns)
    {
        this.Name = name;
        this.Columns = columns;
        var schema = new SqlType[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            schema[i] = columns[i].Type;
        this.Schema = schema;
    }

    public readonly string Name;

    public readonly HeapColumn[] Columns;

    /// <summary>
    /// Per-column types in declaration order; the array passed to
    /// <see cref="RowEncoder.EncodeRow"/> and <see cref="RowDecoder.DecodeColumn"/>.
    /// </summary>
    public readonly SqlType[] Schema;

    /// <summary>The page-backed row store. Insert via <see cref="Heap.Insert"/>; iterate via <see cref="Heap.EnumerateRows"/>.</summary>
    public readonly Heap Heap = new();

    /// <summary>Iterates the rows in allocation order, paging through the underlying <see cref="Heap"/>.</summary>
    public IEnumerable<byte[]> Rows => this.Heap.EnumerateRows();

#if DEBUG
    public override string ToString() => $"{this.Name} ({string.Join(", ", this.Columns.Select(c => c.Name))})";
#endif
}
