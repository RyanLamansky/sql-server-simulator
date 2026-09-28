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
        this.statementMark = context.Batch.CurrentStatement.TakeTransactionMark();
    }

    private readonly StatementTransactionMark statementMark;

    /// <remarks>
    /// With no user transaction it reads 1 for the transaction real opens for
    /// the statement itself: inside the unit a trigger body runs in, inside a
    /// statement writing a table (table variables included), inside a function
    /// body (its caller named the function), and in a statement whose compile
    /// met anything <see cref="StatementContext.OpensTransaction"/> lists
    /// (probed 2026-09-28 against SQL Server 2025).
    /// </remarks>
    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromInt16(
        runtime.Batch.Connection.CurrentTransaction is { TranCount: > 0 } transaction ? (transaction.Doomed ? (short)-1 : (short)1)
            : this.statementMark.Opens
                || runtime.Batch.Connection.TriggerStatementUndoLog is not null
                || runtime.Batch.CurrentStatement.WritesRows
                || runtime.Batch.UdfFrame is not null ? (short)1
            : (short)0);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => "XACT_STATE()";

    internal override void Describe(NodeShape shape) { }
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

    internal override void Describe(NodeShape shape) { }
}
