using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Encapsulates the SQL DATALENGTH command: https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql
/// </summary>
internal sealed class DataLength(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        return value.IsNull
            ? SqlValue.Null(SqlType.Int32)
            : value.Type.IsFixedLength
                ? SqlValue.FromInt32(value.Type.FixedLength)
                : SqlValue.FromInt32(value.Type.GetVariableByteCount(value));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"DATALENGTH({source.DebugDisplay()})";
}
