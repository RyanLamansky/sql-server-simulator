using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SQRT(numeric)</c>: returns the square root of the input,
/// always typed <see cref="SqlType.Float"/> regardless of the input
/// (probe-confirmed against SQL Server 2025, 2026-05-09:
/// <c>SQRT(int) → float</c>, <c>SQRT(decimal) → float</c>). Negative input
/// raises Msg 3623.
/// </summary>
internal sealed class Sqrt(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.source.Run(runtime);
        if (v.IsNull) return SqlValue.Null(SqlType.Float);
        var d = MathScalars.AsDouble(v);
        return d < 0 ? throw SimulatedSqlException.InvalidFloatingPointOperation() : SqlValue.FromDouble(Math.Sqrt(d));
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => $"SQRT({this.source.DebugDisplay()})";
}
