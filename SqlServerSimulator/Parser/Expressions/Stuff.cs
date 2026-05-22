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
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Non-string input / replacement implicit-coerce to varchar per real
        // (probe-confirmed 2026-05-22: STUFF('abcde', 2, 1, 99) → 'a99cde',
        // STUFF(99, 2, 1, 99) → '999', both varchar).
        var inputValue = StringScalars.CoerceToVarchar(this.input.Run(runtime), runtime.Batch, "stuff", argumentIndex: 1);
        var replacementValue = StringScalars.CoerceToVarchar(this.replacement.Run(runtime), runtime.Batch, "stuff", argumentIndex: 4);
        var resultType = ResolveResultType(inputValue.Type, replacementValue.Type);

        if (inputValue.IsNull)
            return SqlValue.Null(resultType);

        var startValue = this.start.Run(runtime);
        var lengthValue = this.length.Run(runtime);
        if (startValue.IsNull || lengthValue.IsNull)
            return SqlValue.Null(resultType);

        var startIndex = startValue.CoerceTo(SqlType.Int32).AsInt32;
        var len = lengthValue.CoerceTo(SqlType.Int32).AsInt32;
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
        return SqlValue.FromString(resultType, result);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(
            StringScalars.ResolveResultType(this.input.GetSqlType(batch, resolveColumnType), batch),
            StringScalars.ResolveResultType(this.replacement.GetSqlType(batch, resolveColumnType), batch));

    /// <summary>
    /// Promotes input + replacement string types to the result type.
    /// <c>nvarchar</c> dominates <c>varchar</c>; LOB-ness on either side
    /// propagates. Both inputs are pre-promoted via
    /// <see cref="StringScalars.ResolveResultType"/> on the call sites, so
    /// non-string types land here as the database-collation
    /// <see cref="SqlType.Varchar"/>; <see cref="SqlType.Promote"/> handles
    /// the rest.
    /// </summary>
    private static SqlType ResolveResultType(SqlType inputType, SqlType replacementType) =>
        SqlType.Promote(inputType, replacementType);

    internal override string DebugDisplay() =>
        $"STUFF({this.input.DebugDisplay()}, {this.start.DebugDisplay()}, {this.length.DebugDisplay()}, {this.replacement.DebugDisplay()})";
}
