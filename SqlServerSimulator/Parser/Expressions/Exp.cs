using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>EXP(numeric)</c>: returns <c>e^x</c> as <see cref="SqlType.Float"/>
/// regardless of input type. Probe-confirmed against SQL Server 2025
/// (2026-05-09): <c>EXP(1000)</c> raises Msg 8115 (float overflow).
/// </summary>
internal sealed class Exp(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.source.Run(runtime);
        if (v.IsNull) return SqlValue.Null(SqlType.Float);
        var result = Math.Exp(MathScalars.AsDouble(v));
        return double.IsInfinity(result) ? throw SimulatedSqlException.ArithmeticOverflow("float") : SqlValue.FromDouble(result);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => $"EXP({this.source.DebugDisplay()})";
}
