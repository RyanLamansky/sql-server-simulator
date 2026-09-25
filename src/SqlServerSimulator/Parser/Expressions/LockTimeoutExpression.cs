using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@LOCK_TIMEOUT</c>: returns the session's lock-timeout in
/// milliseconds as <see cref="SqlType.Int32"/>. Default is <c>-1</c>
/// (wait indefinitely — probe-confirmed: a fresh connection reads
/// <c>@@LOCK_TIMEOUT = -1</c> before any explicit <c>SET LOCK_TIMEOUT</c>);
/// <c>0</c> = fail-fast; positive <c>N</c> = wait up to N ms. Mutated by
/// <c>SET LOCK_TIMEOUT &lt;N&gt;</c>.
/// </summary>
internal sealed class LockTimeoutExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.LockTimeoutMillis);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@LOCK_TIMEOUT";

    internal override void Describe(NodeShape shape) { }
}
