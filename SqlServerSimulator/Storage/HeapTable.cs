using System.Diagnostics;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A user table: schema is <see cref="HeapColumn"/>s typed in
/// <see cref="SqlType"/>; rows are stored in an 8KB-page <see cref="Heap"/>
/// whose page bytes are produced by <see cref="RowEncoder"/>.
/// </summary>
[DebuggerDisplay("{DebugDisplay(),nq}")]
internal sealed class HeapTable
{
    public HeapTable(string name, HeapColumn[] columns, KeyConstraint[]? keyConstraints = null)
    {
        this.Name = name;
        this.Columns = columns;
        this.KeyConstraints = keyConstraints ?? [];

        var storedCount = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].IsStored)
                storedCount++;
        }
        var storedColumns = new HeapColumn[storedCount];
        var schema = new SqlType[storedCount];
        var storageOrdinals = new int[columns.Length];
        var s = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].IsStored)
            {
                storedColumns[s] = columns[i];
                schema[s] = columns[i].Type;
                storageOrdinals[i] = s;
                s++;
            }
            else
            {
                storageOrdinals[i] = -1;
            }
        }
        this.StoredColumns = storedColumns;
        this.Schema = schema;
        this.StorageOrdinals = storageOrdinals;
    }

    public readonly string Name;

    /// <summary>
    /// Full column set in declaration order, the surface area used for name
    /// binding and SQL-ordinal addressing. Includes non-persisted computed
    /// columns; those have <see cref="StorageOrdinals"/> entry <c>-1</c>.
    /// </summary>
    public readonly HeapColumn[] Columns;

    /// <summary>
    /// Subset of <see cref="Columns"/> that participates in row storage —
    /// regular columns plus persisted computed columns. The schema passed
    /// to <see cref="RowEncoder"/> and <see cref="RowDecoder"/>; ordinals
    /// here index into the encoded row's column slots.
    /// </summary>
    public readonly HeapColumn[] StoredColumns;

    /// <summary>
    /// Ordinal of the table's identity column, or <c>-1</c> if there isn't
    /// one. SQL Server allows at most one identity column per table.
    /// </summary>
    public int IdentityOrdinal
    {
        get
        {
            for (var i = 0; i < this.Columns.Length; i++)
            {
                if (this.Columns[i].Identity is not null)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// Storage-ordinal mapping: <c>StorageOrdinals[i]</c> is the index in
    /// <see cref="StoredColumns"/> of <c>Columns[i]</c>, or <c>-1</c> when
    /// <c>Columns[i]</c> is a non-persisted computed column with no row
    /// slot. Identity on regular tables (no computed columns) collapses to
    /// <c>StorageOrdinals[i] == i</c>.
    /// </summary>
    public readonly int[] StorageOrdinals;

    /// <summary>
    /// Stored-column types in storage order; the array passed to
    /// <see cref="RowEncoder"/> and <see cref="RowDecoder"/>. Length matches
    /// <see cref="StoredColumns"/>, not <see cref="Columns"/>.
    /// </summary>
    public readonly SqlType[] Schema;

    /// <summary>
    /// PRIMARY KEY and UNIQUE constraints declared in the CREATE TABLE
    /// statement, in declaration order. Enforced linear-scan at INSERT/MERGE
    /// by <c>EnforceKeyConstraints</c>; SQL Server's NULLs-equal-for-UNIQUE
    /// rule applies. Empty when the table declares neither.
    /// </summary>
    public readonly KeyConstraint[] KeyConstraints;

    /// <summary>The page-backed row store. Insert via <see cref="Heap.Insert"/>; iterate via <see cref="Heap.EnumerateRows"/>.</summary>
    public readonly Heap Heap = new();

    /// <summary>Iterates the rows in allocation order, paging through the underlying <see cref="Heap"/>.</summary>
    public IEnumerable<byte[]> Rows => this.Heap.EnumerateRows();

    internal string DebugDisplay() => $"{this.Name} ({string.Join(", ", this.Columns.Select(c => c.Name))})";
}
