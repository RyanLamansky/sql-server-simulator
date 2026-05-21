using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>PATINDEX(pattern, expression)</c>: returns the 1-based start
/// position of the first match of <c>pattern</c> within <c>expression</c>;
/// 0 when no match. Wildcards follow the same
/// rules as <c>LIKE</c> (<c>%</c>, <c>_</c>, <c>[...]</c>), shared via
/// <see cref="LikePatternBuilder.BuildForPatIndex"/>. There is no
/// <c>ESCAPE</c> clause — real SQL Server raises Msg 156 on parse, and the
/// simulator inherits that via <see cref="Expression.Parse"/>'s general
/// argument-list grammar (no ESCAPE token recognized in a non-LIKE atom).
/// </summary>
/// <remarks>
/// Behavioral fingerprint (probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description>Subject NULL → <strong>Msg 8116</strong> (argument data type NULL is invalid for argument 2 of patindex function); pattern NULL → NULL.</description></item>
/// <item><description>Subject non-string → Msg 8116 with the actual type name.</description></item>
/// <item><description>Pattern non-string → silent CONVERT to the subject's string type (probe: <c>PATINDEX(123, 'abc')</c> returns 0, treated as <c>PATINDEX('123', 'abc')</c>).</description></item>
/// <item><description>Result type: <c>int</c> for non-MAX subjects; <c>bigint</c> for <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c>.</description></item>
/// </list>
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/patindex-transact-sql</remarks>
internal sealed class PatIndex : Expression
{
    private readonly Expression pattern;
    private readonly Expression subject;

    public PatIndex(ParserContext context)
    {
        this.pattern = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.subject = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var s = this.subject.Run(runtime);
        // Subject NULL is an error, not a NULL propagation — probe-verified
        // (real SQL Server raises Msg 8116 on subject NULL but accepts a
        // NULL pattern and returns NULL).
        if (s.IsNull)
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex: 2, "patindex");
        if (!SqlType.IsStringCategory(s.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(s.Type.SqlServerName, argumentIndex: 2, "patindex");

        var isBig = IsBigResult(s.Type);
        SqlType resultType = isBig ? SqlType.BigInt : SqlType.Int32;
        var p = this.pattern.Run(runtime);
        if (p.IsNull)
            return SqlValue.Null(resultType);

        var patternString = SqlType.IsStringCategory(p.Type)
            ? p.AsString
            : p.CoerceTo(s.Type).AsString;

        var regex = LikePatternBuilder.BuildForPatIndex(patternString);
        var subjectStr = s.AsString;
        var match = regex.Match(subjectStr);
        // Result position is code-unit-based under non-SC, codepoint-based
        // under _SC_ — matches CHARINDEX dispatch on the subject's collation.
        var position = !match.Success ? 0L
            : s.Type.Collation?.IsSupplementaryCharacterAware == true
                ? (long)SupplementaryCharacters.CodeUnitToCodepoint(subjectStr, match.Index) + 1
                : (long)match.Index + 1;
        return isBig ? SqlValue.FromInt64(position) : SqlValue.FromInt32((int)position);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        SqlType resultType = IsBigResult(this.subject.GetSqlType(resolveColumnType)) ? SqlType.BigInt : SqlType.Int32;
        return resultType;
    }

    /// <summary>
    /// Probe-confirmed: PATINDEX projects <c>bigint</c> when the subject's
    /// declared type is <c>varchar(MAX)</c> or <c>nvarchar(MAX)</c>, and the
    /// deprecated <c>text</c> / <c>ntext</c> family. Bounded var-types and
    /// fixed-length strings project <c>int</c>. The simulator's
    /// <see cref="SqlType.IsLob"/> flag is reserved for the deprecated
    /// family, so MAX detection compares against the singleton instances.
    /// </summary>
    private static bool IsBigResult(SqlType type) =>
        type.IsLob
        || (type is VarcharSqlType v && v.length == SqlType.MaxLengthSentinel)
        || (type is NVarcharSqlType n && n.length == SqlType.MaxLengthSentinel);

    internal override string DebugDisplay() => $"PATINDEX({this.pattern.DebugDisplay()}, {this.subject.DebugDisplay()})";
}
