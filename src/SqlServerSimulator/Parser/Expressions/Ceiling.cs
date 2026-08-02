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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.source.Run(runtime));
        var resultType = MathScalars.FloorCeilingResult(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, MathScalars.AsLong(v)),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, decimal.Ceiling(MathScalars.AsDecimalOrMoney(v))),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(Math.Ceiling(MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"CEILING doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.FloorCeilingResult(this.source.GetSqlType(batch, resolveColumnType));

    internal override bool ResultReportsNumeric => this.source.ResultReportsNumeric;

    internal override bool ResultIsNullable(NullabilityContext context) => this.source.ResultIsNullable(context);

    internal override string DebugDisplay() => $"CEILING({this.source.DebugDisplay()})";
}
