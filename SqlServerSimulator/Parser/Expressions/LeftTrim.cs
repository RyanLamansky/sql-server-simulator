using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LTRIM(x)</c>: strips leading ASCII spaces (U+0020) from the source
/// value. Other whitespace characters are preserved, matching SQL Server.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/ltrim-transact-sql</remarks>
internal sealed class LeftTrim(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw new NotSupportedException($"LTRIM expects a string operand; got {value.Type}.");
        var trimmed = value.AsString.TrimStart(' ');
        return SqlValue.FromString(value.Type, trimmed);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"LTRIM({source.DebugDisplay()})";
}
