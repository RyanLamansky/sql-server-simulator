namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Subtract : TwoSidedExpression
{
    internal Subtract(Expression left, Expression right) : base(left, right) { }

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => AdditiveArithmetic(left, right, '-', "subtract", static (a, b) => a - b);

    protected override char Operator => '-';
}
