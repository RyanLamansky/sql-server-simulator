using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server's <c>GROUPING(col)</c> — returns <c>tinyint</c> 1 when
/// <c>col</c> is "grouped away" in the current grouping set (so the
/// projection's NULL for <c>col</c> is a subtotal/total-row marker rather
/// than a data NULL), 0 when <c>col</c> is present in the current set.
/// Probe-confirmed against SQL Server 2025 (2026-05-13).
/// </summary>
/// <remarks>
/// <para>
/// The argument must match an expression in the surrounding query's GROUP
/// BY clause; SQL Server raises Msg 8161 otherwise (and also when GROUPING
/// is used outside a GROUP BY context entirely). <see cref="Reference"/>
/// arguments match a GROUP BY <see cref="Reference"/> by leaf-name equality
/// (qualifier-tolerant, case-insensitive) — the common case. A non-Reference
/// argument (e.g. <c>GROUPING(a+1)</c> paired with <c>GROUP BY a+1</c>)
/// matches by structural equality of the parse tree: redundant parentheses
/// are stripped from both sides, then the rendered parse trees are compared.
/// Probe-confirmed 2026-07-10: the match is order-sensitive and value-exact —
/// <c>GROUPING(1+a)</c> and <c>GROUPING(a+2)</c> against <c>GROUP BY a+1</c>
/// both raise Msg 8161, while <c>GROUPING((a+1))</c> (extra parens) matches.
/// </para>
/// </remarks>
internal sealed class Grouping(ParserContext context) : Expression
{
    private readonly Expression argument = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var currentSet = runtime.Batch.GroupingSetExpressions;
        var allSet = runtime.Batch.AllGroupingExpressions;
        return currentSet is null || allSet is null || !FindArg(allSet, this.argument)
            ? throw SimulatedSqlException.GroupingArgumentNotInGroupBy(1)
            : SqlValue.FromByte(FindArg(currentSet, this.argument) ? (byte)0 : (byte)1);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.TinyInt;

    /// <summary>
    /// Looks for <paramref name="argument"/> in <paramref name="haystack"/> by
    /// <see cref="ShapeKey"/>, keying each column by its name alone: real
    /// matches <c>GROUPING(x.a + 1)</c> against <c>ROLLUP(a + 1)</c> (probed
    /// 2026-09-23), treats redundant parentheses as transparent, and refuses
    /// <c>GROUPING(1 + a)</c> and <c>GROUPING(a + 2)</c> there.
    /// </summary>
    internal static bool FindArg(IReadOnlyList<Expression> haystack, Expression argument)
    {
        var key = KeyOf(argument);
        foreach (var entry in haystack)
        {
            if (key.Equals(KeyOf(entry)))
                return true;
        }
        return false;
    }

    private static ShapeKey KeyOf(Expression expression) => ShapeKey.Of(expression, name => name.Leaf);

    internal override string DebugDisplay() => $"GROUPING({this.argument.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.argument);
}

/// <summary>
/// SQL Server's <c>GROUPING_ID(col1, col2, ..., colN)</c> — returns a 32-bit
/// bitmap where the leftmost argument's "grouped-away" status occupies the
/// most-significant bit (bit <c>N - 1</c>) and the rightmost occupies bit 0.
/// Probe-confirmed against SQL Server 2025 (2026-05-13): each argument
/// contributes 0 (in current set) or 1 (grouped away) to its bit position.
/// </summary>
internal sealed class GroupingId : Expression
{
    private readonly Expression[] arguments;

    public GroupingId(ParserContext context)
    {
        var list = new List<Expression> { Parse(context) };
        while (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            list.Add(Parse(context));
        }
        this.arguments = [.. list];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var currentSet = runtime.Batch.GroupingSetExpressions;
        var allSet = runtime.Batch.AllGroupingExpressions;
        if (currentSet is null || allSet is null)
            throw SimulatedSqlException.GroupingArgumentNotInGroupBy(1, "GROUPING_ID");

        var bitmap = 0;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            if (!Grouping.FindArg(allSet, this.arguments[i]))
                throw SimulatedSqlException.GroupingArgumentNotInGroupBy(i + 1, "GROUPING_ID");
            // Leftmost arg (index 0) is the most-significant bit.
            var bitPosition = this.arguments.Length - 1 - i;
            if (!Grouping.FindArg(currentSet, this.arguments[i]))
                bitmap |= 1 << bitPosition;
        }
        return SqlValue.FromInt32(bitmap);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"GROUPING_ID({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    internal override void Describe(NodeShape shape) => shape.Children(this.arguments);
}
