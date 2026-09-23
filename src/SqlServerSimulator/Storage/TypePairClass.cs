namespace SqlServerSimulator.Storage;

/// <summary>
/// The groups SQL Server's type-pair legality actually distinguishes: every
/// member of a group raises the same error (number and operand order) against
/// every partner in every operator, so the per-operator grids in
/// <see cref="SqlType"/>'s pair rules are indexed by this rather than by type.
/// The grouping is the probed one, not the precedence chart's — <c>bit</c>
/// parts company with the other integers, <c>date</c> with <c>datetime2</c>,
/// and <c>money</c> joins <c>decimal</c>.
/// </summary>
/// <remarks>
/// The declaration order is the grids' row and column order.
/// </remarks>
internal enum TypePairClass : byte
{
    Bit,
    Integer,
    ExactNumeric,
    Approximate,
    AnsiString,
    UnicodeString,
    Binary,
    Text,
    Image,
    Timestamp,
    UniqueIdentifier,
    LegacyDateTime,
    Date,
    Time,
    DateTime2,
    Xml,
    Variant,
    HierarchyId,
    Spatial,
}
