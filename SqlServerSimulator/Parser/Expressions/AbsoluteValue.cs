using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Encapsulates the SQL ABS command: https://learn.microsoft.com/en-us/sql/t-sql/functions/abs-transact-sql
/// </summary>
internal sealed class AbsoluteValue(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
        if (!SqlType.IsIntegerCategory(value.Type))
            throw new NotSupportedException($"ABS currently supports only integer operands; got {value.Type}.");
        if (value.IsNull)
            return SqlValue.Null(value.Type);

        var asLong = value.Type == SqlType.Bit ? (value.AsBoolean ? 1L : 0L)
            : value.Type == SqlType.TinyInt ? value.AsByte
            : value.Type == SqlType.SmallInt ? value.AsInt16
            : value.Type == SqlType.Int32 ? value.AsInt32
            : value.AsInt64;
        var abs = Math.Abs(asLong);
        return value.Type == SqlType.Bit ? SqlValue.FromBoolean(abs != 0)
            : value.Type == SqlType.TinyInt ? SqlValue.FromByte((byte)abs)
            : value.Type == SqlType.SmallInt ? SqlValue.FromInt16((short)abs)
            : value.Type == SqlType.Int32 ? SqlValue.FromInt32((int)abs)
            : SqlValue.FromInt64(abs);
    }

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"ABS({source.DebugDisplay()})";
}
