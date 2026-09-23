namespace SqlServerSimulator.Storage;

/// <summary>
/// The operations whose type-pair legality SQL Server decides from the two
/// operand types alone, each with its own probed grid in
/// <see cref="SqlType"/>'s pair rules. <see cref="Unify"/> is the common-type
/// question CASE, COALESCE and the set operators all ask; the comparison
/// operators share one grid, and so do <c>*</c> and <c>/</c>.
/// </summary>
internal enum TypePairOperation : byte
{
    Unify,
    Compare,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
}
