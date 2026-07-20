using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>GET_FILESTREAM_TRANSACTION_CONTEXT()</c>: returns the
/// <c>varbinary(max)</c> FILESTREAM transaction context token, or NULL when the
/// session has no active FILESTREAM transaction. The simulator models no
/// FILESTREAM storage, so it always returns NULL — the faithful "no FILESTREAM
/// context" answer a FILESTREAM-enabled server gives outside such a transaction.
/// (A server whose instance has FILESTREAM file-system access disabled instead
/// raises Msg 5592; the simulator returns the enabled-but-idle answer.) Any
/// argument raises Msg 174, probe-confirmed against SQL Server 2025. Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/get-filestream-transaction-context-transact-sql
/// </summary>
internal sealed class GetFilestreamTransactionContext : Expression
{
    public GetFilestreamTransactionContext(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("get_filestream_transaction_context", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.Null(SqlType.VarbinaryMax);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.VarbinaryMax;

    internal override string DebugDisplay() => "GET_FILESTREAM_TRANSACTION_CONTEXT()";
}
