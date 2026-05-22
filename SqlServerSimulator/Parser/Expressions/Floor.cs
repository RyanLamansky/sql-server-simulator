using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FLOOR(numeric)</c>: largest integer-valued result &lt;= input.
/// Result type matches the input (with tinyint / smallint widening to int) —
/// <c>FLOOR(decimal(p,s))</c> stays <c>decimal(p,s)</c> with the value's
/// fractional digits zeroed, <c>FLOOR(money)</c> stays money,
/// <c>FLOOR(float)</c> stays float. NULL → typed NULL.
/// </summary>
internal sealed class Floor(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.source.Run(runtime);
        var resultType = MathScalars.WidenForResult(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, MathScalars.AsLong(v)),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, decimal.Floor(MathScalars.AsDecimalOrMoney(v))),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(Math.Floor(MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"FLOOR doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.WidenForResult(this.source.GetSqlType(batch, resolveColumnType));

    internal override string DebugDisplay() => $"FLOOR({this.source.DebugDisplay()})";
}
