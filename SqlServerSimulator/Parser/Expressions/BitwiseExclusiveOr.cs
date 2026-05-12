namespace SqlServerSimulator.Parser.Expressions;

internal sealed class BitwiseExclusiveOr : TwoSidedExpression
{
    public BitwiseExclusiveOr(Expression left, ParserContext context) : base(left, context) { }
    internal BitwiseExclusiveOr(Expression left, Expression right) : base(left, right) { }

    public override byte Precedence => 3;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => IntegerArithmetic(left, right, '^', static (a, b) => a ^ b);

    protected override char Operator => '^';
}
