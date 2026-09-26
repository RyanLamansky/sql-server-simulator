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
        this.kind = DatePartKinds.Read(context, "dateadd", out this.keywordText);
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
        // The number evaluates first, so an error in it outranks one in the
        // date (probed 2026-09-25 against SQL Server 2025).
        var n = number.Run(runtime);
        var raw = source.Run(runtime);
        var value = DatePartKinds.CoerceDateArgumentImplicit(SqlType.IsStringCategory(raw.Type) ? raw.CoerceTo(SqlType.DateTime) : raw);
        if (value.IsNull || n.IsNull)
            return SqlValue.Null(value.Type);
        DatePartKinds.RequireCompatible(this.kind, value.Type, "dateadd");
        var nInt = DatePartKinds.CoerceCount(n, value.Type);
        return DatePartKinds.Add(this.kind, value, nInt);
    }

    /// <summary>
    /// A string date argument reads as <c>datetime</c> here, where the other
    /// date functions read it as <c>datetime2</c> — probe-confirmed
    /// 2026-09-23: <c>DATEADD(day, 1, '2024-01-01')</c> is a <c>datetime</c>,
    /// and a seven-digit fraction is Msg 241.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // Every argument binds before the number's slot rule applies, so an
        // error inside the date argument is the one reported (probed
        // 2026-09-25 against SQL Server 2025).
        _ = this.number.GetSqlType(batch, resolveColumnType);
        var sourceType = AssignmentRules.ArgumentType(this.source, SqlType.DateTime, batch, resolveColumnType);
        // A bare NULL has no type to add (probed 2026-09-25 against SQL Server
        // 2025: DATEADD(day, NULL, x) is Msg 8116 naming NULL).
        if (IsUntypedNullLiteral(this.number))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 2, "dateadd");
        ScalarArguments.RequireNumericSlot(this.number, batch, resolveColumnType, "dateadd", 2, NumericSlot.AnyNumber);
        return SqlType.IsStringCategory(sourceType) ? SqlType.DateTime : DatePartKinds.ResolveImplicitDateType(sourceType);
    }

    internal override string DebugDisplay() => $"DATEADD({this.keywordText}, {number.DebugDisplay()}, {source.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.number).Child(this.source);
}
