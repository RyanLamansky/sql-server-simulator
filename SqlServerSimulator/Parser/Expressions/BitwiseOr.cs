namespace SqlServerSimulator.Parser.Expressions;

internal sealed class BitwiseOr(Expression left, ParserContext context) : TwoSidedExpression(left, context)
{
    public override byte Precedence => 3;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => IntegerArithmetic(left, right, '|', static (a, b) => a | b);

    protected override char Operator => '|';
}
