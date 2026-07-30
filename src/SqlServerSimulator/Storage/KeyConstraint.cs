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
internal sealed class KeyConstraint(KeyConstraintKind kind, string name, int[] storageOrdinals, int objectId, bool isClustered, bool ignoreDupKey, bool[]? descending = null)
{
    public readonly KeyConstraintKind Kind = kind;

    public readonly string Name = name;

    public readonly int[] StorageOrdinals = storageOrdinals;

    /// <summary>
    /// Per-key-column <c>DESC</c> flags, parallel to
    /// <see cref="StorageOrdinals"/>; an all-ascending key may pass
    /// <c>null</c> and reads as ascending throughout.
    /// Captured from the <c>PRIMARY KEY (a DESC, b)</c> / <c>UNIQUE (…)</c>
    /// column list at CREATE TABLE and ALTER TABLE ADD CONSTRAINT, and
    /// surfaced as <c>sys.index_columns.is_descending_key</c> — the same
    /// flag <see cref="Index"/> carries on <see cref="IndexKeyColumn"/> for a
    /// CREATE INDEX key.
    /// No runtime effect: the simulator stores rows unordered, so direction
    /// is metadata a schema-diff or index-scripting tool reads.
    /// Real rejects the direction on the *inline* column-level form
    /// (<c>a int PRIMARY KEY DESC</c> → Msg 156), so only the table-level and
    /// ALTER forms ever populate this.
    /// </summary>
    private readonly bool[]? descendingFlags = descending;

    /// <summary>
    /// Whether key column <paramref name="index"/> (a position in
    /// <see cref="StorageOrdinals"/>) was declared <c>DESC</c>.
    /// </summary>
    public bool IsDescending(int index) =>
        this.descendingFlags is { } flags && (uint)index < (uint)flags.Length && flags[index];

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
    /// <summary>
    /// <c>IGNORE_DUP_KEY</c> as declared in the constraint's <c>WITH (…)</c>
    /// clause: an INSERT whose row would duplicate this key skips that row and
    /// continues, instead of raising Msg 2627. Readonly because real refuses to
    /// change it afterwards — <c>ALTER INDEX … SET</c> on a constraint-backed
    /// index raises Msg 1979 — even though it accepts the option at declaration.
    /// See <c>docs/claude/constraints.md</c>.
    /// </summary>
    public readonly bool IgnoreDupKey = ignoreDupKey;

    /// <summary>
    /// Whether <c>ALTER INDEX … DISABLE</c> has taken the constraint's backing
    /// index out of service — real allows that on a constraint even though it
    /// refuses to change the constraint's IGNORE_DUP_KEY (Msg 1979). While
    /// disabled the constraint isn't enforced at all; REBUILD restores it and
    /// re-validates (Msg 1505 on a duplicate). A disabled clustered constraint
    /// index locks the table (Msg 8655), which is the common case since a
    /// PRIMARY KEY defaults clustered.
    /// See <c>docs/claude/indexes.md</c>.
    /// </summary>
    public bool IsDisabled;

    public string ViolationKindWord => this.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE KEY";
}
