using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@ERROR</c>: error number of the most recently completed
/// statement on the connection, <see cref="SqlType.Int32"/>. Reads
/// <see cref="SimulatedDbConnection.LastErrorNumber"/>, which the
/// per-statement dispatch wrapper sets to the caught error's number on
/// failure (inside a <c>TRY/CATCH</c> body) and resets to <c>0</c> on
/// successful statement completion. Outside any TRY/CATCH the value is
/// always <c>0</c> because uncaught errors tear down the batch — no path
/// returns to a subsequent statement that could read @@ERROR.
/// </summary>
internal sealed class LastErrorExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromInt32(runtime.Batch.Connection.LastErrorNumber);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "@@ERROR";
}
