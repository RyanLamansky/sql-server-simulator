using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal abstract class TwoSidedExpression(Expression left, ParserContext context) : Expression
{
    private Expression left = left, right = Parse(context.MoveNextRequiredReturnSelf());

    public TwoSidedExpression AdjustForPrecedence()
    {
        if (this.right is not TwoSidedExpression rightTwo || rightTwo.Precedence < this.Precedence)
            return this;

        (rightTwo.left, this.right) = (this, rightTwo.left);
        return rightTwo;
    }

    public sealed override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
        => Run(left.Run(getColumnValue), right.Run(getColumnValue));

    protected abstract SqlValue Run(SqlValue left, SqlValue right);

    public sealed override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) =>
        SqlType.Promote(left.GetSqlType(resolveColumnType), right.GetSqlType(resolveColumnType));

    /// <summary>
    /// Evaluates an integer-family binary operation, promoting both operands to
    /// SQL Server's common integer type before computing in <c>long</c> arithmetic
    /// and narrowing the result back to that common type. NULL propagates.
    /// </summary>
    private protected static SqlValue IntegerArithmetic(SqlValue left, SqlValue right, char op, Func<long, long, long> compute)
    {
        if (!SqlType.IsIntegerCategory(left.Type) || !SqlType.IsIntegerCategory(right.Type))
            throw new NotSupportedException($"Operator '{op}' currently supports only integer operands; got {left.Type} and {right.Type}.");

        var common = SqlType.Promote(left.Type, right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(common);

        var result = compute(ToInt64(left), ToInt64(right));
        return common == SqlType.Bit ? SqlValue.FromBoolean(result != 0)
            : common == SqlType.TinyInt ? SqlValue.FromByte((byte)result)
            : common == SqlType.SmallInt ? SqlValue.FromInt16((short)result)
            : common == SqlType.Int32 ? SqlValue.FromInt32((int)result)
            : SqlValue.FromInt64(result);
    }

    private protected static long ToInt64(SqlValue v) =>
        v.Type == SqlType.Bit ? (v.AsBoolean ? 1L : 0L)
        : v.Type == SqlType.TinyInt ? v.AsByte
        : v.Type == SqlType.SmallInt ? v.AsInt16
        : v.Type == SqlType.Int32 ? v.AsInt32
        : v.AsInt64;

    protected abstract char Operator { get; }

#if DEBUG
    public sealed override string ToString() => $"{left} {Operator} {right}";
#endif
}
