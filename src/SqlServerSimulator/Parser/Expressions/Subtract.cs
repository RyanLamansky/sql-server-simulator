namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Subtract : TwoSidedExpression
{
    internal Subtract(Expression left, Expression right) : base(left, right) { }

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => AdditiveArithmetic(left, right, '-', "subtract", static (a, b) => a - b);

    /// <summary>
    /// Computes <c>0 - operand</c> for unary minus (<see cref="Negate"/>): the
    /// subtraction machinery handles string coercion, date rejection, money /
    /// float / integer arithmetic, NULL propagation, and overflow exactly as a
    /// binary subtract would. <see cref="Negate"/> re-boxes the result to the
    /// operand-preserving type where that differs from the additive result.
    /// </summary>
    internal static Storage.SqlValue NegateViaZero(Storage.SqlValue operand) =>
        AdditiveArithmetic(Storage.SqlValue.FromInt32(0), operand, '-', "subtract", static (a, b) => a - b);

    protected override char Operator => '-';
}
