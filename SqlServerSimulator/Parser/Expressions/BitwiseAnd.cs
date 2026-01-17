namespace SqlServerSimulator.Parser.Expressions;

internal sealed class BitwiseAnd(Expression left, ParserContext context) : BitwiseExpression(left, context)
{
    public override byte Precedence => 3;

    protected override DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right) => common.BitwiseAnd(left, right);

    protected override char Operator => '&';
}
