namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Multiply(Expression left, ParserContext context) : MathExpression(left, context)
{
    public override byte Precedence => 2;

    protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Multiply(left, right);

    protected override char Operator => '*';
}
