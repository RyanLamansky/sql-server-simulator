using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Encapsulates the SQL DATALENGTH command: https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql
/// </summary>
/// <remarks>
/// The result type follows real SQL Server's documented split, probe-confirmed
/// against SQL Server 2025: <c>bigint</c> when the operand is
/// <c>varchar(max)</c> / <c>nvarchar(max)</c> / <c>varbinary(max)</c>, else
/// <c>int</c> (bounded strings, <c>xml</c>, fixed-length types). DacFx's
/// bacpac-export bulk reader validates the <c>DATALENGTH([maxCol])</c> column
/// it emits before each MAX column against exactly this typing and rejects the
/// whole table read on mismatch. The parse/runtime split shares the
/// <see cref="returnsBigInt"/> flag captured at <see cref="GetSqlType"/> time
/// (idempotent per plan, so concurrent shared-plan calls are benign).
/// </remarks>
internal sealed class DataLength(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    private bool returnsBigInt;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(this.returnsBigInt ? SqlType.BigInt : SqlType.Int32);
        var byteCount = value.Type.IsFixedLength
            ? value.Type.FixedLength
            : value.Type.GetVariableByteCount(value);
        return this.returnsBigInt ? SqlValue.FromInt64(byteCount) : SqlValue.FromInt32(byteCount);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        this.returnsBigInt = source.GetSqlType(batch, resolveColumnType)
            is VarcharSqlType { length: SqlType.MaxLengthSentinel }
            or NVarcharSqlType { length: SqlType.MaxLengthSentinel }
            or VarbinarySqlType { length: SqlType.MaxLengthSentinel };
        return this.returnsBigInt ? SqlType.BigInt : SqlType.Int32;
    }

    internal override string DebugDisplay() => $"DATALENGTH({source.DebugDisplay()})";
}
