namespace SqlServerSimulator.Storage;

/// <summary>
/// Referential action assigned to a <c>FOREIGN KEY</c> constraint's
/// <c>ON DELETE</c> / <c>ON UPDATE</c> clause. The four codes mirror SQL
/// Server's <c>sys.foreign_keys.delete_referential_action</c> /
/// <c>update_referential_action</c> integer codes (probe-confirmed against
/// SQL Server 2025): 0 = NO_ACTION, 1 = CASCADE, 2 = SET_NULL, 3 = SET_DEFAULT.
/// </summary>
internal enum ReferentialAction
{
    NoAction = 0,
    Cascade = 1,
    SetNull = 2,
    SetDefault = 3,
}

/// <summary>
/// One entry in <see cref="HeapTable.OutgoingForeignKeys"/> on the child side
/// and <see cref="HeapTable.IncomingForeignKeys"/> on the parent side. Captures
/// the FK constraint's name, the column-ordinal mapping between child and
/// referenced parent, and the <c>ON DELETE</c> / <c>ON UPDATE</c> actions.
/// </summary>
/// <remarks>
/// <para>
/// Column ordinals are <em>full</em> ordinals (into <see cref="HeapTable.Columns"/>),
/// not storage ordinals. The enforcement loop materializes the affected row
/// in SqlValue form before reading the FK columns, so full ordinals keep the
/// dispatch self-contained.
/// </para>
/// <para>
/// A self-referencing FK has <see cref="ChildTable"/> == <see cref="ReferencedTable"/>;
/// Msg 547 substitutes <c>"FOREIGN KEY SAME TABLE"</c> for <c>"FOREIGN KEY"</c>
/// in that case (probe-confirmed).
/// </para>
/// </remarks>
internal sealed class ForeignKey(
    string name,
    int objectId,
    HeapTable childTable,
    int[] childColumnOrdinals,
    HeapTable referencedTable,
    int[] referencedColumnOrdinals,
    ReferentialAction deleteAction,
    ReferentialAction updateAction,
    bool isSystemNamed)
{
    public readonly string Name = name;

    /// <summary>
    /// Per-database object identifier — allocated at CREATE TABLE alongside
    /// the table. Surfaces in <c>sys.objects</c> as a <c>F</c> row and in
    /// <c>sys.foreign_keys</c> as the constraint's <c>object_id</c>.
    /// </summary>
    public readonly int ObjectId = objectId;

    /// <summary>The table declaring the FK (the referring side).</summary>
    public readonly HeapTable ChildTable = childTable;

    /// <summary>
    /// Full-ordinal indices into <see cref="ChildTable"/>.<see cref="HeapTable.Columns"/>
    /// that participate in the FK, in declaration order.
    /// </summary>
    public readonly int[] ChildColumnOrdinals = childColumnOrdinals;

    /// <summary>The table being referenced (the parent side).</summary>
    public readonly HeapTable ReferencedTable = referencedTable;

    /// <summary>
    /// Full-ordinal indices into <see cref="ReferencedTable"/>.<see cref="HeapTable.Columns"/>
    /// that the FK targets, paired position-wise with <see cref="ChildColumnOrdinals"/>.
    /// </summary>
    public readonly int[] ReferencedColumnOrdinals = referencedColumnOrdinals;

    public readonly ReferentialAction DeleteAction = deleteAction;

    public readonly ReferentialAction UpdateAction = updateAction;

    /// <summary>
    /// True when the FK's name was auto-generated rather than user-supplied
    /// via <c>CONSTRAINT name</c>. Surfaces in <c>sys.foreign_keys.is_system_named</c>.
    /// </summary>
    public readonly bool IsSystemNamed = isSystemNamed;

    /// <summary>
    /// True iff <see cref="ChildTable"/> is the same instance as
    /// <see cref="ReferencedTable"/>. Drives the <c>"FOREIGN KEY SAME TABLE"</c>
    /// substitution in Msg 547 (probe-confirmed wording difference).
    /// </summary>
    public bool IsSelfReferencing => ReferenceEquals(this.ChildTable, this.ReferencedTable);
}
