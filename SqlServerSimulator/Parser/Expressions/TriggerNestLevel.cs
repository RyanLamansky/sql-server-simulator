using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>TRIGGER_NESTLEVEL()</c> — returns the current trigger nesting
/// depth. Real SQL Server also supports a one-arg form filtering by
/// trigger object id; the no-arg form returns the total depth across all
/// firing triggers. The simulator implements only the no-arg form: 1 at
/// the top-level DML's first trigger fire, 2+ when trigger bodies fire
/// further DML that itself triggers. Outside any trigger, returns 0
/// (probe-confirmed).
/// </summary>
internal sealed class TriggerNestLevelFunction : Expression
{
    public TriggerNestLevelFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "trigger_nestlevel");

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.TriggerNestLevel);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "TRIGGER_NESTLEVEL()";
}
