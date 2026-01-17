namespace SqlServerSimulator.Parser.Expressions;

internal sealed class BitwiseExclusiveOr(Expression left, ParserContext context) : BitwiseExpression(left, context)
{
    public override byte Precedence => 3;

    protected override DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right) => common.BitwiseExclusiveOr(left, right);

    protected override char Operator => '^';
}
