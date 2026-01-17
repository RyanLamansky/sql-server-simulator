namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Modulus(Expression left, ParserContext context) : MathExpression(left, context)
{
    public override byte Precedence => 3;

    protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Modulus(left, right);

    protected override char Operator => '+';
}
