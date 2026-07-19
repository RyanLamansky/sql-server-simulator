using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LOG10(value)</c>: base-10 log, always <see cref="SqlType.Float"/>.
/// Same Msg 3623 trigger on non-positive input as <see cref="Log"/>.
/// </summary>
internal sealed class Log10(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.source.Run(runtime));
        if (v.IsNull) return SqlValue.Null(SqlType.Float);
        var d = MathScalars.AsDouble(v);
        return d <= 0 ? throw SimulatedSqlException.InvalidFloatingPointOperation() : SqlValue.FromDouble(Math.Log10(d));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => $"LOG10({this.source.DebugDisplay()})";
}
