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
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.start = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.length = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var s = source.Run(runtime);
        var startValue = start.Run(runtime);
        var lengthValue = length.Run(runtime);
        if (s.IsNull || startValue.IsNull || lengthValue.IsNull)
            return SqlValue.Null(s.Type);
        if (!SqlType.IsStringCategory(s.Type))
            throw new NotSupportedException($"SUBSTRING expects a string first argument; got {s.Type}.");

        var startIndex = startValue.CoerceTo(SqlType.Int32).AsInt32;
        var len = lengthValue.CoerceTo(SqlType.Int32).AsInt32;
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowed("SUBSTRING");

        var input = s.AsString;
        // SQL Server: if start <= 0, the leading |start - 1| characters of the
        // requested window fall before the string and are truncated. Indexing
        // is code-unit-based under non-SC collations and codepoint-based
        // under _SC_; the arithmetic is identical, only the unit of "input
        // length" and the final slice differ.
        var zeroBased = startIndex - 1;
        var effectiveStart = Math.Max(0, zeroBased);
        var effectiveLength = Math.Max(0, len + Math.Min(0, zeroBased));
        var isSc = s.Type.Collation?.IsSupplementaryCharacterAware == true;
        var inputUnits = isSc ? SupplementaryCharacters.CodepointCount(input) : input.Length;
        effectiveLength = Math.Min(effectiveLength, Math.Max(0, inputUnits - effectiveStart));

        if (!isSc)
            return SqlValue.FromString(s.Type, input.Substring(effectiveStart, effectiveLength));
        var startCu = SupplementaryCharacters.CodepointToCodeUnit(input, effectiveStart);
        var endCu = SupplementaryCharacters.CodepointToCodeUnit(input, effectiveStart + effectiveLength);
        return SqlValue.FromString(s.Type, input[startCu..endCu]);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"SUBSTRING({source.DebugDisplay()}, {start.DebugDisplay()}, {length.DebugDisplay()})";
}
