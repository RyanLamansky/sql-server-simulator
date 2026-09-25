using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATEADD(&lt;datepart&gt;, &lt;number&gt;, &lt;date-expr&gt;)</c>:
/// returns the date/time value with the given count of datepart units added.
/// The output type matches the input's (date stays date, time stays time,
/// datetime2 stays datetime2, etc.) — matching SQL Server.
/// </summary>
internal sealed class DateAdd : Expression
{
    private readonly DatePartKind kind;
    private readonly string keywordText;
    private readonly Expression number;
    private readonly Expression source;

    public DateAdd(ParserContext context)
    {
        this.keywordText = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        this.kind = DatePartKinds.ResolveOrThrow(this.keywordText, "dateadd");
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.number = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.source = Parse(context.MoveNextRequiredReturnSelf());
    }

    internal override bool ParallelSafe => this.number.ParallelSafe && this.source.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = source.Run(runtime);
        var value = DatePartKinds.CoerceDateArgumentImplicit(SqlType.IsStringCategory(raw.Type) ? raw.CoerceTo(SqlType.DateTime) : raw);
        var n = number.Run(runtime);
        if (value.IsNull || n.IsNull)
            return SqlValue.Null(value.Type);
        DatePartKinds.RequireCompatible(this.kind, this.keywordText, value.Type, "dateadd");
        var nInt = DatePartKinds.CoerceCount(n, value.Type);
        return DatePartKinds.Add(this.kind, value, nInt);
    }

    /// <summary>
    /// A string date argument reads as <c>datetime</c> here, where the other
    /// date functions read it as <c>datetime2</c> — probe-confirmed
    /// 2026-09-23: <c>DATEADD(day, 1, '2024-01-01')</c> is a <c>datetime</c>,
    /// and a seven-digit fraction is Msg 241.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        AssignmentRules.ArgumentType(this.source, SqlType.DateTime, batch, resolveColumnType) is var sourceType && SqlType.IsStringCategory(sourceType)
            ? SqlType.DateTime
            : DatePartKinds.ResolveImplicitDateType(sourceType);

    internal override string DebugDisplay() => $"DATEADD({this.keywordText}, {number.DebugDisplay()}, {source.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.number).Child(this.source);
}
