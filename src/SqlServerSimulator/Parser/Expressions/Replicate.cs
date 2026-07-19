using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>REPLICATE(string_expression, integer_expression)</c>: returns
/// the first argument concatenated with itself <c>count</c> times.
/// Probe-confirmed semantics:
/// <list type="bullet">
/// <item><description>NULL string or NULL count → NULL.</description></item>
/// <item><description>Count &lt; 0 → NULL (silent — no error).</description></item>
/// <item><description>Result type: input's string type; result is truncated to 8000 bytes for non-MAX <c>varchar</c> / <c>nvarchar</c>.</description></item>
/// <item><description>MAX-typed input (<c>varchar(MAX)</c>, <c>nvarchar(MAX)</c>) carries unbounded length through to the result — probe verified up to 100k chars. MAX-ness is a property of the input's declared type, so a literal / bounded <c>varchar(N)</c> input truncates even when the count would overflow 8000 bytes; only a <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c> column or CAST target bypasses the cap (probe-confirmed 2026-07-10 against a FROM-source column).</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/replicate-transact-sql</remarks>
internal sealed class Replicate : Expression
{
    private const int MaxNonLobBytes = 8000;
    private readonly Expression input;
    private readonly Expression count;

    public Replicate(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.count = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Non-string inputs implicit-coerce to varchar (probe-confirmed
        // 2026-05-22: REPLICATE(12345, 2) → '1234512345', varchar).
        var inputValue = StringScalars.CoerceToVarchar(this.input.Run(runtime), runtime.Batch, "replicate");
        var resultType = ResolveResultType(inputValue.Type, runtime.Batch);
        if (inputValue.IsNull)
            return SqlValue.Null(resultType);

        var countValue = this.count.Run(runtime);
        if (countValue.IsNull)
            return SqlValue.Null(resultType);
        var times = countValue.CoerceTo(SqlType.Int32).AsInt32;
        if (times < 0)
            return SqlValue.Null(resultType);

        var s = inputValue.AsString;
        if (s.Length == 0 || times == 0)
            return SqlValue.FromString(resultType, string.Empty);

        // Cap the build length for non-MAX targets so a count of 1e6
        // doesn't briefly allocate a multi-MB string before truncation.
        // Non-MAX nvarchar caps at 4000 chars (8000 bytes / 2 bytes/char);
        // non-MAX varchar caps at 8000 chars (CP1252-encoded — 1 byte
        // each for typical ASCII). MAX-form input bypasses the cap. MAX-ness
        // is read off the runtime value's type: a MAX-declared column or
        // CAST target decodes to a max-form (length -1) / LOB string type
        // and so preserves its length through the concatenation, matching
        // the FROM-source-column probe. A literal / bounded varchar(N) value
        // carries a non-MAX type and truncates.
        var capChars = IsMaxForm(inputValue.Type) ? int.MaxValue : (resultType is NVarcharSqlType ? MaxNonLobBytes / 2 : MaxNonLobBytes);
        var produceChars = (long)s.Length * times;
        if (produceChars > capChars)
            produceChars = capChars;

        var fullRepeats = (int)(produceChars / s.Length);
        var remainder = (int)(produceChars % s.Length);
        var result = string.Concat(string.Concat(Enumerable.Repeat(s, fullRepeats)), s.AsSpan(0, remainder));
        return SqlValue.FromString(resultType, result);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(this.input.GetSqlType(batch, resolveColumnType), batch);

    /// <summary>
    /// Result width mirrors SQL Server's probed rule: a <c>varchar(MAX)</c> /
    /// <c>nvarchar(MAX)</c> input carries MAX through (unbounded); a bounded
    /// input with a constant count projects as the family-capped product
    /// <c>min(8000/4000, inputWidth × count)</c> (<c>REPLICATE(varchar(5), 3)</c>
    /// → <c>varchar(15)</c>); a non-constant count — or an input whose width is
    /// unspecified — falls back to the family container (<c>varchar(8000)</c> /
    /// <c>nvarchar(4000)</c>), matching real's non-constant behavior. Non-string
    /// input projects as <c>varchar</c> in the active database collation.
    /// </summary>
    private SqlType ResolveResultType(SqlType inputType, BatchContext batch)
    {
        var stringType = StringScalars.ResolveResultType(inputType, batch);
        if (IsMaxForm(stringType))
            return stringType;
        var inputWidth = StringScalars.DeclaredWidth(stringType);
        return inputWidth > 0 && StringScalars.TryConstantCount(this.count, out var times)
            ? StringScalars.SizedResultType(stringType, (int)Math.Min((long)inputWidth * times, StringScalars.FamilyCap(stringType)), batch)
            : StringScalars.ContainerResultType(stringType, batch);
    }

    /// <summary>
    /// A string value is MAX-form when its declared type is a LOB
    /// (<c>text</c> / <c>ntext</c>) or a <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c>
    /// (length sentinel -1). Column values decoded from a MAX-declared column
    /// and CAST-to-MAX results carry this type; literals and bounded
    /// <c>varchar(N)</c> / <c>nvarchar(N)</c> values do not.
    /// </summary>
    private static bool IsMaxForm(SqlType type) =>
        type.IsLob
            || type is VarcharSqlType { length: SqlType.MaxLengthSentinel }
            || type is NVarcharSqlType { length: SqlType.MaxLengthSentinel };

    internal override string DebugDisplay() => $"REPLICATE({this.input.DebugDisplay()}, {this.count.DebugDisplay()})";
}
