using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CEILING(numeric)</c>: smallest integer-valued result &gt;= input.
/// Symmetric to <see cref="Floor"/> in everything except the rounding
/// direction. Result type rules and NULL handling match.
/// </summary>
internal sealed class Ceiling(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        var v = this.source.Run(getColumnValue);
        var resultType = MathScalars.WidenForResult(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, MathScalars.AsLong(v)),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, decimal.Ceiling(MathScalars.AsDecimalOrMoney(v))),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(Math.Ceiling(MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"CEILING doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.WidenForResult(this.source.GetSqlType(resolveColumnType));

    internal override string DebugDisplay() => $"CEILING({this.source.DebugDisplay()})";
}
