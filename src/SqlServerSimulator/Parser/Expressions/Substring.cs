using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SUBSTRING(x, start, length)</c>: 1-indexed substring extraction.
/// Per SQL Server semantics:
/// <list type="bullet">
/// <item><description><c>start</c> &lt;= 0 still produces output, but the
/// effective length is reduced by the negative offset.</description></item>
/// <item><description><c>start + length</c> past the end clamps to the
/// available remainder.</description></item>
/// <item><description>Negative <c>length</c> is an error.</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/substring-transact-sql</remarks>
internal sealed class Substring : Expression
{
    private readonly Expression source;
    private readonly Expression start;
    private readonly Expression length;

    public Substring(ParserContext context)
    {
        this.source = Parse(context);
        ExpectArgumentSeparator(context);
        this.start = Parse(context.MoveNextRequiredReturnSelf());
        ExpectArgumentSeparator(context);
        this.length = Parse(context.MoveNextRequiredReturnSelf());
    }

    // A comma separates SUBSTRING's arguments; the ANSI `SUBSTRING(x FROM a
    // FOR b)` form isn't T-SQL, so a reserved keyword here (FROM / FOR) is
    // rejected with Msg 156 (probe-confirmed against SQL Server 2025), other
    // tokens with the generic Msg 102.
    private static void ExpectArgumentSeparator(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ',' })
            return;
        throw context.Token is Tokens.ReservedKeyword keyword
            ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
            : SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var s = source.Run(runtime);
        var startValue = start.Run(runtime);
        var lengthValue = length.Run(runtime);
        var resultType = ResolveResultType(s.Type, runtime.Batch);
        if (s.IsNull || startValue.IsNull || lengthValue.IsNull)
            return SqlValue.Null(resultType);
        if (!SqlType.IsStringCategory(s.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(s.Type.SqlServerName, argumentIndex: 1, "substring");

        var startIndex = startValue.CoerceTo(SqlType.Int32).AsInt32;
        var len = lengthValue.CoerceTo(SqlType.Int32).AsInt32;
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowed("substring", 8);

        var input = s.AsString;
        // SQL Server: if start <= 0, the leading |start - 1| characters of the
        // requested window fall before the string and are truncated. Indexing
        // is code-unit-based under non-SC collations and codepoint-based
        // under _SC_; the arithmetic is identical, only the unit of "input
        // length" and the final slice differ. The window math runs in long so
        // int.MinValue / int.MaxValue arguments can't overflow — SQL Server
        // clamps them to an empty result rather than erroring.
        var zeroBased = (long)startIndex - 1;
        var effectiveStart = Math.Max(0L, zeroBased);
        var effectiveLength = Math.Max(0L, len + Math.Min(0L, zeroBased));
        var isSc = s.Type.Collation?.IsSupplementaryCharacterAware == true;
        long inputUnits = isSc ? SupplementaryCharacters.CodepointCount(input) : input.Length;
        effectiveStart = Math.Min(effectiveStart, inputUnits);
        effectiveLength = Math.Min(effectiveLength, inputUnits - effectiveStart);

        var sliceStart = (int)effectiveStart;
        var sliceLength = (int)effectiveLength;
        if (!isSc)
            return SqlValue.FromString(resultType, input.Substring(sliceStart, sliceLength));
        var startCu = SupplementaryCharacters.CodepointToCodeUnit(input, sliceStart);
        var endCu = SupplementaryCharacters.CodepointToCodeUnit(input, sliceStart + sliceLength);
        return SqlValue.FromString(resultType, input[startCu..endCu]);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(source.GetSqlType(batch, resolveColumnType), batch);

    /// <summary>
    /// SUBSTRING preserves the input's string family; a constant length
    /// argument tightens the projected width to <c>min(inputWidth, length)</c>
    /// (probe-confirmed: <c>SUBSTRING(varchar(10), 2, 3)</c> → <c>varchar(3)</c>,
    /// <c>SUBSTRING(varchar(10), 5, 20)</c> → <c>varchar(10)</c> — start does
    /// not affect the width). A MAX / LOB input, a non-constant length, or an
    /// unspecified input width leaves the width at the input's.
    /// </summary>
    private SqlType ResolveResultType(SqlType sourceType, BatchContext batch)
    {
        if (StringScalars.IsMaxForm(sourceType) || !SqlType.IsStringCategory(sourceType))
            return sourceType;
        var inputWidth = StringScalars.DeclaredWidth(sourceType);
        return inputWidth > 0 && StringScalars.TryConstantCount(length, out var n)
            ? StringScalars.SizedResultType(sourceType, Math.Min(inputWidth, n), batch)
            : sourceType;
    }

    internal override string DebugDisplay() => $"SUBSTRING({source.DebugDisplay()}, {start.DebugDisplay()}, {length.DebugDisplay()})";
}
