using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>STUFF(input, start, length, replacement)</c>: deletes <c>length</c>
/// characters from <c>input</c> starting at 1-based <c>start</c> and inserts
/// <c>replacement</c> at that position. Probe-confirmed
/// semantics (against SQL Server 2025):
/// <list type="bullet">
/// <item><description>Input NULL → NULL. Replacement NULL → treated as empty (deletion only).</description></item>
/// <item><description>Start &lt; 1 or start &gt; <c>length(input)</c> → NULL. Notably <c>start = len + 1</c> (one past the end) is also NULL — SQL Server requires start to point at a real character.</description></item>
/// <item><description>Length &lt; 0 → NULL. Length &gt; remaining-after-start is clamped to the remainder.</description></item>
/// <item><description>Length 0 is a pure insert: <c>STUFF('abcdef', 2, 0, 'XYZ')</c> → <c>'aXYZbcdef'</c>.</description></item>
/// <item><description>Result type is the promotion of input's and replacement's string types — <c>nvarchar</c> wins over <c>varchar</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/stuff-transact-sql</remarks>
internal sealed class Stuff : Expression
{
    private readonly Expression input;
    private readonly Expression start;
    private readonly Expression length;
    private readonly Expression replacement;

    // The start and length real fold while compiling, which size the result.
    private readonly int? constantStart;
    private readonly int? constantLength;

    public Stuff(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.start = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.length = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.replacement = Parse(context.MoveNextRequiredReturnSelf());
        this.constantStart = StringScalars.FoldCount(this.start, context.Batch);
        this.constantLength = StringScalars.FoldCount(this.length, context.Batch);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Non-string input / replacement implicit-coerce to varchar per real
        // (probe-confirmed 2026-05-22: STUFF('abcde', 2, 1, 99) → 'a99cde',
        // STUFF(99, 2, 1, 99) → '999', both varchar).
        var inputValue = StringScalars.CoerceToVarchar(this.input.Run(runtime), runtime.Batch, "stuff", argumentIndex: 1);
        var replacementValue = StringScalars.CoerceToVarchar(this.replacement.Run(runtime), runtime.Batch, "stuff", argumentIndex: 4);
        var resultType = ResolveResultType(inputValue.Type, replacementValue.Type, runtime.Batch);

        if (inputValue.IsNull)
            return SqlValue.Null(resultType);

        var startValue = this.start.Run(runtime);
        var lengthValue = this.length.Run(runtime);
        if (startValue.IsNull || lengthValue.IsNull)
            return SqlValue.Null(resultType);

        var startIndex = StringScalars.CoerceLengthArgument(startValue);
        var len = StringScalars.CoerceLengthArgument(lengthValue);
        var s = inputValue.AsString;
        // STUFF indexes/lengths are code-unit-based under non-SC and
        // codepoint-based under _SC_. Under _SC_ a delete count of 1
        // removes one full codepoint (whole emoji) rather than splitting
        // a surrogate pair. Probe-confirmed against SQL Server 2025.
        var isSc = inputValue.Type.Collation?.IsSupplementaryCharacterAware == true;
        var inputUnits = isSc ? SupplementaryCharacters.CodepointCount(s) : s.Length;

        // Invalid argument cases all map to NULL silently — matches SQL
        // Server's documented and probed behavior.
        if (startIndex < 1 || startIndex > inputUnits || len < 0)
            return SqlValue.Null(resultType);

        var deleteCount = Math.Min(len, inputUnits - (startIndex - 1));
        var insertText = replacementValue.IsNull ? string.Empty : replacementValue.AsString;
        var sliceStartCu = isSc ? SupplementaryCharacters.CodepointToCodeUnit(s, startIndex - 1) : startIndex - 1;
        var sliceEndCu = isSc ? SupplementaryCharacters.CodepointToCodeUnit(s, startIndex - 1 + deleteCount) : startIndex - 1 + deleteCount;
        var result = string.Concat(s.AsSpan(0, sliceStartCu), insertText, s.AsSpan(sliceEndCu));
        return SqlValue.FromString(resultType, StringScalars.ClipToFamilyCap(result, resultType));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = StringScalars.ResolveResultType(StringScalars.BindSource(this.input, batch, resolveColumnType, "stuff", coerced: true), batch);
        _ = this.start.GetSqlType(batch, resolveColumnType);
        _ = this.length.GetSqlType(batch, resolveColumnType);
        var replacementType = StringScalars.ResolveResultType(StringScalars.BindCoercedArgument(this.replacement, batch, resolveColumnType, "stuff", argumentIndex: 4), batch);
        // A bare NULL replacement inserts nothing but is typed as one
        // character (probed 2026-10-01 against SQL Server 2025).
        if (IsUntypedNullLiteral(this.replacement))
            replacementType = VarcharSqlType.Get(1, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);
        ScalarArguments.RequireNumericSlot(this.start, batch, resolveColumnType, "stuff", 2, NumericSlot.IntegerOrDecimal);
        ScalarArguments.RequireNumericSlot(this.length, batch, resolveColumnType, "stuff", 3, NumericSlot.IntegerOrDecimal);
        StringScalars.RequireResolvableCollations("stuff", inputType, replacementType);
        return ResolveResultType(inputType, replacementType, batch);
    }

