using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Whether a value may be assigned to a typed target — a column an INSERT /
/// UPDATE / MERGE writes, a variable, a parameter, a function's result, a
/// DEFAULT's column, <c>ISNULL</c>'s check expression — without an explicit
/// conversion. Real settles it from the two types while compiling, so a typed
/// NULL and an empty rowset raise it too: Msg 206 for a pair with no
/// conversion at all, Msg 257 for one real performs only explicitly (probed
/// 2026-09-24 against SQL Server 2025; the grid is <c>SqlType</c>'s
/// <see cref="TypePairOperation.Assign"/>).
/// </summary>
internal static class AssignmentRules
{
    /// <summary>
    /// Raises real's error when <paramref name="source"/>, of
    /// <paramref name="sourceType"/>, can't be assigned to
    /// <paramref name="target"/>. A bare <c>NULL</c> has no type to judge and
    /// is always assignable.
    /// </summary>
    public static void RequireAssignable(Expression source, SqlType sourceType, SqlType target)
    {
        if (source is Value { IsUntypedNull: true })
            return;
        if (SqlType.OperandPairError(TypePairOperation.Assign, new TypePairOperand(sourceType, source), new TypePairOperand(target), "assign") is { } error)
            throw error;
    }
}
