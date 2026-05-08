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

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
        var n = number.Run(getColumnValue);
        if (value.IsNull || n.IsNull)
            return SqlValue.Null(value.Type);
        DatePartKinds.RequireCompatible(this.kind, this.keywordText, value.Type, "dateadd");
        var nInt = n.CoerceTo(SqlType.Int32).AsInt32;
        return DatePartKinds.Add(this.kind, value, nInt);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) =>
        source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"DATEADD({this.keywordText}, {number.DebugDisplay()}, {source.DebugDisplay()})";
}