    /// <summary>
    /// STUFF's projected width follows SQL Server's probed rule: the deletion
    /// removes <c>min(length, inputWidth - start + 1)</c> characters from the
    /// declared input width and the replacement adds its own width, so the
    /// result is <c>min(cap, (inputWidth - clampedDelete) + replacementWidth)</c>
    /// — <c>STUFF(varchar(10), 8, 5, 'XY')</c> → <c>varchar(9)</c> (only 3
    /// characters remain to delete), <c>STUFF(varchar(10), 2, 0, 'ZZZZ')</c> →
    /// <c>varchar(14)</c> (pure insert). The family is the <c>nvarchar</c>-wins
    /// promotion of input + replacement, and either operand being MAX carries
    /// MAX through. When <c>start</c> or <c>length</c> isn't a constant real
    /// folds (see <see cref="StringScalars.FoldCount"/>), a start lies past the
    /// input's width, or a width is unspecified, the result falls back to the
    /// family container, matching real.
    /// </summary>
    private SqlType ResolveResultType(SqlType inputType, SqlType replacementType, BatchContext batch)
    {
        var promoted = SqlType.Promote(inputType, replacementType);
        if (StringScalars.IsMaxForm(inputType) || StringScalars.IsMaxForm(replacementType))
            return promoted;

        var inputWidth = StringScalars.DeclaredWidth(inputType);
        var replacementWidth = StringScalars.DeclaredWidth(replacementType);
        if (inputWidth <= 0 || replacementWidth <= 0
            || this.constantStart is not int start
            || this.constantLength is not int deleteLength
            || start > inputWidth)
        {
            return StringScalars.ContainerResultType(promoted, batch);
        }

        // A start at or below zero still sizes (the call answers NULL), and a
        // negative length widens the result by its magnitude: STUFF('abc', 2,
        // -1, 'x') is varchar(5) (probed 2026-10-01 against SQL Server 2025).
        // A UTF-8 varchar can't tell how many bytes the deleted characters
        // held, so nothing is taken off for them: STUFF(<varchar(5)>, 1, 3,
        // 'qq') is varchar(7) under a _UTF8 collation and varchar(4) otherwise
        // (probed 2026-10-02 against SQL Server 2025).
        var clampedDelete = promoted is VarcharSqlType && promoted.Collation?.AnsiCodePage == 65001 && deleteLength >= 0
            ? 0
            : Math.Min(deleteLength, inputWidth - start + 1);
        var width = inputWidth - clampedDelete + replacementWidth;
        return StringScalars.SizedResultType(promoted, width, batch);
    }

    internal override string DebugDisplay() =>
        $"STUFF({this.input.DebugDisplay()}, {this.start.DebugDisplay()}, {this.length.DebugDisplay()}, {this.replacement.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.input).Child(this.start).Child(this.length).Child(this.replacement);
}
