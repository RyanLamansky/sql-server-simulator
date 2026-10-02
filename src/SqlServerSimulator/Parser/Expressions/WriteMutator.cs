using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An <c>UPDATE</c>'s <c>SET column.WRITE(expression, @Offset, @Length)</c>
/// over a <c>varchar(max)</c>, <c>nvarchar(max)</c> or <c>varbinary(max)</c>
/// column: the column's value with <c>@Length</c> units from the zero-based
/// <c>@Offset</c> replaced by <c>expression</c> (probed 2026-10-01 against SQL
/// Server 2025). A NULL <c>@Offset</c> appends, a NULL <c>@Length</c> replaces
/// to the end, a length past the end stops there, and a NULL
/// <c>expression</c> cuts the value at <c>@Offset</c>. A NULL column is Msg
/// 5302, an offset past the end Msg 582, a negative offset or length Msg
/// 583, and a column of any other type Msg 258.
/// </summary>
/// <remarks>
/// Real writes the change in place, logging only the bytes it replaces; the
/// simulator writes the whole new value, which only the row's storage sees.
/// </remarks>
internal sealed class WriteMutator(Expression column, string columnName, Expression value, Expression offset, Expression length) : Expression
{
    /// <summary>
    /// Parses the <c>(expression, @Offset, @Length)</c> of a <c>.WRITE</c>
    /// from its <c>(</c>, leaving the cursor past the <c>)</c>.
    /// </summary>
    public static WriteMutator ParseMethod(ParserContext context, Expression column, string columnName)
    {
        var arguments = new List<Expression>();
        if (context.GetNextRequired() is not Operator { Character: ')' })
        {
            arguments.Add(Parse(context));
            while (context.Token is Operator { Character: ',' })
                arguments.Add(Parse(context.MoveNextRequiredReturnSelf()));
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return arguments.Count switch
        {
            < 3 => throw SimulatedSqlException.InsufficientArgumentsToFunction("write", 101),
            > 3 => throw SimulatedSqlException.TooManyArgumentsToFunction("write", 101),
            _ => new WriteMutator(column, columnName, arguments[0], arguments[1], arguments[2]),
        };
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var current = column.Run(runtime);
        if (current.IsNull)
            throw SimulatedSqlException.WriteMutatorOnNull(columnName);
        var replacement = value.Run(runtime);
        var start = offset.Run(runtime);
        var count = length.Run(runtime);
        var startAt = start.IsNull ? (long?)null : start.CoerceTo(SqlType.BigInt).AsInt64;
        var countOf = count.IsNull ? (long?)null : count.CoerceTo(SqlType.BigInt).AsInt64;
        if (startAt < 0 || countOf < 0)
            throw SimulatedSqlException.WriteNegativeOffsetOrLength();

        if (current.Type is VarbinarySqlType)
        {
            var bytes = current.AsBytes;
            var (keep, skip) = Span(bytes.Length, startAt, countOf, replacement.IsNull);
            var inserted = replacement.IsNull ? [] : replacement.CoerceTo(current.Type).AsBytes;
            return SqlValue.FromVarbinary([.. bytes.AsSpan(0, keep), .. inserted, .. bytes.AsSpan(skip)]);
        }

        var text = current.AsString;
        var (prefix, resume) = Span(text.Length, startAt, countOf, replacement.IsNull);
        var middle = replacement.IsNull ? string.Empty : replacement.CoerceTo(current.Type).AsString;
        return SqlValue.FromString(current.Type, string.Concat(text.AsSpan(0, prefix), middle, text.AsSpan(resume)));
    }

    /// <summary>
    /// The units of a <paramref name="total"/>-long value kept ahead of the
    /// written expression and where the kept tail resumes.
    /// </summary>
    private static (int Keep, int Resume) Span(int total, long? startAt, long? countOf, bool truncates)
    {
        if (startAt is not { } start)
            return (total, total);
        if (start > total)
            throw SimulatedSqlException.WriteOffsetPastEnd();
        var keep = (int)start;
        if (truncates || countOf is not { } count)
            return (keep, total);
        return (keep, (int)Math.Min(total, start + count));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = column.GetSqlType(batch, resolveColumnType);
        if (!StringScalars.IsMaxForm(type) && type is not VarbinarySqlType { length: SqlType.MaxLengthSentinel })
            throw SimulatedSqlException.CannotCallMethodsOn(SimulatedSqlException.FamilyRootName(type));
        _ = value.GetSqlType(batch, resolveColumnType);
        _ = offset.GetSqlType(batch, resolveColumnType);
        _ = length.GetSqlType(batch, resolveColumnType);
        return type;
    }

    internal override string DebugDisplay() => $"{columnName}.WRITE({value.DebugDisplay()}, {offset.DebugDisplay()}, {length.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(columnName).Child(column).Child(value).Child(offset).Child(length);
}
