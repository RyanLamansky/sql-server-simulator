using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CHARINDEX(needle, haystack [, start])</c>: 1-indexed position of
/// the first occurrence of <c>needle</c> in <c>haystack</c> at or after
/// <c>start</c>; returns 0 when not found. Comparison follows the default
/// collation (case-insensitive).
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/charindex-transact-sql</remarks>
internal sealed class CharIndex : Expression
{
    private readonly Expression needle;
    private readonly Expression haystack;
    private readonly Expression? start;

    public CharIndex(ParserContext context)
    {
        this.needle = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.haystack = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.start = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var n = needle.Run(runtime);
        // CHARINDEX's haystack (arg 2) implicit-coerces to varchar per real
        // (probe-confirmed 2026-05-22: CHARINDEX('2', 12345) = 2). Needle
        // (arg 1) stays strict — real rejects non-string with Msg 8116.
        var h = StringScalars.CoerceToVarchar(haystack.Run(runtime), runtime.Batch, "charindex", argumentIndex: 2, allowLegacyLob: true);
        if (n.IsNull || h.IsNull)
            return SqlValue.Null(SqlType.Int32);
        if (!SqlType.IsStringCategory(n.Type) || n.Type == SqlType.Text || n.Type == SqlType.NText)
            throw SimulatedSqlException.InvalidArgumentDataType(n.Type.SqlServerName, argumentIndex: 1, "charindex");

        var needleStr = n.AsString;
        var haystackStr = h.AsString;
        // CHARINDEX indexes in code units under non-SC collations and in
        // codepoints under _SC_. Probe-confirmed against SQL Server 2025:
        // CHARINDEX(N'X', N'😀X') = 3 under non-SC (surrogate pair occupies
        // positions 1-2) and = 2 under _SC_UTF8 (emoji = position 1). The
        // start argument is in the same unit as the result.
        var isSc = h.Type.Collation?.IsSupplementaryCharacterAware == true;
        var startUnits = 0;
        if (start is not null)
        {
            var startValue = start.Run(runtime);
            if (startValue.IsNull)
                return SqlValue.Null(SqlType.Int32);
            startUnits = Math.Max(0, StringScalars.CoerceLengthArgument(startValue) - 1);
        }
        var startCu = isSc
            ? SupplementaryCharacters.CodepointToCodeUnit(haystackStr, startUnits)
            : startUnits;
        if (startCu >= haystackStr.Length)
            return SqlValue.FromInt32(0);

        var foundCu = haystackStr.IndexOf(needleStr, startCu, StringScalars.ComparisonFor(runtime.Batch, h.Type, n.Type));
        return SqlValue.FromInt32(foundCu < 0
            ? 0
            : isSc
                ? SupplementaryCharacters.CodeUnitToCodepoint(haystackStr, foundCu) + 1
                : foundCu + 1);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => start is null
        ? $"CHARINDEX({needle.DebugDisplay()}, {haystack.DebugDisplay()})"
        : $"CHARINDEX({needle.DebugDisplay()}, {haystack.DebugDisplay()}, {start.DebugDisplay()})";
}
