using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FLOOR(numeric)</c>: largest integer-valued result &lt;= input.
/// Result type matches the input (with tinyint / smallint widening to int) —
/// <c>FLOOR(decimal(p,s))</c> becomes <c>decimal(p,0)</c> (scale dropped to
/// 0, the result being integer-valued), <c>FLOOR(money)</c> stays money,
/// <c>FLOOR(float)</c> stays float. NULL → typed NULL.
/// </summary>
internal sealed class Floor(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.source.Run(runtime));
        var resultType = MathScalars.FloorCeilingResult(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, MathScalars.AsLong(v)),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, decimal.Floor(MathScalars.AsDecimalOrMoney(v))),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(Math.Floor(MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"FLOOR doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.FloorCeilingResult(this.source.GetSqlType(batch, resolveColumnType));

    internal override bool ResultReportsNumeric => this.source.ResultReportsNumeric;

    internal override bool ResultIsNullable(NullabilityContext context) => this.source.ResultIsNullable(context);

    internal override string DebugDisplay() => $"FLOOR({this.source.DebugDisplay()})";
}
