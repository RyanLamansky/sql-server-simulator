using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>GET_FILESTREAM_TRANSACTION_CONTEXT()</c>: the <c>varbinary(max)</c>
/// FILESTREAM transaction context token. The simulated server reports a
/// FILESTREAM access level of 0, as a Linux server always does, so every
/// evaluation raises Msg 5592 as real's does there. Any argument raises
/// Msg 174, probe-confirmed against SQL Server 2025. Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/get-filestream-transaction-context-transact-sql
/// </summary>
internal sealed class GetFilestreamTransactionContext : Expression
{
    public GetFilestreamTransactionContext(ParserContext context)
    {
        BuiltInArity.RequireNoArguments(context, "get_filestream_transaction_context");
    }

    public override SqlValue Run(RuntimeContext runtime) => throw SimulatedSqlException.FilestreamAccessNotEnabled();

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.VarbinaryMax;

    internal override string DebugDisplay() => "GET_FILESTREAM_TRANSACTION_CONTEXT()";

    internal override void Describe(NodeShape shape) { }
}
