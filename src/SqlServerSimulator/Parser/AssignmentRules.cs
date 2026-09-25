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

    /// <summary>
    /// The type of <paramref name="argument"/>, after raising real's error
    /// when it can't reach <paramref name="parameter"/>, the type a built-in
    /// declares that argument as: a built-in's argument converts the way an
    /// assignment does (probed 2026-09-25 against SQL Server 2025 — the math
    /// family over a date, a varbinary, xml or a uniqueidentifier is Msg 206
    /// naming <c>float</c> and over a <c>sql_variant</c> Msg 257, or Msg 260
    /// naming a column of the query being compiled; <c>CHAR</c> /
    /// <c>SPACE</c> / <c>LEFT</c>'s count names <c>int</c>, <c>YEAR</c> /
    /// <c>DATEPART</c> / <c>DATEADD</c>'s date <c>datetime</c>).
    /// </summary>
    public static SqlType ArgumentType(Expression argument, SqlType parameter, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = argument.GetSqlType(batch, resolveColumnType);
        if (argument is not Value { IsUntypedNull: true }
            && SqlType.OperandPairError(TypePairOperation.Assign, Expression.PairOperand(argument, type, batch), new TypePairOperand(parameter), "assign") is { } error)
        {
            throw error;
        }
        return type;
    }

    /// <summary>
    /// The same rule for a source known only by its type — a query's column,
    /// a procedure argument's value — which the caller has already cleared of
    /// the untyped-<c>NULL</c> exemption.
    /// </summary>
    public static void RequireAssignable(SqlType sourceType, SqlType target)
    {
        if (SqlType.OperandPairError(TypePairOperation.Assign, new TypePairOperand(sourceType), new TypePairOperand(target), "assign") is { } error)
            throw error;
    }
}
