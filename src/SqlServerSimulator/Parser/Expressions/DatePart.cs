using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATEPART(&lt;datepart&gt;, &lt;date-expr&gt;)</c>: extracts an integer
/// component of the date/time value identified by the bare <c>datepart</c>
/// keyword (year, month, day, hour, minute, second, etc.). Always returns
/// <c>int</c>, mirroring SQL Server.
/// </summary>
/// <remarks>
/// The first argument is a bare unquoted identifier — distinct from a regular
/// expression — so the function constructor reads it as a <see cref="Name"/>
/// and resolves it to a <see cref="DatePartKind"/> at parse time. Aliases
/// (<c>yy</c>/<c>yyyy</c>, <c>mm</c>/<c>m</c>, <c>dd</c>/<c>d</c>, …) all
/// resolve to the canonical kind.
/// </remarks>
internal sealed class DatePart : Expression
{
    private readonly DatePartKind kind;
    private readonly string keywordText;
    private readonly Expression source;

    public DatePart(ParserContext context)
    {
        this.keywordText = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        this.kind = DatePartKinds.ResolveOrThrow(this.keywordText, "datepart");
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.source = Parse(context.MoveNextRequiredReturnSelf());
    }

    /// <summary>
    /// Constructs the single-argument shorthand form (<c>YEAR(x)</c> /
    /// <c>MONTH(x)</c> / <c>DAY(x)</c>) — kind is supplied by the caller,
    /// argument is parsed from the open-paren-advanced position.
    /// </summary>
    public DatePart(ParserContext context, DatePartKind fixedKind, string fixedKeywordText)
    {
        this.kind = fixedKind;
        this.keywordText = fixedKeywordText;
        this.source = Parse(context);
    }

    internal override bool ParallelSafe => this.source.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = DatePartKinds.CoerceDateArgumentImplicit(source.Run(runtime));
        if (value.IsNull)
            return SqlValue.Null(SqlType.Int32);
        DatePartKinds.RequireCompatible(this.kind, this.keywordText, value.Type, "datepart");
        return SqlValue.FromInt32(DatePartKinds.Extract(this.kind, value, runtime.Batch.Connection.DateFirst));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"DATEPART({this.keywordText}, {source.DebugDisplay()})";
}
