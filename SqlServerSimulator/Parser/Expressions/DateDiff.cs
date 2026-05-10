using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATEDIFF(&lt;datepart&gt;, &lt;start&gt;, &lt;end&gt;)</c> and the
/// bigint-returning sibling <c>DATEDIFF_BIG</c>: the count of
/// <c>datepart</c>-unit boundaries crossed going from <c>start</c> to
/// <c>end</c>. Probe-confirmed against SQL Server 2025 (2026-05-08): the two
/// functions differ only in result width (int vs bigint) and overflow
/// threshold; same boundary-crossing arithmetic, same accept-everything
/// type-compatibility rule (only <c>tzoffset</c> / <c>iso_week</c> rejected
/// regardless of operand type). The abstract base owns the shared parsing
/// and execution path; the two nested sealed implementations each supply
/// the result-wrapping behavior that distinguishes them.
/// </summary>
internal abstract class DateDiff : Expression
{
    private readonly DatePartKind kind;
    private readonly string keywordText;
    private readonly Expression start;
    private readonly Expression end;
    protected readonly string functionLowerName;
    protected readonly SqlType resultType;

    protected DateDiff(ParserContext context, string functionLowerName, SqlType resultType)
    {
        this.functionLowerName = functionLowerName;
        this.resultType = resultType;
        this.keywordText = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        this.kind = DatePartKinds.ResolveOrThrow(this.keywordText, functionLowerName);
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.start = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.end = Parse(context.MoveNextRequiredReturnSelf());
    }

    /// <summary>
    /// Wraps the boundary count in the function's result type, raising
    /// Msg 535 if the count overflows it. <c>DATEDIFF</c> guards against
    /// int range; <c>DATEDIFF_BIG</c> takes any value Diff can produce
    /// (Diff itself raises Msg 535 via OverflowException for bigint cases).
    /// </summary>
    protected abstract SqlValue WrapResult(long diff);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var startVal = CoerceStringToDateTime2(this.start.Run(runtime));
        var endVal = CoerceStringToDateTime2(this.end.Run(runtime));
        if (startVal.IsNull || endVal.IsNull)
            return SqlValue.Null(this.resultType);
        DatePartKinds.RequireCompatibleForDiff(this.kind, this.keywordText, this.functionLowerName);
        try
        {
            return this.WrapResult(DatePartKinds.Diff(this.kind, startVal, endVal));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.DateDiffOverflow(this.functionLowerName);
        }
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.resultType;

    internal override string DebugDisplay() =>
        $"{this.functionLowerName.ToUpperInvariant()}({this.keywordText}, {this.start.DebugDisplay()}, {this.end.DebugDisplay()})";

    /// <summary>
    /// Mirrors SQL Server's implicit-cast behavior: a bare string literal
    /// (or string-typed expression) passed as a DATEDIFF argument gets
    /// parsed as <c>datetime2(7)</c> before the boundary math runs.
    /// </summary>
    private static SqlValue CoerceStringToDateTime2(SqlValue v) =>
        SqlType.IsStringCategory(v.Type) ? v.CoerceTo(SqlType.GetDateTime2(7)) : v;

    internal sealed class Standard(ParserContext context) : DateDiff(context, "datediff", SqlType.Int32)
    {
        protected override SqlValue WrapResult(long diff) =>
            diff is < int.MinValue or > int.MaxValue
                ? throw SimulatedSqlException.DateDiffOverflow(this.functionLowerName)
                : SqlValue.FromInt32((int)diff);
    }

    internal sealed class Big(ParserContext context) : DateDiff(context, "datediff_big", SqlType.BigInt)
    {
        protected override SqlValue WrapResult(long diff) => SqlValue.FromInt64(diff);
    }
}
