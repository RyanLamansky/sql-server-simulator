using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>PATINDEX(pattern, expression)</c>: returns the 1-based start
/// position of the first match of <c>pattern</c> within <c>expression</c>;
/// 0 when no match. Wildcards follow the same
/// rules as <c>LIKE</c> (<c>%</c>, <c>_</c>, <c>[...]</c>), shared via
/// <see cref="LikeMatcher"/>. There is no
/// <c>ESCAPE</c> clause — real SQL Server raises Msg 156 on parse, and the
/// simulator inherits that via <see cref="Expression.Parse"/>'s general
/// argument-list grammar (no ESCAPE token recognized in a non-LIKE atom).
/// </summary>
/// <remarks>
/// Behavioral fingerprint (probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description>Subject written as the bare <c>NULL</c> literal → <strong>Msg 8116</strong> (argument data type NULL is invalid for argument 2 of patindex function); a typed subject holding NULL, and a NULL pattern, both → NULL.</description></item>
/// <item><description>Subject non-string → Msg 8116 with the actual type name.</description></item>
/// <item><description>Pattern non-string → silent CONVERT to the subject's string type (probe: <c>PATINDEX(123, 'abc')</c> returns 0, treated as <c>PATINDEX('123', 'abc')</c>).</description></item>
/// <item><description>Result type: <c>int</c> for non-MAX subjects; <c>bigint</c> for <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c>.</description></item>
/// </list>
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/patindex-transact-sql</remarks>
internal sealed class PatIndex : Expression
{
    private readonly LikeMatcher.Cache patterns = new(forPatIndex: true);
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
        RejectUntypedNullSubject(this.subject);
        if (!SqlType.IsStringCategory(s.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(s.Type.SqlServerName, argumentIndex: 2, "patindex");

        var isBig = IsBigResult(s.Type);
        SqlType resultType = isBig ? SqlType.BigInt : SqlType.Int32;
        // A typed subject holding NULL propagates, like every other string
        // scalar; only the untyped literal is refused, above.
        if (s.IsNull)
            return SqlValue.Null(resultType);
        var p = this.pattern.Run(runtime);
        // The subject takes a text / ntext document (it is searched, not
        // transformed); the pattern refuses every legacy LOB.
        StringScalars.RejectLegacyLob(p, "patindex", argumentIndex: 1);
        if (p.IsNull)
            return SqlValue.Null(resultType);

        var patternString = SqlType.IsStringCategory(p.Type)
            ? p.AsString
            : p.CoerceTo(s.Type).AsString;

        // PATINDEX reads the same collation LIKE does — case, accent, kana and
        // width all decide the match (probe-confirmed: `PATINDEX(N'%cafe%', …)`
        // finds `café` under _CI_AI and not under _CI_AS, and `PATINDEX(N'%A%',
        // N'xax')` is 0 under _CS_AS). The subject supplies it: the pattern is
        // coercible-default at every call site that isn't a column.
        var collation = s.Type.Collation ?? Collation.Baseline;
        // The non-Unicode trailing-space slack applies here too, wherever the
        // match has to reach the subject's end: `PATINDEX('x', 'x  ')` is 1 and
        // `PATINDEX(N'x', N'x  ')` is 0.
        var slack = !SqlType.IsNationalStringCategory(s.Type) && !SqlType.IsNationalStringCategory(p.Type);
        var subjectStr = s.AsString;
        var index = this.patterns.Get(patternString, escapeChar: null, collation).Find(subjectStr, slack);
        // Result position is code-unit-based under non-SC, codepoint-based
        // under _SC_ — matches CHARINDEX dispatch on the subject's collation.
        var position = index < 0 ? 0L
            : collation.IsSupplementaryCharacterAware
                ? (long)SupplementaryCharacters.CodeUnitToCodepoint(subjectStr, index) + 1
                : (long)index + 1;
        return isBig ? SqlValue.FromInt64(position) : SqlValue.FromInt32((int)position);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.BindArgument(this.pattern, batch, resolveColumnType, "patindex");
        RejectUntypedNullSubject(this.subject);
        var subjectType = this.subject.GetSqlType(batch, resolveColumnType);
        // The subject is matched rather than transformed, so it takes no
        // legacy-LOB rejection — but the match still needs a definite
        // collation, so an unresolved one reports from either operand.
        StringScalars.RequireSettledCollation(subjectType, "patindex");
        return IsBigResult(subjectType) ? SqlType.BigInt : SqlType.Int32;
    }

    /// <summary>
    /// Raises <strong>Msg 8116</strong> for a subject written as the bare
    /// <c>NULL</c> literal.
    /// </summary>
    /// <remarks>
    /// The rejection is about the argument's <em>type</em>, not its value: a
    /// literal <c>NULL</c> has none, so the binder has nothing to match the
    /// parameter against. A subject that carries a string type and happens to
    /// hold NULL — a typed <c>CAST(NULL AS varchar(50))</c>, a NULL-valued
    /// variable, a column — answers NULL like any other NULL-propagating
    /// scalar (probe-confirmed against SQL Server 2025). A scalar UDF that
    /// patindexes its own <c>varchar</c> parameter is the shape that meets
    /// this, since the parameter carries a type whatever value arrives. A NULL
    /// <em>pattern</em> answers NULL whatever its type.
    /// </remarks>
    private static void RejectUntypedNullSubject(Expression subject)
    {
        if (IsUntypedNullLiteral(subject))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", argumentIndex: 2, "patindex");
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
