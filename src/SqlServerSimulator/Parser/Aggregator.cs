using SqlServerSimulator.Parser.Aggregators;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-group state for a single aggregate function. The Selection executor
/// creates one fresh instance per <see cref="AggregateExpression"/> per
/// group via <see cref="Create"/>, streams input rows through
/// <see cref="Add"/>, and reads the final result via <see cref="Result"/>.
/// </summary>
/// <remarks>
/// Aggregators are single-threaded by contract; they live for the duration
/// of one query's execution and are never shared across queries or threads.
/// Each implementation defines its own NULL semantics — most skip NULL on
/// <see cref="Add"/>, while <c>COUNT(*)</c> counts NULL rows. Empty-input
/// behavior is also per-aggregate (COUNT returns 0; everything else NULL).
/// </remarks>
internal abstract class Aggregator
{
    public abstract void Add(SqlValue value);

    public abstract SqlValue Result();

    /// <summary>
    /// Whether this instance supports <see cref="Remove"/> — i.e. can undo an
    /// earlier <see cref="Add"/> so a sliding window frame can be maintained
    /// incrementally. Defaults to <c>false</c>; the window executor only calls
    /// <see cref="Remove"/> after checking this, and a removable instance is
    /// requested via <see cref="Create"/>'s <c>removable</c> flag (some
    /// aggregators need a heavier removal-capable representation).
    /// </summary>
    public virtual bool CanRemove => false;

    /// <summary>
    /// Reverses one prior <see cref="Add"/> of <paramref name="value"/>. Only
    /// valid when <see cref="CanRemove"/>; the value must have been added
    /// before. NULL inputs are no-ops on the matching <see cref="Add"/>, so
    /// removing a NULL is likewise a no-op.
    /// </summary>
    public virtual void Remove(SqlValue value) =>
        throw new NotSupportedException($"{this.GetType().Name} does not support incremental removal.");

    /// <summary>
    /// Builds a fresh aggregator for the given expression. Caller supplies
    /// the operand's resolved type (some aggregators use it to choose an
    /// accumulator) and the aggregate's overall result type (used to size
    /// the returned <see cref="SqlValue"/>). The Selection executor calls
    /// this once per group; aggregators don't outlive a single group.
    /// <para>
    /// <paramref name="removable"/> asks for a representation that supports
    /// <see cref="Remove"/> (set by the window executor for sliding frames
    /// whose start advances). It only changes the shape of aggregators that
    /// would otherwise be cheaper without removal support — notably MIN / MAX,
    /// which keeps a single running extreme unless asked to track a removable
    /// multiset. Aggregators that are intrinsically removable (or never) ignore
    /// it.
    /// </para>
    /// </summary>
    public static Aggregator Create(AggregateExpression aggregate, SqlType operandType, SqlType resultType, bool removable = false) => aggregate.Kind switch
    {
        AggregateKind.Count => new CountAggregator(isStar: aggregate.Operand is null, isBigCount: false, distinct: aggregate.Distinct),
        AggregateKind.CountBig => new CountAggregator(isStar: aggregate.Operand is null, isBigCount: true, distinct: aggregate.Distinct),
        AggregateKind.ApproxCountDistinct => new CountAggregator(isStar: false, isBigCount: true, distinct: true),
        AggregateKind.Max => operandType.IsLob || operandType is BitSqlType
            ? throw SimulatedSqlException.OperandDataTypeInvalid(operandType, "max")
            : new MinMaxAggregator(resultType, isMax: true, removable),
        AggregateKind.Min => operandType.IsLob || operandType is BitSqlType
            ? throw SimulatedSqlException.OperandDataTypeInvalid(operandType, "min")
            : new MinMaxAggregator(resultType, isMax: false, removable),
        AggregateKind.Sum => SumAggregator.Create(resultType, aggregate.Distinct),
        AggregateKind.Avg => AverageAggregator.Create(resultType, aggregate.Distinct),
        AggregateKind.Stdev or AggregateKind.StdevP or AggregateKind.Var or AggregateKind.VarP => new StatisticalAggregator(aggregate.Kind),
        AggregateKind.StringAgg => new StringAggAggregator(resultType, aggregate.OrderBy),
        AggregateKind.JsonArrayAgg => new JsonArrayAggAggregator(resultType, aggregate.JsonNulls, JsonValueRender.ProducesJson(aggregate.Operand!), aggregate.OrderBy),
        AggregateKind.JsonObjectAgg => new JsonObjectAggAggregator(resultType, aggregate.JsonNulls, JsonValueRender.ProducesJson(aggregate.Operand!)),
        AggregateKind.ChecksumAgg => new ChecksumAggAggregator(),
        _ => throw new NotSupportedException($"Aggregator for {aggregate.Kind} not implemented yet."),
    };
}
