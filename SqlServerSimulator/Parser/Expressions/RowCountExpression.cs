using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@ROWCOUNT</c>: returns the row count of the most recently
/// completed statement in the current batch as <see cref="SqlType.Int32"/>.
/// Probe-confirmed semantics (against SQL Server 2025, 2026-05-12):
/// <list type="bullet">
/// <item>SELECT result count (rows produced).</item>
/// <item>INSERT / UPDATE / DELETE / MERGE rows-affected.</item>
/// <item><c>SET @v = expr</c> and <c>DECLARE @v T = init</c> set it to 1.</item>
/// <item><c>DECLARE @v T</c> (no initializer) does NOT reset it.</item>
/// <item>SELECT-assign with FROM sets it to rows scanned (regardless of
/// whether assignments fired); empty FROM result sets it to 0.</item>
/// <item>Most other statements (PRINT, BEGIN, COMMIT, etc.) reset to 0.</item>
/// </list>
/// </summary>
internal sealed class RowCountExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.LastStatementRowCount);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "@@ROWCOUNT";
}
