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
    private readonly bool targetReportsNumeric;

    public ConvertExpression(ParserContext context, bool tryMode)
    {
        this.tryMode = tryMode;

        // ResolveBuiltIn delivers context.Token already past the opening
        // paren — sitting on the first argument (the type name).
        var typeName = TypeNameSynonyms.TryFoldMultiWordType(context)
            ?? context.Token as Name
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        this.targetReportsNumeric = Cast.ReportsNumeric(typeName);
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

    internal override bool ParallelSafe => this.source.ParallelSafe && this.style?.ParallelSafe != false;

    public override SqlValue Run(RuntimeContext runtime)
    {
        int? styleCode = null;
        if (this.style is { } styleExpr)
        {
            var styleValue = styleExpr.Run(runtime);
            // SQL Server admits only the four integer families in the style
            // slot and reports anything else — including a typed NULL, and
            // including the `numeric(digit_count, 0)` an integer literal past
            // int's range carries — as Msg 8116 before the NULL
            // short-circuit. An in-family value past int narrows through the
            // ordinary overflow surface instead (Msg 8115).
            ScalarArguments.RequireIntegerArgument(styleValue, styleExpr.ResultReportsNumeric, 3, this.tryMode ? "try_convert" : "convert");
            if (styleValue.IsNull)
                return SqlValue.Null(this.targetType);
            styleCode = ScalarArguments.CoerceToInt(styleValue);
        }

        var sourceValue = this.source.Run(runtime);
        var dbCollation = runtime.Batch.CurrentDatabase.Collation;
        if (sourceValue.IsNull)
            return Cast.RecollateStringResult(SqlValue.Null(this.targetType), this.targetType, sourceValue.Type, dbCollation);

        var budgetCollation = Cast.ResultCollation(this.targetType, sourceValue.Type, dbCollation);
        // A styled string rendering goes into a char / nchar through its var
        // form, so it meets the length the way CAST's does rather than being
        // padded and cut first.
        var (renderTarget, renderLength) = styleCode is not null && Cast.VarFormOfFixedString(this.targetType) is var (varTarget, length)
            ? (varTarget, length)
            : (this.targetType, this.targetMaxLength);
        SqlValue coerced;
        try
        {
            Cast.RejectRoundingUnderRoundAbort(sourceValue, this.targetType, runtime.Batch);
            // Style is meaningful only for the six (source-family, target-
            // family) pairs listed below; the default arm and the no-style
            // branch both fall through to the styleless coercion, matching
            // SQL Server's "silently ignore unused style" behavior.
            coerced = styleCode is int sc
                ? (sourceValue.Type, this.targetType) switch
                {
                    ({ Category: SqlTypeCategory.DateTime }, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceDateTimeToStringWithStyle(renderTarget, sc),
                    ({ Category: SqlTypeCategory.String }, { Category: SqlTypeCategory.DateTime })
                        => sourceValue.CoerceStringToDateLikeWithStyle(this.targetType, sc),
                    ({ Category: SqlTypeCategory.Money }, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceMoneyToStringWithStyle(renderTarget, sc),
                    ({ Category: SqlTypeCategory.Approximate }, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceFloatToStringWithStyle(renderTarget, sc),
                    (_, XmlSqlType) => CoerceToXmlWithStyle(sourceValue, sc),
                    (VarbinarySqlType or BinarySqlType or ImageSqlType, { Category: SqlTypeCategory.String })
                        => sourceValue.CoerceBinaryToStringWithStyle(renderTarget, sc),
                    ({ Category: SqlTypeCategory.String }, VarbinarySqlType or BinarySqlType)
                        => sourceValue.CoerceStringToBinaryWithStyle(this.targetType, sc),
                    _ => Cast.ApplyCoercion(sourceValue, this.targetType, this.targetMaxLength, budgetCollation),
                }
                : Cast.ApplyCoercion(sourceValue, this.targetType, this.targetMaxLength, budgetCollation);
            // A styled rendering meets the declared length as CAST's does —
            // truncated from a date, Msg 234 from money, into a char / nchar
            // as into the var form (probed 2026-09-25).
            if (styleCode is int style)
            {
                // Hex text is cut at a whole byte, the 0x prefix included:
                // CONVERT(varchar(3), 0x4142, 1) is 0x and varchar(1) empty.
                if (style is 1 or 2
                    && sourceValue.Type is VarbinarySqlType or BinarySqlType or ImageSqlType
                    && renderLength is int max and > 0
                    && !coerced.IsNull
                    && SqlType.IsStringCategory(renderTarget)
                    && coerced.AsString.Length > max)
                {
                    var prefix = style == 1 ? 2 : 0;
                    coerced = SqlValue.FromString(renderTarget, coerced.AsString[..(max < prefix ? 0 : prefix + ((max - prefix) / 2 * 2))]);
                }
                coerced = Cast.EnforceTargetMaxLength(coerced, renderTarget, renderLength, sourceValue, budgetCollation);
                if (renderTarget != this.targetType)
                    coerced = coerced.CoerceTo(this.targetType);
            }
        }
        catch (SimulatedSqlException ex) when (this.tryMode && Cast.IsConversionFailure(ex.Number))
        {
            coerced = SqlValue.Null(this.targetType);
        }
        catch (SimulatedSqlException ex) when (runtime.Batch.AbsorbsArithmeticFault(ex))
        {
            coerced = Cast.AbsorbedOverflow(this.targetType);
        }

        return Cast.RecollateStringResult(coerced, this.targetType, sourceValue.Type, dbCollation);
    }

    /// <summary>
    /// An <c>xml</c> target reads the style as whitespace and DTD handling
    /// rather than as a text layout, so a binary source isn't rendered as hex:
    /// styles 1 and 3 keep whitespace-only text. Style 2's limited
    /// internal-subset DTD support (and 3's, which is 1 and 2 together) isn't
    /// built: a DTD under it raises <see cref="NotSupportedException"/> rather
    /// than the Msg 6359 the style exists to lift.
    /// </summary>
    private static SqlValue CoerceToXmlWithStyle(SqlValue source, int style)
    {
        try
        {
            return source.CoerceToXml(preserveWhitespace: style is 1 or 3);
        }
        catch (SimulatedSqlException ex) when (style is 2 or 3 && ex.Number == 6359)
        {
            throw new NotSupportedException("CONVERT to xml with style 2 (internal-subset DTD support) isn't built.");
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        Cast.RejectIllegalConversion(this.source, this.source.GetSqlType(batch, resolveColumnType), this.targetType, this.targetReportsNumeric, batch);

    internal override bool ResultReportsNumeric => this.targetReportsNumeric;

    /// <summary>
    /// Forwards to the converted expression, mirroring <see cref="Cast"/>.
    /// A conversion doesn't hide what its operand reads, and without this an
    /// aggregate whose argument is wrapped in CONVERT looked reference-free to
    /// callers that walk references.
    /// </summary>
    internal override string DebugDisplay() =>
        this.style is null
            ? $"{(this.tryMode ? "TRY_CONVERT" : "CONVERT")}({this.targetType}, {this.source.DebugDisplay()})"
            : $"{(this.tryMode ? "TRY_CONVERT" : "CONVERT")}({this.targetType}, {this.source.DebugDisplay()}, {this.style.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.tryMode).Local(this.targetType).Local(this.targetMaxLength).Local(this.targetReportsNumeric).Child(this.source).Child(this.style);

    // Stability is governed by the value operand; the optional style is a
    // constant / variable and never row-varying.
    internal override Expression? PureConversionOperand => this.source;

    private protected override bool IsStructuralConstant => this.source.IsWrittenConstant;
}
