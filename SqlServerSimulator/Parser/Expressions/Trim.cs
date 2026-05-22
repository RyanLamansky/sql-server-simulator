using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TRIM(x)</c>: strips leading and trailing ASCII spaces. Equivalent
/// to <c>LTRIM(RTRIM(x))</c>.
/// </summary>
/// <remarks>
/// The single-argument form only. SQL Server's <c>TRIM(chars FROM x)</c>
/// (custom trim-character set) is not modeled.
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/trim-transact-sql
/// </remarks>
internal sealed class Trim(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(value.Type.SqlServerName, argumentIndex: 1, "Trim");
        var trimmed = value.AsString.Trim(' ');
        return SqlValue.FromString(value.Type, trimmed);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"TRIM({source.DebugDisplay()})";
}
