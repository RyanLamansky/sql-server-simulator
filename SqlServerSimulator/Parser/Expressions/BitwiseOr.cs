namespace SqlServerSimulator.Parser.Expressions;

internal sealed class BitwiseOr : TwoSidedExpression
{
    public BitwiseOr(Expression left, ParserContext context) : base(left, context) { }
    internal BitwiseOr(Expression left, Expression right) : base(left, right) { }

    public override byte Precedence => 3;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => IntegerArithmetic(left, right, '|', static (a, b) => a | b);

    protected override char Operator => '|';
}
