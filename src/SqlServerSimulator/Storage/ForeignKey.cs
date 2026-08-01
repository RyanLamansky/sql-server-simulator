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
    bool isSystemNamed,
    DateTime createDate)
{
    public readonly string Name = name;

    /// <summary>
    /// UTC creation timestamp — the declaring statement's frozen
    /// <c>UtcNow</c>, so a constraint declared inside <c>CREATE TABLE</c>
    /// shares the table's instant while an <c>ALTER TABLE … ADD CONSTRAINT</c>
    /// carries the later one (probe-confirmed). Surfaces in
    /// <c>sys.objects.create_date</c> and the per-family constraint catalog
    /// view's <c>create_date</c>.
    /// </summary>
    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// UTC modification timestamp — equal to <see cref="CreateDate"/> until an
    /// <c>ALTER TABLE … {NOCHECK|CHECK} CONSTRAINT</c> trust toggle or an
    /// <c>sp_rename</c> of the constraint advances it (both probe-confirmed).
    /// Surfaces in <c>sys.objects.modify_date</c> and the per-family
    /// constraint catalog view's <c>modify_date</c>.
    /// </summary>
    public DateTime ModifyDate = createDate;

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
    /// True iff the FK was added via <c>ALTER TABLE … WITH NOCHECK ADD
    /// CONSTRAINT</c> or disabled via <c>ALTER TABLE … NOCHECK CONSTRAINT</c>,
    /// either of which bypasses the existing-row validation pass. Cleared by
    /// <c>ALTER TABLE … WITH CHECK CHECK CONSTRAINT name</c> on successful
    /// re-validation. Surfaces in <c>sys.foreign_keys.is_not_trusted</c>.
    /// False for FKs declared at <c>CREATE TABLE</c> (real SQL Server treats
    /// CREATE-time FKs as trusted unconditionally).
    /// </summary>
    public bool IsNotTrusted;

    /// <summary>
    /// True iff the FK was disabled via <c>ALTER TABLE … NOCHECK CONSTRAINT
    /// name</c>. While disabled, INSERT / UPDATE on the child skips FK
    /// validation, and DELETE / UPDATE on the parent skips both the
    /// NO-ACTION reject and any CASCADE / SET NULL / SET DEFAULT action
    /// (probe-confirmed: disabled CASCADE leaves children orphaned when
    /// parent deletes). Cleared by <c>ALTER TABLE … CHECK CONSTRAINT name</c>.
    /// Surfaces in <c>sys.foreign_keys.is_disabled</c>. Independent of
    /// <see cref="IsNotTrusted"/>: re-enabling with bare <c>CHECK
    /// CONSTRAINT</c> (no <c>WITH CHECK</c> prefix) clears
    /// <see cref="IsDisabled"/> but leaves <see cref="IsNotTrusted"/>
    /// untouched.
    /// </summary>
    public bool IsDisabled;

    /// <summary>
    /// True iff <see cref="ChildTable"/> is the same instance as
    /// <see cref="ReferencedTable"/>. Drives the <c>"FOREIGN KEY SAME TABLE"</c>
    /// substitution in Msg 547 (probe-confirmed wording difference).
    /// </summary>
    public bool IsSelfReferencing => ReferenceEquals(this.ChildTable, this.ReferencedTable);
}
