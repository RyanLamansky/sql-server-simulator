using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@ERROR</c>: error number of the most recently completed
/// statement, <see cref="SqlType.Int32"/>. Always returns 0 in the simulator
/// because TRY/CATCH isn't modeled — any <see cref="SimulatedSqlException"/>
/// propagates out of the dispatch loop and terminates the batch, so the only
/// statements that ever complete are successful ones. When TRY/CATCH lands,
/// this expression becomes the natural home for live error-number tracking
/// against state on <see cref="BatchContext"/>.
/// </summary>
internal sealed class LastErrorExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromInt32(0);

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "@@ERROR";
}
