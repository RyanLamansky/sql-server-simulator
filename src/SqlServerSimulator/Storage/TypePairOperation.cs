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

    /// <summary>The bitwise <c>&amp;</c> / <c>|</c> / <c>^</c> operators, which share one grid.</summary>
    Bitwise,

    /// <summary>
    /// A value assigned to a typed target — a column, a variable, a parameter,
    /// a function's result, <c>ISNULL</c>'s replacement — the left operand the
    /// source and the right the target.
    /// </summary>
    Assign,
}
