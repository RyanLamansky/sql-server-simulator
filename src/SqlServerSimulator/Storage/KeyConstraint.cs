namespace SqlServerSimulator.Storage;

/// <summary>
/// Discriminator for <see cref="KeyConstraint"/>: <c>PRIMARY KEY</c> or
/// <c>UNIQUE</c>. Drives both the violation-message wording (Msg 2627
/// prints <c>"PRIMARY KEY"</c> vs <c>"UNIQUE KEY"</c>) and the auto-name
/// prefix (<c>PK__</c> vs <c>UQ__</c>).
/// </summary>
internal enum KeyConstraintKind
{
    PrimaryKey,
    Unique,
}

/// <summary>
/// One entry in <see cref="HeapTable.KeyConstraints"/>. Stores the constraint's
/// kind, its name (user-supplied or auto-generated), and the storage-ordinal
/// list of the columns participating in the key. Storage ordinals (not
/// declaration ordinals) so the enforcement loop can decode key columns
/// directly from row bytes via <see cref="RowDecoder"/>.
/// </summary>
internal sealed class KeyConstraint(KeyConstraintKind kind, string name, int[] storageOrdinals, int objectId, bool isClustered)
{
    public readonly KeyConstraintKind Kind = kind;

    public readonly string Name = name;

    public readonly int[] StorageOrdinals = storageOrdinals;

    /// <summary>
    /// Whether this constraint's backing index is the table's clustered index.
    /// A PRIMARY KEY defaults clustered (unless declared <c>NONCLUSTERED</c>);
    /// a UNIQUE constraint defaults nonclustered (unless declared
    /// <c>CLUSTERED</c>). Drives index-id allocation in
    /// <see cref="HeapTable.IndexIdentities"/> — a clustered constraint takes
    /// <c>index_id = 1</c> and suppresses the HEAP row. At most one clustered
    /// index exists per table (real SQL Server's Msg 1902 invariant; not
    /// enforced here — an over-declared second clustered entry falls back to a
    /// nonclustered id).
    /// </summary>
    public readonly bool IsClustered = isClustered;

    /// <summary>
    /// Per-database object identifier for this constraint — allocated at
    /// CREATE TABLE alongside the table itself. Surfaces in
    /// <c>sys.objects</c> as a <c>PK</c> / <c>UQ</c> row with
    /// <c>parent_object_id</c> linking back to the owning table.
    /// </summary>
    public readonly int ObjectId = objectId;

    /// <summary>
    /// The phrase SQL Server emits in Msg 2627 for this constraint kind:
    /// <c>"PRIMARY KEY"</c> or <c>"UNIQUE KEY"</c>. SQL Server uses the
    /// <c>UNIQUE KEY</c> wording — not <c>UNIQUE</c> — for unique-constraint
    /// violations; the constraint type itself is still spelled <c>UNIQUE</c>
    /// in DDL.
    /// </summary>
    public string ViolationKindWord => this.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE KEY";
}
