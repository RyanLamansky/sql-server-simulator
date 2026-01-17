namespace SqlServerSimulator.Parser.Expressions;

internal sealed class BitwiseOr(Expression left, ParserContext context) : BitwiseExpression(left, context)
{
    public override byte Precedence => 3;

    protected override DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right) => common.BitwiseOr(left, right);

    protected override char Operator => '|';
}
