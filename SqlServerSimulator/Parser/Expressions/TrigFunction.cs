using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator for the unary float-returning trig / power functions.
/// All eight kinds share an identical shape: one float-coerced argument,
/// always-float result, NULL propagation, and Msg 174 / Msg 3623 error
/// paths.
/// </summary>
internal enum TrigKind
{
    Sin,
    Cos,
    Tan,
    Asin,
    Acos,
    Atan,
    Cot,
    Square,
}

/// <summary>
/// Backs the unary float-returning trig / power functions: <c>SIN</c>,
/// <c>COS</c>, <c>TAN</c>, <c>ASIN</c>, <c>ACOS</c>, <c>ATAN</c>, <c>COT</c>,
/// <c>SQUARE</c>. Result is always <see cref="SqlType.Float"/> regardless of
/// the input category — probe-confirmed against SQL Server 2025 (2026-05-10)
/// for all numeric input families (int / bigint / decimal / money / float /
/// real / bit / smallmoney). NULL propagates as typed-NULL float.
/// </summary>
/// <remarks>
/// <para>
/// Domain errors map to Msg 3623 ("An invalid floating point operation
/// occurred."): <c>ASIN</c> / <c>ACOS</c> argument outside <c>[-1, 1]</c>,
/// <c>COT(0)</c>. <c>TAN</c> at <c>pi/2</c> doesn't actually error because
/// <c>Math.PI/2</c> isn't exactly representable, so the value is just very
/// large. <c>SQUARE</c> overflow surfaces as Msg 8115 float (matches
/// <c>EXP</c> overflow behavior).
/// </para>
/// <para>
/// EF Core 10 emits these from <c>Math.Sin</c> / <c>Math.Cos</c> /
/// <c>Math.Tan</c> / <c>Math.Asin</c> / <c>Math.Acos</c> / <c>Math.Atan</c>
/// in server-evaluated LINQ.
/// </para>
/// </remarks>
internal sealed class TrigFunction : Expression
{
    private readonly TrigKind kind;
    private readonly Expression source;

    public TrigFunction(ParserContext context, TrigKind kind)
    {
        this.kind = kind;
        if (context.Token is Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments(LowercaseName(kind), 1);
        this.source = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments(LowercaseName(kind), 1);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.source.Run(runtime);
        if (v.IsNull) return SqlValue.Null(SqlType.Float);
        var d = MathScalars.AsDouble(v);
        return this.kind switch
        {
            TrigKind.Sin => SqlValue.FromDouble(Math.Sin(d)),
            TrigKind.Cos => SqlValue.FromDouble(Math.Cos(d)),
            TrigKind.Tan => SqlValue.FromDouble(Math.Tan(d)),
            TrigKind.Asin => d is < -1 or > 1
                ? throw SimulatedSqlException.InvalidFloatingPointOperation()
                : SqlValue.FromDouble(Math.Asin(d)),
            TrigKind.Acos => d is < -1 or > 1
                ? throw SimulatedSqlException.InvalidFloatingPointOperation()
                : SqlValue.FromDouble(Math.Acos(d)),
            TrigKind.Atan => SqlValue.FromDouble(Math.Atan(d)),
            TrigKind.Cot => d == 0
                ? throw SimulatedSqlException.InvalidFloatingPointOperation()
                : SqlValue.FromDouble(1.0 / Math.Tan(d)),
            TrigKind.Square => Square(d),
            _ => throw new InvalidOperationException($"Unknown {nameof(TrigKind)} {this.kind}."),
        };
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    private static SqlValue Square(double d)
    {
        var result = d * d;
        return double.IsInfinity(result)
            ? throw SimulatedSqlException.ArithmeticOverflow("float")
            : SqlValue.FromDouble(result);
    }

    private static string LowercaseName(TrigKind kind) => kind switch
    {
        TrigKind.Sin => "sin",
        TrigKind.Cos => "cos",
        TrigKind.Tan => "tan",
        TrigKind.Asin => "asin",
        TrigKind.Acos => "acos",
        TrigKind.Atan => "atan",
        TrigKind.Cot => "cot",
        TrigKind.Square => "square",
        _ => throw new InvalidOperationException($"Unknown {nameof(TrigKind)} {kind}."),
    };

    internal override string DebugDisplay() =>
        $"{LowercaseName(this.kind).ToUpperInvariant()}({this.source.DebugDisplay()})";
}
