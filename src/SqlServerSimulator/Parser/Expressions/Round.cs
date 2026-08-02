using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ROUND(value, length [, function])</c>: rounds (default) or
/// truncates (any non-zero <c>function</c> argument) <c>value</c> at
/// decimal position <c>length</c>. Negative <c>length</c> rounds left of
/// the decimal point (<c>ROUND(127, -1)</c> → 130). Result type matches
/// the input — a <c>decimal(p,s)</c> input produces a <c>decimal(p,s)</c>
/// result with the same scale (the value's "rounded" portion is padded
/// with zeros). Tinyint and smallint widen to int; string-typed value
/// implicit-casts to <c>float</c> via <see cref="MathScalars.CoerceImplicit"/>.
/// Probe-confirmed against SQL Server 2025: rounding is half-away-from-zero
/// for both decimal and float inputs (NOT banker's rounding); length /
/// function args stay strict-int — Msg 8116 on string for either (the
/// <c>InvalidArgumentDataType</c> paths below). NULL on any argument
/// propagates to NULL.
/// </summary>
internal sealed class Round : Expression
{
    private readonly Expression value;
    private readonly Expression length;
    private readonly Expression? function;

    public Round(ParserContext context)
    {
        this.value = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.length = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.function = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.value.Run(runtime));
        var resultType = MathScalars.WidenForResult(v.Type);
        if (v.IsNull) return SqlValue.Null(resultType);

        var lenValue = this.length.Run(runtime);
        if (lenValue.IsNull) return SqlValue.Null(resultType);
        if (lenValue.Type.Category != SqlTypeCategory.Integer)
            throw SimulatedSqlException.InvalidArgumentDataType(SqlTypeFamilyName(lenValue.Type), 2, "round");
        var len = (int)Math.Clamp(MathScalars.AsLong(lenValue), -28, 28);

        var truncate = false;
        if (this.function is not null)
        {
            var fv = this.function.Run(runtime);
            if (fv.IsNull) return SqlValue.Null(resultType);
            if (fv.Type.Category != SqlTypeCategory.Integer)
                throw SimulatedSqlException.InvalidArgumentDataType(SqlTypeFamilyName(fv.Type), 3, "round");
            truncate = MathScalars.AsLong(fv) != 0;
        }

        return resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, RoundLong(MathScalars.AsLong(v), len, truncate)),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, RoundDecimal(MathScalars.AsDecimalOrMoney(v), len, truncate)),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(RoundDouble(MathScalars.AsDouble(v), len, truncate)),
            _ => throw new NotSupportedException($"ROUND doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.WidenForResult(this.value.GetSqlType(batch, resolveColumnType));

    internal override bool ResultReportsNumeric => this.value.ResultReportsNumeric;

    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.value.ResultIsNullable(context)
        || this.length.ResultIsNullable(context)
        || (this.function is not null && this.function.ResultIsNullable(context));

    internal override string DebugDisplay() => $"ROUND({this.value.DebugDisplay()}, {this.length.DebugDisplay()})";

    /// <remarks>
    /// Integer ROUND only matters for negative <paramref name="length"/>
    /// (e.g. <c>ROUND(127, -1)</c> → 130). Non-negative length on integer
    /// input is a no-op.
    /// </remarks>
    private static long RoundLong(long value, int length, bool truncate)
    {
        if (length >= 0) return value;
        var scale = Pow10Long(-length);
        if (scale == 0) return 0;
        if (truncate) return value / scale * scale;
        var half = scale / 2;
        var absRounded = (Math.Abs(value) + half) / scale * scale;
        return value < 0 ? -absRounded : absRounded;
    }

    private static decimal RoundDecimal(decimal value, int length, bool truncate)
    {
        if (length >= 0)
        {
            return truncate
                ? Math.Truncate(value * Pow10Decimal(length)) / Pow10Decimal(length)
                : Math.Round(value, length, MidpointRounding.AwayFromZero);
        }
        var scale = Pow10Decimal(-length);
        var scaled = value / scale;
        var rounded = truncate ? Math.Truncate(scaled) : Math.Round(scaled, 0, MidpointRounding.AwayFromZero);
        return rounded * scale;
    }

    private static double RoundDouble(double value, int length, bool truncate)
    {
        if (length >= 0)
        {
            var p = Math.Pow(10, length);
            return truncate ? Math.Truncate(value * p) / p : Math.Round(value * p, MidpointRounding.AwayFromZero) / p;
        }
        var scale = Math.Pow(10, -length);
        var scaled = value / scale;
        var rounded = truncate ? Math.Truncate(scaled) : Math.Round(scaled, MidpointRounding.AwayFromZero);
        return rounded * scale;
    }

    private static long Pow10Long(int exponent)
    {
        long result = 1;
        for (var i = 0; i < exponent && result <= long.MaxValue / 10; i++)
            result *= 10;
        return result;
    }

    private static decimal Pow10Decimal(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
            result *= 10m;
        return result;
    }

    private static string SqlTypeFamilyName(SqlType t) => t.Category switch
    {
        SqlTypeCategory.String => "varchar",
        SqlTypeCategory.DateTime => "datetime",
        SqlTypeCategory.UniqueIdentifier => "uniqueidentifier",
        _ => t.SqlServerName,
    };
}
