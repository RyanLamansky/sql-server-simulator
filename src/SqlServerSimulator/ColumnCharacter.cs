namespace SqlServerSimulator;

/// <summary>
/// The values of a result column's character — <see cref="SimulatedQueryResult.ColumnWireFlags"/>,
/// the COLMETADATA flag bits SQL Server 2025 sets from what the column reads,
/// before the TDS writer adds fNullable and fCaseSen from the column's
/// nullability and type.
/// </summary>
/// <remarks>
/// <see cref="Derived"/> and <see cref="ColumnSet"/> are markers that sit in
/// the two bits the wire's flags byte reserves (<c>usReservedODBC</c>), which
/// SQL Server never sets: the writer sends <see cref="Derived"/> as read-only
/// and <see cref="ColumnSet"/> as <c>fSparseColumnSet</c>.
/// </remarks>
internal static class ColumnCharacter
{
    /// <summary>Read-only with no other flag: a rowversion, a period column, a column of a <c>FOR SYSTEM_TIME</c> read.</summary>
    public const byte ReadOnly = 0x00;

    /// <summary>Updatability unknown, a plain column's.</summary>
    public const byte UpdatableUnknown = 0x08;

    /// <summary><c>fIdentity</c>, read-only.</summary>
    public const byte Identity = 0x10;

    /// <summary><c>fComputed</c>, read-only: a computed column, or a scalar expression of the statement's own select list.</summary>
    public const byte Computed = 0x20;

    /// <summary>
    /// A value the query derives rather than reads or computes — an aggregate,
    /// a window function, a sequence draw, a set operation's column, a rowset
    /// function's or <c>VALUES</c> constructor's column, an expression a
    /// derived table or CTE projects — sent read-only with neither flag, and
    /// read as updatability-unknown through a view.
    /// </summary>
    public const byte Derived = 0x40;

    /// <summary>A sparse column set (with <see cref="UpdatableUnknown"/>), sent as <c>fSparseColumnSet</c>, <c>0x0400</c>.</summary>
    public const byte ColumnSet = 0x80;

    /// <summary>Whether a column of character <paramref name="character"/> reads as updatable (updatability unknown, not read-only).</summary>
    public static bool IsUpdatable(byte character) => (character & UpdatableUnknown) != 0;

    /// <summary>Whether a column of character <paramref name="character"/> reads as computed.</summary>
    public static bool IsComputed(byte character) => (character & Computed) != 0;

    /// <summary>
    /// The character a view's or inline function's column takes when an
    /// enclosing query expands its body: the body's own, a derived value's
    /// read as updatability-unknown.
    /// </summary>
    public static byte ThroughView(byte character) => character == Derived ? UpdatableUnknown : character;

    /// <summary>
    /// The character a view's or inline function's column takes when a derived
    /// table, CTE or <c>APPLY</c> body reads it: what its catalog entry
    /// describes — an identity, a read-only rowversion or period column, and
    /// updatability-unknown for anything else, a computed or derived column
    /// included (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    public static byte AsCatalogColumn(byte character) => character switch
    {
        Identity or ReadOnly => character,
        _ => UpdatableUnknown,
    };
}
