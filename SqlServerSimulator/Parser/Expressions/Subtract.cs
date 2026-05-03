namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Subtract(Expression left, ParserContext context) : TwoSidedExpression(left, context)
{
    public override byte Precedence => 3;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => AdditiveArithmetic(left, right, '-', "subtract", static (a, b) => a - b);

    protected override char Operator => '-';
}
