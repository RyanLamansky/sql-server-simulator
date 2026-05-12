namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Modulus : TwoSidedExpression
{
    public Modulus(Expression left, ParserContext context) : base(left, context) { }
    internal Modulus(Expression left, Expression right) : base(left, right) { }

    public override byte Precedence => 2;

    protected override Storage.SqlValue Run(Storage.SqlValue left, Storage.SqlValue right) => IntegerArithmetic(left, right, '%', static (a, b) => a % b);

    protected override char Operator => '%';
}
