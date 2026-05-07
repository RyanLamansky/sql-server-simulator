using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CHARINDEX(needle, haystack [, start])</c>: 1-indexed position of
/// the first occurrence of <c>needle</c> in <c>haystack</c> at or after
/// <c>start</c>; returns 0 when not found. Comparison follows the default
/// collation (case-insensitive).
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/charindex-transact-sql</remarks>
internal sealed class CharIndex : Expression
{
    private readonly Expression needle;
    private readonly Expression haystack;
    private readonly Expression? start;

    public CharIndex(ParserContext context)
    {
        this.needle = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.haystack = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.start = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        var n = needle.Run(getColumnValue);
        var h = haystack.Run(getColumnValue);
        if (n.IsNull || h.IsNull)
            return SqlValue.Null(SqlType.Int32);
        if (!SqlType.IsStringCategory(n.Type) || !SqlType.IsStringCategory(h.Type))
            throw new NotSupportedException("CHARINDEX expects string operands.");

        var startIndex = 0;
        if (start is not null)
        {
            var startValue = start.Run(getColumnValue);
            if (startValue.IsNull)
                return SqlValue.Null(SqlType.Int32);
            startIndex = Math.Max(0, startValue.CoerceTo(SqlType.Int32).AsInt32 - 1);
        }

        var needleStr = n.AsString;
        var haystackStr = h.AsString;
        if (startIndex >= haystackStr.Length)
            return SqlValue.FromInt32(0);

        var found = haystackStr.IndexOf(needleStr, startIndex, StringComparison.InvariantCultureIgnoreCase);
        return SqlValue.FromInt32(found < 0 ? 0 : found + 1);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => start is null
        ? $"CHARINDEX({needle.DebugDisplay()}, {haystack.DebugDisplay()})"
        : $"CHARINDEX({needle.DebugDisplay()}, {haystack.DebugDisplay()}, {start.DebugDisplay()})";
}
