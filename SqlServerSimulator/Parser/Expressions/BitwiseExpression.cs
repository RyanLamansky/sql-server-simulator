namespace SqlServerSimulator.Parser.Expressions;

internal abstract class BitwiseExpression(Expression left, ParserContext context) : TwoSidedExpression(left, context)
{
    protected abstract DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right);

    protected sealed override DataValue Run(DataValue left, DataValue right) => Run(DataType.CommonInteger(left, right, this.Operator), left, right);
}
