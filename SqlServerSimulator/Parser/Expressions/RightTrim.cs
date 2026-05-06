using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>RTRIM(x)</c>: strips trailing ASCII spaces from the source value.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/rtrim-transact-sql</remarks>
internal sealed class RightTrim(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw new NotSupportedException($"RTRIM expects a string operand; got {value.Type}.");
        var trimmed = value.AsString.TrimEnd(' ');
        return SqlValue.FromString(value.Type, trimmed);
    }

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"RTRIM({source.DebugDisplay()})";
}
