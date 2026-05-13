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
/// is used outside a GROUP BY context entirely). Matching here is by leaf-
/// name equality for <see cref="Reference"/> arguments — the common case.
/// More exotic argument shapes (constant expressions, function calls
/// matching a GROUP BY expression by structure) raise
/// <see cref="NotSupportedException"/>.
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

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.TinyInt;

    /// <summary>
    /// Looks for <paramref name="argument"/> in <paramref name="haystack"/>.
    /// Reference arguments match by leaf-name equality (case-insensitive via
    /// <see cref="Collation"/>); non-Reference arguments aren't modeled here
    /// — real SQL Server resolves them by exact-match parser comparison, but
    /// the simulator's GROUPING surface is column-reference-only.
    /// </summary>
    internal static bool FindArg(IReadOnlyList<Expression> haystack, Expression argument)
    {
        if (argument is not Reference reference)
            throw new NotSupportedException("GROUPING / GROUPING_ID arguments other than direct column references aren't modeled.");
        var leaf = reference.ReferencedName.Leaf;
        foreach (var entry in haystack)
        {
            if (entry is Reference r && Collation.Default.Equals(r.ReferencedName.Leaf, leaf))
                return true;
        }
        return false;
    }

    internal override string DebugDisplay() => $"GROUPING({this.argument.DebugDisplay()})";
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
            throw SimulatedSqlException.GroupingArgumentNotInGroupBy(1);

        var bitmap = 0;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            if (!Grouping.FindArg(allSet, this.arguments[i]))
                throw SimulatedSqlException.GroupingArgumentNotInGroupBy(i + 1);
            // Leftmost arg (index 0) is the most-significant bit.
            var bitPosition = this.arguments.Length - 1 - i;
            if (!Grouping.FindArg(currentSet, this.arguments[i]))
                bitmap |= 1 << bitPosition;
        }
        return SqlValue.FromInt32(bitmap);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"GROUPING_ID({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
