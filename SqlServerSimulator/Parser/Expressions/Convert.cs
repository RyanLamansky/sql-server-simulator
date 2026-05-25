using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CONVERT(type[(spec)], expr [, style])</c> and the
/// <c>TRY_CONVERT</c> variant. The shared CAST machinery does the heavy
/// lifting (<see cref="Cast.ParseTargetTypeSpec"/> resolves the type,
/// <see cref="Cast.ApplyCoercion"/> performs the coercion); this class
/// adds the type-first argument order, the optional style argument, and
/// — for <c>TRY_CONVERT</c> — the conversion-failure → NULL behavior.
/// </summary>
/// <remarks>
/// Style routing fans out to per-family handlers:
/// <list type="bullet">
/// <item>date-like → string and string → date-like — full Microsoft
/// published style table (0/1/2/3/4/5/6/7/8/9/10/11/12/13/14/20/21/22/23/24/25/100..114/120/121/126/127/130/131,
/// see <see cref="SqlValue.CoerceDateTimeToStringWithStyle"/>);</item>
/// <item>money / smallmoney → string — styles 0/1/2;</item>
/// <item>float / real → string — styles 0/1/2/3/126;</item>
/// <item>varbinary / binary / image ↔ string — styles 0/1/2 in both
/// directions.</item>
/// </list>
/// Styles passed on every other source-target pair are silently ignored
/// to mirror SQL Server. Style is evaluated at run time so a <c>NULL</c>
/// style propagates the entire result to <c>NULL</c>.
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/cast-and-convert-transact-sql
/// </remarks>
internal sealed class ConvertExpression : Expression
{
    private readonly Expression source;
    private readonly Expression? style;
    private readonly SqlType targetType;
    private readonly int? targetMaxLength;
    private readonly bool tryMode;

    public ConvertExpression(ParserContext context, bool tryMode)
    {
        this.tryMode = tryMode;

        // ResolveBuiltIn delivers context.Token already past the opening
        // paren — sitting on the first argument (the type name).
        if (context.Token is not Name typeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        (this.targetType, this.targetMaxLength) = Cast.ParseTargetTypeSpec(context, typeName);

        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        this.source = Parse(context);

        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            this.style = Parse(context);
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        int? styleCode = null;
        if (this.style is { } styleExpr)
        {
            var styleValue = styleExpr.Run(runtime);
            if (styleValue.IsNull)
                return SqlValue.Null(this.targetType);
            // SQL Server requires the style argument to be an integer
            // (Msg 8116 names varchar/decimal/etc. when it isn't). Coerce
            // explicitly so a numeric literal that parsed as decimal
            // (3.14-style) routes through the same overflow surface as
            // any other narrowing-to-int.
            if (styleValue.Type.Category is SqlTypeCategory.String or SqlTypeCategory.UniqueIdentifier or SqlTypeCategory.DateTime)
                throw SimulatedSqlException.InvalidArgumentDataType(styleValue.Type.ToString()!, 3, this.tryMode ? "try_convert" : "convert");
            try
            {
                styleCode = styleValue.CoerceTo(SqlType.Int32).AsInt32;
            }
            catch (OverflowException)
            {
                throw SimulatedSqlException.ArithmeticOverflow(SqlType.Int32.ToString()!);
            }
        }

        var sourceValue = this.source.Run(runtime);
        if (sourceValue.IsNull)
            return SqlValue.Null(this.targetType);

        try
        {
            // Style is meaningful only for the six (source-family, target-
            // family) pairs listed below; the default arm and the no-style
            // branch both fall through to the styleless coercion, matching
            // SQL Server's "silently ignore unused style" behavior.
            return styleCode is int sc
                ? (sourceValue.Type, this.targetType) switch
                {
                    ({ Category: SqlTypeCategory.DateTime }, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceDateTimeToStringWithStyle(this.targetType, sc),
                    ({ Category: SqlTypeCategory.String }, { Category: SqlTypeCategory.DateTime })
                        => sourceValue.CoerceStringToDateLikeWithStyle(this.targetType, sc),
                    ({ Category: SqlTypeCategory.Money }, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceMoneyToStringWithStyle(this.targetType, sc),
                    ({ Category: SqlTypeCategory.Approximate }, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceFloatToStringWithStyle(this.targetType, sc),
                    (VarbinarySqlType or BinarySqlType or ImageSqlType, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceBinaryToStringWithStyle(this.targetType, sc),
                    ({ Category: SqlTypeCategory.String }, VarbinarySqlType or BinarySqlType)
                        => sourceValue.CoerceStringToBinaryWithStyle(this.targetType, sc),
                    _ => Cast.ApplyCoercion(sourceValue, this.targetType, this.targetMaxLength),
                }
                : Cast.ApplyCoercion(sourceValue, this.targetType, this.targetMaxLength);
        }
        catch (SimulatedSqlException ex) when (this.tryMode && Cast.IsConversionFailure(ex.Number))
        {
            return SqlValue.Null(this.targetType);
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.targetType;

    internal override string DebugDisplay() =>
        this.style is null
            ? $"{(this.tryMode ? "TRY_CONVERT" : "CONVERT")}({this.targetType}, {this.source.DebugDisplay()})"
            : $"{(this.tryMode ? "TRY_CONVERT" : "CONVERT")}({this.targetType}, {this.source.DebugDisplay()}, {this.style.DebugDisplay()})";

    // Stability is governed by the value operand; the optional style is a
    // constant / variable and never row-varying.
    internal override Expression? PureConversionOperand => this.source;
}
