namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Subtract : TwoSidedExpression
{
    public Subtract(Expression left, ParserContext context) : base(left, context) { }
    internal Subtract(Expression left, Expression right) : base(left, right) { }

    public override byte Precedence => 3;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => AdditiveArithmetic(left, right, '-', "subtract", static (a, b) => a - b);

    protected override char Operator => '-';
}
