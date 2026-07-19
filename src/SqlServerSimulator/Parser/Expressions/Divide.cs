namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Divide : TwoSidedExpression
{
    internal Divide(Expression left, Expression right) : base(left, right) { }

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => IntegerArithmetic(left, right, '/', static (a, b) => a / b);

    protected override char Operator => '/';
}
