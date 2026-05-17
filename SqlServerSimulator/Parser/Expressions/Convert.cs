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
/// Style-code support is intentionally narrow: only <c>0</c>, <c>120</c>,
/// and <c>121</c> are implemented for date-like sources targeting a
/// character string (the EF Core code-generation defaults). Other style
/// numbers raise Msg 281; styles passed on non-date sources are silently
/// ignored to mirror SQL Server. Style is evaluated at run time so a
/// <c>NULL</c> style propagates the entire result to <c>NULL</c>.
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
            // Style is meaningful for: date-like → string (formatted output),
            // string → date-like (style-aware input parser), and money →
            // string (currency formatting). Everywhere else SQL Server
            // silently ignores it.
            if (styleCode is int sc)
            {
                if (sourceValue.Type.Category == SqlTypeCategory.DateTime
                    && this.targetType.Category == SqlTypeCategory.String)
                {
                    return sourceValue.CoerceDateTimeToStringWithStyle(this.targetType, sc);
                }
                if (sourceValue.Type.Category == SqlTypeCategory.String
                    && this.targetType.Category == SqlTypeCategory.DateTime)
                {
                    return sourceValue.CoerceStringToDateLikeWithStyle(this.targetType, sc);
                }
                if (sourceValue.Type.Category == SqlTypeCategory.Money
                    && this.targetType.Category == SqlTypeCategory.String)
                {
                    return sourceValue.CoerceMoneyToStringWithStyle(this.targetType, sc);
                }
            }
            return Cast.ApplyCoercion(sourceValue, this.targetType, this.targetMaxLength);
        }
        catch (SimulatedSqlException ex) when (this.tryMode && Cast.IsConversionFailure(ex.Number))
        {
            return SqlValue.Null(this.targetType);
        }
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.targetType;

    internal override string DebugDisplay() =>
        this.style is null
            ? $"{(this.tryMode ? "TRY_CONVERT" : "CONVERT")}({this.targetType}, {this.source.DebugDisplay()})"
            : $"{(this.tryMode ? "TRY_CONVERT" : "CONVERT")}({this.targetType}, {this.source.DebugDisplay()}, {this.style.DebugDisplay()})";
}
