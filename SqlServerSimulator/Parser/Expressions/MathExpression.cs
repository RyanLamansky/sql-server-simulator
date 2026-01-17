namespace SqlServerSimulator.Parser.Expressions;

internal abstract class MathExpression(Expression left, ParserContext context) : TwoSidedExpression(left, context)
{
    protected abstract DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right);

    protected sealed override DataValue Run(DataValue left, DataValue right) => Run(DataType.CommonNumeric(left, right, this.Operator), left, right);
}
