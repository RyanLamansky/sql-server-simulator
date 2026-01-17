namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Add(Expression left, Expression right) : MathExpression(left, right)
{
    public override byte Precedence => 3;

    protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Add(left, right);

    protected override char Operator => '+';
}
