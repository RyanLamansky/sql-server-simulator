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
/// <item><description>MAX-typed input (<c>varchar(MAX)</c>, <c>nvarchar(MAX)</c>) carries unbounded length through to the result — probe verified up to 100k chars.</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/replicate-transact-sql</remarks>
internal sealed class Replicate : Expression
{
    private const int MaxNonLobBytes = 8000;
    private readonly Expression input;
    private readonly Expression count;

    /// <summary>
    /// Captured at parse time when the input expression's static type is
    /// resolvable without a FROM-source column resolver (literals, CAST
    /// targets, and arithmetic over those). The simulator's runtime
    /// <see cref="SqlValue"/> doesn't carry varchar / nvarchar length info
    /// — both bounded varchar(N) and varchar(MAX) collapse to the
    /// length-agnostic singleton at the value level — so this is the only
    /// place to capture the MAX-vs-bounded distinction. <see langword="true"/>
    /// means: bypass the 8000-byte truncation cap.
    /// </summary>
    private readonly bool inputIsMaxForm;

    public Replicate(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.count = Parse(context.MoveNextRequiredReturnSelf());

        // Try to nail down whether the source is a MAX-form string at
        // parse time. A literal-only expression / CAST-to-MAX won't need
        // a column resolver and succeeds here. A column reference would
        // throw InvalidColumnName via the empty resolver — for that case
        // we conservatively keep the 8000-byte cap. EF Core's REPLICATE
        // emissions always pass literal counts and literal / variable
        // strings, so this covers the practical surface.
        try
        {
            var staticType = this.input.GetSqlType(context.Batch, context.OuterTypeResolver ?? NoResolver);
            this.inputIsMaxForm = staticType.IsLob
                || (staticType is VarcharSqlType v && v.length == SqlType.MaxLengthSentinel)
                || (staticType is NVarcharSqlType n && n.length == SqlType.MaxLengthSentinel);
        }
        catch (SimulatedSqlException)
        {
            // Column reference (or similar) — defer to the truncating
            // path. A future refactor could thread the projection-time
            // resolver through here; not required for current EF reach.
            this.inputIsMaxForm = false;
        }
    }

    private static SqlType NoResolver(MultiPartName name) =>
        throw SimulatedSqlException.InvalidColumnName(name);

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
        // each for typical ASCII). MAX-form input bypasses the cap; the
        // parse-time-captured flag drives that decision because the runtime
        // SqlValue's type is length-agnostic.
        var capChars = this.inputIsMaxForm ? int.MaxValue : (resultType is NVarcharSqlType ? MaxNonLobBytes / 2 : MaxNonLobBytes);
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
    /// Non-string input projects as <c>varchar</c> in the active database's
    /// collation (matches the runtime coerce + real-server probe). String
    /// input preserves its declared family.
    /// </summary>
    private static SqlType ResolveResultType(SqlType inputType, BatchContext batch) =>
        StringScalars.ResolveResultType(inputType, batch);

    internal override string DebugDisplay() => $"REPLICATE({this.input.DebugDisplay()}, {this.count.DebugDisplay()})";
}
