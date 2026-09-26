using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@TRANCOUNT</c>: returns the connection's current transaction
/// nesting depth as <see cref="SqlType.Int32"/>. Zero when no transaction
/// is active; one after a single <c>BEGIN TRANSACTION</c> (or SqlClient
/// <c>BeginTransaction()</c>); higher when nested SQL-text <c>BEGIN</c>s
/// have been issued without matching <c>COMMIT</c>s. Probe-confirmed
/// behavior against SQL Server 2025 (2026-05-08). A DML trigger body fired by
/// an auto-commit statement reads 1 — the statement's own transaction — until
/// a <c>ROLLBACK</c> in it ends that, and a statement writing a table reads
/// one more than that, counting the transaction real opens for it, auto-commit
/// included (see <see cref="StatementContext.TransactedWrite"/>; probed
/// 2026-09-26).
/// </summary>
internal sealed class TranCountExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime)
    {
        var connection = runtime.Batch.Connection;
        if (runtime.Batch.CurrentStatement.TransactedWrite)
            return SqlValue.FromInt32((connection.CurrentTransaction?.TranCount ?? 1) + 1);
        return SqlValue.FromInt32(connection.CurrentTransaction?.TranCount ?? (connection.TriggerStatementUndoLog is not null ? 1 : 0));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@TRANCOUNT";

    internal override void Describe(NodeShape shape) { }
}
