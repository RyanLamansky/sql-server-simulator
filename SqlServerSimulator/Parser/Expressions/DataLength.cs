using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Encapsulates the SQL DATALENGTH command: https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql
/// </summary>
internal sealed class DataLength(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
        return value.IsNull
            ? SqlValue.Null(SqlType.Int32)
            : value.Type.IsFixedLength
                ? SqlValue.FromInt32(value.Type.FixedLength)
                : SqlValue.FromInt32(value.Type.GetVariableByteCount(value));
    }

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"DATALENGTH({source.DebugDisplay()})";
}
