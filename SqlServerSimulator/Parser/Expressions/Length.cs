using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LEN(x)</c>: number of characters in the source value excluding
/// trailing spaces. Distinct from <c>DATALENGTH</c>, which counts raw bytes.
/// </summary>
/// <remarks>
/// SQL Server's quirk: <c>LEN</c> ignores trailing spaces but not leading
/// spaces. The simulator measures characters in <see cref="string.Length"/>
/// terms (UCS-2 code units for nvarchar; bytes-equal-chars for varchar in
/// CP1252), matching SQL Server.
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/len-transact-sql
/// </remarks>
internal sealed class Length(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
        // NULL passes through any string function regardless of its underlying
        // type tag; the simulator's untyped NULL literal carries Type=Int32 so
        // the IsNull check has to come before the IsStringCategory check.
        if (value.IsNull)
            return SqlValue.Null(SqlType.Int32);
        if (!SqlType.IsStringCategory(value.Type))
            throw new NotSupportedException($"LEN expects a string operand; got {value.Type}.");
        var trimmed = value.AsString.TrimEnd(' ');
        return SqlValue.FromInt32(trimmed.Length);
    }

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => SqlType.Int32;

#if DEBUG
    public override string ToString() => $"LEN({source})";
#endif
}
