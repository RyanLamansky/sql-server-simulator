using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@TRANCOUNT</c>: returns the connection's current transaction
/// nesting depth as <see cref="SqlType.Int32"/>. Zero when no transaction
/// is active; one after a single <c>BEGIN TRANSACTION</c> (or SqlClient
/// <c>BeginTransaction()</c>); higher when nested SQL-text <c>BEGIN</c>s
/// have been issued without matching <c>COMMIT</c>s. Probe-confirmed
/// behavior against SQL Server 2025 (2026-05-08).
/// </summary>
internal sealed class TranCountExpression(ParserContext context) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(context.Connection.CurrentTransaction?.TranCount ?? 0);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@TRANCOUNT";
}
