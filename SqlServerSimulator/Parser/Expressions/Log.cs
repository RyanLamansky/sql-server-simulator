using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LOG(value)</c> (natural log) and <c>LOG(value, base)</c>. Result
/// is always <see cref="SqlType.Float"/>. Probe-confirmed Msg 3623 raises
/// for non-positive value (0 or negative) regardless of arity, and for
/// <c>LOG(value, 1)</c> (base 1 has no inverse).
/// </summary>
internal sealed class Log : Expression
{
    private readonly Expression value;
    private readonly Expression? logBase;

    public Log(ParserContext context)
    {
        this.value = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            this.logBase = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.value.Run(runtime));
        if (v.IsNull) return SqlValue.Null(SqlType.Float);
        var d = MathScalars.AsDouble(v);
        if (d <= 0) throw SimulatedSqlException.InvalidFloatingPointOperation();

        if (this.logBase is null)
            return SqlValue.FromDouble(Math.Log(d));

        var b = MathScalars.CoerceImplicit(this.logBase.Run(runtime));
        if (b.IsNull) return SqlValue.Null(SqlType.Float);
        var bd = MathScalars.AsDouble(b);
        return bd is <= 0 or 1 ? throw SimulatedSqlException.InvalidFloatingPointOperation() : SqlValue.FromDouble(Math.Log(d, bd));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay()
        => this.logBase is null ? $"LOG({this.value.DebugDisplay()})" : $"LOG({this.value.DebugDisplay()}, {this.logBase.DebugDisplay()})";
}
