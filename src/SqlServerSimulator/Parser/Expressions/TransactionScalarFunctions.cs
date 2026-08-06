using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>XACT_STATE()</c>: returns the transaction state of the current
/// request as a tristate <c>smallint</c>. <c>0</c> = no active
/// transaction; <c>1</c> = active, committable; <c>-1</c> = active but
/// uncommittable (doomed by an unrecoverable error under
/// <c>SET XACT_ABORT ON</c> — see
/// <see cref="SimulatedDbTransaction.Doomed"/>). Result type is
/// <see cref="SqlType.SmallInt"/> (real SQL Server's projection — verified
/// 2026-05-22).
/// </summary>
internal sealed class XactState : Expression
{
    public XactState(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("xact_state", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromInt16(
        runtime.Batch.Connection.CurrentTransaction is not { TranCount: > 0 } transaction ? (short)0
            : transaction.Doomed ? (short)-1
            : (short)1);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => "XACT_STATE()";
}

/// <summary>
/// SQL <c>ROWCOUNT_BIG()</c>: returns the rows-affected count of the
/// most recently completed statement as <see cref="SqlType.BigInt"/>
/// (bigint), the wide-int sibling of <c>@@ROWCOUNT</c>. Source semantics
/// match <see cref="RowCountExpression"/>; the only difference is the
/// projected type.
/// </summary>
internal sealed class RowCountBig : Expression
{
    public RowCountBig(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("rowcount_big", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt64(runtime.Batch.Connection.LastStatementRowCount);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.BigInt;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "ROWCOUNT_BIG()";
}
