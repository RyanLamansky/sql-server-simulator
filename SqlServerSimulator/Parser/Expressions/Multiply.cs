namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Multiply : TwoSidedExpression
{
    public Multiply(Expression left, ParserContext context) : base(left, context) { }
    internal Multiply(Expression left, Expression right) : base(left, right) { }

    public override byte Precedence => 2;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => IntegerArithmetic(left, right, '*', static (a, b) => a * b);

    protected override char Operator => '*';
}
