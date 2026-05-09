using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SIGN(numeric)</c>: returns -1, 0, or 1 depending on the input's
/// sign. Result type matches the input (with tinyint / smallint widening
/// to int); decimal returns a same-type decimal where the value is one of
/// -1.0…, 0, or 1.0…; float returns float; bigint returns bigint.
/// </summary>
internal sealed class Sign(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        var v = this.source.Run(getColumnValue);
        var resultType = MathScalars.WidenForResult(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, Math.Sign(MathScalars.AsLong(v))),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, Math.Sign(MathScalars.AsDecimalOrMoney(v))),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(Math.Sign(MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"SIGN doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.WidenForResult(this.source.GetSqlType(resolveColumnType));

    internal override string DebugDisplay() => $"SIGN({this.source.DebugDisplay()})";
}
